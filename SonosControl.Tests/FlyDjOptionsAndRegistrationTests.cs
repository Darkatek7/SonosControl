using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SonosControl.Web.Controllers;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;
using SonosControl.Web.Services.HealthChecks;
using Xunit;

namespace SonosControl.Tests;

// These tests read FLYDJ_ENABLED from the process environment, so they must not run alongside each other.
[CollectionDefinition("FlyDjEnvironment", DisableParallelization = true)]
public sealed class FlyDjEnvironmentCollection;

[Collection("FlyDjEnvironment")]
public class FlyDjOptionsTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable);

    public FlyDjOptionsTests() => Environment.SetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable, null);

    public void Dispose() => Environment.SetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable, _original);

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(item => item.Key, item => item.Value)).Build();

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    public void ConfiguredValueWins(string value, bool expected)
        => Assert.Equal(expected, FlyDjOptions.IsEnabled(Config(("FlyDj:Enabled", value))));

    [Fact]
    public void MissingConfigurationDefaultsToDisabled()
        => Assert.False(FlyDjOptions.IsEnabled(Config()));

    [Fact]
    public void UnparseableConfigurationFallsBackToTheEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable, "true");
        Assert.True(FlyDjOptions.IsEnabled(Config(("FlyDj:Enabled", ""))));
        Assert.True(FlyDjOptions.IsEnabled(Config()));
    }

    [Fact]
    public void ExplicitConfigurationOverridesTheEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable, "true");
        Assert.False(FlyDjOptions.IsEnabled(Config(("FlyDj:Enabled", "false"))));
    }

    [Fact]
    public void EnvironmentVariableCanBeProvidedThroughConfiguration()
    {
        Assert.True(FlyDjOptions.IsEnabled(Config((FlyDjOptions.EnabledEnvironmentVariable, "true"))));
        Assert.False(FlyDjOptions.IsEnabled(Config((FlyDjOptions.EnabledEnvironmentVariable, "nonsense"))));
    }
}

[Collection("FlyDjEnvironment")]
public class FlyDjRegistrationTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable);

    public FlyDjRegistrationTests() => Environment.SetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable, null);

    public void Dispose() => Environment.SetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable, _original);

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PlaybackCommandCoordinator>();
        services.AddSingleton<PlaybackSnapshotStore>();
        services.AddSingleton(Moq.Mock.Of<IScheduleRoomOwnership>());
        services.AddFlyDj(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DisabledRegistersOnlyTheOptions()
    {
        using var provider = Build(new() { ["FlyDj:Enabled"] = "false" });

        Assert.False(provider.GetRequiredService<IOptions<FlyDjOptions>>().Value.Enabled);
        Assert.Null(provider.GetService<IFlyDjConductor>());
        Assert.Null(provider.GetService<IFlyDjClient>());
        Assert.Null(provider.GetService<FlyDjStateStore>());
        Assert.Null(provider.GetService<FlyDjConductorService>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), service => service is FlyDjConductorService);
        Assert.Null(provider.GetService<IHttpClientFactory>());
    }

    [Fact]
    public void MissingConfigurationAlsoLeavesFlyDjUnregistered()
    {
        using var provider = Build([]);

        Assert.False(provider.GetRequiredService<IOptions<FlyDjOptions>>().Value.Enabled);
        Assert.Null(provider.GetService<IFlyDjConductor>());
    }

    [Fact]
    public void EnvironmentFlagEnablesFlyDjWhenConfigurationIsSilent()
    {
        Environment.SetEnvironmentVariable(FlyDjOptions.EnabledEnvironmentVariable, "true");
        using var provider = Build([]);

        Assert.True(provider.GetRequiredService<IOptions<FlyDjOptions>>().Value.Enabled);
        Assert.NotNull(provider.GetService<IFlyDjClient>());
    }

    [Fact]
    public void EnabledRegistersTheServicesAndTheConductorAsHostedService()
    {
        using var provider = Build(new() { ["FlyDj:Enabled"] = "true" });

        Assert.True(provider.GetRequiredService<IOptions<FlyDjOptions>>().Value.Enabled);
        Assert.IsType<FlyDjClient>(provider.GetRequiredService<IFlyDjClient>());
        Assert.Same(provider.GetRequiredService<FlyDjStateStore>(), provider.GetRequiredService<FlyDjStateStore>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is FlyDjConductorService);
        var health = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Contains(health, registration => registration.Name == "flydj");
    }

    [Fact]
    public void ConductorHostedServiceIsSkippedWhenBackgroundServicesAreOff()
    {
        using var provider = Build(new() { ["FlyDj:Enabled"] = "true", ["BackgroundServices:Enabled"] = "false" });

        Assert.DoesNotContain(provider.GetServices<IHostedService>(), service => service is FlyDjConductorService);
        Assert.NotNull(provider.GetService<FlyDjStateStore>());
        Assert.NotNull(provider.GetService<IFlyDjConductor>());
    }

    [Fact]
    public async Task ControllerReturnsNotFoundWhenDisabledAndPassesThroughWhenEnabled()
    {
        var filter = new FlyDjEnabledAttribute();

        Assert.IsType<NotFoundResult>(await RunFilterAsync(filter, enabled: false));
        Assert.Null(await RunFilterAsync(filter, enabled: true));
        Assert.IsType<NotFoundResult>(await RunFilterAsync(filter, enabled: null));
    }

    [Fact]
    public async Task HealthCheckIsDegradedWhenUnreachableAndNeverUnhealthy()
    {
        var healthy = new FlyDjHealthCheck(new FakeHealthClient(new FlyDjHealth(true, true, "ready", null)));
        var building = new FlyDjHealthCheck(new FakeHealthClient(new FlyDjHealth(true, false, "error", "503")));
        var down = new FlyDjHealthCheck(new FakeHealthClient(new FlyDjHealth(false, false, null, "refused")));
        var throwing = new FlyDjHealthCheck(new FakeHealthClient(null));

        Assert.Equal(HealthStatus.Healthy, (await healthy.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(HealthStatus.Degraded, (await building.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(HealthStatus.Degraded, (await down.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(HealthStatus.Degraded, (await throwing.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    private static async Task<IActionResult?> RunFilterAsync(FlyDjEnabledAttribute filter, bool? enabled)
    {
        var services = new ServiceCollection();
        if (enabled is { } value) services.AddSingleton<IOptions<FlyDjOptions>>(Options.Create(new FlyDjOptions { Enabled = value }));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var context = new ResourceExecutingContext(actionContext, [], []);
        filter.OnResourceExecuting(context);
        await Task.CompletedTask;
        return context.Result;
    }

    private sealed class FakeHealthClient(FlyDjHealth? health) : IFlyDjClient
    {
        public Task<FlyDjHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => health is null ? throw new InvalidOperationException("boom") : Task.FromResult(health);
        public Task<FlyDjModelInfo> GetModelAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlyDjSimulationState> GetStateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlyDjSimulationState> StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlyDjSimulationState> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlyDjAuditionResponse> AuditionAsync(FlyDjAuditionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlyDjListenResponse> ListenAsync(string? url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FlyDjGeometryResult> GetGeometryAsync(string? etag = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
