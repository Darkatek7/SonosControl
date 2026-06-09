using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class IndexPageUXTests
{
    [Fact]
    public void IndexPage_RendersEmptyStates_WhenListsAreEmpty()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx, new List<TuneInStation>(), new List<SpotifyObject>(), new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-qa='home-operations-dashboard']"));
            Assert.Contains("Library", cut.Markup);
            Assert.DoesNotContain("Quick Sources", cut.Markup);
            Assert.Contains("No saved stations.", cut.Markup);
            Assert.Empty(cut.FindAll(".spotify-library"));
        });
    }

    [Fact]
    public void IndexPage_LibraryListsAllSourcesInTabs_AndSearchesActiveTab()
    {
        using var ctx = new TestContext();

        var stations = Enumerable.Range(1, 8)
            .Select(index => new TuneInStation { Name = $"Station {index}", Url = $"http://station-{index}.example/stream" })
            .ToList();
        var tracks = new List<SpotifyObject> { new SpotifyObject { Name = "Focus Track", Url = "spotify:track:focus" } };
        var videos = new List<YouTubeObject> { new YouTubeObject { Name = "Live Set", Url = "https://www.youtube.com/watch?v=abc123xyz00" } };
        var collections = new List<YouTubeMusicObject> { new YouTubeMusicObject { Name = "Workout Collection", Url = "https://music.youtube.com/playlist?list=workout" } };

        using var resources = ConfigureServices(ctx, stations, tracks, collections, youTubeVideos: videos);

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Library", cut.Markup);
            Assert.Contains("Station 1", cut.Markup);
            Assert.Contains("Station 8", cut.Markup);
            Assert.DoesNotContain("Focus Track", cut.Markup);
            Assert.NotEmpty(cut.FindAll("button[aria-label^='Play Station 8']"));
        });

        cut.Find("input[aria-label='Search active library tab']").Input("Station 8");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Station 8", cut.Markup);
            Assert.DoesNotContain("Station 1", cut.Markup);
        });

        cut.Find("#home-library-tab-spotify").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Focus Track", cut.Markup);
            Assert.DoesNotContain("Station 8", cut.Markup);
            Assert.Empty(cut.FindAll("button[aria-label^='Play Station']"));
            Assert.NotEmpty(cut.FindAll("button[aria-label^='Play Focus Track']"));
        });

        cut.Find("#home-library-tab-youtube").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Live Set", cut.Markup);
            Assert.DoesNotContain("Focus Track", cut.Markup);
            Assert.NotEmpty(cut.FindAll("button[aria-label^='Play Live Set']"));
        });

        cut.Find("#home-library-tab-youtube-music").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Workout Collection", cut.Markup);
            Assert.NotEmpty(cut.FindAll("button[aria-label^='Play Workout Collection']"));
        });
    }

    [Fact]
    public void IndexPage_RendersGroupedSpeakers_Correctly()
    {
        using var ctx = new TestContext();

        // Setup speakers - MUST use valid Hex characters for Regex matching in component
        var master = new SonosSpeaker { Name = "Living Room", IpAddress = "192.168.1.10", Uuid = "uuid:RINCON_1234567890ABCDEF" };
        var slave = new SonosSpeaker { Name = "Kitchen", IpAddress = "192.168.1.11", Uuid = "uuid:RINCON_0000000000000000" };
        var speakers = new List<SonosSpeaker> { master, slave };

        var settings = new SonosSettings
        {
            IP_Adress = master.IpAddress,
            Speakers = speakers
        };

        // Mocks
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(r => r.GetVolume(It.IsAny<string>())).ReturnsAsync(20);
        connectorRepo.Setup(r => r.IsPlaying(It.IsAny<string>())).ReturnsAsync(true);
        connectorRepo.Setup(r => r.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo { Title = "Test Track", Artist = "Test Artist" });
        connectorRepo.Setup(r => r.GetTrackProgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TimeSpan.Zero, TimeSpan.Zero));
        connectorRepo.Setup(r => r.SetVolume(It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);

        // Grouping setup: Master playing stream, Slave playing group stream
        connectorRepo.Setup(r => r.GetCurrentStationAsync(master.IpAddress, It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://somestream.com");
        connectorRepo.Setup(r => r.GetCurrentStationAsync(slave.IpAddress, It.IsAny<CancellationToken>()))
            .ReturnsAsync($"x-rincon-group:{master.Uuid}"); // Slave points to Master

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(u => u.ISonosConnectorRepo).Returns(connectorRepo.Object);
        unitOfWork.SetupGet(u => u.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);
        ctx.Services.AddSingleton<INotificationService>(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton<IMetricsCollector>(new MetricsCollector());
        ctx.Services.AddSingleton<IDeviceHealthSnapshotStore>(new DeviceHealthSnapshotStore());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(configuration);

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Living Room", cut.Markup);
            Assert.Contains("Stereo/group: Kitchen", cut.Markup);
            Assert.DoesNotContain("Linked to Living Room", cut.Markup);
            Assert.DoesNotContain("↳", cut.Markup);
            Assert.Single(cut.FindAll(".speaker-status-item"));
            Assert.Contains("speaker-status-badge", cut.Find(".speaker-status-badge").ClassList);
        });
    }

    [Fact]
    public void IndexPage_CollapsesGroupedSpeaker_WhenChildReportsPlaying()
    {
        using var ctx = new TestContext();

        var master = new SonosSpeaker { Name = "Office Pair", IpAddress = "192.168.1.20", Uuid = "uuid:RINCON_ABCDEF1234567890" };
        var slave = new SonosSpeaker { Name = "Office Right", IpAddress = "192.168.1.21", Uuid = "uuid:RINCON_0000000000000001" };
        var settings = new SonosSettings
        {
            IP_Adress = master.IpAddress,
            Speakers = new List<SonosSpeaker> { master, slave }
        };

        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(r => r.GetVolume(It.IsAny<string>())).ReturnsAsync(20);
        connectorRepo.Setup(r => r.IsPlaying(master.IpAddress)).ReturnsAsync(false);
        connectorRepo.Setup(r => r.IsPlaying(slave.IpAddress)).ReturnsAsync(true);
        connectorRepo.Setup(r => r.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo());
        connectorRepo.Setup(r => r.GetTrackProgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TimeSpan.Zero, TimeSpan.Zero));
        connectorRepo.Setup(r => r.SetVolume(It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(r => r.GetCurrentStationAsync(master.IpAddress, It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
        connectorRepo.Setup(r => r.GetCurrentStationAsync(slave.IpAddress, It.IsAny<CancellationToken>()))
            .ReturnsAsync($"x-rincon-group:{master.Uuid}");

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(u => u.ISonosConnectorRepo).Returns(connectorRepo.Object);
        unitOfWork.SetupGet(u => u.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);
        ctx.Services.AddSingleton<INotificationService>(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton<IMetricsCollector>(new MetricsCollector());
        ctx.Services.AddSingleton<IDeviceHealthSnapshotStore>(new DeviceHealthSnapshotStore());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(configuration);

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".speaker-status-item"));
            Assert.Contains("Office Pair", cut.Markup);
            Assert.Contains("Stereo/group: Office Right", cut.Markup);
            Assert.Contains("Paused", cut.Find(".speaker-status-badge").TextContent);
            Assert.DoesNotContain("Linked to Office Pair", cut.Markup);
        });
    }

    [Fact]
    public void IndexPage_HasAccessibleLibraryTabs_AndAddButtons()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx, new List<TuneInStation>(), new List<SpotifyObject>(), new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("tab", cut.Find("#home-library-tab-stations").GetAttribute("role"));
            Assert.Equal("true", cut.Find("#home-library-tab-stations").GetAttribute("aria-selected"));
            Assert.Equal("tab", cut.Find("#home-library-tab-spotify").GetAttribute("role"));
            Assert.Equal("tab", cut.Find("#home-library-tab-youtube").GetAttribute("role"));
            Assert.Equal("tab", cut.Find("#home-library-tab-youtube-music").GetAttribute("role"));
            Assert.NotNull(cut.Find("input[aria-label='Search active library tab']"));
            Assert.NotNull(cut.Find("button[aria-label='Add Station']"));
            Assert.NotNull(cut.Find("button[aria-label='Add Spotify Track']"));
            Assert.NotNull(cut.Find("button[aria-label='Add YouTube Video']"));
            Assert.NotNull(cut.Find("button[aria-label='Add YouTube Music Collection']"));
        });
    }

    [Fact]
    public void IndexPage_RendersDashboardOnlyHome_WithLibraryAutomationHealthAndActivity()
    {
        using var ctx = new TestContext();

        var scene = new Scene
        {
            Id = "scene-morning",
            Name = "Morning Radio",
            SourceType = SceneSourceType.Station,
            SpeakerIps = new List<string> { "1.2.3.4", "1.2.3.5" }
        };
        var settings = new SonosSettings
        {
            IP_Adress = "1.2.3.4",
            Volume = 38,
            MaxVolume = 80,
            Stations = new List<TuneInStation>
            {
                new() { Name = "ORF Radio Wien", Url = "http://orf.example/stream" },
                new() { Name = "Cafe del Mar", Url = "http://cafe.example/stream" }
            },
            Speakers = new List<SonosSpeaker>
            {
                new() { IpAddress = "1.2.3.4", Name = "Kitchen" },
                new() { IpAddress = "1.2.3.5", Name = "Living Room" },
                new() { IpAddress = "1.2.3.6", Name = "Bedroom" }
            },
            Scenes = new List<Scene> { scene },
            ScheduleWindows = new List<ScheduleWindow>
            {
                new()
                {
                    Name = "Morning Radio",
                    SceneId = scene.Id,
                    RecurrenceType = ScheduleRecurrenceType.Daily,
                    StartTime = new TimeOnly(0, 0),
                    StopTime = new TimeOnly(23, 59),
                    Priority = 10,
                    FadeOutSeconds = 20
                },
                new()
                {
                    Name = "Lunch Radio",
                    SceneId = scene.Id,
                    RecurrenceType = ScheduleRecurrenceType.Daily,
                    StartTime = new TimeOnly(23, 59),
                    StopTime = new TimeOnly(23, 59),
                    Priority = 20
                },
                new()
                {
                    Name = "Late Radio",
                    SceneId = scene.Id,
                    RecurrenceType = ScheduleRecurrenceType.Daily,
                    StartTime = new TimeOnly(23, 59),
                    StopTime = new TimeOnly(23, 59),
                    Priority = 30
                }
            }
        };

        var healthStore = new DeviceHealthSnapshotStore();
        healthStore.Replace(new[]
        {
            new DeviceHealthStatus { SpeakerIp = "1.2.3.4", SpeakerName = "Kitchen", IsOnline = true, LastLatencyMs = 12 },
            new DeviceHealthStatus { SpeakerIp = "1.2.3.5", SpeakerName = "Living Room", IsOnline = true, LastLatencyMs = 18 },
            new DeviceHealthStatus { SpeakerIp = "1.2.3.6", SpeakerName = "Bedroom", IsOnline = false, LastError = "Timeout" }
        });

        using var resources = ConfigureServices(
            ctx,
            settings.Stations,
            settings.SpotifyTracks,
            settings.YouTubeMusicCollections,
            settings,
            healthStore);

        resources.DbContext.Logs.Add(new LogEntry
        {
            Action = "SceneApplied",
            Details = "Morning Radio scene applied by scheduler",
            PerformedBy = "System",
            Timestamp = DateTime.UtcNow
        });
        resources.DbContext.SaveChanges();

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-qa='home-operations-dashboard']"));
            Assert.Empty(cut.FindAll(".spotify-library"));
            Assert.Empty(cut.FindAll(".spotify-home-context"));
            Assert.Empty(cut.FindAll(".spotify-room-picker"));
            Assert.Contains("Today at a glance", cut.Markup);
            Assert.Contains("Morning Radio", cut.Markup);
            Assert.Contains("Scene: Morning Radio", cut.Markup);
            Assert.Contains("Device Health", cut.Markup);
            Assert.Contains("Library", cut.Markup);
            Assert.DoesNotContain("Quick Sources", cut.Markup);
            Assert.Contains("Online", cut.Markup);
            Assert.Contains("Offline", cut.Markup);
            Assert.Contains("Recent Activity", cut.Markup);
            Assert.Contains("Morning Radio scene applied by scheduler", cut.Markup);
            Assert.Single(cut.FindAll(".home-ops-timeline__row"));
            Assert.NotEmpty(cut.FindAll("button[aria-label='Add Station']"));
            var buttonLabels = string.Join("|", cut.FindAll("button").Select(button => button.GetAttribute("aria-label") ?? button.TextContent.Trim()));
            Assert.Contains("Play ORF Radio Wien", buttonLabels);
            Assert.Contains("home-ops-dashboard", cut.Find("[data-qa='home-operations-dashboard']").ClassList);
        });
    }

    [Fact]
    public void IndexPage_RendersRecentActivityDetailsWithoutTruncation()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx, new List<TuneInStation>(), new List<SpotifyObject>(), new List<YouTubeMusicObject>());

        var longDetails = "Window 'Party Time Wochenende' triggered scene '07a0464f9bb045b181057580af9cf723' for the configured weekend speakers without truncating the operational trail.";
        resources.DbContext.Logs.Add(new LogEntry
        {
            Action = "ScheduleTriggered",
            Details = longDetails,
            PerformedBy = "System",
            Timestamp = DateTime.UtcNow
        });
        resources.DbContext.SaveChanges();

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(longDetails, cut.Markup);
            Assert.Contains("home-ops-activity__details", cut.Find(".home-ops-activity__row strong").ClassList);
        });
    }

    [Fact]
    public void IndexPage_ResolvesSavedStationNameForNowPlaying()
    {
        using var ctx = new TestContext();
        var station = new TuneInStation { Name = "Breakz Radio", Url = "https://breakz-2012-high.rautemusik.fm/stream/mp3" };
        using var resources = ConfigureServices(
            ctx,
            new List<TuneInStation> { station },
            new List<SpotifyObject>(),
            new List<YouTubeMusicObject>(),
            connectorCurrentStation: $"x-rincon-mp3radio://{station.Url}/?ref=rb-djclubcharts");

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Breakz Radio ·", cut.Markup);
            Assert.DoesNotContain("breakz-2012-high.rautemusik.fm/?ref=rb-djclubcharts", cut.Markup);
        });
    }

    [Fact]
    public void IndexPage_PlayMediaItem_UsesSelectedSpeakerEvenWhenPersistedSpeakerDiffers()
    {
        using var ctx = new TestContext();

        var settings = new SonosSettings
        {
            IP_Adress = "1.2.3.4",
            Volume = 20,
            MaxVolume = 80,
            YouTubeCollections = new List<YouTubeObject>
            {
                new() { Name = "Live Set", Url = "https://www.youtube.com/watch?v=abc123xyz00" }
            },
            Speakers = new List<SonosSpeaker>
            {
                new() { IpAddress = "1.2.3.4", Name = "Kitchen" },
                new() { IpAddress = "1.2.3.5", Name = "Living Room" }
            }
        };

        using var resources = ConfigureServices(
            ctx,
            new List<TuneInStation>(),
            new List<SpotifyObject>(),
            new List<YouTubeMusicObject>(),
            settings,
            youTubeVideos: settings.YouTubeCollections);

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("#home-library-tab-youtube"));
        });

        cut.Find("#home-library-tab-youtube").Click();
        cut.WaitForAssertion(() => Assert.Contains("Live Set", cut.Markup));

        var selectedSpeakerField = typeof(IndexPage).GetField("_selectedSpeakerIp", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(selectedSpeakerField);
        selectedSpeakerField!.SetValue(cut.Instance, "1.2.3.5");

        cut.Find("button[aria-label='Play Live Set']").Click();

        resources.ConnectorRepo.Verify(repo => repo.PlayYouTubeAudioAsync(
            "1.2.3.5",
            "https://www.youtube.com/watch?v=abc123xyz00",
            It.IsAny<CancellationToken>()), Times.Once);
        resources.ConnectorRepo.Verify(repo => repo.StartPlaying("1.2.3.5"), Times.Once);
    }

    private sealed class TestResources : IDisposable
    {
        public ApplicationDbContext DbContext { get; }
        public Mock<ISonosConnectorRepo> ConnectorRepo { get; }

        public TestResources(ApplicationDbContext dbContext, Mock<ISonosConnectorRepo> connectorRepo)
        {
            DbContext = dbContext;
            ConnectorRepo = connectorRepo;
        }

        public void Dispose()
        {
            DbContext.Dispose();
        }
    }

    private static TestResources ConfigureServices(
        TestContext ctx,
        List<TuneInStation> stations,
        List<SpotifyObject> tracks,
        List<YouTubeMusicObject> collections,
        SonosSettings? settingsOverride = null,
        IDeviceHealthSnapshotStore? healthStore = null,
        string? connectorCurrentStation = null,
        List<YouTubeObject>? youTubeVideos = null)
    {
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        var settings = settingsOverride ?? new SonosSettings
        {
            IP_Adress = "1.2.3.4",
            Volume = 20,
            MaxVolume = 80,
            Stations = stations,
            SpotifyTracks = tracks,
            YouTubeCollections = youTubeVideos ?? new List<YouTubeObject>(),
            YouTubeMusicCollections = collections,
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4", Name = "Living Room" } }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(r => r.GetVolume(It.IsAny<string>())).ReturnsAsync(settings.Volume);
        connectorRepo.Setup(r => r.IsPlaying(It.IsAny<string>())).ReturnsAsync(false);
        connectorRepo.Setup(r => r.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(connectorCurrentStation ?? "");
        connectorRepo.Setup(r => r.GetCurrentTrackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
        connectorRepo.Setup(r => r.GetTrackProgressAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TimeSpan.Zero, TimeSpan.Zero));
        connectorRepo.Setup(r => r.SetVolume(It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(u => u.ISonosConnectorRepo).Returns(connectorRepo.Object);
        unitOfWork.SetupGet(u => u.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);
        ctx.Services.AddSingleton<INotificationService>(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton<IMetricsCollector>(new MetricsCollector());
        ctx.Services.AddSingleton(healthStore ?? new DeviceHealthSnapshotStore());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(configuration);

        return new TestResources(dbContext, connectorRepo);
    }
}
