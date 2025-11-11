using System.Reflection;
using System.Threading;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SonosControlServiceStartSpeakerTests
{
    private static Task InvokeStartSpeakerAsync(SonosControlService service, string ip, SonosSettings settings, DaySchedule? schedule)
    {
        var method = typeof(SonosControlService).GetMethod("StartSpeaker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, new object[] { ip, settings, schedule })!;
    }

    [Fact]
    public async Task StartSpeaker_WhenTodayIsNotActive_DoesNotStartPlayback()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = new SonosControlService(uow.Object);

        var today = DateTime.Now.DayOfWeek;
        var inactiveDay = (DayOfWeek)(((int)today + 1) % 7);

        var settings = new SonosSettings
        {
            ActiveDays = new List<DayOfWeek> { inactiveDay }
        };

        await InvokeStartSpeakerAsync(service, settings.IP_Adress, settings, null);

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

        var service = new SonosControlService(uow.Object);

        var settings = new SonosSettings
        {
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

        await InvokeStartSpeakerAsync(service, settings.IP_Adress, settings, schedule);

        sonosRepo.Verify(r => r.SetTuneInStationAsync(settings.IP_Adress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.StartPlaying(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StartSpeaker_WhenRandomStationHasNoStations_FallsBackToStartPlaying()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = new SonosControlService(uow.Object);

        var settings = new SonosSettings
        {
            Stations = new List<TuneInStation>(),
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek }
        };

        var schedule = new DaySchedule
        {
            PlayRandomStation = true
        };

        await InvokeStartSpeakerAsync(service, settings.IP_Adress, settings, schedule);

        sonosRepo.Verify(r => r.StartPlaying(settings.IP_Adress), Times.Once);
        sonosRepo.Verify(r => r.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSpeaker_WithRandomYouTubeSchedule_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = new SonosControlService(uow.Object);

        var settings = new SonosSettings
        {
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

        await InvokeStartSpeakerAsync(service, settings.IP_Adress, settings, schedule);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.IP_Adress, It.IsAny<string>(), settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithYouTubeUrlSchedule_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = new SonosControlService(uow.Object);

        var settings = new SonosSettings();
        var schedule = new DaySchedule
        {
            YouTubeMusicUrl = "https://music.youtube.com/watch?v=hijklm"
        };

        await InvokeStartSpeakerAsync(service, settings.IP_Adress, settings, schedule);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.IP_Adress, schedule.YouTubeMusicUrl, settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithAutoPlayRandomYouTube_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = new SonosControlService(uow.Object);

        var settings = new SonosSettings
        {
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek },
            AutoPlayRandomYouTubeMusic = true,
            YouTubeMusicCollections = new List<YouTubeMusicObject>
            {
                new() { Name = "Morning", Url = "https://music.youtube.com/watch?v=def456" }
            }
        };

        await InvokeStartSpeakerAsync(service, settings.IP_Adress, settings, null);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.IP_Adress, It.IsAny<string>(), settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithAutoPlayYouTubeUrl_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = new SonosControlService(uow.Object);

        var settings = new SonosSettings
        {
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek },
            AutoPlayYouTubeMusicUrl = "https://music.youtube.com/watch?v=xyz789"
        };

        await InvokeStartSpeakerAsync(service, settings.IP_Adress, settings, null);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.IP_Adress, settings.AutoPlayYouTubeMusicUrl, settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }
}
