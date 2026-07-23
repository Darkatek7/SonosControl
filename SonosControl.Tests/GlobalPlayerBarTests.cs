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
    public void GlobalPlayerBar_NextButton_UsesCentralPlaybackState()
    {
        using var ctx = new TestContext();
        var connectorRepo = ConfigureServices(ctx);

        var cut = ctx.RenderComponent<GlobalPlayerBar>();

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
    public void GlobalPlayerBar_SyncButton_IsEnabledAfterInitialRefresh()
    {
        using var ctx = new TestContext();
        var connectorRepo = ConfigureServices(ctx);

        var cut = ctx.RenderComponent<GlobalPlayerBar>();

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
            connectorRepo.Verify(repo => repo.StartPlaying("10.0.0.2"), Times.Once);
        });
    }

    [Fact]
    public void GlobalPlayerBar_ResolvesSavedStationNameFromCurrentUri()
    {
        using var ctx = new TestContext();
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
            currentStationUri: "x-rincon-mp3radio://breakz-2012-high.rautemusik.fm/?ref=rb-djclubcharts");

        var cut = ctx.RenderComponent<GlobalPlayerBar>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Breakz Radio", cut.Markup);
            Assert.DoesNotContain("breakz-2012-high.rautemusik.fm/?ref=rb-djclubcharts", cut.Markup);
        });
    }

    [Fact]
    public void GlobalPlayerBar_PlayerSheet_UsesActiveSpeakerAndNumericVolume()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ConfigureServices(ctx, activeSpeakerIp: "10.0.0.2");

        var cut = ctx.RenderComponent<GlobalPlayerBar>();
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
        using var ctx = new TestContext();
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

    private static Mock<ISonosConnectorRepo> ConfigureServices(
        TestContext ctx,
        List<TuneInStation>? stations = null,
        string currentStationUri = "http://stream.example/live",
        string activeSpeakerIp = "10.0.0.1")
    {
        var auth = ctx.AddTestAuthorization();
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
            Speakers =
            [
                new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" },
                new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.2" }
            ]
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(repo => repo.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

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
        connectorRepo.Setup(repo => repo.StartPlaying(It.IsAny<string>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.NextTrack(It.IsAny<string>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.SetVolume(It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.GetQueue(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosQueuePage(Array.Empty<SonosQueueItem>(), 0, 0, 0));

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(uow => uow.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(uow => uow.ISonosConnectorRepo).Returns(connectorRepo.Object);

        ctx.Services.AddSingleton(unitOfWork.Object);
        ctx.Services.AddSingleton(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddScoped<PlaybackUiStateService>();

        return connectorRepo;
    }
}
