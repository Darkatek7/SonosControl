using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Microsoft.Extensions.Time.Testing;
using SonosControl.Web.Services.SonosEvents;
using Xunit;

namespace SonosControl.Tests;

public sealed class PlaybackSnapshotMonitorServiceTests
{
    private sealed class FakeEventHealth : ISonosEventHealth
    {
        public HashSet<string> Healthy { get; } = new(StringComparer.OrdinalIgnoreCase);
        public TimeSpan ReconcileInterval { get; } = TimeSpan.FromSeconds(30);
        public bool IsHealthy(string speakerIp) => Healthy.Contains(speakerIp);
    }

    private sealed class EventedFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public EventedFixture(params SonosSpeaker[] speakers)
        {
            var settingsRepo = new Mock<ISettingsRepo>();
            settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings { Speakers = [.. speakers] });
            foreach (var speaker in speakers)
            {
                var ip = speaker.IpAddress;
                Connector.Setup(repo => repo.GetVolume(ip, It.IsAny<CancellationToken>())).ReturnsAsync(10);
                Connector.Setup(repo => repo.IsPlaying(ip, It.IsAny<CancellationToken>())).ReturnsAsync(true);
                Connector.Setup(repo => repo.GetCurrentStationAsync(ip, It.IsAny<CancellationToken>())).ReturnsAsync("x-rincon-mp3radio://radio.test/live");
                Connector.Setup(repo => repo.GetTrackInfoAsync(ip, It.IsAny<CancellationToken>())).ReturnsAsync(new SonosTrackInfo { Title = "Song" });
            }
            var services = new ServiceCollection();
            services.AddSingleton(settingsRepo.Object);
            services.AddScoped(_ => Connector.Object);
            _provider = services.BuildServiceProvider();
            Monitor = new PlaybackSnapshotMonitorService(_provider.GetRequiredService<IServiceScopeFactory>(), Store,
                NullLogger<PlaybackSnapshotMonitorService>.Instance, Time, Health);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        public FakeEventHealth Health { get; } = new();
        public Mock<ISonosConnectorRepo> Connector { get; } = new();
        public PlaybackSnapshotStore Store { get; } = new();
        public PlaybackSnapshotMonitorService Monitor { get; }

        public int Probes(string ip) => Connector.Invocations.Count(call => call.Method.Name == nameof(ISonosConnectorRepo.GetVolume) && Equals(call.Arguments[0], ip));

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The monitor did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ProbeThatLosesToAConcurrentEventIsConfirmedOnce()
    {
        await using var fixture = new EventedFixture(new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" });
        await fixture.Monitor.PollOnceAsync();
        var raced = 0;
        // A delayed Pause event lands while the probe after a Play command reads PLAYING.
        fixture.Connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (Interlocked.Increment(ref raced) == 1)
                    fixture.Store.TryApplyEvent("10.0.0.1", current => current with { IsPlaying = false }, fixture.Time.GetUtcNow());
            })
            .ReturnsAsync(10);

        await fixture.Monitor.RefreshNowAsync();
        await WaitUntilAsync(() => fixture.Probes("10.0.0.1") == 3 && fixture.Store.Get("10.0.0.1")!.IsPlaying);

