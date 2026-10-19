using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services.FlyDj;

public interface IFlyDjConductor
{
    bool IsRunning { get; }
    Task StartAsync(string? performedBy, CancellationToken cancellationToken = default);
    Task StopAsync(string? performedBy, CancellationToken cancellationToken = default);
}

/// <summary>
/// The FlyDJ decision loop. While stopped it only waits for a signal (no polling, no simulation).
/// While running it auditions library stations on the sidecar, plays the winner on the automation
/// lane, watches engagement, and steps away from any room that a person or a schedule takes over.
/// </summary>
public sealed class FlyDjConductorService : BackgroundService, IFlyDjConductor
{
    public const string DefaultActor = "flydj";

    private const int AuditionBioMs = 500;
    private const int RecentDecisionLimit = 10;
    private const int LowEngagementChecksToSwitch = 2;

    internal static readonly TimeSpan SnapshotGrace = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ModelPollInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ScheduleRecheckInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time;
    private readonly IServiceScopeFactory _scopes;
    private readonly PlaybackCommandCoordinator _commands;
    private readonly PlaybackSnapshotStore _snapshots;
    private readonly FlyDjStateStore _state;
    private readonly IFlyDjClient _client;
    private readonly ILogger<FlyDjConductorService> _logger;
    private readonly IScheduleRoomOwnership? _scheduleOwnership;
    private readonly ManualAutoStopService? _autoStop;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _startSignal = new(0);
    private readonly object _stateLock = new();
    private readonly object _wakeLock = new();
    private TaskCompletionSource _wake = NewSignal();

