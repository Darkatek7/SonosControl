using Newtonsoft.Json;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Models.Json;
using System.Threading;
using System.Linq;

namespace SonosControl.DAL.Repos
{
    public class SettingsRepo : ISettingsRepo
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
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<SonosSettings?> GetSettings()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (!File.Exists(FilePath))
                    return new();

                try
                {
                    var jsonString = await File.ReadAllTextAsync(FilePath);
                    var settings = JsonConvert.DeserializeObject<SonosSettings?>(jsonString, SerializerSettings);

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
                catch (IOException)
                {
                    return new();
                }
                catch (JsonException)
                {
                    return new();
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}

