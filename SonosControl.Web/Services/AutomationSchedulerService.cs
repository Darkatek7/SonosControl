using Microsoft.Extensions.DependencyInjection;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services.FlyDj;

namespace SonosControl.Web.Services;

public interface IAutomationScheduler
{
    AutomationRuntimeSnapshot Status { get; }
    Task EvaluateNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read-only view of which rooms an active schedule window currently owns.</summary>
public interface IScheduleRoomOwnership
{
    IReadOnlyCollection<string> GetScheduleOwnedSpeakers(SonosSettings settings, DateTimeOffset utcNow);
    IReadOnlyList<FlyDjSchedulePlayback> GetFlyDjSchedules(SonosSettings settings, DateTimeOffset utcNow) => [];
}

public sealed class AutomationSchedulerService : BackgroundService, IAutomationScheduler, IScheduleRoomOwnership
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISettingsSchemaMigrationService _migrationService;
    private readonly AutomationRuntimeStatus _runtimeStatus;
    private readonly ILogger<AutomationSchedulerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _timeZone;
    private readonly PlaybackCommandCoordinator _commands;
    private readonly PlaybackSnapshotMonitorService? _playbackSnapshots;
    private readonly ManualAutoStopService? _autoStop;
    private readonly Dictionary<string, ActiveOccurrence> _ownedWindowOccurrences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (AttemptKey Attempt, int Count, DateTimeOffset RetryAt, string Message)> _windowFailures = new(StringComparer.OrdinalIgnoreCase);
    private int _evaluating;
    private static readonly int[] RetrySeconds = [30, 60, 120, 300];
    private sealed record AttemptKey(string WindowId, DateTime WindowRevision, string SceneId, DateTime SceneRevision, DateOnly StartDate);
    private sealed record ActiveOccurrence(AttemptKey Attempt, DateTimeOffset StartUtc, string Name, int FadeOutSeconds, bool IsSynced, IReadOnlyCollection<string> Targets, IReadOnlyCollection<string>? DesiredTargets = null, DateTimeOffset? StopAtUtc = null, IReadOnlyDictionary<string, long>? ScheduleGenerations = null);

    public AutomationSchedulerService(
        IServiceScopeFactory scopeFactory,
        ISettingsSchemaMigrationService migrationService,
        AutomationRuntimeStatus runtimeStatus,
        ILogger<AutomationSchedulerService> logger,
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null,
        PlaybackCommandCoordinator? commands = null,
        PlaybackSnapshotMonitorService? playbackSnapshots = null,
        ManualAutoStopService? autoStop = null)
    {
        _scopeFactory = scopeFactory;
        _migrationService = migrationService;
        _runtimeStatus = runtimeStatus;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _commands = commands ?? new PlaybackCommandCoordinator(_timeProvider);
        _playbackSnapshots = playbackSnapshots;
        _autoStop = autoStop;
        _timeZone = timeZone ?? ResolveTimeZone(configuration["Automation:TimeZone"]);
    }

