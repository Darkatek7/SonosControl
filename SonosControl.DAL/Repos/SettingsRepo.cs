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
        private const string DirectoryPath = "./Data";
        private const string FilePath = "./Data/config.json";
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

        public SettingsRepo()
        {
            // Ensure directory exists
            if (!Directory.Exists(DirectoryPath))
            {
                Directory.CreateDirectory(DirectoryPath);
            }

            try
            {
                var fullPath = Path.GetFullPath(DirectoryPath);
                _watcher = new FileSystemWatcher(fullPath);
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
                if (!Directory.Exists(DirectoryPath))
                    Directory.CreateDirectory(DirectoryPath);

                string jsonString;
                try
                {
                    jsonString = JsonConvert.SerializeObject(settings, SerializerSettings);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("Failed to serialize settings.", ex);
                }

                var tempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempFile, jsonString);
                File.Move(tempFile, FilePath, true);

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
                        if (!File.Exists(FilePath))
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
                                jsonToUse = await File.ReadAllTextAsync(FilePath);
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
                settings.YouTubeMusicCollections ??= new();
                settings.DailySchedules ??= new();
                settings.ActiveDays ??= new();
                settings.HolidaySchedules ??= new();

                foreach (var key in settings.DailySchedules.Keys.ToList())
                {
                    settings.DailySchedules[key] ??= new DaySchedule();
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
