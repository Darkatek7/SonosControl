using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using SonosControl.Web.Data;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class JukeboxConcurrencyTests
{
    [Fact]
    public async Task ParallelVotesAndPlaybackFromSeparateScopes_DoNotLoseVotesOrEnqueueWinnerTwice()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var repository = new SettingsRepo(directory);
            await repository.UpdateSettingsAsync(settings =>
            {
                settings.Speakers = [new SonosSpeaker { IpAddress = "A" }];
                settings.Jukebox.Enabled = true;
                settings.JukeboxSuggestions = [new JukeboxSuggestion { Id = "song", ResourceUri = "https://example.invalid/song.mp3", SuggestedUtc = DateTime.UtcNow }];
            });
            var connector = new Mock<ISonosConnectorRepo>();
            connector.Setup(c => c.AddUriToQueue("A", It.IsAny<string>(), null, true, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var services = new ServiceCollection();
            services.AddSingleton<ISettingsRepo>(repository);
            services.AddSingleton(connector.Object);
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
            services.AddSingleton<ILogger<CollaborativeJukeboxService>>(NullLogger<CollaborativeJukeboxService>.Instance);
            var database = Guid.NewGuid().ToString();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(database));
            services.AddScoped<ActionLogger>();
            services.AddScoped<CollaborativeJukeboxService>();
            using var provider = services.BuildServiceProvider();
            using var first = provider.CreateScope();
            using var second = provider.CreateScope();
            var one = first.ServiceProvider.GetRequiredService<CollaborativeJukeboxService>();
            var two = second.ServiceProvider.GetRequiredService<CollaborativeJukeboxService>();
            var votes = await Task.WhenAll(one.VoteAsync("song", "alice"), two.VoteAsync("song", "bob"));
            Assert.All(votes, vote => Assert.True(vote.Success));
            var playback = await Task.WhenAll(one.PlayTopSuggestionAsync("A", "alice"), two.PlayTopSuggestionAsync("A", "bob"));
            Assert.Single(playback, result => result.Success);
            connector.Verify(c => c.AddUriToQueue("A", It.IsAny<string>(), null, true, It.IsAny<CancellationToken>()), Times.Once);
            var suggestion = Assert.Single((await repository.GetSettings())!.JukeboxSuggestions);
            Assert.Equal(2, suggestion.Votes.Count);
            Assert.True(suggestion.IsPlayed);
        }
        finally { Directory.Delete(directory, true); }
    }
}
