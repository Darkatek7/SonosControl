using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SonosControlServiceStartSpeakerTests
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

        var serviceScope = new Mock<IServiceScope>();
        serviceScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(x => x.CreateScope()).Returns(serviceScope.Object);

        return scopeFactory.Object;
    }

    [Fact]
    public async Task StartSpeaker_WhenTodayIsNotActive_DoesNotStartPlayback()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var service = new SonosControlService(scopeFactory);

        var today = DateTime.Now.DayOfWeek;
        var inactiveDay = (DayOfWeek)(((int)today + 1) % 7);

        var settings = new SonosSettings
        {
            ActiveDays = new List<DayOfWeek> { inactiveDay },
            IP_Adress = "127.0.0.1"
        };
        var speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = settings.IP_Adress } };

        await InvokeStartSpeakerAsync(service, uow.Object, speakers, settings, null);

        sonosRepo.Verify(r => r.StartPlaying(It.IsAny<string>()), Times.Never);
        sonosRepo.Verify(r => r.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        sonosRepo.Verify(r => r.PlaySpotifyTrackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSpeaker_WithRandomStationSchedule_UsesSetTuneInStation()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var scopeFactory = CreateMockScopeFactory(uow.Object);
        var service = new SonosControlService(scopeFactory);

        var settings = new SonosSettings
        {
            Stations = new List<TuneInStation>
            {
                new() { Name = "Rock Antenne", Url = "https://stream.rockantenne.de/rockantenne/stream/mp3" },
                new() { Name = "Radio Paloma", Url = "https://www3.radiopaloma.de/RP-Hauptkanal.pls" }
            },
            IP_Adress = "127.0.0.1"
        };
        var speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = settings.IP_Adress } };

        var schedule = new DaySchedule
        {
            PlayRandomStation = true
        };

        await InvokeStartSpeakerAsync(service, uow.Object, speakers, settings, schedule);

        sonosRepo.Verify(r => r.SetTuneInStationAsync(settings.IP_Adress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.StartPlaying(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StartSpeaker_WhenSynced_PlaysOnAllSpeakers()
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
            IP_Adress = speaker1.IpAddress
        };

        var schedule = new DaySchedule
        {
            PlayRandomStation = true,
            IsSyncedPlayback = true
        };

        await InvokeStartSpeakerAsync(service, uow.Object, speakers, settings, schedule);

        // Verify Play action is called on BOTH speakers
        sonosRepo.Verify(r => r.SetTuneInStationAsync(speaker1.IpAddress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.SetTuneInStationAsync(speaker2.IpAddress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify Grouping is NOT called (based on new requirement)
        sonosRepo.Verify(r => r.CreateGroup(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);

        // Verify Ungrouping is called for all
        sonosRepo.Verify(r => r.UngroupSpeaker(speaker1.IpAddress, It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.UngroupSpeaker(speaker2.IpAddress, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WhenNotSynced_PlaysOnMasterOnly()
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
            IP_Adress = speaker1.IpAddress
        };

        var schedule = new DaySchedule
        {
            PlayRandomStation = true,
            IsSyncedPlayback = false
        };

        await InvokeStartSpeakerAsync(service, uow.Object, speakers, settings, schedule);

        // Verify Play action is called on Master ONLY
        sonosRepo.Verify(r => r.SetTuneInStationAsync(speaker1.IpAddress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.SetTuneInStationAsync(speaker2.IpAddress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Verify Ungrouping is called for Master
        sonosRepo.Verify(r => r.UngroupSpeaker(speaker1.IpAddress, It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.UngroupSpeaker(speaker2.IpAddress, It.IsAny<CancellationToken>()), Times.Never);
    }
}
