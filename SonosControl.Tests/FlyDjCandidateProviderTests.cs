using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public class FlyDjCandidateProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 14, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static ApplicationDbContext NewDb()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"flydj-candidates-{Guid.NewGuid():N}").Options);

    private static FlyDjCandidateProvider Provider(ApplicationDbContext db, TimeZoneInfo? zone = null)
        => new(db, new ConfiguredTimeZoneService(zone ?? TimeZoneInfo.Utc));

    private static SonosSettings Settings(int stations)
    {
        var settings = new SonosSettings
        {
            Stations = Enumerable.Range(1, stations)
                .Select(i => new TuneInStation { Name = $"Station {i}", Url = $"radio{i}.example.com/live" }).ToList()
        };
        return settings;
    }

    private static PlaybackHistory Listened(string station, DateTimeOffset start, double seconds, string mediaType = "Station")
        => new() { TrackName = station, Artist = "Live Stream", MediaType = mediaType, StartTime = start.UtcDateTime, DurationSeconds = seconds };

    [Fact]
    public async Task PriorFavoursStationsHeardNearThisHourAndFavourites()
    {
        using var db = NewDb();
        db.PlaybackStats.AddRange(
            Listened("Station 3", Now.AddDays(-2), 3600),                       // same hour, heavy
            Listened("Station 3", Now.AddDays(-3).AddMinutes(30), 1800),
            Listened("Station 2", Now.AddDays(-1).AddHours(1), 600),            // within +-2 h
            Listened("Station 4", Now.AddDays(-1).AddHours(-12), 7200));        // wrong time of day
        db.UserFavouriteSources.Add(new UserFavouriteSource { UserId = "u1", SourceType = "radio", SourceUrl = "radio5.example.com/live" });
        await db.SaveChangesAsync();

        var candidates = await Provider(db).GetCandidatesAsync(Settings(5), Now);

        var order = candidates.OrderByDescending(candidate => candidate.Prior).Select(candidate => candidate.Name).ToList();
        Assert.Equal("Station 3", order[0]);
        Assert.True(candidates.Single(c => c.Name == "Station 2").Prior > candidates.Single(c => c.Name == "Station 4").Prior);
        Assert.True(candidates.Single(c => c.Name == "Station 5").Prior > candidates.Single(c => c.Name == "Station 1").Prior);
        Assert.Equal(candidates.Single(c => c.Name == "Station 4").Prior, candidates.Single(c => c.Name == "Station 1").Prior);
    }

    [Fact]
    public async Task OldAndNonStationHistoryIsIgnoredAndLocalTimeDecidesTheHourWindow()
    {
        using var db = NewDb();
        db.PlaybackStats.AddRange(
            Listened("Station 1", Now.AddDays(-45), 9000),
            Listened("Station 2", Now.AddDays(-1), 9000, mediaType: "Spotify"),
            Listened("Station 3", Now.AddDays(-1).AddHours(-8), 3600));   // 06:00 UTC = 08:00 in Vienna, 6 h from 14:00 UTC
        await db.SaveChangesAsync();
        var vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

        var utc = await Provider(db).GetCandidatesAsync(Settings(3), Now);
        var local = await Provider(db, vienna).GetCandidatesAsync(Settings(3), Now);

        Assert.All(utc, candidate => Assert.Equal(0, candidate.Prior, 6));
        Assert.All(local, candidate => Assert.Equal(0, candidate.Prior, 6));
    }

    [Fact]
    public async Task LocalHourWrapsAroundMidnight()
    {
        using var db = NewDb();
        var lateNight = new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);
        db.PlaybackStats.Add(Listened("Station 2", lateNight.AddDays(-1), 3600));   // 23:30 yesterday
        await db.SaveChangesAsync();

        var candidates = await Provider(db).GetCandidatesAsync(Settings(3), new DateTimeOffset(2026, 10, 20, 0, 30, 0, TimeSpan.Zero));

        Assert.Equal("Station 2", candidates.OrderByDescending(candidate => candidate.Prior).First().Name);
    }

    [Fact]
    public async Task FlyDjFeedbackRewardsLongDwellAndPunishesOverrides()
    {
        using var db = NewDb();
        var settings = Settings(3);
        db.FlyDjDecisions.AddRange(
            Decision("radio1.example.com/live", Now.AddDays(-1), dwellSeconds: 45 * 60, overridden: false),
            Decision("radio2.example.com/live", Now.AddDays(-1), dwellSeconds: 60, overridden: true),
            Decision("radio2.example.com/live", Now.AddDays(-2), dwellSeconds: 120, overridden: true));
        await db.SaveChangesAsync();

        var candidates = await Provider(db).GetCandidatesAsync(settings, Now);

        var byName = candidates.ToDictionary(candidate => candidate.Name);
        Assert.True(byName["Station 1"].Prior > byName["Station 3"].Prior);
        Assert.True(byName["Station 3"].Prior > byName["Station 2"].Prior);
    }

    [Fact]
    public async Task PriorIsZNormalisedAcrossTheAuditionSet()
    {
        using var db = NewDb();
        db.PlaybackStats.AddRange(
            Listened("Station 1", Now.AddDays(-1), 6000), Listened("Station 2", Now.AddDays(-1), 3000), Listened("Station 3", Now.AddDays(-1), 600));
        await db.SaveChangesAsync();

        var candidates = await Provider(db).GetCandidatesAsync(Settings(4), Now);

        Assert.Equal(0, candidates.Average(candidate => candidate.Prior), 6);
        var variance = candidates.Average(candidate => candidate.Prior * candidate.Prior);
        Assert.Equal(1, variance, 6);
    }

    [Fact]
    public async Task OnlyConfiguredCandidateUrlsAreConsideredWhenTheFilterIsSet()
    {
        using var db = NewDb();
        var settings = Settings(8);
        settings.FlyDj.CandidateStationUrls = ["RADIO2.example.com/live", " radio7.example.com/live "];

        var candidates = await Provider(db).GetCandidatesAsync(settings, Now);

        Assert.Equal(["Station 2", "Station 7"], candidates.Select(candidate => candidate.Name).Order().ToArray());
        Assert.All(candidates, candidate => Assert.False(candidate.IsExploration));
    }

    [Fact]
    public async Task EmptyFilterUsesTheWholeLibraryAndEmptyLibraryYieldsNothing()
    {
        using var db = NewDb();

        Assert.Equal(5, (await Provider(db).GetCandidatesAsync(Settings(5), Now)).Count);
        Assert.Empty(await Provider(db).GetCandidatesAsync(new SonosSettings { Stations = [] }, Now));
        var unmatched = Settings(3);
        unmatched.FlyDj.CandidateStationUrls = ["nothing.example.com/live"];
        Assert.Empty(await Provider(db).GetCandidatesAsync(unmatched, Now));
    }

    [Fact]
    public async Task TopFiveByPriorPlusOneLeastRecentlyAuditionedExplorationStation()
    {
        using var db = NewDb();
        // Stations 1-5 are the habit favourites. Of the rest, station 7 was auditioned long ago, 6 and 8 recently, 9 never.
        db.PlaybackStats.AddRange(Enumerable.Range(1, 5).Select(i => Listened($"Station {i}", Now.AddDays(-1), 1000 * (6 - i))));
        db.FlyDjDecisions.Add(Decision("radio1.example.com/live", Now.AddDays(-5), 600, false,
            audited: [6, 7, 8], at: Now.AddDays(-5)));
        db.FlyDjDecisions.Add(Decision("radio1.example.com/live", Now.AddDays(-1), 600, false,
            audited: [6, 8], at: Now.AddDays(-1)));
        await db.SaveChangesAsync();
        var settings = Settings(9);

        var candidates = await Provider(db).GetCandidatesAsync(settings, Now);

        Assert.Equal(6, candidates.Count);
        Assert.Equal(["Station 1", "Station 2", "Station 3", "Station 4", "Station 5"],
            candidates.Where(candidate => !candidate.IsExploration).Select(candidate => candidate.Name).Order().ToArray());
        var exploration = Assert.Single(candidates, candidate => candidate.IsExploration);
        Assert.Equal("Station 9", exploration.Name);   // never auditioned beats 7 (audited five days ago)
        Assert.Equal(["st-1", "st-2", "st-3", "st-4", "st-5", "st-6"], candidates.Select(candidate => candidate.Id).ToArray());
    }

    [Fact]
    public async Task ExplorationPrefersTheOldestAuditionWhenEveryStationWasAuditioned()
    {
        using var db = NewDb();
        db.PlaybackStats.AddRange(Enumerable.Range(1, 5).Select(i => Listened($"Station {i}", Now.AddDays(-1), 1000 * (6 - i))));
        db.FlyDjDecisions.Add(Decision("radio1.example.com/live", Now.AddDays(-9), 600, false, audited: [7], at: Now.AddDays(-9)));
        db.FlyDjDecisions.Add(Decision("radio1.example.com/live", Now.AddDays(-2), 600, false, audited: [6, 8], at: Now.AddDays(-2)));
        db.FlyDjDecisions.Add(Decision("radio1.example.com/live", Now.AddDays(-4), 600, false, audited: [9], at: Now.AddDays(-4)));
        await db.SaveChangesAsync();

        var candidates = await Provider(db).GetCandidatesAsync(Settings(9), Now);

        Assert.Equal("Station 7", Assert.Single(candidates, candidate => candidate.IsExploration).Name);
    }

    [Fact]
    public async Task SelectionIsDeterministicForTheSameState()
    {
        using var db = NewDb();
        db.PlaybackStats.Add(Listened("Station 4", Now.AddDays(-1), 900));
        await db.SaveChangesAsync();
        var settings = Settings(12);

        var first = await Provider(db).GetCandidatesAsync(settings, Now);
        var second = await Provider(db).GetCandidatesAsync(settings, Now);

        Assert.Equal(first, second);
        Assert.Equal(6, first.Count);
    }

    [Fact]
    public void ZNormalizeHandlesDegenerateInput()
    {
        Assert.Empty(FlyDjCandidateProvider.ZNormalize([]));
        Assert.Equal([0.0], FlyDjCandidateProvider.ZNormalize([3.0]));
        Assert.Equal([0.0, 0.0, 0.0], FlyDjCandidateProvider.ZNormalize([2.0, 2.0, 2.0]));
        var z = FlyDjCandidateProvider.ZNormalize([1.0, 3.0]);
        Assert.Equal([-1.0, 1.0], z);
    }

    private static FlyDjDecision Decision(string url, DateTimeOffset decidedAt, double dwellSeconds, bool overridden,
        int[]? audited = null, DateTimeOffset? at = null)
    {
        var candidates = (audited ?? [1]).Select(i => new FlyDjCandidateScore(
            $"Station {i}", $"radio{i}.example.com/live", 1.0, 0, 0, 0.5, null, null)).ToList();
        return new FlyDjDecision
        {
            DecidedAtUtc = (at ?? decidedAt).UtcDateTime,
            StationName = "x",
            StationUrl = url,
            CandidatesJson = JsonSerializer.Serialize(candidates, Json),
            EndedAtUtc = decidedAt.AddSeconds(dwellSeconds).UtcDateTime,
            DwellSeconds = dwellSeconds,
            OverriddenByUser = overridden
        };
    }
}
