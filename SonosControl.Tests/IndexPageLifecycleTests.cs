using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Pages;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class IndexPageLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_CancelsAndAwaitsActiveRefresh()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectorRepo = new Mock<ISonosConnectorRepo>();
        connectorRepo.Setup(repository => repository.IsPlaying("192.0.2.30"))
            .ReturnsAsync(false);
        connectorRepo.Setup(repository => repository.GetCurrentStationAsync(
                "192.0.2.30",
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken cancellationToken) =>
            {
                refreshStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(value => value.ISonosConnectorRepo).Returns(connectorRepo.Object);

        var page = new IndexPage();
        SetGeneratedProperty(page, "_uow", unitOfWork.Object);
        SetGeneratedProperty(
            page,
            "Configuration",
            new ConfigurationBuilder().AddInMemoryCollection().Build());
        SetGeneratedProperty(page, "Logger", NullLogger<IndexPage>.Instance);
        SetGeneratedProperty(page, "MetricsCollector", new MetricsCollector());
        SetField(
            page,
            "_settings",
            new SonosSettings
            {
                Speakers =
                [
                    new SonosSpeaker
                    {
                        Name = "Test room",
                        IpAddress = "192.0.2.30"
                    }
                ]
            });

        var refresh = Assert.IsAssignableFrom<Task>(
            typeof(IndexPage)
                .GetMethod("RequestPageRefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, [true]));
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await page.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        connectorRepo.Verify(
            repository => repository.GetCurrentStationAsync(
                "192.0.2.30",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static void SetGeneratedProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property.SetValue(target, value);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }
}
