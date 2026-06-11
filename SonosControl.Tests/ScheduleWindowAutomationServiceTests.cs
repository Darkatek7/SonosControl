using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class ScheduleWindowAutomationServiceTests
{
    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute)
    {
        var localDateTime = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(localDateTime);
        return new DateTimeOffset(localDateTime, offset);
    }

    [Fact]
    public async Task EvaluateWindowsAsync_DoesNotApplyBlockedWindowUntilWindowChanges()
    {
        var timeProvider = new ManualTimeProvider(LocalTime(2026, 6, 11, 10, 30));
        var coordinator = new SchedulePriorityCoordinator();
        coordinator.NotifySonosConfigStop("window-a");

        var sceneService = new Mock<ISceneOrchestrationService>(MockBehavior.Strict);
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.SetupSequence(repo => repo.GetSettings())
            .ReturnsAsync(CreateSettings(AlwaysActiveWindow("window-a")))
            .ReturnsAsync(CreateSettings(AlwaysActiveWindow("window-b")));

        var service = CreateService(settingsRepo.Object, sceneService.Object, timeProvider, coordinator, out _);

        await InvokeEvaluateWindowsAsync(service);
        sceneService.Verify(s => s.ApplySceneByIdAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        sceneService.Setup(s => s.ApplySceneByIdAsync("scene-b", "automation-scheduler", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "ok", "scene-b", new[] { "10.0.0.1" }));

        await InvokeEvaluateWindowsAsync(service);

        sceneService.Verify(s => s.ApplySceneByIdAsync("scene-b", "automation-scheduler", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateWindowsAsync_DoesNotStopWindowPlaybackWhileSonosConfigOwnsPlayback()
    {
        var timeProvider = new ManualTimeProvider(LocalTime(2026, 6, 11, 10, 30));
        var coordinator = new SchedulePriorityCoordinator();
        var connectorRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        var sceneService = new Mock<ISceneOrchestrationService>(MockBehavior.Strict);
        var settingsRepo = new Mock<ISettingsRepo>();

        settingsRepo.SetupSequence(repo => repo.GetSettings())
            .ReturnsAsync(CreateSettings(AlwaysActiveWindow("window-a")))
            .ReturnsAsync(CreateSettings());

        sceneService.Setup(s => s.ApplySceneByIdAsync("scene-a", "automation-scheduler", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SceneApplyResult(true, "ok", "scene-a", new[] { "10.0.0.1" }));

        var service = CreateService(settingsRepo.Object, sceneService.Object, timeProvider, coordinator, out var db, connectorRepo.Object);

        await InvokeEvaluateWindowsAsync(service);
        coordinator.NotifySonosConfigStart("window-a");
        await InvokeEvaluateWindowsAsync(service);

        connectorRepo.Verify(r => r.PausePlaying(It.IsAny<string>()), Times.Never);
        Assert.DoesNotContain(db.Logs, log => log.Action == "ScheduleWindowStopped");
    }

    private static ScheduleWindowAutomationService CreateService(
        ISettingsRepo settingsRepo,
        ISceneOrchestrationService sceneService,
        TimeProvider timeProvider,
        ISchedulePriorityCoordinator coordinator,
        out ApplicationDbContext db,
        ISonosConnectorRepo? connectorRepo = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        db = new ApplicationDbContext(options);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo);
        unitOfWork.SetupGet(u => u.ISonosConnectorRepo).Returns(connectorRepo ?? Mock.Of<ISonosConnectorRepo>());
        unitOfWork.SetupGet(u => u.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        var services = new ServiceCollection();
        services.AddSingleton(unitOfWork.Object);
        services.AddSingleton(sceneService);
        services.AddSingleton(db);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<ActionLogger>();
        var provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new ScheduleWindowAutomationService(
            scopeFactory,
            NullLogger<ScheduleWindowAutomationService>.Instance,
            timeProvider,
            coordinator);
    }

    private static Task InvokeEvaluateWindowsAsync(ScheduleWindowAutomationService service)
    {
        var method = typeof(ScheduleWindowAutomationService).GetMethod("EvaluateWindowsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, new object[] { CancellationToken.None })!;
    }

    private static SonosSettings CreateSettings(params ScheduleWindow[] windows)
    {
        return new SonosSettings
        {
            Speakers = new List<SonosSpeaker> { new() { IpAddress = "10.0.0.1", Name = "Living Room" } },
            Scenes = new List<Scene>
            {
                new() { Id = "scene-a", Name = "Scene A", Enabled = true },
                new() { Id = "scene-b", Name = "Scene B", Enabled = true }
            },
            ScheduleWindows = windows.ToList()
        };
    }

    private static ScheduleWindow AlwaysActiveWindow(string id)
    {
        return new ScheduleWindow
        {
            Id = id,
            Name = id,
            IsEnabled = true,
            Priority = 10,
            StartTime = new TimeOnly(0, 0),
            StopTime = new TimeOnly(23, 59),
            RecurrenceType = ScheduleRecurrenceType.Daily,
            SceneId = id == "window-a" ? "scene-a" : "scene-b",
            LastModifiedUtc = DateTime.UtcNow
        };
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }
}
