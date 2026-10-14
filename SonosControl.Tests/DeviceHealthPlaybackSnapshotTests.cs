using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class DeviceHealthPlaybackSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthUsesTheSharedPlaybackObservationWithoutDuplicateTransportProbes(bool stale)
    {
        var settings = new Mock<ISettingsRepo>();
        settings.Setup(repo => repo.GetSettings()).ReturnsAsync(new SonosSettings
        {
            Speakers = [new() { Name = "Room", IpAddress = "192.0.2.1", Uuid = "RINCON_TEST" }]
        });
        var connector = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        var snapshots = new PlaybackSnapshotStore();
        snapshots.Set(new PlaybackSnapshot("192.0.2.1", "Room", true, 42, null, null, stale,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, stale ? "Unavailable" : null));
        var health = new ObservedHealthStore();
        var services = new ServiceCollection();
        services.AddSingleton(settings.Object);
        services.AddSingleton(connector.Object);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ActionLogger>();
        await using var provider = services.BuildServiceProvider();
        using var monitor = new DeviceHealthMonitorService(provider.GetRequiredService<IServiceScopeFactory>(), health,
            NullLogger<DeviceHealthMonitorService>.Instance, TimeProvider.System, snapshots);
        await monitor.StartAsync(CancellationToken.None);
        await health.Updated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync(CancellationToken.None);
        var observation = Assert.Single(health.GetSnapshot());
        Assert.Equal(!stale, observation.IsOnline);
        Assert.Equal(!stale, observation.IsPlaying);
        Assert.Equal(42, observation.CurrentVolume);
        connector.VerifyNoOtherCalls();
    }

    private sealed class ObservedHealthStore : IDeviceHealthSnapshotStore
    {
        private readonly DeviceHealthSnapshotStore _store = new();
        public TaskCompletionSource Updated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<DeviceHealthStatus> GetSnapshot() => _store.GetSnapshot();
        public void Replace(IEnumerable<DeviceHealthStatus> statuses)
        {
            _store.Replace(statuses);
            Updated.TrySetResult();
        }
    }
}
