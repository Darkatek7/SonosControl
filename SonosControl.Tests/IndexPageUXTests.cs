using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Pages;
using Xunit;

namespace SonosControl.Tests;

public class IndexPageUXTests
{
    [Fact]
    public void IndexPage_RendersEmptyStates_WhenListsAreEmpty()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx, new List<TuneInStation>(), new List<SpotifyObject>(), new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            // Check for empty states for Stations (default tab)
            Assert.Contains("No stations saved", cut.Markup);
        });

        // Switch to Spotify
        cut.Find("button.nav-link:nth-child(2)").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No Spotify tracks saved", cut.Markup);
        });

        // Switch to YouTube
        cut.Find("button.nav-link:nth-child(3)").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No YouTube collections saved", cut.Markup);
        });
    }

    [Fact]
    public void IndexPage_RendersItems_WhenListsAreNotEmpty()
    {
        using var ctx = new TestContext();

        var stations = new List<TuneInStation> { new TuneInStation { Name = "Test Station", Url = "http://test" } };
        var tracks = new List<SpotifyObject> { new SpotifyObject { Name = "Test Track", Url = "spotify:track:test" } };
        var collections = new List<YouTubeMusicObject> { new YouTubeMusicObject { Name = "Test Collection", Url = "https://music.youtube.com/playlist" } };

        using var resources = ConfigureServices(ctx, stations, tracks, collections);

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            // Check for items in Stations (default)
            Assert.Contains("Test Station", cut.Markup);
            var playButtons = cut.FindAll("button[aria-label^='Play Test Station']");
            Assert.NotEmpty(playButtons);
        });

        // Switch to Spotify
        cut.Find("button.nav-link:nth-child(2)").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Test Track", cut.Markup);
            var playButtons = cut.FindAll("button[aria-label^='Play Test Track']");
            Assert.NotEmpty(playButtons);
        });

        // Switch to YouTube
        cut.Find("button.nav-link:nth-child(3)").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Test Collection", cut.Markup);
             var playButtons = cut.FindAll("button[aria-label^='Play Test Collection']");
            Assert.NotEmpty(playButtons);
        });
    }

    [Fact]
    public void IndexPage_HasAccessibleTabs_And_AddButtons()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx, new List<TuneInStation>(), new List<SpotifyObject>(), new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            // Verify tabs
            var stationsTab = cut.Find("#tab-stations");
            Assert.Equal("tab", stationsTab.GetAttribute("role"));
            Assert.Equal("true", stationsTab.GetAttribute("aria-selected"));
            Assert.Equal("panel-stations", stationsTab.GetAttribute("aria-controls"));

            var spotifyTab = cut.Find("#tab-spotify");
            Assert.Equal("tab", spotifyTab.GetAttribute("role"));
            Assert.Equal("false", spotifyTab.GetAttribute("aria-selected"));

            // Verify content panel
            var panel = cut.Find("#panel-stations");
            Assert.Equal("tabpanel", panel.GetAttribute("role"));
            Assert.Equal("tab-stations", panel.GetAttribute("aria-labelledby"));

            // Verify Add buttons have aria-labels
            // Note: The visibility depends on which tab is active. Stations is active by default.
            var addStationBtn = cut.Find("button[aria-label='Add Station']");
            Assert.NotNull(addStationBtn);
        });

        // Switch tab to check other buttons
        cut.Find("#tab-spotify").Click();
        cut.WaitForAssertion(() =>
        {
             var addSpotifyBtn = cut.Find("button[aria-label='Add Spotify Track']");
             Assert.NotNull(addSpotifyBtn);
        });
    }

    private sealed class TestResources : IDisposable
    {
        public ApplicationDbContext DbContext { get; }

        public TestResources(ApplicationDbContext dbContext)
        {
            DbContext = dbContext;
        }

        public void Dispose()
        {
            DbContext.Dispose();
        }
    }

    private static TestResources ConfigureServices(TestContext ctx, List<TuneInStation> stations, List<SpotifyObject> tracks, List<YouTubeMusicObject> collections)
    {
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        var settings = new SonosSettings
        {
            IP_Adress = "1.2.3.4",
            Volume = 20,
            MaxVolume = 80,
            Stations = stations,
            SpotifyTracks = tracks,
            YouTubeMusicCollections = collections,
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4", Name = "Living Room" } }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(r => r.GetVolume(It.IsAny<string>())).ReturnsAsync(settings.Volume);
        connectorRepo.Setup(r => r.IsPlaying(It.IsAny<string>())).ReturnsAsync(false);
        connectorRepo.Setup(r => r.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
        connectorRepo.Setup(r => r.GetCurrentTrackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
        connectorRepo.Setup(r => r.GetTrackProgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TimeSpan.Zero, TimeSpan.Zero));
        connectorRepo.Setup(r => r.SetVolume(It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(u => u.ISonosConnectorRepo).Returns(connectorRepo.Object);
        unitOfWork.SetupGet(u => u.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);
        ctx.Services.AddSingleton<INotificationService>(Mock.Of<INotificationService>());

        return new TestResources(dbContext);
    }
}
