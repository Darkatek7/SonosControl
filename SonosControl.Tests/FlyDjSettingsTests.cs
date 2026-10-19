using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests;

public class FlyDjSettingsTests
{
    private static async Task WithRepoAsync(Func<SettingsRepo, string, Task> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            using var repo = new SettingsRepo(directory);
            await body(repo, directory);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DefaultsMatchTheDocumentedValues()
    {
        var settings = new FlyDjSettings();

        Assert.Empty(settings.TargetSpeakerIps);
        Assert.Empty(settings.CandidateStationUrls);
        Assert.Equal(20, settings.Volume);
        Assert.Equal(8, settings.MinDwellMinutes);
        Assert.Equal(45, settings.MaxDwellMinutes);
        Assert.Equal(60, settings.YieldCooldownMinutes);
        Assert.Equal(30, settings.EngagementCheckSeconds);
        Assert.False(settings.AutoResume);
        Assert.False(settings.WasRunning);
        Assert.NotNull(new SonosSettings().FlyDj);
    }

    [Fact]
    public async Task UpdatesAreClampedAndCleanedWhenPersisted()
    {
        await WithRepoAsync(async (repo, _) =>
        {
            var saved = await repo.UpdateSettingsAsync(settings =>
            {
                settings.FlyDj.Volume = 250;
                settings.FlyDj.MinDwellMinutes = 0;
                settings.FlyDj.MaxDwellMinutes = 2000;
                settings.FlyDj.YieldCooldownMinutes = -5;
                settings.FlyDj.EngagementCheckSeconds = 0;
                settings.FlyDj.TargetSpeakerIps = [" 10.0.0.1 ", "10.0.0.1", "", "10.0.0.2"];
                settings.FlyDj.CandidateStationUrls = ["a.example/live", "A.EXAMPLE/live", " "];
            });

            Assert.Equal(100, saved.FlyDj.Volume);
            Assert.Equal(1, saved.FlyDj.MinDwellMinutes);
            Assert.Equal(600, saved.FlyDj.MaxDwellMinutes);
            Assert.Equal(0, saved.FlyDj.YieldCooldownMinutes);
            Assert.True(saved.FlyDj.EngagementCheckSeconds >= 5);
            Assert.Equal(["10.0.0.1", "10.0.0.2"], saved.FlyDj.TargetSpeakerIps);
            Assert.Equal(["a.example/live"], saved.FlyDj.CandidateStationUrls);
        });
    }

    [Fact]
    public async Task MaxDwellNeverFallsBelowMinDwell()
    {
        await WithRepoAsync(async (repo, _) =>
        {
            var saved = await repo.UpdateSettingsAsync(settings =>
            {
                settings.FlyDj.MinDwellMinutes = 30;
                settings.FlyDj.MaxDwellMinutes = 10;
            });

            Assert.Equal(30, saved.FlyDj.MinDwellMinutes);
            Assert.Equal(30, saved.FlyDj.MaxDwellMinutes);
        });
    }

    [Fact]
    public async Task LegacyDocumentsWithoutTheSectionOrWithNullListsLoadWithDefaults()
    {
        await WithRepoAsync(async (repo, directory) =>
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"),
                """{"Volume": 12, "FlyDj": {"TargetSpeakerIps": null, "CandidateStationUrls": null, "Volume": 33}}""");
            var withNulls = await repo.GetSettings();
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), """{"Volume": 12, "FlyDj": null}""");
            var withNullSection = await repo.GetSettings();
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), """{"Volume": 12}""");
            var withoutSection = await repo.GetSettings();

            Assert.Equal(33, withNulls!.FlyDj.Volume);
            Assert.Empty(withNulls.FlyDj.TargetSpeakerIps);
            Assert.Empty(withNulls.FlyDj.CandidateStationUrls);
            Assert.Equal(20, withNullSection!.FlyDj.Volume);
            Assert.Equal(20, withoutSection!.FlyDj.Volume);
        });
    }

    [Fact]
    public async Task BackgroundWritesOnlyTouchTheirOwnFlag()
    {
        await WithRepoAsync(async (repo, _) =>
        {
            await repo.UpdateSettingsAsync(settings => { settings.Volume = 44; settings.FlyDj.TargetSpeakerIps = ["10.0.0.5"]; });
            await repo.UpdateSettingsAsync(settings => settings.FlyDj.WasRunning = true);
            var reloaded = await repo.GetSettings();

            Assert.True(reloaded!.FlyDj.WasRunning);
            Assert.Equal(["10.0.0.5"], reloaded.FlyDj.TargetSpeakerIps);
            Assert.Equal(44, reloaded.Volume);
        });
    }

    [Fact]
    public async Task EditorStyleChangeSetsMergeTheFlyDjSection()
    {
        await WithRepoAsync(async (repo, _) =>
        {
            var editor = (await repo.GetSettings())!;
            var staleEditor = (await repo.GetSettings())!;
            editor.FlyDj.Volume = 35;
            await repo.SaveChangesAsync(editor);
            await repo.UpdateSettingsAsync(settings => settings.FlyDj.WasRunning = true);
            staleEditor.Volume = 17;
            await repo.SaveChangesAsync(staleEditor);

            var reloaded = (await repo.GetSettings())!;
            Assert.Equal(35, reloaded.FlyDj.Volume);
            Assert.True(reloaded.FlyDj.WasRunning);
            Assert.Equal(17, reloaded.Volume);
        });
    }
}
