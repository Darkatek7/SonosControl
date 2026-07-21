using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Web;
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
    public void IndexPage_RendersEverydayHierarchy_WithoutEmbeddedPlayer()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx, new List<TuneInStation>(), new List<SpotifyObject>(), new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-qa='home-dashboard']"));
            Assert.Empty(cut.FindAll("[data-qa='global-player-bar']"));
            Assert.NotEmpty(cut.FindAll("[data-qa='room-card']"));
            Assert.Single(cut.FindAll(".home-quick-library"));
            Assert.Equal("/library", cut.Find(".home-quick-library a").GetAttribute("href"));
            Assert.NotEmpty(cut.FindAll(".home-footer"));
            // Legacy markup must be gone.
            Assert.Empty(cut.FindAll(".home-ops-dashboard"));
            Assert.Empty(cut.FindAll(".home-ops-panel"));
            Assert.Empty(cut.FindAll("details[data-qa='home-direct-url']"));
            Assert.Empty(cut.FindAll("#activeSpeakerSelect"));
        });
    }

    [Fact]
    public void LibraryPage_SearchesAndFiltersEverySourceType()
    {
        using var ctx = new TestContext();

        var stations = Enumerable.Range(1, 8)
            .Select(index => new TuneInStation { Name = $"Station {index}", Url = $"http://station-{index}.example/stream" })
            .ToList();
        var tracks = new List<SpotifyObject> { new SpotifyObject { Name = "Focus Track", Url = "spotify:track:focus" } };
        var videos = new List<YouTubeObject> { new YouTubeObject { Name = "Live Set", Url = "https://www.youtube.com/watch?v=abc123xyz00" } };
        var collections = new List<YouTubeMusicObject> { new YouTubeMusicObject { Name = "Workout Collection", Url = "https://music.youtube.com/playlist?list=workout" } };

        using var resources = ConfigureServices(ctx, stations, tracks, collections, youTubeVideos: videos);

        var cut = ctx.RenderComponent<LibraryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Station 1", cut.Markup);
            Assert.Contains("Station 8", cut.Markup);
            Assert.Contains("Focus Track", cut.Markup);
            Assert.NotEmpty(cut.FindAll("button[aria-label='Play Station 8']"));
        });

        cut.Find("#library-page-search").Input("Station 8");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Station 8", cut.Markup);
            Assert.DoesNotContain("Station 1", cut.Markup);
            Assert.DoesNotContain("Focus Track", cut.Markup);
        });

        cut.Find("#library-page-search").Input(string.Empty);
        cut.Find("#library-page-type").Change("spotify");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Focus Track", cut.Markup);
            Assert.DoesNotContain("Station 1", cut.Markup);
            Assert.Empty(cut.FindAll("button[aria-label^='Play Station']"));
            Assert.NotEmpty(cut.FindAll("button[aria-label^='Play Focus Track']"));
        });

        cut.Find("#library-page-type").Change("youtube");
        cut.WaitForAssertion(() => Assert.Contains("Live Set", cut.Markup));

        cut.Find("#library-page-type").Change("youtubemusic");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Workout Collection", cut.Markup);
            Assert.DoesNotContain("Live Set", cut.Markup);
        });
    }

    [Fact]
    public void IndexPage_RendersEmptyState_WhenLibraryIsEmpty()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx, new List<TuneInStation>(), new List<SpotifyObject>(), new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-qa='home-dashboard']"));
            Assert.Contains("No saved sources yet. Add one in Library.", cut.Markup);
            Assert.Single(cut.FindAll("a[href='/library']"));
        });
    }

    [Fact]
    public void IndexPage_RendersGroupedSpeakers_Correctly()
    {
        using var ctx = new TestContext();

        var master = new SonosSpeaker { Name = "Living Room", IpAddress = "192.168.1.10", Uuid = "uuid:RINCON_1234567890ABCDEF" };
        var slave = new SonosSpeaker { Name = "Kitchen", IpAddress = "192.168.1.11", Uuid = "uuid:RINCON_0000000000000000" };
        var speakers = new List<SonosSpeaker> { master, slave };

        var settings = new SonosSettings
        {
            IP_Adress = master.IpAddress,
            Speakers = speakers
        };

        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        dbContext.Users.Add(new ApplicationUser { Id = "test-user", UserName = "tester", NormalizedUserName = "TESTER", FirstName = string.Empty, LastName = string.Empty });
        dbContext.SaveChanges();
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);
        ctx.Services.AddScoped<UserFavouriteSourceService>();
        ctx.Services.AddScoped<HomeLibraryService>();

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

        connectorRepo.Setup(r => r.GetCurrentStationAsync(master.IpAddress, It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://somestream.com");
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
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(configuration);

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Living Room", cut.Markup);
            Assert.Contains("Kitchen", cut.Markup);
            Assert.DoesNotContain("Linked to Living Room", cut.Markup);
            Assert.DoesNotContain("↳", cut.Markup);
            Assert.Single(cut.FindAll(".room-groups__item"));
            Assert.Contains("Playing", cut.Find(".room-groups__item .badge").TextContent);
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
        dbContext.Users.Add(new ApplicationUser { Id = "test-user", UserName = "tester", NormalizedUserName = "TESTER", FirstName = string.Empty, LastName = string.Empty });
        dbContext.SaveChanges();
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);
        ctx.Services.AddScoped<UserFavouriteSourceService>();
        ctx.Services.AddScoped<HomeLibraryService>();

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
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(configuration);

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".room-groups__item"));
            Assert.Contains("Office Pair", cut.Markup);
            Assert.Contains("Office Right", cut.Markup);
            Assert.Contains("Paused", cut.Find(".room-groups__item .badge").TextContent);
            Assert.DoesNotContain("Linked to Office Pair", cut.Markup);
        });
    }

    [Fact]
    public void LibraryPage_HasAccessibleTabsSearchAndExplicitSourceType()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(ctx, new List<TuneInStation>(), new List<SpotifyObject>(), new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<LibraryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("page", cut.Find(".workspace-tab.is-active").GetAttribute("aria-current"));
            Assert.NotNull(cut.Find("#library-page-search"));
            Assert.NotNull(cut.Find("button.workspace-primary-action"));
        });

        cut.Find("button.workspace-primary-action").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".app-dialog[role='dialog']"));
            Assert.NotNull(cut.Find("#source-type"));
            Assert.Equal(4, cut.FindAll("#source-type option").Count);
        });
    }

    [Fact]
    public void LibraryPage_TogglesFavourite_WithAccessibleHeartButton()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(
            ctx,
            new List<TuneInStation>
            {
                new() { Name = "Favourite Radio", Url = "https://radio.example/live" }
            },
            new List<SpotifyObject>(),
            new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<LibraryPage>();

        cut.WaitForAssertion(() =>
        {
            var addButton = cut.Find("button[aria-label='Add Favourite Radio to favourites']");
            Assert.Equal("false", addButton.GetAttribute("aria-pressed"));
        });

        cut.Find("button[aria-label='Add Favourite Radio to favourites']").Click();

        cut.WaitForAssertion(() =>
        {
            var removeButton = cut.Find("button[aria-label='Remove Favourite Radio from favourites']");
            Assert.Equal("true", removeButton.GetAttribute("aria-pressed"));
            Assert.Single(resources.DbContext.UserFavouriteSources);
        });

        cut.Find("button[aria-label='Remove Favourite Radio from favourites']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("button[aria-label='Add Favourite Radio to favourites']"));
            Assert.Empty(resources.DbContext.UserFavouriteSources);
        });
    }

    [Fact]
    public void LibraryPage_ShowsFavouriteError_WhenCurrentUserCannotBeResolved()
    {
        using var ctx = new TestContext();
        using var resources = ConfigureServices(
            ctx,
            new List<TuneInStation>
            {
                new() { Name = "Unavailable Favourite", Url = "https://radio.example/error" }
            },
            new List<SpotifyObject>(),
            new List<YouTubeMusicObject>());

        var cut = ctx.RenderComponent<LibraryPage>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("button[aria-label='Add Unavailable Favourite to favourites']")));
        resources.DbContext.Users.Remove(resources.DbContext.Users.Single());
        resources.DbContext.SaveChanges();

        cut.Find("button[aria-label='Add Unavailable Favourite to favourites']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Favourite could not be updated", cut.Markup);
            Assert.Empty(resources.DbContext.UserFavouriteSources);
        });
    }

    [Fact]
    public void IndexPage_RendersDashboard_WithAutomationRoomsQuickLibraryAndWarnings()
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
            Assert.Single(cut.FindAll("[data-qa='home-dashboard']"));
            Assert.Empty(cut.FindAll(".spotify-library"));
            Assert.Empty(cut.FindAll(".spotify-home-context"));
            Assert.Empty(cut.FindAll(".spotify-room-picker"));
            Assert.DoesNotContain("Today at a glance", cut.Markup);
            Assert.Contains("Morning Radio", cut.Markup);
            Assert.Contains("Scene: Morning Radio", cut.Markup);
            Assert.Contains("Speakers", cut.Markup);
            Assert.Contains("Favourites", cut.Markup);
            Assert.Contains("View library", cut.Markup);
            Assert.Contains("Online", cut.Markup);
            Assert.Contains("Offline", cut.Markup);
            Assert.Contains("Device warnings", cut.Markup);
            Assert.Contains("Bedroom", cut.Markup);
            Assert.DoesNotContain("Morning Radio scene applied by scheduler", cut.Markup);
            Assert.Single(cut.FindAll(".home-next"));
            var buttonLabels = string.Join("|", cut.FindAll("button").Select(button => button.GetAttribute("aria-label") ?? button.TextContent.Trim()));
            Assert.Contains("Play ORF Radio Wien", buttonLabels);
            Assert.Contains("home-shell", cut.Find("[data-qa='home-dashboard']").ClassList);
        });
    }

    [Fact]
    public void IndexPage_RendersDeviceWarningDetails()
    {
        using var ctx = new TestContext();
        var healthStore = new DeviceHealthSnapshotStore();
        healthStore.Replace(new[]
        {
            new DeviceHealthStatus
            {
                SpeakerIp = "1.2.3.4",
                SpeakerName = "Living Room",
                IsOnline = false,
                LastError = "Connection timed out"
            }
        });
        using var resources = ConfigureServices(
            ctx,
            new List<TuneInStation>(),
            new List<SpotifyObject>(),
            new List<YouTubeMusicObject>(),
            healthStore: healthStore);

        var cut = ctx.RenderComponent<IndexPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(".home-activity__row strong"));
            Assert.Contains("Living Room", cut.Find(".home-activity__row strong").TextContent);
            Assert.Contains("Offline · 1.2.3.4", cut.Markup);
        });
    }

    [Fact]
    public async Task LibraryPage_PlaySource_UsesPlaybackStateSpeakerEvenWhenPersistedSpeakerDiffers()
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

        var cut = ctx.RenderComponent<LibraryPage>();

        cut.WaitForAssertion(() => Assert.Contains("Live Set", cut.Markup));

        var playbackState = ctx.Services.GetRequiredService<PlaybackUiStateService>();
        await playbackState.SetActiveSpeakerAsync("1.2.3.5");

        cut.Find("button[aria-label='Play Live Set']").Click();

        resources.ConnectorRepo.Verify(repo => repo.PlayYouTubeAudioAsync(
            "1.2.3.5",
            "https://www.youtube.com/watch?v=abc123xyz00",
            It.IsAny<YouTubePlaybackMode?>(),
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        resources.ConnectorRepo.Verify(repo => repo.StartPlaying("1.2.3.5"), Times.Once);
    }

    [Fact]
    public void LibraryPage_YouTubeEditor_UpdatesSelectedEntryWithoutPlaying()
    {
        using var ctx = new TestContext();

        var settings = new SonosSettings
        {
            IP_Adress = "1.2.3.4",
            Volume = 20,
            MaxVolume = 80,
            YouTubeCollections = new List<YouTubeObject>
            {
                new() { Name = "Playlist Entry", Url = "https://www.youtube.com/playlist?list=PL123", PlaybackMode = YouTubePlaybackMode.PlaylistOrdered },
                new() { Name = "Single Entry", Url = "https://www.youtube.com/watch?v=abc123xyz00", PlaybackMode = YouTubePlaybackMode.AutoQueueRelated }
            },
            Speakers = new List<SonosSpeaker>
            {
                new() { IpAddress = "1.2.3.4", Name = "Kitchen" }
            }
        };
        using var resources = ConfigureServices(
            ctx,
            new List<TuneInStation>(),
            new List<SpotifyObject>(),
            new List<YouTubeMusicObject>(),
            settings,
            youTubeVideos: settings.YouTubeCollections);

        var cut = ctx.RenderComponent<LibraryPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("button[aria-label='Edit Playlist Entry']"));
        });

        cut.Find("button[aria-label='Edit Playlist Entry']").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#source-playback-mode")));
        cut.Find("#source-playback-mode").Change(YouTubePlaybackMode.PlaylistShuffle.ToString());
        cut.Find(".app-dialog__actions .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".app-dialog[role='dialog']")));
        Assert.Equal(
            YouTubePlaybackMode.PlaylistShuffle,
            settings.YouTubeCollections.Single(item => item.Name == "Playlist Entry").PlaybackMode);
        Assert.Equal(
            YouTubePlaybackMode.AutoQueueRelated,
            settings.YouTubeCollections.Single(item => item.Name == "Single Entry").PlaybackMode);
        resources.ConnectorRepo.Verify(repo => repo.PlayYouTubeAudioAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<YouTubePlaybackMode?>(),
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
        ctx.JSInterop.SetupVoid("window.sonosUi.setBodyScrollLock", _ => true);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        dbContext.Users.Add(new ApplicationUser { Id = "test-user", UserName = "tester", NormalizedUserName = "TESTER", FirstName = string.Empty, LastName = string.Empty });
        dbContext.SaveChanges();
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);
        ctx.Services.AddScoped<UserFavouriteSourceService>();
        ctx.Services.AddScoped<HomeLibraryService>();

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
        connectorRepo.Setup(r => r.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(r => r.PlaySpotifyTrackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(r => r.PlayYouTubeAudioAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<YouTubePlaybackMode?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(r => r.PlayYouTubeMusicTrackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        connectorRepo.Setup(r => r.StartPlaying(It.IsAny<string>())).Returns(Task.CompletedTask);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(u => u.ISonosConnectorRepo).Returns(connectorRepo.Object);
        unitOfWork.SetupGet(u => u.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        ctx.Services.AddSingleton<IUnitOfWork>(unitOfWork.Object);
        ctx.Services.AddSingleton<INotificationService>(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton<IMetricsCollector>(new MetricsCollector());
        ctx.Services.AddSingleton(healthStore ?? new DeviceHealthSnapshotStore());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(configuration);

        return new TestResources(dbContext, connectorRepo);
    }
}
