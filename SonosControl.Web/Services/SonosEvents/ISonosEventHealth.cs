namespace SonosControl.Web.Services.SonosEvents;

/// <summary>Tells the playback poller which speakers report their state through live events.</summary>
public interface ISonosEventHealth
{
    /// <summary>True while the speaker has working event subscriptions, so polling can slow down to reconciliation.</summary>
    bool IsHealthy(string speakerIp);

    /// <summary>How often a speaker with working events is still probed in full.</summary>
    TimeSpan ReconcileInterval { get; }
}
