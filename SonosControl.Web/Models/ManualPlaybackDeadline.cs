namespace SonosControl.Web.Models;

/// <summary>A durable stop deadline armed by a successful manual playback start.</summary>
public sealed class ManualPlaybackDeadline
{
    public string SpeakerIp { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime StopAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RuleKey { get; set; }
    public long Generation { get; set; }
}
