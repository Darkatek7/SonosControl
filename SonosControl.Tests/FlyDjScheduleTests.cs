using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public sealed class FlyDjScheduleTests
{
    private static ScheduleWindow Window(string id = "dj", int priority = 10, SceneSourceType source = SceneSourceType.FlyDj) => new()
    {
        Id = id,
        Name = id,
        Priority = priority,
        RecurrenceType = ScheduleRecurrenceType.Daily,
        StartTime = new(12, 0),
        StopTime = new(13, 0),
        Playback = new Scene
        {
            Id = $"{id}-playback",
            Name = id,
            SourceType = source,
            SourceSelectionMode = SceneSourceSelectionMode.Specific,
            SourceUrl = source == SceneSourceType.FlyDj ? null : "radio.example.com/live",
            MasterSpeakerIp = FlyDjConductorFixture.LivingIp,
            IsSyncedPlayback = false,
            Actions = [new() { SpeakerIp = FlyDjConductorFixture.KitchenIp, Volume = 12 }, new() { SpeakerIp = FlyDjConductorFixture.LivingIp, Volume = 18 }]
        }
    };

    private static Task<FlyDjConductorFixture> FixtureAsync(params ScheduleWindow[] windows) => FlyDjConductorFixture.CreateAsync(settings =>
    {
        settings.SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion;
        settings.FlyDj.TargetSpeakerIps = []; // Scheduled rooms must not depend on standalone settings.
        settings.ScheduleWindows = windows.ToList();
    }, withScheduler: true);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void SourceDropdownRequiresFeatureFlagAndTimeSchedule(bool enabled, bool scheduled, bool visible)
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IOptions<FlyDjOptions>>(Options.Create(new FlyDjOptions { Enabled = enabled }));
        var playback = Window().Playback!;
        playback.SourceType = SceneSourceType.Station;
        var cut = context.Render<PlaybackPayloadEditor>(parameters => parameters
            .Add(component => component.Settings, new SonosSettings())
            .Add(component => component.Playback, playback)
            .Add(component => component.CanEdit, true).Add(component => component.AllowFlyDj, scheduled));
        Assert.Equal(visible, cut.FindAll("#payload-source-type option[value='FlyDj']").Count == 1);
        if (!visible) return;
        cut.Find("#payload-source-type").Change("FlyDj");
        Assert.Null(playback.SourceUrl);
        Assert.Null(playback.TimerMinutes);
        Assert.Empty(cut.FindAll("#payload-source-url"));
        Assert.Contains("FlyDJ chooses and changes", cut.Markup);
        cut.Find("#payload-source-type").Change("Station");
        Assert.NotEmpty(cut.FindAll("#payload-source-url"));
    }

    [Fact]
    public async Task ScheduleStartsWithoutAutopilotUsesScheduleRoomsVolumesAndStopsAtBoundary()
    {
        await using var fixture = await FixtureAsync(Window());
        Assert.False(fixture.Conductor.IsRunning);
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, what: "scheduled station playing", parked: true);
        var call = Assert.Single(fixture.SceneCalls);
        Assert.Equal(SceneSourceType.Station, call.Scene.SourceType);
        Assert.False(call.Scene.IsSyncedPlayback);
        Assert.Equal(FlyDjConductorFixture.LivingIp, call.Scene.MasterSpeakerIp);
        Assert.Equal([12, 18], call.Scene.Actions.Select(action => action.Volume));
        Assert.Equal(2, call.Targets!.Count);
        Assert.False((await fixture.SettingsAsync()).FlyDj.WasRunning);
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var deadlines = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ManualPlaybackDeadlines.ToListAsync();
            Assert.Equal(2, deadlines.Count);
            Assert.All(deadlines, deadline => Assert.Equal(FlyDjConductorFixture.StartTime.AddHours(1).UtcDateTime, deadline.StopAtUtc));
        }
        await fixture.AdvanceAsync(TimeSpan.FromHours(1));
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.False(fixture.Conductor.IsRunning);
        fixture.Connector.Verify(connector => connector.PausePlaying(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(1, fixture.Calls(nameof(IFlyDjClient.AuditionAsync)));
    }

    [Fact]
    public async Task HigherPriorityStationOwnsItsRoomWhileFlyDjPlaysTheOther()
    {
        var regular = Window("regular", 1, SceneSourceType.Station);
        regular.Playback!.Actions.RemoveAt(1);
        await using var fixture = await FixtureAsync(Window(), regular);
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, parked: true);
        var dj = Assert.Single(fixture.SceneCalls, call => call.PerformedBy == FlyDjConductorService.DefaultActor);
        Assert.Equal([FlyDjConductorFixture.LivingIp], dj.Targets);
        Assert.Equal([FlyDjConductorFixture.KitchenIp], Assert.Single(fixture.SceneCalls, call => call.PerformedBy == "automation-scheduler").Targets);
    }

    [Fact]
    public async Task ManualTakeoverIsNotReclaimedOnTheNextStationOrSchedulerPoll()
    {
        await using var fixture = await FixtureAsync(Window());
        await fixture.Repo.UpdateSettingsAsync(settings => { settings.FlyDj.MinDwellMinutes = 1; settings.FlyDj.MaxDwellMinutes = 1; });
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, parked: true);
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);
        await fixture.Scheduler.EvaluateNowAsync();
        var before = fixture.SceneCalls.Count;
        var currentUrl = fixture.Store.Current.CurrentStationUrl;
        fixture.OnAudition = request => FlyDjConductorFixture.DefaultAudition(request) with
        {
            Results = FlyDjConductorFixture.DefaultAudition(request).Results
                .Where(result => request.Candidates.Single(candidate => candidate.Id == result.Id).Url != currentUrl).ToArray()
        };
        await fixture.StepAsync(TimeSpan.FromMinutes(2));
        await fixture.UntilAsync(() => fixture.SceneCalls.Count > before, what: "next scheduled station");
        Assert.All(fixture.SceneCalls.Skip(before), call => Assert.DoesNotContain(FlyDjConductorFixture.KitchenIp, call.Targets!));
    }

    [Fact]
    public async Task StopSwitchSuppressesCurrentOccurrenceButNextDayStartsAgain()
    {
        await using var fixture = await FixtureAsync(Window());
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, parked: true);
        await fixture.Conductor.StopAsync("tester");
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.False(fixture.Conductor.IsRunning);
        fixture.Time.Advance(TimeSpan.FromDays(1));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, parked: true);
        Assert.Equal(2, fixture.Calls(nameof(IFlyDjClient.AuditionAsync)));
    }

    [Fact]
    public async Task AuditionThatCrossesWindowEndDoesNotStartPlayback()
    {
        await using var fixture = await FixtureAsync(Window());
        fixture.OnAudition = request =>
        {
            fixture.Time.Advance(TimeSpan.FromHours(1));
            return FlyDjConductorFixture.DefaultAudition(request);
        };
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Calls(nameof(IFlyDjClient.AuditionAsync)) == 1, parked: true);
        Assert.Empty(fixture.SceneCalls);
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.False(fixture.Conductor.IsRunning);
    }

    [Fact]
    public async Task DisablingWindowStopsItsPlaybackBeforeTheOriginalBoundary()
    {
        await using var fixture = await FixtureAsync(Window());
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, parked: true);
        await fixture.Repo.UpdateSettingsAsync(settings => settings.ScheduleWindows[0].IsEnabled = false);
        await fixture.Scheduler.EvaluateNowAsync();
        Assert.False(fixture.Conductor.IsRunning);
        fixture.Connector.Verify(connector => connector.PausePlaying(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EditingRunningScheduleUpdatesVolumesAndPersistedStopDeadline()
    {
        await using var fixture = await FixtureAsync(Window());
        fixture.Time.Advance(TimeSpan.FromSeconds(1)); // Real starts happen after the nominal window start.
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, parked: true);
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.Repo.UpdateSettingsAsync(settings =>
        {
            settings.ScheduleWindows[0].StopTime = new(14, 0);
            settings.ScheduleWindows[0].Playback!.Actions[0].Volume = 7;
        });
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.SceneCalls.Count == 2 && fixture.Store.Current.Status == FlyDjConductorStatus.Playing, parked: true);
        Assert.Equal(7, fixture.SceneCalls.Last().Scene.Actions[0].Volume);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var deadlines = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ManualPlaybackDeadlines.ToListAsync();
        Assert.All(deadlines, deadline => Assert.Equal(FlyDjConductorFixture.StartTime.AddHours(2).UtcDateTime, deadline.StopAtUtc));
    }

    [Fact]
    public async Task StandaloneManualYieldSurvivesAnUnrelatedScheduleStarting()
    {
        var window = Window();
        window.StartTime = new(12, 1);
        window.Playback!.Actions.RemoveAt(0); // Only Living belongs to this schedule.
        await using var fixture = await FixtureAsync(window);
        await fixture.Repo.UpdateSettingsAsync(settings => settings.FlyDj.TargetSpeakerIps = [FlyDjConductorFixture.KitchenIp]);
        await fixture.StartAndWaitForFirstPlayAsync();
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);
        var prior = fixture.SceneCalls.Count;
        await fixture.AdvanceAsync(TimeSpan.FromMinutes(1));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.UntilAsync(() => fixture.SceneCalls.Count > prior && fixture.Store.Current.Status == FlyDjConductorStatus.Playing, parked: true);
        Assert.All(fixture.SceneCalls.Skip(prior), call => Assert.DoesNotContain(FlyDjConductorFixture.KitchenIp, call.Targets!));
        Assert.Single(fixture.Store.Current.YieldedSpeakers);
    }

    [Fact]
    public async Task DisabledServerReportsScheduleErrorWithoutSidecarOrPlaybackCommands()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
        {
            settings.SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion;
            settings.ScheduleWindows = [Window()];
        });
        var migration = new Mock<ISettingsSchemaMigrationService>();
        migration.Setup(service => service.MigrateIfRequiredAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettingsMigrationResult(true, false, SonosSettings.CurrentSettingsSchemaVersion, []));
        using var scheduler = new AutomationSchedulerService(fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            migration.Object, new AutomationRuntimeStatus(), NullLogger<AutomationSchedulerService>.Instance,
            new ConfigurationBuilder().Build(), fixture.Time, TimeZoneInfo.Utc, fixture.Commands);
        await scheduler.EvaluateNowAsync();
        Assert.Contains("requires FlyDJ to be enabled", scheduler.Status.SchedulerError);
        Assert.Empty(fixture.SceneCalls);
        Assert.Equal(0, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
    }

    [Fact]
    public async Task OvernightScheduleUsesTheSameOccurrenceAndHonorsExclusions()
    {
        var window = Window();
        window.StartTime = new(23, 0);
        window.StopTime = new(1, 0);
        await using var fixture = await FixtureAsync(window);
        var settings = await fixture.SettingsAsync();
        var now = new DateTimeOffset(2026, 10, 20, 0, 30, 0, TimeSpan.Zero);
        var schedule = Assert.Single(fixture.Scheduler.GetFlyDjSchedules(settings, now));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero), schedule.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero), schedule.StopUtc);
        settings.ScheduleWindows[0].ExcludedDates = [new DateOnly(2026, 10, 19)];
        Assert.Empty(fixture.Scheduler.GetFlyDjSchedules(settings, now));
    }

    [Fact]
    public async Task SaveRoundTripKeepsFlyDjWithoutSourceUrlAndRejectsManualTemplate()
    {
        await using var fixture = await FixtureAsync();
        var editor = new SettingsEditingService(fixture.Repo);
        var window = Window();
        window.Playback!.SourceUrl = "ignored.example.com";
        await editor.SaveWindowAsync(window, true);
        var saved = Assert.Single((await fixture.SettingsAsync()).ScheduleWindows);
        Assert.Equal(SceneSourceType.FlyDj, saved.Playback!.SourceType);
        Assert.Null(saved.Playback.SourceUrl);
        saved.HasTimeSchedule = false;
        await Assert.ThrowsAsync<SettingsEditException>(() => editor.SaveWindowAsync(saved, false));
    }
}
