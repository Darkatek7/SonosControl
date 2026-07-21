using Microsoft.Extensions.Logging.Abstractions;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SettingsSchemaMigrationServiceTests
{
    [Fact]
    public async Task Migration_GroupsIdenticalActiveDays_AndMapsSpecificGlobalSource()
    {
        var settings = LegacySettings();
        settings.ActiveDays =
        [
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday
        ];
        settings.StartTime = new TimeOnly(6, 15);
        settings.StopTime = new TimeOnly(9, 45);
        settings.AutoPlayStationUrl = "https://radio.example/live";

        var (service, repository, status) = CreateService(settings);

        var result = await service.MigrateIfRequiredAsync();

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(SonosSettings.CurrentSettingsSchemaVersion, repository.Current!.SettingsSchemaVersion);
        var window = Assert.Single(repository.Current.ScheduleWindows);
        Assert.Equal(new TimeOnly(6, 15), window.StartTime);
        Assert.Equal(new TimeOnly(9, 45), window.StopTime);
        Assert.Equal(5, window.DaysOfWeek.Count);
        Assert.Equal(1000, window.Priority);

        var scene = Assert.Single(repository.Current.Scenes);
        Assert.Equal(SceneSourceSelectionMode.Specific, scene.SourceSelectionMode);
        Assert.Equal(SceneSourceType.Station, scene.SourceType);
        Assert.Equal("https://radio.example/live", scene.SourceUrl);
        Assert.Equal(1, repository.BackupCount);
        Assert.Equal(AutomationRuntimePhase.Ready, status.Snapshot.Phase);
    }

    [Fact]
    public async Task Migration_MapsRandomAndResumeModes_AndPreservesOvernightTimes()
    {
        var settings = LegacySettings();
        settings.ActiveDays = [DayOfWeek.Monday, DayOfWeek.Tuesday];
        settings.DailySchedules[DayOfWeek.Monday] = new DaySchedule
        {
            StartTime = new TimeOnly(22, 0),
            StopTime = new TimeOnly(2, 0),
            PlayRandomYouTubeMusic = true,
            IsSyncedPlayback = false
        };
        settings.DailySchedules[DayOfWeek.Tuesday] = new DaySchedule
        {
            StartTime = new TimeOnly(7, 0),
            StopTime = new TimeOnly(8, 0)
        };

        var (service, repository, _) = CreateService(settings);
        var result = await service.MigrateIfRequiredAsync();

        Assert.True(result.Success);
        Assert.Equal(2, repository.Current!.ScheduleWindows.Count);
        var overnightWindow = repository.Current.ScheduleWindows.Single(window => window.StartTime == new TimeOnly(22, 0));
        var randomScene = repository.Current.Scenes.Single(scene => scene.Id == overnightWindow.SceneId);
        Assert.Equal(new TimeOnly(2, 0), overnightWindow.StopTime);
        Assert.Equal(SceneSourceSelectionMode.Random, randomScene.SourceSelectionMode);
        Assert.Equal(SceneSourceType.YouTubeMusic, randomScene.SourceType);
        Assert.False(randomScene.IsSyncedPlayback);

        var resumeWindow = repository.Current.ScheduleWindows.Single(window => window.StartTime == new TimeOnly(7, 0));
        var resumeScene = repository.Current.Scenes.Single(scene => scene.Id == resumeWindow.SceneId);
        Assert.Equal(SceneSourceSelectionMode.Resume, resumeScene.SourceSelectionMode);
        Assert.Equal(SceneSourceType.None, resumeScene.SourceType);
        Assert.Null(resumeScene.SourceUrl);
    }

    [Fact]
    public async Task Migration_ConvertsPlayableAndSkippedHolidays_WithoutBlockingExistingWindows()
    {
        var settings = LegacySettings();
        settings.ActiveDays = [DayOfWeek.Monday];
        settings.AutoPlayStationUrl = "https://radio.example/default";
        settings.HolidaySchedules =
        [
            new HolidaySchedule
            {
                Date = new DateOnly(2026, 7, 20),
                Name = "Summer override",
                StartTime = new TimeOnly(10, 0),
                StopTime = new TimeOnly(12, 0),
                SpotifyUrl = "spotify:playlist:summer"
            },
            new HolidaySchedule
            {
                Date = new DateOnly(2026, 7, 27),
                Name = "Quiet day",
                SkipPlayback = true
            }
        ];

        settings.Scenes.Add(new Scene { Id = "existing-scene", Name = "Existing", Enabled = true });
        settings.ScheduleWindows =
        [
            ExistingWindow("later", 90),
            ExistingWindow("earlier", 10)
        ];

        var (service, repository, _) = CreateService(settings);
        var result = await service.MigrateIfRequiredAsync();

        Assert.True(result.Success);
        var windows = repository.Current!.ScheduleWindows;
        var holiday = windows.Single(window => window.StartDate == new DateOnly(2026, 7, 20));
        Assert.Equal(holiday.StartDate, holiday.EndDate);
        Assert.Equal(0, holiday.Priority);
        var holidayScene = repository.Current.Scenes.Single(scene => scene.Id == holiday.SceneId);
        Assert.Equal(SceneSourceSelectionMode.Specific, holidayScene.SourceSelectionMode);
        Assert.Equal(SceneSourceType.Spotify, holidayScene.SourceType);

        var baseline = windows.Single(window => window.Priority == 1000);
        Assert.Contains(new DateOnly(2026, 7, 20), baseline.ExcludedDates);
        Assert.Contains(new DateOnly(2026, 7, 27), baseline.ExcludedDates);

        var migratedExisting = windows.Where(window => window.Id is "earlier" or "later").OrderBy(window => window.Priority).ToList();
        Assert.Equal(["earlier", "later"], migratedExisting.Select(window => window.Id).ToArray());
        Assert.Equal([2000, 2001], migratedExisting.Select(window => window.Priority).ToArray());
        Assert.All(migratedExisting, window => Assert.Empty(window.ExcludedDates));
    }

    [Fact]
    public async Task Migration_ConvertsPlayableHolidayOutsideBaselineActiveDays()
    {
        var settings = LegacySettings();
        settings.ActiveDays = [DayOfWeek.Monday];
        settings.HolidaySchedules =
        [
            new HolidaySchedule
            {
                Date = new DateOnly(2026, 7, 21),
                Name = "Tuesday special",
                StartTime = new TimeOnly(11, 0),
                StopTime = new TimeOnly(13, 0),
                YouTubeUrl = "https://youtube.example/special"
            }
        ];

        var (service, repository, _) = CreateService(settings);
        var result = await service.MigrateIfRequiredAsync();

        Assert.True(result.Success);
        var holiday = repository.Current!.ScheduleWindows.Single(window => window.StartDate == new DateOnly(2026, 7, 21));
        var scene = repository.Current.Scenes.Single(item => item.Id == holiday.SceneId);
        Assert.Equal(SceneSourceSelectionMode.Specific, scene.SourceSelectionMode);
        Assert.Equal(SceneSourceType.YouTube, scene.SourceType);
    }

    [Fact]
    public async Task Migration_DisablesExistingWindowsWithMissingOrDisabledScenes()
    {
        var settings = LegacySettings();
        settings.Scenes =
        [
            new Scene { Id = "disabled-scene", Name = "Disabled", Enabled = false }
        ];
        settings.ScheduleWindows =
        [
            new ScheduleWindow { Id = "missing", Name = "Missing", SceneId = "does-not-exist", IsEnabled = true },
            new ScheduleWindow { Id = "disabled", Name = "Disabled ref", SceneId = "disabled-scene", IsEnabled = true }
        ];

        var (service, repository, status) = CreateService(settings);
        var result = await service.MigrateIfRequiredAsync();

        Assert.True(result.Success);
        Assert.All(repository.Current!.ScheduleWindows, window => Assert.False(window.IsEnabled));
        Assert.Contains(result.Warnings, warning => warning.Contains("Missing", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Disabled ref", StringComparison.Ordinal));
        Assert.Equal(AutomationRuntimePhase.Ready, status.Snapshot.Phase);
    }

    [Fact]
    public async Task Migration_IsIdempotent()
    {
        var settings = LegacySettings();
        settings.ActiveDays = [DayOfWeek.Monday, DayOfWeek.Wednesday];
        settings.AutoPlayRandomStation = true;
        var (service, repository, _) = CreateService(settings);

        var first = await service.MigrateIfRequiredAsync();
        var sceneIds = repository.Current!.Scenes.Select(scene => scene.Id).ToArray();
        var windowIds = repository.Current.ScheduleWindows.Select(window => window.Id).ToArray();
        var second = await service.MigrateIfRequiredAsync();

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(1, repository.WriteCount);
        Assert.Equal(1, repository.BackupCount);
        Assert.Equal(sceneIds, repository.Current.Scenes.Select(scene => scene.Id));
        Assert.Equal(windowIds, repository.Current.ScheduleWindows.Select(window => window.Id));
    }

    [Fact]
    public async Task Migration_WriteFailure_RetainsOriginalConfigurationAndPausesAutomation()
    {
        var settings = LegacySettings();
        settings.ActiveDays = [DayOfWeek.Monday];
        settings.AutoPlayStationUrl = "https://radio.example/live";
        var (service, repository, status) = CreateService(settings);
        repository.ThrowOnWrite = true;

        var result = await service.MigrateIfRequiredAsync();

        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Same(settings, repository.Current);
        Assert.Equal(0, repository.Current!.SettingsSchemaVersion);
        Assert.Empty(repository.Current.Scenes);
        Assert.Empty(repository.Current.ScheduleWindows);
        Assert.Equal(AutomationRuntimePhase.Failed, status.Snapshot.Phase);
        Assert.Contains("migration failed", status.Snapshot.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migration_BackupFailure_RetainsOriginalConfigurationAndDoesNotWrite()
    {
        var settings = LegacySettings();
        settings.ActiveDays = [DayOfWeek.Monday];
        var (service, repository, status) = CreateService(settings);
        repository.ThrowOnBackup = true;

        var result = await service.MigrateIfRequiredAsync();

        Assert.False(result.Success);
        Assert.Same(settings, repository.Current);
        Assert.Equal(0, repository.WriteCount);
        Assert.Equal(AutomationRuntimePhase.Failed, status.Snapshot.Phase);
        Assert.Contains("backup", status.Snapshot.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Migration_VerificationFailure_RollsBackOriginalConfiguration()
    {
        var settings = LegacySettings();
        settings.ActiveDays = [DayOfWeek.Monday];
        settings.AutoPlayStationUrl = "https://radio.example/live";
        var (service, repository, status) = CreateService(settings);
        repository.FailVerificationAfterWrite = true;

        var result = await service.MigrateIfRequiredAsync();

        Assert.False(result.Success);
        Assert.Same(settings, repository.Current);
        Assert.Equal(0, repository.Current!.SettingsSchemaVersion);
        Assert.Equal(2, repository.WriteCount);
        Assert.Equal(AutomationRuntimePhase.Failed, status.Snapshot.Phase);
    }

    private static SonosSettings LegacySettings()
        => new()
        {
            SettingsSchemaVersion = 0,
            ActiveDays = new(),
            DailySchedules = new(),
            HolidaySchedules = new(),
            Scenes = new(),
            ScheduleWindows = new(),
            Speakers =
            [
                new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.10" },
                new SonosSpeaker { Name = "Living room", IpAddress = "10.0.0.11" }
            ]
        };

    private static ScheduleWindow ExistingWindow(string id, int priority)
        => new()
        {
            Id = id,
            Name = id,
            Priority = priority,
            SceneId = "existing-scene",
            IsEnabled = true
        };

    private static (SettingsSchemaMigrationService Service, InMemorySettingsRepo Repository, AutomationRuntimeStatus Status)
        CreateService(SonosSettings settings)
    {
        var repository = new InMemorySettingsRepo(settings);
        var status = new AutomationRuntimeStatus();
        var service = new SettingsSchemaMigrationService(
            repository,
            status,
            NullLogger<SettingsSchemaMigrationService>.Instance);
        return (service, repository, status);
    }

    private sealed class InMemorySettingsRepo : ISettingsRepo
    {
        public InMemorySettingsRepo(SonosSettings settings) => Current = settings;

        public SonosSettings? Current { get; private set; }
        public int WriteCount { get; private set; }
        public int BackupCount { get; private set; }
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnBackup { get; set; }
        public bool FailVerificationAfterWrite { get; set; }
        private bool _returnNullOnNextRead;

        public Task<SonosSettings?> GetSettings()
        {
            if (_returnNullOnNextRead)
            {
                _returnNullOnNextRead = false;
                return Task.FromResult<SonosSettings?>(null);
            }

            return Task.FromResult(Current);
        }

        public Task<string?> CreateVersionedBackupAsync(string label, CancellationToken cancellationToken = default)
        {
            if (ThrowOnBackup)
            {
                throw new IOException("Simulated backup failure.");
            }

            BackupCount++;
            return Task.FromResult<string?>($"config-{label}.json");
        }

        public Task WriteSettings(SonosSettings? settings)
        {
            if (ThrowOnWrite)
            {
                throw new IOException("Simulated settings write failure.");
            }

            Current = settings;
            WriteCount++;
            if (FailVerificationAfterWrite
                && settings?.SettingsSchemaVersion == SonosSettings.CurrentSettingsSchemaVersion)
            {
                FailVerificationAfterWrite = false;
                _returnNullOnNextRead = true;
            }

            return Task.CompletedTask;
        }
    }
}
