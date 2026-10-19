namespace SonosControl.Web.Services.FlyDj;

/// <summary>Deployment options for the optional FlyDJ connectome DJ (section <c>FlyDj</c>).</summary>
public sealed class FlyDjOptions
{
    public const string SectionName = "FlyDj";
    public const string EnabledEnvironmentVariable = "FLYDJ_ENABLED";

    public bool Enabled { get; set; }
    public string ServiceUrl { get; set; } = "http://flydj:8090";
    public string? SharedSecret { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Resolves the feature flag from <c>FlyDj:Enabled</c>, falling back to the raw
    /// <c>FLYDJ_ENABLED</c> environment variable used by Docker Compose.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        var configured = configuration[$"{SectionName}:Enabled"];
        if (bool.TryParse(configured, out var enabled)) return enabled;
        var raw = configuration[EnabledEnvironmentVariable] ?? Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        return bool.TryParse(raw, out enabled) && enabled;
    }
}
