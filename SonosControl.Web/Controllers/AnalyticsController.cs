using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SonosControl.Web.Data;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize(Roles = "admin,operator,superadmin")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public AnalyticsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<AnalyticsSummaryResponse>> Summary(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? speaker,
        [FromQuery] string? user)
    {
        var (from, to) = ResolveWindow(fromUtc, toUtc);

        var logs = _db.Logs.AsNoTracking().Where(l => l.Timestamp >= from && l.Timestamp <= to);
        if (!string.IsNullOrWhiteSpace(user))
        {
            logs = logs.Where(l => l.PerformedBy == user);
        }

        var playback = _db.PlaybackStats.AsNoTracking().Where(p => p.StartTime >= from && p.StartTime <= to);
        if (!string.IsNullOrWhiteSpace(speaker))
        {
            playback = playback.Where(p => p.SpeakerName == speaker);
        }

        var totalActions = await logs.CountAsync();
        var totalSessions = await playback.CountAsync();
        var totalDurationSeconds = await playback.SumAsync(x => x.DurationSeconds);

        var topMedia = await playback
            .GroupBy(x => new { x.TrackName, x.MediaType })
            .Select(g => new AnalyticsTopItem(g.Key.TrackName, g.Key.MediaType, g.Sum(x => x.DurationSeconds), g.Count()))
            .OrderByDescending(x => x.DurationSeconds)
            .Take(10)
            .ToListAsync();

        var byDay = await logs
            .GroupBy(x => x.Timestamp.Date)
            .Select(g => new AnalyticsDayStat(g.Key, g.Count()))
            .OrderBy(x => x.Date)
            .ToListAsync();

        return Ok(new AnalyticsSummaryResponse(
            from,
            to,
            totalActions,
            totalSessions,
            totalDurationSeconds,
            topMedia,
            byDay));
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? speaker)
    {
        var (from, to) = ResolveWindow(fromUtc, toUtc);
        var playback = _db.PlaybackStats.AsNoTracking().Where(p => p.StartTime >= from && p.StartTime <= to);
        if (!string.IsNullOrWhiteSpace(speaker))
        {
            playback = playback.Where(p => p.SpeakerName == speaker);
        }

        var rows = await playback
            .OrderByDescending(x => x.StartTime)
            .Select(x => new
            {
                x.SpeakerName,
                x.TrackName,
                x.Artist,
                x.MediaType,
                x.StartTime,
                x.EndTime,
                x.DurationSeconds
            })
            .ToListAsync();

        var csv = new StringBuilder();
        csv.AppendLine("Speaker,Track,Artist,MediaType,StartTimeUtc,EndTimeUtc,DurationSeconds");
        foreach (var row in rows)
        {
            csv.Append(Escape(row.SpeakerName)).Append(',')
                .Append(Escape(row.TrackName)).Append(',')
                .Append(Escape(row.Artist)).Append(',')
                .Append(Escape(row.MediaType)).Append(',')
                .Append(row.StartTime.ToString("O")).Append(',')
                .Append(row.EndTime?.ToString("O") ?? string.Empty).Append(',')
                .Append(row.DurationSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine();
        }

        var content = Encoding.UTF8.GetBytes(csv.ToString());
        return File(content, "text/csv", $"analytics-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
    }

    private static (DateTime From, DateTime To) ResolveWindow(DateTime? fromUtc, DateTime? toUtc)
    {
        var to = toUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var from = fromUtc?.ToUniversalTime() ?? to.AddDays(-30);

        if (from > to)
        {
            (from, to) = (to, from);
        }

        return (from, to);
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    public sealed record AnalyticsSummaryResponse(
        DateTime FromUtc,
        DateTime ToUtc,
        int TotalActions,
        int TotalSessions,
        double TotalDurationSeconds,
        IReadOnlyList<AnalyticsTopItem> TopMedia,
        IReadOnlyList<AnalyticsDayStat> ActionsByDay);

    public sealed record AnalyticsTopItem(
        string Name,
        string MediaType,
        double DurationSeconds,
        int Plays);

    public sealed record AnalyticsDayStat(
        DateTime Date,
        int ActionCount);
}
