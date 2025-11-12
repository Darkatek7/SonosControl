namespace SonosControl.DAL.Models
{
    public class DaySchedule
    {
        public TimeOnly StartTime { get; set; } = new TimeOnly(6, 0);
        public TimeOnly StopTime { get; set; } = new TimeOnly(18, 0);
        public string? StationUrl { get; set; }
        public string? SpotifyUrl { get; set; }
        public string? YouTubeMusicUrl { get; set; }
        public bool PlayRandomStation { get; set; }
        public bool PlayRandomSpotify { get; set; }
        public bool PlayRandomYouTubeMusic { get; set; }
        public bool SkipPlayback { get; set; }
    }
}
