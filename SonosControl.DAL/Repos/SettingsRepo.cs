using Newtonsoft.Json;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Models.Json;
using System.Threading;
using System.Linq;

namespace SonosControl.DAL.Repos
{
    public class SettingsRepo : ISettingsRepo, IDisposable
    {
        private static readonly SemaphoreSlim _semaphore = new(1, 1);
        private static readonly JsonSerializerSettings SerializerSettings = new()
        {
            Formatting = Formatting.Indented,
            Converters = { new DateOnlyJsonConverter() },
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        private volatile string? _cachedJson;
        private FileSystemWatcher? _watcher;
        private readonly bool _cachingEnabled = false;
        private readonly string _directoryPath;
        private readonly string _filePath;

        public SettingsRepo(string? directoryPath = null)
        {
            _directoryPath = Path.GetFullPath(
                string.IsNullOrWhiteSpace(directoryPath) ? "./Data" : directoryPath);
            _filePath = Path.Combine(_directoryPath, "config.json");

            // Ensure directory exists
            if (!Directory.Exists(_directoryPath))
            {
                Directory.CreateDirectory(_directoryPath);
            }

            try
            {
                _watcher = new FileSystemWatcher(_directoryPath);
                _watcher.Filter = "config.json";
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                _watcher.Changed += OnChanged;
                _watcher.Created += OnChanged;
                _watcher.Renamed += OnRenamed;
                _watcher.EnableRaisingEvents = true;
                _cachingEnabled = true;
            }
            catch (Exception)
            {
                // If watcher fails to initialize (e.g. permission issues), we disable caching
                // to fall back to polling behavior (reading from disk every time).
                _cachingEnabled = false;
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e) => InvalidateCache();
        private void OnRenamed(object sender, RenamedEventArgs e) => InvalidateCache();

        private void InvalidateCache()
        {
            _cachedJson = null;
        }

        public async Task WriteSettings(SonosSettings? settings)
        {
            await _semaphore.WaitAsync();
            try
            {
                if (!Directory.Exists(_directoryPath))
                    Directory.CreateDirectory(_directoryPath);

                string jsonString;
                try
                {
                    jsonString = JsonConvert.SerializeObject(settings, SerializerSettings);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("Failed to serialize settings.", ex);
                }

                CreateVersionedBackupIfPresent();

                var tempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempFile, jsonString);
                await ReplaceFileWithRetryAsync(tempFile, _filePath);

                // Update cache only if caching is enabled
                if (_cachingEnabled)
                {
                    _cachedJson = jsonString;
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string?> CreateVersionedBackupAsync(
            string label,
            CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                return CreateVersionedBackupIfPresent(label, throwOnFailure: true);
            }
            finally
            {
                _semaphore.Release();
            }
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
                    $"config-{labelSegment}{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
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

        private static async Task ReplaceFileWithRetryAsync(string sourcePath, string destinationPath)
        {
            const int maxAttempts = 5;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    File.Move(sourcePath, destinationPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts && (ex is IOException || ex is UnauthorizedAccessException))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt));
                }
            }

            // Last attempt without swallowing exceptions.
            File.Move(sourcePath, destinationPath, overwrite: true);
        }

        public async Task<SonosSettings?> GetSettings()
        {
            string jsonToUse;

            // Try to use cache first (only if enabled)
            if (_cachingEnabled && _cachedJson != null)
            {
                jsonToUse = _cachedJson;
            }
            else
            {
                await _semaphore.WaitAsync();
                try
                {
                    // Double check
                    if (_cachingEnabled && _cachedJson != null)
                    {
                        jsonToUse = _cachedJson;
                    }
                    else
                    {
                        bool success = false;
                        if (!File.Exists(_filePath))
                        {
                            jsonToUse = "{}";
                            // If file doesn't exist, we can cache the empty state.
                            // If file is created later, watcher will fire.
                            success = true;
                        }
                        else
                        {
                            try
                            {
                                jsonToUse = await File.ReadAllTextAsync(_filePath);
                                success = true;
                            }
                            catch (IOException)
                            {
                                // Temporary failure (locked file). Do not cache.
                                jsonToUse = "{}";
                                success = false;
                            }
                        }

                        if (_cachingEnabled && success)
                        {
                            _cachedJson = jsonToUse;
                        }
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }

            try
            {
                var settings = JsonConvert.DeserializeObject<SonosSettings?>(jsonToUse, SerializerSettings);

                if (settings == null)
                    return new();

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
                }

                foreach (var scene in settings.Scenes)
                {
                    scene.SpeakerIps ??= new();
                    scene.Actions ??= new();
                }

                return settings;
            }
            catch (JsonException)
            {
                return new();
            }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
        }
    }
}
