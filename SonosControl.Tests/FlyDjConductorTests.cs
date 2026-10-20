using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

internal sealed record FlyDjSceneCall(
    Scene Scene, string? PerformedBy, PlaybackCommandOrigin? LeaseOrigin, IReadOnlyCollection<string>? Targets,
    bool AllowRecovery, CancellationToken Token);

internal sealed class FlyDjConductorFixture : IAsyncDisposable
{
    public static readonly DateTimeOffset StartTime = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    public const string KitchenIp = "10.0.0.1";
    public const string LivingIp = "10.0.0.2";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"flydj-{Guid.NewGuid():N}");
    private readonly string _databaseName = $"flydj-conductor-{Guid.NewGuid():N}";

    public FlyDjTestClock Time { get; } = new(StartTime);
    public Mock<IFlyDjClient> Client { get; } = new();
    public Mock<IScheduleRoomOwnership> Schedules { get; } = new();
    public Mock<ISceneOrchestrationService> Scenes { get; } = new();
    public Mock<ISonosConnectorRepo> Connector { get; } = new();
    public AutomationSchedulerService Scheduler { get; private set; } = null!;
    public ManualAutoStopService AutoStop { get; private set; } = null!;
    public PlaybackCommandCoordinator Commands { get; }
    public PlaybackSnapshotStore Snapshots { get; } = new();
    public FlyDjStateStore Store { get; }
    public SettingsRepo Repo { get; }
    public ServiceProvider Provider { get; }
    public FlyDjConductorService Conductor { get; private set; } = null!;
    public ConcurrentQueue<FlyDjSceneCall> SceneCalls { get; } = new();

    public Func<FlyDjSceneCall, Task>? OnScene { get; set; }
    public Func<FlyDjAuditionRequest, FlyDjAuditionResponse> OnAudition { get; set; }
    public Func<FlyDjModelInfo> Model { get; set; } = () => ReadyModel();
    public Func<Exception?> StartFailure { get; set; } = () => null;
    public double Engagement { get; set; } = 0.5;

    private FlyDjConductorFixture(bool withScheduler)
    {
        Commands = new PlaybackCommandCoordinator(Time);
        Repo = new SettingsRepo(_directory);
        Store = new FlyDjStateStore(Client.Object, Time, NullLogger<FlyDjStateStore>.Instance);
        OnAudition = DefaultAudition;

        Client.Setup(client => client.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(() => StartFailure() is { } failure ? Task.FromException<FlyDjSimulationState>(failure) : Task.FromResult(State(true)));
        Client.Setup(client => client.StopAsync(It.IsAny<CancellationToken>())).ReturnsAsync(State(false));
        Client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(State(true));
        Client.Setup(client => client.GetModelAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(Model()));
        Client.Setup(client => client.AuditionAsync(It.IsAny<FlyDjAuditionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((FlyDjAuditionRequest request, CancellationToken _) => Task.FromResult(WithSampleMetadata(OnAudition(request))));
        Client.Setup(client => client.ListenAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string? _, CancellationToken _) => Task.FromResult(new FlyDjListenResponse(true, null, Engagement, null, null)));

        Schedules.Setup(owner => owner.GetScheduleOwnedSpeakers(It.IsAny<SonosSettings>(), It.IsAny<DateTimeOffset>())).Returns([]);

        Scenes.Setup(service => service.ApplySceneWithContextAsync(
                It.IsAny<Scene>(), It.IsAny<string?>(), It.IsAny<SceneExecutionContext>(),
                It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .Returns(async (Scene scene, string? by, SceneExecutionContext context, Action<IReadOnlyCollection<string>>? record,
                CancellationToken token, IReadOnlyCollection<string>? targets, bool allowRecovery) =>
            {
                var call = new FlyDjSceneCall(scene, by, context.CurrentLease?.Origin, targets?.ToArray(), allowRecovery, context.CancellationToken);
                SceneCalls.Enqueue(call);
                record?.Invoke(targets ?? []);
                if (OnScene is not null) await OnScene(call);
                return new SceneApplyResult(true, "Applied", scene.Id, (targets ?? []).ToList());
            });

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<ISettingsRepo>(Repo);
        services.AddSingleton(Scenes.Object);
        services.AddSingleton(Connector.Object);
        if (withScheduler) services.AddSingleton<IFlyDjScheduleConductor>(_ => Conductor);
        services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        services.AddScoped<ActionLogger>();
        services.AddScoped<FlyDjCandidateProvider>();
        Provider = services.BuildServiceProvider();
        if (withScheduler)
        {
            Connector.Setup(connector => connector.GetAllSpeakersInGroup(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string ip, CancellationToken _) => Task.FromResult<IEnumerable<string>>([ip]));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Automation:TimeZone"] = "UTC" }).Build();
            var scopes = Provider.GetRequiredService<IServiceScopeFactory>();
            AutoStop = new ManualAutoStopService(scopes, Commands, Time, configuration, NullLogger<ManualAutoStopService>.Instance);
            var migration = new Mock<ISettingsSchemaMigrationService>();
            migration.Setup(service => service.MigrateIfRequiredAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SettingsMigrationResult(true, false, SonosSettings.CurrentSettingsSchemaVersion, []));
            Scheduler = new AutomationSchedulerService(scopes, migration.Object, new AutomationRuntimeStatus(),
                NullLogger<AutomationSchedulerService>.Instance, configuration, Time, TimeZoneInfo.Utc, Commands, autoStop: AutoStop);
            Schedules.Setup(owner => owner.GetScheduleOwnedSpeakers(It.IsAny<SonosSettings>(), It.IsAny<DateTimeOffset>()))
                .Returns((SonosSettings settings, DateTimeOffset now) => Scheduler.GetScheduleOwnedSpeakers(settings, now));
            Schedules.Setup(owner => owner.GetFlyDjSchedules(It.IsAny<SonosSettings>(), It.IsAny<DateTimeOffset>()))
                .Returns((SonosSettings settings, DateTimeOffset now) => Scheduler.GetFlyDjSchedules(settings, now));
        }
    }

    public static async Task<FlyDjConductorFixture> CreateAsync(Action<SonosSettings>? configure = null, bool startHost = true, bool withScheduler = false)
    {
        var fixture = new FlyDjConductorFixture(withScheduler);
        await fixture.Repo.UpdateSettingsAsync(settings =>
        {
            settings.MaxVolume = 30;
            settings.Speakers =
            [
                new SonosSpeaker { Name = "Kitchen", IpAddress = KitchenIp },
                new SonosSpeaker { Name = "Living", IpAddress = LivingIp }
            ];
            settings.Stations = Enumerable.Range(1, 3)
                .Select(i => new TuneInStation { Name = $"Station {i}", Url = $"radio{i}.example.com/live" }).ToList();
            settings.FlyDj.TargetSpeakerIps = [KitchenIp, LivingIp];
            settings.FlyDj.Volume = 25;
            configure?.Invoke(settings);
        });
        fixture.Conductor = new FlyDjConductorService(
            fixture.Time, fixture.Provider.GetRequiredService<IServiceScopeFactory>(), fixture.Commands, fixture.Snapshots,
            fixture.Store, fixture.Client.Object, NullLogger<FlyDjConductorService>.Instance, fixture.Schedules.Object, withScheduler ? fixture.AutoStop : null);
        if (startHost) await ((IHostedService)fixture.Conductor).StartAsync(CancellationToken.None);
        return fixture;
    }

    private FlyDjAuditionResponse WithSampleMetadata(FlyDjAuditionResponse response) => response with
    {
        Results = response.Results.Select(result => result.SampleId is not null ? result : result with
        { SampleId = Guid.NewGuid().ToString("N"), SampledAtUtc = Time.GetUtcNow() }).ToArray()
    };

    public bool AutoAdvanceObservation { get; set; } = true;

    public static FlyDjAuditionResponse DefaultAudition(FlyDjAuditionRequest request)
        => new(request.Candidates.Select((candidate, index) => new FlyDjAuditionResult(
                candidate.Id, true, null, int.Parse(candidate.Id[3..]) - 1, new FlyDjAudioFeatures(0.3, 0.3, 0.3, 0.5, 2, 1500), null)).ToList(),
            request.BioMs, new Dictionary<string, double>());

    public static FlyDjSimulationState State(bool running) => new(
        running ? "bursting" : "stopped", running, 0, 0, 1, 0, 0, null, new Dictionary<string, double>(), null, null, "ready");

    public static FlyDjModelInfo ReadyModel(string status = "ready", double progress = 1.0, string? message = null) => new(
        "flywire-783", "auditory", status, progress, message, 100, 1000, 10000, [], [], null);

    public int Calls(string method) => Client.Invocations.Count(invocation => invocation.Method.Name == method);

    public async Task<SonosSettings> SettingsAsync() => (await Repo.GetSettings())!;

    public async Task Settle(int milliseconds = 120) => await Task.Delay(milliseconds);

    /// <summary>
    /// Waits (in real time) for a condition. With <paramref name="advance"/> the fake clock is nudged each round,
    /// but only while the conductor is parked on its timer, so a nudge is never lost. With <paramref name="parked"/>
    /// the condition only counts once the conductor has also finished its step and is waiting again.
    /// </summary>
    public async Task UntilAsync(Func<bool> condition, TimeSpan? advance = null, int timeoutMs = 10000, string? what = null, bool parked = false)
    {
        var watch = Stopwatch.StartNew();
        while (!(condition() && (!parked || Time.PendingTimers > 0)))
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Condition not met: {what ?? "?"}. Status={Store.Current.Status} {Store.Current.StatusMessage}");
            if (!condition() && Time.PendingTimers > 0)
            {
                if (AutoAdvanceObservation && Store.Current.ObservationEndsAtUtc is not null && Store.Current.ActiveSpeakerIps.Count == 0)
                    Time.Advance(FlyDjObservationPlan.FromMinutes((await SettingsAsync()).FlyDj.MinDwellMinutes).SampleInterval);
                else if (advance is { } step) Time.Advance(step);
            }
            await Task.Delay(2);
        }
    }

    /// <summary>Waits until the conductor has finished its step and sleeps on a timer, optionally a newer one than <paramref name="after"/>.</summary>
    public Task ParkedAsync(long? after = null, int timeoutMs = 10000, string? what = null)
        => UntilAsync(() => after is null || Time.TimersCreated > after, timeoutMs: timeoutMs, what: what ?? "conductor parked", parked: true);

    /// <summary>Moves the clock once the conductor is waiting, so the advance cannot land before its timer exists.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        await ParkedAsync(what: $"conductor parked before advancing {by}");
        Time.Advance(by);
    }

    /// <summary>Advances a whole timer interval and waits for the step it triggers to finish. Only for advances that reach the conductor's timer.</summary>
    public async Task StepAsync(TimeSpan by)
    {
        await ParkedAsync(what: $"conductor parked before stepping {by}");
        var mark = Time.TimersCreated;
        Time.Advance(by);
        await ParkedAsync(mark, what: $"conductor stepped after {by}");
    }

    public async Task AdvanceSamplingAsync(TimeSpan duration)
    {
        var interval = FlyDjObservationPlan.FromMinutes((await SettingsAsync()).FlyDj.MinDwellMinutes).SampleInterval;
        while (duration >= interval) { await StepAsync(interval); duration -= interval; }
        if (duration > TimeSpan.Zero) { await AdvanceAsync(duration); await ParkedAsync(); }
    }

    public async Task StartDjAsync() => await Conductor.StartAsync("tester", CancellationToken.None);

    public async Task StartAndWaitForFirstPlayAsync()
    {
        await StartDjAsync();
        await UntilAsync(() => Store.Current.Status == FlyDjConductorStatus.Playing && !SceneCalls.IsEmpty, what: "first station playing", parked: true);
    }

    public async Task TakeManualControlAsync(params string[] speakers)
    {
        var lease = await Commands.EnterManualAsync(speakers);
        await lease.DisposeAsync();
    }

    public async Task<List<SonosControl.Web.Models.LogEntry>> LogsAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Logs.AsNoTracking().ToListAsync();
    }

    public async Task<List<SonosControl.Web.Models.FlyDjDecision>> DecisionsAsync()
    {
        await using var scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().FlyDjDecisions.AsNoTracking()
            .OrderBy(decision => decision.Id).ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { await ((IHostedService)Conductor).StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
        catch (Exception) { }
        Store.Dispose();
        await Provider.DisposeAsync();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch (IOException) { }
    }
}

public class FlyDjConductorTests
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task StoppedConductorNeverTouchesTheSidecarNoMatterHowMuchTimePasses()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();

        for (var i = 0; i < 36; i++) fixture.Time.Advance(TimeSpan.FromMinutes(10));
        await fixture.Settle();

        Assert.Empty(fixture.Client.Invocations);
        Assert.Empty(fixture.SceneCalls);
        Assert.False(fixture.Conductor.IsRunning);
        Assert.Equal(FlyDjConductorStatus.Stopped, fixture.Store.Current.Status);
        Assert.Null(fixture.Store.Current.NextDecisionAtUtc);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task ResumesAfterStartupOnlyWhenAutoResumeAndWasRunning(bool autoResume, bool wasRunning, bool expectRunning)
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
        {
            settings.FlyDj.AutoResume = autoResume;
            settings.FlyDj.WasRunning = wasRunning;
        });

        if (expectRunning)
        {
            await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, what: "auto-resumed playing");
            Assert.True(fixture.Conductor.IsRunning);
            Assert.Equal(1, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
        }
        else
        {
            await fixture.Settle();
            Assert.False(fixture.Conductor.IsRunning);
            Assert.Empty(fixture.Client.Invocations);
        }
    }

    [Fact]
    public async Task StartAuditionsLibraryStationsAndPlaysTheWinnerOnTheAutomationLaneWithClampedVolume()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => settings.FlyDj.Volume = 80);

        await fixture.StartAndWaitForFirstPlayAsync();

        var call = Assert.Single(fixture.SceneCalls);
        Assert.Equal("flydj", call.PerformedBy);
        Assert.Equal(PlaybackCommandOrigin.Automation, call.LeaseOrigin);
        Assert.False(call.AllowRecovery);
        Assert.Equal(SceneSourceType.Station, call.Scene.SourceType);
        Assert.Equal(SceneSourceSelectionMode.Specific, call.Scene.SourceSelectionMode);
        Assert.True(call.Scene.Enabled);
        Assert.True(call.Scene.IsSyncedPlayback);
        Assert.Equal([FlyDjConductorFixture.KitchenIp, FlyDjConductorFixture.LivingIp], call.Scene.SpeakerIps.Order().ToArray());
        Assert.Equal(call.Scene.SpeakerIps.Order(), call.Targets!.Order());
        Assert.All(call.Scene.Actions, action => Assert.Equal(30, action.Volume));   // MaxVolume is 30
        Assert.Matches(@"^radio[1-3]\.example\.com/live$", call.Scene.SourceUrl);

        var state = fixture.Store.Current;
        Assert.Equal(call.Scene.SourceUrl, state.CurrentStationUrl);
        Assert.Equal(2, state.ActiveSpeakerIps.Count);
        Assert.NotNull(state.LastDecision);
        Assert.Equal(3, state.LastDecision!.Candidates.Count);
        Assert.False(string.IsNullOrWhiteSpace(state.LastDecision.Reason));
        Assert.NotNull(state.NextDecisionAtUtc);
        Assert.Equal(1, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
        Assert.Equal(22, fixture.Calls(nameof(IFlyDjClient.AuditionAsync)));
        fixture.Client.Verify(client => client.ListenAsync(call.Scene.SourceUrl, It.IsAny<CancellationToken>()), Times.Once);

        var decision = Assert.Single(await fixture.DecisionsAsync());
        Assert.Equal(call.Scene.SourceUrl, decision.StationUrl);
        Assert.Equal(0.7, decision.BrainWeight);
        Assert.Equal(0.3, decision.PriorWeight);
        Assert.Null(decision.EndedAtUtc);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], JsonSerializer.Deserialize<string[]>(decision.SpeakerIpsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Order());
        Assert.True((await fixture.SettingsAsync()).FlyDj.WasRunning);
    }

    [Fact]
    public async Task OnlyConfiguredRoomsAreTargeted()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
            settings.FlyDj.TargetSpeakerIps = [FlyDjConductorFixture.LivingIp, "10.9.9.9"]);

        await fixture.StartAndWaitForFirstPlayAsync();

        var call = Assert.Single(fixture.SceneCalls);
        Assert.Equal([FlyDjConductorFixture.LivingIp], call.Targets);
        Assert.False(call.Scene.IsSyncedPlayback);
    }

    [Fact]
    public async Task ActionLogRecordsStartChoiceYieldAndStop()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);
        await fixture.AdvanceAsync(CheckInterval);
        await fixture.UntilAsync(() => fixture.Store.Current.YieldedSpeakers.Count == 1, what: "kitchen yielded", parked: true);

        await fixture.Conductor.StopAsync("alice", CancellationToken.None);

        var logs = await fixture.LogsAsync();
        Assert.Contains(logs, log => log.Action == "FlyDjStarted" && log.PerformedBy == "tester");
        Assert.Contains(logs, log => log.Action == "FlyDjStationChosen" && log.PerformedBy == "flydj");
        Assert.Contains(logs, log => log.Action == "FlyDjYielded" && log.Details!.Contains("Kitchen"));
        Assert.Contains(logs, log => log.Action == "FlyDjStopped" && log.PerformedBy == "alice");
    }

    [Fact]
    public async Task ManualPlaybackInADjRoomYieldsThatRoomAndFlyDjNeverCommandsItAgain()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();

        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);
        await fixture.AdvanceAsync(CheckInterval);
        await fixture.UntilAsync(() => fixture.Store.Current.YieldedSpeakers.Count == 1, what: "kitchen yielded", parked: true);

        var state = fixture.Store.Current;
        var yielded = Assert.Single(state.YieldedSpeakers);
        Assert.Equal(FlyDjConductorFixture.KitchenIp, yielded.SpeakerIp);
        Assert.Equal("Kitchen", yielded.SpeakerName);
        Assert.Equal(fixture.Time.GetUtcNow().AddMinutes(60), yielded.UntilUtc);
        Assert.Equal([FlyDjConductorFixture.LivingIp], state.ActiveSpeakerIps);
        Assert.Equal(FlyDjConductorStatus.Playing, state.Status);
        Assert.True((await fixture.DecisionsAsync()).Single().OverriddenByUser);

        // Over the next 50 minutes FlyDJ may re-decide, but only ever for the living room.
        for (var minutes = 0; minutes < 50; minutes++) await fixture.StepAsync(TimeSpan.FromMinutes(1));
        Assert.All(fixture.SceneCalls.Skip(1), call => Assert.DoesNotContain(FlyDjConductorFixture.KitchenIp, call.Targets!));
    }

    [Fact]
    public async Task ManualPreemptionWhileFlyDjIsStartingPlaybackYieldsTheRoomAndKeepsTheOtherRoom()
    {
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.OnScene = async call =>
        {
            if (fixture.SceneCalls.Count != 1) return;
            applying.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Token);   // the lease cancels this when a person takes over
        };

        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => applying.Task.IsCompleted, what: "playback lease entered");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var manual = await fixture.Commands.EnterManualAsync([FlyDjConductorFixture.KitchenIp]).WaitAsync(TimeSpan.FromSeconds(5));
        await manual.DisposeAsync();

        await fixture.UntilAsync(() => fixture.SceneCalls.Count >= 2, advance: TimeSpan.FromSeconds(1), what: "re-plan without kitchen");
        var replanned = fixture.SceneCalls.Skip(1).First();
        Assert.Equal([FlyDjConductorFixture.LivingIp], replanned.Targets);
        Assert.Equal(FlyDjConductorFixture.KitchenIp, Assert.Single(fixture.Store.Current.YieldedSpeakers).SpeakerIp);
    }

    [Fact]
    public async Task SleepTimerStopDuringStartYieldsEveryRoomItTouches()
    {
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.OnScene = async call =>
        {
            applying.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
        };

        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => applying.Task.IsCompleted, what: "playback lease entered");
        var stop = await fixture.Commands.EnterMandatoryStopAsync([FlyDjConductorFixture.KitchenIp, FlyDjConductorFixture.LivingIp])
            .WaitAsync(TimeSpan.FromSeconds(5));
        await stop.DisposeAsync();

        await fixture.UntilAsync(() => fixture.Store.Current.YieldedSpeakers.Count == 2, what: "both yielded after sleep timer");
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Yielded, advance: TimeSpan.FromSeconds(1), what: "resting");
    }

    [Fact]
    public async Task ExternalPlaybackChangeObservedAfterTheGraceWindowYieldsTheRoom()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();
        var url = fixture.SceneCalls.Single().Scene.SourceUrl!;

        // Inside the 20 s grace period a mismatch is expected (the speaker is still switching).
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(5));
        PublishSnapshot(fixture, FlyDjConductorFixture.KitchenIp, "Kitchen", true, "x-rincon-mp3radio://other.example.com/x");
        await fixture.Settle();
        Assert.Empty(fixture.Store.Current.YieldedSpeakers);

        // A matching stream, a group follower pointer and stale data never count as an override.
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(20));
        PublishSnapshot(fixture, FlyDjConductorFixture.KitchenIp, "Kitchen", true, "x-rincon-mp3radio://" + url);
        PublishSnapshot(fixture, FlyDjConductorFixture.LivingIp, "Living", true, "x-rincon:RINCON_0000000000001400");
        await fixture.Settle();
        Assert.Empty(fixture.Store.Current.YieldedSpeakers);
        fixture.Snapshots.Set(new PlaybackSnapshot(FlyDjConductorFixture.KitchenIp, "Kitchen", false, 10, null, null, true,
            fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), "stale"));
        await fixture.Settle();
        Assert.Empty(fixture.Store.Current.YieldedSpeakers);

        // Someone cast something else to the kitchen.
        PublishSnapshot(fixture, FlyDjConductorFixture.KitchenIp, "Kitchen", true, "x-sonos-spotify:spotify%3atrack%3a1");
        await fixture.UntilAsync(() => fixture.Store.Current.YieldedSpeakers.Count == 1, what: "kitchen yielded after external change", parked: true);

        Assert.Equal(FlyDjConductorFixture.KitchenIp, fixture.Store.Current.YieldedSpeakers.Single().SpeakerIp);
        Assert.Equal([FlyDjConductorFixture.LivingIp], fixture.Store.Current.ActiveSpeakerIps);
    }

    [Fact]
    public async Task ExternalPauseYieldsTheRoom()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();

        await fixture.AdvanceAsync(TimeSpan.FromSeconds(21));
        PublishSnapshot(fixture, FlyDjConductorFixture.LivingIp, "Living", false, "x-rincon-mp3radio://" + fixture.SceneCalls.Single().Scene.SourceUrl);
        await fixture.UntilAsync(() => fixture.Store.Current.YieldedSpeakers.Count == 1, what: "living yielded after pause", parked: true);

        Assert.Equal(FlyDjConductorFixture.LivingIp, fixture.Store.Current.YieldedSpeakers.Single().SpeakerIp);
        Assert.Contains("stopped", fixture.Store.Current.YieldedSpeakers.Single().Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenEveryRoomIsYieldedTheSidecarIsParkedAndResumesAfterTheCooldown()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();

        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp, FlyDjConductorFixture.LivingIp);
        await fixture.AdvanceAsync(CheckInterval);
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Yielded && fixture.Store.Current.YieldedSpeakers.Count == 2,
            what: "all rooms resting", parked: true);

        Assert.Equal(1, fixture.Calls(nameof(IFlyDjClient.StopAsync)));
        Assert.Equal(2, fixture.Store.Current.YieldedSpeakers.Count);
        Assert.Null(fixture.Store.Current.CurrentStationUrl);
        Assert.NotNull(fixture.Store.Current.NextDecisionAtUtc);
        Assert.Equal(fixture.Store.Current.YieldedSpeakers.Min(item => item.UntilUtc), fixture.Store.Current.NextDecisionAtUtc);

        // Resting costs nothing: no simulation calls while the cooldown runs.
        var callsBefore = fixture.Client.Invocations.Count;
        for (var minutes = 0; minutes < 55; minutes++) await fixture.AdvanceAsync(TimeSpan.FromMinutes(1));
        await fixture.ParkedAsync();
        Assert.Equal(callsBefore, fixture.Client.Invocations.Count);
        Assert.Equal(FlyDjConductorStatus.Yielded, fixture.Store.Current.Status);

        // Cooldown over (nobody is playing): the sidecar starts again and FlyDJ takes the rooms back.
        await fixture.UntilAsync(() => fixture.SceneCalls.Count >= 2, advance: TimeSpan.FromMinutes(1), what: "resumed after cooldown");
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, what: "playing again", parked: true);
        Assert.Equal(2, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
        Assert.Equal(2, fixture.SceneCalls.Last().Targets!.Count);
        Assert.Empty(fixture.Store.Current.YieldedSpeakers);
    }

    [Fact]
    public async Task RoomStillPlayingTheUsersMusicStaysYieldedAfterTheCooldown()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
            settings.FlyDj.TargetSpeakerIps = [FlyDjConductorFixture.KitchenIp]);
        await fixture.StartAndWaitForFirstPlayAsync();
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);
        await fixture.AdvanceAsync(CheckInterval);
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Yielded, what: "resting", parked: true);
        var startsBefore = fixture.Calls(nameof(IFlyDjClient.StartAsync));

        // The person's own music is still playing when the cooldown ends: FlyDJ must not take the room back.
        PublishSnapshot(fixture, FlyDjConductorFixture.KitchenIp, "Kitchen", true, "x-sonos-spotify:spotify%3atrack%3a1");
        var firstRest = Assert.Single(fixture.Store.Current.YieldedSpeakers);
        await fixture.AdvanceAsync(TimeSpan.FromMinutes(61));
        await fixture.UntilAsync(() => fixture.Store.Current.YieldedSpeakers.SingleOrDefault()?.UntilUtc > firstRest.UntilUtc,
            what: "cooldown restarted", parked: true);

        var rest = Assert.Single(fixture.Store.Current.YieldedSpeakers);
        Assert.Equal(fixture.Time.GetUtcNow().AddMinutes(60), rest.UntilUtc);
        Assert.Single(fixture.SceneCalls);
        Assert.Equal(startsBefore, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
        Assert.Equal(FlyDjConductorStatus.Yielded, fixture.Store.Current.Status);

        // Once the room is quiet, the next cooldown ends with FlyDJ taking it back.
        PublishSnapshot(fixture, FlyDjConductorFixture.KitchenIp, "Kitchen", false, "x-sonos-spotify:spotify%3atrack%3a1");
        await fixture.AdvanceAsync(TimeSpan.FromMinutes(61));
        await fixture.UntilAsync(() => fixture.SceneCalls.Count == 2, advance: TimeSpan.FromMinutes(1), what: "resumed once the room is quiet");
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, what: "playing again", parked: true);
        Assert.Empty(fixture.Store.Current.YieldedSpeakers);
        Assert.Equal([FlyDjConductorFixture.KitchenIp], fixture.SceneCalls.Last().Targets);
    }

    [Fact]
    public async Task ManualPlaybackDuringTheCooldownRestartsTheRest()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings =>
            settings.FlyDj.TargetSpeakerIps = [FlyDjConductorFixture.KitchenIp]);
        await fixture.StartAndWaitForFirstPlayAsync();
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);
        await fixture.AdvanceAsync(CheckInterval);
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Yielded, what: "resting", parked: true);

        await fixture.AdvanceAsync(TimeSpan.FromMinutes(30));
        await fixture.TakeManualControlAsync(FlyDjConductorFixture.KitchenIp);   // someone is still playing
        await fixture.AdvanceAsync(TimeSpan.FromMinutes(35));                    // original cooldown has ended
        await fixture.UntilAsync(() => fixture.Store.Current.YieldedSpeakers.SingleOrDefault()?.UntilUtc > fixture.Time.GetUtcNow(),
            what: "rest restarted", parked: true);

        Assert.Single(fixture.SceneCalls);
        Assert.Equal(FlyDjConductorStatus.Yielded, fixture.Store.Current.Status);
        var rest = Assert.Single(fixture.Store.Current.YieldedSpeakers);
        Assert.True(rest.UntilUtc > fixture.Time.GetUtcNow());
    }

    [Fact]
    public async Task RoomsOwnedByAnActiveScheduleAreSkippedAndJoinOnceTheScheduleEnds()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.Schedules.Setup(owner => owner.GetScheduleOwnedSpeakers(It.IsAny<SonosSettings>(), It.IsAny<DateTimeOffset>()))
            .Returns([FlyDjConductorFixture.KitchenIp]);

        await fixture.StartAndWaitForFirstPlayAsync();
        Assert.Equal([FlyDjConductorFixture.LivingIp], fixture.SceneCalls.Single().Targets);
        Assert.Empty(fixture.Store.Current.YieldedSpeakers);

        fixture.Schedules.Setup(owner => owner.GetScheduleOwnedSpeakers(It.IsAny<SonosSettings>(), It.IsAny<DateTimeOffset>())).Returns([]);
        await fixture.UntilAsync(() => fixture.Store.Current.ActiveSpeakerIps.Count == 2, advance: TimeSpan.FromSeconds(5), what: "kitchen joins", parked: true);

        Assert.True(fixture.Calls(nameof(IFlyDjClient.AuditionAsync)) >= 23);
    }

    [Fact]
    public async Task ScheduleTakingOverAnActiveRoomReleasesItWithoutACooldown()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();
        fixture.Schedules.Setup(owner => owner.GetScheduleOwnedSpeakers(It.IsAny<SonosSettings>(), It.IsAny<DateTimeOffset>()))
            .Returns([FlyDjConductorFixture.LivingIp]);

        await fixture.UntilAsync(() => fixture.Store.Current.ActiveSpeakerIps.Count == 1, advance: TimeSpan.FromSeconds(5), what: "living released", parked: true);

        Assert.Equal([FlyDjConductorFixture.KitchenIp], fixture.Store.Current.ActiveSpeakerIps);
        Assert.Empty(fixture.Store.Current.YieldedSpeakers);
    }

    [Fact]
    public async Task EveryRoomOwnedBySchedulesParksFlyDjWithoutLoad()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.Schedules.Setup(owner => owner.GetScheduleOwnedSpeakers(It.IsAny<SonosSettings>(), It.IsAny<DateTimeOffset>()))
            .Returns([FlyDjConductorFixture.KitchenIp, FlyDjConductorFixture.LivingIp]);

        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Yielded, what: "yielded to schedules", parked: true);

        Assert.Empty(fixture.SceneCalls);
        Assert.Equal(0, fixture.Calls(nameof(IFlyDjClient.AuditionAsync)));
        Assert.Equal(0, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
    }

    [Fact]
    public async Task UnreachableSidecarShowsAnErrorAndRetriesWithBackoffWithoutCrashing()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        var failing = true;
        fixture.StartFailure = () => failing ? new FlyDjUnavailableException("The FlyDJ service is unreachable: refused") : null;

        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Error, what: "error status", parked: true);

        Assert.Contains("unreachable", fixture.Store.Current.StatusMessage);
        Assert.False(fixture.Store.Current.SidecarReachable);
        Assert.True(fixture.Conductor.IsRunning);
        Assert.Equal(1, fixture.Calls(nameof(IFlyDjClient.StartAsync)));

        await fixture.AdvanceAsync(TimeSpan.FromSeconds(14));
        await fixture.Settle();
        Assert.Equal(1, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.UntilAsync(() => fixture.Calls(nameof(IFlyDjClient.StartAsync)) == 2, what: "second attempt after 15 s", parked: true);

        await fixture.AdvanceAsync(TimeSpan.FromSeconds(29));
        await fixture.Settle();
        Assert.Equal(2, fixture.Calls(nameof(IFlyDjClient.StartAsync)));   // backoff doubled to 30 s
        await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
        await fixture.UntilAsync(() => fixture.Calls(nameof(IFlyDjClient.StartAsync)) == 3, what: "third attempt after 30 s", parked: true);

        failing = false;
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, advance: TimeSpan.FromSeconds(15), what: "recovered");
        Assert.True(fixture.Store.Current.SidecarReachable);
        Assert.False(fixture.Conductor.ExecuteTask!.IsFaulted);
    }

    [Fact]
    public async Task BackoffDoublesFromFifteenSecondsAndIsCappedAtFiveMinutes()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.StartFailure = () => new FlyDjUnavailableException("down");

        await fixture.StartDjAsync();
        var attempts = 1;
        foreach (var seconds in new[] { 15, 30, 60, 120, 240, 300, 300 })
        {
            var expected = attempts;
            await fixture.UntilAsync(() => fixture.Calls(nameof(IFlyDjClient.StartAsync)) == expected, what: $"attempt {expected}", parked: true);
            await fixture.AdvanceAsync(TimeSpan.FromSeconds(seconds - 1));
            await fixture.Settle(40);
            Assert.Equal(expected, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
            await fixture.AdvanceAsync(TimeSpan.FromSeconds(1));
            attempts++;
            await fixture.UntilAsync(() => fixture.Calls(nameof(IFlyDjClient.StartAsync)) == attempts, what: $"attempt {attempts} after {seconds} s", parked: true);
        }
    }

    [Fact]
    public async Task ModelStillBuildingShowsStartingAndSkipsAuditions()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        var ready = false;
        fixture.Model = () => ready ? FlyDjConductorFixture.ReadyModel() : FlyDjConductorFixture.ReadyModel("building", 0.4);

        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Starting && fixture.Store.Current.Model is not null,
            what: "building", parked: true);

        Assert.Contains("building", fixture.Store.Current.StatusMessage);
        Assert.Contains("40%", fixture.Store.Current.StatusMessage);
        Assert.Equal(0, fixture.Calls(nameof(IFlyDjClient.AuditionAsync)));

        ready = true;
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, advance: TimeSpan.FromSeconds(10), what: "ready and playing", parked: true);
        Assert.Equal(22, fixture.Calls(nameof(IFlyDjClient.AuditionAsync)));
    }

    [Fact]
    public async Task ModelFailureIsReportedAsAnError()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.Model = () => FlyDjConductorFixture.ReadyModel("error", 0, "Connectome data missing.");

        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Error, what: "model error", parked: true);

        Assert.Equal("Connectome data missing.", fixture.Store.Current.StatusMessage);
    }

    [Fact]
    public async Task SidecarThatLosesItsRunningStateIsRestartedTransparently()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        var failuresLeft = 1;
        fixture.OnAudition = request => failuresLeft-- > 0
            ? throw new FlyDjNotRunningException()
            : FlyDjConductorFixture.DefaultAudition(request);

        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Playing, advance: TimeSpan.FromSeconds(1), what: "playing after restart", parked: true);

        Assert.Equal(2, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
        Assert.True(fixture.Calls(nameof(IFlyDjClient.AuditionAsync)) >= 23);
    }

    [Fact]
    public async Task StopStopsTheSidecarClearsTheFlagAndSilencesAllPolling()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();

        await fixture.Conductor.StopAsync("alice", CancellationToken.None);

        Assert.False(fixture.Conductor.IsRunning);
        Assert.Equal(FlyDjConductorStatus.Stopped, fixture.Store.Current.Status);
        Assert.Null(fixture.Store.Current.CurrentStationUrl);
        Assert.Empty(fixture.Store.Current.ActiveSpeakerIps);
        Assert.Equal(1, fixture.Calls(nameof(IFlyDjClient.StopAsync)));
        Assert.False((await fixture.SettingsAsync()).FlyDj.WasRunning);
        var decision = Assert.Single(await fixture.DecisionsAsync());
        Assert.NotNull(decision.EndedAtUtc);
        Assert.False(decision.OverriddenByUser);

        var calls = fixture.Client.Invocations.Count;
        for (var i = 0; i < 24; i++) fixture.Time.Advance(TimeSpan.FromMinutes(30));
        await fixture.Settle();
        Assert.Equal(calls, fixture.Client.Invocations.Count);
    }

    [Fact]
    public async Task StoppingWhileStoppedIsHarmlessAndStartingAgainWorks()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();

        await fixture.Conductor.StopAsync("alice", CancellationToken.None);
        Assert.Empty(fixture.Client.Invocations);

        await fixture.StartAndWaitForFirstPlayAsync();
        await fixture.Conductor.StopAsync(null, CancellationToken.None);
        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.SceneCalls.Count == 2, advance: TimeSpan.FromSeconds(1), what: "second run plays", parked: true);
        Assert.True(fixture.Conductor.IsRunning);
    }

    [Fact]
    public async Task StartIsIdempotentWhileRunning()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();

        await fixture.StartDjAsync();
        await fixture.Settle();

        Assert.Single(fixture.SceneCalls);
        Assert.Equal(1, fixture.Calls(nameof(IFlyDjClient.StartAsync)));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(5, 6)]
    public async Task StationIsKeptUntilMaxDwellThenActuallyChanges(int minimum, int maximum)
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => { settings.FlyDj.MinDwellMinutes = minimum; settings.FlyDj.MaxDwellMinutes = maximum; });
        await fixture.StartAndWaitForFirstPlayAsync();
        var first = fixture.Store.Current.CurrentStationUrl;
        // Equal brain scores and priors remove the early-switch advantage.
        fixture.OnAudition = request => FlyDjConductorFixture.DefaultAudition(request) with
        { Results = request.Candidates.Select(candidate => new FlyDjAuditionResult(candidate.Id, true, null, 0, null, null)).ToArray() };
        await fixture.AdvanceSamplingAsync(TimeSpan.FromMinutes(maximum).Subtract(TimeSpan.FromSeconds(6)));
        Assert.Single(fixture.SceneCalls);
        await fixture.AdvanceSamplingAsync(TimeSpan.FromSeconds(9));
        Assert.Equal(2, fixture.SceneCalls.Count);
        Assert.NotEqual(first, fixture.Store.Current.CurrentStationUrl);
    }

    [Fact]
    public async Task SuperiorAlternativeNeedsTwoFreshComparisonsAfterMinimumDwell()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => { settings.FlyDj.MinDwellMinutes = 1; settings.FlyDj.MaxDwellMinutes = 3; });
        await fixture.StartAndWaitForFirstPlayAsync();
        var first = fixture.Store.Current.CurrentStationUrl;
        fixture.OnAudition = request => FlyDjConductorFixture.DefaultAudition(request) with
        { Results = request.Candidates.Select(candidate => new FlyDjAuditionResult(candidate.Id, true, null, candidate.Url == first ? -10 : 10, null, null)).ToArray() };
        await fixture.AdvanceSamplingAsync(TimeSpan.FromSeconds(57));
        Assert.Single(fixture.SceneCalls);
        await fixture.StepAsync(TimeSpan.FromSeconds(3));
        Assert.Single(fixture.SceneCalls); // first comparison only nominates a challenger
        await fixture.StepAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.SceneCalls.Count);
        Assert.NotEqual(first, fixture.Store.Current.CurrentStationUrl);
    }

    [Fact]
    public async Task CachedLiveEngagementDoesNotCauseStationChanges()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(settings => { settings.FlyDj.MinDwellMinutes = 1; settings.FlyDj.MaxDwellMinutes = 3; });
        fixture.OnAudition = request => FlyDjConductorFixture.DefaultAudition(request) with
        { Results = request.Candidates.Select(candidate => new FlyDjAuditionResult(candidate.Id, true, null, candidate.Id == "st-1" ? 100 : -100, null, null)).ToArray() };
        await fixture.StartAndWaitForFirstPlayAsync();
        fixture.Engagement = -100;
        await fixture.AdvanceSamplingAsync(TimeSpan.FromMinutes(2));
        Assert.Single(fixture.SceneCalls);
    }

    [Fact]
    public async Task FailedAuditionsAreExcludedAndAllFailuresWaitWithoutStartingUntestedStreams()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.OnAudition = request => new FlyDjAuditionResponse(request.Candidates.Select((candidate, index) => candidate.Id == "st-1"
            ? new FlyDjAuditionResult(candidate.Id, false, "fetch_failed", null, null, null)
            : new FlyDjAuditionResult(candidate.Id, true, null, index, null, null)).ToList(), 500, new Dictionary<string, double>());
        await fixture.StartAndWaitForFirstPlayAsync();

        var first = fixture.Store.Current.LastDecision!;
        var failed = first.Candidates.Single(candidate => candidate.Error is not null);
        Assert.Contains("fetch_failed", failed.Error);
        Assert.Null(failed.BrainScore);
        Assert.Equal(0, failed.Probability);
        Assert.NotEqual(failed.Url, first.StationUrl);
        Assert.Equal(1.0, first.Candidates.Sum(candidate => candidate.Probability), 6);

        await using var allFail = await FlyDjConductorFixture.CreateAsync();
        allFail.OnAudition = request => new FlyDjAuditionResponse(request.Candidates
            .Select(candidate => new FlyDjAuditionResult(candidate.Id, false, "timeout", null, null, null)).ToList(), 500, new Dictionary<string, double>());
        allFail.AutoAdvanceObservation = false;
        await allFail.StartDjAsync();
        await allFail.UntilAsync(() => allFail.Store.Current.Status == FlyDjConductorStatus.Error,
            advance: TimeSpan.FromSeconds(8), what: "failed observation window", parked: true);
        Assert.Empty(allFail.SceneCalls);
        Assert.Empty(await allFail.DecisionsAsync());
        Assert.Contains("no untested station", allFail.Store.Current.StatusMessage);
        Assert.All(allFail.Store.Current.Evidence, item => Assert.True(item.FailureCount > 0));
    }

    [Fact]
    public async Task EmptyLibraryAndMissingRoomsAreReportedWithoutLoad()
    {
        await using var noStations = await FlyDjConductorFixture.CreateAsync(settings => settings.Stations = []);
        await noStations.StartDjAsync();
        await noStations.UntilAsync(() => noStations.Store.Current.Status == FlyDjConductorStatus.Error, what: "no stations");
        Assert.Contains("station", noStations.Store.Current.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(noStations.SceneCalls);

        await using var noRooms = await FlyDjConductorFixture.CreateAsync(settings => settings.FlyDj.TargetSpeakerIps = []);
        await noRooms.StartDjAsync();
        await noRooms.UntilAsync(() => noRooms.Store.Current.Status == FlyDjConductorStatus.Error, what: "no rooms");
        Assert.Contains("room", noRooms.Store.Current.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, noRooms.Calls(nameof(IFlyDjClient.StartAsync)));
    }

    [Fact]
    public async Task FailedPlaybackStartIsRetriedWithBackoffAndReportedAsAnError()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync();
        fixture.Scenes.Setup(service => service.ApplySceneWithContextAsync(
                It.IsAny<Scene>(), It.IsAny<string?>(), It.IsAny<SceneExecutionContext>(), It.IsAny<Action<IReadOnlyCollection<string>>>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new SceneApplyResult(false, "Speaker offline", "flydj", []));

        await fixture.StartDjAsync();
        await fixture.UntilAsync(() => fixture.Store.Current.Status == FlyDjConductorStatus.Error, what: "playback failure", parked: true);

        Assert.Contains("Speaker offline", fixture.Store.Current.StatusMessage);
        Assert.Empty(fixture.Store.Current.ActiveSpeakerIps);
        Assert.Empty(await fixture.DecisionsAsync());
        Assert.True(fixture.Conductor.IsRunning);
    }

    [Fact]
    public async Task WithBackgroundServicesOffStartReportsAnErrorInsteadOfRunning()
    {
        await using var fixture = await FlyDjConductorFixture.CreateAsync(startHost: false);

        await fixture.Conductor.StartAsync("alice", CancellationToken.None);

        Assert.False(fixture.Conductor.IsRunning);
        Assert.Equal(FlyDjConductorStatus.Error, fixture.Store.Current.Status);
        Assert.Contains("background services", fixture.Store.Current.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Client.Invocations);
        Assert.False((await fixture.SettingsAsync()).FlyDj.WasRunning);
    }

    [Fact]
    public async Task DecisionHistoryIsLoadedAtStartupSoTheStoppedPageShowsIt()
    {
        var fixture = await FlyDjConductorFixture.CreateAsync();
        await fixture.StartAndWaitForFirstPlayAsync();
        await fixture.Conductor.StopAsync(null, CancellationToken.None);
        var persisted = fixture.Store.Current.LastDecision!;

        var restarted = new FlyDjStateStore(fixture.Client.Object, fixture.Time, NullLogger<FlyDjStateStore>.Instance);
        var conductor = new FlyDjConductorService(fixture.Time, fixture.Provider.GetRequiredService<IServiceScopeFactory>(), fixture.Commands,
            fixture.Snapshots, restarted, fixture.Client.Object, NullLogger<FlyDjConductorService>.Instance, fixture.Schedules.Object);
        await ((IHostedService)conductor).StartAsync(CancellationToken.None);
        await fixture.UntilAsync(() => restarted.Current.RecentDecisions.Count == 1, what: "history loaded");

        Assert.Equal(persisted.StationUrl, restarted.Current.LastDecision!.StationUrl);
        Assert.Equal(FlyDjConductorStatus.Stopped, restarted.Current.Status);
        Assert.NotNull(restarted.Current.LastDecision.DwellSeconds);
        await ((IHostedService)conductor).StopAsync(CancellationToken.None);
        restarted.Dispose();
        await fixture.DisposeAsync();
    }

    private static void PublishSnapshot(FlyDjConductorFixture fixture, string ip, string name, bool playing, string? uri)
        => fixture.Snapshots.Set(new PlaybackSnapshot(ip, name, playing, 20, uri, null, false,
            fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), null));
}
