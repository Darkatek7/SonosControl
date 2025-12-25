using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests.Repos
{
    public class HolidayRepoTests
    {
        [Fact]
        public async Task IsHoliday_ReturnsTrue_WhenApiReturnsThreeOrMoreChars()
        {
            // Arrange
            var mockFactory = new Mock<IHttpClientFactory>();
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();

            // Mock API response
            mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent("YES") // Length >= 3
                });

            var client = new HttpClient(mockHttpMessageHandler.Object);
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

            var repo = new HolidayRepo(mockFactory.Object);

            // Act
            var result = await repo.IsHoliday();

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task IsHoliday_ReturnsFalse_WhenApiReturnsLessThanThreeChars()
        {
            // Arrange
            var mockFactory = new Mock<IHttpClientFactory>();
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();

            // Mock API response
            mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent("NO") // Length < 3
                });

            var client = new HttpClient(mockHttpMessageHandler.Object);
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

            var repo = new HolidayRepo(mockFactory.Object);

            // Act
            var result = await repo.IsHoliday();

            // Assert
            Assert.False(result);
        }
    }
}
