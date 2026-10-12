using System.Security.Cryptography;
using System.Text;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public enum AutomationRuntimePhase
{
    Pending,
    Ready,
    Failed
}

public sealed record AutomationRuntimeSnapshot(
    AutomationRuntimePhase Phase,
    int SettingsSchemaVersion,
    string? Error,
    IReadOnlyList<string> Warnings,
    string? ActiveScheduleName,
    DateTimeOffset? LastEvaluationUtc,
    string? SchedulerError,
    DateTimeOffset? NextRetryUtc = null)
{
    public string? FailureMessage => Phase == AutomationRuntimePhase.Failed ? Error : SchedulerError;
    public bool HasError => Phase == AutomationRuntimePhase.Failed || !string.IsNullOrWhiteSpace(SchedulerError);
    public bool IsRunning => Phase == AutomationRuntimePhase.Ready && !HasError && !string.IsNullOrWhiteSpace(ActiveScheduleName);
    public string StatusClass => HasError ? "is-error" : Phase == AutomationRuntimePhase.Ready ? "is-ready" : "is-pending";
    public string StatusLabel => Phase == AutomationRuntimePhase.Failed ? "Automation paused"
        : HasError ? "Automation error"
        : Phase != AutomationRuntimePhase.Ready ? "Preparing automation"
        : IsRunning ? "Automation running" : "Scheduler ready";
}

public sealed class AutomationRuntimeStatus
{
    private readonly object _sync = new();
    private AutomationRuntimePhase _phase = AutomationRuntimePhase.Pending;
    private int _schemaVersion;
    private string? _error;
    private IReadOnlyList<string> _warnings = Array.Empty<string>();
    private string? _activeScheduleName;
    private DateTimeOffset? _lastEvaluationUtc;
    private string? _schedulerError;
    private DateTimeOffset? _nextRetryUtc;

    public event Action? Changed;

    public AutomationRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new AutomationRuntimeSnapshot(
                    _phase,
                    _schemaVersion,
                    _error,
                    _warnings.ToArray(),
                    _activeScheduleName,
                    _lastEvaluationUtc,
                    _schedulerError,
                    _nextRetryUtc);
            }
        }
    }

    public void SetReady(int schemaVersion, IReadOnlyList<string>? warnings = null)
    {
        lock (_sync)
        {
            _phase = AutomationRuntimePhase.Ready;
            _schemaVersion = schemaVersion;
            _error = null;
            _warnings = warnings?.ToArray() ?? Array.Empty<string>();
        }

        Changed?.Invoke();
    }

    public void SetFailed(int schemaVersion, string error, IReadOnlyList<string>? warnings = null)
    {
        lock (_sync)
        {
            _phase = AutomationRuntimePhase.Failed;
            _schemaVersion = schemaVersion;
            _error = error;
            _warnings = warnings?.ToArray() ?? Array.Empty<string>();
            _activeScheduleName = null;
            _nextRetryUtc = null;
        }

        Changed?.Invoke();
    }

    public void RecordEvaluation(string? activeScheduleName, DateTimeOffset evaluatedUtc, string? error = null, DateTimeOffset? nextRetryUtc = null)
    {
        lock (_sync)
        {
            _activeScheduleName = activeScheduleName;
            _lastEvaluationUtc = evaluatedUtc;
            _schedulerError = error;
            _nextRetryUtc = nextRetryUtc;
        }

        Changed?.Invoke();
    }
}

public sealed record SettingsMigrationResult(
    bool Success,
    bool Changed,
    int SchemaVersion,
    IReadOnlyList<string> Warnings,
    string? Error = null);

public interface ISettingsSchemaMigrationService
{
    Task<SettingsMigrationResult> MigrateIfRequiredAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsSchemaMigrationService : ISettingsSchemaMigrationService
{
    private readonly ISettingsRepo _settingsRepo;
    private readonly AutomationRuntimeStatus _runtimeStatus;
    private readonly ILogger<SettingsSchemaMigrationService> _logger;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);

    public SettingsSchemaMigrationService(
        ISettingsRepo settingsRepo,
        AutomationRuntimeStatus runtimeStatus,
        ILogger<SettingsSchemaMigrationService> logger)
    {
        _settingsRepo = settingsRepo;
        _runtimeStatus = runtimeStatus;
        _logger = logger;
    }

