namespace SonosControl.DAL.Models;

public static class MediaIdentity
{
    public static bool Matches(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
            && string.Equals(Normalize(first), Normalize(second), StringComparison.Ordinal);

    private static string Normalize(string value)
    {
        value = value.Trim();
        if (value.StartsWith("x-rincon-mp3radio://", StringComparison.OrdinalIgnoreCase))
            value = value["x-rincon-mp3radio://".Length..];
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .GroupBy(pair => Uri.UnescapeDataString(pair[0]), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Length == 2 ? Uri.UnescapeDataString(group.Last()[1]) : "", StringComparer.Ordinal);
        if (host is "youtube.com" or "music.youtube.com" or "m.youtube.com" or "youtu.be")
        {
            query.TryGetValue("v", out var video);
            query.TryGetValue("list", out var playlist);
            if (host == "youtu.be") video = uri.AbsolutePath.Trim('/');
            if (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal)) video = uri.AbsolutePath[8..].Trim('/');
            if (!string.IsNullOrEmpty(video)) return $"youtube:video:{video}:playlist:{playlist}";
            if (!string.IsNullOrEmpty(playlist)) return $"youtube:playlist:{playlist}";
        }
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return host + port + uri.AbsolutePath.TrimEnd('/') + uri.Query;
    }
}
