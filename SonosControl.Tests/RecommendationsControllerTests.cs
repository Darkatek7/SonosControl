using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Controllers;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using Xunit;

namespace SonosControl.Tests;

public class RecommendationsControllerTests
{
    [Fact]
    public async Task Get_NormalizesStationVariantsIntoConfiguredStationName()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;

        db.PlaybackStats.AddRange(
            new PlaybackHistory
            {
                StartTime = now.AddMinutes(-30),
                TrackName = "mp3",
                Artist = "x-rincon-mp3radio://web.radio.antennevorarlberg.at/av-live/stream/mp3",
                MediaType = "Track",
                DurationSeconds = 120
            },
            new PlaybackHistory
            {
                StartTime = now.AddMinutes(-45),
                TrackName = "x-rincon-mp3radio://web.radio.antennevorarlberg.at/av-live/stream/mp3",
                Artist = "Live Stream",
                MediaType = "Stream",
                DurationSeconds = 180
            });

        await db.SaveChangesAsync();

        var settings = new SonosSettings
        {
            Stations = new List<TuneInStation>
            {
                new() { Name = "Antenne Vorarlberg", Url = "web.radio.antennevorarlberg.at/av-live/stream/mp3" }
            }
        };

        var controller = CreateController(db, settings);

        var result = await controller.Get(days: 30, user: "alice");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<RecommendationsController.RecommendationResponse>(ok.Value);

        var timeItem = Assert.Single(payload.TimeOfDayRecommendations.Where(x => x.Name == "Antenne Vorarlberg"));
        Assert.Equal("Station", timeItem.MediaType);
        Assert.Equal(2, timeItem.PlayCount);
        Assert.Equal(300d, timeItem.Score, 3);

        var teamItem = Assert.Single(payload.TeamTrending.Where(x => x.Name == "Antenne Vorarlberg"));
        Assert.Equal("Station", teamItem.MediaType);
        Assert.Equal(2, teamItem.PlayCount);
        Assert.Equal(300d, teamItem.Score, 3);
    }

    [Fact]
    public async Task Get_UsesArtistUrlAsFallbackWhenTrackLabelIsGeneric()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;

        db.PlaybackStats.Add(new PlaybackHistory
        {
            StartTime = now.AddMinutes(-25),
            TrackName = "mp3",
            Artist = "x-rincon-mp3radio://example.invalid/live/stream/mp3",
            MediaType = "Stream",
            DurationSeconds = 90
        });

        await db.SaveChangesAsync();

        var controller = CreateController(db, new SonosSettings());

        var result = await controller.Get(days: 30, user: "alice");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<RecommendationsController.RecommendationResponse>(ok.Value);

        var teamItem = Assert.Single(payload.TeamTrending);
        Assert.Equal("example.invalid/live/stream/mp3", teamItem.Name);
        Assert.Equal("Station", teamItem.MediaType);
    }

    private static RecommendationsController CreateController(ApplicationDbContext db, SonosSettings settings)
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(x => x.ISettingsRepo).Returns(settingsRepo.Object);

        return new RecommendationsController(db, uow.Object);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }
}