    public async Task<SettingsMigrationResult> MigrateIfRequiredAsync(CancellationToken cancellationToken = default)
    {
        await _migrationLock.WaitAsync(cancellationToken);
        try
        {
            var migrated = false;
            var warnings = new List<string>();
            var persisted = await _settingsRepo.UpdateSettingsAsync(settings =>
            {
                migrated = settings.SettingsSchemaVersion < SonosSettings.CurrentSettingsSchemaVersion
                    || settings.ScheduleWindows.Any(window => window.Priority is < 0 or > ScheduleWindowOrdering.MaximumPriority);
                warnings = PrepareForPersistence(settings);
            }, cancellationToken);
            _runtimeStatus.SetReady(persisted.SettingsSchemaVersion, warnings);
            return new SettingsMigrationResult(true, migrated, persisted.SettingsSchemaVersion, warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = $"Settings migration failed: {ex.Message}";
            _runtimeStatus.SetFailed(0, message);
            _logger.LogError(ex, "Settings migration failed. Time-based automation remains disabled.");
            return new SettingsMigrationResult(false, false, 0, Array.Empty<string>(), message);
        }
        finally { _migrationLock.Release(); }
    }

    public static List<string> PrepareForPersistence(SonosSettings settings)
    {
        if (settings.SettingsSchemaVersion >= SonosSettings.CurrentSettingsSchemaVersion)
        {
            settings.ScheduleWindows ??= new();
            var repairs = new List<string>();
            if (settings.ScheduleWindows.Any(window => window.Priority is < 0 or > ScheduleWindowOrdering.MaximumPriority))
            {
                ScheduleWindowOrdering.AssignPriorities(ScheduleWindowOrdering.Order(settings.ScheduleWindows).ToList());
                repairs.Add("Schedule priorities were repaired to the range 0–1000, preserving their run order.");
            }
            repairs.AddRange(ValidateCanonicalSettings(settings, disableInvalidWindows: false));
            return repairs;
        }
        var warnings = MigrateToVersion2(settings);
        warnings.AddRange(ValidateCanonicalSettings(settings, disableInvalidWindows: true));
        settings.SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion;
        return warnings;
    }

    private static List<string> MigrateToVersion2(SonosSettings settings)
    {
        settings.Scenes ??= new();
        settings.ScheduleWindows ??= new();
        settings.DailySchedules ??= new();
        settings.HolidaySchedules ??= new();
        settings.ActiveDays ??= new();

        var warnings = new List<string>();
        var existingWindows = ScheduleWindowOrdering.Order(settings.ScheduleWindows).ToList();

        foreach (var scene in settings.Scenes)
        {
            scene.SpeakerIps ??= new();
            scene.Actions ??= new();
            if (scene.SourceType == SceneSourceType.None)
            {
                scene.SourceSelectionMode = SceneSourceSelectionMode.Resume;
            }
        }

        var activeDays = settings.ActiveDays.Distinct().OrderBy(ToMondayFirstIndex).ToList();
        var groupedProfiles = activeDays
            .Select(day => (Day: day, Profile: BuildProfile(settings, day)))
            .GroupBy(item => item.Profile)
            .OrderBy(group => group.Min(item => ToMondayFirstIndex(item.Day)))
            .ToList();

        var migratedBaselineWindows = new List<ScheduleWindow>();
        foreach (var group in groupedProfiles)
        {
            var days = group.Select(item => item.Day).OrderBy(ToMondayFirstIndex).ToList();
            var profile = group.Key;
            var signature = $"baseline|{string.Join(',', days)}|{profile}";
            var scene = BuildMigratedScene(
                settings,
                StableId("legacy-scene", signature),
                $"{FormatDays(days)} playback",
                profile);

            UpsertScene(settings.Scenes, scene);

            var window = new ScheduleWindow
            {
                Id = StableId("legacy-window", signature),
                Name = $"{FormatDays(days)} schedule",
                IsEnabled = true,
                StartTime = profile.StartTime,
                StopTime = profile.StopTime,
                RecurrenceType = ScheduleRecurrenceType.CustomDays,
                DaysOfWeek = days,
                SceneId = scene.Id,
                ExcludedDates = new(),
                LastModifiedUtc = DateTime.UtcNow
            };

            migratedBaselineWindows.Add(window);
        }

        var migratedHolidayWindows = new List<ScheduleWindow>();
        foreach (var holiday in settings.HolidaySchedules.OrderBy(item => item.Date))
        {
            foreach (var baseline in migratedBaselineWindows.Where(window => window.DaysOfWeek.Contains(holiday.Date.DayOfWeek)))
            {
                if (!baseline.ExcludedDates.Contains(holiday.Date))
                {
                    baseline.ExcludedDates.Add(holiday.Date);
                }
            }

            if (holiday.SkipPlayback || !HasPlaybackTarget(holiday))
            {
                continue;
            }

            var profile = BuildProfile(holiday);
            var signature = $"holiday|{holiday.Date:yyyy-MM-dd}|{profile}";
            var scene = BuildMigratedScene(
                settings,
                StableId("legacy-holiday-scene", signature),
                string.IsNullOrWhiteSpace(holiday.Name) ? $"{holiday.Date:dd/MM/yyyy} override" : holiday.Name.Trim(),
                profile);
            UpsertScene(settings.Scenes, scene);

            migratedHolidayWindows.Add(new ScheduleWindow
            {
                Id = StableId("legacy-holiday-window", signature),
                Name = string.IsNullOrWhiteSpace(holiday.Name) ? $"{holiday.Date:dd/MM/yyyy} override" : holiday.Name.Trim(),
                IsEnabled = true,
                StartTime = holiday.StartTime,
                StopTime = holiday.StopTime,
                RecurrenceType = ScheduleRecurrenceType.Daily,
                StartDate = holiday.Date,
                EndDate = holiday.Date,
                SceneId = scene.Id,
                LastModifiedUtc = DateTime.UtcNow
            });
        }

        var validSceneIds = settings.Scenes
            .Where(scene => scene.Enabled && !string.IsNullOrWhiteSpace(scene.Id))
            .Select(scene => scene.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < existingWindows.Count; index++)
        {
            var window = existingWindows[index];
            window.DaysOfWeek ??= new();
            window.ExcludedDates ??= new();
            window.AnnualExcludedDates ??= new();

            if (window.IsEnabled && (string.IsNullOrWhiteSpace(window.SceneId) || !validSceneIds.Contains(window.SceneId)))
            {
                window.IsEnabled = false;
                warnings.Add($"Schedule '{window.Name}' was disabled because its scene is missing.");
            }
        }

        settings.ScheduleWindows = migratedHolidayWindows
            .Concat(migratedBaselineWindows)
            .Concat(existingWindows)
            .ToList();
        ScheduleWindowOrdering.AssignPriorities(settings.ScheduleWindows);

        return warnings;
    }

    private static LegacyProfile BuildProfile(SonosSettings settings, DayOfWeek day)
    {
        if (settings.DailySchedules.TryGetValue(day, out var schedule) && schedule is not null)
        {
            return BuildProfile(schedule);
        }

        return new LegacyProfile(
            settings.StartTime,
            settings.StopTime,
            ResolveSource(
                settings.AutoPlayRandomStation,
                settings.AutoPlayRandomSpotify,
                settings.AutoPlayRandomYouTube,
                settings.AutoPlayRandomYouTubeMusic,
                settings.AutoPlayStationUrl,
                settings.AutoPlaySpotifyUrl,
                settings.AutoPlayYouTubeUrl,
                settings.AutoPlayYouTubeMusicUrl),
            true);
    }

    private static LegacyProfile BuildProfile(DaySchedule schedule)
        => new(
            schedule.StartTime,
            schedule.StopTime,
            ResolveSource(
                schedule.PlayRandomStation,
                schedule.PlayRandomSpotify,
                schedule.PlayRandomYouTube,
                schedule.PlayRandomYouTubeMusic,
                schedule.StationUrl,
                schedule.SpotifyUrl,
                schedule.YouTubeUrl,
                schedule.YouTubeMusicUrl),
            schedule.IsSyncedPlayback);

    private static LegacySource ResolveSource(
        bool randomStation,
        bool randomSpotify,
        bool randomYouTube,
        bool randomYouTubeMusic,
        string? stationUrl,
        string? spotifyUrl,
        string? youTubeUrl,
        string? youTubeMusicUrl)
    {
        if (randomSpotify) return new(SceneSourceSelectionMode.Random, SceneSourceType.Spotify, null);
        if (randomYouTubeMusic) return new(SceneSourceSelectionMode.Random, SceneSourceType.YouTubeMusic, null);
        if (randomYouTube) return new(SceneSourceSelectionMode.Random, SceneSourceType.YouTube, null);
        if (randomStation) return new(SceneSourceSelectionMode.Random, SceneSourceType.Station, null);
        if (!string.IsNullOrWhiteSpace(spotifyUrl)) return new(SceneSourceSelectionMode.Specific, SceneSourceType.Spotify, spotifyUrl.Trim());
        if (!string.IsNullOrWhiteSpace(youTubeUrl)) return new(SceneSourceSelectionMode.Specific, SceneSourceType.YouTube, youTubeUrl.Trim());
        if (!string.IsNullOrWhiteSpace(youTubeMusicUrl)) return new(SceneSourceSelectionMode.Specific, SceneSourceType.YouTubeMusic, youTubeMusicUrl.Trim());
        if (!string.IsNullOrWhiteSpace(stationUrl)) return new(SceneSourceSelectionMode.Specific, SceneSourceType.Station, stationUrl.Trim());
        return new(SceneSourceSelectionMode.Resume, SceneSourceType.None, null);
    }

    private static Scene BuildMigratedScene(SonosSettings settings, string id, string name, LegacyProfile profile)
        => new()
        {
            Id = id,
            Name = name,
            Description = "Migrated from the legacy time-based automation configuration.",
            Enabled = true,
            SourceSelectionMode = profile.Source.Mode,
            SourceType = profile.Source.Type,
            SourceUrl = profile.Source.Url,
            IsSyncedPlayback = profile.IsSyncedPlayback,
            SpeakerIps = settings.Speakers?.Select(speaker => speaker.IpAddress).Where(ip => !string.IsNullOrWhiteSpace(ip)).ToList() ?? new(),
            LastModifiedUtc = DateTime.UtcNow
        };

    private static void UpsertScene(List<Scene> scenes, Scene migrated)
    {
        var index = scenes.FindIndex(scene => string.Equals(scene.Id, migrated.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            scenes[index] = migrated;
        }
        else
        {
            scenes.Add(migrated);
        }
    }

    private static List<string> ValidateCanonicalSettings(SonosSettings settings, bool disableInvalidWindows)
    {
        settings.Scenes ??= new();
        settings.ScheduleWindows ??= new();
        var warnings = new List<string>();
        var sceneIds = settings.Scenes
            .Where(scene => scene.Enabled && !string.IsNullOrWhiteSpace(scene.Id))
            .Select(scene => scene.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var window in settings.ScheduleWindows)
        {
            window.DaysOfWeek ??= new();
            window.ExcludedDates ??= new();
            window.AnnualExcludedDates ??= new();
            if (!window.IsEnabled || (!string.IsNullOrWhiteSpace(window.SceneId) && sceneIds.Contains(window.SceneId)))
            {
                continue;
            }

            if (disableInvalidWindows)
            {
                window.IsEnabled = false;
            }

            warnings.Add($"Schedule '{window.Name}' is disabled at runtime because it has no valid scene.");
        }

        settings.NormalizeAutomationDateExceptions();
        return warnings.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool HasPlaybackTarget(DaySchedule schedule)
        => schedule.PlayRandomStation
           || schedule.PlayRandomSpotify
           || schedule.PlayRandomYouTubeMusic
           || schedule.PlayRandomYouTube
           || !string.IsNullOrWhiteSpace(schedule.StationUrl)
           || !string.IsNullOrWhiteSpace(schedule.SpotifyUrl)
           || !string.IsNullOrWhiteSpace(schedule.YouTubeUrl)
           || !string.IsNullOrWhiteSpace(schedule.YouTubeMusicUrl);

    private static int ToMondayFirstIndex(DayOfWeek day)
        => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    private static string FormatDays(IReadOnlyList<DayOfWeek> days)
    {
        if (days.Count == 5 && days.All(day => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday)) return "Weekday";
        if (days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday)) return "Weekend";
        return string.Join("/", days.Select(day => day.ToString()[..3]));
    }

    private static string StableId(string prefix, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}-{Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant()}";
    }

    private sealed record LegacySource(SceneSourceSelectionMode Mode, SceneSourceType Type, string? Url);
    private sealed record LegacyProfile(TimeOnly StartTime, TimeOnly StopTime, LegacySource Source, bool IsSyncedPlayback);
}
