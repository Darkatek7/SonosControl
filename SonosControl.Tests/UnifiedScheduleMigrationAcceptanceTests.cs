using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class UnifiedScheduleMigrationAcceptanceTests
{
    private static readonly DateTime Stamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public async Task V2ToV3Migration_RunTwice_YieldsIdenticalSettings()
    {
        var (service, repository) = CreateMigrationService(V2Settings());

        var first = await service.MigrateIfRequiredAsync();
        var afterFirst = Json(repository.Current);
        var second = await service.MigrateIfRequiredAsync();

        Assert.True(first.Success);
        Assert.True(first.Changed);
        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Equal(afterFirst, Json(repository.Current));
        Assert.Equal(1, repository.WriteCount);
        var ids = repository.Current.ScheduleWindows.Select(window => window.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(repository.Current.ScheduleWindows, window => !window.HasTimeSchedule && window.Playback!.Id == "unbound");
    }

    [Fact]
    public async Task V2ToV3Migration_IsDeterministicAcrossIndependentRuns()
    {
        var (firstService, firstRepository) = CreateMigrationService(V2Settings());
        var (secondService, secondRepository) = CreateMigrationService(V2Settings());

        await firstService.MigrateIfRequiredAsync();
        await secondService.MigrateIfRequiredAsync();

        Assert.Equal(Json(firstRepository.Current), Json(secondRepository.Current));
    }

    [Fact]
    public async Task AlreadyV3Settings_AreNotRewrittenOrReprioritised()
    {
        var settings = V3Settings();
        var before = Json(settings);
        var (service, repository) = CreateMigrationService(settings);

        var result = await service.MigrateIfRequiredAsync();

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, repository.WriteCount);
        Assert.Equal(before, Json(repository.Current));
    }

    [Fact]
    public async Task V3SettingsWithOutOfRangePriorities_AreRepairedOnceAndThenStable()
    {
        var settings = V3Settings();
        settings.ScheduleWindows[0].Priority = 5000;
        settings.ScheduleWindows[1].Priority = -4;
        var (service, repository) = CreateMigrationService(settings);

        var first = await service.MigrateIfRequiredAsync();
        var repaired = Json(repository.Current);
        var second = await service.MigrateIfRequiredAsync();

        Assert.True(first.Changed);
        Assert.All(repository.Current.ScheduleWindows, window => Assert.InRange(window.Priority, 0, 1000));
        Assert.False(second.Changed);
        Assert.Equal(repaired, Json(repository.Current));
    }

    [Fact]
    public async Task V2Migration_InvalidSceneReferencesBecomeDisabledRepairableEntries()
    {
        var settings = V2Settings();
        settings.ScheduleWindows.Add(new ScheduleWindow { Id = "blank-ref", Name = "Blank ref", SceneId = "  ", IsEnabled = true, Priority = 40 });
        var (service, repository) = CreateMigrationService(settings);

        var result = await service.MigrateIfRequiredAsync();

        Assert.True(result.Success);
        var missing = repository.Current.ScheduleWindows.Single(window => window.Id == "missing-ref");
        Assert.False(missing.IsEnabled);
        Assert.Null(missing.Playback);
        Assert.Null(missing.SceneId);
        Assert.Contains(result.Warnings, warning => warning.Contains("Missing ref", StringComparison.Ordinal));
        var blank = repository.Current.ScheduleWindows.Single(window => window.Id == "blank-ref");
        Assert.False(blank.IsEnabled);
        Assert.Null(blank.Playback);
        Assert.Contains(result.Warnings, warning => warning.Contains("Blank ref", StringComparison.Ordinal));
        var disabled = repository.Current.ScheduleWindows.Single(window => window.Id == "disabled-ref");
        Assert.False(disabled.IsEnabled);
        Assert.False(disabled.Playback!.Enabled);

        // Repairable: assigning a playback to the kept entry and enabling it passes validation.
        missing.Playback = new Scene { Id = "repaired", Name = "Repaired", SpeakerIps = ["10.0.0.10"] };
        missing.IsEnabled = true;
        SettingsEditingService.ValidateWindow(missing, repository.Current);
        SettingsEditingService.ValidateScene(missing.Playback, repository.Current, checkUniqueName: false);
    }

    [Fact]
    public async Task V2Migration_SceneIdMatchingIsCaseInsensitive()
    {
        var settings = V2Settings();
        settings.ScheduleWindows.Add(new ScheduleWindow { Id = "cased", Name = "Cased", SceneId = "SHARED", IsEnabled = true });
        var (service, repository) = CreateMigrationService(settings);

        await service.MigrateIfRequiredAsync();

        var cased = repository.Current.ScheduleWindows.Single(window => window.Id == "cased");
        Assert.True(cased.IsEnabled);
        Assert.Equal("Shared", cased.Playback!.Name);
    }

    [Fact]
    public async Task V2Migration_SharedSceneIsCopiedPerScheduleAndTimerBecomesIndependentRuntimeLimit()
    {
        var (service, repository) = CreateMigrationService(V2Settings());

        await service.MigrateIfRequiredAsync();

        var one = repository.Current.ScheduleWindows.Single(window => window.Id == "shared-one");
        var two = repository.Current.ScheduleWindows.Single(window => window.Id == "shared-two");
        Assert.Equal("shared-one-playback", one.Playback!.Id);
        Assert.Equal("shared-two-playback", two.Playback!.Id);
        Assert.Equal(45, one.Playback.TimerMinutes);
        Assert.Equal(45, two.Playback.TimerMinutes);
        one.Playback.TimerMinutes = null;
        Assert.Equal(45, two.Playback.TimerMinutes);
        Assert.NotSame(one.Playback.Actions[0], two.Playback.Actions[0]);
        Assert.Equal(25, two.Playback.Actions[0].Volume);
        Assert.Null(one.SceneId);
        Assert.Null(two.SceneId);
    }

    [Fact]
    public async Task V2Migration_UnboundScenesBecomeTemplatesKeepingEnabledStateTimerAndTargets()
    {
        var (service, repository) = CreateMigrationService(V2Settings());

        await service.MigrateIfRequiredAsync();

        var template = Assert.Single(repository.Current.ScheduleWindows, window => window.Playback?.Id == "unbound");
        Assert.False(template.HasTimeSchedule);
        Assert.True(template.IsEnabled);
        Assert.Null(template.SceneId);
        Assert.Equal(20, template.Playback!.TimerMinutes);
        Assert.Equal(["10.0.0.11"], template.Playback.SpeakerIps);
        var disabledTemplate = Assert.Single(repository.Current.ScheduleWindows, window => window.Playback?.Id == "unbound-disabled");
        Assert.False(disabledTemplate.HasTimeSchedule);
        Assert.False(disabledTemplate.IsEnabled);
    }

    [Fact]
    public async Task BackupRoundTrip_OfV3Document_PreservesEverythingViaImportAndRestore()
    {
        using var fixture = new BackupFixture();
        var original = V3Settings();
        var expected = Json(original);
        var json = JsonSerializer.Serialize(original);

        await using (var content = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            await fixture.Service.ImportAsync("v3.json", content, content.Length);
        var imported = Assert.Single(fixture.Replaced);
        Assert.Equal(expected, Json(imported));
        Assert.Equal(SonosSettings.CurrentSettingsSchemaVersion, imported.SettingsSchemaVersion);
        var template = Assert.Single(imported.ScheduleWindows, window => !window.HasTimeSchedule);
        Assert.NotNull(template.Playback);
        var speaker = Assert.Single(imported.Speakers, item => item.IpAddress == "10.0.0.10");
        Assert.Equal(45, speaker.ManualAutoStopMinutes);
        Assert.Equal(new TimeOnly(23, 30), speaker.ManualAutoStopDays[DayOfWeek.Monday].StopTime);
        Assert.Equal(new TimeOnly(6, 0), speaker.ManualAutoStopDays[DayOfWeek.Monday].ReleaseTime);

        fixture.WriteBackup("manual.json", json);
        await fixture.Service.RestoreAsync("manual.json");
        Assert.Equal(2, fixture.Replaced.Count);
        Assert.Equal(expected, Json(fixture.Replaced[1]));
    }

    [Fact]
    public async Task RestoringV2Backup_MigratesToV3WithEmbeddedPlayback()
    {
        using var fixture = new BackupFixture();
        var v2 = V2Settings();
        fixture.WriteBackup("legacy.json", JsonSerializer.Serialize(v2));

        await fixture.Service.RestoreAsync("legacy.json");

        var restored = Assert.Single(fixture.Replaced);
        Assert.Equal(SonosSettings.CurrentSettingsSchemaVersion, restored.SettingsSchemaVersion);
        Assert.All(restored.ScheduleWindows, window => Assert.Null(window.SceneId));
        Assert.All(restored.ScheduleWindows.Where(window => window.IsEnabled), window => Assert.NotNull(window.Playback));
        Assert.Equal("Shared", restored.ScheduleWindows.Single(window => window.Id == "shared-one").Playback!.Name);
        Assert.Contains(restored.ScheduleWindows, window => !window.HasTimeSchedule && window.Playback!.Id == "unbound");
        Assert.False(restored.ScheduleWindows.Single(window => window.Id == "missing-ref").IsEnabled);
        // Restoring the migrated result again is stable.
        var migratedJson = Json(restored);
        fixture.WriteBackup("again.json", JsonSerializer.Serialize(restored));
        await fixture.Service.RestoreAsync("again.json");
        Assert.Equal(migratedJson, Json(fixture.Replaced[1]));
    }

    private static string Json(SonosSettings? settings) => JsonSerializer.Serialize(settings);

    private static SonosSettings V2Settings() => new()
    {
        SettingsSchemaVersion = 2,
        Speakers =
        [
            new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.10" },
            new SonosSpeaker { Name = "Living room", IpAddress = "10.0.0.11" }
        ],
        Scenes =
        [
            new Scene
            {
                Id = "shared", Name = "Shared", TimerMinutes = 45, LastModifiedUtc = Stamp,
                SourceSelectionMode = SceneSourceSelectionMode.Specific, SourceType = SceneSourceType.Station, SourceUrl = "https://radio.example/a",
                SpeakerIps = ["10.0.0.10"],
                Actions = [new SceneAction { SpeakerIp = "10.0.0.10", Volume = 25, IncludeInPlayback = true, IsMaster = true }]
            },
            new Scene { Id = "unbound", Name = "Unbound", TimerMinutes = 20, LastModifiedUtc = Stamp, SourceSelectionMode = SceneSourceSelectionMode.Resume, SpeakerIps = ["10.0.0.11"] },
            new Scene { Id = "unbound-disabled", Name = "Unbound disabled", Enabled = false, LastModifiedUtc = Stamp, SourceSelectionMode = SceneSourceSelectionMode.Resume, SpeakerIps = ["10.0.0.11"] },
            new Scene { Id = "off", Name = "Off", Enabled = false, LastModifiedUtc = Stamp, SourceSelectionMode = SceneSourceSelectionMode.Resume, SpeakerIps = ["10.0.0.11"] }
        ],
        ScheduleWindows =
        [
            new ScheduleWindow { Id = "shared-one", Name = "Shared one", SceneId = "shared", Priority = 10, LastModifiedUtc = Stamp },
            new ScheduleWindow { Id = "shared-two", Name = "Shared two", SceneId = "shared", Priority = 20, LastModifiedUtc = Stamp, StartTime = new(7, 0), StopTime = new(8, 0) },
            new ScheduleWindow { Id = "missing-ref", Name = "Missing ref", SceneId = "nope", Priority = 30, LastModifiedUtc = Stamp },
            new ScheduleWindow { Id = "disabled-ref", Name = "Disabled ref", SceneId = "off", Priority = 40, LastModifiedUtc = Stamp }
        ]
    };

    private static SonosSettings V3Settings()
    {
        var days = Enum.GetValues<DayOfWeek>().ToDictionary(day => day,
            _ => new ManualAutoStopDay { Enabled = true, StopTime = new(23, 30), ReleaseTime = new(6, 0) });
        return new SonosSettings
        {
            SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion,
            MaxVolume = 70,
            Volume = 20,
            AutomationExcludedDates = [new DateOnly(2026, 12, 24)],
            AnnualAutomationExcludedDates = [new DateOnly(2026, 12, 25)],
            Speakers =
            [
                new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.10", ManualAutoStopMinutes = 45, ManualAutoStopDays = days },
                new SonosSpeaker { Name = "Living room", IpAddress = "10.0.0.11" }
            ],
            Scenes = [new Scene { Id = "template-scene", Name = "Template scene", LastModifiedUtc = Stamp, SourceSelectionMode = SceneSourceSelectionMode.Resume, SpeakerIps = ["10.0.0.11"] }],
            ScheduleWindows =
            [
                new ScheduleWindow
                {
                    Id = "morning", Name = "Morning", Priority = 0, StartTime = new(6, 0), StopTime = new(9, 0),
                    RecurrenceType = ScheduleRecurrenceType.CustomDays, DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Friday],
                    ExcludedDates = [], FadeInSeconds = 5, FadeOutSeconds = 7, LastModifiedUtc = Stamp,
                    Playback = new Scene
                    {
                        Id = "morning-playback", Name = "Morning playback", TimerMinutes = 30, LastModifiedUtc = Stamp,
                        SourceSelectionMode = SceneSourceSelectionMode.Specific, SourceType = SceneSourceType.Station, SourceUrl = "https://radio.example/m",
                        SpeakerIps = ["10.0.0.10"],
                        Actions = [new SceneAction { SpeakerIp = "10.0.0.10", Volume = 30, IncludeInPlayback = true, IsMaster = true }]
                    }
                },
                new ScheduleWindow
                {
                    Id = "evening", Name = "Evening", Priority = 500, StartTime = new(22, 0), StopTime = new(2, 0),
                    RecurrenceType = ScheduleRecurrenceType.Daily, LastModifiedUtc = Stamp,
                    Playback = new Scene { Id = "evening-playback", Name = "Evening playback", LastModifiedUtc = Stamp, SourceSelectionMode = SceneSourceSelectionMode.Resume, SpeakerIps = ["10.0.0.10", "10.0.0.11"] }
                },
                new ScheduleWindow
                {
                    Id = "template", Name = "Template scene", Priority = 1000, HasTimeSchedule = false, LastModifiedUtc = Stamp,
                    Playback = new Scene { Id = "template-scene", Name = "Template scene", LastModifiedUtc = Stamp, SourceSelectionMode = SceneSourceSelectionMode.Resume, SpeakerIps = ["10.0.0.11"] }
                }
            ]
        };
    }

    private static (SettingsSchemaMigrationService Service, InMemoryRepo Repository) CreateMigrationService(SonosSettings settings)
    {
        var repository = new InMemoryRepo(settings);
        return (new SettingsSchemaMigrationService(repository, new AutomationRuntimeStatus(), NullLogger<SettingsSchemaMigrationService>.Instance), repository);
    }

    private sealed class InMemoryRepo(SonosSettings initial) : ISettingsRepo
    {
        public SonosSettings Current { get; private set; } = initial;
        public int WriteCount { get; private set; }
        public Task<SonosSettings?> GetSettings() => Task.FromResult<SonosSettings?>(Current);

        public Task<SonosSettings> UpdateSettingsAsync(Action<SonosSettings> update, CancellationToken cancellationToken = default)
        {
            var candidate = SettingsChanges.Deserialize(SettingsChanges.Serialize(Current));
            var before = SettingsChanges.Serialize(candidate);
            update(candidate);
            if (before != SettingsChanges.Serialize(candidate))
            {
                Current = candidate;
                WriteCount++;
            }
            return Task.FromResult(candidate);
        }

        public Task SaveChangesAsync(SonosSettings? settings) => throw new NotSupportedException();
        public Task<string?> ReplaceSettingsAsync(SonosSettings settings, string label = "replacement", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> CreateVersionedBackupAsync(string label, CancellationToken cancellationToken = default) => Task.FromResult<string?>($"config-{label}.json");
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"sonos-unified-backup-{Guid.NewGuid():N}");
        private readonly ApplicationDbContext _db;

        public BackupFixture()
        {
            Directory.CreateDirectory(Path.Combine(_root, "settings", "backups"));
            var repository = new Mock<ISettingsRepo>();
            repository.Setup(item => item.GetSettings()).ReturnsAsync(new SonosSettings { SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion });
            repository.Setup(item => item.ReplaceSettingsAsync(It.IsAny<SonosSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((SonosSettings settings, string _, CancellationToken _) => Replaced.Add(settings))
                .ReturnsAsync((SonosSettings _, string label, CancellationToken _) => $"config-{label}.json");
            _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase($"unified-backup-{Guid.NewGuid():N}").Options);
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(value => value.ContentRootPath).Returns(_root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Settings:DataDirectory"] = Path.Combine(_root, "settings")
            }).Build();
            Service = new SettingsBackupService(repository.Object, new ActionLogger(_db, new HttpContextAccessor()),
                NullLogger<SettingsBackupService>.Instance, environment.Object, configuration);
        }

        public List<SonosSettings> Replaced { get; } = [];
        public SettingsBackupService Service { get; }
        public void WriteBackup(string name, string json) => File.WriteAllText(Path.Combine(_root, "settings", "backups", name), json);
        public void Dispose() { _db.Dispose(); Directory.Delete(_root, recursive: true); }
    }
}
