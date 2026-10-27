using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services.FlyDj;

/// <summary>A library station to audition, with its z-normalised behaviour prior.</summary>
public sealed record FlyDjCandidate(string Id, string Name, string Url, double Prior, bool IsExploration);

/// <summary>
/// Chooses which library stations the brain should audition. Stations are ranked by a behaviour
/// prior (listening history near this hour and FlyDJ's own past outcomes). Personal favourites do not count:
/// they belong to individual users, while FlyDJ plays to a shared room. The top
/// <see cref="TopCount"/> are always included, plus one exploration station that has gone the
/// longest without an audition. Selection is deterministic for a given database state.
/// </summary>
public sealed class FlyDjCandidateProvider(ApplicationDbContext db, ConfiguredTimeZoneService timeZone)
{
    public const int TopCount = 5;
    private const int HistoryDays = 30;
    private const int HourWindow = 2;
    private const int ExplorationLookbackDecisions = 400;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FlyDjCandidate>> GetCandidatesAsync(
        SonosSettings settings, DateTimeOffset now, CancellationToken cancellationToken = default, bool allStations = false)
    {
        var stations = ResolveLibrary(settings);
        if (stations.Count == 0) return [];

        var since = now.UtcDateTime.AddDays(-HistoryDays);
        var localHour = timeZone.Convert(now).Hour;

        var listened = await LoadListenedSecondsAsync(since, localHour, cancellationToken);
        var feedback = await LoadFeedbackAsync(since, Math.Max(1, settings.FlyDj?.MaxDwellMinutes ?? FlyDjSettings.DefaultMaxDwellMinutes), cancellationToken);

        var raw = stations.Select(station =>
        {
            listened.TryGetValue(station.Name.Trim(), out var seconds);
            var listenScore = Math.Log(1 + seconds / 60.0);
            var feedbackScore = feedback.TryGetValue(station.Url.Trim(), out var stats)
                ? stats.AverageDwellRatio - 1.5 * stats.OverrideRate
                : 0.0;
            return (Station: station, Score: listenScore + feedbackScore);
        }).ToList();

        var ranked = raw.OrderByDescending(item => item.Score)
            .ThenBy(item => item.Station.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Station.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selected = (allStations ? ranked : ranked.Take(TopCount)).ToList();
        var rest = allStations ? [] : ranked.Skip(TopCount).ToList();
        TuneInStation? exploration = null;
        if (rest.Count > 0)
        {
            var lastAuditioned = await LoadLastAuditionedAsync(cancellationToken);
            exploration = rest
                .OrderBy(item => lastAuditioned.TryGetValue(item.Station.Url.Trim(), out var at) ? at : DateTime.MinValue)
                .ThenBy(item => item.Station.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Station.Url, StringComparer.OrdinalIgnoreCase)
                .First().Station;
            selected.Add(rest.First(item => ReferenceEquals(item.Station, exploration)));
        }

        // Normalise within the audition set so the prior is on the same scale as the brain z-score.
        var z = ZNormalize(selected.Select(item => item.Score).ToArray());
        return selected.Select((item, index) => new FlyDjCandidate(
            $"st-{index + 1}", item.Station.Name.Trim(), item.Station.Url.Trim(), z[index],
            exploration is not null && ReferenceEquals(item.Station, exploration))).ToList();
    }

    /// <summary>Population z-score; all zeros when there is no spread.</summary>
    public static double[] ZNormalize(IReadOnlyList<double> values)
    {
        var result = new double[values.Count];
        if (values.Count < 2) return result;
        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Count;
        var deviation = Math.Sqrt(variance);
        if (deviation < 1e-9) return result;
        for (var i = 0; i < result.Length; i++) result[i] = (values[i] - mean) / deviation;
        return result;
    }

    private static List<TuneInStation> ResolveLibrary(SonosSettings settings)
    {
        var library = (settings.Stations ?? [])
            .Where(station => !string.IsNullOrWhiteSpace(station.Url) && !string.IsNullOrWhiteSpace(station.Name))
            .GroupBy(station => station.Url.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var allowed = settings.FlyDj?.CandidateStationUrls;
        if (allowed is { Count: > 0 })
        {
            var allowedSet = allowed.Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            library = library.Where(station => allowedSet.Contains(station.Url.Trim())).ToList();
        }
        return library;
    }

    private async Task<Dictionary<string, double>> LoadListenedSecondsAsync(DateTime since, int localHour, CancellationToken cancellationToken)
    {
        var rows = await db.PlaybackStats.AsNoTracking()
            .Where(item => item.MediaType == "Station" && item.StartTime >= since)
            .Select(item => new { item.TrackName, item.StartTime, item.DurationSeconds })
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.TrackName) || row.DurationSeconds <= 0) continue;
            var started = DateTime.SpecifyKind(row.StartTime, DateTimeKind.Utc);
            var hour = timeZone.ConvertUtc(started).Hour;
            var distance = Math.Abs(hour - localHour);
            distance = Math.Min(distance, 24 - distance);
            if (distance > HourWindow) continue;
            var key = row.TrackName.Trim();
            result[key] = result.GetValueOrDefault(key) + row.DurationSeconds;
        }
        return result;
    }

    private sealed record Feedback(double AverageDwellRatio, double OverrideRate);

    private async Task<Dictionary<string, Feedback>> LoadFeedbackAsync(DateTime since, int maxDwellMinutes, CancellationToken cancellationToken)
    {
        var rows = await db.FlyDjDecisions.AsNoTracking()
            .Where(item => item.DecidedAtUtc >= since && item.EndedAtUtc != null)
            .Select(item => new { item.StationUrl, item.DwellSeconds, item.OverriddenByUser })
            .ToListAsync(cancellationToken);
        var maxSeconds = maxDwellMinutes * 60.0;
        return rows.GroupBy(item => item.StationUrl.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Feedback(
                    group.Average(item => Math.Clamp((item.DwellSeconds ?? 0) / maxSeconds, 0, 1)),
                    group.Count(item => item.OverriddenByUser) / (double)group.Count()),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, DateTime>> LoadLastAuditionedAsync(CancellationToken cancellationToken)
    {
        var rows = await db.FlyDjDecisions.AsNoTracking()
            .OrderByDescending(item => item.DecidedAtUtc)
            .Take(ExplorationLookbackDecisions)
            .Select(item => new { item.DecidedAtUtc, item.CandidatesJson })
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            List<FlyDjCandidateScore>? candidates;
            try { candidates = JsonSerializer.Deserialize<List<FlyDjCandidateScore>>(row.CandidatesJson, Json); }
            catch (JsonException) { continue; }
            foreach (var candidate in candidates ?? [])
            {
                var key = candidate.Url.Trim();
                if (!result.TryGetValue(key, out var seen) || row.DecidedAtUtc > seen) result[key] = row.DecidedAtUtc;
            }
        }
        return result;
    }
}
