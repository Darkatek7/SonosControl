namespace SonosControl.Web.Models;

public class PlaybackHistory
{
    public int Id { get; set; }
    public string SpeakerName { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty; // Station, Spotify, YouTube, etc.
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    public double DurationSeconds { get; set; }
}
