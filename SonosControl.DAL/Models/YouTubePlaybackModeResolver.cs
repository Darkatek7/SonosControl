namespace SonosControl.DAL.Models;

public static class YouTubePlaybackModeResolver
{
    public const int DefaultRelatedQueueLength = 10;

    public static YouTubePlaybackMode GetEffectiveMode(string? url, YouTubePlaybackMode? storedMode)
    {
        if (storedMode.HasValue)
        {
            return storedMode.Value;
        }

        return IsPlaylistLikeUrl(url)
            ? YouTubePlaybackMode.PlaylistOrdered
            : YouTubePlaybackMode.AutoQueueRelated;
    }

    public static int GetEffectiveQueueLength(int? preferredQueueLength)
    {
        var value = preferredQueueLength.GetValueOrDefault(DefaultRelatedQueueLength);
        return value > 0 ? value : DefaultRelatedQueueLength;
    }

    public static bool IsPlaylistLikeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return url.Contains("list=", StringComparison.OrdinalIgnoreCase);
        }

        return uri.Query.Contains("list=", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase);
    }
}
