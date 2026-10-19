using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public class FlyDjScoringTests
{
    private static readonly DateTimeOffset Moment = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<FlyDjCandidate> Candidates(params double[] priors)
        => priors.Select((prior, i) => new FlyDjCandidate($"st-{i + 1}", $"Station {i + 1}", $"radio{i + 1}.example.com/live", prior, false)).ToList();

    private static FlyDjAuditionResponse Audition(params double?[] scores)
        => new(scores.Select((score, i) => new FlyDjAuditionResult($"st-{i + 1}", score is not null, score is null ? "fetch_failed" : null, score, null, null)).ToList(),
            500, new Dictionary<string, double>());

    [Fact]
    public void FinalScoreBlendsSeventyPercentBrainAndThirtyPercentPrior()
    {
        var candidates = Candidates(1.0, -1.0);
        var choice = FlyDjScoring.Choose(candidates, Audition(2.0, 0.0), Moment);

        // z(brain) of [2, 0] is [+1, -1]; final = 0.7 * z + 0.3 * prior.
        Assert.Equal(0.7 * 1 + 0.3 * 1, choice.Scores[0].FinalScore, 9);
        Assert.Equal(0.7 * -1 + 0.3 * -1, choice.Scores[1].FinalScore, 9);
        Assert.Equal(2.0, choice.Scores[0].BrainScore);
        Assert.Equal(1.0, choice.Scores[0].PriorScore);
        Assert.Equal(0.7, choice.BrainWeight);
        Assert.Equal(0.3, choice.PriorWeight);
    }

    [Fact]
    public void ProbabilitiesFollowASoftmaxAtTemperatureZeroPointThreeFive()
    {
        var choice = FlyDjScoring.Choose(Candidates(1.0, -1.0), Audition(2.0, 0.0), Moment);

        var a = Math.Exp(1.0 / 0.35);
        var b = Math.Exp(-1.0 / 0.35);
        Assert.Equal(a / (a + b), choice.Scores[0].Probability, 9);
        Assert.Equal(1.0, choice.Scores.Sum(score => score.Probability), 9);
    }

    [Fact]
    public void FailedAuditionsAreExcludedFromTheDraw()
    {
        var candidates = Candidates(0, 0, 0);
        for (var minute = 0; minute < 200; minute++)
        {
            var choice = FlyDjScoring.Choose(candidates, Audition(null, 1.0, 0.5), Moment.AddMinutes(minute));
            Assert.NotEqual("st-1", choice.Chosen.Id);
        }
        var scored = FlyDjScoring.Choose(candidates, Audition(null, 1.0, 0.5), Moment);
        Assert.Equal("fetch_failed", scored.Scores[0].Error);
        Assert.Equal(0, scored.Scores[0].Probability);
        Assert.Null(scored.Scores[0].BrainScore);
    }

    [Fact]
    public void WhenEveryAuditionFailsThePriorAloneDecidesAndTheReasonSaysSo()
    {
        var choice = FlyDjScoring.Choose(Candidates(2.0, -1.0, -1.0), Audition(null, null, null), Moment);

        Assert.Equal(0, choice.BrainWeight);
        Assert.Equal(1, choice.PriorWeight);
        Assert.Contains("habits alone", choice.Reason);
        Assert.Contains("fetch_failed", choice.Reason);
        Assert.Equal(1.0, choice.Scores.Sum(score => score.Probability), 9);
        Assert.True(choice.Scores[0].Probability > choice.Scores[1].Probability);
    }

    [Fact]
    public void TheDrawIsSeededByTheDecisionTime()
    {
        var candidates = Candidates(0, 0, 0, 0);
        var audition = Audition(0.1, 0.1, 0.1, 0.1);

        var first = FlyDjScoring.Choose(candidates, audition, Moment).Chosen.Id;
        Assert.Equal(first, FlyDjScoring.Choose(candidates, audition, Moment).Chosen.Id);

        var chosen = Enumerable.Range(0, 120).Select(minute => FlyDjScoring.Choose(candidates, audition, Moment.AddMinutes(minute)).Chosen.Id).Distinct().ToList();
        Assert.Equal(4, chosen.Count);   // equal odds: every station gets picked over enough decisions
    }

    [Fact]
    public void StrongBrainResponseWinsMostDrawsAndTheReasonExplainsWhy()
    {
        var candidates = Candidates(0, 0, 0);
        var audition = Audition(0.0, 0.0, 6.0);
        var wins = Enumerable.Range(0, 200).Count(minute => FlyDjScoring.Choose(candidates, audition, Moment.AddMinutes(minute)).Chosen.Id == "st-3");

        Assert.True(wins > 150, $"won {wins}/200");
        var reason = FlyDjScoring.Choose(candidates, audition, Moment.AddMinutes(1)).Reason;
        Assert.Contains("fly brain scored", reason);
        Assert.Contains("Runner-up", reason);
    }

    [Fact]
    public void SingleSuccessfulAuditionHasNoSpreadAndIsChosen()
    {
        var choice = FlyDjScoring.Choose(Candidates(0, 0), Audition(null, 1.5), Moment);

        Assert.Equal("st-2", choice.Chosen.Id);
        Assert.Equal(1.0, choice.Scores[1].Probability, 9);
    }
}

