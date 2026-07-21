using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using SonosControl.Web.Data;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services;

public sealed class UserFavouriteSourceService
{
    private readonly ApplicationDbContext _db;
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public UserFavouriteSourceService(
        ApplicationDbContext db,
        AuthenticationStateProvider authenticationStateProvider)
    {
        _db = db;
        _authenticationStateProvider = authenticationStateProvider;
    }

    public async Task<IReadOnlyList<UserFavouriteSource>> GetCurrentUserFavouritesAsync(
        CancellationToken cancellationToken = default)
    {
        var userId = await ResolveCurrentUserIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Array.Empty<UserFavouriteSource>();
        }

        return await _db.UserFavouriteSources
            .AsNoTracking()
            .Where(favourite => favourite.UserId == userId)
            .OrderByDescending(favourite => favourite.CreatedAtUtc)
            .ThenByDescending(favourite => favourite.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ToggleCurrentUserAsync(
        string sourceType,
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var userId = await ResolveCurrentUserIdAsync(cancellationToken)
                ?? throw new InvalidOperationException("The signed-in user could not be resolved.");
            var normalizedType = FavouriteSourceIdentity.NormalizeType(sourceType);
            var normalizedUrl = FavouriteSourceIdentity.NormalizeUrl(sourceUrl);

            if (string.IsNullOrWhiteSpace(normalizedType) || string.IsNullOrWhiteSpace(normalizedUrl))
            {
                throw new ArgumentException("Source type and URL are required.");
            }

            var existing = await _db.UserFavouriteSources
                .SingleOrDefaultAsync(
                    favourite => favourite.UserId == userId
                                 && favourite.SourceType == normalizedType
                                 && favourite.SourceUrl == normalizedUrl,
                    cancellationToken);

            if (existing is not null)
            {
                _db.UserFavouriteSources.Remove(existing);
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            _db.UserFavouriteSources.Add(new UserFavouriteSource
            {
                UserId = userId,
                SourceType = normalizedType,
                SourceUrl = normalizedUrl,
                CreatedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task MoveSourceAsync(
        string oldSourceType,
        string oldSourceUrl,
        string newSourceType,
        string newSourceUrl,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var oldType = FavouriteSourceIdentity.NormalizeType(oldSourceType);
            var oldUrl = FavouriteSourceIdentity.NormalizeUrl(oldSourceUrl);
            var newType = FavouriteSourceIdentity.NormalizeType(newSourceType);
            var newUrl = FavouriteSourceIdentity.NormalizeUrl(newSourceUrl);
            if (oldType == newType && oldUrl == newUrl)
            {
                return;
            }

            var matches = await _db.UserFavouriteSources
                .Where(favourite => favourite.SourceType == oldType && favourite.SourceUrl == oldUrl)
                .ToListAsync(cancellationToken);
            if (matches.Count == 0)
            {
                return;
            }

            var userIds = matches.Select(favourite => favourite.UserId).ToList();
            var duplicateUserIds = await _db.UserFavouriteSources
                .Where(favourite => userIds.Contains(favourite.UserId)
                                    && favourite.SourceType == newType
                                    && favourite.SourceUrl == newUrl)
                .Select(favourite => favourite.UserId)
                .ToHashSetAsync(cancellationToken);

            foreach (var favourite in matches)
            {
                if (duplicateUserIds.Contains(favourite.UserId))
                {
                    _db.UserFavouriteSources.Remove(favourite);
                    continue;
                }

                favourite.SourceType = newType;
                favourite.SourceUrl = newUrl;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveSourceAsync(
        string sourceType,
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var normalizedType = FavouriteSourceIdentity.NormalizeType(sourceType);
            var normalizedUrl = FavouriteSourceIdentity.NormalizeUrl(sourceUrl);
            var matches = await _db.UserFavouriteSources
                .Where(favourite => favourite.SourceType == normalizedType && favourite.SourceUrl == normalizedUrl)
                .ToListAsync(cancellationToken);
            if (matches.Count == 0)
            {
                return;
            }

            _db.UserFavouriteSources.RemoveRange(matches);
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<string?> ResolveCurrentUserIdAsync(CancellationToken cancellationToken)
    {
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        var principal = authenticationState.User;
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            return userId;
        }

        var userName = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        return await _db.Users
            .Where(user => user.UserName == userName)
            .Select(user => user.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }
}

internal static class FavouriteSourceIdentity
{
    public static string NormalizeType(string? sourceType)
    {
        return sourceType?.Trim().ToLowerInvariant() switch
        {
            "station" or "stream" or "radio" => "radio",
            "spotify" => "spotify",
            "youtube" => "youtube",
            "youtube music" or "youtubemusic" => "youtubemusic",
            null => string.Empty,
            var value => value
        };
    }

    public static string NormalizeUrl(string? sourceUrl) => sourceUrl?.Trim().ToLowerInvariant() ?? string.Empty;

    public static string CreateKey(string? sourceType, string? sourceUrl) =>
        $"{NormalizeType(sourceType)}|{NormalizeUrl(sourceUrl)}";
}
