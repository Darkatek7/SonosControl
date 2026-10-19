using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Linq.Expressions;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class AutomationSchedulerServiceTests
{
    [Fact]
    public async Task AManuallyControlledNewWindowStillStopsUnclaimedTargetsFromTheOldWindow()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(Settings(
            Window("current", "scene-a", new(9, 0), new(11, 0), 10),
            Window("next", "scene-b", new(11, 0), new(13, 0), 20)));
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));
        var connector = new Mock<ISonosConnectorRepo>();
        using var fixture = CreateScheduler(repository.Object, connector.Object, scenes.Object, time, commands: commands);
        await fixture.Service.EvaluateNowAsync();
        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 11, 1, 0, TimeSpan.Zero));
        await using (await commands.EnterAsync(true)) commands.TakeManualControl(["10.0.0.2"]);
        await fixture.Service.EvaluateNowAsync();
        await fixture.Service.EvaluateNowAsync();
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-b"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Never);
        connector.Verify(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.PausePlaying("10.0.0.2", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManualPreemptionOfOneIndependentSpeakerStillAppliesTheOtherSpeaker()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var settings = Settings(Window("current", "scene-a", new(9, 0), new(11, 0), 10));
        settings.Scenes[0].IsSyncedPlayback = false;
        settings.Scenes[0].Actions.Add(new SceneAction { SpeakerIp = "10.0.0.2", IncludeInPlayback = true });
        settings.ScheduleWindows[0].Playback!.IsSyncedPlayback = false;
        settings.ScheduleWindows[0].Playback!.Actions.Add(new SceneAction { SpeakerIp = "10.0.0.2", IncludeInPlayback = true });
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(
                It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .Returns(async (Scene _, string? _, SceneExecutionContext context, Action<IReadOnlyCollection<string>>? record,
                CancellationToken token, IReadOnlyCollection<string>? targets, bool _) =>
            {
                record?.Invoke(targets ?? []);
                if (targets?.Contains("10.0.0.1") == true)
                {
                    aStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                bStarted.TrySetResult();
                return new SceneApplyResult(true, "Applied", "scene-a", targets?.ToList() ?? []);
            });
        using var fixture = CreateScheduler(repository.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time, commands: commands);

        var evaluation = fixture.Service.EvaluateNowAsync();
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await using (await commands.EnterManualAsync(["10.0.0.1"]))
        {
            await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await evaluation.WaitAsync(TimeSpan.FromSeconds(2));

        scenes.Verify(service => service.ApplySceneWithContextAsync(
            It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ManualRecoveryRoomDoesNotBlockScheduledPrimaryRoom()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var settings = Settings(Window("current", "scene-a", new(9, 0), new(11, 0), 10));
        settings.Scenes.Add(new Scene { Id = "recovery", Name = "Recovery", Actions = [new() { SpeakerIp = "10.0.0.2" }] });
        settings.AutomationRules = [new() { TriggerType = AutomationTriggerType.SourceFailure, ActionType = AutomationActionType.ApplyScene, SceneId = "recovery" }];
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
                It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));
        using var fixture = CreateScheduler(repository.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time, commands: commands);
        await using var manualRecoveryRoom = await commands.EnterManualAsync(["10.0.0.2"]);

        await fixture.Service.EvaluateNowAsync();

        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
            It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Once);
        Assert.Equal("current", fixture.Status.Snapshot.ActiveScheduleName);
    }

    [Fact]
    public async Task ScheduledRetryRetainsRecoveryTargetsTouchedByEarlierFailureForCleanup()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var settings = Settings(Window("current", "scene-a", new(9, 0), new(11, 0), 10));
        settings.Scenes.Add(new Scene { Id = "recovery", Name = "Recovery", Actions = [new() { SpeakerIp = "10.0.0.2" }] });
        settings.AutomationRules = [new() { TriggerType = AutomationTriggerType.SourceFailure, ActionType = AutomationActionType.ApplyScene, SceneId = "recovery" }];
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var scenes = new Mock<ISceneOrchestrationService>();
        var attempt = 0;
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
                It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .Returns((Scene _, string? _, SceneExecutionContext _, Action<IReadOnlyCollection<string>>? record, CancellationToken _, IReadOnlyCollection<string>? _, bool _) =>
            {
                attempt++;
                record?.Invoke(["10.0.0.1"]);
                if (attempt == 1)
                {
                    record?.Invoke(["10.0.0.2"]);
                    return Task.FromResult(new SceneApplyResult(false, "Recovery failed", "scene-a", ["10.0.0.1"]));
                }

                return Task.FromResult(new SceneApplyResult(true, "Primary recovered", "scene-a", ["10.0.0.1"]));
            });
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.PausePlaying(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var fixture = CreateScheduler(repository.Object, connector.Object, scenes.Object, time, commands: commands);

        await fixture.Service.EvaluateNowAsync();
        time.SetUtcNow(fixture.Status.Snapshot.NextRetryUtc!.Value);
        await fixture.Service.EvaluateNowAsync();
        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 11, 1, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();

        connector.Verify(repo => repo.PausePlaying("10.0.0.2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManualTakeoverDuringFadeInCancelsFurtherVolumeWrites()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var window = Window("current", "scene-a", new(9, 0), new(11, 0), 10);
        window.FadeInSeconds = 20;
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(Settings(window));
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
                It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));
        var fadeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(20);
        connector.Setup(repo => repo.SetSpeakerVolume("10.0.0.1", 1, It.IsAny<CancellationToken>()))
            .Returns(async (string _, int _, CancellationToken token) =>
            {
                fadeStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });
        using var fixture = CreateScheduler(repository.Object, connector.Object, scenes.Object, time, commands: commands);

        var evaluation = fixture.Service.EvaluateNowAsync();
        await fadeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await using var manualLease = await commands.EnterManualAsync(["10.0.0.1"]).WaitAsync(TimeSpan.FromSeconds(2));
        await evaluation.WaitAsync(TimeSpan.FromSeconds(2));

        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManualPlaybackWaitsForScheduledBoundaryStopDuringFadeOut()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var window = Window("current", "scene-a", new(9, 0), new(11, 0), 10);
        window.FadeOutSeconds = 30;
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(Settings(window));
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
                It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));
        var fadeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(20);
        connector.Setup(repo => repo.SetSpeakerVolume("10.0.0.1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, int _, CancellationToken token) =>
            {
                fadeStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });
        connector.Setup(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var fixture = CreateScheduler(repository.Object, connector.Object, scenes.Object, time, commands: commands);

        await fixture.Service.EvaluateNowAsync();
        await using (await commands.EnterManualAsync(["10.0.0.1"]).WaitAsync(TimeSpan.FromSeconds(2))) { }
        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 11, 1, 0, TimeSpan.Zero));
        var evaluation = fixture.Service.EvaluateNowAsync();
        var fadeOrEvaluation = await Task.WhenAny(fadeStarted.Task, evaluation).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ReferenceEquals(fadeStarted.Task, fadeOrEvaluation), fixture.Status.Snapshot.SchedulerError);
        await using var manualLease = await commands.EnterManualAsync(["10.0.0.1"]).WaitAsync(TimeSpan.FromSeconds(2));
        await evaluation.WaitAsync(TimeSpan.FromSeconds(2));

        connector.Verify(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeletingWindowAfterPartialRecoveryFailureStopsSnapshotTargets()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(Window("current", "scene-a", new(9, 0), new(11, 0), 10));
        settings.Scenes.Add(new Scene { Id = "recovery", Name = "Recovery", Actions = [new() { SpeakerIp = "10.0.0.2" }] });
        settings.AutomationRules = [new() { TriggerType = AutomationTriggerType.SourceFailure, ActionType = AutomationActionType.ApplyScene, SceneId = "recovery" }];
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
                It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .Returns((Scene _, string? _, SceneExecutionContext _, Action<IReadOnlyCollection<string>>? record, CancellationToken _, IReadOnlyCollection<string>? _, bool _) =>
            {
                record?.Invoke(["10.0.0.1"]);
                record?.Invoke(["10.0.0.2"]);
                return Task.FromResult(new SceneApplyResult(false, "Recovery failed", "scene-a", ["10.0.0.1"]));
            });
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.PausePlaying(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var fixture = CreateScheduler(repository.Object, connector.Object, scenes.Object, time);

        await fixture.Service.EvaluateNowAsync();
        settings.ScheduleWindows.Clear();
        await fixture.Service.EvaluateNowAsync();

        connector.Verify(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.PausePlaying("10.0.0.2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SchedulerLeaseIncludesCurrentGroupMembersBeforeOrchestration()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(Settings(Window("current", "scene-a", new(9, 0), new(11, 0), 10)));
        var capturedTargets = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
                It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .Returns((Scene _, string? _, SceneExecutionContext context, Action<IReadOnlyCollection<string>>? _, CancellationToken _, IReadOnlyCollection<string>? _, bool _) =>
            {
                capturedTargets.TrySetResult(context.CurrentLease!.Speakers);
                return Task.FromResult(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));
            });
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetAllSpeakersInGroup("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(["10.0.0.1", "10.0.0.2"]);
        using var fixture = CreateScheduler(repository.Object, connector.Object, scenes.Object, time, commands: commands);

        await fixture.Service.EvaluateNowAsync();

        Assert.Equal(["10.0.0.1", "10.0.0.2"], await capturedTargets.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPlaybackPreventsRetriesButScheduledPlaybackStillStopsAtBoundary(bool firstAttemptFails)
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var settings = Settings(
            Window("current", "scene-a", new(9, 0), new(11, 0), 10),
            Window("next", "scene-b", new(11, 0), new(13, 0), 20));
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(!firstAttemptFails, "Attempt", "scene-a", ["10.0.0.1"]));
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-b"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Next", "scene-b", ["10.0.0.2"]));
        var connector = new Mock<ISonosConnectorRepo>();
        using var fixture = CreateScheduler(repository.Object, connector.Object, scenes.Object, time, commands: commands);
        await fixture.Service.EvaluateNowAsync();
        await using (await commands.EnterAsync(true)) commands.TakeManualControl(["10.0.0.1"]);
        time.SetUtcNow(time.GetUtcNow().AddMinutes(2));
        await fixture.Service.EvaluateNowAsync();
        Assert.Null(fixture.Status.Snapshot.NextRetryUtc);
        Assert.Null(fixture.Status.Snapshot.SchedulerError);
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Once);
        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 11, 1, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-b"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Once);
        connector.Verify(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManualPlaybackBeforeTheFirstPollSuppressesOnlyTheCurrentOvernightOccurrence()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 23, 0, 0, TimeSpan.Zero));
        var commands = new PlaybackCommandCoordinator(time);
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(repo => repo.GetSettings()).ReturnsAsync(Settings(Window("night", "scene-a", new(22, 0), new(2, 0), 10)));
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Next day", "scene-a", ["10.0.0.1"]));
        using var fixture = CreateScheduler(repository.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time, commands: commands);
        await using (await commands.EnterAsync(true)) commands.TakeManualControl(["10.0.0.1"]);
        time.SetUtcNow(new DateTimeOffset(2026, 7, 21, 1, 0, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.IsAny<Scene>(), It.IsAny<string>(), It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Never);
        time.SetUtcNow(new DateTimeOffset(2026, 7, 21, 23, 0, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Once);
    }

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
        connector.Setup(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied A", "scene-a", ["10.0.0.1"]));
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-b"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied B", "scene-b", ["10.0.0.2"]));

        using var fixture = CreateScheduler(settingsRepo.Object, connector.Object, scenes.Object, time);

        await fixture.Service.EvaluateNowAsync();
        Assert.Equal("window-a", fixture.Status.Snapshot.ActiveScheduleName);

        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 11, 30, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();

        connector.Verify(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Once);
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-b"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Once);
        Assert.Equal("window-b", fixture.Status.Snapshot.ActiveScheduleName);
        Assert.Equal(2, fixture.Db.Logs.Count(log => log.Action == "ScheduleTriggered"));
        Assert.Single(fixture.Db.Logs.Where(log => log.Action == "ScheduleStopped"));
    }

    [Fact]
    public async Task EvaluateNowAsync_RetriesFailedSceneAtReportedRetryTime()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(Window("window-a", "scene-a", new TimeOnly(9, 0), new TimeOnly(11, 0), priority: 10));
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.SetupSequence(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(false, "Speaker unavailable", "scene-a", []))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));

        using var fixture = CreateScheduler(settingsRepo.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time);

        await fixture.Service.EvaluateNowAsync();
        Assert.Contains("Speaker unavailable", fixture.Status.Snapshot.SchedulerError);
        Assert.Null(fixture.Status.Snapshot.ActiveScheduleName);

        await fixture.Service.EvaluateNowAsync();
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Once);
        Assert.NotNull(fixture.Status.Snapshot.NextRetryUtc);
        time.SetUtcNow(fixture.Status.Snapshot.NextRetryUtc!.Value);
        await fixture.Service.EvaluateNowAsync();
        Assert.Null(fixture.Status.Snapshot.SchedulerError);
        Assert.Equal("window-a", fixture.Status.Snapshot.ActiveScheduleName);
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Exactly(2));
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
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.IsAny<Scene>(), It.IsAny<string?>(), It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Never);
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
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));

        using var fixture = CreateScheduler(settingsRepo.Object, Mock.Of<ISonosConnectorRepo>(), scenes.Object, time, timeZone: timeZone);

        await fixture.Service.EvaluateNowAsync();

        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Once);
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
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(20);
        connector.Setup(repo => repo.SetSpeakerVolume("10.0.0.1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connector.Setup(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));

        using var fixture = CreateScheduler(settingsRepo.Object, connector.Object, scenes.Object, time);

        await fixture.Service.EvaluateNowAsync();
        time.SetUtcNow(new DateTimeOffset(2026, 7, 20, 11, 1, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();

        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", 1, It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", 10, It.IsAny<CancellationToken>()), Times.Exactly(2));
        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", 20, It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.SetSpeakerVolume("10.0.0.1", 0, It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(repo => repo.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EvaluateNowAsync_AfterRestart_ReappliesTheActiveScene()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(Window("window-a", "scene-a", new TimeOnly(9, 0), new TimeOnly(11, 0), priority: 10));
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
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
            service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()),
            Times.Exactly(2));
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public async Task EvaluateNowAsync_GlobalHoliday_DoesNotApplyAnyScene(int year)
    {
        var time = new ManualTimeProvider(new DateTimeOffset(year, 12, 25, 10, 0, 0, TimeSpan.Zero));
        var settings = Settings(
            Window("weekday", "scene-a", new TimeOnly(9, 0), new TimeOnly(12, 0), 10),
            Window("fallback", "scene-b", new TimeOnly(9, 0), new TimeOnly(12, 0), 20));
        settings.AnnualAutomationExcludedDates = [new DateOnly(2026, 12, 25)];
        var repo = new Mock<ISettingsRepo>();
        repo.Setup(value => value.GetSettings()).ReturnsAsync(settings);
        var scenes = new Mock<ISceneOrchestrationService>();
        var connector = new Mock<ISonosConnectorRepo>();
        using var fixture = CreateScheduler(repo.Object, connector.Object, scenes.Object, time);

        await fixture.Service.EvaluateNowAsync();

        scenes.Verify(service => service.ApplySceneWithContextAsync(It.IsAny<Scene>(), It.IsAny<string?>(), It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Never);
        connector.Verify(service => service.PausePlaying(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(fixture.Status.Snapshot.ActiveScheduleName);
    }

    [Fact]
    public async Task EvaluateNowAsync_GlobalHoliday_StopsOvernightPlaybackAtLocalMidnightAndResumesNextDay()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
        // 22:30 UTC is 23:30 in Vienna on Christmas Eve.
        var time = new ManualTimeProvider(new DateTimeOffset(2027, 12, 24, 22, 30, 0, TimeSpan.Zero));
        var overnight = Window("overnight", "scene-a", new TimeOnly(22, 0), new TimeOnly(2, 0), 10);
        overnight.FadeOutSeconds = 30;
        var settings = Settings(overnight);
        settings.AnnualAutomationExcludedDates = [new DateOnly(2026, 12, 25)];
        var repo = new Mock<ISettingsRepo>();
        repo.Setup(value => value.GetSettings()).ReturnsAsync(settings);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(value => value.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var scenes = new Mock<ISceneOrchestrationService>();
        scenes.Setup(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(true, "Applied", "scene-a", ["10.0.0.1"]));
        using var fixture = CreateScheduler(repo.Object, connector.Object, scenes.Object, time, timeZone: timeZone);

        await fixture.Service.EvaluateNowAsync();
        Assert.Equal("overnight", fixture.Status.Snapshot.ActiveScheduleName);

        // Christmas begins locally, while it is still December 24 in UTC.
        time.SetUtcNow(new DateTimeOffset(2027, 12, 24, 23, 0, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();
        await fixture.Service.EvaluateNowAsync();
        connector.Verify(value => value.PausePlaying("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
        connector.Verify(value => value.SetSpeakerVolume(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(fixture.Status.Snapshot.ActiveScheduleName);

        time.SetUtcNow(new DateTimeOffset(2027, 12, 26, 22, 0, 0, TimeSpan.Zero));
        await fixture.Service.EvaluateNowAsync();
        scenes.Verify(service => service.ApplySceneWithContextAsync(It.Is<Scene>(scene => scene.Id == "scene-a"), "automation-scheduler", It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()), Times.Exactly(2));
        Assert.Equal("overnight", fixture.Status.Snapshot.ActiveScheduleName);
    }

    private static SchedulerFixture CreateScheduler(
        ISettingsRepo settingsRepo,
        ISonosConnectorRepo connector,
        ISceneOrchestrationService scenes,
        TimeProvider timeProvider,
        ISettingsSchemaMigrationService? migration = null,
        TimeZoneInfo? timeZone = null,
        PlaybackCommandCoordinator? commands = null)
    {
        var connectorMock = Mock.Get(connector);
        if (!connectorMock.Setups.Any(setup => setup.Expression.Body is MethodCallExpression call
                && call.Method.Name == nameof(ISonosConnectorRepo.GetAllSpeakersInGroup)))
        {
            connectorMock.Setup(repo => repo.GetAllSpeakersInGroup(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string ip, CancellationToken _) => Task.FromResult<IEnumerable<string>>([ip]));
        }

        var databaseOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"canonical-scheduler-{Guid.NewGuid():N}")
            .Options;
        var db = new ApplicationDbContext(databaseOptions);

        var services = new ServiceCollection();
        RepositoryTestServices.Register(services, settingsRepo, connector, Mock.Of<IHolidayRepo>());
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
            timeZone ?? TimeZoneInfo.Utc,
            commands);

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
            Playback = new Scene
            {
                Id = sceneId,
                Name = sceneId,
                IsSyncedPlayback = true,
                Actions = [new SceneAction { SpeakerIp = sceneId == "scene-b" ? "10.0.0.2" : "10.0.0.1", IncludeInPlayback = true }]
            },
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
            Db.Dispose();
        }
    }
}
