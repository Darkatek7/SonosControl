namespace SonosControl.Web.Services.SonosEvents;

public sealed class SonosEventOptions
{
    public bool Enabled { get; set; }

    /// <summary>Base URL the speakers use for NOTIFY callbacks. Falls back to Playback:PublicBaseUrl.</summary>
    public string? CallbackBaseUrl { get; set; }

    public int SubscriptionTimeoutSeconds { get; set; } = 1800;
}
