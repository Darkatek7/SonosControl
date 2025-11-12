using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests;

public class SonosConnectorRepoTests
{
    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        internal sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? SoapAction, string? Body);

        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<CapturedRequest> Requests { get; } = new();

        public void Enqueue(HttpResponseMessage response)
        {
            _responses.Enqueue(response);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            string? soapAction = null;

            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);

                if (request.Content.Headers.TryGetValues("SOAPACTION", out var values))
                {
                    soapAction = values.FirstOrDefault();
                }
            }

            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, soapAction, body));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response configured.");
            }

            return _responses.Dequeue();
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public TestHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class TestableSonosConnectorRepo : SonosConnectorRepo
    {
        public int StartPlayingCallCount { get; private set; }

        public TestableSonosConnectorRepo(IHttpClientFactory httpClientFactory)
            : base(httpClientFactory)
        {
        }

        public override Task StartPlaying(string ip)
        {
            StartPlayingCallCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NextTrack_SendsCorrectSoapRequest()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.NextTrack("1.2.3.4");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://1.2.3.4:1400/MediaRenderer/AVTransport/Control", request.Uri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#Next\"", request.SoapAction);
    }

    [Fact]
    public async Task GetQueue_ParsesTrackTitles()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<s:Envelope><s:Body><dc:title>Track1</dc:title><dc:title>Track2</dc:title></s:Body></s:Envelope>")
        });
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client));

        var result = await repo.GetQueue("1.2.3.4");

        Assert.Equal(new[] { "Track1", "Track2" }, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"", request.SoapAction);
    }

    [Fact]
    public async Task SetTuneInStationAsync_Success_PostsSoapRequestAndStartsPlayback()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var repo = new TestableSonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.SetTuneInStationAsync("1.2.3.4", "example.com/stream");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://1.2.3.4:1400/MediaRenderer/AVTransport/Control", request.Uri!.ToString());
        Assert.NotNull(request.Body);
        Assert.Contains("x-rincon-mp3radio://example.com/stream", request.Body);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"", request.SoapAction);
        Assert.Equal(1, repo.StartPlayingCallCount);
    }

    [Fact]
    public async Task SetTuneInStationAsync_Failure_DoesNotStartPlayback()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new HttpClient(handler);
        var repo = new TestableSonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.SetTuneInStationAsync("1.2.3.4", "example.com/stream");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"", request.SoapAction);
        Assert.Equal(0, repo.StartPlayingCallCount);
    }

    [Fact]
    public async Task PlaySpotifyTrackAsync_Success_UsesFallbackAndStartsPlayback()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<root><UDN>uuid:RINCON_ABC123</UDN></root>")
        });
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var repo = new TestableSonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.PlaySpotifyTrackAsync("1.2.3.4", "https://open.spotify.com/track/12345", "fallback-station");

        Assert.Equal(3, handler.Requests.Count);

        var fallbackRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, fallbackRequest.Method);
        Assert.NotNull(fallbackRequest.Body);
        Assert.Contains("fallback-station", fallbackRequest.Body);

        var deviceDescriptionRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, deviceDescriptionRequest.Method);
        Assert.Equal("http://1.2.3.4:1400/xml/device_description.xml", deviceDescriptionRequest.Uri!.ToString());

        var spotifyRequest = handler.Requests[2];
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"", spotifyRequest.SoapAction);
        Assert.Equal(2, repo.StartPlayingCallCount);
    }

    [Fact]
    public async Task PlaySpotifyTrackAsync_Failure_DoesNotStartPlayback()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<root><UDN>uuid:RINCON_ABC123</UDN></root>")
        });
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new HttpClient(handler);
        var repo = new TestableSonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.PlaySpotifyTrackAsync("1.2.3.4", "https://open.spotify.com/track/12345");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal(0, repo.StartPlayingCallCount);
    }

    [Fact]
    public async Task DiscoverPlayersAsync_ParsesZonePlayers()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<ZonePlayers><ZonePlayer uuid=\"RINCON_000\" location=\"http://1.2.3.4:1400/xml/device_description.xml\">Kitchen</ZonePlayer><ZonePlayer uuid=\"RINCON_111\" location=\"http://5.6.7.8:1400/xml/device_description.xml\">Office</ZonePlayer></ZonePlayers>")
        });
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client));

        var players = await repo.DiscoverPlayersAsync("1.2.3.4");

        Assert.Equal(2, players.Count);
        Assert.Contains(players, p => p.IpAddress == "1.2.3.4" && p.Name == "Kitchen" && p.Uuid == "RINCON_000");
        Assert.Contains(players, p => p.IpAddress == "5.6.7.8" && p.Name == "Office" && p.Uuid == "RINCON_111");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://1.2.3.4:1400/status/topology", request.Uri!.ToString());
    }

    [Fact]
    public async Task JoinGroupAsync_SendsJoinSoap()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<ZonePlayers><ZonePlayer uuid=\"RINCON_123\" location=\"http://1.1.1.1:1400/xml/device_description.xml\">Living Room</ZonePlayer></ZonePlayers>")
        });
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.JoinGroupAsync("1.1.1.1", "2.2.2.2");

        Assert.Equal(2, handler.Requests.Count);
        var topologyRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, topologyRequest.Method);
        Assert.Equal("http://1.1.1.1:1400/status/topology", topologyRequest.Uri!.ToString());

        var joinRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, joinRequest.Method);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"", joinRequest.SoapAction);
        Assert.Equal("http://2.2.2.2:1400/MediaRenderer/AVTransport/Control", joinRequest.Uri!.ToString());
        Assert.NotNull(joinRequest.Body);
        Assert.Contains("x-rincon:RINCON_123", joinRequest.Body);
    }

    [Fact]
    public async Task LeaveGroupAsync_SendsStandaloneSoap()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.LeaveGroupAsync("3.3.3.3");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#BecomeCoordinatorOfStandaloneGroup\"", request.SoapAction);
        Assert.Equal("http://3.3.3.3:1400/MediaRenderer/AVTransport/Control", request.Uri!.ToString());
    }

    [Fact]
    public async Task SetGroupCoordinatorAsync_DelegatesToLeaveGroup()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.SetGroupCoordinatorAsync("4.4.4.4");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#BecomeCoordinatorOfStandaloneGroup\"", request.SoapAction);
        Assert.Equal("http://4.4.4.4:1400/MediaRenderer/AVTransport/Control", request.Uri!.ToString());
    }

    [Fact]
    public async Task RebootDeviceAsync_PostsToRebootEndpoint()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client));

        await repo.RebootDeviceAsync("1.2.3.4");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://1.2.3.4:1400/reboot", request.Uri!.ToString());
    }
}
