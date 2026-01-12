namespace SonosControl.DAL.Models
{
    public class SonosSpeaker
    {
        public string Name { get; set; } = "Living Room";
        public string IpAddress { get; set; } = "";
        public string? Uuid { get; set; }
        public int? StartupVolume { get; set; }
    }
}
