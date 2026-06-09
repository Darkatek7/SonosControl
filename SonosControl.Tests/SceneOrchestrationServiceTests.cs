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

public class SceneOrchestrationServiceTests
{
    [Fact]
    public async Task ApplySceneByIdAsync_WhenSourceFails_RetriesThenUsesFallbackRecovery()
    {
        var scene = new Scene
        {
            Id = "scene-main",
            Name = "Main",
            SourceType = SceneSourceType.Station,
            SourceUrl = "primary://stream",
            IsSyncedPlayback = false,
            Actions = new List<SceneAction>
            {
                new() { SpeakerIp = "192.168.0.10", IncludeInPlayback = true, Volume = 15 }
            }
        };

        var settings = new SonosSettings
        {
            Volume = 10,
            MaxVolume = 100,
            Speakers = new List<SonosSpeaker>
            {
                new() { Name = "Kitchen", IpAddress = "192.168.0.10" }
            },
            Scenes = new List<Scene> { scene },
            AutomationRules = new List<AutomationRule>
            {
                new()
                {
                    Id = "rule-source-failure",
                    Enabled = true,
                    TriggerType = AutomationTriggerType.SourceFailure,
                    ActionType = AutomationActionType.PlayFallbackSource,
                    FallbackSourceType = SceneSourceType.Station,
                    FallbackUrl = "fallback://stream",
                    RetryCount = 2,
                    RetryDelaySeconds = 0,
                    LastModifiedUtc = DateTime.UtcNow
                }
            }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var connectorRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        connectorRepo.Setup(repo => repo.UngroupSpeaker("192.168.0.10", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.SetSpeakerVolume("192.168.0.10", 15, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var primaryAttempts = 0;
        connectorRepo.Setup(repo => repo.SetTuneInStationAsync("192.168.0.10", "primary://stream", It.IsAny<CancellationToken>()))
            .Callback(() => primaryAttempts++)
            .ThrowsAsync(new InvalidOperationException("Primary source unavailable"));

        var fallbackAttempts = 0;
        connectorRepo.Setup(repo => repo.SetTuneInStationAsync("192.168.0.10", "fallback://stream", It.IsAny<CancellationToken>()))
            .Callback(() => fallbackAttempts++)
            .Returns(Task.CompletedTask);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo.Object);
        uow.SetupGet(value => value.ISonosConnectorRepo).Returns(connectorRepo.Object);
        uow.SetupGet(value => value.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var (actionLogger, db) = CreateActionLogger();
        var service = new SceneOrchestrationService(
            uow.Object,
            Mock.Of<IServiceScopeFactory>(),
            notificationService.Object,
            actionLogger,
            NullLogger<SceneOrchestrationService>.Instance);

        var result = await service.ApplySceneByIdAsync("scene-main", "tester", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RecoveryActivated);
        Assert.Equal(3, primaryAttempts);
        Assert.Equal(1, fallbackAttempts);
        Assert.Contains(db.Logs, log => log.Action == "RecoveryActivated");
    }

    [Fact]
    public async Task ApplySceneByIdAsync_WhenRecoveryTargetsSameScene_DoesNotRecurse()
    {
        var scene = new Scene
        {
            Id = "scene-loop",
            Name = "Loop",
            SourceType = SceneSourceType.Station,
            SourceUrl = "primary://stream",
            IsSyncedPlayback = false,
            Actions = new List<SceneAction>
            {
                new() { SpeakerIp = "192.168.0.11", IncludeInPlayback = true, Volume = 20 }
            }
        };

        var settings = new SonosSettings
        {
            Volume = 10,
            MaxVolume = 100,
            Speakers = new List<SonosSpeaker>
            {
                new() { Name = "Office", IpAddress = "192.168.0.11" }
            },
            Scenes = new List<Scene> { scene },
            AutomationRules = new List<AutomationRule>
            {
                new()
                {
                    Id = "rule-loop",
                    Enabled = true,
                    TriggerType = AutomationTriggerType.SourceFailure,
                    ActionType = AutomationActionType.ApplyScene,
                    SceneId = "scene-loop",
                    RetryCount = 1,
                    RetryDelaySeconds = 0,
                    LastModifiedUtc = DateTime.UtcNow
                }
            }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var connectorRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        connectorRepo.Setup(repo => repo.UngroupSpeaker("192.168.0.11", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.SetSpeakerVolume("192.168.0.11", 20, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var primaryAttempts = 0;
        connectorRepo.Setup(repo => repo.SetTuneInStationAsync("192.168.0.11", "primary://stream", It.IsAny<CancellationToken>()))
            .Callback(() => primaryAttempts++)
            .ThrowsAsync(new InvalidOperationException("Primary source unavailable"));

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo.Object);
        uow.SetupGet(value => value.ISonosConnectorRepo).Returns(connectorRepo.Object);
        uow.SetupGet(value => value.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var (actionLogger, db) = CreateActionLogger();
        var service = new SceneOrchestrationService(
            uow.Object,
            Mock.Of<IServiceScopeFactory>(),
            notificationService.Object,
            actionLogger,
            NullLogger<SceneOrchestrationService>.Instance);

        var result = await service.ApplySceneByIdAsync("scene-loop", "tester", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.RecoveryActivated);
        Assert.Equal(2, primaryAttempts);
        Assert.Contains(db.Logs, log => log.Action == "SceneApplyFailed");
    }

    [Fact]
    public async Task ApplySceneByIdAsync_WhenSceneUsesYouTubeMusic_StartsPlaybackOnTargetSpeaker()
    {
        var scene = new Scene
        {
            Id = "scene-youtube",
            Name = "YouTube Mix",
            SourceType = SceneSourceType.YouTubeMusic,
            SourceUrl = "https://music.youtube.com/playlist?list=PL123",
            IsSyncedPlayback = false,
            Actions = new List<SceneAction>
            {
                new() { SpeakerIp = "192.168.0.12", IncludeInPlayback = true, Volume = 22 }
            }
        };

        var settings = new SonosSettings
        {
            Volume = 10,
            MaxVolume = 100,
            AutoPlayStationUrl = "fallback://station",
            Speakers = new List<SonosSpeaker>
            {
                new() { Name = "Studio", IpAddress = "192.168.0.12" }
            },
            Scenes = new List<Scene> { scene }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var connectorRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        connectorRepo.Setup(repo => repo.UngroupSpeaker("192.168.0.12", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.SetSpeakerVolume("192.168.0.12", 22, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.PlayYouTubeMusicTrackAsync(
                "192.168.0.12",
                "https://music.youtube.com/playlist?list=PL123",
                "fallback://station",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo.Object);
        uow.SetupGet(value => value.ISonosConnectorRepo).Returns(connectorRepo.Object);
        uow.SetupGet(value => value.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var (actionLogger, db) = CreateActionLogger();
        var service = new SceneOrchestrationService(
            uow.Object,
            Mock.Of<IServiceScopeFactory>(),
            notificationService.Object,
            actionLogger,
            NullLogger<SceneOrchestrationService>.Instance);

        var result = await service.ApplySceneByIdAsync("scene-youtube", "tester", CancellationToken.None);

        Assert.True(result.Success);
        connectorRepo.Verify(repo => repo.PlayYouTubeMusicTrackAsync(
            "192.168.0.12",
            "https://music.youtube.com/playlist?list=PL123",
            "fallback://station",
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(db.Logs, log => log.Action == "SceneApplied");
    }

    [Fact]
    public async Task ApplySceneByIdAsync_WhenSceneUsesYouTube_StartsPlaybackOnTargetSpeaker()
    {
        var scene = new Scene
        {
            Id = "scene-youtube-video",
            Name = "YouTube Video",
            SourceType = SceneSourceType.YouTube,
            SourceUrl = "https://www.youtube.com/watch?v=abc123xyz00",
            IsSyncedPlayback = false,
            Actions = new List<SceneAction>
            {
                new() { SpeakerIp = "192.168.0.13", IncludeInPlayback = true, Volume = 18 }
            }
        };

        var settings = new SonosSettings
        {
            Volume = 10,
            MaxVolume = 100,
            Speakers = new List<SonosSpeaker>
            {
                new() { Name = "Ops", IpAddress = "192.168.0.13" }
            },
            Scenes = new List<Scene> { scene }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var connectorRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        connectorRepo.Setup(repo => repo.UngroupSpeaker("192.168.0.13", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.SetSpeakerVolume("192.168.0.13", 18, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.PlayYouTubeAudioAsync(
                "192.168.0.13",
                "https://www.youtube.com/watch?v=abc123xyz00",
                It.IsAny<YouTubePlaybackMode?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo.Object);
        uow.SetupGet(value => value.ISonosConnectorRepo).Returns(connectorRepo.Object);
        uow.SetupGet(value => value.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var (actionLogger, db) = CreateActionLogger();
        var service = new SceneOrchestrationService(
            uow.Object,
            Mock.Of<IServiceScopeFactory>(),
            notificationService.Object,
            actionLogger,
            NullLogger<SceneOrchestrationService>.Instance);

        var result = await service.ApplySceneByIdAsync("scene-youtube-video", "tester", CancellationToken.None);

        Assert.True(result.Success);
        connectorRepo.Verify(repo => repo.PlayYouTubeAudioAsync(
            "192.168.0.13",
            "https://www.youtube.com/watch?v=abc123xyz00",
            It.IsAny<YouTubePlaybackMode?>(),
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(db.Logs, log => log.Action == "SceneApplied");
    }

    private static (ActionLogger Logger, ApplicationDbContext Db) CreateActionLogger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"scene-orchestration-tests-{Guid.NewGuid():N}")
            .Options;

        var db = new ApplicationDbContext(options);
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var logger = new ActionLogger(db, httpContextAccessor);
        return (logger, db);
    }
}
