using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Controllers;
using SonosControl.Web.Services;
using SonosControl.Web.Services.SonosEvents;
using Xunit;

namespace SonosControl.Tests;

public sealed class SonosEventsControllerTests
{
    private readonly SonosEventSubscriptionRegistry _registry = new();
    private readonly SonosEventDispatcher _dispatcher;

    public SonosEventsControllerTests()
    {
        var scopes = new ServiceCollection().AddSingleton(Mock.Of<ISettingsRepo>()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var store = new PlaybackSnapshotStore();
        var monitor = new PlaybackSnapshotMonitorService(scopes, store, NullLogger<PlaybackSnapshotMonitorService>.Instance, TimeProvider.System);
        _dispatcher = new SonosEventDispatcher(scopes, store, monitor, _registry, NullLogger<SonosEventDispatcher>.Instance, TimeProvider.System);
    }

    private SonosEventsController Controller(string? sid = "uuid:sub-1", string? nt = "upnp:event", string? nts = "upnp:propchange",
        string? seq = "0", string body = "<e:propertyset/>")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "NOTIFY";
        if (sid is not null) http.Request.Headers["SID"] = sid;
        if (nt is not null) http.Request.Headers["NT"] = nt;
        if (nts is not null) http.Request.Headers["NTS"] = nts;
        if (seq is not null) http.Request.Headers["SEQ"] = seq;
        var bytes = Encoding.UTF8.GetBytes(body);
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentLength = bytes.Length;
        return new SonosEventsController(_registry, _dispatcher, TimeProvider.System) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public async Task Notify_QueuesEventsForKnownSubscriptions()
    {
        var registration = _registry.Register("10.0.0.1", "RINCON_C", SonosEventService.AVTransport);
        registration.SetSid("uuid:sub-1");

        var result = await Controller().NotifyAsync(registration.Token, CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Equal(1, _dispatcher.PendingCount);
    }

    [Fact]
    public async Task Notify_AcceptsTheInitialEventBeforeTheSidIsKnown()
    {
        var registration = _registry.Register("10.0.0.1", "RINCON_C", SonosEventService.AVTransport);

        Assert.IsType<OkResult>(await Controller(sid: "uuid:any").NotifyAsync(registration.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Notify_RejectsUnknownTokens()
    {
        Assert.IsType<NotFoundResult>(await Controller().NotifyAsync("unknown", CancellationToken.None));
        Assert.Equal(0, _dispatcher.PendingCount);
    }

    [Fact]
    public async Task Notify_RejectsAForeignSid()
    {
        var registration = _registry.Register("10.0.0.1", "RINCON_C", SonosEventService.AVTransport);
        registration.SetSid("uuid:sub-1");

        var result = await Controller(sid: "uuid:sub-2").NotifyAsync(registration.Token, CancellationToken.None);

        Assert.Equal(StatusCodes.Status412PreconditionFailed, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(0, _dispatcher.PendingCount);
    }

    [Theory]
    [InlineData(null, "upnp:propchange", "0")]
    [InlineData("upnp:event", "ssdp:alive", "0")]
    [InlineData("upnp:event", "upnp:propchange", null)]
    [InlineData("upnp:event", "upnp:propchange", "-1")]
    [InlineData("upnp:event", "upnp:propchange", "one")]
    public async Task Notify_RejectsMalformedGenaHeaders(string? nt, string? nts, string? seq)
    {
        var registration = _registry.Register("10.0.0.1", "RINCON_C", SonosEventService.AVTransport);

        Assert.IsType<BadRequestResult>(await Controller(nt: nt, nts: nts, seq: seq).NotifyAsync(registration.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Notify_RejectsOversizedBodies()
    {
        var registration = _registry.Register("10.0.0.1", "RINCON_C", SonosEventService.AVTransport);

        var result = await Controller(body: new string('x', SonosEventParser.MaxBodyLength + 1)).NotifyAsync(registration.Token, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(0, _dispatcher.PendingCount);
    }
}
