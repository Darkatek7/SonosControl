namespace SonosControl.DAL.Models;

public class SceneAction
{
    public string SpeakerIp { get; set; } = string.Empty;
    public int? Volume { get; set; }
    public bool IncludeInPlayback { get; set; } = true;
    public bool IsMaster { get; set; }
}
