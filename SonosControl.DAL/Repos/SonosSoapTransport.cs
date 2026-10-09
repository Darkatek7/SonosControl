using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SonosControl.DAL.Repos;

public sealed class SonosSoapTransport(ILogger<SonosSoapTransport>? logger = null)
{
    private readonly ILogger<SonosSoapTransport> _logger = logger ?? NullLogger<SonosSoapTransport>.Instance;

    public async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, HttpContent content, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await client.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(body) && XDocument.Parse(body).Descendants().Any(element => element.Name.LocalName == "Fault"))
                throw new HttpRequestException("The speaker rejected the SOAP command.", null, response.StatusCode);
            return response;
        }
        catch (OperationCanceledException) { response?.Dispose(); throw; }
        catch (Exception ex)
        {
            response?.Dispose();
            _logger.LogWarning(ex, "Sonos SOAP request failed at {Endpoint}.", url);
            throw;
        }
    }

    public async Task<string> SendAsync(HttpClient client, string ip, string action, string arguments, CancellationToken cancellationToken, string service = "AVTransport")
    {
        var envelope = $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"urn:schemas-upnp-org:service:{service}:1\"><InstanceID>0</InstanceID>{arguments}</u:{action}></s:Body></s:Envelope>";
        using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPACTION", $"\"urn:schemas-upnp-org:service:{service}:1#{action}\"");
        using var response = await PostAsync(client, $"http://{ip}:1400/MediaRenderer/{service}/Control", content, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
