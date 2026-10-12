using System.Net;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using SonosControl.DAL.Models;

namespace SonosControl.DAL.Repos;

internal static class SonosMetadataParser
{
    internal static string NormalizeUuid(string value) => value.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? value[5..] : value;

    internal static HashSet<string> ParseGroupMembers(string response, string requestedId)
    {
        var topology = XDocument.Parse(RequiredValue(response, "ZoneGroupState"));
        var group = topology.Descendants().FirstOrDefault(element => element.Name.LocalName == "ZoneGroup"
            && element.Descendants().Any(member => member.Name.LocalName == "ZoneGroupMember"
                && string.Equals(NormalizeUuid(member.Attribute("UUID")?.Value ?? ""), requestedId, StringComparison.OrdinalIgnoreCase)));
        if (group is null) throw new InvalidDataException($"The speaker topology does not contain {requestedId}.");
        return group.Descendants().Where(member => member.Name.LocalName == "ZoneGroupMember")
            .Select(member => NormalizeUuid(member.Attribute("UUID")?.Value ?? ""))
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static string RequiredValue(string response, string elementName)
        => XDocument.Parse(response).Descendants().FirstOrDefault(element => element.Name.LocalName == elementName)?.Value
            ?? throw new InvalidDataException($"The speaker response is missing {elementName}.");

    internal static SonosQueuePage ParseQueueResponse(string responseBody, int startIndex, int requestedCount)
    {
        var items = new List<SonosQueueItem>();
        int numberReturned = 0;
        int totalMatches = 0;

        try
        {
            var soapDoc = XDocument.Parse(responseBody);
            var browseResponse = soapDoc
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "BrowseResponse");

            if (browseResponse is null)
            {
                return new SonosQueuePage(items, startIndex, numberReturned, totalMatches);
            }

            numberReturned = ParseIntSafe(browseResponse.Elements().FirstOrDefault(e => e.Name.LocalName == "NumberReturned")?.Value);
            totalMatches = ParseIntSafe(browseResponse.Elements().FirstOrDefault(e => e.Name.LocalName == "TotalMatches")?.Value);

            var resultElement = browseResponse.Elements().FirstOrDefault(e => e.Name.LocalName == "Result");
            if (resultElement is null)
            {
                return new SonosQueuePage(items, startIndex, numberReturned, totalMatches);
            }

            var didlPayload = resultElement.Value;
            if (string.IsNullOrWhiteSpace(didlPayload))
            {
                return new SonosQueuePage(items, startIndex, numberReturned, totalMatches);
            }

            XDocument didl;
            try
            {
                didl = XDocument.Parse(didlPayload);
            }
            catch (XmlException)
            {
                var decoded = WebUtility.HtmlDecode(didlPayload);
                if (string.IsNullOrWhiteSpace(decoded))
                {
                    return new SonosQueuePage(items, startIndex, numberReturned, totalMatches);
                }

                didl = XDocument.Parse(decoded);
            }

            var itemElements = didl.Root?
                .Elements()
                .Where(e => e.Name.LocalName == "item")
                ?? Enumerable.Empty<XElement>();

            foreach (var element in itemElements)
            {
                var parsedItem = ParseQueueItem(element, startIndex + items.Count);
                if (parsedItem is not null)
                {
                    items.Add(parsedItem);
                }
            }
        }
        catch (Exception)
        {

        }

        if (numberReturned == 0)
        {
            numberReturned = items.Count;
        }

        if (totalMatches == 0)
        {
            totalMatches = startIndex + items.Count;
            if (items.Count == requestedCount)
            {
                totalMatches += 1; // Assume more items exist when exactly the requested count was returned
            }
        }

        return new SonosQueuePage(items, startIndex, numberReturned, totalMatches);
    }

