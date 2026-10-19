using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Controllers;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public class FlyDjScheduleOwnershipTests
{
    private static AutomationSchedulerService Scheduler(TimeZoneInfo zone)
        => new(Mock.Of<IServiceScopeFactory>(), Mock.Of<ISettingsSchemaMigrationService>(), new AutomationRuntimeStatus(),
            NullLogger<AutomationSchedulerService>.Instance, new ConfigurationBuilder().Build(), TimeProvider.System, zone);

    private static ScheduleWindow Window(string id, string ip, int start, int stop, int priority = 10, bool enabled = true)
        => new()
        {
            Id = id,
            Name = id,
            IsEnabled = enabled,
            StartTime = new TimeOnly(start, 0),
            StopTime = new TimeOnly(stop, 0),
            Priority = priority,
            RecurrenceType = ScheduleRecurrenceType.Daily,
            Playback = new Scene { Id = $"{id}-playback", Name = id, Actions = [new SceneAction { SpeakerIp = ip, IncludeInPlayback = true }] },
            LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    private static SonosSettings Settings(params ScheduleWindow[] windows) => new()
    {
        SettingsSchemaVersion = SonosSettings.CurrentSettingsSchemaVersion,
        Speakers = [new SonosSpeaker { Name = "A", IpAddress = "10.0.0.1" }, new SonosSpeaker { Name = "B", IpAddress = "10.0.0.2" }],
        ScheduleWindows = windows.ToList()
    };

    [Fact]
    public void ReportsRoomsOfWindowsThatAreActiveRightNow()
    {
        IScheduleRoomOwnership scheduler = Scheduler(TimeZoneInfo.Utc);
        var settings = Settings(Window("morning", "10.0.0.1", 8, 10), Window("evening", "10.0.0.2", 18, 22));

        Assert.Equal(["10.0.0.1"], scheduler.GetScheduleOwnedSpeakers(settings, new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero)));
        Assert.Equal(["10.0.0.2"], scheduler.GetScheduleOwnedSpeakers(settings, new DateTimeOffset(2026, 10, 19, 19, 0, 0, TimeSpan.Zero)));
        Assert.Empty(scheduler.GetScheduleOwnedSpeakers(settings, new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void UsesTheConfiguredTimeZoneAndIgnoresDisabledWindowsAndUnmigratedSettings()
    {
        var vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
        IScheduleRoomOwnership scheduler = Scheduler(vienna);
        var settings = Settings(Window("morning", "10.0.0.1", 8, 10), Window("off", "10.0.0.2", 0, 23, enabled: false));
        var sevenUtc = new DateTimeOffset(2026, 10, 19, 7, 30, 0, TimeSpan.Zero);   // 09:30 in Vienna (UTC+2)

        Assert.Equal(["10.0.0.1"], scheduler.GetScheduleOwnedSpeakers(settings, sevenUtc));
        Assert.Empty(scheduler.GetScheduleOwnedSpeakers(settings, new DateTimeOffset(2026, 10, 19, 9, 30, 0, TimeSpan.Zero)));   // 11:30 local

        settings.SettingsSchemaVersion = 0;
        Assert.Empty(scheduler.GetScheduleOwnedSpeakers(settings, sevenUtc));
    }

    [Fact]
    public void ReportsOnlyTheRoomsEachWinningWindowActuallyTargets()
    {
        IScheduleRoomOwnership scheduler = Scheduler(TimeZoneInfo.Utc);
        var settings = Settings(Window("both-a", "10.0.0.1", 8, 12, priority: 5), Window("both-b", "10.0.0.2", 8, 12, priority: 6));

        Assert.Equal(["10.0.0.1", "10.0.0.2"], scheduler.GetScheduleOwnedSpeakers(settings, new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero)).Order());
    }
}

public class FlyDjGeometryEndpointTests
{
    private static readonly byte[] Geometry = [1, 2, 3, 4];

    private static (FlyDjController Controller, Mock<IFlyDjClient> Client, DefaultHttpContext Http) Create(Func<string?, FlyDjGeometryResult>? onGeometry = null)
    {
        var client = new Mock<IFlyDjClient>();
        client.Setup(c => c.GetGeometryAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string? etag, CancellationToken _) => Task.FromResult((onGeometry ?? (tag => tag == "\"v1\""
                ? new FlyDjGeometryResult(null, tag, true)
                : new FlyDjGeometryResult(Geometry, "\"v1\"", false)))(etag)));
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var http = new DefaultHttpContext();
        var controller = new FlyDjController(
            new FlyDjStateStore(client.Object, time, NullLogger<FlyDjStateStore>.Instance),
            Mock.Of<IFlyDjConductor>(), new FlyDjGeometryCache(client.Object, time))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return (controller, client, http);
    }

    [Fact]
    public async Task FirstRequestReturnsTheBytesWithEtagAndPrivateCacheHeaders()
    {
        var (controller, _, http) = Create();

        var result = Assert.IsType<FileContentResult>(await controller.GetGeometry(CancellationToken.None));

        Assert.Equal(Geometry, result.FileContents);
        Assert.Equal("application/octet-stream", result.ContentType);
        Assert.Equal("\"v1\"", http.Response.Headers.ETag.ToString());
        Assert.Equal("private, max-age=3600", http.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("\"v1\"")]
    [InlineData("W/\"v1\"")]
    [InlineData("\"other\", \"v1\"")]
    [InlineData("*")]
    public async Task MatchingIfNoneMatchGetsNotModified(string header)
    {
        var (controller, _, http) = Create();
        http.Request.Headers.IfNoneMatch = header;

        var result = Assert.IsType<StatusCodeResult>(await controller.GetGeometry(CancellationToken.None));

        Assert.Equal(304, result.StatusCode);
        Assert.Equal("\"v1\"", http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task StaleBrowserEtagGetsTheFullBody()
    {
        var (controller, _, http) = Create();
        http.Request.Headers.IfNoneMatch = "\"old\"";

        Assert.IsType<FileContentResult>(await controller.GetGeometry(CancellationToken.None));
    }

    [Fact]
    public async Task SidecarIsContactedOnceAndThenRevalidatedWithTheEtag()
    {
        var calls = new List<string?>();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var client = new Mock<IFlyDjClient>();
        client.Setup(c => c.GetGeometryAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string? etag, CancellationToken _) =>
            {
                calls.Add(etag);
                return Task.FromResult(etag is null ? new FlyDjGeometryResult(Geometry, "\"v1\"", false) : new FlyDjGeometryResult(null, etag, true));
            });
        var cache = new FlyDjGeometryCache(client.Object, time);

        await cache.GetAsync(CancellationToken.None);
        await cache.GetAsync(CancellationToken.None);
        Assert.Single(calls);
        time.Advance(FlyDjGeometryCache.RevalidateAfter + TimeSpan.FromSeconds(1));
        var (content, etag) = await cache.GetAsync(CancellationToken.None);

        Assert.Equal([null, "\"v1\""], calls);
        Assert.Equal(Geometry, content);
        Assert.Equal("\"v1\"", etag);
    }

    [Fact]
    public async Task UnavailableSidecarAnswers503()
    {
        var (controller, _, _) = Create(_ => throw new FlyDjUnavailableException("down"));

        var result = Assert.IsType<ObjectResult>(await controller.GetGeometry(CancellationToken.None));

        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public async Task StartAndStopDelegateToTheConductorWithTheUserName()
    {
        var conductor = new Mock<IFlyDjConductor>();
        var client = new Mock<IFlyDjClient>();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var http = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "alice")], "test"))
        };
        var controller = new FlyDjController(new FlyDjStateStore(client.Object, time, NullLogger<FlyDjStateStore>.Instance),
            conductor.Object, new FlyDjGeometryCache(client.Object, time))
        { ControllerContext = new ControllerContext { HttpContext = http } };

        await controller.Start(CancellationToken.None);
        await controller.Stop(CancellationToken.None);

        conductor.Verify(c => c.StartAsync("alice", It.IsAny<CancellationToken>()), Times.Once);
        conductor.Verify(c => c.StopAsync("alice", It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsType<OkObjectResult>(controller.GetState().Result);
    }
}

public class FlyDjStreamRelayTests
{
    private static async Task<WebApplication> StartAsync(Action<WebApplication> map, Dictionary<string, string?>? config = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (config is not null) builder.Configuration.AddInMemoryCollection(config);
        builder.Services.Configure<FlyDjOptions>(builder.Configuration.GetSection(FlyDjOptions.SectionName));
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static string HttpAddress(WebApplication app)
        => app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

    private static Uri AddressOf(WebApplication app, string path = "")
        => new(HttpAddress(app).Replace("http://", "ws://") + "/" + path);

    private static async Task<WebApplication> StartSidecarAsync(Func<WebSocket, HttpContext, Task> serve, List<string?> tokens)
        => await StartAsync(app =>
        {
            app.UseWebSockets();
            app.Map("/v1/stream", async context =>
            {
                tokens.Add(context.Request.Headers["X-FlyDj-Token"]);
                if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await serve(socket, context);
            });
        });

    private static async Task<WebApplication> StartRelayAsync(string sidecarUrl, string? secret = null)
        => await StartAsync(app =>
        {
            app.UseWebSockets();
            app.Map("/flydj/stream", FlyDjStreamRelay.HandleAsync);
        }, new Dictionary<string, string?>
        {
            ["FlyDj:ServiceUrl"] = sidecarUrl,
            ["FlyDj:SharedSecret"] = secret,
            ["FlyDj:RequestTimeoutSeconds"] = "5"
        });

    [Fact]
    public async Task NonWebSocketRequestsGetBadRequest()
    {
        await using var relay = await StartRelayAsync("http://127.0.0.1:1");
        using var http = new HttpClient { BaseAddress = new Uri(HttpAddress(relay)) };

        var response = await http.GetAsync("/flydj/stream");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RelaysBinaryFramesOneToOneAndSendsTheSharedSecretUpstream()
    {
        var tokens = new List<string?>();
        var frames = new[] { new byte[] { 0xF1, 1, 2, 0, 5, 0, 0, 0 }, new byte[] { 0xF1, 1, 2, 0, 0, 0, 0, 0, 9, 9 }, new byte[70_000] };
        frames[2][0] = 0xF1;
        await using var sidecar = await StartSidecarAsync(async (socket, _) =>
        {
            foreach (var frame in frames) await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.ReceiveAsync(new byte[16], CancellationToken.None);   // wait for the relay to close
        }, tokens);
        await using var relay = await StartRelayAsync(HttpAddress(sidecar), "s3cret");

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(AddressOf(relay, "flydj/stream"), CancellationToken.None);
        var received = new List<byte[]>();
        var buffer = new byte[128 * 1024];
        while (received.Count < frames.Length)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await browser.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
            received.Add(message.ToArray());
        }

        Assert.Equal(frames, received);
        Assert.Equal(["s3cret"], tokens);
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [Fact]
    public async Task ClosingTheSidecarStreamClosesTheBrowserSideCleanly()
    {
        await using var sidecar = await StartSidecarAsync(async (socket, _) =>
        {
            await socket.SendAsync(new byte[] { 0xF1, 1 }, WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }, []);
        await using var relay = await StartRelayAsync(HttpAddress(sidecar));

        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(AddressOf(relay, "flydj/stream"), CancellationToken.None);
        var buffer = new byte[1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = await browser.ReceiveAsync(buffer, timeout.Token);
        var second = await browser.ReceiveAsync(buffer, timeout.Token);

        Assert.Equal(WebSocketMessageType.Binary, first.MessageType);
        Assert.Equal(WebSocketMessageType.Close, second.MessageType);
    }

    [Fact]
    public async Task ClosingTheBrowserClosesTheSidecarConnectionAndDataFromTheBrowserIsIgnored()
    {
        var sidecarClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sidecarReceivedData = false;
        await using var sidecar = await StartSidecarAsync(async (socket, _) =>
        {
            var buffer = new byte[64];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) { sidecarClosed.TrySetResult(true); return; }
                sidecarReceivedData = true;
            }
        }, []);
        await using var relay = await StartRelayAsync(HttpAddress(sidecar));
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(AddressOf(relay, "flydj/stream"), CancellationToken.None);

        await browser.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        Assert.True(await sidecarClosed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(sidecarReceivedData);
    }

    [Fact]
    public async Task UnreachableSidecarAnswers502()
    {
        await using var relay = await StartRelayAsync("http://127.0.0.1:1");
        using var browser = new ClientWebSocket();

        var failure = await Assert.ThrowsAsync<WebSocketException>(() => browser.ConnectAsync(AddressOf(relay, "flydj/stream"), CancellationToken.None));

        Assert.Contains("502", failure.Message);
    }

    [Theory]
    [InlineData("http://flydj:8090", "ws://flydj:8090/v1/stream")]
    [InlineData("http://flydj:8090/", "ws://flydj:8090/v1/stream")]
    [InlineData("https://brain.example.com", "wss://brain.example.com/v1/stream")]
    [InlineData("http://flydj", "ws://flydj/v1/stream")]
    [InlineData("http://host:9000/base/", "ws://host:9000/base/v1/stream")]
    public void StreamUriMirrorsTheServiceUrlOverWebSockets(string serviceUrl, string expected)
        => Assert.Equal(expected, FlyDjStreamRelay.BuildStreamUri(serviceUrl).ToString());
}
