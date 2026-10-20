using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class GlobalPlayerBarTests
{
    [Fact]
    public async Task SleepTimer_PausesOriginalRoomAfterRoomSelectionChanges()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var connector = ConfigureServices(ctx);
        var callbacks = ConfigureTimerClock(ctx);
        var cut = ctx.Render<GlobalPlayerBar>();
        cut.Find(".player-surface__open").Click();
        cut.Find(".player-sheet__timer .input-group button").Click();

        await cut.InvokeAsync(() => ctx.Services.GetRequiredService<PlaybackUiStateService>().SetActiveSpeakerAsync("10.0.0.2"));
        Assert.Contains("Office pauses at", cut.Find(".player-sheet__timer").TextContent);
        await cut.InvokeAsync(callbacks[0]);

        cut.WaitForAssertion(() =>
        {
            connector.Verify(repo => repo.PausePlaying("10.0.0.1"), Times.Once);
            connector.Verify(repo => repo.PausePlaying("10.0.0.2"), Times.Never);
            Assert.Contains("Not running", cut.Find(".player-sheet__timer").TextContent);
        });
    }

    [Fact]
    public async Task SleepTimer_RestartKeepsNewRoomAndIgnoresOldTimer()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var connector = ConfigureServices(ctx);
        var callbacks = ConfigureTimerClock(ctx);
        var cut = ctx.Render<GlobalPlayerBar>();
        cut.Find(".player-surface__open").Click();
        cut.Find(".player-sheet__timer .input-group button").Click();
        await cut.InvokeAsync(() => ctx.Services.GetRequiredService<PlaybackUiStateService>().SetActiveSpeakerAsync("10.0.0.2"));
        cut.Find(".player-sheet__timer .input-group button").Click();
        await cut.InvokeAsync(callbacks[0]);

        Assert.Contains("Kitchen pauses at", cut.Find(".player-sheet__timer").TextContent);
        connector.Verify(repo => repo.PausePlaying(It.IsAny<string>()), Times.Never);
        await cut.InvokeAsync(callbacks[1]);
        cut.WaitForAssertion(() =>
        {
            connector.Verify(repo => repo.PausePlaying("10.0.0.2"), Times.Once);
            Assert.Contains("Not running", cut.Find(".player-sheet__timer").TextContent);
        });
    }

    [Fact]
    public async Task SleepTimer_IsSharedAcrossPlayerComponentsAndCanBeCancelledFromEither()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ConfigureServices(ctx);
        ConfigureTimerClock(ctx);
        var first = ctx.Render<GlobalPlayerBar>();
        var second = ctx.Render<GlobalPlayerBar>();
        first.Find(".player-surface__open").Click();
        first.Find(".player-sheet__timer .input-group button").Click();
        second.Find(".player-surface__open").Click();

        Assert.Contains("Office pauses at", second.Find(".player-sheet__timer").TextContent);
        second.Find(".player-sheet__timer .btn-outline-danger").Click();
        Assert.Contains("Not running", first.Find(".player-sheet__timer").TextContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SleepTimer_CancelStopsTimerButComponentDisposalDoesNot(bool dispose)
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var connector = ConfigureServices(ctx);
        var callbacks = ConfigureTimerClock(ctx);
        var cut = ctx.Render<GlobalPlayerBar>();
        cut.Find(".player-surface__open").Click();
        cut.Find(".player-sheet__timer .input-group button").Click();
        if (dispose)
        {
            var timer = ctx.Services.GetRequiredService<SleepTimerService>();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timer.Changed += () => { if (timer.GetSnapshot()?.IsRunning == false) completed.TrySetResult(); };
            cut.Dispose();
            Assert.True(timer.GetSnapshot()?.IsRunning);
            callbacks[0]();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            connector.Verify(repo => repo.PausePlaying("10.0.0.1"), Times.Once);
            Assert.False(timer.GetSnapshot()?.IsRunning);
        }
        else
        {
            cut.Find(".player-sheet__timer .btn-outline-danger").Click();
            callbacks[0]();
            await Task.Yield();
            connector.Verify(repo => repo.PausePlaying(It.IsAny<string>()), Times.Never);
        }
    }

    [Fact]
    public async Task SleepTimer_FailureShowsActionableFeedback()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var connector = ConfigureServices(ctx);
        connector.Setup(repo => repo.PausePlaying(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("Offline"));
        var callbacks = ConfigureTimerClock(ctx);
        var cut = ctx.Render<GlobalPlayerBar>();
        cut.Find(".player-surface__open").Click();
        cut.Find(".player-sheet__timer .input-group button").Click();
        await cut.InvokeAsync(callbacks[0]);

        cut.WaitForAssertion(() => Assert.Contains("Check the speaker connection", cut.Find(".player-sheet__timer [role='alert']").TextContent));
    }

    [Fact]
    public async Task GroupFailureKeepsPlayerAvailableAndShowsFeedback()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var connector = ConfigureServices(ctx);
        connector.Setup(repo => repo.CreateGroup(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("SOAP fault"));
        var cut = ctx.Render<GlobalPlayerBar>();
        cut.Find(".player-surface__open").Click();
        cut.FindAll(".player-sheet__actions button").Single(button => button.TextContent.Contains("Group all")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Check the connection", cut.Find(".player-sheet [role='alert']").TextContent));
        Assert.Single(cut.FindAll(".global-player-bar"));
    }

    private static List<Action> ConfigureTimerClock(BunitContext ctx)
    {
        var callbacks = new List<Action>();
        var clock = new Mock<TimeProvider>();
        clock.Setup(provider => provider.GetUtcNow()).Returns(new DateTimeOffset(2026, 9, 30, 20, 0, 0, TimeSpan.Zero));
        clock.Setup(provider => provider.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object?>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
        .Callback<TimerCallback, object?, TimeSpan, TimeSpan>((callback, state, due, _) =>
        {
            if (due > TimeSpan.FromSeconds(10)) callbacks.Add(() => callback(state));
        })
            .Returns(() => Mock.Of<ITimer>());
        ctx.Services.AddSingleton(clock.Object);
        return callbacks;
    }

    [Fact]
    public async Task GlobalPlayerBar_NextButton_UsesCentralPlaybackState()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connectorRepo = ConfigureServices(ctx);

        var cut = ctx.Render<GlobalPlayerBar>();

        cut.WaitForAssertion(() =>
        {
            var nextButton = cut.Find("[data-qa='global-player-next']");
            Assert.Null(nextButton.GetAttribute("disabled"));
        });

        cut.Find("[data-qa='global-player-next']").Click();

        cut.WaitForAssertion(() =>
            connectorRepo.Verify(repo => repo.NextTrack("10.0.0.1"), Times.Once));
    }

    [Fact]
    public async Task GlobalPlayerBar_SyncButton_IsEnabledAfterInitialRefresh()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connectorRepo = ConfigureServices(ctx);

        var cut = ctx.Render<GlobalPlayerBar>();

        cut.WaitForAssertion(() =>
        {
            var syncButton = cut.Find("[data-qa='global-player-sync']");
            Assert.Null(syncButton.GetAttribute("disabled"));
        });

        cut.Find("[data-qa='global-player-sync']").Click();

        cut.WaitForAssertion(() =>
        {
            connectorRepo.Verify(
                repo => repo.SetTuneInStationAsync("10.0.0.2", "http://stream.example/live", It.IsAny<CancellationToken>()),
                Times.Once);
            connectorRepo.Verify(repo => repo.StartPlaying("10.0.0.2", It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    [Fact]
    public void GlobalPlayerBar_ResolvesSavedStationNameFromCurrentUri()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureServices(
            ctx,
            stations:
            [
                new TuneInStation
                {
                    Name = "Breakz Radio",
                    Url = "https://breakz-2012-high.rautemusik.fm/stream/mp3"
                }
            ],
            currentStationUri: "x-rincon-mp3radio://breakz-2012-high.rautemusik.fm/stream/mp3");

        var cut = ctx.Render<GlobalPlayerBar>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Breakz Radio", cut.Markup);
            Assert.DoesNotContain("breakz-2012-high.rautemusik.fm/?ref=rb-djclubcharts", cut.Markup);
        });
    }

    [Fact]
    public void GlobalPlayerBar_PlayerSheet_UsesActiveSpeakerAndNumericVolume()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ConfigureServices(ctx, activeSpeakerIp: "10.0.0.2");

        var cut = ctx.Render<GlobalPlayerBar>();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("25", cut.Find("#global-player-volume-number").GetAttribute("value"));
            Assert.Equal("80", cut.Find("#global-player-volume-number").GetAttribute("max"));
        });

        cut.Find("button[aria-label='Open queue and room controls']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".player-sheet[role='dialog']"));
            Assert.Equal("10.0.0.2", cut.Find("#player-sheet-room").GetAttribute("value"));
            Assert.Contains("Kitchen", cut.Find(".player-sheet .app-dialog__header").TextContent);
            Assert.Equal("25", cut.Find("#player-sheet-volume-number").GetAttribute("value"));
            Assert.Equal("80", cut.Find("#player-sheet-volume-slider").GetAttribute("max"));
        });
    }

    [Fact]
    public async Task PlaybackState_CoalescesRapidVolumeUpdatesAndPersistsFinalValue()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connectorRepo = ConfigureServices(ctx);
        var playbackState = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await playbackState.InitializeAsync();

        var supersededUpdate = playbackState.SetVolumeAsync(15);
        var finalUpdate = playbackState.SetVolumeAsync(44);
        await Task.WhenAll(supersededUpdate, finalUpdate);

        connectorRepo.Verify(repo => repo.SetVolume("10.0.0.1", 44), Times.Once);
        connectorRepo.Verify(repo => repo.SetVolume(It.IsAny<string>(), It.IsAny<int>()), Times.Once);
        Assert.Equal(44, playbackState.Volume);
    }

    [Fact]
    public async Task VolumeCommandPreservesPlaybackOwnershipWhileWaitingForAutomation()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connector = ConfigureServices(ctx);
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        var coordinator = ctx.Services.GetRequiredService<PlaybackCommandCoordinator>();
        var automation = await coordinator.EnterAutomationAsync(["10.0.0.1"], DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.NotNull(automation);
        var claimedSince = DateTimeOffset.UtcNow.AddSeconds(-1);

        var volume = state.SetVolumeAsync(46);
        Assert.False(automation.WasPreempted);
        await automation.DisposeAsync();
        await volume.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(coordinator.ManualSpeakersSince(claimedSince));
        connector.Verify(repo => repo.SetVolume("10.0.0.1", 46), Times.Once);
    }

    [Fact]
    public async Task SyncPlayClaimsAllAffectedRoomsTogether()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ConfigureServices(ctx);
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        var coordinator = ctx.Services.GetRequiredService<PlaybackCommandCoordinator>();
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await state.SyncPlayAsync();

        Assert.True(result.Success);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, coordinator.ManualSpeakersSince(since).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("skip")]
    public async Task GlobalPlayerBar_FailedCommand_ShowsErrorAndRetriesOriginalAction(string command)
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connectorRepo = ConfigureServices(ctx);
        var wasPlaying = command != "play";
        connectorRepo.Setup(repo => repo.IsPlaying(It.IsAny<string>())).ReturnsAsync(wasPlaying);
        if (command == "play")
            connectorRepo.SetupSequence(repo => repo.StartPlaying("10.0.0.1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Internal device details"))
                .Returns(Task.CompletedTask);
        else if (command == "pause")
            connectorRepo.SetupSequence(repo => repo.PausePlaying("10.0.0.1"))
                .ThrowsAsync(new InvalidOperationException("Internal device details"))
                .Returns(Task.CompletedTask);
        else
            connectorRepo.SetupSequence(repo => repo.NextTrack("10.0.0.1"))
                .ThrowsAsync(new InvalidOperationException("Internal device details"))
                .Returns(Task.CompletedTask);

        var cut = ctx.Render<GlobalPlayerBar>();
        cut.Find(command == "skip" ? "[data-qa='global-player-next']" : ".global-player-bar__play").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Check the speaker connection", cut.Find("[data-qa='playback-command-error']").TextContent);
            Assert.DoesNotContain("Internal device details", cut.Markup);
        });

        // A background refresh must neither hide the error nor change the retry intent.
        connectorRepo.Setup(repo => repo.IsPlaying(It.IsAny<string>())).ReturnsAsync(!wasPlaying);
        await cut.InvokeAsync(() => ctx.Services.GetRequiredService<PlaybackUiStateService>().RefreshAsync());
        cut.Find("[data-qa='playback-command-error'] button").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-qa='playback-command-error']")));
        if (command == "play")
        {
            connectorRepo.Verify(repo => repo.StartPlaying("10.0.0.1", It.IsAny<CancellationToken>()), Times.Exactly(2));
            connectorRepo.Verify(repo => repo.PausePlaying(It.IsAny<string>()), Times.Never);
        }
        else if (command == "pause")
        {
            connectorRepo.Verify(repo => repo.PausePlaying("10.0.0.1"), Times.Exactly(2));
            connectorRepo.Verify(repo => repo.StartPlaying(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            connectorRepo.Verify(repo => repo.NextTrack("10.0.0.1"), Times.Exactly(2));
        }
    }

    [Fact]
    public async Task PlaybackState_ChangingRoom_DiscardsFailedCommand()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        var connectorRepo = ConfigureServices(ctx);
        connectorRepo.Setup(repo => repo.NextTrack("10.0.0.1")).ThrowsAsync(new InvalidOperationException("Offline"));
        var state = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await state.InitializeAsync();
        await state.SkipNextAsync();
        Assert.NotNull(state.PlaybackErrorMessage);

        await state.SetActiveSpeakerAsync("10.0.0.2");
        await state.RetryLastPlaybackCommandAsync();

        Assert.Null(state.PlaybackErrorMessage);
        connectorRepo.Verify(repo => repo.NextTrack("10.0.0.1"), Times.Once);
        connectorRepo.Verify(repo => repo.NextTrack("10.0.0.2"), Times.Never);
    }

    [Fact]
    public void GlobalPlayerBar_WithoutSpeakers_RendersNoPlaybackControls()
    {
        using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var connectorRepo = ConfigureServices(ctx, withSpeakers: false);
        var cut = ctx.Render<GlobalPlayerBar>();

        Assert.Empty(cut.FindAll("[data-qa='global-player-bar'], .player-sheet, button, select, input"));
        connectorRepo.Verify(repo => repo.StartPlaying(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        connectorRepo.Verify(repo => repo.GetVolume(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GlobalPlayerBar_FailedCommand_CanBeRetriedInsideExpandedPlayer()
    {
        await using var ctx = new BunitContext();
        RepositoryTestServices.AddDefaults(ctx.Services);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var connectorRepo = ConfigureServices(ctx);
        connectorRepo.SetupSequence(repo => repo.NextTrack("10.0.0.1"))
            .ThrowsAsync(new InvalidOperationException("Offline"))
            .Returns(Task.CompletedTask);
        var cut = ctx.Render<GlobalPlayerBar>();
        cut.Find("[data-qa='global-player-next']").Click();
        cut.Find(".player-surface__open").Click();

        Assert.Single(cut.FindAll("[data-qa='playback-command-error']"));
        cut.Find(".player-sheet [data-qa='playback-command-error'] button").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-qa='playback-command-error']")));
        connectorRepo.Verify(repo => repo.NextTrack("10.0.0.1"), Times.Exactly(2));
    }

    private static Mock<ISonosConnectorRepo> ConfigureServices(
        BunitContext ctx,
        List<TuneInStation>? stations = null,
        string currentStationUri = "http://stream.example/live",
        string activeSpeakerIp = "10.0.0.1",
        bool withSpeakers = true)
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
            IP_Adress = activeSpeakerIp,
            Volume = 25,
            MaxVolume = 80,
            Stations = stations ?? new List<TuneInStation>(),
            Speakers = withSpeakers ?
            [
                new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" },
                new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.2" }
            ] : []
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.UpdateSettingsAsync(It.IsAny<Action<SonosSettings>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Action<SonosSettings> update, CancellationToken token) => { update(settings); return settings; });
        settingsRepo.Setup(repo => repo.SaveChangesAsync(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(repo => repo.GetVolume(It.IsAny<string>())).ReturnsAsync(settings.Volume);
        connectorRepo.Setup(repo => repo.IsPlaying(It.IsAny<string>())).ReturnsAsync(true);
        connectorRepo.Setup(repo => repo.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentStationUri);
        connectorRepo.Setup(repo => repo.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "Current track", Artist = "Current artist" });
        connectorRepo.Setup(repo => repo.GetSpeakerUUID(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string ip, CancellationToken _) => $"uuid:{ip}");
        connectorRepo.Setup(repo => repo.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.StartPlaying(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.PausePlaying(It.IsAny<string>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.NextTrack(It.IsAny<string>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.SetVolume(It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.GetQueue(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosQueuePage(Array.Empty<SonosQueueItem>(), 0, 0, 0));

        RepositoryTestServices.Register(ctx.Services, settingsRepo.Object, connectorRepo.Object, Mock.Of<IHolidayRepo>());
        ctx.Services.AddSingleton(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddSingleton(TimeProvider.System);
        ctx.Services.AddScoped<PlaybackUiStateService>();

        return connectorRepo;
    }
}
