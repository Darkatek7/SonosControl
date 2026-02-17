using System;
using System.Collections.Generic;
using System.Linq;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

internal sealed record ResolvedRecommendationMedia(
    string Name,
    string MediaType,
    string? SourceUrl,
    bool MatchedCatalog);

internal sealed class RecommendationMediaResolver
{
    private const string RinconPrefix = "x-rincon-mp3radio://";

    private readonly Dictionary<string, CatalogEntry> _byCanonicalKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogEntry> _byLooseKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogEntry> _byHostPathKey = new(StringComparer.Ordinal);
    private readonly List<CatalogEntry> _stationEntries = new();
    private static readonly HashSet<string> GenericPlaybackLabels = new(StringComparer.Ordinal)
    {
        "playing stream",
        "stream",
        "station",
        "live stream",
        "mp3",
        "aac"
    };

    private RecommendationMediaResolver()
    {
    }

    public static RecommendationMediaResolver Create(SonosSettings? settings)
    {
        var resolver = new RecommendationMediaResolver();

        settings ??= new SonosSettings();
        settings.Stations ??= new();
        settings.SpotifyTracks ??= new();
        settings.YouTubeMusicCollections ??= new();

        foreach (var station in settings.Stations.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            resolver.AddEntry(station.Name, "Station", station.Url);
        }

        foreach (var spotify in settings.SpotifyTracks.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            resolver.AddEntry(spotify.Name, "Spotify", spotify.Url);
        }

        foreach (var yt in settings.YouTubeMusicCollections.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            resolver.AddEntry(yt.Name, "YouTubeMusic", yt.Url);
        }

        return resolver;
    }

    public ResolvedRecommendationMedia ResolveFromSourceUrl(string? rawSourceUrl, string? fallbackMediaType)
    {
        var normalizedMediaType = NormalizeMediaType(fallbackMediaType);
        if (string.IsNullOrWhiteSpace(rawSourceUrl))
        {
            return new ResolvedRecommendationMedia(string.Empty, normalizedMediaType, null, false);
        }

        if (TryResolve(rawSourceUrl, normalizedMediaType, out var entry))
        {
            return new ResolvedRecommendationMedia(entry.Name, entry.MediaType, entry.SourceUrl, true);
        }

        var fallbackLabel = IsStationLike(normalizedMediaType)
            ? RemoveRinconPrefix(rawSourceUrl).Trim()
            : rawSourceUrl.Trim();

        return new ResolvedRecommendationMedia(fallbackLabel, normalizedMediaType, rawSourceUrl.Trim(), false);
    }

    public ResolvedRecommendationMedia ResolveDisplay(string? rawName, string? rawMediaType)
    {
        var normalizedMediaType = NormalizeMediaType(rawMediaType);
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return new ResolvedRecommendationMedia(string.Empty, normalizedMediaType, null, false);
        }

        if (TryResolve(rawName, normalizedMediaType, out var entry))
        {
            return new ResolvedRecommendationMedia(entry.Name, entry.MediaType, entry.SourceUrl, true);
        }

        var fallbackLabel = IsStationLike(normalizedMediaType)
            ? RemoveRinconPrefix(rawName).Trim()
            : rawName.Trim();

