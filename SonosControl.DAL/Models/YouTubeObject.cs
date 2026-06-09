namespace SonosControl.DAL.Models;

public enum YouTubePlaybackMode
{
    Single = 0,
    PlaylistOrdered = 1,
    PlaylistShuffle = 2,
    AutoQueueRelated = 3
}

public class YouTubeObject
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public YouTubePlaybackMode? PlaybackMode { get; set; }
    public int? PreferredQueueLength { get; set; }
}
