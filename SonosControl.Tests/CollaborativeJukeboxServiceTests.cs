using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class CollaborativeJukeboxServiceTests
{
    [Fact]
    public async Task SuggestAsync_EnforcesPerUserSuggestionLimit()
    {
        var settings = new SonosSettings
        {
            Jukebox = new JukeboxSettings
            {
                Enabled = true,
                MaxSuggestionsPerUserPerHour = 1,
                MaxVotesPerUserPerHour = 20,
                PlayedSuggestionRetentionDays = 14
            },
            JukeboxSuggestions = new List<JukeboxSuggestion>(),
            Speakers = new List<SonosSpeaker> { new() { Name = "Kitchen", IpAddress = "10.0.0.2" } }
        };

        var service = CreateService(settings, out _);

        var first = await service.SuggestAsync("spotify:track:abc", "Track A", "Artist A", "alice");
        var second = await service.SuggestAsync("spotify:track:def", "Track B", "Artist B", "alice");

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("limit", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayTopSuggestionAsync_QueuesHighestVotedPendingSuggestion()
    {
        var settings = new SonosSettings
        {
            IP_Adress = "10.0.0.2",
            Jukebox = new JukeboxSettings
            {
                Enabled = true,
                MaxSuggestionsPerUserPerHour = 5,
                MaxVotesPerUserPerHour = 20,
                PlayedSuggestionRetentionDays = 14
            },
            Speakers = new List<SonosSpeaker>
            {
                new() { Name = "Kitchen", IpAddress = "10.0.0.2" }
            },
            JukeboxSuggestions = new List<JukeboxSuggestion>
            {
                new()
                {
                    Id = "s-low",
                    ResourceUri = "spotify:track:low",
                    Title = "Low",
                    SuggestedBy = "alice",
                    SuggestedUtc = DateTime.UtcNow.AddMinutes(-15),
                    Votes = new List<JukeboxVote>
                    {
                        new() { UserName = "alice", VotedUtc = DateTime.UtcNow.AddMinutes(-10) }
                    }
                },
                new()
                {
                    Id = "s-high",
                    ResourceUri = "spotify:track:high",
                    Title = "High",
                    SuggestedBy = "bob",
                    SuggestedUtc = DateTime.UtcNow.AddMinutes(-5),
                    Votes = new List<JukeboxVote>
                    {
                        new() { UserName = "alice", VotedUtc = DateTime.UtcNow.AddMinutes(-4) },
                        new() { UserName = "charlie", VotedUtc = DateTime.UtcNow.AddMinutes(-3) }
                    }
                }
            }
        };

        var service = CreateService(settings, out var sonosRepo);
        var result = await service.PlayTopSuggestionAsync("10.0.0.2", "operator");

        Assert.True(result.Success);
        sonosRepo.Verify(
            repo => repo.AddUriToQueue("10.0.0.2", "spotify:track:high", null, true, It.IsAny<CancellationToken>()),
            Times.Once);

        var state = await service.GetStateAsync();
        var winner = state.Suggestions.Single(s => s.Id == "s-high");
        Assert.True(winner.IsPlayed);
        Assert.NotNull(winner.PlayedUtc);
        Assert.Equal("10.0.0.2", winner.PlayedOnSpeakerIp);
    }

    [Fact]
    public async Task VoteAsync_EnforcesPerUserVoteLimit()
    {
        var settings = new SonosSettings
        {
            Jukebox = new JukeboxSettings
            {
                Enabled = true,
                MaxSuggestionsPerUserPerHour = 5,
                MaxVotesPerUserPerHour = 1,
                PlayedSuggestionRetentionDays = 14
            },
            JukeboxSuggestions = new List<JukeboxSuggestion>
            {
                new()
                {
                    Id = "s1",
                    ResourceUri = "spotify:track:1",
                    Title = "One",
                    SuggestedBy = "alice",
                    SuggestedUtc = DateTime.UtcNow.AddMinutes(-20)
                },
                new()
                {
                    Id = "s2",
                    ResourceUri = "spotify:track:2",
                    Title = "Two",
                    SuggestedBy = "bob",
                    SuggestedUtc = DateTime.UtcNow.AddMinutes(-10)
                }
            },
            Speakers = new List<SonosSpeaker> { new() { Name = "Kitchen", IpAddress = "10.0.0.2" } }
        };

        var service = CreateService(settings, out _);

        var firstVote = await service.VoteAsync("s1", "alice");
        var secondVote = await service.VoteAsync("s2", "alice");

        Assert.True(firstVote.Success);
        Assert.False(secondVote.Success);
        Assert.Contains("limit", secondVote.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CollaborativeJukeboxService CreateService(
        SonosSettings initialSettings,
        out Mock<ISonosConnectorRepo> sonosRepo)
    {
        var currentSettings = initialSettings;

        var settingsRepo = new Mock<ISettingsRepo>();
        settingsRepo.Setup(repo => repo.GetSettings())
            .ReturnsAsync(() => currentSettings);
        settingsRepo.Setup(repo => repo.WriteSettings(It.IsAny<SonosSettings?>()))
            .Returns<SonosSettings?>(updated =>
            {
                currentSettings = updated ?? new SonosSettings();
                return Task.CompletedTask;
            });

        sonosRepo = new Mock<ISonosConnectorRepo>();
        sonosRepo.Setup(repo => repo.AddUriToQueue(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(value => value.ISettingsRepo).Returns(settingsRepo.Object);
        uow.SetupGet(value => value.ISonosConnectorRepo).Returns(sonosRepo.Object);
        uow.SetupGet(value => value.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());

        var actionLogger = CreateActionLogger();
        return new CollaborativeJukeboxService(
            uow.Object,
            actionLogger,
            NullLogger<CollaborativeJukeboxService>.Instance);
    }

    private static ActionLogger CreateActionLogger()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"jukebox-tests-{Guid.NewGuid():N}")
            .Options;

        var db = new ApplicationDbContext(options);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        return new ActionLogger(db, accessor);
    }
}
