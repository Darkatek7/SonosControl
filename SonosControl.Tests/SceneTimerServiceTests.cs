using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SceneTimerServiceTests
{
    [Fact]
    public async Task SceneFromAnotherScope_OnlyReplacesOverlappingSpeakerTimers()
    {
        var clock = new ManualClock();
        var pausedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = Connector();
        connector.Setup(c => c.PausePlaying("B")).Callback(() => pausedB.TrySetResult()).Returns(Task.CompletedTask);
        var settings = new SonosSettings
        {
            Speakers = [new SonosSpeaker { IpAddress = "A" }, new SonosSpeaker { IpAddress = "B" }],
            Scenes =
            [
                new Scene { Id = "old", SpeakerIps = ["A", "B"], TimerMinutes = 1, IsSyncedPlayback = false },
                new Scene { Id = "new", SpeakerIps = ["A"], IsSyncedPlayback = false }
            ]
        };
        var repository = new Mock<ISettingsRepo>();
        repository.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        var services = Services(connector.Object);
        services.AddSingleton(repository.Object);
        services.AddSingleton<SceneTimerService>(provider => new SceneTimerService(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<SceneTimerService>.Instance));
        services.AddScoped<SceneOrchestrationService>();
        await using var provider = services.BuildServiceProvider();
        var timers = provider.GetRequiredService<SceneTimerService>();
        using (var first = provider.CreateScope())
            Assert.True((await first.ServiceProvider.GetRequiredService<SceneOrchestrationService>().ApplySceneByIdAsync("old")).Success);
        clock.Advance(TimeSpan.FromSeconds(30));
        using (var second = provider.CreateScope())
            Assert.True((await second.ServiceProvider.GetRequiredService<SceneOrchestrationService>().ApplySceneByIdAsync("new")).Success);
        clock.Advance(TimeSpan.FromSeconds(30));
        await pausedB.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await timers.StopAsync(CancellationToken.None);
        connector.Verify(c => c.PausePlaying("A"), Times.Never);
        connector.Verify(c => c.PausePlaying("B"), Times.Once);
    }

    [Fact]
    public async Task CancelledTimerCannotRemoveItsReplacement_AndShutdownStopsPendingTimers()
    {
        var clock = new ManualClock();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = Connector();
        connector.Setup(c => c.PausePlaying("A")).Callback(() => paused.TrySetResult()).Returns(Task.CompletedTask);
        await using var provider = Services(connector.Object).BuildServiceProvider();
        await using var timers = new SceneTimerService(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<SceneTimerService>.Instance);
        await using (await timers.BeginSceneAsync(["A"], CancellationToken.None)) timers.Schedule("old", "Old", ["A"], 1);
        await using (await timers.BeginSceneAsync(["A"], CancellationToken.None)) timers.Schedule("new", "New", ["A"], 2);
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using (await timers.BeginSceneAsync(["A"], CancellationToken.None)) timers.Schedule("pending", "Pending", ["A"], 1);
        await timers.StopAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        connector.Verify(c => c.PausePlaying("A"), Times.Once);
    }

    [Fact]
    public async Task NewSceneWaitsForAnAlreadyRunningPause()
    {
        var clock = new ManualClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = Connector();
        connector.Setup(c => c.PausePlaying("A")).Returns(async () => { started.TrySetResult(); await finish.Task; });
        await using var provider = Services(connector.Object).BuildServiceProvider();
        await using var timers = new SceneTimerService(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<SceneTimerService>.Instance);
        await using (await timers.BeginSceneAsync(["A"], CancellationToken.None)) timers.Schedule("old", "Old", ["A"], 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next = timers.BeginSceneAsync(["A"], CancellationToken.None);
        Assert.False(next.IsCompleted);
        finish.SetResult();
        await using var lease = await next.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ShutdownWaitsForARunningTimerCommand()
    {
        var clock = new ManualClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = Connector();
        connector.Setup(c => c.PausePlaying("A")).Returns(async () => { started.TrySetResult(); await finish.Task; });
        await using var provider = Services(connector.Object).BuildServiceProvider();
        await using var timers = new SceneTimerService(provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<SceneTimerService>.Instance);
        await using (await timers.BeginSceneAsync(["A"], CancellationToken.None)) timers.Schedule("old", "Old", ["A"], 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = timers.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted);
        finish.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static Mock<ISonosConnectorRepo> Connector()
    {
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(c => c.UngroupSpeaker(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(c => c.SetSpeakerVolume(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connector.Setup(c => c.StartPlaying(It.IsAny<string>())).Returns(Task.CompletedTask);
        return connector;
    }

    private static ServiceCollection Services(ISonosConnectorRepo connector)
    {
        var services = new ServiceCollection();
        services.AddSingleton(connector);
        services.AddSingleton(Mock.Of<INotificationService>());
        services.AddSingleton<ILogger<SceneOrchestrationService>>(NullLogger<SceneOrchestrationService>.Instance);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ActionLogger>();
        return services;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ClockTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray()) timer.Fire(_now);
        }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { due = clock._now + dueTime; return !_disposed; }
            public void Fire(DateTimeOffset now) { if (!_disposed && now >= due) { _disposed = true; callback(state); } }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
