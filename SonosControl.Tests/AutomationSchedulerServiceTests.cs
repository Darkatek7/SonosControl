using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class AutomationSchedulerServiceTests
{
    [Fact]
    public async Task EvaluateNowAsync_TransitionsBetweenWindows_StopsOldTargetsAndAppliesNewScene()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(
            Window("window-a", "scene-a", new TimeOnly(9, 0), new TimeOnly(11, 0), priority: 10),
            Window("window-b", "scene-b", new TimeOnly(11, 0), new TimeOnly(13, 0), priority: 20));
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.PausePlaying("10.0.0.1")).Returns(Task.CompletedTask);

        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied A", "scene-a", ["10.0.0.1"]));
        scenes.Setup(service => service.ApplySceneByIdAsync("scene-b", "automation-scheduler", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied B", "scene-b", ["10.0.0.2"]));

        using var fixture = CreateScheduler(settingsRepo.Object, connector.Object, scenes.Object, time);

        await fixture.Service.EvaluateNowAsync();
        Assert.Equal("window-a", fixture.Status.Snapshot.ActiveScheduleName);

        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 11, 30, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();

        connector.Verify(repo => repo.PausePlaying("10.0.0.1"), Times.Once);
        scenes.Verify(service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()), Times.Once);
        scenes.Verify(service => service.ApplySceneByIdAsync("scene-b", "automation-scheduler", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("window-b", fixture.Status.Snapshot.ActiveScheduleName);
        Assert.Equal(2, fixture.Db.Logs.Count(log => log.Action == "ScheduleTriggered"));
        Assert.Single(fixture.Db.Logs.Where(log => log.Action == "ScheduleStopped"));
    }

    [Fact]
    public async Task EvaluateNowAsync_RetriesFailedSceneOnNextEvaluation()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(Window("window-a", "scene-a", new TimeOnly(9, 0), new TimeOnly(11, 0), priority: 10));
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.SetupSequence(service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(false, "Speaker unavailable", "scene-a", []))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));

        using var fixture = CreateScheduler(settingsRepo.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time);

        await fixture.Service.EvaluateNowAsync();
        Assert.Contains("Speaker unavailable", fixture.Status.Snapshot.SchedulerError);
        Assert.Null(fixture.Status.Snapshot.ActiveScheduleName);

        await fixture.Service.EvaluateNowAsync();
        Assert.Null(fixture.Status.Snapshot.SchedulerError);
        Assert.Equal("window-a", fixture.Status.Snapshot.ActiveScheduleName);
        scenes.Verify(service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EvaluateNowAsync_MigrationFailure_DoesNotStartAutomation()
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        var scenes = new Mock<ISceneOrchestrationService>();
        var migration = new Mock<ISettingsSchemaMigrationService>();
        migration.Setup(service => service.MigrateIfRequiredAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettingsMigrationResult(false, false, 0, [], "Invalid legacy settings"));

        using var fixture = CreateScheduler(
            settingsRepo.Object,
            Mock.Of<ISonosConnectorRepo>(),
            scenes.Object,
            new ManualTimeProvider(DateTimeOffset.UtcNow),
            migration.Object);

        await fixture.Service.EvaluateNowAsync();

        settingsRepo.Verify(repo => repo.GetSettings(), Times.Never);
        scenes.Verify(service => service.ApplySceneByIdAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateNowAsync_UsesConfiguredTimezoneAcrossDstTransition()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
        // 01:30 UTC is 03:30 local after the spring DST jump on 29/03/2026.
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero));
        var settings = Settings(Window("dst-window", "scene-a", new TimeOnly(3, 0), new TimeOnly(4, 0), priority: 10));
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));

        using var fixture = CreateScheduler(settingsRepo.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time, timeZone: timeZone);

        await fixture.Service.EvaluateNowAsync();

        scenes.Verify(service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("dst-window", fixture.Status.Snapshot.ActiveScheduleName);
    }

    [Fact]
    public async Task EvaluateNowAsync_AppliesFadeInAndFadeOutAroundWindowPlayback()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var window = Window("fade-window", "scene-a", new TimeOnly(9, 0), new TimeOnly(11, 0), priority: 10);
        window.FadeInSeconds = 1;
        window.FadeOutSeconds = 1;
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(Settings(window));

        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume("10.0.0.1")).ReturnsAsync(20);
        connector.Setup(repo => repo.SetSpeakerVolume("10.0.0.1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connector.Setup(repo => repo.PausePlaying("10.0.0.1")).Returns(Task.CompletedTask);

        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));

        using var fixture = CreateScheduler(settingsRepo.Object, connector.Object, scenes.Object, time);

        await fixture.Service.EvaluateNowAsync();
        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 11, 1, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();

        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", 1, It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", 10, It.IsAny<CancellationToken>()), Times.Exactly(2));
        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", 20, It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", 0, It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.PausePlaying("10.0.0.1"), Times.Once);
    }

    [Fact]
    public async Task EvaluateNowAsync_AfterRestart_ReappliesTheActiveScene()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(Window("window-a", "scene-a", new TimeOnly(9, 0), new TimeOnly(11, 0), priority: 10));
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));

        using (var firstRun = CreateScheduler(settingsRepo.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time))
        {
            await firstRun.Service.EvaluateNowAsync();
        }

        using (var restarted = CreateScheduler(settingsRepo.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time))
        {
            await restarted.Service.EvaluateNowAsync();
        }

        scenes.Verify(
            service => service.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private static SchedulerFixture CreateScheduler(
        ISettingsRepo settingsRepo,
        ISonosConnectorRepo connector,
        ISceneOrchestrationService scenes,
        TimeProvider timeProvider,
        ISettingsSchemaMigrationService? migration = null,
        TimeZoneInfo? timeZone = null)
    {
        var databaseOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"canonical-scheduler-{Guid.NewGuid():N}")
            .Options;
        var db = new ApplicationDbContext(databaseOptions);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo);
        uow.SetupGet(value => value.ISonosConnectorRepo).Returns(connector);
        uow.SetupGet(value => value.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(scenes);
        services.AddSingleton(db);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<ActionLogger>();
        var provider = services.BuildServiceProvider();

        var migrationMock = migration is null ? new Mock<ISettingsSchemaMigrationService>() : null;
        migrationMock?.Setup(service => service.MigrateIfRequiredAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettingsMigrationResult(true, false, SonosSettings.CurrentSettingsSchemaVersion, []));

        var status = new AutomationRuntimeStatus();
        status.SetReady(SonosSettings.CurrentSettingsSchemaVersion);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Automation:TimeZone"] = "Europe/Vienna" })
            .Build();

        var scheduler = new AutomationSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            migration ?? migrationMock!.Object,
            status,
            NullLogger<AutomationSchedulerService>.Instance,
            configuration,
            timeProvider,
            timeZone ?? TimeZoneInfo.Utc);

        return new SchedulerFixture(scheduler, status, db, provider);
    }

    private static SonosSettings Settings(params ScheduleWindow[] windows)
        => new()
        {
            SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion,
            Speakers =
            [
                new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.1" },
                new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.2" }
            ],
            Scenes =
            [
                new Scene
                {
                    Id = "scene-a",
                    Name = "Scene A",
                    Enabled = true,
                    Actions = [new SceneAction { SpeakerIp = "10.0.0.1", IncludeInPlayback = true }]
                },
                new Scene
                {
                    Id = "scene-b",
                    Name = "Scene B",
                    Enabled = true,
                    Actions = [new SceneAction { SpeakerIp = "10.0.0.2", IncludeInPlayback = true }]
                }
            ],
            ScheduleWindows = windows.ToList()
        };

    private static ScheduleWindow Window(string id, string sceneId, TimeOnly start, TimeOnly stop, int priority)
        => new()
        {
            Id = id,
            Name = id,
            SceneId = sceneId,
            IsEnabled = true,
            StartTime = start,
            StopTime = stop,
            Priority = priority,
            RecurrenceType = ScheduleRecurrenceType.Daily,
            LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }

    private sealed class SchedulerFixture(
        AutomationSchedulerService service,
        AutomationRuntimeStatus status,
        ApplicationDbContext db,
        ServiceProvider provider) : IDisposable
    {
        public AutomationSchedulerService Service { get; } = service;
        public AutomationRuntimeStatus Status { get; } = status;
        public ApplicationDbContext Db { get; } = db;

        public void Dispose()
        {
            provider.Dispose();
            db.Dispose();
        }
    }
}
