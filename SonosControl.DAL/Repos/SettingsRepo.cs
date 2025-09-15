using Newtonsoft.Json;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using System.Threading;

namespace SonosControl.DAL.Repos
{
    public class SettingsRepo : ISettingsRepo
    {
        private const string DirectoryPath = "./Data";
        private const string FilePath = "./Data/config.json";
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

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
                    jsonString = JsonConvert.SerializeObject(settings, Formatting.Indented);
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
                    var settings = JsonConvert.DeserializeObject<SonosSettings?>(jsonString, new JsonSerializerSettings
                    {
                        ObjectCreationHandling = ObjectCreationHandling.Replace
                    });

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

