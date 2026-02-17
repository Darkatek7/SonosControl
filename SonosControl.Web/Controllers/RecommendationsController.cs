using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Data;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/recommendations")]
[Authorize(Roles = "admin,operator,superadmin")]
public sealed class RecommendationsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IUnitOfWork _uow;

    public RecommendationsController(ApplicationDbContext db, IUnitOfWork uow)
    {
        _db = db;
        _uow = uow;
    }

    [HttpGet]
    public async Task<ActionResult<RecommendationResponse>> Get([FromQuery] int days = 30, [FromQuery] string? user = null)
    {
        var effectiveDays = Math.Clamp(days, 7, 120);
        var from = DateTime.UtcNow.AddDays(-effectiveDays);
        var settings = await _uow.ISettingsRepo.GetSettings() ?? new();
        var resolver = RecommendationMediaResolver.Create(settings);

        var playback = _db.PlaybackStats
            .AsNoTracking()
            .Where(x => x.StartTime >= from);

        var currentHour = DateTime.UtcNow.Hour;
        var minHour = (currentHour + 22) % 24;
        var maxHour = (currentHour + 2) % 24;

        IQueryable<SonosControl.Web.Models.PlaybackHistory> hourFiltered;
        if (minHour <= maxHour)
        {
            hourFiltered = playback.Where(x => x.StartTime.Hour >= minHour && x.StartTime.Hour <= maxHour);
        }
        else
        {
            hourFiltered = playback.Where(x => x.StartTime.Hour >= minHour || x.StartTime.Hour <= maxHour);
        }

        var timeOfDayRawRows = await hourFiltered
            .GroupBy(x => new { x.TrackName, x.Artist, x.MediaType })
            .Select(g => new
            {
                g.Key.TrackName,
                g.Key.Artist,
                g.Key.MediaType,
                Score = g.Sum(x => x.DurationSeconds),
                PlayCount = g.Count()
            })
            .ToListAsync();

        var timeOfDayRows = timeOfDayRawRows
            .Select(row => new PlaybackAggregateRow(row.TrackName, row.Artist, row.MediaType, row.Score, row.PlayCount))
            .ToList();

        var topTimeOfDay = BuildNormalizedPlaybackRecommendations(timeOfDayRows, resolver, 8);

        var trendingRawRows = await playback
            .GroupBy(x => new { x.TrackName, x.Artist, x.MediaType })
            .Select(g => new
            {
                g.Key.TrackName,
                g.Key.Artist,
                g.Key.MediaType,
                Score = g.Sum(x => x.DurationSeconds),
                PlayCount = g.Count()
            })
            .ToListAsync();

        var trendingRows = trendingRawRows
            .Select(row => new PlaybackAggregateRow(row.TrackName, row.Artist, row.MediaType, row.Score, row.PlayCount))
            .ToList();

        var trendingTeam = BuildNormalizedPlaybackRecommendations(trendingRows, resolver, 12);

        var effectiveUser = string.IsNullOrWhiteSpace(user) ? User.Identity?.Name : user;
        var personal = await BuildPersonalRecommendationsAsync(from, effectiveUser, resolver);

        return Ok(new RecommendationResponse(
            GeneratedAtUtc: DateTime.UtcNow,
            WindowDays: effectiveDays,
            PersonalRecommendations: personal,
            TimeOfDayRecommendations: topTimeOfDay,
            TeamTrending: trendingTeam));
    }

    private async Task<IReadOnlyList<RecommendationItem>> BuildPersonalRecommendationsAsync(
        DateTime fromUtc,
        string? user,
        RecommendationMediaResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return Array.Empty<RecommendationItem>();
        }

        var actions = new[]
        {
            "Station Changed",
            "Spotify Track Changed",
            "YouTube Music Changed",
            "Spotify URL Played"
        };

        var logRows = await _db.Logs
            .AsNoTracking()
            .Where(l => l.Timestamp >= fromUtc)
            .Where(l => l.PerformedBy == user)
            .Where(l => actions.Contains(l.Action))
            .Select(l => new { l.Action, l.Details })
            .ToListAsync();

        var personal = logRows
            .Select(row =>
            {
                var url = ExtractUrl(row.Details);
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }

                var resolved = resolver.ResolveFromSourceUrl(url, ResolveMediaType(row.Action));
                return new RecommendationItem(resolved.Name, resolved.MediaType, 1, 1, resolved.SourceUrl);
            })
            .Where(x => x is not null)
            .Select(x => x!)
            .GroupBy(x => new { x.SourceUrl, x.Name, x.MediaType })
            .Select(g => new RecommendationItem(g.Key.Name, g.Key.MediaType, g.Count(), g.Count(), g.Key.SourceUrl))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name)
            .Take(10)
            .ToList();

        return personal;
    }

    private static string? ExtractUrl(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        var trimmed = details.Trim();
        const string prefix = "URL:";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[prefix.Length..].Trim();
        }

        var openParen = trimmed.LastIndexOf('(');
        var closeParen = trimmed.LastIndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
        {
            return trimmed[(openParen + 1)..closeParen].Trim();
        }

        return trimmed;
    }

    private static string ResolveMediaType(string action)
    {
        return action switch
        {
            "Station Changed" => "Station",
            "Spotify Track Changed" => "Spotify",
            "Spotify URL Played" => "Spotify",
            "YouTube Music Changed" => "YouTubeMusic",
            _ => "Unknown"
        };
    }

    private static List<RecommendationItem> BuildNormalizedPlaybackRecommendations(
        IEnumerable<PlaybackAggregateRow> rows,
        RecommendationMediaResolver resolver,
        int take)
    {
        return rows
            .Select(row =>
            {
                var resolved = resolver.ResolveFromPlaybackEntry(row.TrackName, row.Artist, row.MediaType);
                return new RecommendationItem(
                    resolved.Name,
                    resolved.MediaType,
                    row.Score,
                    row.PlayCount,
                    resolved.SourceUrl);
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => new { item.Name, item.MediaType })
            .Select(group => new RecommendationItem(
                group.Key.Name,
                group.Key.MediaType,
                group.Sum(item => item.Score),
                group.Sum(item => item.PlayCount),
                group.Select(item => item.SourceUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Name)
            .Take(take)
            .ToList();
    }

    private sealed record PlaybackAggregateRow(
        string TrackName,
        string Artist,
        string MediaType,
        double Score,
        int PlayCount);

    public sealed record RecommendationResponse(
        DateTime GeneratedAtUtc,
        int WindowDays,
        IReadOnlyList<RecommendationItem> PersonalRecommendations,
        IReadOnlyList<RecommendationItem> TimeOfDayRecommendations,
        IReadOnlyList<RecommendationItem> TeamTrending);

    public sealed record RecommendationItem(
        string Name,
        string MediaType,
        double Score,
        int PlayCount,
        string? SourceUrl);
}
