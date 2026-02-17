using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public interface ISonosDeviceDiscoveryService
{
    Task<IReadOnlyList<SonosSpeaker>> DiscoverAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

public sealed class SonosDeviceDiscoveryService : ISonosDeviceDiscoveryService
{
    private static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SonosDeviceDiscoveryService> _logger;

    public SonosDeviceDiscoveryService(IHttpClientFactory httpClientFactory, ILogger<SonosDeviceDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SonosSpeaker>> DiscoverAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(4);
        var discovered = new Dictionary<string, SonosSpeaker>(StringComparer.OrdinalIgnoreCase);

        using var udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.EnableBroadcast = true;
        udpClient.MulticastLoopback = false;

        var query = string.Join("\r\n",
            "M-SEARCH * HTTP/1.1",
            "HOST: 239.255.255.250:1900",
            "MAN: \"ssdp:discover\"",
            "MX: 2",
            "ST: urn:schemas-upnp-org:device:ZonePlayer:1",
            string.Empty,
            string.Empty);

        var payload = Encoding.ASCII.GetBytes(query);
        await udpClient.SendAsync(payload, payload.Length, SsdpEndpoint);

        var deadline = DateTime.UtcNow.Add(effectiveTimeout);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var receiveTask = udpClient.ReceiveAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(remaining, cancellationToken));
            if (completed != receiveTask)
            {
                break;
            }

            UdpReceiveResult result;
            try
            {
                result = await receiveTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SSDP receive failed.");
                continue;
            }

            var responseText = Encoding.UTF8.GetString(result.Buffer);
            var location = ExtractHeaderValue(responseText, "LOCATION");
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            var speaker = await BuildSpeakerFromLocationAsync(location, cancellationToken);
            if (speaker is not null && !string.IsNullOrWhiteSpace(speaker.IpAddress))
            {
                discovered[speaker.IpAddress] = speaker;
            }
        }

        return discovered.Values.OrderBy(s => s.Name).ToList();
    }

    private async Task<SonosSpeaker?> BuildSpeakerFromLocationAsync(string location, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(location);
            var ipAddress = uri.Host;

            var client = _httpClientFactory.CreateClient(nameof(SonosDeviceDiscoveryService));
            using var response = await client.GetAsync(location, cancellationToken);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);

            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null)
            {
                return new SonosSpeaker { Name = $"Sonos {ipAddress}", IpAddress = ipAddress };
            }

            var roomName = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "roomName")?.Value;
            var udn = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "UDN")?.Value;

            return new SonosSpeaker
            {
                Name = string.IsNullOrWhiteSpace(roomName) ? $"Sonos {ipAddress}" : roomName.Trim(),
                IpAddress = ipAddress,
                Uuid = string.IsNullOrWhiteSpace(udn) ? null : udn.Trim()
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse Sonos device description from {Location}.", location);
            return null;
        }
    }

    private static string? ExtractHeaderValue(string response, string headerName)
    {
        var lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var candidateName = line[..separatorIndex].Trim();
            if (!string.Equals(candidateName, headerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(separatorIndex + 1)..].Trim();
        }

        return null;
    }
}
