using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Repos;
using Moq;
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
            : base(httpClientFactory, new Mock<ISettingsRepo>().Object)
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
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client), new Mock<ISettingsRepo>().Object);

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
        const string soapResponse = """
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
  <s:Body>
    <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
      <Result>&lt;DIDL-Lite xmlns:dc='http://purl.org/dc/elements/1.1/' xmlns:upnp='urn:schemas-upnp-org:metadata-1-0/upnp/'&gt;
        &lt;item id='1'&gt;
          &lt;dc:title&gt;Track1&lt;/dc:title&gt;
        &lt;/item&gt;
        &lt;item id='2'&gt;
          &lt;dc:title&gt;Track2&lt;/dc:title&gt;
        &lt;/item&gt;
      &lt;/DIDL-Lite&gt;</Result>
      <NumberReturned>2</NumberReturned>
      <TotalMatches>2</TotalMatches>
      <UpdateID>1</UpdateID>
    </u:BrowseResponse>
  </s:Body>
</s:Envelope>
""";
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(soapResponse)
        });
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client), new Mock<ISettingsRepo>().Object);

        var result = await repo.GetQueue("1.2.3.4");

        Assert.Equal(new[] { "Track1", "Track2" }, result.Items.Select(i => i.Title));
        Assert.False(result.HasMore);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://1.2.3.4:1400/MediaServer/ContentDirectory/Control", request.Uri!.ToString());
        Assert.Equal("\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"", request.SoapAction);
    }

    [Fact]
    public async Task GetQueue_ParsesSpotifyMetadata()
    {
        var handler = new QueueHttpMessageHandler();
        const string soapResponse = """
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
  <s:Body>
    <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
      <Result>&lt;DIDL-Lite xmlns:dc='http://purl.org/dc/elements/1.1/' xmlns:upnp='urn:schemas-upnp-org:metadata-1-0/upnp/' xmlns:r='urn:schemas-rinconnetworks-com:metadata-1-0/'&gt;
        &lt;item id='3'&gt;
          &lt;dc:title&gt;Placeholder&lt;/dc:title&gt;
          &lt;r:resMD&gt;&lt;DIDL-Lite xmlns:dc='http://purl.org/dc/elements/1.1/' xmlns:upnp='urn:schemas-upnp-org:metadata-1-0/upnp/'&gt;
            &lt;item&gt;
              &lt;dc:title&gt;Song Title&lt;/dc:title&gt;
              &lt;dc:creator&gt;Artist Name&lt;/dc:creator&gt;
              &lt;upnp:album&gt;Album Name&lt;/upnp:album&gt;
            &lt;/item&gt;
          &lt;/DIDL-Lite&gt;&lt;/r:resMD&gt;
        &lt;/item&gt;
      &lt;/DIDL-Lite&gt;</Result>
      <NumberReturned>1</NumberReturned>
      <TotalMatches>1</TotalMatches>
      <UpdateID>1</UpdateID>
    </u:BrowseResponse>
  </s:Body>
</s:Envelope>
""";
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(soapResponse)
        });
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client), new Mock<ISettingsRepo>().Object);

        var result = await repo.GetQueue("1.2.3.4");

        var item = Assert.Single(result.Items);
        Assert.Equal("Song Title", item.Title);
        Assert.Equal("Artist Name", item.Artist);
        Assert.Equal("Album Name", item.Album);
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
    public async Task RebootDeviceAsync_PostsToRebootEndpoint()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(new TestHttpClientFactory(client), new Mock<ISettingsRepo>().Object);

        await repo.RebootDeviceAsync("1.2.3.4");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://1.2.3.4:1400/reboot", request.Uri!.ToString());
    }
}
