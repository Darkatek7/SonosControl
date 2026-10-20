using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public sealed class FlyDjObservationWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);
    private static readonly FlyDjCandidate Station = new("a", "Station", "https://radio.example/live", 0, false);

    [Theory]
    [InlineData(1, 30, 3, 5)]
    [InlineData(6, 60, 6, 30)]
    [InlineData(30, 300, 30, 150)]
    [InlineData(60, 600, 60, 300)]
    [InlineData(240, 600, 60, 300)]
    public void CadenceScalesAndCapsWithMinimumDwell(int minimum, int window, int sampling, int comparison)
    {
        var plan = FlyDjObservationPlan.FromMinutes(minimum);
        Assert.Equal(TimeSpan.FromSeconds(window), plan.Window);
        Assert.Equal(TimeSpan.FromSeconds(sampling), plan.SampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(comparison), plan.ComparisonInterval);
        Assert.InRange(plan.SampleSeconds(2), 0.8, 10);
        Assert.InRange(plan.SampleSeconds(100), 0.8, 10);
    }

    [Fact]
    public void CachedDuplicateCannotCreateConfidenceAndAStaleSampleCannotBecomeFresh()
    {
        var window = new FlyDjObservationWindow();
        var plan = FlyDjObservationPlan.FromMinutes(60);
        var sample = Sample("cached", Now, 3);
        for (var i = 0; i < 10; i++) window.Add(Station.Url, sample, Now.AddSeconds(i), plan);
        var one = Assert.Single(window.Summarize([Station], Now.AddSeconds(10), plan).Evidence);
        Assert.Equal(1, one.SampleCount);
        Assert.False(one.Ready);
        for (var i = 1; i < 6; i++) window.Add(Station.Url, Sample($"s{i}", Now.AddSeconds(i), 3), Now.AddSeconds(i), plan);
        Assert.True(Assert.Single(window.Summarize([Station], Now.AddSeconds(10), plan).Evidence).Ready);
        var stale = Assert.Single(window.Summarize([Station], Now.AddMinutes(4), plan).Evidence);
        Assert.Equal(6, stale.SampleCount);
        Assert.False(stale.Ready);
        Assert.Null(Assert.Single(window.Summarize([Station], Now.AddMinutes(11), plan).Audition.Results).Score);
    }

    [Fact]
    public void MedianRejectsAnIsolatedOutlierAndFailuresDoNotCountAsSuccessfulSamples()
    {
        var window = new FlyDjObservationWindow();
        var plan = FlyDjObservationPlan.FromMinutes(60);
        for (var i = 0; i < 7; i++) window.Add(Station.Url, Sample($"s{i}", Now.AddSeconds(i), i == 6 ? -100 : 2), Now.AddSeconds(i), plan);
        window.Add(Station.Url, Sample("failed", Now.AddSeconds(8), 0) with { Ok = false, Error = "timeout" }, Now.AddSeconds(8), plan);
        var evidence = Assert.Single(window.Summarize([Station], Now.AddSeconds(9), plan).Evidence);
        Assert.Equal(2, evidence.MedianScore);
        Assert.Equal(7, evidence.SampleCount);
        Assert.Equal(1, evidence.FailureCount);
        Assert.False(evidence.Ready); // a failed latest probe cannot qualify an alternative for playback
        window.Add(Station.Url, Sample("recovered", Now.AddSeconds(10), 2), Now.AddSeconds(10), plan);
        Assert.True(Assert.Single(window.Summarize([Station], Now.AddSeconds(11), plan).Evidence).Ready);
        window.Retain([]);
        Assert.Equal(0, Assert.Single(window.Summarize([Station], Now, plan).Evidence).SampleCount);
    }

    private static FlyDjAuditionResult Sample(string id, DateTimeOffset at, double score) =>
        new("a", true, null, score, null, null) { SampleId = id, SampledAtUtc = at };
}
