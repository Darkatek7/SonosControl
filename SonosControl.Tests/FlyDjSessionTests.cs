using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public sealed class FlyDjSessionTests
{
    private static ScheduleWindow Window(string id, string room, params string[] urls) => new()
    {
        Id = id,
        Name = id,
        StartTime = new(12, 0),
        StopTime = new(13, 0),
        RecurrenceType = ScheduleRecurrenceType.Daily,
        FlyDjCandidateStationUrls = urls.ToList(),
        Playback = new()
        {
            Id = id + "-playback",
            SourceType = SceneSourceType.FlyDj,
            SourceSelectionMode = SceneSourceSelectionMode.Specific,
            Actions = [new() { SpeakerIp = room, Volume = 12, IncludeInPlayback = true }]
        }
    };

    private static Task<FlyDjConductorFixture> Fixture(params ScheduleWindow[] windows) => FlyDjConductorFixture.CreateAsync(settings =>
    {
        settings.SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion;
        settings.ScheduleWindows = windows.ToList();
        settings.FlyDj.MinDwellMinutes = 1;
        settings.FlyDj.MaxDwellMinutes = 2;
    }, withScheduler: true);

    [Fact]
    public async Task IdleScheduleRefreshPreservesTheDashboardsSuccessfulSidecarProbe()
    {
        await using var fixture = await Fixture();
        await fixture.Store.RefreshAsync();
        Assert.True(fixture.Store.Current.SidecarReachable);
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.Equal(FlyDjConductorStatus.Stopped, fixture.Store.Current.Status);
        Assert.True(fixture.Store.Current.SidecarReachable);
    }

    [Fact]
    public async Task ParallelPoolsChooseIndependentlyAndStoppingOneDoesNotStopTheOtherBrain()
    {
        var one = Window("one", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live");
        var two = Window("two", FlyDjConductorFixture.LivingIp, "radio3.example.com/live");
        await using var fixture = await Fixture(one, two);
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.SceneCalls.Count == 2, advance: TimeSpan.FromSeconds(3), parked: true, what: "two independent schedules");
        Assert.Equal("radio1.example.com/live", Assert.Single(fixture.SceneCalls, call => call.Targets!.Contains(FlyDjConductorFixture.KitchenIp)).Scene.SourceUrl);
        Assert.Equal("radio3.example.com/live", Assert.Single(fixture.SceneCalls, call => call.Targets!.Contains(FlyDjConductorFixture.LivingIp)).Scene.SourceUrl);
        Assert.Equal(2, fixture.Store.Current.Sessions.Count);
        var second = fixture.Store.Current.Sessions.Single(session => session.Name == "two");
        await fixture.Conductor.SelectSessionAsync(second.Id);
        Assert.Equal("radio3.example.com/live", fixture.Store.Current.CurrentStationUrl);
        var stops = fixture.Calls(nameof(IFlyDjClient.StopAsync));
        await fixture.Repo.UpdateSettingsAsync(settings => settings.ScheduleWindows[0].IsEnabled = false);
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.True(fixture.Conductor.IsRunning);
        Assert.Equal(stops, fixture.Calls(nameof(IFlyDjClient.StopAsync)));
        Assert.Single(fixture.Store.Current.Sessions);
        Assert.Equal("radio3.example.com/live", fixture.Store.Current.CurrentStationUrl);
        fixture.Connector.Verify(connector => connector.PausePlaying(FlyDjConductorFixture.LivingIp, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SwitchingBackOnResumesCustomScheduleContexts()
    {
        await using var fixture = await Fixture(Window("one", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live"));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.SceneCalls.Count == 1, parked: true);
        await fixture.Conductor.StopAsync("tester");
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.False(fixture.Conductor.IsRunning);
        await fixture.Conductor.StartAsync("tester");
        await fixture.UntilAsync(() => fixture.Store.Current.Sessions.Any(session => session.Name == "one" && session.Status == FlyDjConductorStatus.Playing),
            advance: TimeSpan.FromSeconds(3), parked: true, what: "custom schedule resumed after switching on");
        var scheduled = fixture.Store.Current.Sessions.Single(session => session.Name == "one");
        Assert.Equal(FlyDjConductorStatus.Playing, scheduled.Status);
        Assert.Equal("Station 1", scheduled.StationName);
    }

    [Fact]
    public async Task PrewarmingCollectsEvidenceWithoutSpeakerCommandsAndStartsAtTheBoundary()
    {
        var window = Window("morning", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live");
        window.StartTime = new(12, 1);
        window.FlyDjPrewarm = true;
        await using var fixture = await Fixture(window);
        fixture.AutoAdvanceObservation = false;
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.IsPrewarming && fixture.Store.Current.Evidence.Count == 1, parked: true);
        for (var i = 0; i < 9; i++)
        {
            await fixture.StepAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(fixture.SceneCalls);
        }
        Assert.True(Assert.Single(fixture.Store.Current.Evidence).Ready);
        fixture.Connector.VerifyNoOtherCalls();
        await fixture.StepAsync(TimeSpan.FromSeconds(3));
        await fixture.UntilAsync(() => fixture.SceneCalls.Count == 1, advance: TimeSpan.FromSeconds(1), parked: true);
        Assert.False(fixture.Store.Current.IsPrewarming);
        Assert.True(fixture.Time.GetUtcNow() >= FlyDjConductorFixture.StartTime.AddMinutes(1));
        Assert.True(fixture.Store.Current.NextDecisionAtUtc >= fixture.Time.GetUtcNow().AddMinutes(1));
    }

    [Fact]
    public async Task RemovingPrewarmingWindowCancelsItAndOffSuppressesOnlyThisOccurrence()
    {
        var window = Window("morning", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live");
        window.StartTime = new(12, 1); window.FlyDjPrewarm = true;
        await using var fixture = await Fixture(window);
        fixture.AutoAdvanceObservation = false;
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.IsPrewarming, parked: true);
        await fixture.Conductor.StopAsync("tester");
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.False(fixture.Conductor.IsRunning);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.IsPrewarming, parked: true);
        await fixture.Repo.UpdateSettingsAsync(settings => settings.ScheduleWindows.Clear());
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.False(fixture.Conductor.IsRunning);
        Assert.Empty(fixture.SceneCalls);
        var calls = fixture.Calls(nameof(IFlyDjClient.AuditionAsync));
        fixture.Time.Advance(TimeSpan.FromHours(1));
        await fixture.Settle();
        Assert.Equal(calls, fixture.Calls(nameof(IFlyDjClient.AuditionAsync)));
    }

    [Fact]
    public async Task PrewarmUsesUpcomingWinningRoomsAndHonorsDatesAcrossMidnight()
    {
        var window = Window("midnight", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live");
        window.StartTime = new(0, 2); window.StopTime = new(1, 0); window.FlyDjPrewarm = true;
        await using var fixture = await Fixture(window);
        var settings = await fixture.SettingsAsync(); settings.FlyDj.MinDwellMinutes = 60;
        var now = new DateTimeOffset(2026, 10, 19, 23, 55, 0, TimeSpan.Zero);
        var upcoming = Assert.Single(fixture.Scheduler.GetFlyDjSchedules(settings, now));
        Assert.Equal(now.Date.AddDays(1).AddMinutes(2), upcoming.StartUtc.UtcDateTime);
        Assert.Empty(fixture.Scheduler.GetScheduleOwnedSpeakers(settings, now));
        settings.AutomationExcludedDates = [new DateOnly(2026, 10, 20)];
        Assert.Empty(fixture.Scheduler.GetFlyDjSchedules(settings, now));
    }

    [Theory]
    [InlineData(2026, 3, 29, 0, 55, 1, 0)]
    [InlineData(2026, 10, 25, 0, 25, 0, 30)]
    public async Task PreparationUsesConfiguredDstRules(int year, int month, int day, int hour, int minute, int expectedHour, int expectedMinute)
    {
        var window = Window("dst", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live");
        window.StartTime = new(2, 30); window.StopTime = new(4, 0); window.FlyDjPrewarm = true;
        await using var fixture = await Fixture(window);
        var settings = await fixture.SettingsAsync(); settings.FlyDj.MinDwellMinutes = 60;
        var migration = new Mock<ISettingsSchemaMigrationService>();
        using var scheduler = new AutomationSchedulerService(fixture.Provider.GetRequiredService<IServiceScopeFactory>(), migration.Object,
            new AutomationRuntimeStatus(), NullLogger<AutomationSchedulerService>.Instance, new ConfigurationBuilder().Build(), fixture.Time,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna"), fixture.Commands);
        var upcoming = Assert.Single(scheduler.GetFlyDjSchedules(settings, new(year, month, day, hour, minute, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(year, month, day, expectedHour, expectedMinute, 0, TimeSpan.Zero), upcoming.StartUtc);
    }

    [Fact]
    public async Task CustomPoolEditsDoNotReclaimAManuallyTakenRoom()
    {
        await using var fixture = await Fixture(Window("custom", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live"));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.SceneCalls.Count == 1, parked: true);
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);
        await fixture.StepAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(FlyDjConductorStatus.Yielded, fixture.Store.Current.Status);
        await fixture.Repo.UpdateSettingsAsync(settings => settings.ScheduleWindows[0].FlyDjCandidateStationUrls = ["radio2.example.com/live"]);
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Yielded, parked: true);
        Assert.Single(fixture.SceneCalls);
        Assert.Single(fixture.Store.Current.YieldedSpeakers);
    }

    [Fact]
    public async Task SlowPrewarmingRequestDoesNotBlockScheduleCancellation()
    {
        var window = Window("cancel", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live");
        window.StartTime = new(12, 1); window.FlyDjPrewarm = true;
        await using var fixture = await Fixture(window);
        fixture.AutoAdvanceObservation = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<FlyDjAuditionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Setup(client => client.AuditionAsync(It.IsAny<FlyDjAuditionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (FlyDjAuditionRequest _, CancellationToken token) => { entered.TrySetResult(); return await blocked.Task.WaitAsync(token); });
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Scheduler.EvaluateNowAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Repo.UpdateSettingsAsync(settings => settings.ScheduleWindows.Clear());
        await fixture.Scheduler.EvaluateNowAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Conductor.IsRunning);
        Assert.Empty(fixture.SceneCalls);
    }

    [Fact]
    public async Task CustomPoolRoundTripAndInvalidPoolValidation()
    {
        await using var fixture = await Fixture();
        var editor = new SettingsEditingService(fixture.Repo);
        var window = Window("saved", FlyDjConductorFixture.KitchenIp, "radio1.example.com/live");
        window.FlyDjPrewarm = true;
        await editor.SaveWindowAsync(window, true);
        var saved = Assert.Single((await fixture.SettingsAsync()).ScheduleWindows);
        Assert.Equal(window.FlyDjCandidateStationUrls, saved.FlyDjCandidateStationUrls);
        Assert.True(saved.FlyDjPrewarm);
        saved.FlyDjCandidateStationUrls = [];
        await Assert.ThrowsAsync<SettingsEditException>(() => editor.SaveWindowAsync(saved, false));
        saved.FlyDjCandidateStationUrls = ["missing.example/live"];
        await Assert.ThrowsAsync<SettingsEditException>(() => editor.SaveWindowAsync(saved, false));
        saved.FlyDjCandidateStationUrls = null;
        await editor.SaveWindowAsync(saved, false);
        Assert.Null(Assert.Single((await fixture.SettingsAsync()).ScheduleWindows).FlyDjCandidateStationUrls);
    }
}
