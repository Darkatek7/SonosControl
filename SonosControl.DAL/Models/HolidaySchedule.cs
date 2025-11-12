namespace SonosControl.DAL.Models
{
    public class HolidaySchedule : DaySchedule
    {
        public DateOnly Date { get; set; }
        public string? Name { get; set; }
        public bool SkipPlayback { get; set; }
    }
}
