using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests
{
    public class AggregateNotificationServiceTests
    {
        private readonly Mock<ILogger<AggregateNotificationService>> _loggerMock;

        public AggregateNotificationServiceTests()
        {
            _loggerMock = new Mock<ILogger<AggregateNotificationService>>();
        }

        [Fact]
        public async Task SendNotificationAsync_ShouldCallAllNotifiers()
        {
            // Arrange
            var notifier1Mock = new Mock<INotifier>();
            var notifier2Mock = new Mock<INotifier>();
            var notifiers = new List<INotifier> { notifier1Mock.Object, notifier2Mock.Object };

            var service = new AggregateNotificationService(notifiers, _loggerMock.Object);

            // Act
            await service.SendNotificationAsync("Test Message", "User1");

            // Assert
            notifier1Mock.Verify(x => x.SendNotificationAsync("Test Message", "User1"), Times.Once);
            notifier2Mock.Verify(x => x.SendNotificationAsync("Test Message", "User1"), Times.Once);
        }

        [Fact]
        public async Task SendNotificationAsync_ShouldContinue_WhenOneNotifierFails()
        {
            // Arrange
            var notifier1Mock = new Mock<INotifier>();
            notifier1Mock.Setup(x => x.SendNotificationAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .ThrowsAsync(new Exception("Notifier failure"));

            var notifier2Mock = new Mock<INotifier>();

            var notifiers = new List<INotifier> { notifier1Mock.Object, notifier2Mock.Object };

            var service = new AggregateNotificationService(notifiers, _loggerMock.Object);

            // Act
            await service.SendNotificationAsync("Test Message");

            // Assert
            notifier1Mock.Verify(x => x.SendNotificationAsync("Test Message", null), Times.Once);
            notifier2Mock.Verify(x => x.SendNotificationAsync("Test Message", null), Times.Once);

            // Should log the error
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }
    }
}
