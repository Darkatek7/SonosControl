using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class UserFavouriteSourceServiceTests
{
    [Fact]
    public async Task Toggle_IsScopedToCurrentUser_AndNormalizesIdentity()
    {
        await using var db = CreateDb();
        AddUsers(db);
        var auth = new MutableAuthenticationStateProvider("user-one", "one");
        var service = new UserFavouriteSourceService(db, auth);

        Assert.True(await service.ToggleCurrentUserAsync("Station", " HTTPS://Radio.Example/Live "));
        Assert.Equal("radio", db.UserFavouriteSources.Single().SourceType);
        Assert.Equal("https://radio.example/live", db.UserFavouriteSources.Single().SourceUrl);

        auth.SetUser("user-two", "two");
        Assert.Empty(await service.GetCurrentUserFavouritesAsync());
        Assert.True(await service.ToggleCurrentUserAsync("radio", "https://radio.example/live"));
        Assert.Equal(2, db.UserFavouriteSources.Count());

        auth.SetUser("user-one", "one");
        Assert.False(await service.ToggleCurrentUserAsync("radio", "https://radio.example/live"));
        Assert.Single(db.UserFavouriteSources);
        Assert.Equal("user-two", db.UserFavouriteSources.Single().UserId);
    }

    [Fact]
    public async Task MoveAndRemoveSource_UpdatesReferencesForEveryUser()
    {
        await using var db = CreateDb();
        AddUsers(db);
        db.UserFavouriteSources.AddRange(
            Favourite("user-one", "radio", "https://old.example/live"),
            Favourite("user-two", "radio", "https://old.example/live"));
        await db.SaveChangesAsync();
        var service = new UserFavouriteSourceService(
            db,
            new MutableAuthenticationStateProvider("user-one", "one"));

        await service.MoveSourceAsync(
            "radio",
            "https://old.example/live",
            "spotify",
            "spotify:track:updated");

        Assert.Equal(2, db.UserFavouriteSources.Count());
        Assert.All(db.UserFavouriteSources, favourite =>
        {
            Assert.Equal("spotify", favourite.SourceType);
            Assert.Equal("spotify:track:updated", favourite.SourceUrl);
        });

        await service.RemoveSourceAsync("spotify", "spotify:track:updated");
        Assert.Empty(db.UserFavouriteSources);
    }

    [Fact]
    public async Task Favourites_AreReturnedNewestFirst()
    {
        await using var db = CreateDb();
        AddUsers(db);
        db.UserFavouriteSources.AddRange(
            Favourite("user-one", "radio", "https://older.example", DateTime.UtcNow.AddMinutes(-5)),
            Favourite("user-one", "radio", "https://newer.example", DateTime.UtcNow));
        await db.SaveChangesAsync();
        var service = new UserFavouriteSourceService(
            db,
            new MutableAuthenticationStateProvider("user-one", "one"));

        var favourites = await service.GetCurrentUserFavouritesAsync();

        Assert.Equal("https://newer.example", favourites[0].SourceUrl);
        Assert.Equal("https://older.example", favourites[1].SourceUrl);
    }

    [Fact]
    public void Model_EnforcesUniqueUserSource_AndUserCascadeDelete()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(UserFavouriteSource));

        Assert.NotNull(entity);
        var uniqueIndex = entity!.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "UserId", "SourceType", "SourceUrl" }));
        Assert.True(uniqueIndex.IsUnique);
        Assert.Equal(DeleteBehavior.Cascade, entity.GetForeignKeys().Single().DeleteBehavior);
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static void AddUsers(ApplicationDbContext db)
    {
        db.Users.AddRange(
            new ApplicationUser { Id = "user-one", UserName = "one", NormalizedUserName = "ONE", FirstName = string.Empty, LastName = string.Empty },
            new ApplicationUser { Id = "user-two", UserName = "two", NormalizedUserName = "TWO", FirstName = string.Empty, LastName = string.Empty });
        db.SaveChanges();
    }

    private static UserFavouriteSource Favourite(
        string userId,
        string sourceType,
        string sourceUrl,
        DateTime? createdAtUtc = null) =>
        new()
        {
            UserId = userId,
            SourceType = sourceType,
            SourceUrl = sourceUrl,
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };

    private sealed class MutableAuthenticationStateProvider : AuthenticationStateProvider
    {
        private AuthenticationState _state;

        public MutableAuthenticationStateProvider(string userId, string userName)
        {
            _state = CreateState(userId, userName);
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);

        public void SetUser(string userId, string userName) => _state = CreateState(userId, userName);

        private static AuthenticationState CreateState(string userId, string userName)
        {
            var identity = new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim(ClaimTypes.Name, userName)
                },
                "Test");
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
    }
}