        Assert.True(fixture.Store.Get("10.0.0.1")!.IsPlaying);
        await Task.Delay(100);
        Assert.Equal(3, fixture.Probes("10.0.0.1"));
    }

    [Fact]
    public async Task ConfirmationProbesDoNotChainWhenEventsKeepRacing()
    {
        await using var fixture = new EventedFixture(new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" });
        await fixture.Monitor.PollOnceAsync();
        fixture.Connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>()))
            .Callback(() => fixture.Store.TryApplyEvent("10.0.0.1", current => current with { IsPlaying = false }, fixture.Time.GetUtcNow()))
            .ReturnsAsync(10);

        await fixture.Monitor.RefreshNowAsync();
        await WaitUntilAsync(() => fixture.Probes("10.0.0.1") == 3);
        await Task.Delay(200);

        Assert.Equal(3, fixture.Probes("10.0.0.1"));
    }

    [Fact]
    public void IsSupersededByEventOnlyReportsAnEventAsTheLatestWrite()
    {
        var store = new PlaybackSnapshotStore();
        var now = DateTimeOffset.UtcNow;
        store.Set(new PlaybackSnapshot("10.0.0.1", "Office", true, 20, null, null, false, now, now, null));
        var probe = store.BeginProbe("10.0.0.1");
        Assert.False(store.IsSupersededByEvent("10.0.0.1", probe));

        store.TryApplyEvent("10.0.0.1", current => current, now);
        Assert.True(store.IsSupersededByEvent("10.0.0.1", probe));

        store.BeginProbe("10.0.0.1");
        Assert.False(store.IsSupersededByEvent("10.0.0.1", probe));
    }

    [Fact]
    public async Task EventedSpeakersAreOnlyReconciledWhilePolledSpeakersArePolledEveryCycle()
    {
        await using var fixture = new EventedFixture(
            new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" },
            new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.2" });
        fixture.Health.Healthy.Add("10.0.0.1");

        await fixture.Monitor.PollOnceAsync();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(5));
            await fixture.Monitor.PollOnceAsync();
        }
        Assert.Equal(1, fixture.Probes("10.0.0.1"));
        Assert.Equal(6, fixture.Probes("10.0.0.2"));

        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Monitor.PollOnceAsync();
        Assert.Equal(2, fixture.Probes("10.0.0.1"));
    }

    [Fact]
    public async Task LosingEventsReturnsTheSpeakerToRegularPolling()
    {
        await using var fixture = new EventedFixture(new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" });
        fixture.Health.Healthy.Add("10.0.0.1");
        await fixture.Monitor.PollOnceAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Monitor.PollOnceAsync();
        Assert.Equal(1, fixture.Probes("10.0.0.1"));

        fixture.Health.Healthy.Clear();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Monitor.PollOnceAsync();

        Assert.Equal(2, fixture.Probes("10.0.0.1"));
        Assert.Equal(TimeSpan.FromSeconds(15), fixture.Monitor.StaleAfterFor("10.0.0.1"));
    }

    [Fact]
    public async Task RefreshNowProbesEventedSpeakersToo()
    {
        await using var fixture = new EventedFixture(new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" });
        fixture.Health.Healthy.Add("10.0.0.1");
        await fixture.Monitor.PollOnceAsync();

        await fixture.Monitor.RefreshNowAsync();

        Assert.Equal(2, fixture.Probes("10.0.0.1"));
    }

    [Fact]
    public async Task EventedSpeakersGoStaleAfterSeveralMissedReconciliations()
    {
        await using var fixture = new EventedFixture(
            new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" },
            new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.2" });
        fixture.Health.Healthy.Add("10.0.0.1");
        await fixture.Monitor.PollOnceAsync();
        var start = fixture.Time.GetUtcNow();
        Assert.Equal(TimeSpan.FromSeconds(75), fixture.Monitor.StaleAfterFor("10.0.0.1"));
        Assert.Equal(start + TimeSpan.FromSeconds(15), fixture.Store.GetNextStaleDeadline(fixture.Monitor.StaleAfterFor));

        fixture.Store.MarkExpiredStale(start + TimeSpan.FromSeconds(20), fixture.Monitor.StaleAfterFor);
        Assert.False(fixture.Store.Get("10.0.0.1")!.IsStale);
        Assert.True(fixture.Store.Get("10.0.0.2")!.IsStale);

        fixture.Store.MarkExpiredStale(start + TimeSpan.FromSeconds(75), fixture.Monitor.StaleAfterFor);
        Assert.True(fixture.Store.Get("10.0.0.1")!.IsStale);
    }

    [Fact]
    public async Task PolledGroupMemberFollowsItsFreshCoordinator()
    {
        await using var fixture = new EventedFixture(
            new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1", Uuid = "RINCON_C" },
            new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.2", Uuid = "RINCON_M" });
        fixture.Connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "live.aacp", StreamContent = "Band - Song" });
        fixture.Connector.Setup(repo => repo.IsPlaying("10.0.0.2", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        fixture.Connector.Setup(repo => repo.GetVolume("10.0.0.2", It.IsAny<CancellationToken>())).ReturnsAsync(4);
        fixture.Connector.Setup(repo => repo.GetCurrentStationAsync("10.0.0.2", It.IsAny<CancellationToken>())).ReturnsAsync("x-rincon:uuid:RINCON_C");
        fixture.Connector.Setup(repo => repo.GetTrackInfoAsync("10.0.0.2", It.IsAny<CancellationToken>())).ReturnsAsync((SonosTrackInfo?)null);

        await fixture.Monitor.PollOnceAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Monitor.PollOnceAsync();

        var member = fixture.Store.Get("10.0.0.2")!;
        Assert.True(member.IsPlaying);
        Assert.Equal("Band - Song", member.TrackInfo?.GetDisplayString());
        Assert.Equal(4, member.Volume);
        Assert.Equal("x-rincon:uuid:RINCON_C", member.CurrentStationUrl);
    }

    [Fact]
    public async Task PollOnceKeepsTheRadioTitleWhileTheStreamReconnects()
    {
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Speakers = [new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }]
        });
        var connector = new Mock<ISonosConnectorRepo>();
        connector.Setup(repo => repo.GetVolume("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(8);
        connector.Setup(repo => repo.IsPlaying("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        connector.Setup(repo => repo.GetCurrentStationAsync("10.0.0.1", It.IsAny<CancellationToken>())).ReturnsAsync("x-rincon-mp3radio://radio.test/live");
        connector.SetupSequence(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "live.aacp", StreamContent = "Band - Song" })
            .ReturnsAsync(new SonosTrackInfo { Title = "live.aacp" })
            .ReturnsAsync(new SonosTrackInfo { Title = "live.aacp", StreamContent = "Band - Next Song" });
        var services = new ServiceCollection();
        services.AddSingleton(settingsRepo.Object);
        services.AddScoped(_ => connector.Object);
        await using var provider = services.BuildServiceProvider();
        var store = new PlaybackSnapshotStore();
        var monitor = new PlaybackSnapshotMonitorService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<PlaybackSnapshotMonitorService>.Instance, TimeProvider.System);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        Assert.Equal("Band - Song", store.Get("10.0.0.1")?.TrackInfo?.GetDisplayString());

        await monitor.PollOnceAsync();
        Assert.Equal("Band - Next Song", store.Get("10.0.0.1")?.TrackInfo?.GetDisplayString());
    }

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
            // The stale heartbeat sleeps until the next deadline, at most one 5 s poll interval.
            if (dueTime == TimeSpan.FromSeconds(5)) StaleTimerStarted.TrySetResult();
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
