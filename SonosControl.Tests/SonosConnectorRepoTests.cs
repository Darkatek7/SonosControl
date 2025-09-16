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
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public HttpResponseMessage Response { get; set; } = new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(Response);
        }
    }

    [Fact]
    public async Task NextTrack_SendsCorrectSoapRequest()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(client);

        await repo.NextTrack("1.2.3.4");

        Assert.Equal("http://1.2.3.4:1400/MediaRenderer/AVTransport/Control", handler.Request!.RequestUri.ToString());
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#Next\"", handler.Request!.Content.Headers.GetValues("SOAPACTION").First());
    }

    [Fact]
    public async Task GetQueue_ParsesTrackTitles()
    {
        var xml = "<dc:title>Track1</dc:title><dc:title>Track2</dc:title>";
        var handler = new MockHttpMessageHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<s:Envelope><s:Body>{xml}</s:Body></s:Envelope>")
            }
        };
        var client = new HttpClient(handler);
        var repo = new SonosConnectorRepo(client);

        var result = await repo.GetQueue("1.2.3.4");

        Assert.Equal(new[] { "Track1", "Track2" }, result);
        Assert.Equal("\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"", handler.Request!.Content.Headers.GetValues("SOAPACTION").First());
    }

    [Fact]
    public async Task SetTuneInStationAsync_PostsSoapRequest()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var repo = new TestableSonosConnectorRepo(client);

        await repo.SetTuneInStationAsync("1.2.3.4", "example.com/stream");

        Assert.Equal("http://1.2.3.4:1400/MediaRenderer/AVTransport/Control", handler.Request!.RequestUri.ToString());
        var body = await handler.Request!.Content.ReadAsStringAsync();
        Assert.Contains("x-rincon-mp3radio://example.com/stream", body);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#SetAVTransportURI\"", handler.Request!.Content.Headers.GetValues("SOAPACTION").First());
    }

    private class TestableSonosConnectorRepo : SonosConnectorRepo
    {
        public TestableSonosConnectorRepo(HttpClient client) : base(client) { }
        public override Task StartPlaying(string ip) => Task.CompletedTask;
    }
}
