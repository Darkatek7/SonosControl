using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SonosControlServiceSchedulerPriorityTests
{
    [Fact]
    public async Task StartAndStopSpeaker_WorkEvenWhenScheduleWindowsExist()
    {
        var settings = new SonosSettings
        {
            IP_Adress = "10.0.0.1",
            AutoPlayStationUrl = "stream.example.com/live",
            ActiveDays = Enum.GetValues<DayOfWeek>().ToList(),
            Speakers = new List<SonosSpeaker> { new() { IpAddress = "10.0.0.1", Name = "Kitchen" } },
            ScheduleWindows = new List<ScheduleWindow>
            {
                new()
                {
                    Id = "window-a",
                    Name = "Window A",
                    IsEnabled = true,
                    StartTime = new TimeOnly(0, 0),
                    StopTime = new TimeOnly(23, 59),
                    RecurrenceType = ScheduleRecurrenceType.Daily
                }
            }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(repo => repo.GetSpeakerUUID("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync("uuid:10.0.0.1");
        connectorRepo.Setup(repo => repo.SetTuneInStationAsync("10.0.0.1", "stream.example.com/live", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.UngroupSpeaker("10.0.0.1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.SetSpeakerVolume("10.0.0.1", settings.Volume, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.StopPlaying("10.0.0.1"))
            .Returns(Task.CompletedTask);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo.Object);
        uow.SetupGet(value => value.ISonosConnectorRepo).Returns(connectorRepo.Object);
        uow.SetupGet(value => value.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        var service = new SonosControlService(CreateMockScopeFactory(uow.Object));
        var speakers = settings.Speakers.ToList();

        await InvokeStartSpeakerAsync(service, uow.Object, speakers, settings, null);
        await InvokeStopSpeakerAsync(service, uow.Object, speakers, DateTimeOffset.Now, null);

        connectorRepo.Verify(repo => repo.SetTuneInStationAsync("10.0.0.1", "stream.example.com/live", It.IsAny<CancellationToken>()), Times.Once);
        connectorRepo.Verify(repo => repo.StopPlaying("10.0.0.1"), Times.Once);
    }

    private static Task InvokeStartSpeakerAsync(
        SonosControlService service,
        IUnitOfWork uow,
        IEnumerable<SonosSpeaker> speakers,
        SonosSettings settings,
        DaySchedule? schedule)
    {
        var method = typeof(SonosControlService).GetMethod("StartSpeaker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, new object[] { uow, speakers, settings, schedule, CancellationToken.None })!;
    }

    private static Task InvokeStopSpeakerAsync(
        SonosControlService service,
        IUnitOfWork uow,
        IEnumerable<SonosSpeaker> speakers,
        DateTimeOffset stopDateTime,
        DaySchedule? schedule)
    {
        var method = typeof(SonosControlService).GetMethod("StopSpeaker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, new object[] { uow, speakers, stopDateTime, schedule, CancellationToken.None })!;
    }

    private static IServiceScopeFactory CreateMockScopeFactory(IUnitOfWork uow)
    {
        var notificationService = new Mock<INotificationService>();
        notificationService.Setup(service => service.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(IUnitOfWork))).Returns(uow);
        serviceProvider.Setup(x => x.GetService(typeof(INotificationService))).Returns(notificationService.Object);

        var serviceScope = new Mock<IServiceScope>();
        serviceScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(x => x.CreateScope()).Returns(serviceScope.Object);

        return scopeFactory.Object;
    }
}
