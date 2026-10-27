using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Services;
using SonosControl.Web.Services.SonosEvents;
using Xunit;

namespace SonosControl.Tests;

public sealed class SonosEventSubscriptionManagerTests
{
    private const string Ip = "10.0.0.1";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
    private readonly SonosEventSubscriptionRegistry _registry = new();
    private readonly Mock<ISonosEventSubscriptionClient> _client = new();
    private readonly SonosSettings _settings = new() { Speakers = [new SonosSpeaker { Name = "Office", IpAddress = Ip, Uuid = "RINCON_C" }] };
    private readonly List<(SonosEventService Service, Uri Callback)> _subscribed = [];
    private int _sidCounter;

    public SonosEventSubscriptionManagerTests()
    {
        _client.Setup(client => client.SubscribeAsync(Ip, It.IsAny<SonosEventService>(), It.IsAny<Uri>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback((string _, SonosEventService service, Uri callback, TimeSpan _, CancellationToken _) => _subscribed.Add((service, callback)))
            .ReturnsAsync(() => new SonosEventSubscription($"uuid:sub-{++_sidCounter}", TimeSpan.FromSeconds(600)));
    }

    private readonly Mock<IMetricsCollector> _metrics = new();

    private SonosEventSubscriptionManager Manager(string? callbackBaseUrl = "http://192.0.2.50:8080", string? publicBaseUrl = null)
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(() => _settings);
        var scopes = new ServiceCollection().AddSingleton(settingsRepo.Object).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new SonosEventSubscriptionManager(scopes, _client.Object, _registry,
            Options.Create(new SonosEventOptions { Enabled = true, CallbackBaseUrl = callbackBaseUrl, SubscriptionTimeoutSeconds = 600 }),
            Options.Create(new YouTubePlaybackOptions { PublicBaseUrl = publicBaseUrl }),
            NullLogger<SonosEventSubscriptionManager>.Instance, _time, _metrics.Object);
    }

    private static SonosCommandException Rejected(HttpStatusCode status) => new(Ip, "AVTransport", "Subscribe", status, null, "rejected");

    [Fact]
    public async Task ReconcileOnce_SubscribesAllServicesWithTokenCallbacks()
    {
        var manager = Manager();

        await manager.ReconcileOnceAsync();

        Assert.Equal([SonosEventService.AVTransport, SonosEventService.RenderingControl, SonosEventService.ZoneGroupTopology], _subscribed.Select(item => item.Service));
        foreach (var (_, callback) in _subscribed)
        {
            Assert.StartsWith("http://192.0.2.50:8080/api/sonos-events/", callback.AbsoluteUri);
            var token = callback.Segments[^1];
            Assert.Equal(32, token.Length);
            Assert.True(_registry.TryGet(token, out var registration));
            Assert.Equal(Ip, registration.SpeakerIp);
            Assert.True(registration.MatchesSid("uuid:sub-1") || registration.MatchesSid("uuid:sub-2") || registration.MatchesSid("uuid:sub-3"));
            Assert.False(registration.MatchesSid("uuid:other"));
        }
        Assert.Equal(Ip, _registry.FindSpeakerIp("uuid:RINCON_C"));
        Assert.True(manager.IsHealthy(Ip));

        await manager.ReconcileOnceAsync();
        Assert.Equal(3, _subscribed.Count);
    }

    [Theory]
    [InlineData(null, "http://192.0.2.60:8080/", "http://192.0.2.60:8080/api/sonos-events/")]
    [InlineData("https://sonos.example/app", "http://192.0.2.60:8080", "https://sonos.example/app/api/sonos-events/")]
    public async Task CallbackBase_PrefersSonosEventsSettingAndKeepsPathPrefix(string? callbackBaseUrl, string publicBaseUrl, string expectedPrefix)
    {
        await Manager(callbackBaseUrl, publicBaseUrl).ReconcileOnceAsync();

        Assert.All(_subscribed, item => Assert.StartsWith(expectedPrefix, item.Callback.AbsoluteUri));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("not a url", null)]
    [InlineData(null, "ftp://192.0.2.60/")]
    public async Task WithoutUsableCallbackBase_NothingIsSubscribed(string? callbackBaseUrl, string? publicBaseUrl)
    {
        var manager = Manager(callbackBaseUrl, publicBaseUrl);

        await manager.ReconcileOnceAsync();

        Assert.Null(manager.CallbackBase);
        Assert.Empty(_subscribed);
    }

    [Fact]
    public async Task Renewal_HappensAtHalfTheGrantedTimeout()
    {
        var manager = Manager();
        await manager.ReconcileOnceAsync();
        _client.Setup(client => client.RenewAsync(Ip, It.IsAny<SonosEventService>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosEventSubscription("uuid:renewed", TimeSpan.FromSeconds(600)));

        _time.Advance(TimeSpan.FromSeconds(299));
        await manager.ReconcileOnceAsync();
        _client.Verify(client => client.RenewAsync(It.IsAny<string>(), It.IsAny<SonosEventService>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);

        _time.Advance(TimeSpan.FromSeconds(1));
        await manager.ReconcileOnceAsync();
        _client.Verify(client => client.RenewAsync(Ip, SonosEventService.AVTransport, "uuid:sub-1", TimeSpan.FromSeconds(600), It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(client => client.RenewAsync(Ip, SonosEventService.RenderingControl, "uuid:sub-2", TimeSpan.FromSeconds(600), It.IsAny<CancellationToken>()), Times.Once);

        // Without further renewals the subscriptions expire and the speaker no longer counts as healthy.
        _time.Advance(TimeSpan.FromSeconds(599));
        Assert.True(manager.IsHealthy(Ip));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(manager.IsHealthy(Ip));
    }

    [Fact]
    public async Task LostSubscription_IsReplacedWithANewToken()
    {
        var manager = Manager();
        await manager.ReconcileOnceAsync();
        var oldTokens = _subscribed.Select(item => item.Callback.Segments[^1]).ToArray();
        _client.Setup(client => client.RenewAsync(Ip, It.IsAny<SonosEventService>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SonosEventSubscription?)null);

        _time.Advance(TimeSpan.FromSeconds(300));
        await manager.ReconcileOnceAsync();

        Assert.Equal(6, _subscribed.Count);
        Assert.All(oldTokens, token => Assert.False(_registry.TryGet(token, out _)));
        Assert.All(_subscribed.Skip(3), item => Assert.True(_registry.TryGet(item.Callback.Segments[^1], out _)));
        Assert.True(manager.IsHealthy(Ip));
    }

    [Fact]
    public async Task FailedSubscriptions_BackOffAndReportTheError()
    {
        _client.Setup(client => client.SubscribeAsync(Ip, It.IsAny<SonosEventService>(), It.IsAny<Uri>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback((string _, SonosEventService service, Uri callback, TimeSpan _, CancellationToken _) => _subscribed.Add((service, callback)))
            .ThrowsAsync(Rejected(HttpStatusCode.NotImplemented));
        var manager = Manager();

        await manager.ReconcileOnceAsync();
        Assert.Equal(3, _subscribed.Count);
        Assert.False(manager.IsHealthy(Ip));
        Assert.Contains("rejected", manager.LastError(Ip));
        Assert.All(_subscribed, item => Assert.False(_registry.TryGet(item.Callback.Segments[^1], out _)));

        _time.Advance(SonosEventSubscriptionManager.MinBackoff - TimeSpan.FromSeconds(1));
        await manager.ReconcileOnceAsync();
        Assert.Equal(3, _subscribed.Count);

        _time.Advance(TimeSpan.FromSeconds(1));
        await manager.ReconcileOnceAsync();
        Assert.Equal(6, _subscribed.Count);

        // The second failure doubles the wait.
        _time.Advance(SonosEventSubscriptionManager.MinBackoff);
        await manager.ReconcileOnceAsync();
        Assert.Equal(6, _subscribed.Count);
        _time.Advance(SonosEventSubscriptionManager.MinBackoff);
        await manager.ReconcileOnceAsync();
        Assert.Equal(9, _subscribed.Count);
    }

    [Fact]
    public async Task RemovedSpeakers_AreUnsubscribed()
    {
        var manager = Manager();
        await manager.ReconcileOnceAsync();
        var tokens = _subscribed.Select(item => item.Callback.Segments[^1]).ToArray();

        _settings.Speakers = [];
        await manager.ReconcileOnceAsync();

        _client.Verify(client => client.UnsubscribeAsync(Ip, SonosEventService.AVTransport, "uuid:sub-1", It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(client => client.UnsubscribeAsync(Ip, SonosEventService.RenderingControl, "uuid:sub-2", It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(client => client.UnsubscribeAsync(Ip, SonosEventService.ZoneGroupTopology, "uuid:sub-3", It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(tokens, token => Assert.False(_registry.TryGet(token, out _)));
        Assert.False(manager.IsHealthy(Ip));
    }

    [Fact]
    public async Task ReconcileOnce_ReportsTheSpeakerSplitAndFailures()
    {
        var manager = Manager();
        await manager.ReconcileOnceAsync();
        _metrics.Verify(metrics => metrics.SetSonosEventSpeakers(3, 1, 0), Times.Once);

        _client.Setup(client => client.RenewAsync(Ip, It.IsAny<SonosEventService>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Rejected(HttpStatusCode.InternalServerError));
        _time.Advance(TimeSpan.FromSeconds(300));
        await manager.ReconcileOnceAsync();

        _metrics.Verify(metrics => metrics.RecordSonosSubscriptionFailure(), Times.Exactly(3));
        _metrics.Verify(metrics => metrics.SetSonosEventSpeakers(0, 0, 1), Times.Once);
    }

    [Fact]
    public async Task GetStatus_IsNullUntilTheManagerRunsAndThenReportsLiveSpeakers()
    {
        var manager = Manager();
        Assert.Null(manager.GetStatus(Ip));
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(client => client.SubscribeAsync(Ip, SonosEventService.RenderingControl, It.IsAny<Uri>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => subscribed.TrySetResult())
            .ReturnsAsync(new SonosEventSubscription("uuid:sub-rc", TimeSpan.FromSeconds(600)));

        await manager.StartAsync(CancellationToken.None);
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var status = manager.GetStatus(Ip);
        await manager.StopAsync(CancellationToken.None);

        Assert.NotNull(status);
        Assert.True(status.Live);
        Assert.Null(status.LastError);
        Assert.Null(manager.GetStatus(Ip));
    }

    [Fact]
    public async Task Stopping_UnsubscribesEverything()
    {
        var manager = Manager();
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(client => client.SubscribeAsync(Ip, SonosEventService.RenderingControl, It.IsAny<Uri>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => subscribed.TrySetResult())
            .ReturnsAsync(new SonosEventSubscription("uuid:sub-rc", TimeSpan.FromSeconds(600)));

        await manager.StartAsync(CancellationToken.None);
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.StopAsync(CancellationToken.None);

        _client.Verify(client => client.UnsubscribeAsync(Ip, SonosEventService.AVTransport, "uuid:sub-1", It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(client => client.UnsubscribeAsync(Ip, SonosEventService.RenderingControl, "uuid:sub-rc", It.IsAny<CancellationToken>()), Times.Once);
    }
}
