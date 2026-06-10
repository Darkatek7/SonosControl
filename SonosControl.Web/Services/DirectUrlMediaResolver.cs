using System;

namespace SonosControl.Web.Services;

public sealed record DirectUrlMediaResolution(
    bool IsValid,
    string NormalizedUrl,
    string MediaType,
    string? ErrorMessage);

public static class DirectUrlMediaResolver
{
    public static DirectUrlMediaResolution Resolve(string? rawUrl)
    {
        var normalizedUrl = rawUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            return Invalid("Enter a media URL first.");
        }

        if (ContainsEmbeddedWhitespace(normalizedUrl))
        {
            return Invalid("The media URL is invalid.");
        }

        if (normalizedUrl.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            return Valid(normalizedUrl, "spotify");
        }

        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var absoluteUri))
        {
            if (!string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("Only HTTP, HTTPS, and Spotify URLs are supported.");
            }

            var host = absoluteUri.Host.Trim().Trim('.').ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal))
            {
                host = host[4..];
            }

            if (string.Equals(host, "music.youtube.com", StringComparison.Ordinal))
            {
                return Valid(normalizedUrl, "youtubemusic");
            }

            if (string.Equals(host, "youtube.com", StringComparison.Ordinal)
                || host.EndsWith(".youtube.com", StringComparison.Ordinal)
                || string.Equals(host, "youtu.be", StringComparison.Ordinal))
            {
                return Valid(normalizedUrl, "youtube");
            }

            if (string.Equals(host, "spotify.com", StringComparison.Ordinal)
                || host.EndsWith(".spotify.com", StringComparison.Ordinal))
            {
                return Valid(normalizedUrl, "spotify");
            }

            return Valid(normalizedUrl, "station");
        }

        return LooksLikeStationToken(normalizedUrl)
            ? Valid(normalizedUrl, "station")
            : Invalid("The media URL is invalid.");
    }

    private static DirectUrlMediaResolution Valid(string normalizedUrl, string mediaType)
        => new(true, normalizedUrl, mediaType, null);

    private static DirectUrlMediaResolution Invalid(string errorMessage)
        => new(false, string.Empty, string.Empty, errorMessage);

    private static bool ContainsEmbeddedWhitespace(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeStationToken(string value)
        => value.Contains('.', StringComparison.Ordinal)
           && value.Contains('/', StringComparison.Ordinal)
           && !value.Contains("://", StringComparison.Ordinal);
}
