using System.Collections.Concurrent;
using System.Threading.Channels;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;

namespace SonosControl.Web.Services.SonosEvents;

public sealed record SonosEventNotification(SonosEventRegistration Registration, long Sequence, string Body, DateTimeOffset ReceivedUtc);

/// <summary>
/// Applies NOTIFY bodies to the playback snapshot store off the request path, so a slow store subscriber
/// never delays the speaker. Anything it cannot apply is handed to a probe of that speaker.
/// </summary>
public sealed class SonosEventDispatcher(
    IServiceScopeFactory scopeFactory,
    PlaybackSnapshotStore store,
    PlaybackSnapshotMonitorService monitor,
    SonosEventSubscriptionRegistry registry,
    ILogger<SonosEventDispatcher> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly Channel<SonosEventNotification> _queue = Channel.CreateBounded<SonosEventNotification>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    // Speaker IP -> UUID of the coordinator it follows, taken from its x-rincon: transport URI.
    private readonly ConcurrentDictionary<string, string> _coordinators = new(StringComparer.OrdinalIgnoreCase);

    internal int PendingCount => _queue.Reader.Count;

    public bool Enqueue(SonosEventNotification notification)
    {
        if (_queue.Writer.TryWrite(notification)) return true;
        logger.LogWarning("Sonos event queue is full; dropping an event from {SpeakerIp}.", notification.Registration.SpeakerIp);
        RequestProbe(notification.Registration.SpeakerIp);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var notification in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try { await ApplyAsync(notification, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not apply a Sonos event from {SpeakerIp}.", notification.Registration.SpeakerIp);
                    RequestProbe(notification.Registration.SpeakerIp);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task ApplyAsync(SonosEventNotification notification, CancellationToken cancellationToken = default)
    {
        var registration = notification.Registration;
        var ip = registration.SpeakerIp;
        if (!registry.IsActive(registration)) return;
        var sequence = registration.Observe(notification.Sequence, notification.ReceivedUtc);
        if (sequence == SonosEventSequence.Stale) return;
        if (sequence == SonosEventSequence.Gap) RequestProbe(ip);

        var playbackEvent = SonosEventParser.Parse(registration.Service, notification.Body);
        if (playbackEvent is null)
        {
            logger.LogDebug("Ignoring an unreadable {Service} event from {SpeakerIp}.", registration.Service, ip);
            RequestProbe(ip);
            return;
        }
        if (!playbackEvent.HasValues) return;

        var track = await ResolveTrackAsync(ip, playbackEvent, cancellationToken);
        TrackCoordinator(ip, playbackEvent.TransportUri);
        var coordinator = CoordinatorSnapshot(ip);
        var now = timeProvider.GetUtcNow();
        if (!store.TryApplyEvent(ip, current =>
            {
                var merged = PlaybackSnapshotMerge.ApplyEvent(current, playbackEvent, track);
                return coordinator is null ? merged : PlaybackSnapshotMerge.FollowCoordinator(merged, coordinator);
            }, now))
        {
            RequestProbe(ip);
            return;
        }

        if (registration.Service == SonosEventService.AVTransport && registration.SpeakerUuid is { } uuid
            && store.Get(ip) is { } leader)
        {
            foreach (var member in _coordinators.Where(pair => string.Equals(pair.Value, uuid, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key))
                store.TryApplyEvent(member, current => PlaybackSnapshotMerge.FollowCoordinator(current, leader), now);
        }
    }

    private async Task<SonosTrackInfo?> ResolveTrackAsync(string ip, SonosPlaybackEvent playbackEvent, CancellationToken cancellationToken)
    {
        if (playbackEvent.TrackMetadata is null) return null;
        try
        {
            using var scope = scopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISonosConnectorRepo>()
                .ResolveTrackInfoAsync(ip, playbackEvent.TrackMetadata, playbackEvent.TrackUri, playbackEvent.TrackNumber, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Keep the transport state from this event; the probe fills in the track.
            logger.LogDebug(ex, "Could not resolve track metadata from a {SpeakerIp} event.", ip);
            RequestProbe(ip);
            return null;
        }
    }

    private void TrackCoordinator(string ip, string? transportUri)
    {
        if (transportUri is null) return;
        if (PlaybackSnapshotMerge.CoordinatorUuid(transportUri) is { } uuid)
            _coordinators[ip] = uuid;
        else
            _coordinators.TryRemove(ip, out _);
    }

    private PlaybackSnapshot? CoordinatorSnapshot(string ip)
    {
        if (!_coordinators.TryGetValue(ip, out var uuid) || registry.FindSpeakerIp(uuid) is not { } coordinatorIp
            || string.Equals(coordinatorIp, ip, StringComparison.OrdinalIgnoreCase)) return null;
        return store.Get(coordinatorIp);
    }

    private void RequestProbe(string ip)
    {
        _ = ProbeAsync();

        async Task ProbeAsync()
        {
            try { await monitor.ProbeSpeakerAsync(ip); }
            catch (Exception ex) { logger.LogDebug(ex, "Playback probe after a Sonos event failed for {SpeakerIp}.", ip); }
        }
    }
}
