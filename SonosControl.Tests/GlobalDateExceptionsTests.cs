using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class GlobalDateExceptionsTests
{
    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    [InlineData(2028)]
    [InlineData(2029)]
    public void AnnualException_BlocksAllWeekdaySchedulesAndFallbacks(int year)
    {
        var holiday = new DateTimeOffset(year, 12, 25, 10, 0, 0, TimeSpan.FromHours(1));
        var settings = new SonosSettings
        {
            AnnualAutomationExcludedDates = [new DateOnly(2026, 12, 25)],
            ScheduleWindows = Enum.GetValues<DayOfWeek>().Select(day => new ScheduleWindow
            {
                Name = $"{day} schedule",
                StartTime = new TimeOnly(9, 0),
                StopTime = new TimeOnly(12, 0),
                RecurrenceType = ScheduleRecurrenceType.CustomDays,
                DaysOfWeek = [day]
            }).ToList()
        };
        settings.ScheduleWindows.Add(new ScheduleWindow
        {
            Name = "Daily fallback",
            StartTime = new TimeOnly(9, 0),
            StopTime = new TimeOnly(12, 0),
            RecurrenceType = ScheduleRecurrenceType.Daily
        });

        Assert.Null(ScheduleWindowEvaluator.SelectActiveWindow(settings.ScheduleWindows, holiday, settings));
        Assert.NotNull(ScheduleWindowEvaluator.SelectActiveWindow(settings.ScheduleWindows, holiday.AddDays(1), settings));
    }

    [Theory]
    [InlineData(2026, 12, 25, false)]
    [InlineData(2027, 12, 25, true)]
    [InlineData(2026, 12, 26, true)]
    public void OneTimeException_OnlyBlocksTheSpecifiedYear(int year, int month, int day, bool canPlay)
    {
        var settings = new SonosSettings { AutomationExcludedDates = [new DateOnly(2026, 12, 25)] };
        var window = DailyWindow();

        Assert.Equal(canPlay, ScheduleWindowEvaluator.IsWindowActive(window,
            new DateTimeOffset(year, month, day, 10, 0, 0, TimeSpan.Zero), settings));
    }

    [Theory]
    [InlineData(2028, 2, 29, false)]
    [InlineData(2029, 2, 28, true)]
    [InlineData(2029, 3, 1, true)]
    public void AnnualLeapDay_OnlyBlocksFebruary29(int year, int month, int day, bool canPlay)
    {
        var settings = new SonosSettings { AnnualAutomationExcludedDates = [new DateOnly(2024, 2, 29)] };

        Assert.Equal(canPlay, ScheduleWindowEvaluator.IsWindowActive(DailyWindow(),
            new DateTimeOffset(year, month, day, 10, 0, 0, TimeSpan.Zero), settings));
    }

    [Theory]
    [InlineData(24, 23, true)]
    [InlineData(25, 1, false)]
    [InlineData(25, 23, false)]
    [InlineData(26, 1, false)]
    [InlineData(26, 23, true)]
    public void GlobalException_BlocksOvernightPlaybackAndItsStartDate(int day, int hour, bool canPlay)
    {
        var settings = new SonosSettings { AnnualAutomationExcludedDates = [new DateOnly(2026, 12, 25)] };
        var overnight = DailyWindow();
        overnight.StartTime = new TimeOnly(22, 0);
        overnight.StopTime = new TimeOnly(2, 0);

        Assert.Equal(canPlay, ScheduleWindowEvaluator.IsWindowActive(overnight,
            new DateTimeOffset(2027, 12, day, hour, 0, 0, TimeSpan.Zero), settings));
    }

    [Fact]
    public void NormalizeExceptions_PromotesExistingDatesAndDeduplicatesAnnualDates()
    {
        var date = new DateOnly(2026, 12, 25);
        var settings = new SonosSettings
        {
            AnnualAutomationExcludedDates = [date],
            ScheduleWindows =
            [
                new ScheduleWindow { ExcludedDates = [date], AnnualExcludedDates = [date] },
                new ScheduleWindow { AnnualExcludedDates = [new DateOnly(2027, 12, 25)] }
            ]
        };

        settings.NormalizeAutomationDateExceptions();
        settings.NormalizeAutomationDateExceptions();

        Assert.Equal(date, Assert.Single(settings.AutomationExcludedDates));
        Assert.Equal(date, Assert.Single(settings.AnnualAutomationExcludedDates));
        Assert.All(settings.ScheduleWindows, window =>
        {
            Assert.Empty(window.ExcludedDates);
            Assert.Empty(window.AnnualExcludedDates);
        });
    }

    [Fact]
    public void NormalizeExceptions_PreservesPlayableLegacyHolidayOverride()
    {
        var playableHoliday = new DateOnly(2026, 12, 24);
        var quietHoliday = new DateOnly(2026, 12, 25);
        var baseline = DailyWindow();
        baseline.Id = "legacy-window-baseline";
        baseline.ExcludedDates = [playableHoliday, quietHoliday];
        var holidayOverride = DailyWindow();
        holidayOverride.Id = "legacy-holiday-window-christmas-eve";
        holidayOverride.StartDate = holidayOverride.EndDate = playableHoliday;
        var settings = new SonosSettings { ScheduleWindows = [baseline, holidayOverride] };

        settings.NormalizeAutomationDateExceptions();

        Assert.Equal(playableHoliday, Assert.Single(baseline.ExcludedDates));
        Assert.Equal(quietHoliday, Assert.Single(settings.AutomationExcludedDates));
        Assert.Same(holidayOverride, ScheduleWindowEvaluator.SelectActiveWindow(settings.ScheduleWindows,
            new DateTimeOffset(2026, 12, 24, 10, 0, 0, TimeSpan.Zero), settings));
        Assert.Null(ScheduleWindowEvaluator.SelectActiveWindow(settings.ScheduleWindows,
            new DateTimeOffset(2026, 12, 25, 10, 0, 0, TimeSpan.Zero), settings));
    }

    private static ScheduleWindow DailyWindow() => new()
    {
        StartTime = new TimeOnly(9, 0),
        StopTime = new TimeOnly(12, 0),
        RecurrenceType = ScheduleRecurrenceType.Daily
    };
}
