using System.Diagnostics;
using System.Reflection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SonosControlServiceTests
{
    private static Task<(SonosSettings settings, DaySchedule? schedule)> InvokeWait(SonosControlService svc, CancellationToken token)
    {
        var method = typeof(SonosControlService).GetMethod("WaitUntilStartTime", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<(SonosSettings, DaySchedule?)>)method.Invoke(svc, new object[] { token })!;
        return task;
    }

    [Fact]
    public async Task WaitUntilStartTime_WaitsUntilSettingStart()
    {
        var settings = new SonosSettings
        {
            StartTime = TimeOnly.FromDateTime(DateTime.Now.AddMilliseconds(500))
        };
        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var svc = new SonosControlService(uow.Object);

        var sw = Stopwatch.StartNew();
        var result = await InvokeWait(svc, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 300, $"Elapsed {sw.ElapsedMilliseconds}ms");
        Assert.Same(settings, result.settings);
        Assert.Null(result.schedule);
    }

    [Fact]
    public async Task WaitUntilStartTime_UsesDailySchedule()
    {
        var schedule = new DaySchedule
        {
            StartTime = TimeOnly.FromDateTime(DateTime.Now.AddMilliseconds(200))
        };
        var settings = new SonosSettings
        {
            StartTime = TimeOnly.FromDateTime(DateTime.Now.AddHours(1)),
            DailySchedules = new Dictionary<DayOfWeek, DaySchedule>
            {
                [DateTime.Now.DayOfWeek] = schedule
            }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);

        var svc = new SonosControlService(uow.Object);

        var sw = Stopwatch.StartNew();
        var result = await InvokeWait(svc, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 150, $"Elapsed {sw.ElapsedMilliseconds}ms");
        Assert.Same(schedule, result.schedule);
    }
}
