using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Pages;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Components.Authorization;
using SonosControl.Web.Models;

namespace SonosControl.Tests
{
    public class IndexPagePerfTests : TestContext
    {
        private Mock<IUnitOfWork> _mockUow;
        private Mock<ISonosConnectorRepo> _mockSonosRepo;
        private Mock<ISettingsRepo> _mockSettingsRepo;
        private ApplicationDbContext _db;
        private Mock<AuthenticationStateProvider> _mockAuthProvider;
        private Mock<INotificationService> _mockNotificationService;

        public IndexPagePerfTests()
        {
            _mockUow = new Mock<IUnitOfWork>();
            _mockSonosRepo = new Mock<ISonosConnectorRepo>();
            _mockSettingsRepo = new Mock<ISettingsRepo>();
            _mockNotificationService = new Mock<INotificationService>();

            // Use InMemory database instead of mocking DbContext directly
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            _db = new ApplicationDbContext(options);

            _mockAuthProvider = new Mock<AuthenticationStateProvider>();

            _mockUow.Setup(u => u.ISonosConnectorRepo).Returns(_mockSonosRepo.Object);
            _mockUow.Setup(u => u.ISettingsRepo).Returns(_mockSettingsRepo.Object);

            Services.AddSingleton(_mockUow.Object);
            Services.AddSingleton(_db);
            Services.AddSingleton(_mockAuthProvider.Object);
            Services.AddSingleton(_mockNotificationService.Object);

            // Mock auth state
            var identity = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "TestUser"),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "admin")
            }, "TestAuth");
            var principal = new System.Security.Claims.ClaimsPrincipal(identity);
            _mockAuthProvider.Setup(p => p.GetAuthenticationStateAsync())
                .ReturnsAsync(new AuthenticationState(principal));
        }

        [Fact]
        public void SyncPlay_ShouldExecuteInParallel()
        {
            // Arrange
            var speakers = new List<SonosSpeaker>
            {
                new SonosSpeaker { Name = "S1", IpAddress = "192.168.1.101" },
                new SonosSpeaker { Name = "S2", IpAddress = "192.168.1.102" },
                new SonosSpeaker { Name = "S3", IpAddress = "192.168.1.103" }
            };

            var settings = new SonosSettings { Speakers = speakers, IP_Adress = "192.168.1.101" };
            _mockSettingsRepo.Setup(s => s.GetSettings()).ReturnsAsync(settings);

            // Current speaker is playing a station
            _mockSonosRepo.Setup(s => s.GetCurrentStationAsync("192.168.1.101", It.IsAny<CancellationToken>()))
                .ReturnsAsync("x-rincon-mp3radio://example.com/stream");
            _mockSonosRepo.Setup(s => s.IsPlaying("192.168.1.101")).ReturnsAsync(true);
            _mockSonosRepo.Setup(s => s.GetVolume(It.IsAny<string>())).ReturnsAsync(20);

            // Simulate delay for SetTuneInStationAsync and StartPlaying to prove parallelism
            var delayTime = 100; // ms
            _mockSonosRepo.Setup(s => s.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async () => await Task.Delay(delayTime));

            _mockSonosRepo.Setup(s => s.StartPlaying(It.IsAny<string>()))
                .Returns(async () => await Task.Delay(delayTime));

            var cut = RenderComponent<IndexPage>();

            // Wait for OnInitializedAsync
            cut.WaitForState(() => cut.Instance != null);

            // Find the Sync Play button
            var buttons = cut.FindAll("button");
            var syncButton = buttons.FirstOrDefault(b => b.TextContent.Contains("Sync Play"));

            Assert.NotNull(syncButton);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Act
            syncButton.Click();

            // Wait for the UI to update back (spinner gone)
            cut.WaitForState(() => !syncButton.HasAttribute("disabled"), TimeSpan.FromSeconds(2));
            stopwatch.Stop();

            // Assert
            // With 3 speakers total and 1 master (S1), we expect calls for S2 and S3.
            _mockSonosRepo.Verify(s => s.SetTuneInStationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
    }
}
