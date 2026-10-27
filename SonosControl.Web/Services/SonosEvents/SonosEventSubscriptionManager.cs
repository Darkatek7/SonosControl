using Microsoft.Extensions.Options;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;

namespace SonosControl.Web.Services.SonosEvents;

/// <summary>Keeps one AVTransport and one RenderingControl subscription alive per configured speaker.</summary>
public sealed class SonosEventSubscriptionManager(
    IServiceScopeFactory scopeFactory,
    ISonosEventSubscriptionClient client,
    SonosEventSubscriptionRegistry registry,
    IOptions<SonosEventOptions> options,
    IOptions<YouTubePlaybackOptions> playbackOptions,
    ILogger<SonosEventSubscriptionManager> logger,
    TimeProvider timeProvider) : BackgroundService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly SonosEventService[] Services = [SonosEventService.AVTransport, SonosEventService.RenderingControl];
    private readonly object _sync = new();
    private readonly Dictionary<(string Ip, SonosEventService Service), Slot> _slots = new();

    private sealed class Slot
    {
        public SonosEventRegistration? Active;
        public string? Sid;
        public DateTimeOffset RenewAt;
        public DateTimeOffset ExpiresAt;
        public DateTimeOffset RetryAt;
        public TimeSpan Backoff;
        public string? LastError;
    }

    private TimeSpan SubscriptionTimeout => TimeSpan.FromSeconds(Math.Clamp(options.Value.SubscriptionTimeoutSeconds, 60, 86400));

    /// <summary>The callback base URL, or null when neither SonosEvents:CallbackBaseUrl nor Playback:PublicBaseUrl is usable.</summary>
    public Uri? CallbackBase
    {
        get
        {
            var value = options.Value.CallbackBaseUrl;
            if (string.IsNullOrWhiteSpace(value)) value = playbackOptions.Value.PublicBaseUrl;
            return Uri.TryCreate(value?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
        }
    }

    /// <summary>True while both services of the speaker have an unexpired subscription.</summary>
    public bool IsHealthy(string speakerIp)
    {
        var now = timeProvider.GetUtcNow();
        lock (_sync)
            return Services.All(service => _slots.TryGetValue((speakerIp, service), out var slot) && slot.Active is not null && now < slot.ExpiresAt);
    }

    public string? LastError(string speakerIp)
    {
        lock (_sync)
            return Services.Select(service => _slots.TryGetValue((speakerIp, service), out var slot) ? slot.LastError : null)
                .FirstOrDefault(error => error is not null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (CallbackBase is null)
        {
            logger.LogInformation("Sonos events are enabled but no callback URL is configured; playback keeps polling. Set SonosEvents:CallbackBaseUrl or Playback:PublicBaseUrl.");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileOnceAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "Sonos event subscription cycle failed."); }
                try { await Task.Delay(CheckInterval, timeProvider, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            using var timeout = new CancellationTokenSource(ShutdownTimeout, timeProvider);
            await UnsubscribeAllAsync(timeout.Token);
        }
    }

    /// <summary>Subscribes new speakers, renews due subscriptions and ends those of removed speakers.</summary>
    public async Task ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        if (CallbackBase is not { } callbackBase) return;
        SonosSettings? settings;
        using (var scope = scopeFactory.CreateScope())
            settings = await scope.ServiceProvider.GetRequiredService<ISettingsRepo>().GetSettings();
        var speakers = (settings?.Speakers ?? [])
            .Where(speaker => !string.IsNullOrWhiteSpace(speaker.IpAddress))
            .DistinctBy(speaker => speaker.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(speaker => speaker.IpAddress, StringComparer.OrdinalIgnoreCase);

        List<((string Ip, SonosEventService Service) Key, Slot Slot)> removed;
        lock (_sync)
        {
            removed = _slots.Where(pair => !speakers.ContainsKey(pair.Key.Ip)).Select(pair => (pair.Key, pair.Value)).ToList();
            foreach (var (key, _) in removed) _slots.Remove(key);
        }
        foreach (var (key, slot) in removed) await EndAsync(key, slot, cancellationToken);

        foreach (var (speaker, service) in speakers.Values.SelectMany(speaker => Services.Select(service => (speaker, service))))
        {
            var key = (speaker.IpAddress, service);
            Slot slot;
            lock (_sync)
            {
                if (!_slots.TryGetValue(key, out slot!)) _slots[key] = slot = new Slot();
            }
            var now = timeProvider.GetUtcNow();
            if (slot.Active is null)
            {
                if (now >= slot.RetryAt) await StartAsync(key, speaker.Uuid, slot, callbackBase, cancellationToken);
            }
            else if (now >= slot.RenewAt)
            {
                await RenewAsync(key, speaker.Uuid, slot, callbackBase, cancellationToken);
            }
        }
    }

    private async Task StartAsync((string Ip, SonosEventService Service) key, string? uuid, Slot slot, Uri callbackBase, CancellationToken cancellationToken)
    {
        var registration = registry.Register(key.Ip, uuid, key.Service);
        try
        {
            var grant = await client.SubscribeAsync(key.Ip, key.Service, new Uri(callbackBase, $"api/sonos-events/{registration.Token}"),
                SubscriptionTimeout, cancellationToken);
            registration.SetSid(grant.Sid);
            var now = timeProvider.GetUtcNow();
            lock (_sync)
            {
                slot.Active = registration;
                slot.Sid = grant.Sid;
                slot.RenewAt = now + grant.Timeout / 2;
                slot.ExpiresAt = now + grant.Timeout;
                slot.Backoff = TimeSpan.Zero;
                slot.LastError = null;
            }
        }
        catch (SonosCommandException ex)
        {
            registry.Remove(registration);
            Fail(slot, ex.Message);
        }
    }

    private async Task RenewAsync((string Ip, SonosEventService Service) key, string? uuid, Slot slot, Uri callbackBase, CancellationToken cancellationToken)
    {
        try
        {
            var grant = await client.RenewAsync(key.Ip, key.Service, slot.Sid!, SubscriptionTimeout, cancellationToken);
            if (grant is not null)
            {
                var now = timeProvider.GetUtcNow();
                lock (_sync)
                {
                    slot.RenewAt = now + grant.Timeout / 2;
                    slot.ExpiresAt = now + grant.Timeout;
                }
                return;
            }
            // The speaker forgot the subscription, for example after a reboot: start over with a new token.
            Drop(slot);
            await StartAsync(key, uuid, slot, callbackBase, cancellationToken);
        }
        catch (SonosCommandException ex)
        {
            Drop(slot);
            Fail(slot, ex.Message);
        }
    }

    private async Task EndAsync((string Ip, SonosEventService Service) key, Slot slot, CancellationToken cancellationToken)
    {
        var sid = slot.Sid;
        Drop(slot);
        if (sid is null) return;
        try { await client.UnsubscribeAsync(key.Ip, key.Service, sid, cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private async Task UnsubscribeAllAsync(CancellationToken cancellationToken)
    {
        List<((string Ip, SonosEventService Service) Key, Slot Slot)> all;
        lock (_sync)
        {
            all = _slots.Select(pair => (pair.Key, pair.Value)).ToList();
            _slots.Clear();
        }
        await Task.WhenAll(all.Select(item => EndAsync(item.Key, item.Slot, cancellationToken)));
    }

    private void Drop(Slot slot)
    {
        lock (_sync)
        {
            if (slot.Active is not null) registry.Remove(slot.Active);
            slot.Active = null;
            slot.Sid = null;
        }
    }

    private void Fail(Slot slot, string error)
    {
        lock (_sync)
        {
            slot.Backoff = slot.Backoff == TimeSpan.Zero ? MinBackoff : TimeSpan.FromTicks(Math.Min(slot.Backoff.Ticks * 2, MaxBackoff.Ticks));
            slot.RetryAt = timeProvider.GetUtcNow() + slot.Backoff;
            slot.LastError = error;
        }
    }
}
