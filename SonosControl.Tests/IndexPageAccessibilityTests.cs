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
using SonosControl.Web.Services;
using SonosControl.Web.Shared;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SonosControl.Tests;

public class IndexPageAccessibilityTests
{
    [Fact]
    public void GlobalPlayer_UsesDecorativeAlbumArtWithAdjacentTrackText()
    {
        using var ctx = new TestContext();

        // Setup Mocks
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        var settings = new SonosSettings
        {
            IP_Adress = "1.2.3.4",
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4", Name = "Living Room" } }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(r => r.GetVolume(It.IsAny<string>())).ReturnsAsync(20);
        connectorRepo.Setup(r => r.IsPlaying(It.IsAny<string>())).ReturnsAsync(true);
        connectorRepo.Setup(r => r.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://some.url");
        connectorRepo.Setup(r => r.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo
            {
                Title = "Accessible Song",
                Artist = "Accessible Artist",
                AlbumArtUri = "http://art.url/img.jpg"
            });
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
        ctx.Services.AddSingleton<IDeviceHealthSnapshotStore>(new DeviceHealthSnapshotStore());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(configuration);

        // Render
        var cut = ctx.RenderComponent<GlobalPlayerBar>();

        // Verify
        cut.WaitForAssertion(() =>
        {
            var img = cut.Find(".global-player-bar__art img");
            Assert.Equal(string.Empty, img.GetAttribute("alt"));
            Assert.Contains("Accessible Song — Accessible Artist", cut.Find(".global-player-bar__meta strong").TextContent);
        });
    }

    [Fact]
    public void GlobalPlayer_FallbackArt_IsHiddenFromScreenReaders()
    {
         using var ctx = new TestContext();

        // Setup Mocks (No album art)
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ApplicationDbContext(options);
        ctx.Services.AddSingleton<ApplicationDbContext>(dbContext);

        var settings = new SonosSettings
        {
            IP_Adress = "1.2.3.4",
            Speakers = new List<SonosSpeaker> { new SonosSpeaker { IpAddress = "1.2.3.4", Name = "Living Room" } }
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(r => r.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(r => r.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(r => r.GetVolume(It.IsAny<string>())).ReturnsAsync(20);
        connectorRepo.Setup(r => r.IsPlaying(It.IsAny<string>())).ReturnsAsync(true);
        connectorRepo.Setup(r => r.GetCurrentStationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://some.url");
        connectorRepo.Setup(r => r.GetTrackInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo
            {
                Title = "No Art Song",
                Artist = "Artist",
                AlbumArtUri = null // Trigger fallback
            });
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
        ctx.Services.AddSingleton<IDeviceHealthSnapshotStore>(new DeviceHealthSnapshotStore());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddSingleton(new ConfiguredTimeZoneService(TimeZoneInfo.Utc));
        ctx.Services.AddScoped<PlaybackUiStateService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        ctx.Services.AddSingleton<IConfiguration>(configuration);

        // Render
        var cut = ctx.RenderComponent<GlobalPlayerBar>();

        // Verify
        cut.WaitForAssertion(() =>
        {
            var fallback = cut.Find(".global-player-bar__art");
            Assert.Equal("true", fallback.GetAttribute("aria-hidden"));
        });
    }
}
