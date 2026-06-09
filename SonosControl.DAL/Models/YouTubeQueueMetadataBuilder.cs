using System.Security;

namespace SonosControl.DAL.Models;

public static class YouTubeQueueMetadataBuilder
{
    public static string Build(YouTubePlaybackQueueItem item)
        => Build(item.Title, item.StreamUrl, item.Artist, item.AlbumArtUrl, item.StreamContent);

    public static string Build(string title, string uri, string? artist = null, string? albumArtUrl = null, string? streamContent = null)
    {
        var safeTitle = EscapeOrFallback(title, "YouTube Audio");
        var safeUri = EscapeOrFallback(uri, string.Empty);
        var safeArtist = EscapeOrFallback(artist, string.Empty);
        var safeStreamContent = EscapeOrFallback(
            string.IsNullOrWhiteSpace(streamContent) ? FormatStreamContent(title, artist) : streamContent,
            string.Empty);
        var safeAlbumArtUrl = EscapeOrFallback(albumArtUrl, string.Empty);

        var albumArtMarkup = string.IsNullOrWhiteSpace(safeAlbumArtUrl)
            ? string.Empty
            : $@"<upnp:albumArtURI>{safeAlbumArtUrl}</upnp:albumArtURI>";

        var creatorMarkup = string.IsNullOrWhiteSpace(safeArtist)
            ? string.Empty
            : $@"<dc:creator>{safeArtist}</dc:creator><upnp:artist>{safeArtist}</upnp:artist>";

        var streamContentMarkup = string.IsNullOrWhiteSpace(safeStreamContent)
            ? string.Empty
            : $@"<r:streamContent>{safeStreamContent}</r:streamContent>";

        return $@"<DIDL-Lite xmlns:dc=""http://purl.org/dc/elements/1.1/""
                               xmlns:upnp=""urn:schemas-upnp-org:metadata-1-0/upnp/""
                               xmlns:r=""urn:schemas-rinconnetworks-com:metadata-1-0/""
                               xmlns=""urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"">
                    <item id=""0"" parentID=""-1"" restricted=""true"">
                        <dc:title>{safeTitle}</dc:title>
                        {creatorMarkup}
                        {streamContentMarkup}
                        {albumArtMarkup}
                        <upnp:class>object.item.audioItem.musicTrack</upnp:class>
                        <res protocolInfo=""http-get:*:audio/mpeg:*"">{safeUri}</res>
                    </item>
                 </DIDL-Lite>";
    }

    public static string FormatStreamContent(string? title, string? artist)
    {
        var trimmedTitle = title?.Trim();
        var trimmedArtist = artist?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmedArtist) && !string.IsNullOrWhiteSpace(trimmedTitle))
        {
            if (StartsWithArtist(trimmedTitle, trimmedArtist))
            {
                return trimmedTitle;
            }

            return $"{trimmedArtist} - {trimmedTitle}";
        }

        if (!string.IsNullOrWhiteSpace(trimmedTitle))
        {
            return trimmedTitle;
        }

        return string.IsNullOrWhiteSpace(trimmedArtist) ? "YouTube Audio" : trimmedArtist;
    }

    public static bool StartsWithArtist(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return false;
        }

        var trimmedTitle = title.Trim();
        var trimmedArtist = artist.Trim();
        return trimmedTitle.StartsWith($"{trimmedArtist} - ", StringComparison.OrdinalIgnoreCase)
            || trimmedTitle.StartsWith($"{trimmedArtist} – ", StringComparison.OrdinalIgnoreCase)
            || trimmedTitle.StartsWith($"{trimmedArtist}: ", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeOrFallback(string? value, string fallback)
        => SecurityElement.Escape(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()) ?? fallback;
}
