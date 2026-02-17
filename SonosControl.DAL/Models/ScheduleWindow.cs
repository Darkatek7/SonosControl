namespace SonosControl.DAL.Models;

public enum ScheduleRecurrenceType
{
    Daily = 0,
    Weekdays = 1,
    Weekends = 2,
    CustomDays = 3
}

public class ScheduleWindow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Window";
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public TimeOnly StartTime { get; set; } = new(6, 0);
    public TimeOnly StopTime { get; set; } = new(18, 0);
    public ScheduleRecurrenceType RecurrenceType { get; set; } = ScheduleRecurrenceType.Weekdays;
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? SceneId { get; set; }
    public int FadeInSeconds { get; set; }
    public int FadeOutSeconds { get; set; }
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
}
