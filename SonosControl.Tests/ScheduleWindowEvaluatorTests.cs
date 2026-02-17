using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class ScheduleWindowEvaluatorTests
{
    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute)
        => new(year, month, day, hour, minute, 0, DateTimeOffset.Now.Offset);

    private static ScheduleWindow AlwaysActiveWindow(string id, int priority, DateTime lastModifiedUtc)
    {
        return new ScheduleWindow
        {
            Id = id,
            Name = id,
            IsEnabled = true,
            Priority = priority,
            StartTime = new TimeOnly(0, 0),
            StopTime = new TimeOnly(23, 59),
            RecurrenceType = ScheduleRecurrenceType.Daily,
            LastModifiedUtc = lastModifiedUtc
        };
    }

    [Fact]
    public void IsWindowActive_DailySameDayWindow_ReturnsTrueWithinRange()
    {
        var window = new ScheduleWindow
        {
            IsEnabled = true,
            StartTime = new TimeOnly(9, 0),
            StopTime = new TimeOnly(12, 0),
            RecurrenceType = ScheduleRecurrenceType.Daily
        };

        var now = LocalTime(2026, 2, 16, 10, 30);
        Assert.True(ScheduleWindowEvaluator.IsWindowActive(window, now));
    }

    [Fact]
    public void IsWindowActive_DailySameDayWindow_ReturnsFalseOutsideRange()
    {
        var window = new ScheduleWindow
        {
            IsEnabled = true,
            StartTime = new TimeOnly(9, 0),
            StopTime = new TimeOnly(12, 0),
            RecurrenceType = ScheduleRecurrenceType.Daily
        };

        var now = LocalTime(2026, 2, 16, 12, 0);
        Assert.False(ScheduleWindowEvaluator.IsWindowActive(window, now));
    }

    [Fact]
    public void IsWindowActive_OvernightWeekdays_UsesPreviousDayAfterMidnight()
    {
        var window = new ScheduleWindow
        {
            IsEnabled = true,
            StartTime = new TimeOnly(22, 0),
            StopTime = new TimeOnly(2, 0),
            RecurrenceType = ScheduleRecurrenceType.Weekdays
        };

        // Tuesday 01:00 should still be active because Monday is a weekday.
        var now = LocalTime(2026, 2, 17, 1, 0);
        Assert.True(ScheduleWindowEvaluator.IsWindowActive(window, now));
    }

    [Fact]
    public void IsWindowActive_CustomDays_RequiresMatchingDay()
    {
        var window = new ScheduleWindow
        {
            IsEnabled = true,
            StartTime = new TimeOnly(8, 0),
            StopTime = new TimeOnly(9, 0),
            RecurrenceType = ScheduleRecurrenceType.CustomDays,
            DaysOfWeek = new List<DayOfWeek> { DayOfWeek.Saturday }
        };

        var monday = LocalTime(2026, 2, 16, 8, 30);
        Assert.False(ScheduleWindowEvaluator.IsWindowActive(window, monday));

        var saturday = LocalTime(2026, 2, 21, 8, 30);
        Assert.True(ScheduleWindowEvaluator.IsWindowActive(window, saturday));
    }

    [Fact]
    public void SelectActiveWindow_ResolvesOverlap_ByPriorityFirst()
    {
        var now = LocalTime(2026, 2, 16, 10, 15);
        var highPriorityNumber = AlwaysActiveWindow("window-a", priority: 100, lastModifiedUtc: DateTime.UtcNow.AddMinutes(5));
        var lowPriorityNumber = AlwaysActiveWindow("window-b", priority: 10, lastModifiedUtc: DateTime.UtcNow.AddMinutes(-5));

        var selected = ScheduleWindowEvaluator.SelectActiveWindow(new[] { highPriorityNumber, lowPriorityNumber }, now);

        Assert.NotNull(selected);
        Assert.Equal("window-b", selected!.Id);
    }

    [Fact]
    public void SelectActiveWindow_ResolvesOverlap_ByLatestEditThenId()
    {
        var now = LocalTime(2026, 2, 16, 10, 15);
        var sharedModifiedTime = DateTime.UtcNow;

        var older = AlwaysActiveWindow("window-c", priority: 20, lastModifiedUtc: sharedModifiedTime.AddMinutes(-20));
        var newerB = AlwaysActiveWindow("window-b", priority: 20, lastModifiedUtc: sharedModifiedTime);
        var newerA = AlwaysActiveWindow("window-a", priority: 20, lastModifiedUtc: sharedModifiedTime);

        var selected = ScheduleWindowEvaluator.SelectActiveWindow(new[] { older, newerB, newerA }, now);

        Assert.NotNull(selected);
        Assert.Equal("window-a", selected!.Id);
    }
}
