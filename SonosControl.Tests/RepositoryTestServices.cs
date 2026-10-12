using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
        services.AddSingleton(holidays ?? Mock.Of<IHolidayRepo>());
    }

    public static void AddDefaults(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(NullLogger<SceneTimerService>.Instance);
        services.TryAddSingleton<SceneTimerService>(provider => new SceneTimerService(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(), NullLogger<SceneTimerService>.Instance));
        services.TryAddScoped<SettingsEditingService>();
        services.TryAddScoped<EditorNavigationRequests>();
    }

    public static SceneTimerService CreateTimers() => new(Mock.Of<IServiceScopeFactory>(), TimeProvider.System, NullLogger<SceneTimerService>.Instance);
}
