using Microsoft.Extensions.Logging.Abstractions;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class ReliableSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"settings-regression-{Guid.NewGuid():N}");
    private SettingsRepo Open() => new(_directory);
    [Fact]
    public async Task StartupVolumesAreNormalizedBeforePersistence()
    {
        using var repository = Open();
        await repository.UpdateSettingsAsync(settings => settings.Speakers =
        [
            new() { IpAddress = "A", StartupVolume = 456051 },
            new() { IpAddress = "B", StartupVolume = -12 },
            new() { IpAddress = "C", StartupVolume = null }
        ]);
        var settings = (await repository.GetSettings())!;
        Assert.Equal(100, settings.Speakers[0].StartupVolume);
        Assert.Equal(0, settings.Speakers[1].StartupVolume);
        Assert.Null(settings.Speakers[2].StartupVolume);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public async Task StalePlayerAndFormEdits_PreserveNewScenesAndIndependentFields()
    {
        using var repository = Open();
        var player = (await repository.GetSettings())!;
        var form = (await repository.GetSettings())!;
        await repository.UpdateSettingsAsync(s => s.Scenes.Add(new Scene { Id = "new", Name = "New scene" }));
        player.Volume = 35;
        form.DiscordWebhookUrl = "https://example.invalid/webhook";
        await Task.WhenAll(repository.SaveChangesAsync(player), repository.SaveChangesAsync(form));
        var persisted = (await repository.GetSettings())!;
        Assert.Equal(35, persisted.Volume);
        Assert.Equal(form.DiscordWebhookUrl, persisted.DiscordWebhookUrl);
        Assert.Equal("new", Assert.Single(persisted.Scenes).Id);
    }

    [Fact]
    public async Task ConcurrentVotesAndMutations_KeepAllUpdatesAndIndependentSnapshots()
    {
        using var repository = Open();
        await repository.UpdateSettingsAsync(s => s.JukeboxSuggestions.Add(new JukeboxSuggestion { Id = "song" }));
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repository.GetSettings()));
        for (var i = 0; i < snapshots.Length; i++) snapshots[i]!.JukeboxSuggestions[0].Votes.Add(new JukeboxVote { UserName = $"user-{i}" });
        await Task.WhenAll(snapshots.Select(s => repository.SaveChangesAsync(s)));
        var persisted = (await repository.GetSettings())!;
        Assert.Equal(8, persisted.JukeboxSuggestions[0].Votes.Count);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repository.UpdateSettingsAsync(s => s.Volume++)));
        Assert.Equal(22, (await repository.GetSettings())!.Volume);
        results[0].Scenes.Add(new Scene());
        Assert.Empty((await repository.GetSettings())!.Scenes);
    }

    [Fact]
    public async Task LastSavedFieldWins_AndNoOpDoesNotCreateABackup()
    {
        using var repository = Open();
        await repository.UpdateSettingsAsync(s => s.Volume = 20);
        var first = (await repository.GetSettings())!;
        var second = (await repository.GetSettings())!;
        first.Volume = 40;
        second.Volume = 60;
        await repository.SaveChangesAsync(first);
        await repository.SaveChangesAsync(second);
        Assert.Equal(60, (await repository.GetSettings())!.Volume);
        var backups = Directory.GetFiles(Path.Combine(_directory, "backups")).Length;
        await repository.UpdateSettingsAsync(_ => { });
        Assert.Equal(backups, Directory.GetFiles(Path.Combine(_directory, "backups")).Length);
    }

    [Fact]
    public async Task DeletedEntity_CannotBeResurrectedByAnOldEditor()
    {
        using var repository = Open();
        await repository.UpdateSettingsAsync(s => s.Scenes.Add(new Scene { Id = "scene" }));
        var editor = (await repository.GetSettings())!;
        await repository.UpdateSettingsAsync(s => s.Scenes.Clear());
        editor.Scenes[0].Name = "Old edit";
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveChangesAsync(editor));
        Assert.Empty((await repository.GetSettings())!.Scenes);
    }

    [Fact]
    public async Task UrlEditPreservesAConcurrentNameChange_AndCannotRestoreADeletedStation()
    {
        using var repository = Open();
        var editor = (await repository.GetSettings())!;
        var originalUrl = editor.Stations[0].Url;
        editor.Stations[0].Url = "example.com/new-stream";
        await repository.UpdateSettingsAsync(s => s.Stations[0].Name = "Updated name");
        await repository.SaveChangesAsync(editor);
        var latest = (await repository.GetSettings())!;
        Assert.Equal("Updated name", latest.Stations[0].Name);
        Assert.Equal("example.com/new-stream", latest.Stations[0].Url);
        latest.Stations[0].Url = originalUrl;
        await repository.UpdateSettingsAsync(s => s.Stations.RemoveAt(0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveChangesAsync(latest));
        Assert.DoesNotContain((await repository.GetSettings())!.Stations, station => station.Url == originalUrl);
    }

    [Fact]
    public async Task CorruptConfiguration_BlocksMigrationAndOrdinarySaves_ButCanBeRestored()
    {
        using var repository = Open();
        var oldEditor = (await repository.GetSettings())!;
        var path = Path.Combine(_directory, "config.json");
        await File.WriteAllTextAsync(path, "{broken");
        oldEditor.Volume = 42;
        await Assert.ThrowsAsync<SettingsReadException>(() => repository.SaveChangesAsync(oldEditor));
        var migration = new SettingsSchemaMigrationService(repository, new AutomationRuntimeStatus(), NullLogger<SettingsSchemaMigrationService>.Instance);
        Assert.False((await migration.MigrateIfRequiredAsync()).Success);
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        var backup = await repository.ReplaceSettingsAsync(new SonosSettings { Volume = 23 }, "pre-restore");
        Assert.Equal("{broken", await File.ReadAllTextAsync(Path.Combine(_directory, "backups", backup!)));
        Assert.Equal(23, (await repository.GetSettings())!.Volume);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task UnreadableConfigurationAndCancelledReplacement_LeaveActiveDataUntouched()
    {
        using var repository = Open();
        var path = Path.Combine(_directory, "config.json");
        Directory.CreateDirectory(path);
        await Assert.ThrowsAsync<SettingsReadException>(() => repository.GetSettings());
        Directory.Delete(path);
        await repository.ReplaceSettingsAsync(new SonosSettings { Volume = 15 });
        var original = await File.ReadAllTextAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReplaceSettingsAsync(new SonosSettings { Volume = 99 }, cancellationToken: cancellation.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("unreadable")]
    public async Task HealthReflectsTheSettingsReadFailure(string state)
    {
        using var repository = Open();
        var path = Path.Combine(_directory, "config.json");
        if (state == "corrupt") await File.WriteAllTextAsync(path, "{broken");
        if (state == "unreadable") Directory.CreateDirectory(path);
        var check = new SonosControl.Web.Services.HealthChecks.SettingsHealthCheck(repository);
        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(state == "missing" ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, result.Status);
        if (state == "missing") Assert.False(File.Exists(path));
        if (state == "corrupt") Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FailedAtomicReplacement_RemovesItsTemporaryFile()
    {
        using var repository = Open();
        Directory.CreateDirectory(Path.Combine(_directory, "config.json"));
        await Assert.ThrowsAsync<IOException>(() => repository.ReplaceSettingsAsync(new SonosSettings { Volume = 41 }));
        Assert.True(Directory.Exists(Path.Combine(_directory, "config.json")));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Autosave_RetryKeepsUnrelatedChangesAndOnlyPersistsCapturedEdits()
    {
        using var repository = Open();
        var form = (await repository.GetSettings())!;
        using var autosave = new SettingsAutosaveCoordinator(repository, NullLogger<SettingsAutosaveCoordinator>.Instance);
        form.MaxVolume = 70;
        await File.WriteAllTextAsync(Path.Combine(_directory, "config.json"), "{broken");
        await autosave.QueueSaveAsync(form, immediate: true);
        Assert.Equal(SettingsSaveState.Error, autosave.State);
        await repository.ReplaceSettingsAsync(new SonosSettings { Volume = 19, Scenes = [new Scene { Id = "new" }] });
        await autosave.RetryAsync();
        var saved = (await repository.GetSettings())!;
        Assert.Equal(70, saved.MaxVolume);
        Assert.Equal(19, saved.Volume);
        Assert.Single(saved.Scenes);
        Assert.Equal(SettingsSaveState.Saved, autosave.State);
    }
}
