using Microsoft.Extensions.Diagnostics.HealthChecks;
using SonosControl.Web.Services.FlyDj;

namespace SonosControl.Web.Services.HealthChecks;

/// <summary>Reports the optional FlyDJ sidecar. Never Unhealthy: the sidecar is experimental and must not fail the app's probe.</summary>
public sealed class FlyDjHealthCheck(IFlyDjClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        FlyDjHealth health;
        try { health = await client.CheckHealthAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("The FlyDJ service could not be checked.", ex);
        }

        if (!health.Reachable)
            return HealthCheckResult.Degraded($"The FlyDJ service is unreachable. {health.Message}".Trim());
        if (!health.Ok)
            return HealthCheckResult.Degraded($"The FlyDJ service reports a problem (model status: {health.ModelStatus ?? "unknown"}).");
        return HealthCheckResult.Healthy($"The FlyDJ service is reachable (model status: {health.ModelStatus ?? "unknown"}).");
    }
}
