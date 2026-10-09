using System.Text.RegularExpressions;

namespace SonosControl.DAL.Repos;

internal static class SonosMediaSourceBuilder
{
    internal static string Radio(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = source.Trim();
        if (source.StartsWith("x-sonosapi-stream:", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("x-sonosapi-radio:", StringComparison.OrdinalIgnoreCase)) return source;
        foreach (var prefix in new[] { "x-rincon-mp3radio://", "https://", "http://" })
            if (source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { source = source[prefix.Length..]; break; }
        if (source.Any(char.IsWhiteSpace) || !Uri.TryCreate("http://" + source, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host) || source.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("A radio stream URL is required.", nameof(source));
        return "x-rincon-mp3radio://" + source;
    }

    internal static (string Uri, string Metadata) Spotify(string source, string rinconId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var match = Regex.Match(source.Trim(), @"^(?:https?://open\.spotify\.com/(?:intl-[^/]+/)?|spotify:)(?<type>track|playlist|album)[/:](?<id>[A-Za-z0-9]+)(?:[?#].*)?$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new ArgumentException("Invalid Spotify URL.", nameof(source));
        var type = match.Groups["type"].Value.ToLowerInvariant();
        var id = match.Groups["id"].Value;
        var prefix = type == "track" ? "00032020" : "00020000";
        var metadata = Metadata($"{prefix}spotify%3a{type}%3a{id}", "00020000spotify", $"Spotify {Title(type)}", MediaClass(type), "SA_RINCON2311_X_#Svc2311-0-Token");
        return ($"x-sonos-vli:RINCON_{rinconId}:2,spotify:{id}", metadata);
    }

    internal static (string Uri, string Metadata) YouTubeMusic(string source, string rinconId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = source.Trim();
        var custom = Regex.Match(source, @"^(?:ytm|youtube(?:music)?):(?<type>track|playlist):(?<id>[A-Za-z0-9_-]+)$", RegexOptions.IgnoreCase);
        string type;
        string id;
        if (custom.Success)
        {
            type = custom.Groups["type"].Value.ToLowerInvariant();
            id = custom.Groups["id"].Value;
        }
        else
        {
            if (!System.Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.Host.ToLowerInvariant() is not ("music.youtube.com" or "www.youtube.com" or "youtube.com"))
                throw new ArgumentException("Invalid YouTube Music URL.", nameof(source));
            var playlist = Regex.Match(uri.Query, @"[?&]list=(?<id>[A-Za-z0-9_-]+)(?:&|$)");
            var video = Regex.Match(uri.Query, @"[?&]v=(?<id>[A-Za-z0-9_-]+)(?:&|$)");
            type = playlist.Success ? "playlist" : "track";
            id = playlist.Success ? playlist.Groups["id"].Value : video.Groups["id"].Value;
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Invalid YouTube Music URL.", nameof(source));
        }
        var prefix = type == "playlist" ? "0006206c" : "0004206c";
        var metadata = Metadata($"{prefix}youtubemusic%3a{type}%3a{System.Uri.EscapeDataString(id)}", "0006206cyoutubemusic", $"YouTube Music {Title(type)}", MediaClass(type), "SA_RINCON51463_X_#Svc51463-0-Token");
        return ($"x-sonos-vli:RINCON_{rinconId}:4,youtubemusic:{type}:{id}", metadata);
    }

    private static string Title(string type) => type switch { "playlist" => "Playlist", "album" => "Album", _ => "Track" };
    private static string MediaClass(string type) => type switch
    {
        "playlist" => "object.container.playlistContainer",
        "album" => "object.container.album.musicAlbum",
        _ => "object.item.audioItem.musicTrack"
    };
    private static string Metadata(string id, string parent, string title, string mediaClass, string descriptor)
        => $"<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\"><item id=\"{id}\" parentID=\"{parent}\" restricted=\"true\"><dc:title>{title}</dc:title><upnp:class>{mediaClass}</upnp:class><desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">{descriptor}</desc></item></DIDL-Lite>";
}
