namespace SonosControl.Web.Services.SonosEvents;

/// <summary>How one speaker currently reports its playback state.</summary>
public sealed record SonosEventSpeakerStatus(bool Live, DateTimeOffset? LastEventUtc, string? LastError);

public interface ISonosEventStatusProvider
{
    /// <summary>The speaker's event status, or null while live events are off.</summary>
    SonosEventSpeakerStatus? GetStatus(string speakerIp);
}
