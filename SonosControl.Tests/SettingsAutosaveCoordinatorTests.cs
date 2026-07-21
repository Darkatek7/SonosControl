using Microsoft.Extensions.Logging.Abstractions;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SettingsAutosaveCoordinatorTests
{
    [Fact]
    public async Task QueueSaveAsync_DebouncesRapidEdits_AndPersistsOnlyFinalSnapshot()
    {
        var repository = new RecordingSettingsRepo();
        using var coordinator = new SettingsAutosaveCoordinator(
            repository,
            NullLogger<SettingsAutosaveCoordinator>.Instance);
        var settings = new SonosSettings { Volume = 10 };

        var first = coordinator.QueueSaveAsync(settings);
        settings.Volume = 20;
        var second = coordinator.QueueSaveAsync(settings);
        settings.Volume = 35;
        var third = coordinator.QueueSaveAsync(settings);

        await Task.WhenAll(first, second, third);

        Assert.Single(repository.Writes);
        Assert.Equal(35, repository.Writes[0].Volume);
        Assert.Equal(SettingsSaveState.Saved, coordinator.State);
    }

    [Fact]
    public async Task RetryAsync_AfterWriteError_PersistsFailedSnapshot()
    {
        var repository = new RecordingSettingsRepo { FailNextWrite = true };
        using var coordinator = new SettingsAutosaveCoordinator(
            repository,
            NullLogger<SettingsAutosaveCoordinator>.Instance);
        var settings = new SonosSettings { Volume = 42 };

        await coordinator.QueueSaveAsync(settings, immediate: true);

        Assert.Equal(SettingsSaveState.Error, coordinator.State);
        Assert.Contains("simulated", coordinator.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await coordinator.RetryAsync();

        Assert.Equal(SettingsSaveState.Saved, coordinator.State);
        Assert.Single(repository.Writes);
        Assert.Equal(42, repository.Writes[0].Volume);
    }

    private sealed class RecordingSettingsRepo : ISettingsRepo
    {
        public bool FailNextWrite { get; set; }
        public List<SonosSettings> Writes { get; } = new();

        public Task<SonosSettings?> GetSettings() => Task.FromResult<SonosSettings?>(Writes.LastOrDefault());

        public Task<string?> CreateVersionedBackupAsync(string label, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task WriteSettings(SonosSettings? settings)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Simulated settings failure.");
            }

            Writes.Add(settings ?? throw new ArgumentNullException(nameof(settings)));
            return Task.CompletedTask;
        }
    }
}
