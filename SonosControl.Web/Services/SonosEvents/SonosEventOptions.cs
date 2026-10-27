namespace SonosControl.Web.Services.SonosEvents;

public sealed class SonosEventOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL the speakers use for NOTIFY callbacks. Falls back to Playback:PublicBaseUrl.</summary>
    public string? CallbackBaseUrl { get; set; }

    public int SubscriptionTimeoutSeconds { get; set; } = 1800;

    /// <summary>Full probe interval for speakers with working events. Their status goes stale after 2.5 intervals.</summary>
    public int ReconcileSeconds { get; set; } = 30;
}
