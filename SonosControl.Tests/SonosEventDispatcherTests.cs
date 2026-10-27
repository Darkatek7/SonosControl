using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Services;
using SonosControl.Web.Services.SonosEvents;
using Xunit;

namespace SonosControl.Tests;

public sealed class SonosEventDispatcherTests
{
    private const string Ip = "10.0.0.1";
    private const string MemberIp = "10.0.0.2";
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Fixture(ISonosConnectorRepo connector)
        {
            var settings = new SonosSettings
            {
                Speakers =
                [
                    new SonosSpeaker { Name = "Office", IpAddress = Ip, Uuid = "RINCON_C" },
                    new SonosSpeaker { Name = "Kitchen", IpAddress = MemberIp, Uuid = "RINCON_M" }
                ]
            };
            var settingsRepo = new Mock<ISettingsRepo>();
            settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
            var services = new ServiceCollection();
            services.AddSingleton(settingsRepo.Object);
            services.AddScoped(_ => connector);
            _provider = services.BuildServiceProvider();
            var scopes = _provider.GetRequiredService<IServiceScopeFactory>();
            Monitor = new PlaybackSnapshotMonitorService(scopes, Store, NullLogger<PlaybackSnapshotMonitorService>.Instance, TimeProvider.System);
            Dispatcher = new SonosEventDispatcher(scopes, Store, Monitor, Registry, NullLogger<SonosEventDispatcher>.Instance, TimeProvider.System);
        }

        public PlaybackSnapshotStore Store { get; } = new();
        public SonosEventSubscriptionRegistry Registry { get; } = new();
        public PlaybackSnapshotMonitorService Monitor { get; }
        public SonosEventDispatcher Dispatcher { get; }

        public Task ApplyAsync(SonosEventRegistration registration, long sequence, string body)
            => Dispatcher.ApplyAsync(new SonosEventNotification(registration, sequence, body, Now));

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private static PlaybackSnapshot Snapshot(string ip, bool playing, int volume, string station, SonosTrackInfo track)
        => new(ip, ip, playing, volume, station, track, true, Now.AddMinutes(-5), Now.AddMinutes(-5), "Playback status is delayed");

    /// <summary>A connector that resolves metadata like the real one but fails on any device request.</summary>
    private static SonosConnectorRepo OfflineConnector()
        => new(new Mock<IHttpClientFactory>(MockBehavior.Strict).Object, Mock.Of<ISettingsRepo>());

    [Fact]
    public async Task RecordedPauseAndPlay_EndsPlayingWithoutPlaceholderOrLostTitle()
    {
        await using var fixture = new Fixture(OfflineConnector());
        fixture.Store.Set(Snapshot(Ip, true, 8, "x-rincon-mp3radio://radiorecord.hostingradio.ru/summerparty96.aacp",
            new SonosTrackInfo { Title = "summerparty96.aacp", StreamContent = "twocolors feat Safri Duo - CYNICAL" }));
        var registration = fixture.Registry.Register(Ip, "RINCON_C", SonosEventService.AVTransport);
        var seen = new List<(bool Playing, string Display)>();
        fixture.Store.Changed += () =>
        {
            var snapshot = fixture.Store.Get(Ip)!;
            seen.Add((snapshot.IsPlaying, snapshot.TrackInfo!.GetDisplayString()));
        };

        await fixture.ApplyAsync(registration, 0, SonosEventFixtures.Era100AvTransportStoppedAfterPause);
        await fixture.ApplyAsync(registration, 1, SonosEventFixtures.Era100AvTransportTransitioningAfterPlay);
        await fixture.ApplyAsync(registration, 2, SonosEventFixtures.Era100AvTransportPlayingWithoutStreamTitle);

        Assert.Equal([false, true, true], seen.Select(item => item.Playing));
        Assert.All(seen, item => Assert.Equal("twocolors feat Safri Duo - CYNICAL", item.Display));
        var result = fixture.Store.Get(Ip)!;
        Assert.False(result.IsStale);
        Assert.Null(result.Error);
        Assert.Equal("x-rincon-mp3radio://radiorecord.hostingradio.ru/summerparty96.aacp", result.CurrentStationUrl);
    }

    [Fact]
    public async Task RecordedInitialEvents_FillVolumeAndStreamTitle()
    {
        await using var fixture = new Fixture(OfflineConnector());
        fixture.Store.Set(Snapshot(Ip, false, 30, "", new SonosTrackInfo()));
        var transport = fixture.Registry.Register(Ip, "RINCON_C", SonosEventService.AVTransport);
        var rendering = fixture.Registry.Register(Ip, "RINCON_C", SonosEventService.RenderingControl);

        await fixture.ApplyAsync(transport, 0, SonosEventFixtures.Play1AvTransportInitial);
        await fixture.ApplyAsync(rendering, 0, SonosEventFixtures.Play1RenderingControlInitial);

        var result = fixture.Store.Get(Ip)!;
        Assert.True(result.IsPlaying);
        Assert.Equal(14, result.Volume);
        Assert.Equal("x-rincon-mp3radio://radiorecord.hostingradio.ru/summerparty96.aacp", result.CurrentStationUrl);
        Assert.Equal("FISHER/LAUREL - Staring At The Sun", result.TrackInfo?.GetDisplayString());
    }

