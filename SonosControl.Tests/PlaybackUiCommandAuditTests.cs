using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class PlaybackUiCommandAuditTests
{
    [Fact]
    public async Task VolumeKeepsPlaybackOwnershipAndAuditsTheUserAndActualRoom()
    {
        await using var fixture = new Fixture();
        await fixture.Ui.InitializeAsync();
        await fixture.Ui.SetVolumeAsync(21);
        fixture.Connector.Verify(connector => connector.SetVolume(Fixture.Ip, 21), Times.Once);
        Assert.Empty(fixture.Commands.ManualSpeakersSince(DateTimeOffset.MinValue));
        var log = Assert.Single(await fixture.Db.Logs.ToListAsync());
        Assert.Equal("Volume Changed", log.Action);
        Assert.Equal("operator-test", log.PerformedBy);
        Assert.Contains($"{Fixture.Ip}: 21%", log.Details);
        Assert.Contains("ownership unchanged", log.Details);
    }

    [Fact]
    public async Task PauseStillClaimsPlaybackAndAuditsTheOperator()
    {
        await using var fixture = new Fixture();
        await fixture.Ui.InitializeAsync();
        await fixture.Ui.TogglePlaybackAsync();
        fixture.Connector.Verify(connector => connector.PausePlaying(Fixture.Ip), Times.Once);
        Assert.Contains(Fixture.Ip, fixture.Commands.ManualSpeakersSince(DateTimeOffset.MinValue));
        var log = Assert.Single(await fixture.Db.Logs.ToListAsync());
        Assert.Equal("Playback Paused", log.Action);
        Assert.Equal("operator-test", log.PerformedBy);
        Assert.Equal(Fixture.Ip, log.Details);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Ip = "192.0.2.1";
        public Mock<ISonosConnectorRepo> Connector { get; } = new();
        public PlaybackCommandCoordinator Commands { get; } = new(TimeProvider.System);
        public ApplicationDbContext Db { get; }
        public PlaybackUiStateService Ui { get; }
        private readonly ServiceProvider _provider;

        public Fixture()
        {
            var settings = new SonosSettings { IP_Adress = Ip, Speakers = [new() { Name = "Test room", IpAddress = Ip }], MaxVolume = 50 };
            var repository = new Mock<ISettingsRepo>();
            repository.Setup(repo => repo.GetSettings()).ReturnsAsync(settings);
            repository.Setup(repo => repo.UpdateSettingsAsync(It.IsAny<Action<SonosSettings>>(), It.IsAny<CancellationToken>()))
                .Returns((Action<SonosSettings> change, CancellationToken _) => { change(settings); return Task.FromResult(settings); });
            Connector.Setup(connector => connector.GetVolume(Ip, It.IsAny<CancellationToken>())).ReturnsAsync(18);
            Connector.Setup(connector => connector.GetCurrentStationAsync(Ip, It.IsAny<CancellationToken>())).ReturnsAsync("https://radio.example.com/live");
            Connector.Setup(connector => connector.IsPlaying(Ip, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Connector.Setup(connector => connector.GetAllSpeakersInGroup(Ip, It.IsAny<CancellationToken>())).ReturnsAsync([Ip]);
            var auth = new Mock<AuthenticationStateProvider>();
            auth.Setup(provider => provider.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator-test")], "test"))));
            Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var services = new ServiceCollection();
            services.AddSingleton<SceneTimerService>(provider => new(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<SceneTimerService>.Instance));
            _provider = services.BuildServiceProvider();
            var manual = new ManualPlaybackService(Commands, _provider, Connector.Object, NullLogger<ManualPlaybackService>.Instance);
            Ui = new PlaybackUiStateService(repository.Object, Connector.Object, NullLogger<PlaybackUiStateService>.Instance,
                Db, auth.Object, Mock.Of<INotificationService>(), manual);
        }

        public async ValueTask DisposeAsync()
        {
            Ui.Dispose();
            await _provider.DisposeAsync();
            await Db.DisposeAsync();
        }
    }
}
