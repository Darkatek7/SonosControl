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
    public double ExcludedOverlapSeconds { get; init; }
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
        var rows = new List<(PlaybackHistory Playback, string Room, ResolvedRecommendationMedia Media, string Source, double Seconds)>();
        var intervals = new List<(PlaybackHistory Playback, string Room, DateTime Start, DateTime End)>();

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
            if (playback.EndTime is { } savedEnd && savedEnd < recordedEnd)
            {
                recordedEnd = savedEnd;
            }
            var start = playback.StartTime > boundaries[0] ? playback.StartTime : boundaries[0];
            var end = recordedEnd < nowUtc ? recordedEnd : nowUtc;
            if (end <= start)
            {
                continue;
            }

            var room = string.IsNullOrWhiteSpace(playback.SpeakerName) ? "Unassigned room" : playback.SpeakerName.Trim();
            intervals.Add((playback, room, start, end));
        }

        // Sweep each room's interval boundaries. A speaker can contribute time only once
        // at any instant; the newest active entry supplies the media attribution.
        foreach (var room in intervals.GroupBy(interval => interval.Room, StringComparer.OrdinalIgnoreCase))
        {
            var entries = room.ToArray();
            var events = entries.SelectMany((entry, index) => new[]
            {
                (At: entry.Start, IsStart: true, Index: index),
                (At: entry.End, IsStart: false, Index: index)
            }).OrderBy(item => item.At).ToArray();
            var active = new SortedSet<int>(Comparer<int>.Create((left, right) =>
            {
                var order = entries[left].Playback.StartTime.CompareTo(entries[right].Playback.StartTime);
                if (order == 0) order = entries[left].Playback.Id.CompareTo(entries[right].Playback.Id);
                return order != 0 ? order : left.CompareTo(right);
            }));
            var previous = events[0].At;
            foreach (var boundary in events.GroupBy(item => item.At))
            {
                if (boundary.Key > previous && active.Count > 0)
                {
                    var playback = entries[active.Max].Playback;
                    AddSegment(playback, room.Key, previous, boundary.Key);
                }
                foreach (var item in boundary)
                {
                    if (item.IsStart) active.Add(item.Index);
                    else active.Remove(item.Index);
                }
                previous = boundary.Key;
            }
        }

        void AddSegment(PlaybackHistory playback, string room, DateTime start, DateTime end)
        {
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
            rows.Add((playback, room, media, source, (end - start).TotalSeconds));
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
            ExcludedOverlapSeconds = Math.Max(0, intervals.Sum(interval => (interval.End - interval.Start).TotalSeconds) - totals.Sum()),
            Rooms = rows.GroupBy(row => row.Room)
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
