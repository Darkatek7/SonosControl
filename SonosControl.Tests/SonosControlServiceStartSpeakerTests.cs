using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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

    private SonosControlService CreateService(IUnitOfWork uow)
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        return new SonosControlService(scopeFactory.Object);
    }

    [Fact]
    public async Task StartSpeaker_WhenTodayIsNotActive_DoesNotStartPlayback()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = CreateService(uow.Object);

        var today = DateTime.Now.DayOfWeek;
        var inactiveDay = (DayOfWeek)(((int)today + 1) % 7);

        var settings = new SonosSettings
        {
            ActiveDays = new List<DayOfWeek> { inactiveDay },
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4" } }
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

        var service = CreateService(uow.Object);

        var settings = new SonosSettings
        {
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4" } },
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

        sonosRepo.Verify(r => r.SetTuneInStationAsync(settings.Speakers.First().IpAddress, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        sonosRepo.Verify(r => r.StartPlaying(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StartSpeaker_WhenRandomStationHasNoStations_FallsBackToStartPlaying()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = CreateService(uow.Object);

        var settings = new SonosSettings
        {
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4" } },
            Stations = new List<TuneInStation>(),
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek }
        };

        var schedule = new DaySchedule
        {
            PlayRandomStation = true
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, schedule);

        sonosRepo.Verify(r => r.StartPlaying(settings.Speakers.First().IpAddress), Times.Once);
        sonosRepo.Verify(r => r.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSpeaker_WithRandomYouTubeSchedule_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = CreateService(uow.Object);

        var settings = new SonosSettings
        {
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4" } },
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

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.Speakers.First().IpAddress, It.IsAny<string>(), settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithYouTubeUrlSchedule_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = CreateService(uow.Object);

        var settings = new SonosSettings
        {
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4" } }
        };
        var schedule = new DaySchedule
        {
            YouTubeMusicUrl = "https://music.youtube.com/watch?v=hijklm"
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, schedule);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.Speakers.First().IpAddress, schedule.YouTubeMusicUrl, settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithAutoPlayRandomYouTube_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = CreateService(uow.Object);

        var settings = new SonosSettings
        {
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4" } },
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek },
            AutoPlayRandomYouTubeMusic = true,
            YouTubeMusicCollections = new List<YouTubeMusicObject>
            {
                new() { Name = "Morning", Url = "https://music.youtube.com/watch?v=def456" }
            }
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, null);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.Speakers.First().IpAddress, It.IsAny<string>(), settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSpeaker_WithAutoPlayYouTubeUrl_UsesConnector()
    {
        var sonosRepo = new Mock<ISonosConnectorRepo>();
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(sonosRepo.Object);

        var service = CreateService(uow.Object);

        var settings = new SonosSettings
        {
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4" } },
            ActiveDays = new List<DayOfWeek> { DateTime.Now.DayOfWeek },
            AutoPlayYouTubeMusicUrl = "https://music.youtube.com/watch?v=xyz789"
        };

        await InvokeStartSpeakerAsync(service, uow.Object, settings.Speakers, settings, null);

        sonosRepo.Verify(r => r.PlayYouTubeMusicTrackAsync(settings.Speakers.First().IpAddress, settings.AutoPlayYouTubeMusicUrl, settings.AutoPlayStationUrl, It.IsAny<CancellationToken>()), Times.Once);
    }
}
