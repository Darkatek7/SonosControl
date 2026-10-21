namespace SonosControl.Web.Services.FlyDj;

/// <summary>Sampling and comparison cadence scales with the configured dwell, not wall-clock schedule length.</summary>
public sealed record FlyDjObservationPlan(TimeSpan Window, TimeSpan SampleInterval, TimeSpan ComparisonInterval)
{
    public const int RequiredSamples = 6;
    public const double SwitchMargin = 0.3;
    public static FlyDjObservationPlan FromMinutes(int minimum)
    {
        var window = TimeSpan.FromSeconds(Math.Clamp(minimum * 10.0, 30, 600));
        return new(window, window / 10, TimeSpan.FromSeconds(Math.Clamp(minimum * 5.0, 5, 300)));
    }
    public double SampleSeconds(int candidates) => Math.Clamp(SampleInterval.TotalSeconds / (Math.Ceiling(candidates / 2.0) + 1), 0.8, 10);
    public FlyDjObservationPlan WithRoundDuration(TimeSpan duration)
    {
        // A complete round must fit the cadence, even with many slow streams.
        var interval = TimeSpan.FromTicks(Math.Max(SampleInterval.Ticks, (long)(duration.Ticks * 1.25)));
        return this with { SampleInterval = interval, Window = TimeSpan.FromTicks(Math.Max(Window.Ticks, interval.Ticks * 10)) };
    }
}

public sealed record FlyDjStationEvidence(string Name, string Url, int SampleCount, int FailureCount,
    DateTimeOffset? LastSampleAtUtc, double? MedianScore, bool Ready, string? Error)
{
    public double? ScoreVariation { get; init; }
}

/// <summary>Bounded rolling evidence. Cached samples are never counted twice, even under another request id.</summary>
public sealed class FlyDjObservationWindow
{
    private sealed record Observation(string Id, DateTimeOffset At, double? Score, FlyDjAudioFeatures? Features, string? Error);
    private readonly Dictionary<string, List<Observation>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.OrdinalIgnoreCase);
    public void Clear() { _samples.Clear(); _seen.Clear(); }
    public void Retain(IEnumerable<string> urls)
    {
        var keep = urls.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _samples.Keys.Where(key => !keep.Contains(key)).ToArray()) { _samples.Remove(key); _seen.Remove(key); }
    }
    public void Add(string url, FlyDjAuditionResult result, DateTimeOffset receivedAt, FlyDjObservationPlan plan)
    {
        if (!_samples.TryGetValue(url, out var items)) _samples[url] = items = [];
        if (!_seen.TryGetValue(url, out var seen)) _seen[url] = seen = new(StringComparer.Ordinal);
        if (result.SampleId is not { Length: > 0 } id || result.SampledAtUtc is not { } at
            || at > receivedAt.AddSeconds(5) || at < receivedAt - plan.Window || !seen.Add(id)) return;
        var valid = result.Ok && result.Score is { } score && double.IsFinite(score);
        items.Add(new(id, at, valid ? result.Score : null, result.Features, valid ? null : result.Error ?? "audition failed"));
        // Leave a bounded identity tombstone so expired cached responses cannot become new evidence.
        if (seen.Count > 256) { seen.Clear(); foreach (var item in items.TakeLast(128)) seen.Add(item.Id); }
        items.RemoveAll(item => item.At < receivedAt - plan.Window);
    }
    public (FlyDjAuditionResponse Audition, IReadOnlyList<FlyDjStationEvidence> Evidence) Summarize(
        IReadOnlyList<FlyDjCandidate> candidates, DateTimeOffset now, FlyDjObservationPlan plan)
    {
        var results = new List<FlyDjAuditionResult>();
        var evidence = new List<FlyDjStationEvidence>();
        foreach (var candidate in candidates)
        {
            var items = _samples.GetValueOrDefault(candidate.Url) ?? [];
            items.RemoveAll(item => item.At < now - plan.Window);
            var valid = items.Where(item => item.Score.HasValue).OrderBy(item => item.Score).ToArray();
            var last = valid.Length == 0 ? null : valid.MaxBy(item => item.At);
            var fresh = last is not null && now - last.At <= plan.SampleInterval * 3;
            var ready = valid.Length >= FlyDjObservationPlan.RequiredSamples && fresh && items.MaxBy(item => item.At)?.Score is not null;
            var median = valid.Length == 0 ? (double?)null : (valid[(valid.Length - 1) / 2].Score!.Value + valid[valid.Length / 2].Score!.Value) / 2;
            var error = ready ? null : items.MaxBy(item => item.At)?.Error is not null ? "Latest audio probe failed"
                : !fresh ? "No fresh audio measurement" : $"Collecting samples ({valid.Length}/{FlyDjObservationPlan.RequiredSamples})";
            var variation = median is null ? (double?)null : Median(valid.Select(item => Math.Abs(item.Score!.Value - median.Value))) * 1.4826;
            var failures = items.Count(item => item.Score is null);
            if (!ready && items.LastOrDefault(item => item.Error is not null)?.Error is { } failure) error += $"; last error: {failure}";
            evidence.Add(new(candidate.Name, candidate.Url, valid.Length, failures, last?.At, median, ready, error) { ScoreVariation = variation });
            results.Add(new(candidate.Id, ready, error, ready ? median : null, last?.Features, null) { ScoreVariation = variation });
        }
        return (new(results, 500, new Dictionary<string, double>()), evidence);
    }
    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

}
