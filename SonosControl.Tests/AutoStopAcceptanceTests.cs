using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class AutoStopAcceptanceTests
{
    [Fact]
    public async Task OnlySuccessfulStartsRenewDeadlineWhileOtherCommandsAndReloadDoNot()
    {
        using var fixture = new Fixture();
        await fixture.Service.RegisterSuccessfulStartAsync(["A"]);
        var first = await fixture.Service.GetNextStopAsync("A");
        fixture.Time.Now = fixture.Time.Now.AddMinutes(10);

        // Volume, skip and failed starts acquire command ownership but do not
        // report a successful playback start.
        await using (await fixture.Commands.EnterManualAsync(["A"]))
        {
            Assert.Equal(first, await fixture.Service.GetNextStopAsync("A"));
        }
        Assert.Equal(first, await fixture.CreateService().GetNextStopAsync("A"));

        await fixture.Service.RegisterSuccessfulStartAsync(["A"]);
        var renewed = await fixture.Service.GetNextStopAsync("A");
        Assert.Equal(fixture.Time.Now.AddMinutes(30), renewed!.Value.StopAtUtc);
        fixture.Time.Now = first!.Value.StopAtUtc.AddSeconds(1);
        await fixture.Service.ProcessDueAsync();
        fixture.Connector.Verify(c => c.PausePlaying("A", It.IsAny<CancellationToken>()), Times.Never);
        fixture.Time.Now = renewed.Value.StopAtUtc.AddSeconds(1);
        await fixture.Service.ProcessDueAsync();
        fixture.Connector.Verify(c => c.PausePlaying("A", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistedDeadlineStopsAfterServiceRestartUsingSqlite()
    {
        using var fixture = new Fixture();
        await fixture.Service.RegisterSuccessfulStartAsync(["A"]);
        var armed = await fixture.Service.GetNextStopAsync("A");
        Assert.NotNull(armed);
        Assert.Equal(fixture.Time.Now.AddMinutes(30), armed.Value.StopAtUtc);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(31);
        var restarted = fixture.CreateService();

        await restarted.ProcessDueAsync().WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Connector.Verify(c => c.PausePlaying("A", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await restarted.GetNextStopAsync("A"));
    }

    [Fact]
    public async Task NewStartWhileDueStopWaitsForOwnershipInvalidatesOldGeneration()
    {
        using var fixture = new Fixture();
        await fixture.Service.RegisterSuccessfulStartAsync(["A"]);
        var armed = await fixture.Service.GetNextStopAsync("A");
        Assert.NotNull(armed);
        Assert.Equal(fixture.Time.Now.AddMinutes(30), armed.Value.StopAtUtc);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(31);
        var groupRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connector.Setup(c => c.GetAllSpeakersInGroup("A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { groupRead.TrySetResult(); return new[] { "A" }; });
        var lease = await fixture.Commands.EnterManualAsync(["A"]);
        try
        {
            var stop = fixture.Service.ProcessDueAsync();
            await groupRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Service.RegisterSuccessfulStartAsync(["A"]);
            await lease.DisposeAsync();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await lease.DisposeAsync(); }

        fixture.Connector.Verify(c => c.PausePlaying("A", It.IsAny<CancellationToken>()), Times.Never);
        var next = await fixture.Service.GetNextStopAsync("A");
        Assert.Equal(fixture.Time.Now.AddMinutes(30), next!.Value.StopAtUtc);
    }

    [Fact]
    public async Task ReleaseDoesNotPauseAndContinuingAppPlaybackGetsNextDailyCutoff()
    {
        using var fixture = new Fixture();
        fixture.Time.Now = new DateTimeOffset(2026, 10, 20, 5, 50, 0, TimeSpan.Zero);
        await fixture.Service.RegisterSuccessfulStartAsync(["A"]);
        fixture.Time.Now = new DateTimeOffset(2026, 10, 20, 6, 1, 0, TimeSpan.Zero);

        await fixture.Service.ProcessDueAsync().WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Connector.Verify(c => c.PausePlaying("A", It.IsAny<CancellationToken>()), Times.Never);
        fixture.Time.Now = new DateTimeOffset(2026, 10, 20, 22, 1, 0, TimeSpan.Zero);
        await fixture.Service.ProcessDueAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Connector.Verify(c => c.PausePlaying("A", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedIsolationDoesNotPauseOtherGroupMemberAndCanRetry()
    {
        using var fixture = new Fixture();
        var isolated = false;
        fixture.Connector.Setup(c => c.GetAllSpeakersInGroup("A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => isolated ? new[] { "A" } : new[] { "A", "B" });
        fixture.Connector.Setup(c => c.UngroupSpeaker("A", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Offline"));
        await fixture.Service.RegisterSuccessfulStartAsync(["A"]);
        var armed = await fixture.Service.GetNextStopAsync("A");
        Assert.NotNull(armed);
        Assert.Equal(fixture.Time.Now.AddMinutes(30), armed.Value.StopAtUtc);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(31);
        await fixture.Service.ProcessDueAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Connector.Verify(c => c.PausePlaying(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        fixture.Connector.Setup(c => c.UngroupSpeaker("A", It.IsAny<CancellationToken>()))
            .Callback(() => isolated = true).Returns(Task.CompletedTask);
        fixture.Time.Now = fixture.Time.Now.AddMinutes(1);
        await fixture.Service.ProcessDueAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Connector.Verify(c => c.PausePlaying("A", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Connector.Verify(c => c.PausePlaying("B", It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Automation:TimeZone"] = "UTC" }).Build();
        public Clock Time { get; } = new();
        public PlaybackCommandCoordinator Commands { get; }
        public Mock<ISonosConnectorRepo> Connector { get; } = new();
        public ManualAutoStopService Service { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Commands = new PlaybackCommandCoordinator(Time);
            var settings = new SonosSettings
            {
                SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion,
                Speakers = [new SonosSpeaker
                {
                    IpAddress = "A", ManualAutoStopMinutes = 30,
                    ManualAutoStopDays = Enum.GetValues<DayOfWeek>().ToDictionary(day => day,
                        _ => new ManualAutoStopDay { Enabled = true, StopTime = new(22, 0), ReleaseTime = new(6, 0) })
                }]
            };
            var repository = new Mock<ISettingsRepo>();
            repository.Setup(r => r.GetSettings()).ReturnsAsync(settings);
            Connector.Setup(c => c.GetAllSpeakersInGroup(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string ip, CancellationToken _) => new[] { ip });
            var services = new ServiceCollection();
            services.AddSingleton(repository.Object);
            services.AddSingleton(Connector.Object);
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(_directory, "test.db")};Pooling=False"));
            _provider = services.BuildServiceProvider();
            using (var scope = _provider.CreateScope()) scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate();
            Service = CreateService();
        }

        public ManualAutoStopService CreateService() => new(_provider.GetRequiredService<IServiceScopeFactory>(), Commands,
            Time, _configuration, NullLogger<ManualAutoStopService>.Instance);
        public void Dispose() { _provider.Dispose(); Directory.Delete(_directory, true); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 22, 10, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
