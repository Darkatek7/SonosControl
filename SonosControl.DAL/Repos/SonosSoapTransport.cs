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
        var speakerIp = new Uri(url).Host;
        var soapAction = content.Headers.TryGetValues("SOAPACTION", out var values) ? values.First().Trim('"') : "";
        var parts = soapAction.Split('#');
        var service = parts[0].Split(':').ElementAtOrDefault(3) ?? "SOAP";
        var action = parts.ElementAtOrDefault(1) ?? "Request";
        HttpResponseMessage? response = null;
        try
        {
            response = await client.PostAsync(url, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            XElement? fault = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try { fault = XDocument.Parse(body).Descendants().FirstOrDefault(element => element.Name.LocalName == "Fault"); }
                catch (System.Xml.XmlException) when (!response.IsSuccessStatusCode) { }
            }
            if (fault is not null || !response.IsSuccessStatusCode)
            {
                string? Value(string name) => fault?.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
                var code = int.TryParse(Value("errorCode"), out var parsed) ? parsed : (int?)null;
                var description = Value("errorDescription") ?? Value("faultstring")
                    ?? (fault is not null ? "The speaker rejected the SOAP command." : response.ReasonPhrase ?? "The speaker returned an HTTP error without a readable SOAP fault.");
                throw new SonosCommandException(speakerIp, service, action, response.StatusCode, code, description);
            }
            return response;
        }
        catch (OperationCanceledException) { response?.Dispose(); throw; }
        catch (Exception ex)
        {
            var failure = ex as SonosCommandException ?? new SonosCommandException(speakerIp, service, action, response?.StatusCode, null, ex.Message, ex);
            response?.Dispose();
            _logger.LogWarning(failure, "Sonos command {Service}.{Action} failed on {SpeakerIp} at {Endpoint}, HTTP {StatusCode}, UPnP {UpnpErrorCode}: {Description}.",
                service, action, speakerIp, url, failure.StatusCode, failure.UpnpErrorCode, failure.ErrorDescription);
            throw failure;
        }
    }

    public async Task<string> SendAsync(HttpClient client, string ip, string action, string arguments, CancellationToken cancellationToken, string service = "AVTransport")
    {
        var instance = service == "ZoneGroupTopology" ? "" : "<InstanceID>0</InstanceID>";
        var envelope = $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"urn:schemas-upnp-org:service:{service}:1\">{instance}{arguments}</u:{action}></s:Body></s:Envelope>";
        using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPACTION", $"\"urn:schemas-upnp-org:service:{service}:1#{action}\"");
        var path = service == "ZoneGroupTopology" ? "ZoneGroupTopology/Control" : $"MediaRenderer/{service}/Control";
        using var response = await PostAsync(client, $"http://{ip}:1400/{path}", content, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
