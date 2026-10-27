namespace SonosControl.DAL.Models;

public enum SonosEventService
{
    AVTransport,
    RenderingControl,
    ZoneGroupTopology
}

/// <summary>
/// State reported by one UPnP <c>LastChange</c> event. A null value means the event did not report that field;
/// an empty string means the speaker reported it as empty or not implemented.
/// </summary>
public sealed record SonosPlaybackEvent(SonosEventService Service)
{
    public string? TransportState { get; init; }
    public string? TransportUri { get; init; }
    public string? TrackUri { get; init; }
    public int? TrackNumber { get; init; }
    public string? TrackMetadata { get; init; }
    public int? Volume { get; init; }

    public bool HasValues => TransportState is not null || TransportUri is not null || TrackUri is not null
        || TrackNumber is not null || TrackMetadata is not null || Volume is not null;
}
