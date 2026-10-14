using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Pages.Index.Components;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class NowPlayingHeroTests
{
    [Fact]
    public async Task Hero_RendersAlbumArtLiveStateAndAccessibleControls()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connectorRepo = ConfigureServices(
            ctx,
            isPlaying: true,
            albumArtUrl: "https://images.example.test/midnight-city.jpg");
        await ctx.Services.GetRequiredService<PlaybackUiStateService>().InitializeAsync();

        var cut = ctx.Render<NowPlayingHero>();

        Assert.Contains("np-hero--has-art", cut.Find("[data-qa='now-playing-hero']").ClassList);
        Assert.Single(cut.FindAll(".np-hero img"));
        Assert.All(cut.FindAll(".np-hero img"), image => Assert.Equal(string.Empty, image.GetAttribute("alt")));
        Assert.Contains("Midnight City", cut.Find("#now-playing-hero-title").TextContent);
        Assert.Contains("Night Drive", cut.Markup);
        Assert.Contains("Office", cut.Find("#home-now-playing-room").TextContent);
        Assert.Empty(cut.FindAll(".np-hero__eq"));
        Assert.Equal("Pause playback", cut.Find("[data-qa='home-player-toggle']").GetAttribute("aria-label"));
        Assert.Equal("Refresh playback state", cut.Find("[data-qa='home-player-refresh']").GetAttribute("aria-label"));
        Assert.Equal("Next track", cut.Find("[data-qa='home-player-next']").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[data-qa='home-group-speakers']"));

        cut.Find("[data-qa='home-player-next']").Click();
        cut.WaitForAssertion(() => connectorRepo.Verify(repo => repo.NextTrack("10.0.0.1"), Times.Once));
    }

    [Fact]
    public async Task Hero_UsesDecorativeFallbackWhenArtworkIsUnavailable()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureServices(ctx, isPlaying: false, albumArtUrl: null);
        await ctx.Services.GetRequiredService<PlaybackUiStateService>().InitializeAsync();

        var cut = ctx.Render<NowPlayingHero>();

        Assert.Contains("np-hero--no-art", cut.Find("[data-qa='now-playing-hero']").ClassList);
        Assert.Empty(cut.FindAll(".np-hero img"));
        Assert.Equal("true", cut.Find(".np-hero__art").GetAttribute("aria-hidden"));
        Assert.Single(cut.FindAll(".np-hero__art-fallback"));
        Assert.Single(cut.FindAll(".np-hero__record"));
        Assert.Equal("SC", cut.Find(".np-hero__record-brand").TextContent);
        Assert.Single(cut.FindAll(".np-hero__record-imprint svg"));
        Assert.DoesNotContain("is-spinning", cut.Find(".np-hero__record").ClassList);
        Assert.Empty(cut.FindAll(".np-hero__eq"));
        Assert.Contains("Paused", cut.Find(".np-hero__playback-state").TextContent);
        Assert.Equal("Start playback", cut.Find("[data-qa='home-player-toggle']").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Hero_SpinsFallbackWhilePlaybackIsActive()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureServices(ctx, isPlaying: true, albumArtUrl: null);
        await ctx.Services.GetRequiredService<PlaybackUiStateService>().InitializeAsync();

        var cut = ctx.Render<NowPlayingHero>();

        Assert.Contains("is-spinning", cut.Find(".np-hero__record").ClassList);
        Assert.Equal("Pause playback", cut.Find("[data-qa='home-player-toggle']").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Hero_UpdatesFromSharedSnapshotWithoutAnotherBrowserRefresh()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureServices(ctx, isPlaying: false, albumArtUrl: null);
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        var cut = ctx.Render<NowPlayingHero>();

        ctx.Services.GetRequiredService<PlaybackSnapshotStore>().Set(new PlaybackSnapshot(
            "10.0.0.1", "Office", true, 42, "https://radio.example.test/night-drive",
            new SonosTrackInfo { Title = "New track", Artist = "New artist" }, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("New track", cut.Find("#now-playing-hero-title").TextContent);
            Assert.Contains("is-spinning", cut.Find(".np-hero__record").ClassList);
            Assert.Equal("Pause playback", cut.Find("[data-qa='home-player-toggle']").GetAttribute("aria-label"));
        });
    }

    [Fact]
    public async Task LateResponseFromPreviousRoomCannotReplaceNewRoomState()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connector = ConfigureServices(ctx, isPlaying: false, albumArtUrl: null, includeSecondSpeaker: true);
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();

        var oldRoomResponse = new TaskCompletionSource<SonosTrackInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>())).Returns(oldRoomResponse.Task);
        var oldRefresh = state.RefreshAsync();
        var roomChange = state.SetActiveSpeakerAsync("10.0.0.2");
        oldRoomResponse.SetResult(new SonosTrackInfo { Title = "Late Office result" });
        await Task.WhenAll(oldRefresh, roomChange);

        Assert.Equal("10.0.0.2", state.ActiveSpeakerIp);
        Assert.Contains("Kitchen Track", state.CurrentTrack);
    }

    [Fact]
    public async Task SwitchingToUnprobedRoomClearsPreviousTrackBeforeEmptyResponseCompletes()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connector = ConfigureServices(ctx, true, "https://art.test/office.jpg", includeSecondSpeaker: true);
        ctx.Services.RemoveAll<PlaybackSnapshotMonitorService>();
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        var officeArt = state.CurrentTrackArtUrl;
        Assert.NotNull(officeArt);
        Assert.Null(ctx.Services.GetRequiredService<PlaybackSnapshotStore>().Get("10.0.0.2"));

        var stationResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.Setup(repo => repo.GetCurrentStationAsync("10.0.0.2", It.IsAny<CancellationToken>())).Returns(stationResponse.Task);
        connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.2", It.IsAny<CancellationToken>())).ReturnsAsync((SonosTrackInfo?)null);
        var switching = state.SetActiveSpeakerAsync("10.0.0.2");

        Assert.Equal("No metadata available", state.CurrentTrack);
        Assert.Equal("No source", state.CurrentStationDisplay);
        Assert.Null(state.CurrentTrackArtUrl);
        Assert.Null(state.LastSuccessfulRefreshUtc);
        stationResponse.SetResult(string.Empty);
        await switching;

        Assert.Equal("Kitchen", state.ActiveSpeakerName);
        Assert.Equal("No metadata available", state.CurrentTrack);
        Assert.Equal("No source", state.CurrentStationDisplay);
        Assert.Null(state.CurrentTrackArtUrl);
        Assert.NotEqual(officeArt, state.CurrentTrackArtUrl);
    }

    [Fact]
    public async Task DisposedPlaybackUiStateUnsubscribesFromSharedSnapshots()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureServices(ctx, false, null);
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        var notifications = 0;
        state.StateChanged += () => notifications++;
        state.Dispose();
        var afterDispose = notifications;

        ctx.Services.GetRequiredService<PlaybackSnapshotStore>().Set(new PlaybackSnapshot(
            "10.0.0.1", "Office", true, 40, "uri", new SonosTrackInfo { Title = "External" }, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        Assert.Equal(afterDispose, notifications);
    }

    [Fact]
    public async Task LateManualPlaybackCompletionCannotOverwriteNewlySelectedRoom()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connector = ConfigureServices(ctx, isPlaying: false, albumArtUrl: null, includeSecondSpeaker: true);
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.Setup(repo => repo.StartPlaying("10.0.0.1", It.IsAny<CancellationToken>()))
            .Returns(async () => { startGate.TrySetResult(); await start.Task; });

        var command = state.TogglePlaybackAsync();
        await startGate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await state.SetActiveSpeakerAsync("10.0.0.2");
        var kitchenTrack = state.CurrentTrack;
        start.SetResult();
        await command;

        Assert.Equal("10.0.0.2", state.ActiveSpeakerIp);
        Assert.Equal(kitchenTrack, state.CurrentTrack);
        Assert.False(state.IsPlaying);
    }

    [Fact]
    public async Task SuccessfulPlayWithFailedStatusProbeRemainsStale()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connector = ConfigureServices(ctx, isPlaying: false, albumArtUrl: null);
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));

        await state.TogglePlaybackAsync();

        Assert.True(state.IsStale);
        Assert.Equal("Playback status unavailable", ctx.Services.GetRequiredService<PlaybackSnapshotStore>().Get("10.0.0.1")?.Error);
    }

    [Fact]
    public async Task SuccessfulVolumeWriteWithFailedStatusProbeRemainsStale()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connector = ConfigureServices(ctx, isPlaying: false, albumArtUrl: null);
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));

        await state.SetVolumeAsync(46);

        Assert.True(state.IsStale);
        Assert.Equal("Playback status unavailable", ctx.Services.GetRequiredService<PlaybackSnapshotStore>().Get("10.0.0.1")?.Error);
    }

    [Fact]
    public async Task BrokenArtworkFallsBackAcrossPlayers_AndDoesNotRetryTheSameUrlOnRefresh()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connector = ConfigureServices(ctx, true, "http://speaker/getaa?missing");
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        var cut = ctx.Render<NowPlayingHero>();
        cut.Find(".np-hero img").TriggerEvent("onerror", EventArgs.Empty);
        cut.WaitForAssertion(() => Assert.Contains("is-spinning", cut.Find(".np-hero__record").ClassList));
        Assert.Null(state.CurrentTrackArtUrl);
        await state.RefreshAsync();
        Assert.Null(state.CurrentTrackArtUrl);
        connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "New", AlbumArtUri = "http://speaker/new-art" });
        await state.RefreshAsync();
        Assert.Equal("http://speaker/new-art", state.CurrentTrackArtUrl);
        state.ReportArtworkFailure("http://speaker/getaa?missing");
        Assert.Equal("http://speaker/new-art", state.CurrentTrackArtUrl);
    }

    private static Mock<ISonosConnectorRepo> ConfigureServices(
        BunitContext ctx,
        bool isPlaying,
        string? albumArtUrl,
        bool includeSecondSpeaker = false)
    {
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        ctx.Services.AddSingleton(new ApplicationDbContext(options));

        var settings = new SonosSettings
        {
            IP_Adress = "10.0.0.1",
            Volume = 24,
            MaxVolume = 80,
            Stations =
            [
                new TuneInStation { Name = "Night Drive", Url = "https://radio.example.test/night-drive" }
            ],
            Speakers = includeSecondSpeaker
                ? [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }, new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.2" }]
                : [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }]
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.UpdateSettingsAsync(It.IsAny<Action<SonosSettings>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action<SonosSettings> update, CancellationToken token) => { update(settings); return settings; });
        settingsRepo.Setup(repo => repo.SaveChangesAsync(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(repo => repo.GetVolume(It.IsAny<string>())).ReturnsAsync(settings.Volume);
        connectorRepo.Setup(repo => repo.IsPlaying(It.IsAny<string>())).ReturnsAsync(isPlaying);
        connectorRepo.Setup(repo => repo.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings.Stations[0].Url);
        connectorRepo.Setup(repo => repo.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string ip, CancellationToken _) => new SonosTrackInfo
            {
                Title = ip == "10.0.0.1" ? "Midnight City" : "Kitchen Track",
                Artist = ip == "10.0.0.1" ? "M83" : "Kitchen Artist",
                AlbumArtUri = albumArtUrl
            });
        connectorRepo.Setup(repo => repo.NextTrack("10.0.0.1")).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.StartPlaying("10.0.0.1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.PausePlaying("10.0.0.1")).Returns(Task.CompletedTask);

        RepositoryTestServices.Register(ctx.Services, settingsRepo.Object, connectorRepo.Object, Mock.Of<IHolidayRepo>());
        ctx.Services.AddSingleton(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddScoped<PlaybackUiStateService>();

        return connectorRepo;
    }
}
