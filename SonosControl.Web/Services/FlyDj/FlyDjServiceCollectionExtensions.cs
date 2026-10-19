using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SonosControl.Web.Services.HealthChecks;

namespace SonosControl.Web.Services.FlyDj;

public static class FlyDjServiceCollectionExtensions
{
    public const string StreamPath = "/flydj/stream";
    /// <summary>Every role the app seeds; FlyDJ (page, start/stop, settings, API, live stream) is open to all of them.</summary>
    public const string AllowedRoles = "admin,operator,superadmin";

    /// <summary>
    /// Registers FlyDJ. The options are always available (so the UI can read
    /// <c>IOptions&lt;FlyDjOptions&gt;.Value.Enabled</c>); every other service exists only when enabled.
    /// </summary>
    public static IServiceCollection AddFlyDj(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FlyDjOptions>(configuration.GetSection(FlyDjOptions.SectionName));
        var enabled = FlyDjOptions.IsEnabled(configuration);
        services.PostConfigure<FlyDjOptions>(options => options.Enabled = enabled);
        if (!enabled) return services;

        services.AddHttpClient(FlyDjClient.HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<FlyDjOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.ServiceUrl) ? "http://flydj:8090" : options.ServiceUrl.Trim();
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 300));
            if (!string.IsNullOrWhiteSpace(options.SharedSecret))
                client.DefaultRequestHeaders.TryAddWithoutValidation(FlyDjClient.TokenHeader, options.SharedSecret);
        });
        services.TryAddSingleton<IFlyDjClient, FlyDjClient>();
        services.TryAddSingleton<FlyDjStateStore>();
        services.TryAddSingleton<FlyDjGeometryCache>();
        services.TryAddScoped<FlyDjCandidateProvider>();
        services.TryAddSingleton<IScheduleRoomOwnership>(provider => provider.GetRequiredService<AutomationSchedulerService>());
        services.TryAddSingleton<FlyDjConductorService>();
        services.TryAddSingleton<IFlyDjConductor>(provider => provider.GetRequiredService<FlyDjConductorService>());
        // Background services off (for example in UI smoke runs): the page still works, it just stays stopped.
        if (configuration.GetValue("BackgroundServices:Enabled", true))
            services.AddHostedService(provider => provider.GetRequiredService<FlyDjConductorService>());
        services.AddHealthChecks().AddCheck<FlyDjHealthCheck>("flydj");
        return services;
    }

    /// <summary>Adds the WebSocket middleware needed by the live-view relay when FlyDJ is enabled.</summary>
    public static IApplicationBuilder UseFlyDj(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<FlyDjOptions>>().Value;
        if (options.Enabled) app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        return app;
    }

    /// <summary>Maps the authenticated WebSocket relay to the sidecar's live stream when FlyDJ is enabled.</summary>
    public static IEndpointRouteBuilder MapFlyDj(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<FlyDjOptions>>().Value;
        if (!options.Enabled) return endpoints;
        endpoints.Map(StreamPath, FlyDjStreamRelay.HandleAsync)
            .RequireAuthorization(policy => policy.RequireRole(AllowedRoles.Split(',')));
        return endpoints;
    }
}
