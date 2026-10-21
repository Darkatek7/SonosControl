using Microsoft.Extensions.Time.Testing;
using Moq;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public sealed class FlyDjMeasurementPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RobustVariationDistinguishesFluctuatingAudioFromOneIsolatedOutlier()
    {
        var plan = FlyDjObservationPlan.FromMinutes(60);
        FlyDjStationEvidence Measure(double[] scores)
        {
            var window = new FlyDjObservationWindow();
            for (var i = 0; i < scores.Length; i++) window.Add("url", new("a", true, null, scores[i], null, null)
            { SampleId = i.ToString(), SampledAtUtc = Now.AddSeconds(i) }, Now.AddSeconds(i), plan);
            return Assert.Single(window.Summarize([new("a", "Station", "url", 0, false)], Now.AddSeconds(10), plan).Evidence);
        }
        Assert.Equal(0, Measure([1, 1, 1, 1, 1, 50]).ScoreVariation);
        Assert.Equal(2.2239, Measure([0, 1, 2, 3, 4, 5]).ScoreVariation!.Value, 4);
    }

    [Fact]
    public void EarlyLeadMustExceedCombinedVariationWithoutChangingTheSeventyThirtyWeights()
    {
        FlyDjCandidateScore Score(double final, double variation) => new("s", "url", 1, 0, final, 1, null, null) { FinalVariation = variation };
        Assert.True(FlyDjScoring.HasStableLead(Score(0, 0), Score(.4, 0)));
        Assert.False(FlyDjScoring.HasStableLead(Score(0, .3), Score(.4, .3)));
        Assert.False(FlyDjScoring.HasStableLead(Score(0, 0), Score(.2, 0)));
        Assert.True(FlyDjScoring.HasStableLead(Score(0, .3), Score(.7, .3)));
        var candidates = new[] { new FlyDjCandidate("a", "A", "a", 2, false), new FlyDjCandidate("b", "B", "b", -2, false) };
        var audition = new FlyDjAuditionResponse([new("a", true, null, 1, null, null) { ScoreVariation = .5 }, new("b", true, null, 3, null, null) { ScoreVariation = .5 }], 500, new Dictionary<string, double>());
        var choice = FlyDjScoring.Choose(candidates, audition, Now);
        Assert.Equal(.7, choice.BrainWeight); Assert.Equal(.3, choice.PriorWeight);
        Assert.Equal(-.1, choice.Scores[0].FinalScore, 8);
        Assert.Equal(.35, choice.Scores[0].FinalVariation, 8);
    }

    [Fact]
    public void SamplingRotatesInitialRoundsAndKeepsWeakStationsInTheFreshEvidenceWindow()
    {
        var schedule = new FlyDjSamplingSchedule();
        var plan = FlyDjObservationPlan.FromMinutes(60);
        var candidates = Enumerable.Range(1, 5).Select(i => new FlyDjCandidate(i.ToString(), i.ToString(), i.ToString(), 0, false)).ToArray();
        var priority = new HashSet<string>(["1", "2"]);
        Assert.Equal(candidates, schedule.Due(candidates, priority, [], plan, Now, false));
        foreach (var candidate in candidates) schedule.Record(candidate.Url, Now);
        var initial = schedule.Due(candidates, priority, [], plan, Now.AddMinutes(1), false);
        Assert.Equal(5, initial.Count); Assert.NotEqual("1", initial[0].Url);
        var evidence = candidates.Select(candidate => new FlyDjStationEvidence(candidate.Name, candidate.Url, 10, 0, Now, 1, true, null)).ToArray();
        var frequent = schedule.Due(candidates, priority, evidence, plan, Now.AddMinutes(1), true);
        Assert.Equal(new[] { "1", "2" }, frequent.Select(candidate => candidate.Url).Order());
        Assert.Equal(5, schedule.Due(candidates, priority, evidence, plan, Now.AddSeconds(90), true).Count);
        var thin = evidence.Select(item => item with { SampleCount = 6 }).ToArray();
        Assert.Equal(5, schedule.Due(candidates, priority, thin, plan, Now.AddMinutes(1), true).Count);
    }

    [Fact]
    public async Task SharedProbesKeepTheirIdentityAcrossContextsAndOnlyTheSelectedContextDrivesThePicture()
    {
        var time = new FakeTimeProvider(Now);
        var client = new Mock<IFlyDjClient>();
        client.Setup(item => item.StartAsync(It.IsAny<CancellationToken>())).ReturnsAsync(FlyDjConductorFixture.State(true));
        client.Setup(item => item.StopAsync(It.IsAny<CancellationToken>())).ReturnsAsync(FlyDjConductorFixture.State(false));
        client.Setup(item => item.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(FlyDjConductorFixture.State(true));
        client.Setup(item => item.ListenAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new FlyDjListenResponse(true, null, null, null, null));
        client.Setup(item => item.AuditionAsync(It.IsAny<FlyDjAuditionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((FlyDjAuditionRequest request, CancellationToken _) => Task.FromResult(new FlyDjAuditionResponse(
                request.Candidates.Select(candidate => new FlyDjAuditionResult(candidate.Id, true, null, 1, null, null)
                { SampleId = Guid.NewGuid().ToString(), SampledAtUtc = time.GetUtcNow() }).ToArray(), 500, new Dictionary<string, double>())));
        var shared = new FlyDjSharedSidecar(client.Object, time);
        var one = shared.ForSession("one"); var two = shared.ForSession("two");
        await one.StartAsync(); await two.StartAsync();
        FlyDjAuditionRequest Request(string id) => new([new(id, "https://same.example/live")]) { Fresh = true, ReuseWithin = TimeSpan.FromMinutes(1) };
        var results = await Task.WhenAll(one.AuditionAsync(Request("a")), two.AuditionAsync(Request("b")));
        Assert.Equal(results[0].Results[0].SampleId, results[1].Results[0].SampleId);
        Assert.Equal("b", results[1].Results[0].Id);
        client.Verify(item => item.AuditionAsync(It.IsAny<FlyDjAuditionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        await one.ListenAsync("first"); await two.ListenAsync("second");
        client.Verify(item => item.ListenAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        await shared.SelectAsync("two");
        client.Verify(item => item.ListenAsync("second", It.IsAny<CancellationToken>()), Times.Once);
        await one.StopAsync();
        client.Verify(item => item.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
        time.Advance(TimeSpan.FromMinutes(1));
        var next = await two.AuditionAsync(Request("b"));
        Assert.NotEqual(results[0].Results[0].SampleId, next.Results[0].SampleId);
        await two.StopAsync();
        client.Verify(item => item.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
