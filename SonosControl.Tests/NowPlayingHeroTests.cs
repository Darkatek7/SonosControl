using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Pages.Index.Components;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class NowPlayingHeroTests
{
    [Fact]
    public async Task Hero_RendersAlbumArtLiveStateAndAccessibleControls()
    {
        using var ctx = new TestContext();
        var connectorRepo = ConfigureServices(
            ctx,
            isPlaying: true,
            albumArtUrl: "https://images.example.test/midnight-city.jpg");
        await ctx.Services.GetRequiredService<PlaybackUiStateService>().InitializeAsync();

        var cut = ctx.RenderComponent<NowPlayingHero>();

        Assert.Contains("np-hero--has-art", cut.Find("[data-qa='now-playing-hero']").ClassList);
        Assert.Equal(2, cut.FindAll(".np-hero img").Count);
        Assert.All(cut.FindAll(".np-hero img"), image => Assert.Equal(string.Empty, image.GetAttribute("alt")));
        Assert.Contains("Midnight City", cut.Find("#now-playing-hero-title").TextContent);
        Assert.Contains("Night Drive", cut.Markup);
        Assert.Contains("Office", cut.Find(".np-hero__room-chip").TextContent);
        Assert.Single(cut.FindAll(".np-hero__eq"));
        Assert.Equal("Pause playback", cut.Find("[data-qa='home-player-toggle']").GetAttribute("aria-label"));
        Assert.Equal("Refresh playback state", cut.Find("[data-qa='home-player-refresh']").GetAttribute("aria-label"));
        Assert.Equal("Next track", cut.Find("[data-qa='home-player-next']").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[data-qa='home-group-speakers']"));

        cut.Find("[data-qa='home-player-next']").Click();
        cut.WaitForAssertion(() => connectorRepo.Verify(repo => repo.NextTrack("10.0.0.1"), Times.Once));
    }

    [Fact]
    public async Task Hero_UsesDecorativeFallbackWhenArtworkIsUnavailable()
    {
        using var ctx = new TestContext();
        ConfigureServices(ctx, isPlaying: false, albumArtUrl: null);
        await ctx.Services.GetRequiredService<PlaybackUiStateService>().InitializeAsync();

        var cut = ctx.RenderComponent<NowPlayingHero>();

        Assert.Contains("np-hero--no-art", cut.Find("[data-qa='now-playing-hero']").ClassList);
        Assert.Empty(cut.FindAll(".np-hero img"));
        Assert.Equal("true", cut.Find(".np-hero__art").GetAttribute("aria-hidden"));
        Assert.Single(cut.FindAll(".np-hero__art-fallback"));
        Assert.Empty(cut.FindAll(".np-hero__eq"));
        Assert.Contains("Paused", cut.Find(".np-hero__playback-state").TextContent);
        Assert.Equal("Start playback", cut.Find("[data-qa='home-player-toggle']").GetAttribute("aria-label"));
    }

    private static Mock<ISonosConnectorRepo> ConfigureServices(
        TestContext ctx,
        bool isPlaying,
        string? albumArtUrl)
    {
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized("tester");
        auth.SetRoles("admin");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        ctx.Services.AddSingleton(new ApplicationDbContext(options));

        var settings = new SonosSettings
        {
            IP_Adress = "10.0.0.1",
            Volume = 24,
            MaxVolume = 80,
            Stations =
            [
                new TuneInStation { Name = "Night Drive", Url = "https://radio.example.test/night-drive" }
            ],
            Speakers =
            [
                new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" }
            ]
        };

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
        settingsRepo.Setup(repo => repo.WriteSettings(It.IsAny<SonosSettings?>())).Returns(Task.CompletedTask);

        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(repo => repo.GetVolume("10.0.0.1")).ReturnsAsync(settings.Volume);
        connectorRepo.Setup(repo => repo.IsPlaying("10.0.0.1")).ReturnsAsync(isPlaying);
        connectorRepo.Setup(repo => repo.GetCurrentStationAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings.Stations[0].Url);
        connectorRepo.Setup(repo => repo.GetTrackInfoAsync("10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonosTrackInfo
            {
                Title = "Midnight City",
                Artist = "M83",
                AlbumArtUri = albumArtUrl
            });
        connectorRepo.Setup(repo => repo.NextTrack("10.0.0.1")).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.StartPlaying("10.0.0.1")).Returns(Task.CompletedTask);
        connectorRepo.Setup(repo => repo.PausePlaying("10.0.0.1")).Returns(Task.CompletedTask);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(uow => uow.ISettingsRepo).Returns(settingsRepo.Object);
        unitOfWork.SetupGet(uow => uow.ISonosConnectorRepo).Returns(connectorRepo.Object);

        ctx.Services.AddSingleton(unitOfWork.Object);
        ctx.Services.AddSingleton(Mock.Of<INotificationService>());
        ctx.Services.AddSingleton(Mock.Of<ILogger<PlaybackUiStateService>>());
        ctx.Services.AddScoped<PlaybackUiStateService>();

        return connectorRepo;
    }
}
