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
using IndexPage = SonosControl.Web.Pages.Index;
using Xunit;

namespace SonosControl.Tests;

public class IndexQueueTests
{
    [Fact]
    public void QueuePanel_RendersRadioQueueItems()
    {
        using var ctx = new TestContext();

        var queuePage = new SonosQueuePage(
            new[]
            {
                new SonosQueueItem(0, "Morning Briefing", null, null, null),
                new SonosQueueItem(1, "City Updates", null, null, null)
            },
            StartIndex: 0,
            NumberReturned: 2,
            TotalMatches: 2);

        using var resources = ConfigureServices(ctx, queuePage, "x-rincon-mp3radio://example");

        using var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-qa='queue-item']");
            Assert.Equal(2, items.Count);
            Assert.Contains("Morning Briefing", items[0].TextContent);
        });
    }

    [Fact]
    public void QueuePanel_RendersSpotifyQueueItemsWithArtist()
    {
        using var ctx = new TestContext();

        var queuePage = new SonosQueuePage(
            new[]
            {
                new SonosQueueItem(0, "Skyline", "Neon Dreams", "Night Drive", null)
            },
            StartIndex: 0,
            NumberReturned: 1,
            TotalMatches: 1);

        using var resources = ConfigureServices(ctx, queuePage, "spotify:track:abc123");

        using var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("[data-qa='queue-item']");
            Assert.Single(items);
            Assert.Contains("Neon Dreams – Skyline", items[0].TextContent);
        });

        Assert.DoesNotContain("Queue is empty", cut.Markup);
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

    private static TestResources ConfigureServices(TestContext ctx, SonosQueuePage queuePage, string currentStation)
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
            Stations = new List<TuneInStation>(),
            SpotifyTracks = new List<SpotifyObject>(),
            YouTubeMusicCollections = new List<YouTubeMusicObject>()
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(r => r.GetVolume(It.IsAny<string>())).ReturnsAsync(settings.Volume);
        connectorRepo.Setup(r => r.IsPlaying(It.IsAny<string>())).ReturnsAsync(true);
        connectorRepo.Setup(r => r.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentStation);
        connectorRepo.Setup(r => r.GetCurrentTrackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Test Track");
        connectorRepo.Setup(r => r.GetTrackProgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TimeSpan.Zero, TimeSpan.Zero));
        connectorRepo.Setup(r => r.GetQueue(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queuePage);
        connectorRepo.Setup(r => r.SetVolume(It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(r => r.StartPlaying(It.IsAny<string>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(r => r.PausePlaying(It.IsAny<string>())).Returns(Task.CompletedTask);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(u => u.ISonosConnectorRepo).Returns(connectorRepo.Object);
        unitOfWork.SetupGet(u => u.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);

        return new TestResources(dbContext);
    }
}
