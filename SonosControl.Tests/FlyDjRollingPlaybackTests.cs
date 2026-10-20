using SonosControl.DAL.Models;
using Moq;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public sealed class FlyDjRollingPlaybackTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(60)]
    public async Task InitialObservationScalesAndSendsNoPlaybackBeforeItsDeadline(int minimum)
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
        { settings.FlyDj.MinDwellMinutes = minimum; settings.FlyDj.MaxDwellMinutes = minimum * 2; });
        fixture.AutoAdvanceObservation = false;
        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.ObservationEndsAtUtc is not null, parked: true);
        var plan = FlyDjObservationPlan.FromMinutes(minimum);
        Assert.Equal(FlyDjConductorFixture.StartTime + plan.Window, fixture.Store.Current.ObservationEndsAtUtc);
        await fixture.AdvanceSamplingAsync(plan.Window - plan.SampleInterval);
        Assert.Empty(fixture.SceneCalls);
        await fixture.StepAsync(plan.SampleInterval);
        Assert.Single(fixture.SceneCalls);
        Assert.All(fixture.SceneCalls, call => Assert.Equal(SonosControl.Web.Services.PlaybackCommandOrigin.Automation, call.LeaseOrigin));
        Assert.All(fixture.Client.Invocations.Where(call => call.Method.Name == nameof(IFlyDjClient.AuditionAsync)), call =>
        {
            var request = Assert.IsType<FlyDjAuditionRequest>(call.Arguments[0]);
            Assert.True(request.Fresh);
            Assert.InRange(request.Candidates.Count, 1, 2);
            Assert.InRange(request.SampleSeconds, 0.8, 10);
        });
        Assert.All(fixture.Store.Current.Evidence, evidence => Assert.True(evidence.Ready));
        Assert.True(fixture.Store.Current.NextDecisionAtUtc >= fixture.Time.GetUtcNow().AddMinutes(minimum));
    }

    [Fact]
    public async Task AllAllowedStationsAreMeasuredIncludingThoseOutsideTheOldSixStationShortlist()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
        {
            settings.FlyDj.MinDwellMinutes = 1;
            settings.Stations = Enumerable.Range(1, 11).Select(i => new TuneInStation { Name = $"Station {i}", Url = $"radio{i}.example.com/live" }).ToList();
        });
        await fixture.StartAndWaitForFirstPlayAsync();
        Assert.Equal(11, fixture.Store.Current.Evidence.Count);
        Assert.All(fixture.Store.Current.Evidence, item => Assert.True(item.Ready));
        Assert.Equal(11, fixture.Store.Current.LastDecision!.Candidates.Count);
    }

    [Fact]
    public async Task ShortDwellWithSlowStreamsExpandsEvidenceWindowAndEventuallyStarts()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
        {
            settings.FlyDj.MinDwellMinutes = 1;
            settings.FlyDj.MaxDwellMinutes = 2;
            settings.Stations = Enumerable.Range(1, 11).Select(i => new TuneInStation { Name = $"Station {i}", Url = $"radio{i}.example.com/live" }).ToList();
        });
        fixture.AutoAdvanceObservation = false;
        fixture.OnAudition = request =>
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            return FlyDjConductorFixture.DefaultAudition(request);
        };
        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.ObservationEndsAtUtc is not null, parked: true);
        Assert.Equal(150, fixture.Store.Current.ObservationWindowSeconds);
        Assert.Empty(fixture.SceneCalls);
        for (var round = 0; round < 12 && fixture.SceneCalls.IsEmpty; round++) await fixture.StepAsync(TimeSpan.FromSeconds(15));
        Assert.Single(fixture.SceneCalls);
        Assert.All(fixture.Store.Current.Evidence, item => Assert.True(item.Ready));
        Assert.True(fixture.Store.Current.NextDecisionAtUtc >= fixture.Time.GetUtcNow().AddMinutes(1));
    }

    [Fact]
    public async Task RepeatedCachedIdentityNeverStartsPlaybackAndStopCancelsObservation()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => settings.FlyDj.MinDwellMinutes = 1);
        fixture.AutoAdvanceObservation = false;
        fixture.OnAudition = request => FlyDjConductorFixture.DefaultAudition(request) with
        {
            Results = FlyDjConductorFixture.DefaultAudition(request).Results.Select(result => result with
            { SampleId = "same-cached-sample", SampledAtUtc = FlyDjConductorFixture.StartTime }).ToArray()
        };
        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Evidence.Count > 0, parked: true);
        await fixture.AdvanceSamplingAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(fixture.SceneCalls);
        Assert.All(fixture.Store.Current.Evidence, item => Assert.Equal(1, item.SampleCount));
        await fixture.Conductor.StopAsync("tester");
        var before = fixture.Client.Invocations.Count;
        fixture.Time.Advance(TimeSpan.FromHours(1));
        await fixture.Settle();
        Assert.Equal(before, fixture.Client.Invocations.Count);
    }

    [Fact]
    public async Task AnOldSidecarReportsAnUpgradeErrorInsteadOfInventingFreshEvidence()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.Client.Setup(client => client.AuditionAsync(It.IsAny<FlyDjAuditionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((FlyDjAuditionRequest request, CancellationToken _) => Task.FromResult(FlyDjConductorFixture.DefaultAudition(request)));
        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Error, parked: true);
        Assert.Contains("Update both container images", fixture.Store.Current.StatusMessage);
        Assert.Empty(fixture.SceneCalls);
    }

    [Fact]
    public async Task MaximumDwellDoesNotForceAChangeWhenOnlyOneStationIsAllowed()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
        { settings.FlyDj.MinDwellMinutes = 1; settings.FlyDj.MaxDwellMinutes = 1; settings.Stations = settings.Stations.Take(1).ToList(); });
        await fixture.StartAndWaitForFirstPlayAsync();
        await fixture.AdvanceSamplingAsync(TimeSpan.FromSeconds(75));
        Assert.Single(fixture.SceneCalls);
        Assert.Equal(FlyDjConductorStatus.Playing, fixture.Store.Current.Status);
    }

    [Fact]
    public async Task ExternalTakeoverDuringFinalProbeWinsBeforeTheFirstPlaybackWrite()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => settings.FlyDj.MinDwellMinutes = 1);
        var ip = FlyDjConductorFixture.KitchenIp;
        fixture.Snapshots.Set(new PlaybackSnapshot(ip, "Kitchen", false, 10, "https://old.example/live", null, false,
            fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), null));
        fixture.OnAudition = request =>
        {
            if (fixture.Time.GetUtcNow() >= FlyDjConductorFixture.StartTime.AddSeconds(30))
                fixture.Snapshots.Set(new PlaybackSnapshot(ip, "Kitchen", true, 10, "https://manual.example/live", null, false,
                    fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), null));
            return FlyDjConductorFixture.DefaultAudition(request);
        };
        await fixture.StartAndWaitForFirstPlayAsync();
        Assert.All(fixture.SceneCalls, call => Assert.DoesNotContain(ip, call.Targets!));
        Assert.Contains(fixture.Store.Current.YieldedSpeakers, room => room.SpeakerIp == ip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmupRespectsExternalSourceChangesButIgnoresVolume(bool volumeOnly)
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => settings.FlyDj.MinDwellMinutes = 1);
        fixture.AutoAdvanceObservation = false;
        var ip = FlyDjConductorFixture.KitchenIp;
        fixture.Snapshots.Set(new PlaybackSnapshot(ip, "Kitchen", false, 10, "https://old.example/live", null, false,
            fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), null));
        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.ObservationEndsAtUtc is not null, parked: true);
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        fixture.Snapshots.Set(new PlaybackSnapshot(ip, "Kitchen", !volumeOnly, 20,
            volumeOnly ? "https://old.example/live" : "https://manual.example/live", null, false,
            fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), null));
        await fixture.AdvanceSamplingAsync(TimeSpan.FromSeconds(32));
        var call = Assert.Single(fixture.SceneCalls);
        if (volumeOnly) { Assert.Contains(ip, call.Targets!); Assert.Empty(fixture.Store.Current.YieldedSpeakers); }
        else { Assert.DoesNotContain(ip, call.Targets!); Assert.Single(fixture.Store.Current.YieldedSpeakers); }
    }

    [Fact]
    public async Task OwnPlaybackSnapshotsDuringStartAreNotExternalTakeovers()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => settings.FlyDj.MinDwellMinutes = 1);
        var ip = FlyDjConductorFixture.KitchenIp;
        fixture.Snapshots.Set(new PlaybackSnapshot(ip, "Kitchen", false, 10, "https://old.example/live", null, false,
            fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), null));
        fixture.OnScene = call =>
        {
            fixture.Snapshots.Set(new PlaybackSnapshot(ip, "Kitchen", true, 10, call.Scene.SourceUrl, null, false,
                fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), null));
            return Task.CompletedTask;
        };
        await fixture.StartAndWaitForFirstPlayAsync();
        await fixture.StepAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(fixture.Store.Current.YieldedSpeakers);
        Assert.Equal(2, fixture.Store.Current.ActiveSpeakerIps.Count);
    }

    [Fact]
    public async Task ManualPlaybackDuringWarmupIsNeverOverwritten()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => settings.FlyDj.MinDwellMinutes = 1);
        fixture.AutoAdvanceObservation = false;
        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.ObservationEndsAtUtc is not null, parked: true);
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);
        await fixture.AdvanceSamplingAsync(TimeSpan.FromSeconds(32));
        Assert.All(fixture.SceneCalls, call => Assert.DoesNotContain(FlyDjConductorFixture.KitchenIp, call.Targets!));
        Assert.Single(fixture.SceneCalls);
        Assert.Single(fixture.Store.Current.YieldedSpeakers);
    }
}