        return new ResolvedRecommendationMedia(fallbackLabel, normalizedMediaType, null, false);
    }

    public ResolvedRecommendationMedia ResolveFromPlaybackEntry(string? trackName, string? artist, string? rawMediaType)
    {
        var normalizedMediaType = NormalizeMediaType(rawMediaType);

        foreach (var candidate in EnumeratePlaybackCandidates(trackName, artist))
        {
            if (TryResolve(candidate, normalizedMediaType, out var entry))
            {
                var resolvedMediaType = string.Equals(entry.MediaType, "Station", StringComparison.OrdinalIgnoreCase)
                    ? "Station"
                    : entry.MediaType;
                return new ResolvedRecommendationMedia(entry.Name, resolvedMediaType, entry.SourceUrl, true);
            }
        }

        var fallbackSource = IsGenericPlaybackTrackLabel(trackName) && !string.IsNullOrWhiteSpace(artist)
            ? artist
            : (!string.IsNullOrWhiteSpace(trackName) ? trackName : artist);
        if (string.IsNullOrWhiteSpace(fallbackSource))
        {
            return new ResolvedRecommendationMedia(string.Empty, normalizedMediaType, null, false);
        }

        var fallbackLabel = IsStationLike(normalizedMediaType)
            ? RemoveRinconPrefix(fallbackSource).Trim()
            : fallbackSource.Trim();

        return new ResolvedRecommendationMedia(fallbackLabel, normalizedMediaType, null, false);
    }

    private void AddEntry(string? name, string mediaType, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return;
        }

        var entry = new CatalogEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? sourceUrl.Trim() : name.Trim(),
            MediaType = NormalizeMediaType(mediaType),
            SourceUrl = sourceUrl.Trim(),
            LooseKey = NormalizeLoose(sourceUrl)
        };

        if (!string.IsNullOrWhiteSpace(entry.LooseKey))
        {
            _byLooseKey.TryAdd(entry.LooseKey, entry);
        }

        var canonicalKey = ToCanonicalKey(sourceUrl);
        if (!string.IsNullOrWhiteSpace(canonicalKey))
        {
            _byCanonicalKey.TryAdd(canonicalKey, entry);
        }

        var hostPathKey = ToHostPathKey(sourceUrl);
        if (!string.IsNullOrWhiteSpace(hostPathKey))
        {
            _byHostPathKey.TryAdd(hostPathKey, entry);
        }

        if (string.Equals(entry.MediaType, "Station", StringComparison.OrdinalIgnoreCase))
        {
            _stationEntries.Add(entry);
        }
    }

    private bool TryResolve(string rawValue, string mediaTypeHint, out CatalogEntry entry)
    {
        var canonicalKey = ToCanonicalKey(rawValue);
        if (!string.IsNullOrWhiteSpace(canonicalKey)
            && _byCanonicalKey.TryGetValue(canonicalKey, out var canonicalMatch)
            && canonicalMatch is not null)
        {
            entry = canonicalMatch;
            return true;
        }

        var looseKey = NormalizeLoose(rawValue);
        if (!string.IsNullOrWhiteSpace(looseKey)
            && _byLooseKey.TryGetValue(looseKey, out var looseMatch)
            && looseMatch is not null)
        {
            entry = looseMatch;
            return true;
        }

        var hostPathKey = ToHostPathKey(rawValue);
        if (!string.IsNullOrWhiteSpace(hostPathKey)
            && _byHostPathKey.TryGetValue(hostPathKey, out var hostPathMatch)
            && hostPathMatch is not null)
        {
            entry = hostPathMatch;
            return true;
        }

        if (IsStationLike(mediaTypeHint) && !string.IsNullOrWhiteSpace(looseKey) && LooksLikeUrl(rawValue))
        {
            foreach (var station in _stationEntries)
            {
                if (string.IsNullOrWhiteSpace(station.LooseKey))
                {
                    continue;
                }

                if (looseKey.Contains(station.LooseKey, StringComparison.Ordinal)
                    || station.LooseKey.Contains(looseKey, StringComparison.Ordinal))
                {
                    entry = station;
                    return true;
                }
            }
        }

        entry = CatalogEntry.Empty;
        return false;
    }

    private static IEnumerable<string> EnumeratePlaybackCandidates(string? trackName, string? artist)
    {
        var useTrackFirst = !IsGenericPlaybackTrackLabel(trackName);
        if (useTrackFirst && !string.IsNullOrWhiteSpace(trackName))
        {
            yield return trackName;
        }

        if (!string.IsNullOrWhiteSpace(artist))
        {
            yield return artist;
        }

        if (!useTrackFirst && !string.IsNullOrWhiteSpace(trackName))
        {
            yield return trackName;
        }
    }

    private static bool IsGenericPlaybackTrackLabel(string? trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName))
        {
            return false;
        }

        var trimmed = trackName.Trim();
        if (LooksLikeUrl(trimmed))
        {
            return false;
        }

        return GenericPlaybackLabels.Contains(trimmed.ToLowerInvariant());
    }

    private static string NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return "Unknown";
        }

        var normalized = mediaType.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "station" => "Station",
            "stream" => "Station",
            "spotify" => "Spotify",
            "youtube" => "YouTubeMusic",
            "youtube music" => "YouTubeMusic",
            "youtubemusic" => "YouTubeMusic",
            _ => normalized
        };
    }

    private static bool IsStationLike(string mediaType)
    {
        return string.Equals(mediaType, "Station", StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType, "Stream", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith(RinconPrefix, StringComparison.OrdinalIgnoreCase)
               || (trimmed.Contains('.') && trimmed.Contains('/'));
    }

    private static string NormalizeLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = RemoveRinconPrefix(value).Trim().ToLowerInvariant();
        return cleaned.TrimEnd('/');
    }

    private static string ToCanonicalKey(string? value)
    {
        if (!TryParseUrl(value, out var uri) || uri is null)
        {
            return string.Empty;
        }

        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        var query = uri.Query.ToLowerInvariant();
        return $"{uri.Host.ToLowerInvariant()}{path}{query}";
    }

    private static string ToHostPathKey(string? value)
    {
        if (!TryParseUrl(value, out var uri) || uri is null)
        {
            return string.Empty;
        }

        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        return $"{uri.Host.ToLowerInvariant()}{path}";
    }

    private static bool TryParseUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var cleaned = RemoveRinconPrefix(value).Trim();
        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var parsed) && parsed is not null)
        {
            uri = parsed;
            return true;
        }

        if (!cleaned.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate($"http://{cleaned}", UriKind.Absolute, out parsed)
            && parsed is not null)
        {
            uri = parsed;
            return true;
        }

        return false;
    }

    private static string RemoveRinconPrefix(string value)
    {
        if (value.StartsWith(RinconPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value[RinconPrefix.Length..];
        }

        return value;
    }

    private sealed class CatalogEntry
    {
        public static readonly CatalogEntry Empty = new();

        public string Name { get; set; } = string.Empty;
        public string MediaType { get; set; } = "Unknown";
        public string SourceUrl { get; set; } = string.Empty;
        public string LooseKey { get; set; } = string.Empty;
    }
}