    public AutomationRuntimeSnapshot Status => _runtimeStatus.Snapshot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Canonical automation scheduler started in timezone {TimeZone}.", _timeZone.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateNowAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _runtimeStatus.RecordEvaluation(null, _timeProvider.GetUtcNow(), ex.Message);
                _logger.LogWarning(ex, "Canonical schedule evaluation failed.");
            }

            await Task.Delay(PollInterval, _timeProvider, stoppingToken);
        }
    }

    public async Task EvaluateNowAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _evaluating, 1) != 0) return;
        try
        {
            var migration = await _migrationService.MigrateIfRequiredAsync(cancellationToken);
            if (!migration.Success)
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<ISettingsRepo>();
            var sonosConnectorRepo = scope.ServiceProvider.GetRequiredService<ISonosConnectorRepo>();
            var sceneOrchestration = scope.ServiceProvider.GetRequiredService<ISceneOrchestrationService>();
            var actionLogger = scope.ServiceProvider.GetRequiredService<ActionLogger>();

            var settings = await settingsRepo.GetSettings();
            if (settings is null || settings.SettingsSchemaVersion < SonosSettings.CurrentSettingsSchemaVersion)
            {
                return;
            }

            settings.NormalizeAutomationDateExceptions();
            settings.ScheduleWindows ??= new();
            settings.Scenes ??= new();
            var eligibleWindows = GetEligibleWindows(settings);

            var utcNow = _timeProvider.GetUtcNow();
            var localNow = TimeZoneInfo.ConvertTime(utcNow, _timeZone);
            var winningWindows = SelectWinningWindows(eligibleWindows, localNow, settings);
            var flyDj = scope.ServiceProvider.GetService<IFlyDjScheduleConductor>();
            if (flyDj is not null)
                await flyDj.UpdateSchedulesAsync(GetFlyDjSchedules(settings, utcNow), cancellationToken);
            if (winningWindows.Count > 0 || _ownedWindowOccurrences.Count > 0)
            {
                await EvaluateWinningWindowsAsync(winningWindows, settings, localNow, utcNow, sonosConnectorRepo, sceneOrchestration, actionLogger, cancellationToken, flyDj is not null);
                return;
            }
            _runtimeStatus.RecordEvaluation(null, utcNow);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtimeStatus.RecordEvaluation(null, _timeProvider.GetUtcNow(), ex.Message);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Overlapping manual playback preempted this occurrence. It is not a scheduler failure.
            _runtimeStatus.RecordEvaluation(null, _timeProvider.GetUtcNow());
        }
        finally { Volatile.Write(ref _evaluating, 0); }
    }

    private static List<ScheduleWindow> GetEligibleWindows(SonosSettings settings)
        => settings.ScheduleWindows
            .Where(window => window.IsEnabled)
            .Where(window => window.HasTimeSchedule)
            .Where(window => window.Playback is { Enabled: true })
            .ToList();

    /// <summary>
    /// Rooms targeted by the schedule windows that are active (and winning) at <paramref name="utcNow"/>,
    /// using the same selection as the evaluation loop. Other automation, such as FlyDJ, stays out of them.
    /// </summary>
    public IReadOnlyCollection<string> GetScheduleOwnedSpeakers(SonosSettings settings, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SettingsSchemaVersion < SonosSettings.CurrentSettingsSchemaVersion) return [];
        settings.NormalizeAutomationDateExceptions();
        settings.Speakers ??= new();
        settings.Scenes ??= new();
        var localNow = TimeZoneInfo.ConvertTime(utcNow, _timeZone);
        return SelectWinningWindows(GetEligibleWindows(settings), localNow, settings)
            .SelectMany(item => item.Targets)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<FlyDjSchedulePlayback> GetFlyDjSchedules(SonosSettings settings, DateTimeOffset utcNow)
    {
        if (settings.SettingsSchemaVersion < SonosSettings.CurrentSettingsSchemaVersion) return [];
        var localNow = TimeZoneInfo.ConvertTime(utcNow, _timeZone);
        var eligible = GetEligibleWindows(settings);
        var result = SelectWinningWindows(eligible, localNow, settings)
            .Where(item => item.Window.Playback?.SourceType == SceneSourceType.FlyDj)
            .Select(item => new FlyDjSchedulePlayback(item.Window, item.Targets.ToArray(),
                ResolveWindowStartUtc(item.Window, localNow), ResolveWindowEndUtc(item.Window, localNow))).ToList();
        var lead = FlyDjObservationPlan.FromMinutes(settings.FlyDj?.MinDwellMinutes ?? FlyDjSettings.DefaultMinDwellMinutes).Window;
        foreach (var window in eligible.Where(window => window.FlyDjPrewarm && window.Playback?.SourceType == SceneSourceType.FlyDj))
            for (var day = 0; day <= 1; day++)
            {
                var localStart = DateOnly.FromDateTime(localNow.DateTime).AddDays(day).ToDateTime(window.StartTime);
                while (_timeZone.IsInvalidTime(localStart)) localStart = localStart.AddMinutes(1);
                var start = _timeZone.IsAmbiguousTime(localStart)
                    ? new DateTimeOffset(localStart, _timeZone.GetAmbiguousTimeOffsets(localStart).Max()).ToUniversalTime()
                    : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, _timeZone));
                if (start <= utcNow || start - utcNow > lead) continue;
                var startLocal = TimeZoneInfo.ConvertTime(start, _timeZone);
                var winning = SelectWinningWindows(eligible, startLocal, settings).FirstOrDefault(item => item.Window.Id == window.Id);
                if (winning.Window is null) continue;
                result.Add(new(window, winning.Targets.ToArray(), start, ResolveWindowEndUtc(window, startLocal)));
            }
        return result.DistinctBy(schedule => schedule.Key).ToArray();
    }

    private static List<(ScheduleWindow Window, List<string> Targets)> SelectWinningWindows(
        IEnumerable<ScheduleWindow> windows, DateTimeOffset localNow, SonosSettings settings)
    {
        var candidates = ScheduleWindowOrdering.Order(windows
                .Where(window => ScheduleWindowEvaluator.IsWindowActive(window, localNow, settings)))
            .ToList();
        var winners = new Dictionary<string, ScheduleWindow>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in candidates)
        {
            if (window.Playback is null) continue;
            foreach (var ip in ResolveWindowTargetSpeakers(window, settings))
                winners.TryAdd(ip, window);
        }
        return winners.GroupBy(pair => pair.Value.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => (candidates.First(window => string.Equals(window.Id, group.Key, StringComparison.OrdinalIgnoreCase)), group.Select(pair => pair.Key).ToList()))
            .ToList();
    }

    private async Task EvaluateWinningWindowsAsync(
        IReadOnlyCollection<(ScheduleWindow Window, List<string> Targets)> winners,
        SonosSettings settings,
        DateTimeOffset localNow,
        DateTimeOffset utcNow,
        ISonosConnectorRepo connector,
        ISceneOrchestrationService orchestration,
        ActionLogger actionLogger,
        CancellationToken cancellationToken, bool flyDjEnabled)
    {
        var date = DateOnly.FromDateTime(localNow.DateTime);
        var active = new Dictionary<string, (ScheduleWindow Window, List<string> Targets)>(StringComparer.OrdinalIgnoreCase);
        foreach (var winner in winners)
            active[winner.Window.Id] = winner;
        var protectedTargets = winners.SelectMany(item => item.Targets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? pendingFailure = null;
        DateTimeOffset? pendingRetryAt = null;

        foreach (var old in _ownedWindowOccurrences.ToArray())
        {
            if (active.TryGetValue(old.Key, out var stillActive)
                && stillActive.Targets.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual((old.Value.DesiredTargets ?? old.Value.Targets).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                continue;
            try
            {
                await StopOwnedPlaybackAsync(old.Value, connector, actionLogger, cancellationToken,
                    protectedTargets, skipFadeOut: settings.ExcludesAutomationDate(DateOnly.FromDateTime(localNow.DateTime)), mandatoryStop: !active.ContainsKey(old.Key));
                _ownedWindowOccurrences.Remove(old.Key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missing or changing room must not prevent independent winning rooms from
                // starting. Retain ownership so the next poll retries the cleanup.
                pendingFailure ??= $"Schedule '{old.Value.Name}' could not stop playback: {ex.Message}";
                pendingRetryAt ??= utcNow.AddSeconds(PollInterval.TotalSeconds);
                _logger.LogWarning(ex, "Schedule {WindowId} cleanup failed; other winning windows will continue.", old.Key);
            }
        }

        var statusNames = new List<string>();
        foreach (var item in winners)
        {
            var window = item.Window;
            var scene = window.Playback;
            if (scene is null || item.Targets.Count == 0) continue;
            if (scene.SourceType == SceneSourceType.FlyDj)
            {
                // FlyDJ owns station selection and registers accepted writes with the stop
                // worker. The scheduler retains cleanup ownership and the usual priority rules.
                if (!flyDjEnabled)
                {
                    pendingFailure ??= $"Schedule '{window.Name}' requires FlyDJ to be enabled on this server.";
                    continue;
                }
                var start = ResolveWindowStartUtc(window, localNow);
                _ownedWindowOccurrences[window.Id] = new ActiveOccurrence(
                    new AttemptKey(window.Id, window.LastModifiedUtc, scene.Id, scene.LastModifiedUtc, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, _timeZone).DateTime)),
                    start, window.Name, window.FadeOutSeconds, scene.IsSyncedPlayback,
                    item.Targets.ToArray(), item.Targets.ToArray(), ResolveWindowEndUtc(window, localNow));
                statusNames.Add(window.Name);
                continue;
            }
            var startDate = date;
            if (window.StopTime <= window.StartTime && TimeOnly.FromDateTime(localNow.DateTime) < window.StopTime)
                startDate = startDate.AddDays(-1);
            var localStart = startDate.ToDateTime(window.StartTime);
            while (_timeZone.IsInvalidTime(localStart)) localStart = localStart.AddMinutes(1);
            var occurrenceStart = _timeZone.IsAmbiguousTime(localStart)
                ? new DateTimeOffset(localStart, _timeZone.GetAmbiguousTimeOffsets(localStart).Max()).ToUniversalTime()
                : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, _timeZone));
            var attempt = new AttemptKey(window.Id, window.LastModifiedUtc, scene.Id, scene.LastModifiedUtc, startDate);
            var existing = _ownedWindowOccurrences.TryGetValue(window.Id, out var current) ? current : null;
            if (_windowFailures.TryGetValue(window.Id, out var failure) && failure.Attempt == attempt && utcNow < failure.RetryAt)
            {
                pendingFailure ??= failure.Message;
                pendingRetryAt = !pendingRetryAt.HasValue || failure.RetryAt < pendingRetryAt.Value ? failure.RetryAt : pendingRetryAt;
                statusNames.Add(window.Name);
                continue;
            }
            if (existing?.Attempt == attempt && (existing.DesiredTargets ?? existing.Targets).Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(item.Targets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                && (!_windowFailures.TryGetValue(window.Id, out var pending) || pending.Attempt != attempt))
            {
                statusNames.Add(window.Name);
                continue;
            }

            var groups = await ResolveAffectedGroupsAsync(item.Targets, connector, cancellationToken);
            var manual = _commands.ManualSpeakersSince(occurrenceStart).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = item.Targets.Where(ip => !groups[ip].Any(manual.Contains)).ToList();
            if (targets.Count == 0) continue;
            var starts = scene.IsSyncedPlayback ? new List<IReadOnlyCollection<string>> { targets }
                : targets.Select(ip => (IReadOnlyCollection<string>)[ip]).ToList();
            var touched = new HashSet<string>(existing?.Targets ?? [], StringComparer.OrdinalIgnoreCase);
            var occurrence = new ActiveOccurrence(attempt, occurrenceStart, window.Name, window.FadeOutSeconds, scene.IsSyncedPlayback, touched, item.Targets.ToArray(), ResolveWindowEndUtc(window, localNow),
                existing?.Attempt == attempt ? existing.ScheduleGenerations : null);
            _ownedWindowOccurrences[window.Id] = occurrence;
            var touchedSync = new object();
            var failedSync = new object();
            var failedResults = new List<string>();
            void RecordTouched(IReadOnlyCollection<string> applied)
            {
                lock (touchedSync)
                {
                    foreach (var ip in applied) touched.Add(ip);
                    _ownedWindowOccurrences[window.Id] = _ownedWindowOccurrences[window.Id] with { Targets = touched.ToArray() };
                }
            }
            async Task ApplyPartitionAsync(IReadOnlyCollection<string> partition)
            {
                var partitionTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void RecordPartitionTargets(IReadOnlyCollection<string> applied)
                {
                    foreach (var ip in applied) partitionTouched.Add(ip);
                    RecordTouched(applied);
                }
                var leaseTargets = partition.SelectMany(ip => groups[ip]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var commandLease = await _commands.EnterAutomationAsync(leaseTargets, occurrenceStart, cancellationToken);
                if (commandLease is null) return;
                await using var context = new SceneExecutionContext(_commands, PlaybackCommandOrigin.Automation, occurrenceStart, cancellationToken, commandLease);
                try
                {
                    using var partitionScope = _scopeFactory.CreateScope();
                    var partitionOrchestration = partitionScope.ServiceProvider.GetRequiredService<ISceneOrchestrationService>();
                    var result = await partitionOrchestration.ApplySceneWithContextAsync(scene, "automation-scheduler", context,
                        RecordPartitionTargets, context.CancellationToken, partition);
                    // Orchestrators normally report every write through RecordTouched before
                    // returning. Keep the result as a second ownership source so successful
                    // adapters that only report their final targets are still tracked for
                    // status and eventual cleanup.
                    RecordPartitionTargets(result.TargetSpeakers);
                    var scheduleTargets = partitionTouched.Concat(result.TargetSpeakers)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (_autoStop is not null && scheduleTargets.Length > 0)
                    {
                        // Persist a stop deadline for every room that accepted writes, even
                        // when a later scene action or recovery action made the overall
                        // partition report failure.
                        var generations = await _autoStop.RegisterScheduleOccurrenceAsync(scheduleTargets, window, cancellationToken, includeRecoveryTargets: true);
                        lock (touchedSync)
                        {
                            var combined = (_ownedWindowOccurrences[window.Id].ScheduleGenerations
                                ?? new Dictionary<string, long>()).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                            foreach (var pair in generations) combined[pair.Key] = pair.Value;
                            _ownedWindowOccurrences[window.Id] = _ownedWindowOccurrences[window.Id] with
                            {
                                ScheduleGenerations = combined,
                                StopAtUtc = ResolveWindowEndUtc(window, localNow)
                            };
                        }
                    }
                    if (!result.Success)
                    {
                        lock (failedSync) failedResults.Add(result.Message);
                        _logger.LogWarning("Schedule {WindowId} failed: {Message}", window.Id, result.Message);
                    }
                    else
                    {
                        if (window.FadeInSeconds > 0 && result.TargetSpeakers.Count > 0 && context.CurrentLease is not null)
                            await ApplyFadeInAsync(result.TargetSpeakers, window.FadeInSeconds, connector, context.CancellationToken);
                    }
                }
                catch (OperationCanceledException) when (context.WasPreempted) { }
            }
            await Task.WhenAll(starts.Select(ApplyPartitionAsync));
            if (failedResults.Count > 0)
            {
                var count = _windowFailures.TryGetValue(window.Id, out var oldFailure) && oldFailure.Attempt == attempt ? oldFailure.Count + 1 : 1;
                var error = $"Schedule '{window.Name}' could not apply playback '{scene.Name}': {string.Join("; ", failedResults)}";
                var retryAt = utcNow.AddSeconds(RetrySeconds[Math.Min(count - 1, RetrySeconds.Length - 1)]);
                _windowFailures[window.Id] = (attempt, count, retryAt, error);
                await actionLogger.LogAsync("ScheduleTriggerFailed", error, "automation-scheduler");
                pendingFailure ??= error;
                pendingRetryAt = !pendingRetryAt.HasValue || retryAt < pendingRetryAt.Value ? retryAt : pendingRetryAt;
                continue;
            }
            if (touched.Count > 0 || failedResults.Count == 0)
            {
                // Keep generation/deadline state written while each partition was running.
                // Replacing it with the initial occurrence snapshot would make the stop
                // worker and scheduler disagree about ownership at the boundary.
                _ownedWindowOccurrences[window.Id] = _ownedWindowOccurrences[window.Id] with { Targets = touched.ToArray() };
                statusNames.Add(window.Name);
                if (_windowFailures.Remove(window.Id, out var recovered))
                    await actionLogger.LogAsync("ScheduleRecovered", $"Schedule '{window.Name}' recovered after {recovered.Count} failed start(s).", "automation-scheduler");
                await actionLogger.LogAsync("ScheduleTriggered", $"Schedule '{window.Name}' triggered playback '{scene.Name}'.", "automation-scheduler");
            }
        }
        _runtimeStatus.RecordEvaluation(pendingFailure is null && statusNames.Count > 0 ? string.Join(", ", statusNames.Distinct()) : null,
            utcNow, pendingFailure, pendingRetryAt);
    }

    private static TimeZoneInfo ResolveTimeZone(string? configuredId)
    {
        var id = string.IsNullOrWhiteSpace(configuredId) ? "Europe/Vienna" : configuredId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private DateTimeOffset ResolveWindowEndUtc(ScheduleWindow window, DateTimeOffset localNow)
    {
        var date = DateOnly.FromDateTime(localNow.DateTime);
        if (window.StopTime <= window.StartTime && TimeOnly.FromDateTime(localNow.DateTime) >= window.StartTime)
            date = date.AddDays(1);
        var localEnd = date.ToDateTime(window.StopTime);
        while (_timeZone.IsInvalidTime(localEnd)) localEnd = localEnd.AddMinutes(1);
        return _timeZone.IsAmbiguousTime(localEnd)
            ? new DateTimeOffset(localEnd, _timeZone.GetAmbiguousTimeOffsets(localEnd).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, _timeZone));
    }

    private DateTimeOffset ResolveWindowStartUtc(ScheduleWindow window, DateTimeOffset localNow)
    {
        var date = DateOnly.FromDateTime(localNow.DateTime);
        if (window.StopTime <= window.StartTime && TimeOnly.FromDateTime(localNow.DateTime) < window.StopTime)
            date = date.AddDays(-1);
        var localStart = date.ToDateTime(window.StartTime);
        while (_timeZone.IsInvalidTime(localStart)) localStart = localStart.AddMinutes(1);
        return _timeZone.IsAmbiguousTime(localStart)
            ? new DateTimeOffset(localStart, _timeZone.GetAmbiguousTimeOffsets(localStart).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, _timeZone));
    }

    private async Task StopOwnedPlaybackAsync(
        ActiveOccurrence occurrence,
        ISonosConnectorRepo sonosConnectorRepo,
        ActionLogger actionLogger,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludeSpeakerIps = null,
        bool skipFadeOut = false,
        bool mandatoryStop = false)
    {
        // Snapshot the generation token before any wait for room ownership. A manual restart
        // may replace the persisted occurrence while this stop is queued.
        var expectedScheduleGenerations = occurrence.ScheduleGenerations is null
            ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            : occurrence.ScheduleGenerations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var targetSpeakers = occurrence.Targets.ToList();
        if (excludeSpeakerIps?.Count > 0)
        {
            targetSpeakers = targetSpeakers
                .Where(ip => !excludeSpeakerIps.Contains(ip, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        if (targetSpeakers.Count == 0)
        {
            return;
        }

        if (mandatoryStop && _autoStop is not null && occurrence.StopAtUtc.HasValue)
        {
            expectedScheduleGenerations = (await _autoStop.GetScheduleStopGenerationsAsync(
                targetSpeakers, occurrence.Attempt.WindowId, occurrence.StopAtUtc.Value, cancellationToken))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }
        var stopIsDue = !occurrence.StopAtUtc.HasValue || occurrence.StopAtUtc.Value <= _timeProvider.GetUtcNow();

        List<IReadOnlyCollection<string>> partitions = occurrence.IsSynced
            ? [targetSpeakers]
            : targetSpeakers.Select(ip => (IReadOnlyCollection<string>)[ip]).ToList();
        var stoppedAny = false;
        foreach (var partition in partitions)
        {
            IReadOnlyCollection<string> leaseTargets = partition;
            if (mandatoryStop)
            {
                var affected = new HashSet<string>(partition, StringComparer.OrdinalIgnoreCase);
                foreach (var ip in partition)
                {
                    var members = await sonosConnectorRepo.GetAllSpeakersInGroup(ip, cancellationToken)
                        ?? throw new InvalidOperationException($"Could not verify the current group for scheduled speaker {ip}; stop was deferred.");
                    if (!members.Any()) throw new InvalidOperationException($"Could not verify the current group for scheduled speaker {ip}; stop was deferred.");
                    affected.UnionWith(members.Where(member => !string.IsNullOrWhiteSpace(member)));
                }
                leaseTargets = affected;
            }
            PlaybackCommandCoordinator.PlaybackCommandLease? ownership = mandatoryStop
                ? await _commands.EnterMandatoryStopAsync(leaseTargets, cancellationToken)
                : await _commands.EnterAutomationAsync(partition, occurrence.StartUtc, cancellationToken);
            if (ownership is null) continue;
            await using var lease = ownership;
            try
            {
                IReadOnlyCollection<string> stopTargets = partition;
                IReadOnlySet<string>? validScheduledTargets = null;
                if (mandatoryStop && _autoStop is not null)
                {
                    if (occurrence.StopAtUtc is null) continue;
                    validScheduledTargets = await _autoStop.GetValidDueScheduleStopGenerationsAsync(
                        partition, occurrence.Attempt.WindowId, occurrence.StopAtUtc.Value,
                        expectedScheduleGenerations, lease.CancellationToken, requireDue: stopIsDue);
                    stopTargets = partition.Where(validScheduledTargets.Contains).ToArray();
                    if (stopTargets.Count == 0) continue;
                }

                // Group membership can change while ownership is being acquired. Revalidate
                // before touching volume or isolation, and never act on a member outside the
                // room lease.
                var leaseTargetsSet = lease.Speakers.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var ip in stopTargets)
                {
                    var members = await sonosConnectorRepo.GetAllSpeakersInGroup(ip, lease.CancellationToken);
                    if (members is null) throw new InvalidOperationException($"Could not verify the current group for scheduled speaker {ip}; stop was deferred.");
                    if (!members.Any()) throw new InvalidOperationException($"Could not verify the current group for scheduled speaker {ip}; stop was deferred.");
                    var currentGroup = members.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    currentGroup.Add(ip);
                    if (currentGroup.Any(member => !leaseTargetsSet.Contains(member)))
                        throw new InvalidOperationException($"Speaker group changed while stopping scheduled speaker {ip}; stop was deferred.");
                }
                if (!skipFadeOut && occurrence.FadeOutSeconds > 0)
                {
                    using var fadeCancellation = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken, lease.YieldToken);
                    try { await ApplyFadeOutAsync(stopTargets, occurrence.FadeOutSeconds, sonosConnectorRepo, fadeCancellation.Token); }
                    catch (OperationCanceledException) when (!lease.CancellationToken.IsCancellationRequested && lease.YieldToken.IsCancellationRequested) { }
                }
                lease.CancellationToken.ThrowIfCancellationRequested();
                if (mandatoryStop)
                {
                    foreach (var ip in stopTargets)
                    {
                        var members = await sonosConnectorRepo.GetAllSpeakersInGroup(ip, lease.CancellationToken);
                        if (members is null) throw new InvalidOperationException($"Could not verify the group for scheduled speaker {ip}; stop was deferred.");
                        if (!members.Any()) throw new InvalidOperationException($"Could not verify the group for scheduled speaker {ip}; stop was deferred.");
                        var group = members.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        group.Add(ip);
                        if (group.Any(member => !leaseTargetsSet.Contains(member)))
                            throw new InvalidOperationException($"Speaker group changed while isolating scheduled speaker {ip}; stop was deferred.");
                        if (group.Count > 1)
                        {
                            await sonosConnectorRepo.UngroupSpeaker(ip, lease.CancellationToken);
                            var after = await sonosConnectorRepo.GetAllSpeakersInGroup(ip, lease.CancellationToken);
                            if (after is null) throw new InvalidOperationException($"Could not verify isolation for scheduled speaker {ip}; stop was deferred.");
                            var afterUngroup = after.ToHashSet(StringComparer.OrdinalIgnoreCase);
                            if (afterUngroup.Count != 1 || !afterUngroup.Contains(ip))
                                throw new InvalidOperationException($"Could not isolate scheduled speaker {ip} before stopping playback.");
                        }
                        await sonosConnectorRepo.PausePlaying(ip, lease.CancellationToken);
                        stoppedAny = true;
                        if (_autoStop is not null && occurrence.StopAtUtc.HasValue && validScheduledTargets?.Contains(ip) == true)
                            await _autoStop.TryAcknowledgeScheduleStopAsync([ip], occurrence.Attempt.WindowId,
                                occurrence.StopAtUtc.Value, expectedScheduleGenerations, lease.CancellationToken, requireDue: stopIsDue);
                    }
                }
                else
                {
                    await Task.WhenAll(stopTargets.Select(ip => sonosConnectorRepo.PausePlaying(ip, lease.CancellationToken)));
                    stoppedAny = true;
                }
            }
            catch (OperationCanceledException) when (lease.WasPreempted) { continue; }
        }
        if (!stoppedAny) return;
        await actionLogger.LogAsync(
            "ScheduleStopped",
            $"Schedule '{occurrence.Name}' stopped.", "automation-scheduler");
        if (_playbackSnapshots is not null)
            await _playbackSnapshots.RefreshNowAsync(cancellationToken);
    }

    private static IReadOnlyCollection<string> ResolveWindowTargetSpeakers(ScheduleWindow? window, SonosSettings settings)
    {
        settings.Speakers ??= new();
        settings.Scenes ??= new();

        if (window?.Playback is not null) return SceneTargetResolver.Resolve(window.Playback, settings);

        return settings.Speakers
            .Select(speaker => speaker.IpAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<string, IReadOnlyCollection<string>>> ResolveAffectedGroupsAsync(
        IReadOnlyCollection<string> selectedTargets, ISonosConnectorRepo connector, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in selectedTargets)
        {
            var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ip };
            try
            {
                foreach (var member in await connector.GetAllSpeakersInGroup(ip, cancellationToken) ?? [])
                    if (!string.IsNullOrWhiteSpace(member)) group.Add(member.Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not resolve current group for scheduled target {SpeakerIp}; playback will revalidate it before writes.", ip);
            }
            result[ip] = group.ToArray();
        }
        return result;
    }

    private static async Task ApplyFadeOutAsync(
        IReadOnlyCollection<string> targetSpeakers,
        int fadeOutSeconds,
        ISonosConnectorRepo sonosConnectorRepo,
        CancellationToken cancellationToken)
    {
        var steps = Math.Clamp(fadeOutSeconds, 2, 20);
        var delay = TimeSpan.FromSeconds((double)fadeOutSeconds / steps);
        var startingVolumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in targetSpeakers)
        {
            try
            {
                startingVolumes[ip] = await sonosConnectorRepo.GetVolume(ip, cancellationToken);
            }
            catch
            {
                startingVolumes[ip] = 10;
            }
        }

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ratio = (double)(steps - step) / steps;
            await Task.WhenAll(targetSpeakers.Select(ip =>
            {
                var start = startingVolumes.TryGetValue(ip, out var value) ? value : 10;
                return sonosConnectorRepo.SetSpeakerVolume(
                    ip,
                    Math.Max(0, (int)Math.Round(start * ratio)),
                    cancellationToken);
            }));
            await Task.Delay(delay, cancellationToken);
        }
    }

    internal static async Task ApplyFadeInAsync(
        IReadOnlyCollection<string> targetSpeakers,
        int fadeInSeconds,
        ISonosConnectorRepo sonosConnectorRepo,
        CancellationToken cancellationToken)
    {
        var steps = Math.Clamp(fadeInSeconds, 2, 20);
        var delay = TimeSpan.FromSeconds((double)fadeInSeconds / steps);
        var targetVolumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in targetSpeakers)
        {
            try
            {
                targetVolumes[ip] = await sonosConnectorRepo.GetVolume(ip, cancellationToken);
            }
            catch
            {
                targetVolumes[ip] = 15;
            }
        }

        await Task.WhenAll(targetSpeakers.Select(ip =>
            sonosConnectorRepo.SetSpeakerVolume(ip, 1, cancellationToken)));

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ratio = (double)step / steps;
            await Task.WhenAll(targetSpeakers.Select(ip =>
            {
                var target = targetVolumes.TryGetValue(ip, out var value) ? value : 15;
                return sonosConnectorRepo.SetSpeakerVolume(
                    ip,
                    Math.Max(1, (int)Math.Round(target * ratio)),
                    cancellationToken);
            }));
            await Task.Delay(delay, cancellationToken);
        }
    }
}
