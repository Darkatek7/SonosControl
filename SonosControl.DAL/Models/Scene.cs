namespace SonosControl.DAL.Models;

public enum SceneSourceType
{
    None = 0,
    Station = 1,
    Spotify = 2,
    YouTubeMusic = 3
}

public class Scene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Scene";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public SceneSourceType SourceType { get; set; } = SceneSourceType.None;
    public string? SourceUrl { get; set; }
    public bool IsSyncedPlayback { get; set; } = true;
    public string? MasterSpeakerIp { get; set; }
    public int? TimerMinutes { get; set; }
    public List<string> SpeakerIps { get; set; } = new();
    public List<SceneAction> Actions { get; set; } = new();
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
}
