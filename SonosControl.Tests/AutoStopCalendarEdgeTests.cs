using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class AutoStopCalendarEdgeTests
{
    private const string Speaker = "10.0.0.1";

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task DailyRuleSpanningMidnight_StartAfterMidnightIsStillInPostStopPhaseAndGetsGrace()
    {
        using var fixture = new Fixture(NightRuleSettings(), At(20, 0, 15));

        await fixture.AutoStop.RegisterSuccessfulStartAsync([Speaker]);

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(At(20, 0, 45).UtcDateTime, deadline.StopAtUtc);
        Assert.Equal("Daily auto-stop", deadline.Reason);
        Assert.Equal("daily:2026-10-19:23:30-06:00", deadline.RuleKey);
    }

    [Fact]
    public async Task DailyRuleSpanningMidnight_StartJustBeforeReleaseIsBoundedByRelease()
    {
        using var fixture = new Fixture(NightRuleSettings(minutes: 60), At(20, 5, 30));

        await fixture.AutoStop.RegisterSuccessfulStartAsync([Speaker]);

        Assert.Equal(At(20, 6, 0).UtcDateTime, (await fixture.ReadDeadlineAsync()).StopAtUtc);
    }

    [Fact]
    public async Task DailyRuleSpanningMidnight_StartAfterReleaseIsNotStoppedUntilNextCutoff()
    {
        using var fixture = new Fixture(NightRuleSettings(), At(20, 6, 5));

        await fixture.AutoStop.RegisterSuccessfulStartAsync([Speaker]);
        fixture.Time.Set(At(20, 6, 40));
        await fixture.AutoStop.ProcessDueAsync();

        var deadline = await fixture.ReadDeadlineAsync();
        Assert.Equal(At(20, 23, 30).UtcDateTime, deadline.StopAtUtc);
        Assert.Equal("daily:2026-10-20:23:30-06:00", deadline.RuleKey);
        fixture.Connector.Verify(c => c.PausePlaying(Speaker, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DailyRuleSpanningMidnight_StopsAtGraceEndAndNotAgainAfterwards()
    {
        using var fixture = new Fixture(NightRuleSettings(), At(19, 23, 45));

        await fixture.AutoStop.RegisterSuccessfulStartAsync([Speaker]);
        Assert.Equal(At(20, 0, 15).UtcDateTime, (await fixture.ReadDeadlineAsync()).StopAtUtc);
        fixture.Time.Set(At(20, 0, 16));
        await fixture.AutoStop.ProcessDueAsync();
        fixture.Time.Set(At(20, 7, 0));
        await fixture.AutoStop.ProcessDueAsync();

        fixture.Connector.Verify(c => c.PausePlaying(Speaker, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludedScheduleDoesNotBecomeNextEffectiveStartThatEndsDailyStopEarly(bool windowLevelExclusion)
    {
        // Manual start at 23:50 Monday is inside Monday's 23:30 -> 06:00 rule. The daily
        // schedule at 05:00 is excluded on Tuesday 2026-10-20 so it must not cut the window short.
        var settings = NightRuleSettings(minutes: 600);
        settings.ScheduleWindows.Add(DailyWindow("early", new(5, 0), new(5, 30), priority: 1));
        var excluded = new DateOnly(2026, 10, 20);
        if (windowLevelExclusion) settings.ScheduleWindows[0].ExcludedDates.Add(excluded);
        else settings.AutomationExcludedDates.Add(excluded);
        using var fixture = new Fixture(settings, At(19, 23, 50));

        await fixture.AutoStop.RegisterSuccessfulStartAsync([Speaker]);

        // 23:50 + 10h = 09:50, bounded by release at 06:00; never by the excluded 05:00 start.
        Assert.Equal(At(20, 6, 0).UtcDateTime, (await fixture.ReadDeadlineAsync()).StopAtUtc);
    }

    [Fact]
    public async Task NonExcludedScheduleStillEndsDailyStopEarly_ControlForExclusionTest()
    {
        var settings = NightRuleSettings(minutes: 600);
        settings.ScheduleWindows.Add(DailyWindow("early", new(5, 0), new(5, 30), priority: 1));
        using var fixture = new Fixture(settings, At(19, 23, 50));

        await fixture.AutoStop.RegisterSuccessfulStartAsync([Speaker]);

        Assert.Equal(At(20, 5, 0).UtcDateTime, (await fixture.ReadDeadlineAsync()).StopAtUtc);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task ExcludedDateDoesNotStopPlaybackAtScheduleUsualEnd(bool isExcluded, int expectedPauses)
    {
        var settings = BaseSettings();
        settings.ScheduleWindows.Add(DailyWindow("morning", new(9, 0), new(11, 0), priority: 1));
        // isExcluded == false is the control: the schedule end stops playback once.
        if (isExcluded) settings.AutomationExcludedDates.Add(new DateOnly(2026, 10, 19));
        using var fixture = new Fixture(settings, At(19, 10, 0));

        await fixture.Scheduler.EvaluateNowAsync();
        await using (await fixture.Commands.EnterManualAsync([Speaker]))
            await fixture.ManualPlayback.PlaybackSucceededAsync([Speaker]);
        fixture.Time.Set(At(19, 11, 1));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.AutoStop.ProcessDueAsync();

        fixture.Connector.Verify(c => c.PausePlaying(Speaker, It.IsAny<CancellationToken>()), Times.Exactly(expectedPauses));
    }

    [Fact]
    public async Task HigherPriorityWindowTakesOverAndLowerPriorityEndDoesNotStopIt()
    {
        var settings = BaseSettings();
        // Lower number wins. "low" runs 09:00-10:30, "high" takes over at 10:00 and runs until 12:00.
        settings.ScheduleWindows.Add(DailyWindow("low", new(9, 0), new(10, 30), priority: 500));
        settings.ScheduleWindows.Add(DailyWindow("high", new(10, 0), new(12, 0), priority: 100));
        using var fixture = new Fixture(settings, At(19, 9, 30));

        await fixture.Scheduler.EvaluateNowAsync();
        Assert.Equal(At(19, 10, 30).UtcDateTime, (await fixture.ReadDeadlineAsync()).StopAtUtc);
        Assert.Equal("schedule:low", (await fixture.ReadDeadlineAsync()).RuleKey);

        fixture.Time.Set(At(19, 10, 5));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.AutoStop.ProcessDueAsync();
        var takenOver = await fixture.ReadDeadlineAsync();
        Assert.Equal(At(19, 12, 0).UtcDateTime, takenOver.StopAtUtc);
        Assert.StartsWith("schedule:high", takenOver.RuleKey);

        fixture.Time.Set(At(19, 10, 31));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.AutoStop.ProcessDueAsync();
        fixture.Connector.Verify(c => c.PausePlaying(Speaker, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(At(19, 12, 0).UtcDateTime, (await fixture.ReadDeadlineAsync()).StopAtUtc);

        fixture.Time.Set(At(19, 12, 1));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.AutoStop.ProcessDueAsync();
        fixture.Connector.Verify(c => c.PausePlaying(Speaker, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PendingLowerPriorityStopIsIgnoredWhenHigherPriorityStartsAtSameEvaluationAsStopDue()
    {
        var settings = BaseSettings();
        settings.ScheduleWindows.Add(DailyWindow("low", new(9, 0), new(10, 0), priority: 500));
        settings.ScheduleWindows.Add(DailyWindow("high", new(10, 0), new(12, 0), priority: 100));
        using var fixture = new Fixture(settings, At(19, 9, 30));
        await fixture.Scheduler.EvaluateNowAsync();

        // The lower window's end and the higher window's start coincide; the worker and
        // scheduler both run for the first time after that instant.
        fixture.Time.Set(At(19, 10, 0).AddSeconds(2));
        await fixture.AutoStop.ProcessDueAsync();
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.AutoStop.ProcessDueAsync();

        fixture.Connector.Verify(c => c.PausePlaying(Speaker, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(At(19, 12, 0).UtcDateTime, (await fixture.ReadDeadlineAsync()).StopAtUtc);
    }

    private static SonosSettings BaseSettings() => new()
    {
        SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion,
        Speakers = [new SonosSpeaker { Name = "Kitchen", IpAddress = Speaker, ManualAutoStopMinutes = 30 }]
    };

    private static SonosSettings NightRuleSettings(int minutes = 30)
    {
        var settings = BaseSettings();
        settings.Speakers[0].ManualAutoStopMinutes = minutes;
        settings.Speakers[0].ManualAutoStopDays = Enum.GetValues<DayOfWeek>().ToDictionary(day => day,
            _ => new ManualAutoStopDay { Enabled = true, StopTime = new(23, 30), ReleaseTime = new(6, 0) });
        return settings;
    }

    private static ScheduleWindow DailyWindow(string id, TimeOnly start, TimeOnly stop, int priority) => new()
    {
        Id = id,
        Name = id,
        IsEnabled = true,
        StartTime = start,
        StopTime = stop,
        Priority = priority,
        RecurrenceType = ScheduleRecurrenceType.Daily,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Playback = new Scene
        {
            Id = $"{id}-scene",
            Name = id,
            IsSyncedPlayback = true,
            LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Actions = [new SceneAction { SpeakerIp = Speaker, IncludeInPlayback = true }]
        }
    };

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset value) => _now = value;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        public Clock Time { get; }
        public PlaybackCommandCoordinator Commands { get; }
        public Mock<ISonosConnectorRepo> Connector { get; } = new();
        public ManualAutoStopService AutoStop { get; }
        public AutomationSchedulerService Scheduler { get; }
        public ManualPlaybackService ManualPlayback { get; }

        public Fixture(SonosSettings settings, DateTimeOffset start)
        {
            Directory.CreateDirectory(_directory);
            Time = new Clock(start);
            Commands = new PlaybackCommandCoordinator(Time);
            var repository = new Mock<ISettingsRepo>();
            repository.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
            Connector.Setup(c => c.GetAllSpeakersInGroup(Speaker, It.IsAny<CancellationToken>())).ReturnsAsync([Speaker]);
            Connector.Setup(c => c.PausePlaying(Speaker, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var orchestration = new Mock<ISceneOrchestrationService>();
            orchestration.Setup(service => service.ApplySceneWithContextAsync(
                    It.IsAny<Scene>(), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
                    It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(),
                    It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
                .ReturnsAsync((Scene scene, string? _, SceneExecutionContext _, Action<IReadOnlyCollection<string>>? record,
                    CancellationToken _, IReadOnlyCollection<string>? targets, bool _) =>
                {
                    var applied = targets ?? [Speaker];
                    record?.Invoke(applied);
                    return new SceneApplyResult(true, "Applied", scene.Id, applied.ToList());
                });
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Automation:TimeZone"] = "UTC" }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton(repository.Object);
            services.AddSingleton(Connector.Object);
            services.AddSingleton(orchestration.Object);
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(_directory, "edge.db")};Pooling=False"));
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
            services.AddScoped<ActionLogger>();
            _provider = services.BuildServiceProvider();
            using (var scope = _provider.CreateScope())
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate();
            AutoStop = new ManualAutoStopService(_provider.GetRequiredService<IServiceScopeFactory>(), Commands, Time,
                configuration, NullLogger<ManualAutoStopService>.Instance);
            var status = new AutomationRuntimeStatus();
            status.SetReady(SonosSettings.CurrentSettingsSchemaVersion);
            var migration = new Mock<ISettingsSchemaMigrationService>();
            migration.Setup(service => service.MigrateIfRequiredAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SettingsMigrationResult(true, false, SonosSettings.CurrentSettingsSchemaVersion, []));
            Scheduler = new AutomationSchedulerService(_provider.GetRequiredService<IServiceScopeFactory>(), migration.Object,
                status, NullLogger<AutomationSchedulerService>.Instance, configuration, Time, TimeZoneInfo.Utc, Commands, autoStop: AutoStop);
            ManualPlayback = new ManualPlaybackService(Commands, _provider, Connector.Object,
                NullLogger<ManualPlaybackService>.Instance, AutoStop);
        }

        public async Task<ManualPlaybackDeadline> ReadDeadlineAsync()
        {
            using var scope = _provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .ManualPlaybackDeadlines.AsNoTracking().SingleAsync();
        }

        public void Dispose()
        {
            _provider.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}
