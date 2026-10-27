using System.Net;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests;

public sealed class SonosEventSubscriptionClientTests
{
    [Fact]
    public async Task SubscribeAsync_SendsGenaHeadersAndReadsGrant()
    {
        var handler = new RecordingHandler(_ => Response(HttpStatusCode.OK, ("SID", "uuid:RINCON_TEST_sub0000000609"), ("TIMEOUT", "Second-600")));
        var client = new SonosEventSubscriptionClient(new Factory(handler));

        var result = await client.SubscribeAsync("192.0.2.10", SonosEventService.AVTransport,
            new Uri("http://192.0.2.50:8080/api/sonos-events/abc"), TimeSpan.FromMinutes(30));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("SUBSCRIBE", request.Method.Method);
        Assert.Equal("http://192.0.2.10:1400/MediaRenderer/AVTransport/Event", request.RequestUri?.AbsoluteUri);
        Assert.Equal("<http://192.0.2.50:8080/api/sonos-events/abc>", Header(request, "CALLBACK"));
        Assert.Equal("upnp:event", Header(request, "NT"));
        Assert.Equal("Second-1800", Header(request, "TIMEOUT"));
        Assert.Null(Header(request, "SID"));
        Assert.Equal(new SonosEventSubscription("uuid:RINCON_TEST_sub0000000609", TimeSpan.FromSeconds(600)), result);
    }

    [Fact]
    public async Task RenewAsync_SendsSidWithoutCallback()
    {
        var handler = new RecordingHandler(_ => Response(HttpStatusCode.OK, ("SID", "uuid:sub-1"), ("TIMEOUT", "Second-300")));
        var client = new SonosEventSubscriptionClient(new Factory(handler));

        var result = await client.RenewAsync("192.0.2.10", SonosEventService.RenderingControl, "uuid:sub-1", TimeSpan.FromSeconds(300));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("SUBSCRIBE", request.Method.Method);
        Assert.Equal("http://192.0.2.10:1400/MediaRenderer/RenderingControl/Event", request.RequestUri?.AbsoluteUri);
        Assert.Equal("uuid:sub-1", Header(request, "SID"));
        Assert.Null(Header(request, "CALLBACK"));
        Assert.Null(Header(request, "NT"));
        Assert.Equal(TimeSpan.FromSeconds(300), result?.Timeout);
    }

    [Fact]
    public async Task RenewAsync_ReturnsNullWhenSubscriptionIsGone()
    {
        var client = new SonosEventSubscriptionClient(new Factory(new RecordingHandler(_ => Response(HttpStatusCode.PreconditionFailed))));

        Assert.Null(await client.RenewAsync("192.0.2.10", SonosEventService.AVTransport, "uuid:unknown", TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public async Task UnsubscribeAsync_SendsSidAndReportsFailureWithoutThrowing()
    {
        var handler = new RecordingHandler(_ => Response(HttpStatusCode.PreconditionFailed));
        var client = new SonosEventSubscriptionClient(new Factory(handler));

        var result = await client.UnsubscribeAsync("192.0.2.10", SonosEventService.AVTransport, "uuid:sub-1");

        Assert.False(result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("UNSUBSCRIBE", request.Method.Method);
        Assert.Equal("uuid:sub-1", Header(request, "SID"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SubscribeAsync_ThrowsSonosCommandExceptionWhenRejected(HttpStatusCode status)
    {
        var client = new SonosEventSubscriptionClient(new Factory(new RecordingHandler(_ => Response(status))));

        var failure = await Assert.ThrowsAsync<SonosCommandException>(() => client.SubscribeAsync("192.0.2.10",
            SonosEventService.AVTransport, new Uri("http://192.0.2.50/api/sonos-events/abc"), TimeSpan.FromSeconds(300)));

        Assert.Equal(status, failure.StatusCode);
        Assert.Equal("AVTransport", failure.Service);
    }

    [Fact]
    public async Task SubscribeAsync_RequiresSid()
    {
        var client = new SonosEventSubscriptionClient(new Factory(new RecordingHandler(_ => Response(HttpStatusCode.OK, ("TIMEOUT", "Second-300")))));

        await Assert.ThrowsAsync<SonosCommandException>(() => client.SubscribeAsync("192.0.2.10",
            SonosEventService.AVTransport, new Uri("http://192.0.2.50/api/sonos-events/abc"), TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public async Task SubscribeAsync_WrapsTransportFailures()
    {
        var client = new SonosEventSubscriptionClient(new Factory(new RecordingHandler(_ => throw new HttpRequestException("unreachable"))));

        var failure = await Assert.ThrowsAsync<SonosCommandException>(() => client.SubscribeAsync("192.0.2.10",
            SonosEventService.RenderingControl, new Uri("http://192.0.2.50/api/sonos-events/abc"), TimeSpan.FromSeconds(300)));

        Assert.Null(failure.StatusCode);
        Assert.IsType<HttpRequestException>(failure.InnerException);
    }

    [Theory]
    [InlineData("Second-600", 600)]
    [InlineData("second-45", 45)]
    [InlineData("Second-infinite", 300)]
    [InlineData("Second-0", 300)]
    [InlineData(null, 300)]
    public void ParseTimeout_FallsBackToRequestedLifetime(string? header, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SonosEventSubscriptionClient.ParseTimeout(header, TimeSpan.FromSeconds(300)));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status);
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
