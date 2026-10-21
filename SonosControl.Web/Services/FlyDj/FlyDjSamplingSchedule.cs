namespace SonosControl.Web.Services.FlyDj;

/// <summary>Rotate fair initial rounds, then retain fresh coverage while prioritising the current station and challengers.</summary>
internal sealed class FlyDjSamplingSchedule
{
    private readonly Dictionary<string, DateTimeOffset> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private int _round;
    public void Clear() { _attempted.Clear(); _round = 0; }
    public void Record(string url, DateTimeOffset at) => _attempted[url] = at;
    private static TimeSpan Interval(string url, IReadOnlySet<string> priority, IReadOnlyList<FlyDjStationEvidence> evidence, FlyDjObservationPlan plan, bool playing)
    {
        var station = evidence.FirstOrDefault(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        return playing && !priority.Contains(url) && station is { Ready: true, SampleCount: >= 8 }
            ? plan.SampleInterval * 1.5 : plan.SampleInterval;
    }
    public IReadOnlyList<FlyDjCandidate> Due(IReadOnlyList<FlyDjCandidate> candidates, IReadOnlySet<string> priority,
        IReadOnlyList<FlyDjStationEvidence> evidence, FlyDjObservationPlan plan, DateTimeOffset now, bool playing)
    {
        foreach (var key in _attempted.Keys.Where(key => !candidates.Any(candidate => candidate.Url.Equals(key, StringComparison.OrdinalIgnoreCase))).ToArray()) _attempted.Remove(key);
        var due = candidates.Where(candidate => !_attempted.TryGetValue(candidate.Url, out var at)
            || now >= at + Interval(candidate.Url, priority, evidence, plan, playing)).ToArray();
        if (due.Length == 0) return due;
        var offset = _round++ % due.Length;
        return due.Skip(offset).Concat(due.Take(offset)).ToArray();
    }
    public DateTimeOffset Next(IReadOnlyList<FlyDjCandidate> candidates, IReadOnlySet<string> priority,
        IReadOnlyList<FlyDjStationEvidence> evidence, FlyDjObservationPlan plan, DateTimeOffset now, bool playing)
        => candidates.Min(candidate => _attempted.GetValueOrDefault(candidate.Url, now) + Interval(candidate.Url, priority, evidence, plan, playing));
}
