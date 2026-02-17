namespace SonosControl.DAL.Models;

public class DeviceHealthStatus
{
    public string SpeakerIp { get; set; } = string.Empty;
    public string SpeakerName { get; set; } = string.Empty;
    public string? SpeakerUuid { get; set; }
    public bool IsOnline { get; set; }
    public bool IsPlaying { get; set; }
    public int? CurrentVolume { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastCheckedUtc { get; set; } = DateTime.UtcNow;
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public long? LastLatencyMs { get; set; }
}
