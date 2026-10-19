using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Linq.Expressions;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Services;

namespace SonosControl.Tests;

internal static class RepositoryTestServices
{
    public static void Register(IServiceCollection services, ISettingsRepo? settings = null, ISonosConnectorRepo? connector = null, IHolidayRepo? holidays = null)
    {
        AddDefaults(services);
        services.AddSingleton(settings ?? Mock.Of<ISettingsRepo>());
        services.AddSingleton(connector ?? Mock.Of<ISonosConnectorRepo>());
        if (connector is not null) ConfigureCancellation(connector);
        services.AddSingleton(holidays ?? Mock.Of<IHolidayRepo>());
    }

    public static void ConfigureCancellation(ISonosConnectorRepo connector)
    {
        Mock<ISonosConnectorRepo> mock;
        try { mock = Mock.Get(connector); }
        catch (ArgumentException) { return; }
        bool HasSetup(string name) => mock.Setups.Any(setup => setup.Expression.Body is MethodCallExpression call
            && call.Method.Name == name && call.Method.GetParameters().Length == 2);
        if (!HasSetup(nameof(ISonosConnectorRepo.GetVolume)))
            mock.Setup(repo => repo.GetVolume(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string ip, CancellationToken token) => connector.GetVolume(ip).WaitAsync(token));
        if (!HasSetup(nameof(ISonosConnectorRepo.IsPlaying)))
            mock.Setup(repo => repo.IsPlaying(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string ip, CancellationToken token) => connector.IsPlaying(ip).WaitAsync(token));
        if (!HasSetup(nameof(ISonosConnectorRepo.PausePlaying)))
            mock.Setup(repo => repo.PausePlaying(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string ip, CancellationToken token) => { token.ThrowIfCancellationRequested(); return connector.PausePlaying(ip); });
    }

    public static void AddDefaults(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        services.TryAddSingleton<IManualAutoStopStatusProvider, NullManualAutoStopStatusProvider>();
        services.TryAddSingleton<ISceneOrchestrationService>(_ =>
        {
            var orchestration = new Mock<ISceneOrchestrationService>();
            orchestration.Setup(service => service.ApplySceneAsync(It.IsAny<SonosControl.DAL.Models.Scene>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SonosControl.DAL.Models.Scene scene, string? _, CancellationToken _) =>
                    new SceneApplyResult(true, "Playback started.", scene.Id, []));
            return orchestration.Object;
        });
        services.TryAddSingleton<PlaybackCommandCoordinator>();
        services.TryAddSingleton<PlaybackSnapshotStore>();
        services.TryAddSingleton<Microsoft.Extensions.Logging.ILogger<PlaybackSnapshotMonitorService>>(NullLogger<PlaybackSnapshotMonitorService>.Instance);
        services.TryAddSingleton<PlaybackSnapshotMonitorService>();
        services.TryAddSingleton<Microsoft.Extensions.Logging.ILogger<SleepTimerService>>(NullLogger<SleepTimerService>.Instance);
        services.TryAddSingleton<SleepTimerService>();
        services.TryAddSingleton<Microsoft.Extensions.Logging.ILogger<ManualPlaybackService>>(NullLogger<ManualPlaybackService>.Instance);
        services.TryAddScoped<ManualPlaybackService>();
        services.TryAddSingleton(NullLogger<SceneTimerService>.Instance);
        services.TryAddSingleton<SceneTimerService>(provider => new SceneTimerService(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(), NullLogger<SceneTimerService>.Instance));
        services.TryAddSingleton<PlaybackSnapshotStore>();
        services.TryAddSingleton<SleepTimerService>(provider => new SleepTimerService(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<TimeProvider>(),
            NullLogger<SleepTimerService>.Instance));
        services.TryAddScoped<SettingsEditingService>();
        services.TryAddScoped<EditorNavigationRequests>();
    }

    public static SceneTimerService CreateTimers() => new(Mock.Of<IServiceScopeFactory>(), TimeProvider.System, NullLogger<SceneTimerService>.Instance);
}

internal sealed class NullManualAutoStopStatusProvider : IManualAutoStopStatusProvider
{
    public Task<ManualAutoStopStatus?> GetNextStopAsync(string speakerIp, CancellationToken cancellationToken = default)
        => Task.FromResult<ManualAutoStopStatus?>(null);
}
