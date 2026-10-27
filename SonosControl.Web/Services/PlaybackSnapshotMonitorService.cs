using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services.SonosEvents;

namespace SonosControl.Web.Services;

/// <summary>
/// Runs one bounded, server-owned playback poller for all connected UI circuits. Speakers with working
/// live events are only reconciled now and then; all others are polled every cycle.
/// </summary>
public sealed class PlaybackSnapshotMonitorService(
    IServiceScopeFactory scopeFactory,
    PlaybackSnapshotStore store,
    ILogger<PlaybackSnapshotMonitorService> logger,
    TimeProvider timeProvider,
    ISonosEventHealth? eventHealth = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private const double EventedStaleFactor = 2.5;
    private readonly SemaphoreSlim _parallelism = new(4, 4);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _inFlightSync = new();
    private readonly Dictionary<string, Task> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProbeStarted = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, string> _speakerIpsByUuid = new Dictionary<string, string>();

    /// <summary>Evented speakers only go stale after several missed reconciliations; polled ones after 15 s.</summary>
    internal TimeSpan StaleAfterFor(string speakerIp)
        => eventHealth?.IsHealthy(speakerIp) == true ? eventHealth.ReconcileInterval * EventedStaleFactor : StaleAfter;

    private bool NeedsProbe(string speakerIp, DateTimeOffset now)
        => eventHealth?.IsHealthy(speakerIp) != true
            || !_lastProbeStarted.TryGetValue(speakerIp, out var last)
            || now - last >= eventHealth.ReconcileInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(PollLoopAsync(stoppingToken), StaleHeartbeatAsync(stoppingToken));
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var started = timeProvider.GetUtcNow();
            try { await PollOnceAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Playback snapshot poll cycle failed."); }
            var remaining = PollInterval - (timeProvider.GetUtcNow() - started);
            if (remaining > TimeSpan.Zero)
                try { await Task.Delay(remaining, timeProvider, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task StaleHeartbeatAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            store.MarkExpiredStale(now, StaleAfterFor);
            var deadline = store.GetNextStaleDeadline(StaleAfterFor);
            // Thresholds shrink when a speaker loses its events, so never sleep longer than one poll interval.
            var delay = deadline is { } next && next > now
                ? TimeSpan.FromTicks(Math.Min((next - now).Ticks, PollInterval.Ticks))
                : deadline is null ? PollInterval : TimeSpan.Zero;
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, token);
                else await Task.Yield();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    /// <summary>One regular cycle: probes polled speakers and evented speakers that are due for reconciliation.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        await PollCycleCoreAsync(probeAll: false, cancellationToken);
    }

    private async Task PollCycleCoreAsync(bool probeAll, CancellationToken cancellationToken)
    {
        var read = store.BeginConfigurationRead();
        SonosSettings settings;
        using (var scope = scopeFactory.CreateScope())
            settings = await scope.ServiceProvider.GetRequiredService<ISettingsRepo>().GetSettings() ?? new SonosSettings();

        settings.Speakers ??= [];
        settings.Stations ??= [];
        settings.SpotifyTracks ??= [];
        settings.YouTubeCollections ??= [];
        settings.YouTubeMusicCollections ??= [];
        var speakers = settings.Speakers.Select(speaker => new SonosSpeaker
        {
            IpAddress = speaker.IpAddress,
            Name = speaker.Name,
            Uuid = speaker.Uuid
        }).ToArray();

        if (!store.TryApplyConfiguration(read.Sequence, read.MutationVersion, speakers.Select(speaker => speaker.IpAddress), out var epoch)) return;
        store.NotifySettingsChanged(CreateFingerprint(settings));
        store.RemoveExcept(speakers.Select(speaker => speaker.IpAddress));
        _speakerIpsByUuid = speakers
            .Select(speaker => (Uuid: PlaybackSnapshotMerge.NormalizeUuid(speaker.Uuid), speaker.IpAddress))
            .Where(item => item.Uuid is not null)
            .DistinctBy(item => item.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Uuid!, item => item.IpAddress, StringComparer.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow();
        await Task.WhenAll(speakers.Where(speaker => probeAll || NeedsProbe(speaker.IpAddress, now))
            .Select(speaker => PollSpeakerAsync(speaker, epoch, cancellationToken)));
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            // Drain probes already running or queued before starting an explicit post-command refresh.
            while (true)
            {
                Task[] prior;
                lock (_inFlightSync) prior = _inFlight.Values.ToArray();
                if (prior.Length == 0) break;
                try { await Task.WhenAll(prior).WaitAsync(cancellationToken); }
                catch when (!cancellationToken.IsCancellationRequested) { }
                cancellationToken.ThrowIfCancellationRequested();
            }
            // A command follow-up must see every speaker, evented or not.
            await PollCycleCoreAsync(probeAll: true, cancellationToken);
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Probes one configured speaker now, for example after a lost or unreadable event.</summary>
    public async Task ProbeSpeakerAsync(string speakerIp, CancellationToken cancellationToken = default)
    {
        SonosSpeaker? speaker;
        using (var scope = scopeFactory.CreateScope())
            speaker = (await scope.ServiceProvider.GetRequiredService<ISettingsRepo>().GetSettings())?.Speakers?
                .FirstOrDefault(item => string.Equals(item.IpAddress, speakerIp, StringComparison.OrdinalIgnoreCase));
        if (speaker is null) return;
        await PollSpeakerAsync(new SonosSpeaker { IpAddress = speaker.IpAddress, Name = speaker.Name, Uuid = speaker.Uuid },
            store.ConfigurationEpoch, cancellationToken);
    }

    private Task PollSpeakerAsync(SonosSpeaker speaker, long epoch, CancellationToken cancellationToken, bool confirmConflicts = true)
    {
        var key = $"{epoch}:{speaker.IpAddress}";
        TaskCompletionSource? owner = null;
        Task existing;
        lock (_inFlightSync)
        {
            if (!store.IsConfigured(speaker.IpAddress, epoch)) return Task.CompletedTask;
            if (!_inFlight.TryGetValue(key, out existing!))
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                existing = owner.Task;
                _inFlight.Add(key, existing);
            }
        }
        if (owner is not null)
        {
            var generation = store.BeginProbe(speaker.IpAddress, epoch);
            if (generation == 0)
            {
                lock (_inFlightSync)
                    if (_inFlight.TryGetValue(key, out var active) && ReferenceEquals(active, owner.Task)) _inFlight.Remove(key);
                owner.TrySetResult();
            }
            else _ = PollSpeakerCoreAsync(speaker, epoch, generation, key, cancellationToken, owner, confirmConflicts);
        }
        return existing.WaitAsync(cancellationToken);
    }

    private async Task PollSpeakerCoreAsync(SonosSpeaker speaker, long epoch, long generation, string key, CancellationToken cancellationToken,
        TaskCompletionSource completion, bool confirmConflicts)
    {
        var acquired = false;
        var confirm = false;
        try
        {
            await _parallelism.WaitAsync(cancellationToken);
            acquired = true;
            if (!store.IsConfigured(speaker.IpAddress, epoch) || !store.IsCurrentProbe(speaker.IpAddress, generation)) return;
            _lastProbeStarted[speaker.IpAddress] = timeProvider.GetUtcNow();
            using var timeout = new CancellationTokenSource(DeviceTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var connector = scope.ServiceProvider.GetRequiredService<ISonosConnectorRepo>();
                var ip = speaker.IpAddress;
                var volumeTask = connector.GetVolume(ip, linked.Token);
                var playingTask = connector.IsPlaying(ip, linked.Token);
                var stationTask = connector.GetCurrentStationAsync(ip, linked.Token);
                var trackTask = connector.GetTrackInfoAsync(ip, linked.Token);
                await Task.WhenAll(volumeTask, playingTask, stationTask, trackTask);
                var now = timeProvider.GetUtcNow();
                var track = PlaybackSnapshotMerge.KeepStreamTitle(store.Get(ip)?.TrackInfo, trackTask.Result);
                var snapshot = new PlaybackSnapshot(ip, speaker.Name, playingTask.Result, volumeTask.Result,
                    stationTask.Result, track, false, now, now, null);
                if (FreshCoordinatorSnapshot(ip, stationTask.Result) is { } coordinator)
                    snapshot = PlaybackSnapshotMerge.FollowCoordinator(snapshot, coordinator);
                // An event that lands during the probe can be older than what the probe read, for example a delayed
                // Pause notification after a Play command. Read the speaker once more instead of trusting either side.
                confirm = !store.TrySet(snapshot, generation, epoch) && confirmConflicts && store.IsSupersededByEvent(ip, generation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Playback snapshot refresh failed for {SpeakerIp}.", speaker.IpAddress);
                var now = timeProvider.GetUtcNow();
                var previous = store.Get(speaker.IpAddress);
                store.TrySet(previous is null
                    ? new PlaybackSnapshot(speaker.IpAddress, speaker.Name, false, null, null, null, true, now, null, "Playback status unavailable")
                    : previous with { SpeakerName = speaker.Name, IsStale = true, LastCheckedUtc = now, Error = "Playback status unavailable" }, generation, epoch);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally
        {
            if (acquired) _parallelism.Release();
            lock (_inFlightSync)
                if (_inFlight.TryGetValue(key, out var active) && ReferenceEquals(active, completion.Task)) _inFlight.Remove(key);
            completion.TrySetResult();
        }

        if (confirm)
        {
            logger.LogDebug("Confirming playback of {SpeakerIp} after an event raced a probe.", speaker.IpAddress);
            _ = PollSpeakerAsync(speaker, epoch, cancellationToken, confirmConflicts: false);
        }
    }

    /// <summary>The snapshot of the configured coordinator a group member points at, if it is fresh.</summary>
    private PlaybackSnapshot? FreshCoordinatorSnapshot(string memberIp, string? transportUri)
    {
        if (PlaybackSnapshotMerge.CoordinatorUuid(transportUri) is not { } uuid
            || !_speakerIpsByUuid.TryGetValue(uuid, out var coordinatorIp)
            || string.Equals(coordinatorIp, memberIp, StringComparison.OrdinalIgnoreCase)) return null;
        return store.Get(coordinatorIp) is { IsStale: false } coordinator ? coordinator : null;
    }

    private static string CreateFingerprint(SonosSettings settings)
    {
        var builder = new StringBuilder().Append(settings.MaxVolume).Append('|');
        foreach (var speaker in settings.Speakers.OrderBy(item => item.IpAddress, StringComparer.OrdinalIgnoreCase))
            builder.Append(speaker.IpAddress).Append(':').Append(speaker.Name).Append(':').Append(speaker.Uuid).Append('|');
        foreach (var source in settings.Stations.Select(item => (item.Name, item.Url))
                     .Concat(settings.SpotifyTracks.Select(item => (item.Name, item.Url)))
                     .Concat(settings.YouTubeCollections.Select(item => (item.Name, item.Url)))
                     .Concat(settings.YouTubeMusicCollections.Select(item => (item.Name, item.Url))))
            builder.Append(source.Name).Append(':').Append(source.Url).Append('|');
        return builder.ToString();
    }
}
