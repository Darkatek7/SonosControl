using Newtonsoft.Json;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.DAL.Repos;

public sealed class SettingsReadException : IOException
{
    public SettingsReadException(string message, Exception? inner = null) : base(message, inner) { }
}

public class SettingsRepo : ISettingsRepo, IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _directoryPath;
    private readonly string _filePath;

    public SettingsRepo(string? directoryPath = null)
    {
        _directoryPath = Path.GetFullPath(string.IsNullOrWhiteSpace(directoryPath) ? "./Data" : directoryPath);
        _filePath = Path.Combine(_directoryPath, "config.json");
        Directory.CreateDirectory(_directoryPath);
    }

    public async Task<SonosSettings?> GetSettings()
    {
        await Gate.WaitAsync();
        try { return SettingsChanges.Track(await ReadAsync(CancellationToken.None)); }
        finally { Gate.Release(); }
    }

    public async Task<SonosSettings> UpdateSettingsAsync(Action<SonosSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var settings = await ReadAsync(cancellationToken);
            var before = SettingsChanges.Serialize(settings);
            update(settings);
            Normalize(settings);
            ValidateAutoStopRules(settings);
            var after = SettingsChanges.Serialize(settings);
            if (before != after)
            {
                CreateVersionedBackupIfPresent(throwOnFailure: true);
                await PersistAsync(after, cancellationToken);
            }
            return SettingsChanges.Track(SettingsChanges.Deserialize(after));
        }
        finally { Gate.Release(); }
    }

    public async Task SaveChangesAsync(SonosSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changes = SettingsChanges.Capture(settings);
        await UpdateSettingsAsync(changes.Apply);
        SettingsChanges.Accept(settings, changes);
    }

    public async Task<string?> ReplaceSettingsAsync(SonosSettings settings, string label = "replacement", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var candidate = SettingsChanges.Deserialize(SettingsChanges.Serialize(settings));
        Normalize(candidate);
        ValidateAutoStopRules(candidate);
        var json = SettingsChanges.Serialize(candidate);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            // Preserve even an unreadable JSON document during an explicit restore.
            var backup = CreateVersionedBackupIfPresent(label, throwOnFailure: true);
            await PersistAsync(json, cancellationToken);
            return backup;
        }
        finally { Gate.Release(); }
    }

    public async Task<string?> CreateVersionedBackupAsync(string label, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try { return CreateVersionedBackupIfPresent(label, throwOnFailure: true); }
        finally { Gate.Release(); }
    }

    private async Task<SonosSettings> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            var settings = SettingsChanges.Deserialize(json);
            Normalize(settings);
            return settings;
        }
        catch (FileNotFoundException) { return new SonosSettings(); }
        catch (DirectoryNotFoundException) { return new SonosSettings(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new SettingsReadException("The settings file is invalid or cannot be read. Restore a valid backup before saving changes.", ex);
        }
    }

    private async Task PersistAsync(string json, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directoryPath);
        var temporaryPath = Path.Combine(_directoryPath, $".config-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            // Validate the complete staged document before the atomic replacement.
            SettingsChanges.Deserialize(await File.ReadAllTextAsync(temporaryPath, cancellationToken));
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Move(temporaryPath, _filePath, overwrite: true); break; }
                catch (Exception ex) when (attempt < maxAttempts && ex is IOException or UnauthorizedAccessException)
                { await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken); }
            }
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static void ValidateAutoStopRules(SonosSettings settings)
    {
        foreach (var speaker in settings.Speakers)
        {
            if (speaker.ManualAutoStopDays.Keys.Any(day => !Enum.IsDefined(day)))
                throw new InvalidOperationException("Auto-stop rules must use valid weekdays.");
            if (speaker.ManualAutoStopDays.Values.Any(rule => rule.Enabled) && speaker.ManualAutoStopMinutes is not > 0)
                throw new InvalidOperationException($"Set a positive manual playback limit for {speaker.Name} before enabling daily stop rules.");
        }
    }

    private static void Normalize(SonosSettings settings)
    {
        settings.MaxVolume = Math.Clamp(settings.MaxVolume, 0, 100);
        settings.Volume = Math.Clamp(settings.Volume, 0, settings.MaxVolume);
        settings.Speakers ??= new();
        foreach (var speaker in settings.Speakers)
        {
            if (speaker.StartupVolume.HasValue) speaker.StartupVolume = Math.Clamp(speaker.StartupVolume.Value, 0, 100);
            speaker.ManualAutoStopDays ??= new();
        }
        settings.Stations ??= new();
        settings.SpotifyTracks ??= new();
        settings.YouTubeCollections ??= new();
        settings.YouTubeMusicCollections ??= new();
        settings.DailySchedules ??= new();
        settings.ActiveDays ??= new();
        settings.HolidaySchedules ??= new();
        settings.Scenes ??= new();
        settings.ScheduleWindows ??= new();
        settings.AutomationRules ??= new();
        settings.QueueSnapshots ??= new();
        settings.DeviceHealthStatuses ??= new();
        settings.Jukebox ??= new();
        settings.JukeboxSuggestions ??= new();

        foreach (var suggestion in settings.JukeboxSuggestions)
        {
            suggestion.Votes ??= new();
        }

        foreach (var key in settings.DailySchedules.Keys)
        {
            settings.DailySchedules[key] ??= new DaySchedule();
        }

        foreach (var window in settings.ScheduleWindows)
        {
            window.DaysOfWeek ??= new();
            window.ExcludedDates ??= new();
            window.AnnualExcludedDates ??= new();
        }

        foreach (var scene in settings.Scenes)
        {
            scene.SpeakerIps ??= new();
            scene.Actions ??= new();
        }

        settings.NormalizeAutomationDateExceptions();
    }

    private string? CreateVersionedBackupIfPresent(string? label = null, bool throwOnFailure = false)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var backupDirectory = Path.Combine(_directoryPath, "backups");
            Directory.CreateDirectory(backupDirectory);

            var safeLabel = string.IsNullOrWhiteSpace(label)
                ? null
                : string.Concat(label.Trim().ToLowerInvariant().Select(character =>
                    char.IsLetterOrDigit(character) || character == '-' ? character : '-')).Trim('-');
            var labelSegment = string.IsNullOrWhiteSpace(safeLabel) ? string.Empty : $"{safeLabel}-";
            var backupFile = Path.Combine(
                backupDirectory,
                $"config-{labelSegment}{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            File.Copy(_filePath, backupFile, overwrite: false);

            var existingBackups = Directory.GetFiles(backupDirectory, "config-*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .ToList();

            foreach (var staleFile in existingBackups.Skip(30))
            {
                try
                {
                    staleFile.Delete();
                }
                catch
                {
                    // Keep write path resilient; stale backup cleanup is best effort.
                }
            }

            return Path.GetFileName(backupFile);
        }
        catch when (!throwOnFailure)
        {
            // Backup/versioning is best effort and must not block settings writes.
            return null;
        }
    }

    public void Dispose() { }
}
