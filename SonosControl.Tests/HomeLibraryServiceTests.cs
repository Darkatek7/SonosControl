using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class HomeLibraryServiceTests
{
    [Fact]
    public async Task QuickAccess_PutsNewestFavouritesFirst_ThenPopularity_ThenAlphabeticalFallback()
    {
        await using var db = CreateDb();
        AddUser(db);
        var settings = SettingsWithStations("Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf");
        db.UserFavouriteSources.AddRange(
            Favourite("Bravo", DateTime.UtcNow.AddMinutes(-5)),
            Favourite("Delta", DateTime.UtcNow));
        db.PlaybackStats.AddRange(
            Playback("Foxtrot", 300),
            Playback("Alpha", 120));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.GetQuickAccessAsync(settings, 6);

        Assert.Equal(new[] { "Delta", "Bravo", "Foxtrot", "Alpha", "Charlie", "Echo" }, result.Select(item => item.Name));
        Assert.Equal(new[] { true, true, false, false, false, false }, result.Select(item => item.IsFavourite));
    }

    [Fact]
    public async Task QuickAccess_IgnoresStaleFavourites_Deduplicates_AndHonoursLimit()
    {
        await using var db = CreateDb();
        AddUser(db);
        var settings = SettingsWithStations("Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf");
        db.UserFavouriteSources.Add(new UserFavouriteSource
        {
            UserId = "user-one",
            SourceType = "radio",
            SourceUrl = "https://stale.example/live",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.GetQuickAccessAsync(settings, 6);

        Assert.Equal(6, result.Count);
        Assert.Equal(6, result.Select(item => item.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(result, item => Assert.False(item.IsFavourite));
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot" }, result.Select(item => item.Name));
    }

    private static HomeLibraryService CreateService(ApplicationDbContext db)
    {
        var auth = new StaticAuthenticationStateProvider();
        var favourites = new UserFavouriteSourceService(db, auth);
        return new HomeLibraryService(db, favourites);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static void AddUser(ApplicationDbContext db)
    {
        db.Users.Add(new ApplicationUser
        {
            Id = "user-one",
            UserName = "one",
            NormalizedUserName = "ONE",
            FirstName = string.Empty,
            LastName = string.Empty
        });
        db.SaveChanges();
    }

    private static SonosSettings SettingsWithStations(params string[] names) => new()
    {
        Stations = names
            .Select(name => new TuneInStation { Name = name, Url = StationUrl(name) })
            .ToList(),
        SpotifyTracks = new List<SpotifyObject>(),
        YouTubeCollections = new List<YouTubeObject>(),
        YouTubeMusicCollections = new List<YouTubeMusicObject>()
    };

    private static UserFavouriteSource Favourite(string name, DateTime createdAtUtc) => new()
    {
        UserId = "user-one",
        SourceType = "radio",
        SourceUrl = StationUrl(name),
        CreatedAtUtc = createdAtUtc
    };

    private static PlaybackHistory Playback(string name, double durationSeconds) => new()
    {
        TrackName = "Playing stream",
        Artist = StationUrl(name),
        MediaType = "Station",
        StartTime = DateTime.UtcNow,
        DurationSeconds = durationSeconds
    };

    private static string StationUrl(string name) => $"https://{name.ToLowerInvariant()}.example/live";

    private sealed class StaticAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly AuthenticationState _state = new(
            new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, "user-one"),
                        new Claim(ClaimTypes.Name, "one")
                    },
                    "Test")));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);
    }
}
