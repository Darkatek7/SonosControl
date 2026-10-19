namespace SonosControl.DAL.Models
{
    public class SonosSpeaker
    {
        public string Name { get; set; } = "Living Room";
        public string IpAddress { get; set; } = "";
        public string? Uuid { get; set; }
        public int? StartupVolume { get; set; }
        public Dictionary<DayOfWeek, ManualAutoStopDay> ManualAutoStopDays { get; set; } = new();
        public int? ManualAutoStopMinutes { get; set; }
    }

    public class ManualAutoStopDay
    {
        public bool Enabled { get; set; }
        public TimeOnly StopTime { get; set; } = new(22, 0);
        public TimeOnly ReleaseTime { get; set; } = new(6, 0);
    }
}
