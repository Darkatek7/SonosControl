using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests
{
    public class DiscordNotificationServiceTests
    {
        private readonly Mock<ISettingsRepo> _settingsRepoMock;
        private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
        private readonly Mock<ILogger<DiscordNotificationService>> _loggerMock;
        private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;

        public DiscordNotificationServiceTests()
        {
            _settingsRepoMock = new Mock<ISettingsRepo>();
            _httpClientFactoryMock = new Mock<IHttpClientFactory>();
            _loggerMock = new Mock<ILogger<DiscordNotificationService>>();
            _httpMessageHandlerMock = new Mock<HttpMessageHandler>();

            var client = new HttpClient(_httpMessageHandlerMock.Object);
            _httpClientFactoryMock.Setup(x => x.CreateClient("DiscordWebhook")).Returns(client);
        }

        [Fact]
        public async Task SendNotificationAsync_ShouldDoNothing_WhenUrlIsNotConfigured()
        {
            // Arrange
            _settingsRepoMock.Setup(x => x.GetSettings()).ReturnsAsync(new SonosSettings { DiscordWebhookUrl = null });
            var service = new DiscordNotificationService(_settingsRepoMock.Object, _httpClientFactoryMock.Object, _loggerMock.Object);

            // Act
            await service.SendNotificationAsync("Test Message");

            // Assert
            _httpMessageHandlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendNotificationAsync_ShouldSendPostRequest_WhenUrlIsConfigured()
        {
            // Arrange
            string webhookUrl = "https://discord.com/api/webhooks/12345";
            _settingsRepoMock.Setup(x => x.GetSettings()).ReturnsAsync(new SonosSettings { DiscordWebhookUrl = webhookUrl });

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var service = new DiscordNotificationService(_settingsRepoMock.Object, _httpClientFactoryMock.Object, _loggerMock.Object);

            // Act
            await service.SendNotificationAsync("Test Message", "User1");

            // Assert
            _httpMessageHandlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri.ToString() == webhookUrl &&
                    req.Content.ReadAsStringAsync().Result.Contains("Test Message") &&
                    req.Content.ReadAsStringAsync().Result.Contains("User1")
                ),
                ItExpr.IsAny<CancellationToken>()
            );
        }
    }
}
