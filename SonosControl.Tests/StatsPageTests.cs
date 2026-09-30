using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class StatsPageTests
{
    [Fact]
    public void ListeningOverview_UsesPlaybackAndHidesTechnicalNames()
    {
        using var ctx = CreateContext();
        var db = ctx.Services.GetRequiredService<ApplicationDbContext>();
        db.Logs.Add(new LogEntry { Action = "Playback Started", PerformedBy = "TestUser", Timestamp = DateTime.UtcNow });
        db.PlaybackStats.AddRange(
            Playback("Track1", "Spotify", 120, "Kitchen", "Artist1"),
            Playback("Radio 1", "Station", 300, "Office"),
            Playback("breakz?ref=rb-djclubcharts&amp;upd-meta&amp;token=abc123", "Track", 600, "Office"),
            Playback("vbg-q2a", "Track", 500, "Office"));
        db.SaveChanges();

        var cut = ctx.Render<StatsPage>();
        cut.WaitForAssertion(() => Assert.Equal("25m", cut.Find("[data-qa='listening-total']").TextContent));
        Assert.Contains("Listening by day", cut.Markup);
        Assert.Contains("Kitchen", cut.Markup);
        Assert.Contains("Radio 1", cut.Markup);
        Assert.Contains("Track1", cut.Markup);
        Assert.DoesNotContain("breakz?", cut.Markup);
        Assert.DoesNotContain("vbg-q2a", cut.Markup);
        Assert.DoesNotContain("TestUser", cut.Markup);
        Assert.DoesNotContain("Station Master", cut.Markup);
        Assert.Equal(30, cut.FindAll(".listening-chart-column").Count);
        Assert.Equal(30, cut.FindAll(".listening-daily-values tbody tr").Count);
    }

    [Fact]
    public void ListeningOverview_PeriodChangeRecalculatesAllSections()
    {
        using var ctx = CreateContext();
        var db = ctx.Services.GetRequiredService<ApplicationDbContext>();
        db.PlaybackStats.AddRange(
            Playback("Recent song", "Spotify", 600, "Kitchen"),
            Playback("Older song", "Spotify", 1200, "Office", daysAgo: 15),
            Playback("Oldest song", "Spotify", 1800, "Bedroom", daysAgo: 45));
        db.SaveChanges();
        var cut = ctx.Render<StatsPage>();
        cut.WaitForAssertion(() => Assert.Equal("30m", cut.Find("[data-qa='listening-total']").TextContent));

        cut.Find("#listening-period").Change("7");
        cut.WaitForAssertion(() => Assert.Equal("10m", cut.Find("[data-qa='listening-total']").TextContent));
        Assert.DoesNotContain("Older song", cut.Markup);
        Assert.DoesNotContain("Office", cut.Markup);
        Assert.Equal(7, cut.FindAll(".listening-chart-column").Count);

        cut.Find("#listening-period").Change("90");
        cut.WaitForAssertion(() => Assert.Equal("1h 0m", cut.Find("[data-qa='listening-total']").TextContent));
        Assert.Contains("Oldest song", cut.Markup);
        Assert.Contains("Bedroom", cut.Markup);
        Assert.Equal(90, cut.FindAll(".listening-chart-column").Count);
    }

    [Fact]
    public void SmallListeningTotals_UseAMinuteScale()
    {
        using var ctx = CreateContext();
        var db = ctx.Services.GetRequiredService<ApplicationDbContext>();
        db.PlaybackStats.Add(Playback("Short song", "Spotify", 120, "Kitchen"));
        db.SaveChanges();
        var cut = ctx.Render<StatsPage>();
        cut.WaitForAssertion(() => Assert.Equal("2m1m0", cut.Find(".listening-chart-scale").TextContent));
        Assert.Equal("height: 100%", cut.Find(".listening-chart-column .is-peak").GetAttribute("style"));
    }

    [Fact]
    public void ActivityWithoutPlayback_ShowsListeningEmptyState()
    {
        using var ctx = CreateContext();
        var db = ctx.Services.GetRequiredService<ApplicationDbContext>();
        db.Logs.Add(new LogEntry { Action = "Playback Started", Timestamp = DateTime.UtcNow });
        db.SaveChanges();
        var cut = ctx.Render<StatsPage>();
        cut.WaitForAssertion(() => Assert.Contains("No listening recorded in this period", cut.Markup));
        Assert.Empty(cut.FindAll(".listening-chart"));
        Assert.Equal("/insights?tab=activity", cut.Find(".listening-empty a").GetAttribute("href"));
    }

    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("admin");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        ctx.Services.AddSingleton(new ApplicationDbContext(options));
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddLogging();
        return ctx;
    }

    private static PlaybackHistory Playback(string name, string mediaType, double seconds, string room, string artist = "", int daysAgo = 1) => new()
    {
        TrackName = name,
        Artist = artist,
        MediaType = mediaType,
        SpeakerName = room,
        DurationSeconds = seconds,
        StartTime = DateTime.UtcNow.Date.AddDays(-daysAgo).AddHours(12)
    };
}
