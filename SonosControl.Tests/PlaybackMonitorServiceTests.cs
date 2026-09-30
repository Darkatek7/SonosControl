using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class PlaybackMonitorServiceTests
{
    [Fact]
    public async Task LongObservationGap_EndsAtLastSeenPlaybackAndStartsANewSession()
    {
        using var fixture = new MonitorFixture();
        var start = fixture.Clock.Now;
        await fixture.Poll();
        fixture.Clock.Now = start.AddSeconds(15);
        await fixture.Poll(); // This observation is inside the persistence throttle.
        fixture.Clock.Now = start.AddDays(2);
        await fixture.Poll();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(60);
        await fixture.Poll();

        var history = await fixture.History();
        Assert.Equal(2, history.Count);
        Assert.Equal(15, history[0].DurationSeconds);
        Assert.Equal(start.AddSeconds(15).UtcDateTime, history[0].EndTime);
        Assert.Equal(60, history[1].DurationSeconds);
        Assert.Equal(start.AddDays(2).UtcDateTime, history[1].StartTime);
    }

    [Fact]
    public async Task FailedQueryAfterLongGap_PreservesTheLastConfirmedPlayback()
    {
        using var fixture = new MonitorFixture();
        var start = fixture.Clock.Now;
        await fixture.Poll();
        fixture.Clock.Now = start.AddSeconds(15);
        await fixture.Poll();
        fixture.Clock.Now = start.AddHours(8);
        fixture.Connector.Setup(connector => connector.IsPlaying(It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Poll());
        Assert.Equal(15, Assert.Single(await fixture.History()).DurationSeconds);

        fixture.Connector.Setup(connector => connector.IsPlaying(It.IsAny<string>())).ReturnsAsync(true);
        await fixture.Poll();
        Assert.Equal(2, (await fixture.History()).Count);
    }

    [Fact]
    public async Task NormalPlayback_AccumulatesUntilPause()
    {
        using var fixture = new MonitorFixture();
        var start = fixture.Clock.Now;
        await fixture.Poll();
        for (var seconds = 15; seconds <= 60; seconds += 15)
        {
            fixture.Clock.Now = start.AddSeconds(seconds);
            await fixture.Poll();
        }
        fixture.Connector.Setup(connector => connector.IsPlaying(It.IsAny<string>())).ReturnsAsync(false);
        fixture.Clock.Now = start.AddSeconds(75);
        await fixture.Poll();
        Assert.Equal(75, Assert.Single(await fixture.History()).DurationSeconds);
    }

    [Fact]
    public async Task RemovingSpeaker_ClosesAtLastObservationInsteadOfAddingUnobservedTime()
    {
        using var fixture = new MonitorFixture();
        var start = fixture.Clock.Now;
        await fixture.Poll();
        fixture.Clock.Now = start.AddSeconds(15);
        await fixture.Poll();
        fixture.Settings.Speakers.Clear();
        fixture.Clock.Now = start.AddHours(3);
        await fixture.Poll();
        fixture.Settings.Speakers.Add(new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.1" });
        await fixture.Poll();

        var history = await fixture.History();
        Assert.Equal(2, history.Count);
        Assert.Equal(15, history[0].DurationSeconds);
        Assert.Equal(0, history[1].DurationSeconds);
    }

    [Fact]
    public async Task KnownRadioWithSongMetadata_IsRecordedAsRadioAcrossSongChanges()
    {
        using var fixture = new MonitorFixture();
        fixture.Settings.Stations.Add(new() { Name = "Demo Radio", Url = "https://radio.example.invalid/live" });
        fixture.Connector.Setup(connector => connector.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("x-rincon-mp3radio://https://radio.example.invalid/live");
        await fixture.Poll();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(60);
        fixture.Connector.Setup(connector => connector.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "Next song", Artist = "Another artist" });
        await fixture.Poll();

        var history = Assert.Single(await fixture.History());
        Assert.Equal("Station", history.MediaType);
        Assert.Equal("Demo Radio", history.TrackName);
        Assert.Equal(60, history.DurationSeconds);
    }

    [Fact]
    public async Task DuplicateSpeakerConfiguration_IsPolledOnce()
    {
        using var fixture = new MonitorFixture();
        fixture.Settings.Speakers.Add(new SonosSpeaker { Name = "Duplicate", IpAddress = " 10.0.0.1 " });
        await fixture.Poll();
        Assert.Single(await fixture.History());
        fixture.Connector.Verify(connector => connector.IsPlaying("10.0.0.1"), Times.Once);
    }

    [Fact]
    public async Task ChangedStreamUrlWithoutMetadata_StartsANewSession()
    {
        using var fixture = new MonitorFixture();
        fixture.Connector.Setup(connector => connector.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SonosTrackInfo?)null);
        fixture.Connector.Setup(connector => connector.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://first.example.invalid/live");
        await fixture.Poll();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(15);
        fixture.Connector.Setup(connector => connector.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://second.example.invalid/live");
        await fixture.Poll();
        var history = await fixture.History();
        Assert.Equal(2, history.Count);
        Assert.Equal(15, history[0].DurationSeconds);
        Assert.Equal(0, history[1].DurationSeconds);
    }

    private sealed class MonitorFixture : IDisposable
    {
        public MutableClock Clock { get; } = new();
        public SonosSettings Settings { get; } = new()
        {
            Speakers = [new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.1" }]
        };
        public Mock<ISonosConnectorRepo> Connector { get; } = new();
        private readonly ServiceProvider _provider;
        private readonly PlaybackMonitorService _monitor;

        public MonitorFixture()
        {
            Connector.Setup(connector => connector.IsPlaying(It.IsAny<string>())).ReturnsAsync(true);
            Connector.Setup(connector => connector.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SonosTrackInfo { Title = "A song", Artist = "An artist" });
            Connector.Setup(connector => connector.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("spotify:track:demo");
            var settingsRepo = new Mock<ISettingsRepo>();
            settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(Settings);
            var uow = new Mock<IUnitOfWork>();
            uow.SetupGet(unit => unit.ISettingsRepo).Returns(settingsRepo.Object);
            uow.SetupGet(unit => unit.ISonosConnectorRepo).Returns(Connector.Object);
            var databaseName = Guid.NewGuid().ToString();
            _provider = new ServiceCollection()
                .AddSingleton(uow.Object)
                .AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(databaseName))
                .BuildServiceProvider();
            _monitor = new PlaybackMonitorService(_provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<PlaybackMonitorService>.Instance, Mock.Of<IMetricsCollector>(), Clock);
        }

        public Task Poll() => (Task)typeof(PlaybackMonitorService)
            .GetMethod("MonitorPlayback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_monitor, [CancellationToken.None])!;

        public async Task<List<PlaybackHistory>> History()
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PlaybackStats
                .AsNoTracking().OrderBy(playback => playback.Id).ToListAsync();
        }

        public void Dispose()
        {
            _monitor.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