    internal static SonosQueueItem? ParseQueueItem(XElement element, int index)
    {
        string title = GetFirstValue(element, "title", "http://purl.org/dc/elements/1.1/") ?? string.Empty;
        string? artist = GetFirstValue(element, "artist", "urn:schemas-upnp-org:metadata-1-0/upnp/")
            ?? GetFirstValue(element, "creator", "http://purl.org/dc/elements/1.1/");
        string? album = GetFirstValue(element, "album", "urn:schemas-upnp-org:metadata-1-0/upnp/");
        string? resourceUri = element.Elements().FirstOrDefault(e => e.Name.LocalName == "res")?.Value;

        ApplyStreamContentFallback(element, ref title, ref artist);

        var metadataOverride = ParseResourceMetadata(element.Elements().FirstOrDefault(e => e.Name.LocalName == "resMD"));
        if (metadataOverride is not null)
        {
            if (!string.IsNullOrWhiteSpace(metadataOverride.Value.Title))
            {
                title = metadataOverride.Value.Title;
            }

            if (!string.IsNullOrWhiteSpace(metadataOverride.Value.Artist))
            {
                artist = metadataOverride.Value.Artist;
            }

            if (!string.IsNullOrWhiteSpace(metadataOverride.Value.Album))
            {
                album = metadataOverride.Value.Album;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = resourceUri ?? "Unknown title";
        }

        return new SonosQueueItem(index, title.Trim(), artist?.Trim(), album?.Trim(), resourceUri?.Trim());
    }

    internal static bool TryParseYouTubePlaybackUri(string? resourceUri, out string sessionId, out int itemIndex)
    {
        sessionId = string.Empty;
        itemIndex = 0;

        if (string.IsNullOrWhiteSpace(resourceUri))
        {
            return false;
        }

        var match = Regex.Match(
            resourceUri,
            @"/api/youtube-audio/(?<sessionId>[A-Za-z0-9]+)/(?<itemIndex>\d+)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return false;
        }

        sessionId = match.Groups["sessionId"].Value;
        return int.TryParse(match.Groups["itemIndex"].Value, out itemIndex);
    }

    internal static void ApplyStreamContentFallback(XElement element, ref string title, ref string? artist)
    {
        var streamContent = element.Elements().FirstOrDefault(e => e.Name.LocalName == "streamContent")?.Value;
        if (string.IsNullOrWhiteSpace(streamContent))
        {
            return;
        }

        var parts = streamContent.Split(" - ", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            artist ??= parts[0];
            if (string.IsNullOrWhiteSpace(title))
            {
                title = parts[1];
            }
        }
        else if (string.IsNullOrWhiteSpace(title))
        {
            title = streamContent;
        }
    }

    internal static (string? Title, string? Artist, string? Album)? ParseResourceMetadata(XElement? resMdElement)
    {
        if (resMdElement is null)
        {
            return null;
        }

        if (resMdElement.HasElements)
        {
            var nestedItem = resMdElement
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "item");

            if (nestedItem is not null)
            {
                var nestedTitle = GetFirstValue(nestedItem, "title", "http://purl.org/dc/elements/1.1/");
                var nestedArtist = GetFirstValue(nestedItem, "artist", "urn:schemas-upnp-org:metadata-1-0/upnp/")
                    ?? GetFirstValue(nestedItem, "creator", "http://purl.org/dc/elements/1.1/");
                var nestedAlbum = GetFirstValue(nestedItem, "album", "urn:schemas-upnp-org:metadata-1-0/upnp/");

                return (nestedTitle, nestedArtist, nestedAlbum);
            }
        }

        var rawMetadata = resMdElement.Value;
        if (string.IsNullOrWhiteSpace(rawMetadata))
        {
            return null;
        }

        var decoded = (WebUtility.HtmlDecode(rawMetadata) ?? rawMetadata).Trim();

        try
        {
            var doc = XDocument.Parse(decoded);
            var item = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "item");
            if (item is null)
            {
                return null;
            }

            var title = GetFirstValue(item, "title", "http://purl.org/dc/elements/1.1/");
            var artist = GetFirstValue(item, "artist", "urn:schemas-upnp-org:metadata-1-0/upnp/")
                ?? GetFirstValue(item, "creator", "http://purl.org/dc/elements/1.1/");
            var album = GetFirstValue(item, "album", "urn:schemas-upnp-org:metadata-1-0/upnp/");

            return (title, artist, album);
        }
        catch (Exception)
        {

            return null;
        }
    }

    internal static string? GetFirstValue(XElement element, string localName, string? ns = null)
    {
        if (element is null)
        {
            return null;
        }

        IEnumerable<XElement> candidates;
        if (string.IsNullOrWhiteSpace(ns))
        {
            candidates = element.Elements().Where(e => e.Name.LocalName == localName);
        }
        else
        {
            candidates = element.Elements(XName.Get(localName, ns));
        }

        return candidates.FirstOrDefault()?.Value;
    }

    internal static int ParseIntSafe(string? value)
    {
        return int.TryParse(value, out var result) ? result : 0;
    }


    internal static string DecodeMetadataText(string? value)
    {
        return WebUtility.HtmlDecode(value)?.Trim() ?? string.Empty;
    }

    internal static int? TryParseTrackNumber(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return int.TryParse(doc.GetElementsByTagName("Track").Item(0)?.InnerText, out var trackNumber)
                ? trackNumber
                : null;
        }
        catch
        {
            return null;
        }
    }

}
