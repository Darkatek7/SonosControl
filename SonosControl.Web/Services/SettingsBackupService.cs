using System.Text.Json;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public sealed record SettingsBackupInfo(string FileName, DateTime CreatedUtc, long Bytes);

public sealed record SettingsRestoreResult(string FileName, string? SafetyBackup);

public sealed class SettingsBackupException : Exception
{
    public SettingsBackupException(string message, int statusCode = StatusCodes.Status400BadRequest, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public interface ISettingsBackupService
{
    const long MaxImportBytes = 5 * 1024 * 1024;

    Task<IReadOnlyList<SettingsBackupInfo>> ListAsync(CancellationToken cancellationToken = default);
    Task<SettingsBackupInfo?> CreateAsync(CancellationToken cancellationToken = default);
    Task<(Stream Stream, string FileName)?> OpenReadAsync(string fileName, CancellationToken cancellationToken = default);
    Task<SettingsRestoreResult> RestoreAsync(string fileName, CancellationToken cancellationToken = default);
    Task<SettingsRestoreResult> ImportAsync(string fileName, Stream content, long? declaredLength, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string fileName, CancellationToken cancellationToken = default);
}

public sealed class SettingsBackupService : ISettingsBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISettingsRepo _settingsRepo;
    private readonly ISettingsSchemaMigrationService _migrationService;
    private readonly ActionLogger _actionLogger;
    private readonly ILogger<SettingsBackupService> _logger;
    private readonly string _backupDirectory;

    public SettingsBackupService(
        ISettingsRepo settingsRepo,
        ISettingsSchemaMigrationService migrationService,
        ActionLogger actionLogger,
        ILogger<SettingsBackupService> logger,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _settingsRepo = settingsRepo;
        _migrationService = migrationService;
        _actionLogger = actionLogger;
        _logger = logger;
        var configuredDataDirectory = configuration["Settings:DataDirectory"];
        var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? Path.Combine(environment.ContentRootPath, "Data")
            : Path.GetFullPath(configuredDataDirectory);
        _backupDirectory = Path.Combine(dataDirectory, "backups");
    }

    public Task<IReadOnlyList<SettingsBackupInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_backupDirectory);
        IReadOnlyList<SettingsBackupInfo> backups = Directory
            .EnumerateFiles(_backupDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(ToInfo)
            .ToList();
        return Task.FromResult(backups);
    }

    public async Task<SettingsBackupInfo?> CreateAsync(CancellationToken cancellationToken = default)
    {
        var fileName = await _settingsRepo.CreateVersionedBackupAsync("manual", cancellationToken);
        if (fileName is null)
        {
            return null;
        }

        var info = GetExistingFile(fileName);
        await _actionLogger.LogAsync("ConfigBackupCreated", fileName);
        return ToInfo(info);
    }

    public Task<(Stream Stream, string FileName)?> OpenReadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(fileName);
        if (!File.Exists(path))
        {
            return Task.FromResult<(Stream, string)?>(null);
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<(Stream, string)?>((stream, Path.GetFileName(path)));
    }