public class FlyDjStateStoreTests
{
    private static (FlyDjStateStore Store, Mock<IFlyDjClient> Client, FlyDjTestClock Time) Create(Func<FlyDjModelInfo>? model = null)
    {
        var client = new Mock<IFlyDjClient>();
        client.Setup(c => c.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(FlyDjConductorFixture.State(true));
        client.Setup(c => c.GetModelAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult((model ?? (() => FlyDjConductorFixture.ReadyModel()))()));
        var time = new FlyDjTestClock(FlyDjConductorFixture.StartTime);
        return (new FlyDjStateStore(client.Object, time, NullLogger<FlyDjStateStore>.Instance), client, time);
    }

    private static int Count(Mock<IFlyDjClient> client, string method) => client.Invocations.Count(invocation => invocation.Method.Name == method);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task NothingPollsWithoutViewersAndStateStartsStopped()
    {
        var (store, client, time) = Create();
        using var _ = store;

        time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(50);

        Assert.Empty(client.Invocations);
        Assert.Equal(FlyDjConductorStatus.Stopped, store.Current.Status);
        Assert.Equal(0, store.ViewerCount);
    }

    [Fact]
    public async Task ViewersStartPollingStateEveryTwoSecondsAndModelEveryThirty()
    {
        var (store, client, time) = Create();
        using var _ = store;
        var changes = 0;
        store.Changed += () => Interlocked.Increment(ref changes);

        using var viewer = store.AttachViewer();
        // The state is published after the client calls return, so wait for the published model, not the calls.
        await UntilAsync(() => Count(client, "GetStateAsync") == 1 && Count(client, "GetModelAsync") == 1 && store.Current.Model is not null);
        Assert.True(store.Current.SidecarReachable);
        Assert.Equal("bursting", store.Current.Simulation!.Mode);
        Assert.Equal("ready", store.Current.Model!.Status);
        Assert.True(changes > 0);

        for (var tick = 1; tick <= 14; tick++)
        {
            var expected = Count(client, "GetStateAsync") + 1;
            await time.AdvanceWhenParkedAsync(TimeSpan.FromSeconds(2));
            await UntilAsync(() => Count(client, "GetStateAsync") == expected);
        }

        Assert.Equal(15, Count(client, "GetStateAsync"));       // one per 2 s over 28 s plus the first
        Assert.Equal(1, Count(client, "GetModelAsync"));        // model is only refetched every 30 s once ready
        await time.AdvanceWhenParkedAsync(TimeSpan.FromSeconds(2));
        await UntilAsync(() => Count(client, "GetModelAsync") == 2);
    }

    [Fact]
    public async Task ModelIsPolledEveryTickUntilItIsReady()
    {
        var ready = false;
        var (store, client, time) = Create(() => FlyDjConductorFixture.ReadyModel(ready ? "ready" : "building", ready ? 1 : 0.3));
        using var _ = store;

        using var viewer = store.AttachViewer();
        await UntilAsync(() => store.Current.Model?.Status == "building");
        await AdvanceUntilAsync(time, () => Count(client, "GetModelAsync") >= 2);
        Assert.Equal("building", store.Current.Model!.Status);

        ready = true;
        await AdvanceUntilAsync(time, () => store.Current.Model!.Status == "ready");
        var fetched = Count(client, "GetModelAsync");
        var states = Count(client, "GetStateAsync");
        await AdvanceUntilAsync(time, () => Count(client, "GetStateAsync") > states);
        Assert.Equal(fetched, Count(client, "GetModelAsync"));
    }

    // The refresher registers its next delay only after a tick finishes, so an advance sent too early
    // is lost. Advance one poll interval at a time, and only while the refresher is waiting on the clock.
    private static async Task AdvanceUntilAsync(FlyDjTestClock time, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            if (time.PendingTimers > 0) time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task PollingStopsWhenTheLastViewerLeavesAndResumesForTheNext()
    {
        var (store, client, time) = Create();
        using var _ = store;
        var first = store.AttachViewer();
        var second = store.AttachViewer();
        await UntilAsync(() => Count(client, "GetStateAsync") >= 1 && Count(client, "GetModelAsync") >= 1);   // the first tick has finished

        first.Dispose();
        first.Dispose();   // idempotent
        Assert.Equal(1, store.ViewerCount);
        second.Dispose();
        var calls = client.Invocations.Count;
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(80);
        Assert.Equal(calls, client.Invocations.Count);

        using var third = store.AttachViewer();
        await UntilAsync(() => client.Invocations.Count > calls);
    }

    [Fact]
    public async Task UnreachableSidecarMarksTheStateAndKeepsTheLastModel()
    {
        var (store, client, time) = Create();
        using var _ = store;
        await store.RefreshAsync();
        Assert.True(store.Current.SidecarReachable);

        client.Setup(c => c.GetStateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new FlyDjUnavailableException("refused"));
        await store.RefreshAsync();

        Assert.False(store.Current.SidecarReachable);
        Assert.Null(store.Current.Simulation);
        Assert.NotNull(store.Current.Model);
    }

    [Fact]
    public void UpdateStampsTheTimeAndRaisesChanged()
    {
        var (store, _, time) = Create();
        using var _ = store;
        var raised = 0;
        store.Changed += () => raised++;
        time.Advance(TimeSpan.FromMinutes(1));

        store.Update(state => state with { Status = FlyDjConductorStatus.Playing, StatusMessage = "x" });

        Assert.Equal(1, raised);
        Assert.Equal(FlyDjConductorStatus.Playing, store.Current.Status);
        Assert.Equal(time.GetUtcNow(), store.Current.UpdatedUtc);
    }
}