    [Fact]
    public async Task StaleAndRemovedRegistrations_AreIgnored()
    {
        await using var fixture = new Fixture(OfflineConnector());
        fixture.Store.Set(Snapshot(Ip, true, 8, "uri", new SonosTrackInfo { Title = "Song" }));
        var registration = fixture.Registry.Register(Ip, "RINCON_C", SonosEventService.RenderingControl);

        await fixture.ApplyAsync(registration, 0, SonosEventFixtures.Era100RenderingControlVolumeChange);
        await fixture.ApplyAsync(registration, 0, SonosEventFixtures.Play1RenderingControlInitial);
        Assert.Equal(10, fixture.Store.Get(Ip)!.Volume);

        fixture.Registry.Remove(registration);
        await fixture.ApplyAsync(registration, 1, SonosEventFixtures.Play1RenderingControlInitial);
        Assert.Equal(10, fixture.Store.Get(Ip)!.Volume);
    }

    [Theory]
    [InlineData(5, "valid")]
    [InlineData(0, "unreadable")]
    public async Task SequenceGapsAndUnreadableBodies_ProbeTheSpeaker(long sequence, string kind)
    {
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume(Ip, It.IsAny<CancellationToken>())).Callback(() => probed.TrySetResult()).ReturnsAsync(9);
        connector.Setup(repo => repo.IsPlaying(Ip, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        connector.Setup(repo => repo.GetCurrentStationAsync(Ip, It.IsAny<CancellationToken>())).ReturnsAsync("uri");
        connector.Setup(repo => repo.GetTrackInfoAsync(Ip, It.IsAny<CancellationToken>())).ReturnsAsync(new SonosTrackInfo { Title = "Song" });
        await using var fixture = new Fixture(connector.Object);
        fixture.Store.Set(Snapshot(Ip, true, 8, "uri", new SonosTrackInfo { Title = "Song" }));
        var registration = fixture.Registry.Register(Ip, "RINCON_C", SonosEventService.RenderingControl);

        await fixture.ApplyAsync(registration, sequence, kind == "valid" ? SonosEventFixtures.Play1RenderingControlInitial : "<broken");

        await probed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GroupMember_FollowsItsCoordinatorButKeepsItsVolumeAndSource()
    {
        await using var fixture = new Fixture(OfflineConnector());
        fixture.Store.Set(Snapshot(Ip, true, 30, "x-rincon-mp3radio://radio.test/live",
            new SonosTrackInfo { Title = "live.aacp", StreamContent = "Band - Song" }));
        fixture.Store.Set(Snapshot(MemberIp, false, 5, "", new SonosTrackInfo()));
        var coordinator = fixture.Registry.Register(Ip, "RINCON_C", SonosEventService.AVTransport);
        var member = fixture.Registry.Register(MemberIp, "uuid:RINCON_M", SonosEventService.AVTransport);

        await fixture.ApplyAsync(member, 0, SonosEventParserTests.AvTransportBody(
            "<TransportState val=\"STOPPED\"/><AVTransportURI val=\"x-rincon:RINCON_C\"/>"));

        var followed = fixture.Store.Get(MemberIp)!;
        Assert.True(followed.IsPlaying);
        Assert.Equal("Band - Song", followed.TrackInfo?.GetDisplayString());
        Assert.Equal(5, followed.Volume);
        Assert.Equal("x-rincon:RINCON_C", followed.CurrentStationUrl);

        await fixture.ApplyAsync(coordinator, 0, SonosEventParserTests.AvTransportBody("<TransportState val=\"STOPPED\"/>"));

        Assert.False(fixture.Store.Get(Ip)!.IsPlaying);
        Assert.False(fixture.Store.Get(MemberIp)!.IsPlaying);
    }

    [Fact]
    public async Task LeavingAGroup_StopsFollowingTheCoordinator()
    {
        await using var fixture = new Fixture(OfflineConnector());
        fixture.Store.Set(Snapshot(Ip, true, 30, "uri", new SonosTrackInfo { Title = "Song" }));
        fixture.Store.Set(Snapshot(MemberIp, false, 5, "", new SonosTrackInfo()));
        var coordinator = fixture.Registry.Register(Ip, "RINCON_C", SonosEventService.AVTransport);
        var member = fixture.Registry.Register(MemberIp, "RINCON_M", SonosEventService.AVTransport);
        await fixture.ApplyAsync(member, 0, SonosEventParserTests.AvTransportBody("<AVTransportURI val=\"x-rincon:RINCON_C\"/>"));

        await fixture.ApplyAsync(member, 1, SonosEventParserTests.AvTransportBody(
            "<TransportState val=\"STOPPED\"/><AVTransportURI val=\"x-rincon-mp3radio://radio.test/other\"/>"));
        await fixture.ApplyAsync(coordinator, 0, SonosEventParserTests.AvTransportBody("<TransportState val=\"PLAYING\"/>"));

        Assert.False(fixture.Store.Get(MemberIp)!.IsPlaying);
    }

    [Fact]
    public async Task Enqueue_RejectsWhenTheQueueIsFull()
    {
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable));
        await using var fixture = new Fixture(connector.Object);
        var registration = fixture.Registry.Register(Ip, "RINCON_C", SonosEventService.RenderingControl);
        var notification = new SonosEventNotification(registration, 0, "<x/>", Now);

        for (var index = 0; index < 256; index++) Assert.True(fixture.Dispatcher.Enqueue(notification));

        Assert.False(fixture.Dispatcher.Enqueue(notification));
        Assert.Equal(256, fixture.Dispatcher.PendingCount);
    }
}