    private readonly Dictionary<string, YieldInfo> _yielded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset ObservedUtc, string Reason)> _pendingOverrides
        = new(StringComparer.OrdinalIgnoreCase);
    private List<FlyDjDecisionView> _recent = [];
    private FlyDjConductorStatus _status = FlyDjConductorStatus.Stopped;
    private string? _statusMessage;
    private DateTimeOffset? _nextDecisionAt;
    private IReadOnlyDictionary<string, string> _speakerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private volatile ActiveSelection? _active;
    private volatile bool _running;
    private volatile bool _hostStarted;
    private volatile bool _sidecarActive;
    private volatile bool _modelReady;
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _sessionCancellation;
    private TaskCompletionSource? _sessionDone;

    private sealed record YieldInfo(string SpeakerName, DateTimeOffset UntilUtc, DateTimeOffset YieldedAtUtc, string Reason);

    private sealed record ActiveSelection(
        long DecisionId,
        string Name,
        string Url,
        string[] Rooms,
        DateTimeOffset DecidedAtUtc,
        DateTimeOffset LastCommandUtc,
        DateTimeOffset MinDwellUntilUtc,
        DateTimeOffset MaxDwellUntilUtc,
        DateTimeOffset NextCheckUtc,
        int LowEngagementStreak,
        bool Overridden);

    public FlyDjConductorService(
        TimeProvider time,
        IServiceScopeFactory scopes,
        PlaybackCommandCoordinator commands,
        PlaybackSnapshotStore snapshots,
        FlyDjStateStore state,
        IFlyDjClient client,
        ILogger<FlyDjConductorService> logger,
        IScheduleRoomOwnership? scheduleOwnership = null,
        ManualAutoStopService? autoStop = null)
    {
        _time = time;
        _scopes = scopes;
        _commands = commands;
        _snapshots = snapshots;
        _state = state;
        _client = client;
        _logger = logger;
        _scheduleOwnership = scheduleOwnership;
        _autoStop = autoStop;
    }

    public bool IsRunning => _running;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------------------------------------------------------------- lifecycle

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Set before ExecuteAsync is scheduled, so a Start request can never race the hosted-service start-up.
        _hostStarted = true;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _shutdown.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _snapshots.Changed += OnSnapshotsChanged;
        try
        {
            await LoadRecentDecisionsAsync(stoppingToken);
            await TryAutoResumeAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                // Stopped: no timers, no network. Only a Start signal wakes the loop.
                await _startSignal.WaitAsync(stoppingToken);
                CancellationTokenSource? cancellation;
                TaskCompletionSource? done;
                lock (_stateLock)
                {
                    cancellation = _sessionCancellation;
                    done = _sessionDone;
                }
                try
                {
                    if (_running && cancellation is not null) await RunSessionAsync(cancellation.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FlyDJ session ended unexpectedly.");
                    SetStatus(FlyDjConductorStatus.Error, $"FlyDJ stopped unexpectedly: {ex.Message}");
                }
                finally { done?.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _snapshots.Changed -= OnSnapshotsChanged;
        }
    }

    public async Task StartAsync(string? performedBy, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_running) return;
            if (!_hostStarted)
            {
                SetStatus(FlyDjConductorStatus.Error, "FlyDJ cannot run because background services are disabled on this server.");
                return;
            }
            lock (_stateLock)
            {
                _yielded.Clear();
                _active = null;
                _pendingOverrides.Clear();
                _sessionCancellation?.Dispose();
                _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _sessionDone = NewSignal();
                _nextDecisionAt = null;
            }
            _modelReady = false;
            _running = true;
            SetStatus(FlyDjConductorStatus.Starting, "Starting the fly brain...");
            await PersistWasRunningAsync(true, cancellationToken);
            await LogAsync("FlyDjStarted", "FlyDJ started.", performedBy ?? DefaultActor);
            _startSignal.Release();
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(string? performedBy, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            var wasRunning = _running;
            _running = false;
            CancellationTokenSource? cancellation;
            TaskCompletionSource? done;
            lock (_stateLock)
            {
                cancellation = _sessionCancellation;
                done = _sessionDone;
            }
            if (cancellation is not null) await cancellation.CancelAsync();
            if (wasRunning && done is not null) await done.Task.WaitAsync(cancellationToken);

            var active = _active;
            lock (_stateLock)
            {
                _active = null;
                _yielded.Clear();
                _nextDecisionAt = null;
            }
            _pendingOverrides.Clear();
            if (active is not null) await EndDecisionAsync(active, _time.GetUtcNow());

            if (wasRunning || _sidecarActive)
            {
                try { await _client.StopAsync(cancellationToken); }
                catch (FlyDjException ex) { _logger.LogWarning(ex, "FlyDJ sidecar could not be stopped."); }
            }
            _sidecarActive = false;
            _modelReady = false;
            await PersistWasRunningAsync(false, cancellationToken);
            if (wasRunning) await LogAsync("FlyDjStopped", "FlyDJ stopped.", performedBy ?? DefaultActor);
            SetStatus(FlyDjConductorStatus.Stopped, null);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task TryAutoResumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            if (settings.FlyDj.AutoResume && settings.FlyDj.WasRunning)
                await StartAsync(DefaultActor, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlyDJ could not resume after start-up.");
        }
    }

    // ---------------------------------------------------------------- session loop

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        var backoff = MinBackoff;
        var notRunningStreak = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay;
            var stepStarted = _time.GetUtcNow();
            try
            {
                delay = await StepAsync(cancellationToken);
                backoff = MinBackoff;
                notRunningStreak = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (FlyDjNotRunningException) when (++notRunningStreak <= 3)
            {
                // The sidecar lost its running state (for example after a container restart).
                _sidecarActive = false;
                _modelReady = false;
                delay = TimeSpan.FromSeconds(1);
            }
            catch (Exception ex)
            {
                var unreachable = ex is FlyDjUnavailableException;
                _sidecarActive = false;
                _modelReady = false;
                _logger.LogWarning(ex, "FlyDJ step failed; retrying in {Delay}.", backoff);
                var message = ex is FlyDjException ? ex.Message : $"FlyDJ hit a problem: {ex.Message}";
                SetStatus(FlyDjConductorStatus.Error, $"{message} Retrying in {FormatDelay(backoff)}.", unreachable ? false : null);
                delay = backoff;
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }

            // Delays are measured from the start of the step, so time spent working is not added on top.
            var remaining = delay - (_time.GetUtcNow() - stepStarted);
            try { await WaitAsync(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var settings = await LoadSettingsAsync(cancellationToken);
        var fly = settings.FlyDj;
        _speakerNames = settings.Speakers.Where(speaker => !string.IsNullOrWhiteSpace(speaker.IpAddress))
            .GroupBy(speaker => speaker.IpAddress.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);

        await ProcessOverridesAsync(now, fly);
        ExpireYields(now, fly);

        var configured = fly.TargetSpeakerIps
            .Select(ip => _speakerNames.Keys.FirstOrDefault(known => string.Equals(known, ip, StringComparison.OrdinalIgnoreCase)))
            .Where(ip => ip is not null).Select(ip => ip!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (configured.Count == 0)
        {
            await DeactivateSidecarAsync(now, cancellationToken);
            SetStatus(FlyDjConductorStatus.Error, "Select at least one configured room for FlyDJ.");
            return ScheduleRecheckInterval;
        }

        var owned = (_scheduleOwnership?.GetScheduleOwnedSpeakers(settings, now) ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await ReleaseUnavailableActiveRoomsAsync(now, configured, owned);

        List<string> available;
        lock (_stateLock)
            available = configured.Where(ip => !_yielded.ContainsKey(ip) && !owned.Contains(ip)).ToList();
        if (available.Count == 0) return await RestAsync(now, configured, owned, cancellationToken);

        if (!_sidecarActive)
        {
            await _client.StartAsync(cancellationToken);
            _sidecarActive = true;
            _modelReady = false;
        }
        if (!_modelReady)
        {
            var model = await _client.GetModelAsync(cancellationToken);
            _state.Update(state => state with { Model = model, SidecarReachable = true });
            if (string.Equals(model.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(FlyDjConductorStatus.Error, model.Message ?? "The fly brain model failed to load.", true);
                return ModelPollInterval;
            }
            if (!string.Equals(model.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                var percent = Math.Clamp(model.BuildProgress, 0, 1) * 100;
                SetStatus(FlyDjConductorStatus.Starting,
                    model.Message ?? string.Format(CultureInfo.InvariantCulture, "The fly brain is {0} ({1:0}%).", model.Status, percent), true);
                return ModelPollInterval;
            }
            _modelReady = true;
        }
        _state.Update(state => state with { SidecarReachable = true });

        var active = _active;
        var needsDecision = active is null || available.Any(ip => !active.Rooms.Contains(ip, StringComparer.OrdinalIgnoreCase));
        if (!needsDecision && active is not null)
        {
            if (now >= active.MaxDwellUntilUtc) needsDecision = true;
            else if (now >= active.NextCheckUtc)
            {
                var listen = await _client.ListenAsync(active.Url, cancellationToken);
                var streak = listen.Ok && listen.Engagement is < 0 ? active.LowEngagementStreak + 1 : 0;
                needsDecision = now >= active.MinDwellUntilUtc && streak >= LowEngagementChecksToSwitch;
                var updated = active with
                {
                    LowEngagementStreak = streak,
                    NextCheckUtc = now + TimeSpan.FromSeconds(fly.EngagementCheckSeconds)
                };
                lock (_stateLock)
                    if (ReferenceEquals(_active, active)) _active = updated;
                active = updated;
            }
        }

        if (needsDecision)
        {
            var delay = await DecideAndPlayAsync(settings, available, now, cancellationToken);
            if (delay is not null) return delay.Value;
            active = _active;
        }

        return ComputeDwellDelay(active);
    }

    private TimeSpan ComputeDwellDelay(ActiveSelection? active)
    {
        var now = _time.GetUtcNow();
        if (active is null)
        {
            SetNextDecision(null);
            return TimeSpan.FromSeconds(1);
        }
        SetNextDecision(active.MaxDwellUntilUtc);
        var next = active.NextCheckUtc < active.MaxDwellUntilUtc ? active.NextCheckUtc : active.MaxDwellUntilUtc;
        return Max(next - now, TimeSpan.FromSeconds(1));
    }

    /// <summary>All target rooms are resting or owned by schedules: park the simulation.</summary>
    private async Task<TimeSpan> RestAsync(DateTimeOffset now, List<string> configured, HashSet<string> owned, CancellationToken cancellationToken)
    {
        await DeactivateSidecarAsync(now, cancellationToken);
        DateTimeOffset? earliest;
        bool scheduleBlocked;
        lock (_stateLock)
        {
            earliest = _yielded.Count == 0 ? null : _yielded.Values.Min(item => item.UntilUtc);
            scheduleBlocked = configured.Any(ip => owned.Contains(ip));
        }
        string message;
        if (earliest is not null)
            message = $"Every FlyDJ room is resting after a takeover. FlyDJ resumes at {FormatClock(earliest.Value)} unless someone is still playing.";
        else
            message = "Every FlyDJ room is currently used by a schedule. FlyDJ waits for it to end.";
        SetStatus(FlyDjConductorStatus.Yielded, message);
        SetNextDecision(earliest);
        var delay = earliest is null ? ScheduleRecheckInterval : Max(earliest.Value - now, TimeSpan.FromSeconds(1));
        if (scheduleBlocked && delay > ScheduleRecheckInterval) delay = ScheduleRecheckInterval;
        return delay;
    }

    private async Task DeactivateSidecarAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = _active;
        if (active is not null)
        {
            lock (_stateLock) _active = null;
            await EndDecisionAsync(active, now);
        }
        if (_sidecarActive)
        {
            try { await _client.StopAsync(cancellationToken); }
            catch (FlyDjException ex) { _logger.LogWarning(ex, "FlyDJ sidecar could not be parked."); }
            _sidecarActive = false;
            _modelReady = false;
        }
        PublishStation();
    }

    // ---------------------------------------------------------------- decisions

    private async Task<TimeSpan?> DecideAndPlayAsync(
        SonosSettings settings, List<string> available, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fly = settings.FlyDj;
        SetStatus(FlyDjConductorStatus.Auditioning, "Letting the fly brain listen to candidate stations...");

        IReadOnlyList<FlyDjCandidate> candidates;
        await using (var scope = _scopes.CreateAsyncScope())
            candidates = await scope.ServiceProvider.GetRequiredService<FlyDjCandidateProvider>()
                .GetCandidatesAsync(settings, now, cancellationToken);
        if (candidates.Count == 0)
        {
            SetStatus(FlyDjConductorStatus.Error, "FlyDJ needs at least one radio station in the library.");
            return ScheduleRecheckInterval;
        }

        var audition = await _client.AuditionAsync(new FlyDjAuditionRequest(
            candidates.Select(candidate => new FlyDjAuditionCandidate(candidate.Id, candidate.Url)).ToList(), AuditionBioMs), cancellationToken);
        var decidedAt = _time.GetUtcNow();
        var choice = FlyDjScoring.Choose(candidates, audition, decidedAt);

        // Manual priority: re-check just before touching any room.
        var rooms = available.ToList();
        var previous = _active;
        var manual = _commands.ManualSpeakersSince(now)
            .Where(ip => rooms.Contains(ip, StringComparer.OrdinalIgnoreCase)).ToList();
        if (manual.Count > 0)
        {
            await YieldRoomsAsync(manual, "Manual playback took over", decidedAt, fly);
            rooms = rooms.Except(manual, StringComparer.OrdinalIgnoreCase).ToList();
            if (rooms.Count == 0) return TimeSpan.FromSeconds(1);
        }

        var chosen = choice.Chosen;
        var sameStation = previous is not null && MediaIdentity.Matches(previous.Url, chosen.Url)
            && rooms.All(ip => previous.Rooms.Contains(ip, StringComparer.OrdinalIgnoreCase));

        IReadOnlyCollection<string> played = rooms;
        IReadOnlyCollection<string> touchedRooms = rooms;
        var commandAt = decidedAt;
        if (sameStation)
        {
            touchedRooms = [];
        }
        else
        {
            SetStatus(FlyDjConductorStatus.Auditioning, $"Starting {chosen.Name}...");
            var result = await PlayAsync(settings, rooms, chosen, now, cancellationToken);
            if (result is null)
            {
                // Rooms were taken away mid-command. Forget the old selection and re-plan.
                var stale = _active;
                if (stale is not null)
                {
                    lock (_stateLock) _active = null;
                    await EndDecisionAsync(stale, _time.GetUtcNow());
                }
                PublishStation();
                return TimeSpan.FromSeconds(1);
            }
            touchedRooms = result;
            commandAt = _time.GetUtcNow();
        }

        if (previous is not null) await EndDecisionAsync(previous, decidedAt);

        var decisionId = await PersistDecisionAsync(fly, choice, played, decidedAt, cancellationToken);
        var selection = new ActiveSelection(
            decisionId, chosen.Name, chosen.Url, played.ToArray(), decidedAt,
            sameStation ? previous!.LastCommandUtc : commandAt,
            decidedAt + TimeSpan.FromMinutes(fly.MinDwellMinutes),
            commandAt + TimeSpan.FromMinutes(fly.MaxDwellMinutes),
            commandAt + TimeSpan.FromSeconds(fly.EngagementCheckSeconds),
            0, false);
        lock (_stateLock) _active = selection;
        foreach (var ip in played) _pendingOverrides.TryRemove(ip, out _);
        PublishDecision(choice, decisionId, decidedAt, played);
        SetStatus(FlyDjConductorStatus.Playing, $"Playing {chosen.Name}.");

        await LogAsync("FlyDjStationChosen", $"{chosen.Name}: {choice.Reason}", DefaultActor);
        if (!sameStation)
        {
            if (_autoStop is not null)
            {
                try { await _autoStop.RegisterSuccessfulStartAsync(touchedRooms, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.LogWarning(ex, "FlyDJ could not register stop rules for its rooms."); }
            }
            try { await _client.ListenAsync(chosen.Url, cancellationToken); }
            catch (FlyDjException ex) when (ex is not FlyDjNotRunningException)
            { _logger.LogDebug(ex, "FlyDJ could not set the continuous stimulus."); }
        }
        return null;
    }

    /// <summary>Plays <paramref name="chosen"/> on the automation lane. Returns the rooms written, or null when ownership was lost.</summary>
    private async Task<IReadOnlyCollection<string>?> PlayAsync(
        SonosSettings settings, List<string> rooms, FlyDjCandidate chosen, DateTimeOffset occurrenceStart, CancellationToken cancellationToken)
    {
        var fly = settings.FlyDj;
        var lease = await _commands.EnterAutomationAsync(rooms, occurrenceStart, cancellationToken);
        if (lease is null)
        {
            // A person started playback in at least one room after this decision began.
            var taken = _commands.ManualSpeakersSince(occurrenceStart)
                .Where(ip => rooms.Contains(ip, StringComparer.OrdinalIgnoreCase)).ToList();
            await YieldRoomsAsync(taken.Count > 0 ? taken : rooms, "Manual playback took over", _time.GetUtcNow(), fly);
            return null;
        }

        var volume = Math.Clamp(fly.Volume, 0, settings.MaxVolume);
        var scene = new Scene
        {
            Id = "flydj",
            Name = "FlyDJ",
            Description = "Station chosen by the FlyDJ connectome.",
            Enabled = true,
            SourceType = SceneSourceType.Station,
            SourceSelectionMode = SceneSourceSelectionMode.Specific,
            SourceUrl = chosen.Url,
            IsSyncedPlayback = rooms.Count > 1,
            SpeakerIps = rooms.ToList(),
            Actions = rooms.Select(ip => new SceneAction { SpeakerIp = ip, Volume = volume, IncludeInPlayback = true }).ToList()
        };
        await using var context = new SceneExecutionContext(_commands, PlaybackCommandOrigin.Automation, occurrenceStart, cancellationToken, lease);
        try
        {
            using var scope = _scopes.CreateScope();
            var orchestration = scope.ServiceProvider.GetRequiredService<ISceneOrchestrationService>();
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = await orchestration.ApplySceneWithContextAsync(
                scene, DefaultActor, context, ips => { lock (touched) touched.UnionWith(ips); },
                context.CancellationToken, rooms, allowRecovery: false);
            if (!result.Success)
                throw new InvalidOperationException($"Could not start {chosen.Name}: {result.Message}");
            return result.TargetSpeakers.Count > 0 ? result.TargetSpeakers : rooms;
        }
        catch (OperationCanceledException) when (context.WasPreempted && !cancellationToken.IsCancellationRequested)
        {
            var taken = _commands.ManualSpeakersSince(occurrenceStart)
                .Where(ip => rooms.Contains(ip, StringComparer.OrdinalIgnoreCase)).ToList();
            await YieldRoomsAsync(taken.Count > 0 ? taken : rooms, "Manual playback or a sleep timer took over", _time.GetUtcNow(), fly);
            return null;
        }
    }

    // ---------------------------------------------------------------- yielding

    private async Task ProcessOverridesAsync(DateTimeOffset now, FlyDjSettings fly)
    {
        var active = _active;
        if (active is null)
        {
            _pendingOverrides.Clear();
            return;
        }
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in _commands.ManualSpeakersSince(active.LastCommandUtc))
            if (active.Rooms.Contains(ip, StringComparer.OrdinalIgnoreCase)) reasons[ip] = "Manual playback took over";
        foreach (var pending in _pendingOverrides.ToArray())
        {
            if (!active.Rooms.Contains(pending.Key, StringComparer.OrdinalIgnoreCase)
                || pending.Value.ObservedUtc < active.LastCommandUtc + SnapshotGrace)
            {
                _pendingOverrides.TryRemove(pending.Key, out _);
                continue;
            }
            reasons.TryAdd(pending.Key, pending.Value.Reason);
        }
        foreach (var group in reasons.GroupBy(pair => pair.Value, pair => pair.Key))
            await YieldRoomsAsync(group.ToList(), group.Key, now, fly);
    }

    private void ExpireYields(DateTimeOffset now, FlyDjSettings fly)
    {
        List<KeyValuePair<string, YieldInfo>> due;
        lock (_stateLock) due = _yielded.Where(pair => pair.Value.UntilUtc <= now).ToList();
        if (due.Count == 0) return;
        foreach (var pair in due)
        {
            // Someone played in the room while it rested, or their music is still playing (FlyDJ has
            // stopped commanding this room, so any playback is theirs): the rest starts over.
            var snapshot = _snapshots.Get(pair.Key);
            var retaken = _commands.ManualSpeakersSince(pair.Value.YieldedAtUtc.AddTicks(1))
                .Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
                || snapshot is { IsStale: false, IsPlaying: true };
            lock (_stateLock)
            {
                if (retaken)
                    _yielded[pair.Key] = pair.Value with
                    {
                        UntilUtc = now + TimeSpan.FromMinutes(fly.YieldCooldownMinutes),
                        YieldedAtUtc = now
                    };
                else _yielded.Remove(pair.Key);
            }
        }
        PublishStation();
    }

    private async Task ReleaseUnavailableActiveRoomsAsync(DateTimeOffset now, List<string> configured, HashSet<string> owned)
    {
        var active = _active;
        if (active is null) return;
        var remaining = active.Rooms.Where(ip => configured.Contains(ip, StringComparer.OrdinalIgnoreCase) && !owned.Contains(ip)).ToArray();
        if (remaining.Length == active.Rooms.Length) return;
        if (remaining.Length == 0)
        {
            lock (_stateLock) _active = null;
            await EndDecisionAsync(active, now);
        }
        else
        {
            lock (_stateLock)
                if (ReferenceEquals(_active, active)) _active = active with { Rooms = remaining };
        }
        PublishStation();
    }

    private async Task YieldRoomsAsync(IReadOnlyCollection<string> rooms, string reason, DateTimeOffset now, FlyDjSettings fly)
    {
        if (rooms.Count == 0) return;
        var cooldown = TimeSpan.FromMinutes(fly.YieldCooldownMinutes);
        ActiveSelection? ended = null;
        ActiveSelection? flagged = null;
        lock (_stateLock)
        {
            foreach (var ip in rooms)
                _yielded[ip] = new YieldInfo(_speakerNames.GetValueOrDefault(ip, ip), now + cooldown, now, reason);
            var active = _active;
            if (active is not null)
            {
                var remaining = active.Rooms.Where(ip => !rooms.Contains(ip, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (remaining.Length != active.Rooms.Length)
                {
                    var overridden = active.Overridden || now - active.DecidedAtUtc < TimeSpan.FromMinutes(fly.MinDwellMinutes);
                    var updated = active with { Rooms = remaining, Overridden = overridden };
                    if (overridden && !active.Overridden) flagged = updated;
                    if (remaining.Length == 0)
                    {
                        _active = null;
                        ended = updated;
                    }
                    else _active = updated;
                }
            }
        }
        foreach (var ip in rooms) _pendingOverrides.TryRemove(ip, out _);
        if (ended is not null) await EndDecisionAsync(ended, now);
        else if (flagged is not null) await UpdateDecisionAsync(flagged.DecisionId, decision => decision.OverriddenByUser = true);
        var names = string.Join(", ", rooms.Select(ip => _speakerNames.GetValueOrDefault(ip, ip)));
        await LogAsync("FlyDjYielded", $"{reason} in {names}; FlyDJ steps away for {fly.YieldCooldownMinutes} min.", DefaultActor);
        PublishStation();
    }

    private void OnSnapshotsChanged()
    {
        var active = _active;
        if (active is null || !_running) return;
        var wake = false;
        try
        {
            foreach (var ip in active.Rooms)
            {
                var snapshot = _snapshots.Get(ip);
                if (snapshot is null || snapshot.IsStale || snapshot.LastSuccessfulRefreshUtc is not { } observed) continue;
                if (observed < active.LastCommandUtc + SnapshotGrace) continue;
                string? reason = null;
                if (!snapshot.IsPlaying) reason = "Playback was stopped from outside FlyDJ";
                else if (!SnapshotMatchesStation(snapshot.CurrentStationUrl, active.Url))
                    reason = "Something other than the FlyDJ station started playing";
                if (reason is null) continue;
                _pendingOverrides[ip] = (observed, reason);
                wake = true;
            }
            if (!wake && _commands.ManualSpeakersSince(active.LastCommandUtc).Any(ip => active.Rooms.Contains(ip, StringComparer.OrdinalIgnoreCase)))
                wake = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FlyDJ could not evaluate a playback snapshot.");
        }
        if (wake) Signal();
    }

    internal static bool SnapshotMatchesStation(string? currentUri, string stationUrl)
    {
        if (string.IsNullOrWhiteSpace(currentUri)) return false;
        // Followers of a synchronised group point at their coordinator, not at the stream.
        if (currentUri.StartsWith("x-rincon:", StringComparison.OrdinalIgnoreCase)) return true;
        if (MediaIdentity.Matches(currentUri, stationUrl)) return true;
        var stripped = currentUri.Replace("x-rincon-mp3radio://", "", StringComparison.OrdinalIgnoreCase).Trim();
        var station = stationUrl.Trim();
        var schemeEnd = station.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0) station = station[(schemeEnd + 3)..];
        return station.Length > 0 && stripped.Contains(station, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- signalling

    private void Signal()
    {
        lock (_wakeLock) _wake.TrySetResult();
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task signal;
        lock (_wakeLock) signal = _wake.Task;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Task.Delay(delay, _time, cancel.Token);
        try
        {
            await Task.WhenAny(timer, signal);
        }
        finally
        {
            await cancel.CancelAsync();
        }
        lock (_wakeLock)
            if (_wake.Task.IsCompleted) _wake = NewSignal();
        cancellationToken.ThrowIfCancellationRequested();
    }

    // ---------------------------------------------------------------- persistence

    private async Task<SonosSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepo>();
        var settings = await repo.GetSettings().WaitAsync(cancellationToken) ?? new SonosSettings();
        settings.Speakers ??= new();
        settings.Stations ??= new();
        settings.FlyDj ??= new FlyDjSettings();
        settings.FlyDj.Normalize();
        return settings;
    }

    private async Task PersistWasRunningAsync(bool value, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepo>();
            await repo.UpdateSettingsAsync(settings =>
            {
                settings.FlyDj ??= new FlyDjSettings();
                settings.FlyDj.WasRunning = value;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FlyDJ could not persist its running flag.");
        }
    }

    private async Task LogAsync(string action, string details, string? performedBy)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ActionLogger>().LogAsync(action, details, performedBy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "FlyDJ could not write an action log entry.");
        }
    }

    private async Task<long> PersistDecisionAsync(
        FlyDjSettings fly, FlyDjScoredChoice choice, IReadOnlyCollection<string> rooms, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entity = new FlyDjDecision
        {
            DecidedAtUtc = decidedAt.UtcDateTime,
            StationName = Truncate(choice.Chosen.Name, 256),
            StationUrl = Truncate(choice.Chosen.Url, 2048),
            SpeakerIpsJson = JsonSerializer.Serialize(rooms, Json),
            CandidatesJson = JsonSerializer.Serialize(choice.Scores, Json),
            Reason = Truncate(choice.Reason, 1024),
            BrainWeight = choice.BrainWeight,
            PriorWeight = choice.PriorWeight
        };
        db.FlyDjDecisions.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    private async Task UpdateDecisionAsync(long id, Action<FlyDjDecision> update)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await db.FlyDjDecisions.FirstOrDefaultAsync(decision => decision.Id == id);
            if (entity is null) return;
            update(entity);
            await db.SaveChangesAsync();
            var view = ToView(entity);
            lock (_stateLock)
            {
                var index = _recent.FindIndex(item => item.Id == id);
                if (index >= 0)
                {
                    _recent = _recent.ToList();
                    _recent[index] = view;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FlyDJ could not update decision {DecisionId}.", id);
        }
    }

    private Task EndDecisionAsync(ActiveSelection selection, DateTimeOffset endedAt)
        => UpdateDecisionAsync(selection.DecisionId, decision =>
        {
            decision.EndedAtUtc = endedAt.UtcDateTime;
            decision.DwellSeconds = Math.Max(0, (endedAt - selection.LastCommandUtc).TotalSeconds);
            decision.OverriddenByUser |= selection.Overridden;
        });

    private async Task LoadRecentDecisionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.FlyDjDecisions.AsNoTracking()
                .OrderByDescending(decision => decision.DecidedAtUtc).ThenByDescending(decision => decision.Id)
                .Take(RecentDecisionLimit).ToListAsync(cancellationToken);
            lock (_stateLock) _recent = rows.Select(ToView).ToList();
            PublishStation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FlyDJ could not load its decision history.");
        }
    }

    internal static FlyDjDecisionView ToView(FlyDjDecision entity)
    {
        IReadOnlyList<string> ips = [];
        IReadOnlyList<FlyDjCandidateScore> candidates = [];
        try { ips = JsonSerializer.Deserialize<List<string>>(entity.SpeakerIpsJson, Json) ?? []; } catch (JsonException) { }
        try { candidates = JsonSerializer.Deserialize<List<FlyDjCandidateScore>>(entity.CandidatesJson, Json) ?? []; } catch (JsonException) { }
        return new FlyDjDecisionView(
            entity.Id, new DateTimeOffset(DateTime.SpecifyKind(entity.DecidedAtUtc, DateTimeKind.Utc)),
            entity.StationName, entity.StationUrl, ips, entity.Reason, candidates, entity.DwellSeconds, entity.OverriddenByUser);
    }

    // ---------------------------------------------------------------- publishing

    private void PublishDecision(FlyDjScoredChoice choice, long id, DateTimeOffset decidedAt, IReadOnlyCollection<string> rooms)
    {
        var view = new FlyDjDecisionView(id, decidedAt, choice.Chosen.Name, choice.Chosen.Url, rooms.ToArray(),
            choice.Reason, choice.Scores, null, false);
        lock (_stateLock)
            _recent = _recent.Prepend(view).Take(RecentDecisionLimit).ToList();
        PublishStation();
    }

    private void SetNextDecision(DateTimeOffset? at)
    {
        lock (_stateLock)
        {
            if (_nextDecisionAt == at) return;
            _nextDecisionAt = at;
        }
        PublishStation();
    }

    private void SetStatus(FlyDjConductorStatus status, string? message, bool? sidecarReachable = null)
    {
        lock (_stateLock)
        {
            _status = status;
            _statusMessage = message;
        }
        if (sidecarReachable is { } reachable) _state.Update(state => state with { SidecarReachable = reachable });
        PublishStation();
    }

    private void PublishStation()
    {
        FlyDjConductorStatus status;
        string? message;
        ActiveSelection? active;
        List<FlyDjYieldedSpeaker> yielded;
        List<FlyDjDecisionView> recent;
        DateTimeOffset? next;
        lock (_stateLock)
        {
            status = _status;
            message = _statusMessage;
            active = _active;
            next = _nextDecisionAt;
            recent = _recent;
            yielded = _yielded.OrderBy(pair => pair.Value.UntilUtc).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new FlyDjYieldedSpeaker(pair.Key, pair.Value.SpeakerName, pair.Value.UntilUtc, pair.Value.Reason)).ToList();
        }
        _state.Update(state => state with
        {
            Status = status,
            StatusMessage = message,
            CurrentStationName = active?.Name,
            CurrentStationUrl = active?.Url,
            ActiveSpeakerIps = active?.Rooms ?? [],
            YieldedSpeakers = yielded,
            LastDecision = recent.FirstOrDefault(),
            RecentDecisions = recent,
            NextDecisionAtUtc = status is FlyDjConductorStatus.Stopped ? null : next
        });
    }

    // ---------------------------------------------------------------- helpers

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
    private static string FormatDelay(TimeSpan delay) => delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0.#} min" : $"{delay.TotalSeconds:0} s";
    // Match the page, which shows times in the configured automation time zone, not the server's.
    private string FormatClock(DateTimeOffset at)
    {
        using var scope = _scopes.CreateScope();
        var zone = scope.ServiceProvider.GetService<ConfiguredTimeZoneService>();
        return zone is null ? at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : zone.Format(at, "HH:mm");
    }
}
