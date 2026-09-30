using SonosControl.DAL.Models;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services;

public sealed record ListeningDay(DateTime Date, double Seconds);
public sealed record ListeningBreakdown(string Name, double Seconds);
public sealed record ListeningContent(string Name, string Artist, double Seconds);

public sealed class ListeningInsights
{
    public required IReadOnlyList<ListeningDay> Days { get; init; }
    public required IReadOnlyList<ListeningBreakdown> Rooms { get; init; }
    public required IReadOnlyList<ListeningBreakdown> Sources { get; init; }
    public required IReadOnlyList<ListeningContent> Stations { get; init; }
    public required IReadOnlyList<ListeningContent> Tracks { get; init; }
    public double TotalSeconds => Days.Sum(day => day.Seconds);
    public int ActiveDays => Days.Count(day => day.Seconds > 0);
    public double AveragePerActiveDay => ActiveDays > 0 ? TotalSeconds / ActiveDays : 0;
    public ListeningDay? PeakDay => Days.Where(day => day.Seconds > 0).OrderByDescending(day => day.Seconds).FirstOrDefault();

    public static ListeningInsights Create(
        IEnumerable<PlaybackHistory> history,
        SonosSettings settings,
        ConfiguredTimeZoneService timeZone,
        DateTime nowUtc,
        int dayCount)
    {
        var firstDate = timeZone.ConvertUtc(nowUtc).Date.AddDays(1 - dayCount);
        var days = Enumerable.Range(0, dayCount).Select(index => firstDate.AddDays(index)).ToArray();
        var boundaries = days.Select(day => timeZone.FromLocal(day).UtcDateTime).Append(nowUtc).ToArray();
        var totals = new double[dayCount];
        var resolver = RecommendationMediaResolver.Create(settings);
        var rows = new List<(PlaybackHistory Playback, ResolvedRecommendationMedia Media, string Source, double Seconds)>();

        foreach (var playback in history)
        {
            if (!double.IsFinite(playback.DurationSeconds) || playback.DurationSeconds <= 0)
            {
                continue;
            }

            // Use only persisted listening time. Never extend an old open entry to the present.
            var recordedSeconds = Math.Min(playback.DurationSeconds, (nowUtc - playback.StartTime).TotalSeconds);
            if (recordedSeconds <= 0)
            {
                continue;
            }
            var recordedEnd = playback.StartTime.AddSeconds(recordedSeconds);
            var start = playback.StartTime > boundaries[0] ? playback.StartTime : boundaries[0];
            var end = recordedEnd < nowUtc ? recordedEnd : nowUtc;
            if (end <= start)
            {
                continue;
            }

            for (var index = 0; index < dayCount; index++)
            {
                var overlapStart = start > boundaries[index] ? start : boundaries[index];
                var overlapEnd = end < boundaries[index + 1] ? end : boundaries[index + 1];
                totals[index] += Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
            }

            var media = resolver.ResolveFromPlaybackEntry(playback.TrackName, playback.Artist, playback.MediaType);
            var source = media.MediaType.ToLowerInvariant() switch
            {
                "station" when media.MatchedCatalog || playback.MediaType.Equals("Station", StringComparison.OrdinalIgnoreCase) => "Radio",
                "station" or "stream" => "Other streams",
                "spotify" => "Spotify",
                "youtube" => "YouTube",
                "youtubemusic" or "youtube music" => "YouTube Music",
                "unknown" or "" => "Unidentified audio",
                _ => "Other audio"
            };
            rows.Add((playback, media, source, (end - start).TotalSeconds));
        }

        IReadOnlyList<ListeningContent> Content(bool stations) => rows
            .Where(row => stations ? row.Source == "Radio" : row.Source is not ("Radio" or "Other streams" or "Unidentified audio"))
            .Where(row => !string.IsNullOrWhiteSpace(row.Media.Name))
            .Where(row => row.Media.MatchedCatalog || !RecommendationMediaResolver.IsTechnicalPlaybackDisplayName(row.Media.Name, row.Playback.Artist))
            .GroupBy(row => new
            {
                row.Media.Name,
                Artist = stations || RecommendationMediaResolver.IsTechnicalPlaybackDisplayName(row.Playback.Artist, "")
                    ? "" : row.Playback.Artist.Trim()
            })
            .Select(group => new ListeningContent(group.Key.Name, group.Key.Artist, group.Sum(row => row.Seconds)))
            .OrderByDescending(item => item.Seconds).ThenBy(item => item.Name)
            .Take(8).ToList();

        return new ListeningInsights
        {
            Days = days.Select((date, index) => new ListeningDay(date, totals[index])).ToList(),
            Rooms = rows.GroupBy(row => string.IsNullOrWhiteSpace(row.Playback.SpeakerName) ? "Unassigned room" : row.Playback.SpeakerName.Trim())
                .Select(group => new ListeningBreakdown(group.Key, group.Sum(row => row.Seconds)))
                .OrderByDescending(item => item.Seconds).ThenBy(item => item.Name).ToList(),
            Sources = rows.GroupBy(row => row.Source)
                .Select(group => new ListeningBreakdown(group.Key, group.Sum(row => row.Seconds)))
                .OrderByDescending(item => item.Seconds).ThenBy(item => item.Name).ToList(),
            Stations = Content(true),
            Tracks = Content(false)
        };
    }
}
