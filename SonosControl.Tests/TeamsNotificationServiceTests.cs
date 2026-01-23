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
    public class TeamsNotificationServiceTests
    {
        private readonly Mock<ISettingsRepo> _settingsRepoMock;
        private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
        private readonly Mock<ILogger<TeamsNotificationService>> _loggerMock;
        private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;

        public TeamsNotificationServiceTests()
        {
            _settingsRepoMock = new Mock<ISettingsRepo>();
            _httpClientFactoryMock = new Mock<IHttpClientFactory>();
            _loggerMock = new Mock<ILogger<TeamsNotificationService>>();
            _httpMessageHandlerMock = new Mock<HttpMessageHandler>();

            var client = new HttpClient(_httpMessageHandlerMock.Object);
            _httpClientFactoryMock.Setup(x => x.CreateClient("TeamsWebhook")).Returns(client);
        }

        [Fact]
        public async Task SendNotificationAsync_ShouldDoNothing_WhenUrlIsNotConfigured()
        {
            // Arrange
            _settingsRepoMock.Setup(x => x.GetSettings()).ReturnsAsync(new SonosSettings { TeamsWebhookUrl = null });
            var service = new TeamsNotificationService(_settingsRepoMock.Object, _httpClientFactoryMock.Object, _loggerMock.Object);

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
            string webhookUrl = "https://outlook.office.com/webhook/12345";
            _settingsRepoMock.Setup(x => x.GetSettings()).ReturnsAsync(new SonosSettings { TeamsWebhookUrl = webhookUrl });

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var service = new TeamsNotificationService(_settingsRepoMock.Object, _httpClientFactoryMock.Object, _loggerMock.Object);

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
                    req.Content.ReadAsStringAsync().Result.Contains("User1") &&
                    req.Content.ReadAsStringAsync().Result.Contains("\"text\":")
                ),
                ItExpr.IsAny<CancellationToken>()
            );
        }
    }
}
