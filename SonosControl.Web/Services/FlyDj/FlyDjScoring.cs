using System.Globalization;

namespace SonosControl.Web.Services.FlyDj;

public sealed record FlyDjScoredChoice(
    FlyDjCandidate Chosen,
    IReadOnlyList<FlyDjCandidateScore> Scores,
    string Reason,
    double BrainWeight,
    double PriorWeight);

/// <summary>
/// Combines the fly brain's audition scores with the behaviour prior and draws a station.
/// final = 0.7 * z(brain) + 0.3 * prior, then a softmax at temperature 0.35 and a seeded draw.
/// Stations whose audition failed are excluded; when every audition failed the prior alone decides.
/// </summary>
public static class FlyDjScoring
{
    public const double BrainWeight = 0.7;
    public const double PriorWeight = 0.3;
    public const double Temperature = 0.35;

    public static (int Index, double[] Probabilities, double[] Finals, double[] BrainZ, bool BrainUsed)
        Score(IReadOnlyList<FlyDjCandidate> candidates, FlyDjAuditionResponse audition, DateTimeOffset decidedAt)
    {
        var byId = audition.Results.GroupBy(result => result.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var okIndexes = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
            if (byId.TryGetValue(candidates[i].Id, out var result) && result is { Ok: true, Score: not null }
                && double.IsFinite(result.Score.Value))
                okIndexes.Add(i);

        var brainUsed = okIndexes.Count > 0;
        var brainZ = new double[candidates.Count];
        var finals = new double[candidates.Count];
        var probabilities = new double[candidates.Count];
        var eligible = brainUsed ? okIndexes : Enumerable.Range(0, candidates.Count).ToList();

        if (brainUsed)
        {
            var z = FlyDjCandidateProvider.ZNormalize(okIndexes.Select(i => byId[candidates[i].Id].Score!.Value).ToArray());
            for (var k = 0; k < okIndexes.Count; k++) brainZ[okIndexes[k]] = z[k];
        }
        foreach (var i in eligible)
            finals[i] = brainUsed ? BrainWeight * brainZ[i] + PriorWeight * candidates[i].Prior : candidates[i].Prior;

        var max = eligible.Max(i => finals[i]);
        var sum = eligible.Sum(i => Math.Exp((finals[i] - max) / Temperature));
        foreach (var i in eligible) probabilities[i] = Math.Exp((finals[i] - max) / Temperature) / sum;

        // Seeded draw: reproducible for a given decision time, yet different from one decision to the next.
        var seed = unchecked((int)(decidedAt.UtcTicks ^ (decidedAt.UtcTicks >> 32)));
        var draw = new Random(seed).NextDouble();
        var cumulative = 0.0;
        var index = eligible[^1];
        foreach (var i in eligible)
        {
            cumulative += probabilities[i];
            if (draw < cumulative) { index = i; break; }
        }
        return (index, probabilities, finals, brainZ, brainUsed);
    }

    public static FlyDjScoredChoice Choose(IReadOnlyList<FlyDjCandidate> candidates, FlyDjAuditionResponse audition, DateTimeOffset decidedAt)
    {
        var scored = Score(candidates, audition, decidedAt);
        var byId = audition.Results.GroupBy(result => result.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var scores = new List<FlyDjCandidateScore>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            byId.TryGetValue(candidates[i].Id, out var result);
            var ok = result is { Ok: true, Score: not null } && double.IsFinite(result.Score.Value);
            scores.Add(new FlyDjCandidateScore(
                candidates[i].Name, candidates[i].Url,
                ok ? result!.Score : null,
                candidates[i].Prior,
                scored.Finals[i],
                scored.Probabilities[i],
                result?.Features,
                ok ? null : result?.Error ?? (result is null ? "not auditioned" : "audition failed")));
        }
        var chosen = candidates[scored.Index];
        var reason = BuildReason(candidates, scores, scored.Index, scored.BrainUsed);
        return new FlyDjScoredChoice(chosen, scores, reason, scored.BrainUsed ? BrainWeight : 0, scored.BrainUsed ? PriorWeight : 1);
    }

    private static string BuildReason(IReadOnlyList<FlyDjCandidate> candidates, IReadOnlyList<FlyDjCandidateScore> scores, int index, bool brainUsed)
    {
        var ci = CultureInfo.InvariantCulture;
        var pick = scores[index];
        var parts = new List<string>();
        if (brainUsed)
        {
            var brainRank = scores.Select((score, i) => (score, i)).Where(item => item.score.BrainScore is not null)
                .OrderByDescending(item => item.score.BrainScore).Select(item => item.i).ToList();
            parts.Add($"Chose {pick.Name} ({pick.Probability.ToString("P0", ci)} chance).");
            parts.Add(string.Format(ci, "The fly brain scored it {0:+0.00;-0.00;0.00}, ranking {1} of {2} audited stations.",
                pick.BrainScore ?? 0, brainRank.IndexOf(index) + 1, brainRank.Count));
            parts.Add(string.Format(ci, "Listening habits add {0:+0.00;-0.00;0.00}.", pick.PriorScore));
            if (candidates[index].IsExploration) parts.Add("It was an exploration pick that had not been auditioned recently.");
            var failed = scores.Count(score => score.Error is not null);
            if (failed > 0) parts.Add($"{failed} station(s) could not be auditioned and were skipped.");
            var runnerUp = scores.Select((score, i) => (score, i)).Where(item => item.i != index && item.score.Error is null)
                .OrderByDescending(item => item.score.Probability).Select(item => item.score).FirstOrDefault();
            if (runnerUp is not null) parts.Add($"Runner-up: {runnerUp.Name} ({runnerUp.Probability.ToString("P0", ci)}).");
        }
        else
        {
            parts.Add($"Chose {pick.Name} ({pick.Probability.ToString("P0", ci)} chance) from listening habits alone.");
            parts.Add("No station could be auditioned by the fly brain" +
                (scores.Select(score => score.Error).FirstOrDefault(error => error is not null) is { } first ? $" ({first})." : "."));
        }
        return string.Join(' ', parts);
    }
}