    public async Task<SettingsRestoreResult> RestoreAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var file = GetExistingFile(fileName);
        if (file.Length > ISettingsBackupService.MaxImportBytes)
        {
            throw TooLarge();
        }

        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await ReadAndValidateAsync(stream, file.Length, cancellationToken);
        return await ApplyAsync(settings, file.Name, "pre-restore", "ConfigBackupRestored", cancellationToken);
    }

    public async Task<SettingsRestoreResult> ImportAsync(
        string fileName,
        Stream content,
        long? declaredLength,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new SettingsBackupException("Only .json files are supported.");
        }

        if (declaredLength is > ISettingsBackupService.MaxImportBytes)
        {
            throw TooLarge();
        }

        var settings = await ReadAndValidateAsync(content, declaredLength, cancellationToken);
        return await ApplyAsync(settings, Path.GetFileName(fileName), "pre-import", "ConfigImported", cancellationToken);
    }

    public async Task<bool> DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        await _actionLogger.LogAsync("ConfigBackupDeleted", Path.GetFileName(path));
        return true;
    }

    private async Task<SettingsRestoreResult> ApplyAsync(
        SonosSettings importedSettings,
        string sourceName,
        string backupLabel,
        string actionName,
        CancellationToken cancellationToken)
    {
        var previousSettings = await _settingsRepo.GetSettings();
        var safetyBackup = await _settingsRepo.CreateVersionedBackupAsync(backupLabel, cancellationToken);

        try
        {
            await _settingsRepo.WriteSettings(importedSettings);
            var migration = await _migrationService.MigrateIfRequiredAsync(cancellationToken);
            if (!migration.Success)
            {
                throw new SettingsBackupException("The settings document could not be migrated safely.");
            }

            await _actionLogger.LogAsync(actionName, $"{sourceName}; safety backup: {safetyBackup ?? "not required"}");
            _logger.LogInformation("Settings restored from {SourceName}; safety backup {SafetyBackup}.", sourceName, safetyBackup);
            return new SettingsRestoreResult(sourceName, safetyBackup);
        }
        catch (Exception ex)
        {
            if (previousSettings is not null)
            {
                try
                {
                    await _settingsRepo.WriteSettings(previousSettings);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(rollbackException, "Settings rollback failed after import of {SourceName}.", sourceName);
                }
            }

            if (ex is SettingsBackupException)
            {
                throw;
            }

            throw new SettingsBackupException("The settings document could not be applied safely.", innerException: ex);
        }
    }

    private static async Task<SonosSettings> ReadAndValidateAsync(Stream content, long? declaredLength, CancellationToken cancellationToken)
    {
        if (declaredLength is <= 0)
        {
            throw new SettingsBackupException("A non-empty JSON file is required.");
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > ISettingsBackupService.MaxImportBytes)
            {
                throw TooLarge();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        if (buffer.Length == 0)
        {
            throw new SettingsBackupException("A non-empty JSON file is required.");
        }

        buffer.Position = 0;
        SonosSettings? settings;
        try
        {
            settings = await JsonSerializer.DeserializeAsync<SonosSettings>(buffer, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new SettingsBackupException("The uploaded file is not a valid Sonos settings JSON.", innerException: ex);
        }

        if (settings is null)
        {
            throw new SettingsBackupException("The uploaded file is not a valid Sonos settings JSON.");
        }

        NormalizeAndValidate(settings);
        return settings;
    }

    private static void NormalizeAndValidate(SonosSettings settings)
    {
        if (settings.SettingsSchemaVersion < 0 || settings.SettingsSchemaVersion > SonosSettings.CurrentSettingsSchemaVersion)
        {
            throw new SettingsBackupException($"Settings schema version {settings.SettingsSchemaVersion} is not supported.");
        }

        settings.Speakers ??= new();
        settings.Stations ??= new();
        settings.SpotifyTracks ??= new();
        settings.YouTubeCollections ??= new();
        settings.YouTubeMusicCollections ??= new();
        settings.DailySchedules ??= new();
        settings.HolidaySchedules ??= new();
        settings.Scenes ??= new();
        settings.ScheduleWindows ??= new();
        settings.AutomationRules ??= new();
        settings.QueueSnapshots ??= new();
        settings.DeviceHealthStatuses ??= new();
        settings.Jukebox ??= new JukeboxSettings();
        settings.JukeboxSuggestions ??= new();
        settings.ActiveDays ??= new();
        settings.MaxVolume = Math.Clamp(settings.MaxVolume, 0, 100);
        settings.Volume = Math.Clamp(settings.Volume, 0, settings.MaxVolume);

        if (settings.Speakers.Any(speaker => string.IsNullOrWhiteSpace(speaker.Name) || string.IsNullOrWhiteSpace(speaker.IpAddress)))
        {
            throw new SettingsBackupException("Every configured speaker must have a name and IP address.");
        }

        var duplicateSpeaker = settings.Speakers
            .GroupBy(speaker => speaker.IpAddress.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSpeaker is not null)
        {
            throw new SettingsBackupException($"Speaker IP address '{duplicateSpeaker.Key}' is configured more than once.");
        }
    }

    private FileInfo GetExistingFile(string fileName)
    {
        var path = ResolvePath(fileName);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new SettingsBackupException("The selected backup was not found.", StatusCodes.Status404NotFound);
        }

        return info;
    }

    private string ResolvePath(string fileName)
    {
        Directory.CreateDirectory(_backupDirectory);
        var sanitized = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitized) || !string.Equals(sanitized, fileName, StringComparison.Ordinal))
        {
            throw new SettingsBackupException("The backup file name is invalid.");
        }

        return Path.Combine(_backupDirectory, sanitized);
    }

    private static SettingsBackupInfo ToInfo(FileInfo file)
        => new(file.Name, file.CreationTimeUtc, file.Length);

    private static SettingsBackupException TooLarge()
        => new("Settings files may not exceed 5 MB.", StatusCodes.Status413PayloadTooLarge);
}
