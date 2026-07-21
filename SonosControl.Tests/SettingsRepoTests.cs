using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests;

public class SettingsRepoTests
{
    [Fact]
    public async Task CreateVersionedBackup_UsesConfiguredDataDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            using var repo = new SettingsRepo(tempDir);
            await repo.WriteSettings(new SonosSettings { Volume = 37 });

            var fileName = await repo.CreateVersionedBackupAsync("pre-restore");

            Assert.NotNull(fileName);
            Assert.StartsWith("config-pre-restore-", fileName, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(tempDir, "backups", fileName!)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task WriteAndReadSettings_ReturnsPersistedValues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            using var repo = new SettingsRepo(Path.Combine(tempDir, "Data"));
            var settings = new SonosSettings { IP_Adress = "1.2.3.4", Volume = 42 };
            await repo.WriteSettings(settings);

            var result = await repo.GetSettings();

            Assert.NotNull(result);
            Assert.Equal("1.2.3.4", result!.IP_Adress);
            Assert.Equal(42, result.Volume);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GetSettings_EnsuresDailySchedulesValuesInitialized()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            var dataDir = Path.Combine(tempDir, "Data");
            using var repo = new SettingsRepo(dataDir);
            Directory.CreateDirectory(dataDir);
            var configPath = Path.Combine(dataDir, "config.json");
            // Create a config with a null value for a key in DailySchedules
            await File.WriteAllTextAsync(configPath, "{\"DailySchedules\": {\"Monday\": null}}");

            var result = await repo.GetSettings();

            Assert.NotNull(result);
            Assert.True(result!.DailySchedules.ContainsKey(DayOfWeek.Monday));
            Assert.NotNull(result.DailySchedules[DayOfWeek.Monday]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_DoNotCorruptSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            using var repo = new SettingsRepo(Path.Combine(tempDir, "Data"));
            var tasks = new List<Task>();

            for (int i = 0; i < 20; i++)
            {
                var index = i;
                tasks.Add(Task.Run(async () =>
                {
                    var settings = new SonosSettings { IP_Adress = $"10.0.0.{index}" };
                    await repo.WriteSettings(settings);
                }));

                tasks.Add(Task.Run(async () =>
                {
                    await repo.GetSettings();
                }));
            }

            await Task.WhenAll(tasks);

            var finalSettings = await repo.GetSettings();
            Assert.NotNull(finalSettings);
            Assert.Matches(@"10\.0\.0\.\d+", finalSettings!.IP_Adress);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GetSettings_ReturnsNewWhenFileMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            using var repo = new SettingsRepo(Path.Combine(tempDir, "Data"));
            var result = await repo.GetSettings();

            Assert.NotNull(result);
            Assert.Equal("10.0.0.0", result!.IP_Adress);
            Assert.Equal(10, result.Volume);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GetSettings_ReturnsNewWhenJsonCorrupted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            var dataDir = Path.Combine(tempDir, "Data");
            using var repo = new SettingsRepo(dataDir);
            Directory.CreateDirectory(dataDir);
            var configPath = Path.Combine(dataDir, "config.json");
            await File.WriteAllTextAsync(configPath, "{ invalid json }");

            var result = await repo.GetSettings();

            Assert.NotNull(result);
            Assert.Equal("10.0.0.0", result!.IP_Adress);
            Assert.Equal(10, result.Volume);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
