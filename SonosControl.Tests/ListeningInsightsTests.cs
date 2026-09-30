using SonosControl.DAL.Models;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class ListeningInsightsTests
{
    private static readonly ConfiguredTimeZoneService Vienna = new(TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"));

    [Fact]
    public void MidnightPlayback_IsSplitIntoLocalDaysAndZeroDaysAreRetained()
    {
        var now = Utc(2026, 9, 30, 12);
        var insights = Create([Playback(Utc(2026, 9, 28, 21, 30), 7200)], now);

        Assert.Equal(7, insights.Days.Count);
        Assert.Equal(1800, insights.Days.Single(day => day.Date == new DateTime(2026, 9, 28)).Seconds);
        Assert.Equal(5400, insights.Days.Single(day => day.Date == new DateTime(2026, 9, 29)).Seconds);
        Assert.Equal(5, insights.Days.Count(day => day.Seconds == 0));
        Assert.Equal(2, insights.ActiveDays);
        Assert.Equal(3600, insights.AveragePerActiveDay);
        Assert.Equal(new DateTime(2026, 9, 29), insights.PeakDay?.Date);
    }

    [Fact]
    public void WindowBoundaries_ClipCarryOverPlaybackAndExcludeFutureTime()
    {
        var now = Utc(2026, 9, 30, 12);
        var insights = Create([
            Playback(Utc(2026, 9, 23, 21), 7200), // Last hour overlaps the first local day.
            Playback(Utc(2026, 9, 30, 11), 7200), // Only the saved hour before now counts.
            Playback(Utc(2026, 9, 30, 13), 3600),
            Playback(Utc(2026, 9, 20, 12), 3600)
        ], now);

        Assert.Equal(7200, insights.TotalSeconds);
        Assert.Equal(3600, insights.Days[0].Seconds);
        Assert.Equal(3600, insights.Days[^1].Seconds);
        Assert.Equal(insights.TotalSeconds, insights.Rooms.Sum(room => room.Seconds));
        Assert.Equal(insights.TotalSeconds, insights.Sources.Sum(source => source.Seconds));
    }

    [Theory]
    [InlineData(3, 29, 3, 28, 23, 23)]
    [InlineData(10, 25, 10, 24, 22, 25)]
    public void DaylightSavingDays_UseActualElapsedTime(int month, int day, int startMonth, int startDay, int startHour, int hours)
    {
        var start = Utc(2026, startMonth, startDay, startHour);
        var now = Utc(2026, month, day + 1, 12);
        var insights = Create([Playback(start, hours * 3600)], now);

        Assert.Equal(hours * 3600, insights.Days.Single(value => value.Date == new DateTime(2026, month, day)).Seconds);
        Assert.Equal(1, insights.ActiveDays);
    }

    [Fact]
    public void SimultaneousRooms_AreAddedAndStaleOpenEntriesAreNotExtended()
    {
        var now = Utc(2026, 9, 30, 12);
        var kitchen = Playback(Utc(2026, 9, 29, 10), 3600);
        kitchen.SpeakerName = "Kitchen";
        var office = Playback(kitchen.StartTime, 3600);
        office.SpeakerName = "Office";
        var insights = Create([kitchen, office], now);

        Assert.Equal(7200, insights.TotalSeconds);
        Assert.Equal(2, insights.Rooms.Count);
        Assert.Equal(1, insights.ActiveDays);
    }

    [Fact]
    public void Sources_ResolveCatalogStreamsAndMergeYouTubeMusicLabels()
    {
        var now = Utc(2026, 9, 30, 12);
        var start = now.AddDays(-1);
        var radio = Playback(start, 3600, "Track", "https://radio.example.invalid/live?token=demo");
        var settings = new SonosSettings
        {
            Stations = [new() { Name = "Demo Radio", Url = "https://radio.example.invalid/live" }]
        };
        var insights = ListeningInsights.Create([
            radio,
            Playback(start.AddHours(1), 600, "YouTube Music", "One song"),
            Playback(start.AddHours(2), 600, "YouTubeMusic", "Other song"),
            Playback(start.AddHours(3), 600, "Stream", "Playing Stream")
        ], settings, Vienna, now, 7);

        Assert.Equal(3600, insights.Sources.Single(source => source.Name == "Radio").Seconds);
        Assert.Equal(1200, insights.Sources.Single(source => source.Name == "YouTube Music").Seconds);
        Assert.Equal(600, insights.Sources.Single(source => source.Name == "Other streams").Seconds);
        Assert.Equal("Demo Radio", Assert.Single(insights.Stations).Name);
        Assert.Equal(2, insights.Tracks.Count);
    }

    [Fact]
    public void TwoSpeakersWithOverlappingHistory_CannotExceed48HoursOnANormalDay()
    {
        var start = Utc(2026, 9, 16, 22); // Midnight on 17 September in Vienna.
        var kitchen = Playback(start, 24 * 3600);
        var duplicate = Playback(start.AddHours(10), 12 * 3600 + 49 * 60);
        duplicate.Id = 2;
        var office = Playback(start, 24 * 3600);
        office.SpeakerName = "Office";
        var insights = Create([kitchen, duplicate, office], Utc(2026, 9, 18, 12));

        Assert.Equal(48 * 3600, insights.TotalSeconds);
        Assert.Equal(48 * 3600, insights.PeakDay?.Seconds);
        Assert.Equal(12 * 3600 + 49 * 60, insights.ExcludedOverlapSeconds);
        Assert.All(insights.Rooms, room => Assert.Equal(24 * 3600, room.Seconds));
        Assert.Equal(insights.TotalSeconds, insights.Sources.Sum(source => source.Seconds));
        Assert.Equal(insights.TotalSeconds, insights.Tracks.Sum(track => track.Seconds));
    }

    [Fact]
    public void OverlapUsesLatestMedia_AndRetainsEarlierNonOverlappingParts()
    {
        var start = Utc(2026, 9, 29, 8);
        var first = Playback(start, 3 * 3600, "Spotify", "First song");
        var second = Playback(start.AddHours(1), 3600, "Station", "Radio");
        var duplicate = Playback(second.StartTime, 3600, "Station", "Radio");
        duplicate.SpeakerName = " kitchen ";
        var insights = Create([duplicate, first, second], Utc(2026, 9, 30, 12));

        Assert.Equal(3 * 3600, insights.TotalSeconds);
        Assert.Equal(2 * 3600, insights.ExcludedOverlapSeconds);
        Assert.Equal(2 * 3600, insights.Sources.Single(source => source.Name == "Spotify").Seconds);
        Assert.Equal(3600, insights.Sources.Single(source => source.Name == "Radio").Seconds);
        Assert.Single(insights.Rooms);
    }

    [Fact]
    public void SavedEndTimeBoundsDuration_AndGapsBetweenRecordsStayEmpty()
    {
        var start = Utc(2026, 9, 29, 8);
        var first = Playback(start, 12 * 3600);
        first.EndTime = start.AddMinutes(30);
        var second = Playback(start.AddHours(3), 3600);
        var insights = Create([first, second], Utc(2026, 9, 30, 12));

        Assert.Equal(5400, insights.TotalSeconds);
        Assert.Equal(0, insights.ExcludedOverlapSeconds);
    }

    [Fact]
    public void InvalidDurations_DoNotBecomeListening()
    {
        var now = Utc(2026, 9, 30, 12);
        var insights = Create([Playback(now.AddDays(-1), -1), Playback(now.AddDays(-1), double.NaN), Playback(now.AddDays(-1), double.PositiveInfinity)], now);
        Assert.Equal(0, insights.TotalSeconds);
        Assert.Null(insights.PeakDay);
        Assert.Empty(insights.Rooms);
    }

    private static ListeningInsights Create(IEnumerable<PlaybackHistory> history, DateTime now) =>
        ListeningInsights.Create(history, new SonosSettings(), Vienna, now, 7);

    private static PlaybackHistory Playback(DateTime start, double seconds, string mediaType = "Spotify", string name = "A song") => new()
    {
        StartTime = start,
        DurationSeconds = seconds,
        MediaType = mediaType,
        TrackName = name,
        SpeakerName = "Kitchen"
    };

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
