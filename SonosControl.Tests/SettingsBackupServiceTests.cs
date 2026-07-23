using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SettingsBackupServiceTests
{
    [Fact]
    public async Task ImportAsync_RejectsDeclaredPayloadAboveFiveMegabytes()
    {
        using var fixture = new BackupFixture();

        var exception = await Assert.ThrowsAsync<SettingsBackupException>(() =>
            fixture.Service.ImportAsync(
                "config.json",
                Stream.Null,
                ISettingsBackupService.MaxImportBytes + 1));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, exception.StatusCode);
        fixture.SettingsRepo.Verify(
            repository => repository.WriteSettings(It.IsAny<SonosSettings?>()),
            Times.Never);
    }

    [Fact]
    public async Task ImportAsync_RejectsInvalidJsonBeforeWriting()
    {
        using var fixture = new BackupFixture();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("{not-json"));

        var exception = await Assert.ThrowsAsync<SettingsBackupException>(() =>
            fixture.Service.ImportAsync("config.json", content, content.Length));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Contains("valid Sonos settings JSON", exception.Message);
        fixture.SettingsRepo.Verify(
            repository => repository.WriteSettings(It.IsAny<SonosSettings?>()),
            Times.Never);
    }

    [Fact]
    public async Task ImportAsync_RejectsUnsupportedFutureSchema()
    {
        using var fixture = new BackupFixture();
        await using var content = new MemoryStream(
            Encoding.UTF8.GetBytes("{\"SettingsSchemaVersion\":999}"));

        var exception = await Assert.ThrowsAsync<SettingsBackupException>(() =>
            fixture.Service.ImportAsync("config.json", content, content.Length));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Contains("not supported", exception.Message);
    }

    [Fact]
    public async Task ImportAsync_CreatesSafetyBackupAndAppliesValidatedSettings()
    {
        using var fixture = new BackupFixture();
        var imported = new SonosSettings
        {
            SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion,
            MaxVolume = 64,
            Volume = 22,
            Speakers =
            [
                new SonosSpeaker { Name = "Office", IpAddress = "192.0.2.10" }
            ]
        };
        var json = JsonSerializer.Serialize(imported);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await fixture.Service.ImportAsync("import.json", content, content.Length);

        Assert.Equal("import.json", result.FileName);
        Assert.Equal("config-pre-import.json", result.SafetyBackup);
        fixture.SettingsRepo.Verify(
            repository => repository.CreateVersionedBackupAsync(
                "pre-import",
                It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.SettingsRepo.Verify(
            repository => repository.WriteSettings(
                It.Is<SonosSettings>(settings =>
                    settings.MaxVolume == 64 &&
                    settings.Speakers.Count == 1)),
            Times.Once);
        fixture.MigrationService.Verify(
            service => service.MigrateIfRequiredAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly string _root;
        private readonly ApplicationDbContext _db;

        public BackupFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"sonos-backup-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            SettingsRepo = new Mock<ISettingsRepo>();
            SettingsRepo.Setup(repository => repository.GetSettings())
                .ReturnsAsync(new SonosSettings
                {
                    SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion
                });
            SettingsRepo.Setup(repository => repository.CreateVersionedBackupAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string label, CancellationToken _) => $"config-{label}.json");
            SettingsRepo.Setup(repository => repository.WriteSettings(It.IsAny<SonosSettings?>()))
                .Returns(Task.CompletedTask);

            MigrationService = new Mock<ISettingsSchemaMigrationService>();
            MigrationService.Setup(service => service.MigrateIfRequiredAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SettingsMigrationResult(
                    true,
                    false,
                    SonosSettings.CurrentSettingsSchemaVersion,
                    []));

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"backup-tests-{Guid.NewGuid():N}")
                .Options;
            _db = new ApplicationDbContext(options);
            var actionLogger = new ActionLogger(_db, new HttpContextAccessor());

            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(value => value.ContentRootPath).Returns(_root);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Settings:DataDirectory"] = Path.Combine(_root, "settings")
                })
                .Build();

            Service = new SettingsBackupService(
                SettingsRepo.Object,
                MigrationService.Object,
                actionLogger,
                NullLogger<SettingsBackupService>.Instance,
                environment.Object,
                configuration);
        }

        public Mock<ISettingsRepo> SettingsRepo { get; }
        public Mock<ISettingsSchemaMigrationService> MigrationService { get; }
        public SettingsBackupService Service { get; }

        public void Dispose()
        {
            _db.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
