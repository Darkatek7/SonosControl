using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SonosControlServiceFallbackTests
{
    private static Task InvokeStartSpeakerAsync(SonosControlService service, IUnitOfWork uow, IEnumerable<SonosSpeaker> speakers, SonosSettings settings, DaySchedule? schedule)
    {
        var method = typeof(SonosControlService).GetMethod("StartSpeaker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, new object[] { uow, speakers, settings, schedule, CancellationToken.None })!;
    }

    private IServiceScopeFactory CreateMockScopeFactory(IUnitOfWork uow)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(IUnitOfWork))).Returns(uow);
        serviceProvider.Setup(x => x.GetService(typeof(INotificationService))).Returns(Mock.Of<INotificationService>());

        var serviceScope = new Mock<IServiceScope>();
        serviceScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(x => x.CreateScope()).Returns(serviceScope.Object);

        return scopeFactory.Object;
    }

    [Fact]
    public async Task StartSpeaker_WhenGroupingFails_PlaysOnAllSpeakers()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var service = new SonosControlService(scopeFactory);

        var speaker1 = new SonosSpeaker { IpAddress = "192.168.1.101" };
        var speaker2 = new SonosSpeaker { IpAddress = "192.168.1.102" };
        var speakers = new List<SonosSpeaker> { speaker1, speaker2 };

        var settings = new SonosSettings
        {
            Stations = new List<TuneInStation>
            {
                new() { Name = "Rock Antenne", Url = "https://stream.rockantenne.de/rockantenne/stream/mp3" }
            },
            IP_Adress = speaker1.IpAddress,
            Volume = 20
        };

        var schedule = new DaySchedule
        {
            PlayRandomStation = true,
            IsSyncedPlayback = true
        };

        // Mock CreateGroup to return false (failure)
        sonosRepo.Setup(r => r.CreateGroup(speaker1.IpAddress, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(false);

        // Mock Ungroup (called for cleanup)
        sonosRepo.Setup(r => r.UngroupSpeaker(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        // Mock SetSpeakerVolume
        sonosRepo.Setup(r => r.SetSpeakerVolume(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        // Mock SetTuneInStationAsync
        sonosRepo.Setup(r => r.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        // Mock StartPlaying (needed for fallback legacy sync usually, but here playAction calls SetTuneInStationAsync or StartPlaying)
        // Wait, playAction for PlayRandomStation calls SetTuneInStationAsync.
        // If url is null, it calls StartPlaying. Here we have stations, so it calls SetTuneInStationAsync.

        await InvokeStartSpeakerAsync(service, uow.Object, speakers, settings, schedule);

        // Verify CreateGroup was called
        sonosRepo.Verify(r => r.CreateGroup(speaker1.IpAddress, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify Play action is called on BOTH speakers because of fallback
        // Currently (before fix), this test should FAIL because it will only call on speaker1.
        sonosRepo.Verify(r => r.SetTuneInStationAsync(speaker1.IpAddress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.SetTuneInStationAsync(speaker2.IpAddress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
