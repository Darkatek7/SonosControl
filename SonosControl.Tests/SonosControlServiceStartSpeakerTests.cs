using System.Reflection;
using System.Threading;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using SonosControl.Tests.Mocks;
using Xunit;

namespace SonosControl.Tests;

public class SonosControlServiceStartSpeakerTests
{
    private static Task InvokeStartSpeakerAsync(SonosControlService service, IEnumerable<SonosSpeaker> speakers, SonosSettings settings, DaySchedule? schedule)
    {
        var method = typeof(SonosControlService).GetMethod("StartSpeaker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // The method signature is (IUnitOfWork uow, IEnumerable<SonosSpeaker> speakers, SonosSettings settings, DaySchedule? schedule, CancellationToken cancellationToken)
        // We need to pass UOW somehow or invoke it with null if we can't access it, but the method uses UOW.
        // Wait, the service stores UOW? No, it's passed in.

        // Actually, StartSpeaker in the service takes (IUnitOfWork uow, ...).
        // But here we are invoking it via reflection on the instance.
        // The tests previously assumed a constructor injection of UOW, but now it's ScopeFactory.
        // We need to adjust how we test StartSpeaker.

        // Since StartSpeaker is private and we are testing it, we need to pass the arguments.
        // But we don't have easy access to the UOW instance used inside ExecuteAsync.
        // However, we can just pass a mocked UOW to the method via reflection.

        return (Task)method.Invoke(service, new object[] { null!, speakers, settings, schedule, CancellationToken.None })!;
    }

    // Better helper that accepts UOW
    private static Task InvokeStartSpeakerAsync(SonosControlService service, IUnitOfWork uow, IEnumerable<SonosSpeaker> speakers, SonosSettings settings, DaySchedule? schedule)
    {
        var method = typeof(SonosControlService).GetMethod("StartSpeaker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, new object[] { uow, speakers, settings, schedule, CancellationToken.None })!;
    }

    [Fact]
    public async Task StartSpeaker_WhenTodayIsNotActive_DoesNotStartPlayback()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);
        var scopeFactory = MockHelper.CreateScopeFactory(uow.Object);

        var service = new SonosControlService(scopeFactory);

        var today = DateTime.Now.DayOfWeek;
        var inactiveDay = (DayOfWeek)(((int)today + 1) % 7);

        var settings = new SonosSettings
        {
            ActiveDays = new List<DayOfWeek> { inactiveDay },
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "192.168.1.1" } }
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, null);

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
        var scopeFactory = MockHelper.CreateScopeFactory(uow.Object);

        var service = new SonosControlService(scopeFactory);

        var settings = new SonosSettings
        {
            IP_Adress = "127.0.0.1",
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "127.0.0.1" } },
            Stations = new List<TuneInStation>
            {
                new() { Name = "Rock Antenne", Url = "https://stream.rockantenne.de/rockantenne/stream/mp3" },
                new() { Name = "Radio Paloma", Url = "https://www3.radiopaloma.de/RP-Hauptkanal.pls" }
            }
        };

        var schedule = new DaySchedule
        {
            PlayRandomStation = true
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, schedule);

        sonosRepo.Verify(r => r.SetTuneInStationAsync(settings.IP_Adress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.StartPlaying(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StartSpeaker_WhenRandomStationHasNoStations_FallsBackToStartPlaying()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);
        var scopeFactory = MockHelper.CreateScopeFactory(uow.Object);

        var service = new SonosControlService(scopeFactory);

        var settings = new SonosSettings
        {
            IP_Adress = "127.0.0.1",
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "127.0.0.1" } },
            Stations = new List<TuneInStation>(),
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek }
        };

        var schedule = new DaySchedule
        {
            PlayRandomStation = true
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, schedule);

        sonosRepo.Verify(r => r.StartPlaying(settings.IP_Adress), Times.Once);
        sonosRepo.Verify(r => r.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSpeaker_WithRandomYouTubeSchedule_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);
        var scopeFactory = MockHelper.CreateScopeFactory(uow.Object);

        var service = new SonosControlService(scopeFactory);

        var settings = new SonosSettings
        {
            IP_Adress = "127.0.0.1",
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "127.0.0.1" } },
            YouTubeMusicCollections = new List<YouTubeMusicObject>
            {
                new() { Name = "Focus", Url = "https://music.youtube.com/watch?v=abc123" },
                new() { Name = "Mix", Url = "https://music.youtube.com/playlist?list=LM" }
            }
        };

        var schedule = new DaySchedule
        {
            PlayRandomYouTubeMusic = true
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, schedule);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.IP_Adress, It.IsAny<string>(), settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithYouTubeUrlSchedule_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);
        var scopeFactory = MockHelper.CreateScopeFactory(uow.Object);

        var service = new SonosControlService(scopeFactory);

        var settings = new SonosSettings
        {
             IP_Adress = "127.0.0.1",
             Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "127.0.0.1" } }
        };
        var schedule = new DaySchedule
        {
            YouTubeMusicUrl = "https://music.youtube.com/watch?v=hijklm"
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, schedule);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.IP_Adress, schedule.YouTubeMusicUrl, settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithAutoPlayRandomYouTube_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);
        var scopeFactory = MockHelper.CreateScopeFactory(uow.Object);

        var service = new SonosControlService(scopeFactory);

        var settings = new SonosSettings
        {
            IP_Adress = "127.0.0.1",
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "127.0.0.1" } },
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek },
            AutoPlayRandomYouTubeMusic = true,
            YouTubeMusicCollections = new List<YouTubeMusicObject>
            {
                new() { Name = "Morning", Url = "https://music.youtube.com/watch?v=def456" }
            }
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, null);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.IP_Adress, It.IsAny<string>(), settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithAutoPlayYouTubeUrl_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);
        var scopeFactory = MockHelper.CreateScopeFactory(uow.Object);

        var service = new SonosControlService(scopeFactory);

        var settings = new SonosSettings
        {
            IP_Adress = "127.0.0.1",
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "127.0.0.1" } },
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek },
            AutoPlayYouTubeMusicUrl = "https://music.youtube.com/watch?v=xyz789"
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, null);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.IP_Adress, settings.AutoPlayYouTubeMusicUrl, settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }
}
