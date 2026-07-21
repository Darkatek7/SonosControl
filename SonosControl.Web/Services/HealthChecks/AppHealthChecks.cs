using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Data;
using SonosControl.Web.Services;

namespace SonosControl.Web.Services.HealthChecks;

public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly ApplicationDbContext _dbContext;

    public DatabaseHealthCheck(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
            if (canConnect)
            {
                return HealthCheckResult.Healthy("Database connection is available.");
            }

            return HealthCheckResult.Unhealthy("Database connectivity check failed.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connectivity check threw an exception.", ex);
        }
    }
}

public sealed class SettingsHealthCheck : IHealthCheck
{
    private readonly ISettingsRepo _settingsRepo;

    public SettingsHealthCheck(ISettingsRepo settingsRepo)
    {
        _settingsRepo = settingsRepo;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _settingsRepo.GetSettings();
            if (settings == null)
            {
                return HealthCheckResult.Unhealthy("Settings file could not be loaded.");
            }

            return HealthCheckResult.Healthy("Settings file is readable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Settings file read failed.", ex);
        }
    }
}

public sealed class AutomationHealthCheck : IHealthCheck
{
    private readonly AutomationRuntimeStatus _runtimeStatus;

    public AutomationHealthCheck(AutomationRuntimeStatus runtimeStatus)
    {
        _runtimeStatus = runtimeStatus;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = _runtimeStatus.Snapshot;
        if (snapshot.Phase == AutomationRuntimePhase.Failed)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                snapshot.Error ?? "Automation settings migration failed."));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.SchedulerError))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"The last automation evaluation failed: {snapshot.SchedulerError}"));
        }

        return Task.FromResult(snapshot.Phase == AutomationRuntimePhase.Ready
            ? HealthCheckResult.Healthy($"Automation is ready on settings schema v{snapshot.SettingsSchemaVersion}.")
            : HealthCheckResult.Degraded("Automation is waiting for settings migration."));
    }
}
