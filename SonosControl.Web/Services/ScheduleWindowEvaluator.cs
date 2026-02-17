using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public static class ScheduleWindowEvaluator
{
    public static ScheduleWindow? SelectActiveWindow(IEnumerable<ScheduleWindow>? windows, DateTimeOffset nowLocal)
    {
        if (windows is null)
        {
            return null;
        }

        return windows
            .Where(window => window.IsEnabled)
            .Where(window => IsWindowActive(window, nowLocal))
            .OrderBy(window => window.Priority)
            .ThenByDescending(window => window.LastModifiedUtc)
            .ThenBy(window => window.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static bool IsWindowActive(ScheduleWindow window, DateTimeOffset nowLocal)
    {
        if (!window.IsEnabled)
        {
            return false;
        }

        var date = DateOnly.FromDateTime(nowLocal.LocalDateTime);
        var time = TimeOnly.FromDateTime(nowLocal.LocalDateTime);

        if (window.StartDate.HasValue && date < window.StartDate.Value)
        {
            return false;
        }

        if (window.EndDate.HasValue && date > window.EndDate.Value)
        {
            return false;
        }

        var isOvernight = window.StopTime <= window.StartTime;
        if (!isOvernight)
        {
            if (time < window.StartTime || time >= window.StopTime)
            {
                return false;
            }

            return IsDateAllowed(window, nowLocal.DayOfWeek);
        }

        if (time >= window.StartTime)
        {
            return IsDateAllowed(window, nowLocal.DayOfWeek);
        }

        if (time < window.StopTime)
        {
            var previousDay = nowLocal.AddDays(-1).DayOfWeek;
            return IsDateAllowed(window, previousDay);
        }

        return false;
    }

    private static bool IsDateAllowed(ScheduleWindow window, DayOfWeek day)
    {
        return window.RecurrenceType switch
        {
            ScheduleRecurrenceType.Daily => true,
            ScheduleRecurrenceType.Weekdays => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            ScheduleRecurrenceType.Weekends => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
            ScheduleRecurrenceType.CustomDays => window.DaysOfWeek?.Contains(day) == true,
            _ => false
        };
    }
}
