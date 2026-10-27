using System.Xml;
using System.Xml.Linq;
using SonosControl.DAL.Models;

namespace SonosControl.DAL.Repos;

/// <summary>Parses the body of a UPnP GENA NOTIFY request sent by a Sonos speaker.</summary>
public static class SonosEventParser
{
    public const int MaxBodyLength = 256 * 1024;
    private const string NotImplemented = "NOT_IMPLEMENTED";

    /// <summary>Returns the state carried by the event, or null when the body is not a readable LastChange event.</summary>
    public static SonosPlaybackEvent? Parse(SonosEventService service, string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxBodyLength) return null;
        try
        {
            // LastChange carries the event document as escaped text inside the property set.
            var lastChange = Child(XDocument.Parse(body).Root, "LastChange", descendants: true)?.Value;
            if (string.IsNullOrWhiteSpace(lastChange)) return null;
            var instance = XDocument.Parse(lastChange).Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "InstanceID" && element.Attribute("val")?.Value == "0");
            if (instance is null) return null;

            return service == SonosEventService.RenderingControl
                ? new SonosPlaybackEvent(service) { Volume = ParseVolume(instance) }
                : new SonosPlaybackEvent(service)
                {
                    TransportState = Value(instance, "TransportState"),
                    TransportUri = Value(instance, "AVTransportURI"),
                    TrackUri = Value(instance, "CurrentTrackURI"),
                    TrackNumber = int.TryParse(Value(instance, "CurrentTrack"), out var track) ? track : null,
                    TrackMetadata = Value(instance, "CurrentTrackMetaData")
                };
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static int? ParseVolume(XElement instance)
    {
        var master = instance.Elements().FirstOrDefault(element => element.Name.LocalName == "Volume"
            && string.Equals(element.Attribute("channel")?.Value, "Master", StringComparison.OrdinalIgnoreCase));
        return int.TryParse(master?.Attribute("val")?.Value, out var volume) && volume is >= 0 and <= 100 ? volume : null;
    }

    private static string? Value(XElement instance, string name)
    {
        var value = Child(instance, name)?.Attribute("val")?.Value;
        return value == NotImplemented ? string.Empty : value;
    }

    private static XElement? Child(XElement? parent, string localName, bool descendants = false)
        => (descendants ? parent?.Descendants() : parent?.Elements())?.FirstOrDefault(element => element.Name.LocalName == localName);
}
