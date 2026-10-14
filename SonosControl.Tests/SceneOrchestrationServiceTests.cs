using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SceneOrchestrationServiceTests
{
    [Fact]
    public async Task SuccessfulManualPrimaryDoesNotClaimUnneededRecoveryRoom()
    {
        var now = TimeProvider.System.GetUtcNow();
        var commands = new PlaybackCommandCoordinator(TimeProvider.System);
        var scheduledB = await commands.EnterAutomationAsync(["B"], now.AddMinutes(-1));
        Assert.NotNull(scheduledB);
        var primary = new Scene { Id = "primary", Name = "Primary", Actions = [new() { SpeakerIp = "A" }] };
        var recovery = new Scene { Id = "recovery", Name = "Recovery", Actions = [new() { SpeakerIp = "B" }] };
        var settings = new SonosSettings
        {
            Scenes = [primary, recovery],
            Speakers = [new() { IpAddress = "A" }, new() { IpAddress = "B" }],
            AutomationRules = [new() { TriggerType = AutomationTriggerType.SourceFailure, ActionType = AutomationActionType.ApplyScene, SceneId = "recovery" }]
        };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.UngroupSpeaker(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.StartPlaying("A", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var (logger, _) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(settingsRepo.Object, connector.Object, timers,
            Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance, commands);

        var result = await service.ApplySceneByIdAsync("primary", "tester");

        Assert.True(result.Success);
        Assert.False(scheduledB.CancellationToken.IsCancellationRequested);
        Assert.DoesNotContain("B", commands.ManualSpeakersSince(now.AddMinutes(-1)));
        connector.Verify(repo => repo.StartPlaying("B", It.IsAny<CancellationToken>()), Times.Never);
        await scheduledB.DisposeAsync();
    }

    [Fact]
    public async Task AutomationRecoverySwapsOwnershipToRecoveryTargetsAndCanBePreemptedThere()
    {
        var now = TimeProvider.System.GetUtcNow();
        var commands = new PlaybackCommandCoordinator(TimeProvider.System);
        var primaryLease = await commands.EnterAutomationAsync(["A"], now.AddMinutes(-1));
        Assert.NotNull(primaryLease);
        var main = new Scene { Id = "main", Name = "Main", SourceType = SceneSourceType.Station, SourceUrl = "bad://source", Actions = [new() { SpeakerIp = "A" }] };
        var recovery = new Scene { Id = "recovery", Name = "Recovery", Actions = [new() { SpeakerIp = "B" }] };
        var settings = new SonosSettings
        {
            Scenes = [main, recovery],
            Speakers = [new() { IpAddress = "A" }, new() { IpAddress = "B" }],
            AutomationRules = [new() { TriggerType = AutomationTriggerType.SourceFailure, ActionType = AutomationActionType.ApplyScene, SceneId = "recovery", RetryCount = 0 }]
        };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.UngroupSpeaker(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.SetSpeakerVolume(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.SetTuneInStationAsync("A", "bad://source", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("source failed"));
        connector.Setup(repo => repo.StartPlaying("B", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var (logger, _) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(settingsRepo.Object, connector.Object, timers,
            Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance, commands);
        await using var context = new SceneExecutionContext(commands, PlaybackCommandOrigin.Automation, now.AddMinutes(-1), CancellationToken.None, primaryLease);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = await service.ApplySceneByIdWithContextAsync("main", "automation-scheduler", context,
            targets => touched.UnionWith(targets), CancellationToken.None, ["A"]);

        Assert.True(result.Success);
        Assert.Equal(new[] { "B" }, result.TargetSpeakers);
        Assert.Contains("A", touched);
        Assert.Contains("B", touched);
        Assert.Equal(new[] { "B" }, context.CurrentLease!.Speakers);
        var manualTask = commands.EnterManualAsync(["B"]);
        await WaitUntilAsync(() => context.CancellationToken.IsCancellationRequested);
        await context.DisposeAsync();
        await using var manualLease = await manualTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(context.WasPreempted);
    }

    [Fact]
    public async Task AutomationRecoveryDoesNotResolveOrClaimDisabledRecoverySceneTargets()
    {
        var now = TimeProvider.System.GetUtcNow();
        var commands = new PlaybackCommandCoordinator(TimeProvider.System);
        var primaryLease = await commands.EnterAutomationAsync(["A"], now.AddMinutes(-1));
        Assert.NotNull(primaryLease);
        var primary = new Scene
        {
            Id = "primary",
            Name = "Primary",
            SourceType = SceneSourceType.Station,
            SourceUrl = "bad://station",
            Actions = [new() { SpeakerIp = "A" }]
        };
        var disabledRecovery = new Scene
        {
            Id = "disabled-recovery",
            Name = "Disabled recovery",
            Enabled = false,
            Actions = [new() { SpeakerIp = "B" }]
        };
        var settings = new SonosSettings
        {
            Scenes = [primary, disabledRecovery],
            Speakers = [new() { IpAddress = "A" }, new() { IpAddress = "B" }],
            AutomationRules = [new()
            {
                Id = "source-failure",
                Enabled = true,
                TriggerType = AutomationTriggerType.SourceFailure,
                ActionType = AutomationActionType.ApplyScene,
                SceneId = disabledRecovery.Id,
                RetryCount = 0
            }]
        };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var connector = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        connector.Setup(repo => repo.GetAllSpeakersInGroup("A", It.IsAny<CancellationToken>())).ReturnsAsync(["A"]);
        connector.Setup(repo => repo.UngroupSpeaker("A", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.SetTuneInStationAsync("A", "bad://station", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("primary source failed"));
        var (logger, _) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(settingsRepo.Object, connector.Object, timers,
            Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance, commands);
        await using var context = new SceneExecutionContext(commands, PlaybackCommandOrigin.Automation,
            now.AddMinutes(-1), CancellationToken.None, primaryLease);
        var observedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = await service.ApplySceneByIdWithContextAsync("primary", "automation-scheduler", context,
            targets => observedTargets.UnionWith(targets), context.CancellationToken, ["A"]);

        Assert.False(result.Success);
        Assert.Equal(new[] { "A" }, result.TargetSpeakers);
        Assert.Equal(new[] { "A" }, observedTargets.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "A" }, context.CurrentLease!.Speakers);
        Assert.Empty(commands.ManualSpeakersSince(now.AddMinutes(-1)));
        connector.Verify(repo => repo.GetAllSpeakersInGroup("B", It.IsAny<CancellationToken>()), Times.Never);
        connector.Verify(repo => repo.UngroupSpeaker("B", It.IsAny<CancellationToken>()), Times.Never);
        connector.Verify(repo => repo.SetTuneInStationAsync("B", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SchedulerCancellationDuringRecoveryRemainsLinkedAfterOwnershipTransfer()
    {
        using var lifetime = new CancellationTokenSource();
        var commands = new PlaybackCommandCoordinator(TimeProvider.System);
        var primaryLease = await commands.EnterAutomationAsync(["A"], TimeProvider.System.GetUtcNow().AddMinutes(-1), lifetime.Token);
        Assert.NotNull(primaryLease);
        var main = new Scene { Id = "main", Name = "Main", SourceType = SceneSourceType.Station, SourceUrl = "bad://source", Actions = [new() { SpeakerIp = "A" }] };
        var recovery = new Scene { Id = "recovery", Name = "Recovery", Actions = [new() { SpeakerIp = "B" }] };
        var settings = new SonosSettings
        {
            Scenes = [main, recovery],
            Speakers = [new() { IpAddress = "A" }, new() { IpAddress = "B" }],
            AutomationRules = [new() { TriggerType = AutomationTriggerType.SourceFailure, ActionType = AutomationActionType.ApplyScene, SceneId = "recovery", RetryCount = 0 }]
        };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.UngroupSpeaker(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.SetSpeakerVolume(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.SetTuneInStationAsync("A", "bad://source", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("source failed"));
        connector.Setup(repo => repo.StartPlaying("B", It.IsAny<CancellationToken>())).Returns(async (string _, CancellationToken token) =>
        {
            recoveryStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var (logger, _) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(settingsRepo.Object, connector.Object, timers,
            Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance, commands);
        await using var context = new SceneExecutionContext(commands, PlaybackCommandOrigin.Automation, TimeProvider.System.GetUtcNow().AddMinutes(-1),
            lifetime.Token, primaryLease);
        var applying = service.ApplySceneByIdWithContextAsync("main", "automation-scheduler", context, null,
            context.CancellationToken, ["A"]);

        await recoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(context.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ManualTakeoverCancelsScheduledRetryDelayBeforeAnotherSourceWrite()
    {
        var commands = new PlaybackCommandCoordinator(TimeProvider.System);
        var started = TimeProvider.System.GetUtcNow().AddMinutes(-1);
        var primaryLease = await commands.EnterAutomationAsync(["A"], started);
        Assert.NotNull(primaryLease);
        var scene = new Scene { Id = "main", Name = "Main", SourceType = SceneSourceType.Station, SourceUrl = "bad://source", Actions = [new() { SpeakerIp = "A" }] };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Scenes = [scene],
            Speakers = [new() { IpAddress = "A" }],
            AutomationRules = [new() { TriggerType = AutomationTriggerType.SourceFailure, ActionType = AutomationActionType.ApplyScene, RetryCount = 1, RetryDelaySeconds = 300 }]
        });
        var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.UngroupSpeaker("A", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.SetTuneInStationAsync("A", "bad://source", It.IsAny<CancellationToken>())).Returns(() =>
        {
            firstFailure.TrySetResult();
            return Task.FromException(new InvalidOperationException("source failed"));
        });
        var (logger, _) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(settingsRepo.Object, connector.Object, timers,
            Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance, commands);
        await using var context = new SceneExecutionContext(commands, PlaybackCommandOrigin.Automation, started, CancellationToken.None, primaryLease);
        var applying = service.ApplySceneByIdWithContextAsync("main", "automation-scheduler", context, null, context.CancellationToken, ["A"]);

        await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var manualTask = commands.EnterManualAsync(["A"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying.WaitAsync(TimeSpan.FromSeconds(2)));
        await context.DisposeAsync();
        await using var manualLease = await manualTask.WaitAsync(TimeSpan.FromSeconds(2));
        connector.Verify(repo => repo.SetTuneInStationAsync("A", "bad://source", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScheduledSceneClaimsCurrentGroupButWritesOnlySelectedTargets()
    {
        var commands = new PlaybackCommandCoordinator(TimeProvider.System);
        var started = TimeProvider.System.GetUtcNow().AddMinutes(-1);
        var primaryLease = await commands.EnterAutomationAsync(["A"], started);
        Assert.NotNull(primaryLease);
        var scene = new Scene { Id = "main", Name = "Main", Actions = [new() { SpeakerIp = "A" }] };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Scenes = [scene],
            Speakers = [new() { IpAddress = "A" }, new() { IpAddress = "B" }]
        });
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetAllSpeakersInGroup("A", It.IsAny<CancellationToken>())).ReturnsAsync(["A", "B"]);
        connector.Setup(repo => repo.UngroupSpeaker("A", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.SetSpeakerVolume("A", It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.StartPlaying("A", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var (logger, _) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(settingsRepo.Object, connector.Object, timers,
            Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance, commands);
        await using var context = new SceneExecutionContext(commands, PlaybackCommandOrigin.Automation, started, CancellationToken.None, primaryLease);

        var result = await service.ApplySceneByIdWithContextAsync("main", "automation-scheduler", context, null, context.CancellationToken, ["A"]);

        Assert.True(result.Success);
        Assert.Equal(["A", "B"], context.CurrentLease!.Speakers);
        connector.Verify(repo => repo.UngroupSpeaker("A", It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.UngroupSpeaker("B", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManualApplyReplansOnceWhenSceneChangesWhileWaitingForAutomationToStop()
    {
        var now = TimeProvider.System.GetUtcNow();
        var commands = new PlaybackCommandCoordinator(TimeProvider.System);
        var scheduled = await commands.EnterAutomationAsync(["A"], now.AddMinutes(-1));
        Assert.NotNull(scheduled);
        var scene = new Scene
        {
            Id = "manual",
            Name = "Manual",
            Actions = [new SceneAction { SpeakerIp = "A", IncludeInPlayback = true }]
        };
        var settings = new SonosSettings { Scenes = [scene], Speakers = [new() { IpAddress = "A" }, new() { IpAddress = "B" }] };
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.UngroupSpeaker("B", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.StartPlaying("B", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var (logger, _) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(repository.Object, connector.Object, timers,
            Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance, commands);

        var applying = service.ApplySceneByIdAsync("manual", "tester");
        await WaitUntilAsync(() => commands.ManualSpeakersSince(now.AddMinutes(-1)).Contains("A"));
        scene.Actions = [new SceneAction { SpeakerIp = "B", IncludeInPlayback = true }];
        scene.LastModifiedUtc = DateTime.UtcNow.AddSeconds(1);
        await scheduled.DisposeAsync();

        var result = await applying.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Success);
        connector.Verify(repo => repo.UngroupSpeaker("A", It.IsAny<CancellationToken>()), Times.Never);
        connector.Verify(repo => repo.StartPlaying("A", It.IsAny<CancellationToken>()), Times.Never);
        connector.Verify(repo => repo.StartPlaying("B", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublicManualApplyClaimsAndPreemptsAnOverlappingScheduledCommand()
    {
        var time = TimeProvider.System;
        var now = time.GetUtcNow();
        var commands = new PlaybackCommandCoordinator(time);
        var scheduled = await commands.EnterAutomationAsync(["A"], now.AddMinutes(-1));
        Assert.NotNull(scheduled);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = scheduled.CancellationToken.Register(cancellationObserved.SetResult);

        var scene = new Scene
        {
            Id = "manual",
            Name = "Manual",
            Enabled = true,
            Actions = [new SceneAction { SpeakerIp = "A", IncludeInPlayback = true }]
        };
        var settings = new Mock<ISettingsRepo>();
        settings.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Scenes = [scene],
            Speakers = [new SonosSpeaker { IpAddress = "A" }]
        });
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.UngroupSpeaker("A", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(repo => repo.StartPlaying("A", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var (logger, _) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(settings.Object, connector.Object, timers,
            Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance, commands);

        var applying = service.ApplySceneByIdAsync("manual", "tester");
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scheduled.DisposeAsync();
        var result = await applying.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Success);
        Assert.Contains("A", commands.ManualSpeakersSince(now.AddMinutes(-1)));
        connector.Verify(repo => repo.StartPlaying("A", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(501, true)]
    [InlineData(714, false)]
    public async Task UnsupportedGroupingFallsBackToIndependentPlayback_ButOtherCommandErrorsRemainFailures(int upnpCode, bool succeeds)
    {
        var scene = new Scene
        {
            Id = "group",
            Name = "Grouped radio",
            IsSyncedPlayback = true,
            SourceType = SceneSourceType.Station,
            SourceUrl = "https://radio.example/stream",
            Actions = [new() { SpeakerIp = "A", IncludeInPlayback = true }, new() { SpeakerIp = "B", IncludeInPlayback = true }]
        };
        var settings = new Mock<ISettingsRepo>();
        settings.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Scenes = [scene],
            Speakers = [new() { IpAddress = "A" }, new() { IpAddress = "B" }]
        });
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.CreateGroup("A", It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SonosCommandException("B", "AVTransport", "SetAVTransportURI", System.Net.HttpStatusCode.InternalServerError, upnpCode, "UPnPError"));
        var (logger, db) = CreateActionLogger();
        await using var timers = RepositoryTestServices.CreateTimers();
        var service = new SceneOrchestrationService(settings.Object, connector.Object, timers, Mock.Of<INotificationService>(), logger, NullLogger<SceneOrchestrationService>.Instance);
        var result = await service.ApplySceneByIdAsync("group", "automation-scheduler");
        Assert.Equal(succeeds, result.Success);
        foreach (var ip in new[] { "A", "B" })
            connector.Verify(repo => repo.SetTuneInStationAsync(ip, scene.SourceUrl, It.IsAny<CancellationToken>()), succeeds ? Times.Once() : Times.Never());
        if (succeeds)
        {
            Assert.Contains("independently", result.Message);
            Assert.Contains(db.Logs, log => log.Action == "SceneGroupingUnavailable");
            connector.Verify(repo => repo.UngroupSpeaker(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
        }
        else Assert.Equal(upnpCode, result.CommandFailure!.UpnpErrorCode);
    }

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

        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var (actionLogger, db) = CreateActionLogger();
        var service = new SceneOrchestrationService(
            settingsRepo.Object, connectorRepo.Object,
            RepositoryTestServices.CreateTimers(),
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

        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var (actionLogger, db) = CreateActionLogger();
        var service = new SceneOrchestrationService(
            settingsRepo.Object, connectorRepo.Object,
            RepositoryTestServices.CreateTimers(),
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

        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var (actionLogger, db) = CreateActionLogger();
        var service = new SceneOrchestrationService(
            settingsRepo.Object, connectorRepo.Object,
            RepositoryTestServices.CreateTimers(),
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

        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var (actionLogger, db) = CreateActionLogger();
        var service = new SceneOrchestrationService(
            settingsRepo.Object, connectorRepo.Object,
            RepositoryTestServices.CreateTimers(),
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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(predicate(), "Condition was not reached before timeout.");
    }
}
