using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;

namespace SonosControl.Testing;

public class SettingsRepoTests
{
    [Fact]
    public async Task ConcurrentReadsAndWrites_DoNotCorruptSettings()
    {
        var repo = new SettingsRepo();

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir);

        try
        {
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
            Directory.SetCurrentDirectory(originalDir);
            Directory.Delete(tempDir, true);
        }
    }
}

