using Microsoft.EntityFrameworkCore;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;

namespace SonosControl.Web.Services;

public sealed record HomeLibrarySource(
    string Name,
    string Url,
    string MediaType,
    bool IsFavourite);

public sealed class HomeLibraryService
{
    private static readonly TimeSpan PopularityWindow = TimeSpan.FromDays(30);
    private readonly ApplicationDbContext _db;
    private readonly UserFavouriteSourceService _favourites;

    public HomeLibraryService(ApplicationDbContext db, UserFavouriteSourceService favourites)
    {
        _db = db;
        _favourites = favourites;
    }

    public async Task<IReadOnlyList<HomeLibrarySource>> GetQuickAccessAsync(
        SonosSettings settings,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Array.Empty<HomeLibrarySource>();
        }

        var catalog = BuildCatalog(settings)
            .GroupBy(source => FavouriteSourceIdentity.CreateKey(source.SourceType, source.Url))
            .Select(group => group.First())
            .ToList();
        var catalogByKey = catalog.ToDictionary(
            source => FavouriteSourceIdentity.CreateKey(source.SourceType, source.Url),
            StringComparer.Ordinal);
        var favourites = await _favourites.GetCurrentUserFavouritesAsync(cancellationToken);
        var selected = new List<HomeLibrarySource>(Math.Min(limit, catalog.Count));
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var favourite in favourites)
        {
            var key = FavouriteSourceIdentity.CreateKey(favourite.SourceType, favourite.SourceUrl);
            if (!catalogByKey.TryGetValue(key, out var source) || !selectedKeys.Add(key))
            {
                continue;
            }

            selected.Add(ToHomeSource(source, isFavourite: true));
            if (selected.Count == limit)
            {
                return selected;
            }
        }

        var popularKeys = await GetPopularSourceKeysAsync(settings, catalogByKey, cancellationToken);
        foreach (var key in popularKeys)
        {
            if (!selectedKeys.Add(key))
            {
                continue;
            }

            selected.Add(ToHomeSource(catalogByKey[key], isFavourite: false));
            if (selected.Count == limit)
            {
                return selected;
            }
        }

        foreach (var source in catalog.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = FavouriteSourceIdentity.CreateKey(source.SourceType, source.Url);
            if (!selectedKeys.Add(key))
            {
                continue;
            }

            selected.Add(ToHomeSource(source, isFavourite: false));
            if (selected.Count == limit)
            {
                break;
            }
        }

        return selected;
    }

    private async Task<IReadOnlyList<string>> GetPopularSourceKeysAsync(
        SonosSettings settings,
        IReadOnlyDictionary<string, CatalogSource> catalogByKey,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.Subtract(PopularityWindow);
        var playbackRows = await _db.PlaybackStats
            .AsNoTracking()
            .Where(playback => playback.StartTime >= cutoff)
            .Select(playback => new
            {
                playback.TrackName,
                playback.Artist,
                playback.MediaType,
                playback.DurationSeconds
            })
            .ToListAsync(cancellationToken);
        var resolver = RecommendationMediaResolver.Create(settings);

        return playbackRows
            .Select(playback => new
            {
                Playback = playback,
                Resolved = resolver.ResolveFromPlaybackEntry(
                    playback.TrackName,
                    playback.Artist,
                    playback.MediaType)
            })
            .Where(item => item.Resolved.MatchedCatalog && !string.IsNullOrWhiteSpace(item.Resolved.SourceUrl))
            .Select(item => new
            {
                Key = FavouriteSourceIdentity.CreateKey(item.Resolved.MediaType, item.Resolved.SourceUrl),
                Score = Math.Max(0, item.Playback.DurationSeconds)
            })
            .Where(item => catalogByKey.ContainsKey(item.Key))
            .GroupBy(item => item.Key)
            .Select(group => new
            {
                Key = group.Key,
                Duration = group.Sum(item => item.Score),
                PlayCount = group.Count(),
                Name = catalogByKey[group.Key].Name
            })
            .OrderByDescending(item => item.Duration)
            .ThenByDescending(item => item.PlayCount)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key)
            .ToList();
    }

    private static IEnumerable<CatalogSource> BuildCatalog(SonosSettings settings)
    {
        foreach (var station in settings.Stations ?? new List<TuneInStation>())
        {
            yield return new CatalogSource(station.Name, station.Url, "radio", "station");
        }

        foreach (var spotify in settings.SpotifyTracks ?? new List<SpotifyObject>())
        {
            yield return new CatalogSource(spotify.Name, spotify.Url, "spotify", "spotify");
        }

        foreach (var youtube in settings.YouTubeCollections ?? new List<YouTubeObject>())
        {
            yield return new CatalogSource(youtube.Name, youtube.Url, "youtube", "youtube");
        }

        foreach (var youtubeMusic in settings.YouTubeMusicCollections ?? new List<YouTubeMusicObject>())
        {
            yield return new CatalogSource(youtubeMusic.Name, youtubeMusic.Url, "youtubemusic", "youtubemusic");
        }
    }

    private static HomeLibrarySource ToHomeSource(CatalogSource source, bool isFavourite) =>
        new(source.Name, source.Url, source.MediaType, isFavourite);

    private sealed record CatalogSource(string Name, string Url, string SourceType, string MediaType);
}
