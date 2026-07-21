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

    private static Mock<ISonosConnectorRepo> ConfigureServices(
        TestContext ctx,
        List<TuneInStation>? stations = null,
        string currentStationUri = "http://stream.example/live")
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
            IP_Adress = "10.0.0.1",
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
        connectorRepo.Setup(repo => repo.GetVolume("10.0.0.1")).ReturnsAsync(settings.Volume);
        connectorRepo.Setup(repo => repo.IsPlaying("10.0.0.1")).ReturnsAsync(true);
        connectorRepo.Setup(repo => repo.GetCurrentStationAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentStationUri);
        connectorRepo.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "Current track", Artist = "Current artist" });
        connectorRepo.Setup(repo => repo.GetSpeakerUUID(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string ip, CancellationToken _) => $"uuid:{ip}");
        connectorRepo.Setup(repo => repo.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.StartPlaying(It.IsAny<string>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.NextTrack(It.IsAny<string>())).Returns(Task.CompletedTask);

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
