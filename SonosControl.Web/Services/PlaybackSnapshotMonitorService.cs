using System.Text;
using Microsoft.Extensions.Hosting;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

/// <summary>Runs one bounded, server-owned playback poller for all connected UI circuits.</summary>
public sealed class PlaybackSnapshotMonitorService(
    IServiceScopeFactory scopeFactory,
    PlaybackSnapshotStore store,
    ILogger<PlaybackSnapshotMonitorService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _parallelism = new(4, 4);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _inFlightSync = new();
    private readonly Dictionary<string, Task> _inFlight = new(StringComparer.OrdinalIgnoreCase);

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
            store.MarkExpiredStale(now, StaleAfter);
            var deadline = store.GetNextStaleDeadline(StaleAfter);
            var delay = deadline is { } next && next > now
                ? next - now
                : deadline is null ? PollInterval : TimeSpan.Zero;
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, token);
                else await Task.Yield();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        await PollCycleCoreAsync(cancellationToken);
    }

    private async Task PollCycleCoreAsync(CancellationToken cancellationToken)
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
        await Task.WhenAll(speakers.Select(speaker => PollSpeakerAsync(speaker, epoch, cancellationToken)));
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
            await PollCycleCoreAsync(cancellationToken);
        }
        finally { _refreshGate.Release(); }
    }

    private Task PollSpeakerAsync(SonosSpeaker speaker, long epoch, CancellationToken cancellationToken)
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
            else _ = PollSpeakerCoreAsync(speaker, epoch, generation, key, cancellationToken, owner);
        }
        return existing.WaitAsync(cancellationToken);
    }

    private async Task PollSpeakerCoreAsync(SonosSpeaker speaker, long epoch, long generation, string key, CancellationToken cancellationToken, TaskCompletionSource completion)
    {
        var acquired = false;
        try
        {
            await _parallelism.WaitAsync(cancellationToken);
            acquired = true;
            if (!store.IsConfigured(speaker.IpAddress, epoch) || !store.IsCurrentProbe(speaker.IpAddress, generation)) return;
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
                store.TrySet(new PlaybackSnapshot(ip, speaker.Name, playingTask.Result, volumeTask.Result,
                    stationTask.Result, trackTask.Result, false, now, now, null), generation, epoch);
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
