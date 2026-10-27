namespace SonosControl.DAL.Models;

/// <summary>An active UPnP event subscription as granted by the speaker.</summary>
public sealed record SonosEventSubscription(string Sid, TimeSpan Timeout);
