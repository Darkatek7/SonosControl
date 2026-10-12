namespace SonosControl.DAL.Repos;

public static class YouTubeSourceUrl
{
    public static string Normalize(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new InvalidOperationException("A YouTube URL is required.");
        }

        var trimmed = sourceUrl.Trim();
        if (trimmed.StartsWith("https://youtu.be/", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(trimmed);
            var id = uri.AbsolutePath.Trim('/').Split('/')[0];
            var query = ParseQueryParameters(uri.Query);
            query["v"] = id;
            return BuildWatchUrl(query);
        }

        if (trimmed.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var query = ParseQueryParameters(uri.Query);
                if (query.TryGetValue("v", out var videoId) && !string.IsNullOrWhiteSpace(videoId))
                {
                    query["v"] = videoId.Trim();
                    return BuildWatchUrl(query);
                }

                if (uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase) && query.Count > 0)
                {
                    var queryString = string.Join("&", query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
                    return $"https://www.youtube.com/playlist?{queryString}";
                }
            }

            return trimmed;
        }

        throw new InvalidOperationException("Only YouTube video and playlist URLs are supported.");
    }

    private static Dictionary<string, string> ParseQueryParameters(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildWatchUrl(Dictionary<string, string> query)
    {
        var orderedKeys = new[] { "v", "list", "start_radio" };
        var queryParts = new List<string>();

        foreach (var key in orderedKeys)
        {
            if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                queryParts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        foreach (var pair in query.Where(pair => !orderedKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                queryParts.Add($"{pair.Key}={Uri.EscapeDataString(pair.Value)}");
            }
        }

        return queryParts.Count == 0
            ? "https://www.youtube.com/watch"
            : $"https://www.youtube.com/watch?{string.Join("&", queryParts)}";
    }

}
