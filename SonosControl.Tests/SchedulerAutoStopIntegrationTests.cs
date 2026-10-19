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

public sealed class SchedulerAutoStopIntegrationTests
{
    [Fact]
    public async Task WorkerStopsFirstThenPostEndManualRestartIsNotPausedByStaleSchedulerOccurrence()
    {
        using var fixture = new Fixture();
        await fixture.Scheduler.EvaluateNowAsync();
        fixture.Time.SetUtcNow(new DateTimeOffset(2026, 10, 19, 11, 1, 0, TimeSpan.Zero));

        await fixture.AutoStop.ProcessDueAsync();
        await using (var manual = await fixture.Commands.EnterManualAsync([Fixture.SpeakerIp]))
            await fixture.ManualPlayback.PlaybackSucceededAsync([Fixture.SpeakerIp]);
        await fixture.Scheduler.EvaluateNowAsync();

        fixture.Connector.Verify(connector => connector.PausePlaying(Fixture.SpeakerIp, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManualRestartDuringActiveScheduleRetainsBoundaryStopAndIsPausedOnce()
    {
        using var fixture = new Fixture();
        await fixture.Scheduler.EvaluateNowAsync();

        await using (var manual = await fixture.Commands.EnterManualAsync([Fixture.SpeakerIp]))
            await fixture.ManualPlayback.PlaybackSucceededAsync([Fixture.SpeakerIp]);
        var deadline = await fixture.AutoStop.GetNextStopAsync(Fixture.SpeakerIp);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.Zero), deadline!.Value.StopAtUtc);

        fixture.Time.SetUtcNow(new DateTimeOffset(2026, 10, 19, 11, 1, 0, TimeSpan.Zero));
        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.AutoStop.ProcessDueAsync();

        fixture.Connector.Verify(connector => connector.PausePlaying(Fixture.SpeakerIp, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await fixture.AutoStop.GetNextStopAsync(Fixture.SpeakerIp));
    }

    [Fact]
    public async Task RemovingWindowBeforeEndStillCleansUpItsOwnedPlayback()
    {
        using var fixture = new Fixture();
        await fixture.Scheduler.EvaluateNowAsync();
        fixture.Settings.ScheduleWindows.Clear();

        await fixture.Scheduler.EvaluateNowAsync();
        await fixture.AutoStop.ProcessDueAsync();

        fixture.Connector.Verify(connector => connector.PausePlaying(Fixture.SpeakerIp, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await fixture.AutoStop.GetNextStopAsync(Fixture.SpeakerIp));
    }

    private sealed class Fixture : IDisposable
    {
        public const string SpeakerIp = "10.0.0.1";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        public Clock Time { get; } = new();
        public PlaybackCommandCoordinator Commands { get; }
        public Mock<ISonosConnectorRepo> Connector { get; } = new();
        public ManualAutoStopService AutoStop { get; }
        public AutomationSchedulerService Scheduler { get; }
        public ManualPlaybackService ManualPlayback { get; }
        public SonosSettings Settings { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            var dbPath = Path.Combine(_directory, "integration.db");
            Commands = new PlaybackCommandCoordinator(Time);
            var window = new ScheduleWindow
            {
                Id = "schedule",
                Name = "Morning",
                SceneId = "scene",
                IsEnabled = true,
                StartTime = new(9, 0),
                StopTime = new(11, 0),
                Priority = 1,
                RecurrenceType = ScheduleRecurrenceType.Daily,
                LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Playback = new Scene
                {
                    Id = "scene",
                    Name = "Morning",
                    Enabled = true,
                    IsSyncedPlayback = true,
                    Actions = [new SceneAction { SpeakerIp = SpeakerIp, IncludeInPlayback = true }]
                }
            };
            Settings = new SonosSettings
            {
                SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion,
                Speakers = [new SonosSpeaker { Name = "Kitchen", IpAddress = SpeakerIp, ManualAutoStopMinutes = 30 }],
                Scenes = [window.Playback!],
                ScheduleWindows = [window]
            };
            var settingsRepo = new Mock<ISettingsRepo>();
            settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(Settings);
            Connector.Setup(connector => connector.GetAllSpeakersInGroup(SpeakerIp, It.IsAny<CancellationToken>()))
                .ReturnsAsync([SpeakerIp]);
            Connector.Setup(connector => connector.PausePlaying(SpeakerIp, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var orchestration = new Mock<ISceneOrchestrationService>();
            orchestration.Setup(service => service.ApplySceneWithContextAsync(
                    It.IsAny<Scene>(), "automation-scheduler", It.IsAny<SceneExecutionContext>(),
                    It.IsAny<Action<IReadOnlyCollection<string>>>(), It.IsAny<CancellationToken>(),
                    It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>()))
                .ReturnsAsync((Scene scene, string? _, SceneExecutionContext _, Action<IReadOnlyCollection<string>>? record,
                    CancellationToken _, IReadOnlyCollection<string>? targets, bool _) =>
                {
                    var applied = targets ?? [SpeakerIp];
                    record?.Invoke(applied);
                    return new SceneApplyResult(true, "Applied", scene.Id, applied.ToList());
                });

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Automation:TimeZone"] = "UTC" })
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton(TimeProviderForServices);
            services.AddSingleton(settingsRepo.Object);
            services.AddSingleton(Connector.Object);
            services.AddSingleton(orchestration.Object);
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite($"Data Source={dbPath};Pooling=False"));
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
                status, NullLogger<AutomationSchedulerService>.Instance, configuration, Time,
                TimeZoneInfo.Utc, Commands, autoStop: AutoStop);
            ManualPlayback = new ManualPlaybackService(Commands, _provider, Connector.Object,
                NullLogger<ManualPlaybackService>.Instance, AutoStop);
        }

        private TimeProvider TimeProviderForServices => Time;

        public void Dispose()
        {
            _provider.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }
}
