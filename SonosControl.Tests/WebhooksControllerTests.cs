using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Controllers;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class WebhooksControllerTests
{
    [Fact]
    public async Task Execute_ConcurrentIdenticalRequestReturnsConflictThenDuplicateSuccess()
    {
        using var fixture = new WebhookFixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ConnectorRepo
            .Setup(repository => repository.StartPlaying("192.0.2.20"))
            .Returns(gate.Task);
        var key = $"parallel-{Guid.NewGuid():N}";

        var firstController = fixture.CreateController(key);
        var firstRequest = firstController.Execute(
            new WebhooksController.WebhookActionRequest
            {
                Action = "play",
                SpeakerIp = "192.0.2.20"
            },
            CancellationToken.None);

        var concurrentController = fixture.CreateController(key);
        var concurrentResult = await concurrentController.Execute(
            new WebhooksController.WebhookActionRequest
            {
                Action = "play",
                SpeakerIp = "192.0.2.20"
            },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(concurrentResult);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal("2", concurrentController.Response.Headers.RetryAfter);

        gate.SetResult();
        Assert.IsType<OkObjectResult>(await firstRequest);

        var duplicateController = fixture.CreateController(key);
        var duplicateResult = await duplicateController.Execute(
            new WebhooksController.WebhookActionRequest
            {
                Action = "play",
                SpeakerIp = "192.0.2.20"
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(duplicateResult);
        fixture.ConnectorRepo.Verify(
            repository => repository.StartPlaying("192.0.2.20"),
            Times.Once);
    }

    [Fact]
    public async Task Execute_FailedRequestReleasesReservationForRetry()
    {
        using var fixture = new WebhookFixture();
        fixture.ConnectorRepo
            .Setup(repository => repository.StartPlaying("192.0.2.21"))
            .ThrowsAsync(new HttpRequestException("speaker unavailable"));
        var key = $"retry-{Guid.NewGuid():N}";
        var request = new WebhooksController.WebhookActionRequest
        {
            Action = "play",
            SpeakerIp = "192.0.2.21"
        };

        var failedResult = await fixture.CreateController(key)
            .Execute(request, CancellationToken.None);

        var failure = Assert.IsType<ObjectResult>(failedResult);
        Assert.Equal(StatusCodes.Status502BadGateway, failure.StatusCode);

        fixture.ConnectorRepo
            .Setup(repository => repository.StartPlaying("192.0.2.21"))
            .Returns(Task.CompletedTask);

        var retryResult = await fixture.CreateController(key)
            .Execute(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(retryResult);
        fixture.ConnectorRepo.Verify(
            repository => repository.StartPlaying("192.0.2.21"),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Execute_RejectsIncorrectApiKey()
    {
        using var fixture = new WebhookFixture();
        var controller = fixture.CreateController(
            $"auth-{Guid.NewGuid():N}",
            apiKey: "wrong-key");

        var result = await controller.Execute(
            new WebhooksController.WebhookActionRequest { Action = "play", SpeakerIp = "192.0.2.22" },
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        fixture.ConnectorRepo.Verify(
            repository => repository.StartPlaying(It.IsAny<string>()),
            Times.Never);
    }

    private sealed class WebhookFixture : IDisposable
    {
        private const string ConfiguredApiKey = "test-webhook-key";
        private readonly ApplicationDbContext _db;
        private readonly IConfiguration _configuration;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ActionLogger _actionLogger;

        public WebhookFixture()
        {
            ConnectorRepo = new Mock<ISonosConnectorRepo>();
            var settingsRepo = new Mock<ISettingsRepo>();
            settingsRepo.Setup(repository => repository.GetSettings())
                .ReturnsAsync(new SonosSettings());

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(value => value.ISonosConnectorRepo).Returns(ConnectorRepo.Object);
            unitOfWork.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo.Object);
            _unitOfWork = unitOfWork.Object;

            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Webhook:ApiKey"] = ConfiguredApiKey
                })
                .Build();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"webhook-tests-{Guid.NewGuid():N}")
                .Options;
            _db = new ApplicationDbContext(options);
            _actionLogger = new ActionLogger(_db, new HttpContextAccessor());
        }

        public Mock<ISonosConnectorRepo> ConnectorRepo { get; }

        public WebhooksController CreateController(string idempotencyKey, string? apiKey = null)
        {
            var controller = new WebhooksController(
                _configuration,
                _unitOfWork,
                Mock.Of<ISceneOrchestrationService>(),
                _actionLogger,
                NullLogger<WebhooksController>.Instance);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers["X-API-Key"] = apiKey ?? ConfiguredApiKey;
            httpContext.Request.Headers["Idempotency-Key"] = idempotencyKey;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        public void Dispose() => _db.Dispose();
    }
}
