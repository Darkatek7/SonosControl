using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class PlaybackSnapshotMonitorServiceTests
{
    [Fact]
    public async Task PollOncePublishesCurrentPlaybackAndSettingsChanges()
    {
        var settings = new SonosSettings
        {
            MaxVolume = 72,
            Speakers = [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1", Uuid = "uuid-1" }],
            Stations = [new TuneInStation { Name = "Night Drive", Url = "https://radio.test/night" }]
        };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(31);
        connector.Setup(repo => repo.IsPlaying("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        connector.Setup(repo => repo.GetCurrentStationAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://radio.test/night");
        connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "Midnight", Artist = "M83" });

        var services = new ServiceCollection();
        services.AddSingleton(settingsRepo.Object);
        services.AddScoped(_ => connector.Object);
        await using var provider = services.BuildServiceProvider();
        var store = new PlaybackSnapshotStore();
        var settingsChanged = 0;
        var changed = 0;
        store.SettingsChanged += () => settingsChanged++;
        store.Changed += () => changed++;
        var monitor = new PlaybackSnapshotMonitorService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<PlaybackSnapshotMonitorService>.Instance, TimeProvider.System);

        await monitor.PollOnceAsync();

        var snapshot = store.Get("10.0.0.1");
        Assert.NotNull(snapshot);
        Assert.Equal("Office", snapshot.SpeakerName);
        Assert.True(snapshot.IsPlaying);
        Assert.Equal(31, snapshot.Volume);
        Assert.Equal("https://radio.test/night", snapshot.CurrentStationUrl);
        Assert.Equal("Midnight", snapshot.TrackInfo?.Title);
        Assert.False(snapshot.IsStale);
        Assert.Equal(1, settingsChanged);
        Assert.True(changed >= 2); // initial configuration plus the first snapshot

        await monitor.PollOnceAsync();
        Assert.Equal(1, settingsChanged);
    }

    [Fact]
    public async Task FailedPollPreservesLastGoodValuesAndMarksSnapshotStale()
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Speakers = [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }]
        });
        var connector = new Mock<ISonosConnectorRepo>();
        connector.SetupSequence(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(25).ThrowsAsync(new IOException());
        connector.Setup(repo => repo.IsPlaying("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        connector.Setup(repo => repo.GetCurrentStationAsync("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync("uri");
        connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "Track" });

        var services = new ServiceCollection();
        services.AddSingleton(settingsRepo.Object);
        services.AddScoped(_ => connector.Object);
        await using var provider = services.BuildServiceProvider();
        var store = new PlaybackSnapshotStore();
        var monitor = new PlaybackSnapshotMonitorService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<PlaybackSnapshotMonitorService>.Instance, TimeProvider.System);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        var snapshot = store.Get("10.0.0.1");
        Assert.True(snapshot?.IsStale);
        Assert.Equal(25, snapshot?.Volume);
        Assert.Equal("Track", snapshot?.TrackInfo?.Title);
        Assert.Equal("Playback status unavailable", snapshot?.Error);
    }

    [Fact]
    public async Task ConcurrentCyclesShareOneProbePerSpeaker()
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Speakers = [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }]
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishVolume = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) => { started.TrySetResult(); return await finishVolume.Task.WaitAsync(token); });
        connector.Setup(repo => repo.IsPlaying("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        connector.Setup(repo => repo.GetCurrentStationAsync("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync("uri");
        connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(new SonosTrackInfo());

        var services = new ServiceCollection();
        services.AddSingleton(settingsRepo.Object);
        services.AddScoped(_ => connector.Object);
        await using var provider = services.BuildServiceProvider();
        var monitor = new PlaybackSnapshotMonitorService(provider.GetRequiredService<IServiceScopeFactory>(), new PlaybackSnapshotStore(),
            NullLogger<PlaybackSnapshotMonitorService>.Instance, TimeProvider.System);
        var firstCycle = monitor.PollOnceAsync();
        await started.Task;
        var secondCycle = monitor.PollOnceAsync();
        connector.Verify(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);

        finishVolume.SetResult(17);
        await Task.WhenAll(firstCycle, secondCycle);
        connector.Verify(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemovedSpeakerQueuedBehindConcurrencyLimitIsNeverPublishedOrProbed()
    {
        var ips = Enumerable.Range(1, 5).Select(index => $"10.0.0.{index}").ToArray();
        var settings = new SonosSettings { Speakers = ips.Select((ip, index) => new SonosSpeaker { IpAddress = ip, Name = $"Room {index}" }).ToList() };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(() => settings);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCalls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string ip, CancellationToken token) =>
            {
                var count = firstCalls.AddOrUpdate(ip, 1, (_, current) => current + 1);
                if (ip != ips[4] && count == 1)
                {
                    if (firstCalls.Where(pair => pair.Key != ips[4] && pair.Value == 1).Count() == 4) started.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return 20;
            });
        connector.Setup(repo => repo.IsPlaying(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        connector.Setup(repo => repo.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        connector.Setup(repo => repo.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((SonosTrackInfo?)null);

        var services = new ServiceCollection();
        services.AddSingleton(settingsRepo.Object);
        services.AddScoped(_ => connector.Object);
        await using var provider = services.BuildServiceProvider();
        var store = new PlaybackSnapshotStore();
        var monitor = new PlaybackSnapshotMonitorService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<PlaybackSnapshotMonitorService>.Instance, TimeProvider.System);
        var oldCycle = monitor.PollOnceAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        settings = new SonosSettings { Speakers = ips.Take(4).Select((ip, index) => new SonosSpeaker { IpAddress = ip, Name = $"Room {index}" }).ToList() };
        var configurationChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += () => { if (store.ConfigurationEpoch > 1) configurationChanged.TrySetResult(); };
        var newCycle = monitor.PollOnceAsync();
        await configurationChanged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        await Task.WhenAll(oldCycle, newCycle);

        connector.Verify(repo => repo.GetVolume(ips[4], It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null(store.Get(ips[4]));
    }

    [Fact]
    public async Task RefreshNowWaitsForOlderCycleThenRunsAFreshProbe()
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Speakers = [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }]
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Mock<ISonosConnectorRepo>();
        var volumeCalls = 0;
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref volumeCalls) == 1)
                {
                    started.TrySetResult();
                    return await releaseOld.Task.WaitAsync(token);
                }
                return 88;
            });
        connector.Setup(repo => repo.IsPlaying("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        connector.Setup(repo => repo.GetCurrentStationAsync("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync((string)null!);
        connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync((SonosTrackInfo?)null);
        var services = new ServiceCollection();
        services.AddSingleton(settingsRepo.Object);
        services.AddScoped(_ => connector.Object);
        await using var provider = services.BuildServiceProvider();
        var store = new PlaybackSnapshotStore();
        var monitor = new PlaybackSnapshotMonitorService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<PlaybackSnapshotMonitorService>.Instance, TimeProvider.System);

        var olderCycle = monitor.PollOnceAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var refresh = monitor.RefreshNowAsync();
        releaseOld.SetResult(12);
        await Task.WhenAll(olderCycle, refresh);

        Assert.Equal(88, store.Get("10.0.0.1")?.Volume);
        connector.Verify(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task HostedStaleHeartbeatFiresAtSnapshotDeadlineDuringBlockedPoll()
    {
        var time = new ManualClock();
        var store = new PlaybackSnapshotStore();
        store.Set(new PlaybackSnapshot("10.0.0.1", "Office", true, 20, null, null, false,
            time.GetUtcNow(), time.GetUtcNow(), null));
        var becameStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += () => { if (store.Get("10.0.0.1")?.IsStale == true) becameStale.TrySetResult(); };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Speakers = [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }]
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>()))
            .Returns(async () => { started.TrySetResult(); return await releaseProbe.Task; });
        connector.Setup(repo => repo.IsPlaying("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        connector.Setup(repo => repo.GetCurrentStationAsync("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync((string)null!);
        connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync((SonosTrackInfo?)null);
        var services = new ServiceCollection();
        services.AddSingleton(settingsRepo.Object);
        services.AddScoped(_ => connector.Object);
        await using var provider = services.BuildServiceProvider();
        var monitor = new PlaybackSnapshotMonitorService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<PlaybackSnapshotMonitorService>.Instance, time);
        using var stopping = new CancellationTokenSource();
        await monitor.StartAsync(stopping.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await time.StaleTimerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(15));
        await becameStale.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(store.Get("10.0.0.1")?.IsStale);

        releaseProbe.SetResult(20);
        await monitor.StopAsync(CancellationToken.None);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];
        public TaskCompletionSource StaleTimerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _now + dueTime);
            _timers.Add(timer);
            if (dueTime == TimeSpan.FromSeconds(15)) StaleTimerStarted.TrySetResult();
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray()) timer.Fire(_now);
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state, DateTimeOffset deadline) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { deadline = owner._now + dueTime; return !_disposed; }
            public void Fire(DateTimeOffset now) { if (!_disposed && now >= deadline) { _disposed = true; callback(state); } }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
