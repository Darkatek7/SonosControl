using Newtonsoft.Json;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using System;
using System.Linq;
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

                    if (settings == null)
                        return new();

                    settings.Stations ??= new();
                    settings.SpotifyTracks ??= new();
                    settings.YouTubeMusicCollections ??= new();
                    settings.DailySchedules ??= new();
                    settings.ActiveDays ??= new();
                    settings.Groups ??= new();

                    foreach (var key in settings.DailySchedules.Keys.ToList())
                    {
                        settings.DailySchedules[key] ??= new DaySchedule();
                    }

                    foreach (var group in settings.Groups)
                    {
                        if (string.IsNullOrWhiteSpace(group.Id))
                            group.Id = Guid.NewGuid().ToString("N");

                        group.Name = (group.Name ?? string.Empty).Trim();
                        group.CoordinatorIp = (group.CoordinatorIp ?? string.Empty).Trim();

                        group.MemberIps ??= new();
                        group.MemberIps = group.MemberIps
                            .Select(ip => ip?.Trim())
                            .Where(ip => !string.IsNullOrWhiteSpace(ip))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (!string.IsNullOrWhiteSpace(group.CoordinatorIp) &&
                            !group.MemberIps.Any(ip => string.Equals(ip, group.CoordinatorIp, StringComparison.OrdinalIgnoreCase)))
                        {
                            group.MemberIps.Insert(0, group.CoordinatorIp);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(settings.ActiveGroupId))
                        settings.ActiveGroupId = settings.ActiveGroupId.Trim();

                    if (!string.IsNullOrWhiteSpace(settings.ActiveGroupId) &&
                        settings.Groups.All(g => !string.Equals(g.Id, settings.ActiveGroupId, StringComparison.OrdinalIgnoreCase)))
                    {
                        settings.ActiveGroupId = null;
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

