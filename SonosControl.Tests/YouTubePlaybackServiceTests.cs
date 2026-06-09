using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class YouTubePlaybackServiceTests
{
    [Fact]
    public async Task PreparePlaybackAsync_WhenPlaylistShuffle_ReturnsQueueItems()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var service = CreateService(
                tempRoot,
                new FakeYouTubeToolRunner((_, mode, _) => new ResolvedYouTubeQueue(
                    "https://www.youtube.com/playlist?list=PL123",
                    "Playlist",
                    new List<ResolvedYouTubeSourceItem>
                    {
                        new("https://www.youtube.com/watch?v=track1", "https://audio.example/1", "Track 1"),
                        new("https://www.youtube.com/watch?v=track2", "https://audio.example/2", "Track 2"),
                        new("https://www.youtube.com/watch?v=track3", "https://audio.example/3", "Track 3")
                    },
                    mode ?? YouTubePlaybackMode.PlaylistShuffle,
                    true)));

            var session = await service.PreparePlaybackAsync("https://www.youtube.com/playlist?list=PL123", YouTubePlaybackMode.PlaylistShuffle);

            Assert.Equal(3, session.QueueItems.Count);
            Assert.All(session.QueueItems, item => Assert.Contains($"/api/youtube-audio/{session.SessionId}/", item.StreamUrl));
            Assert.Equal(YouTubePlaybackMode.PlaylistShuffle, session.PlaybackMode);
            Assert.True(session.UsesTempFile);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task MaintainSessionsAsync_WhenRemainingBelowThreshold_AppendsItemsToQueue()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var toolRunner = new FakeYouTubeToolRunner((_, _, preferredQueueLength) =>
            {
                var items = new List<ResolvedYouTubeSourceItem>
                {
                    new("https://www.youtube.com/watch?v=track1", "https://audio.example/1", "Track 1"),
                    new("https://www.youtube.com/watch?v=track2", "https://audio.example/2", "Track 2"),
                    new("https://www.youtube.com/watch?v=track3", "https://audio.example/3", "Track 3"),
                    new("https://www.youtube.com/watch?v=track4", "https://audio.example/4", "Track 4")
                };

                return new ResolvedYouTubeQueue(
                    "https://www.youtube.com/playlist?list=PL123",
                    "Playlist",
                    items.Take(Math.Max(2, preferredQueueLength)).ToList(),
                    YouTubePlaybackMode.PlaylistOrdered,
                    true);
            });

            var repo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
            var service = CreateService(tempRoot, toolRunner, CreateScopeFactory(repo.Object));
            var session = await service.PreparePlaybackAsync(
                "https://www.youtube.com/playlist?list=PL123",
                YouTubePlaybackMode.PlaylistOrdered,
                preferredQueueLength: 2);

            await service.ActivateSessionAsync(session.SessionId, "1.2.3.4");

            repo.Setup(r => r.GetCurrentStationAsync("1.2.3.4", It.IsAny<CancellationToken>()))
                .ReturnsAsync("x-rincon-queue:RINCON_TEST#0");
            repo.Setup(r => r.GetCurrentTrackNumberAsync("1.2.3.4", It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
            repo.Setup(r => r.GetQueue("1.2.3.4", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SonosQueuePage(
                    new[]
                    {
                        new SonosQueueItem(0, "Track 1", null, null, $"http://sonos-control.local:5107/api/youtube-audio/{session.SessionId}/0"),
                        new SonosQueueItem(1, "Track 2", null, null, $"http://sonos-control.local:5107/api/youtube-audio/{session.SessionId}/1")
                    },
                    0,
                    2,
                    2));
            repo.Setup(r => r.AddUriToQueue(
                    "1.2.3.4",
                    It.Is<string>(uri => uri.Contains($"/api/youtube-audio/{session.SessionId}/2") || uri.Contains($"/api/youtube-audio/{session.SessionId}/3")),
                    It.IsAny<string?>(),
                    false,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            await service.MaintainSessionsAsync();

            repo.Verify(r => r.AddUriToQueue("1.2.3.4", It.Is<string>(uri => uri.Contains($"/api/youtube-audio/{session.SessionId}/2")), It.IsAny<string?>(), false, It.IsAny<CancellationToken>()), Times.Once);
            repo.Verify(r => r.AddUriToQueue("1.2.3.4", It.Is<string>(uri => uri.Contains($"/api/youtube-audio/{session.SessionId}/3")), It.IsAny<string?>(), false, It.IsAny<CancellationToken>()), Times.Once);
            Assert.True(toolRunner.ResolveCallCount >= 2);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task MaintainSessionsAsync_WhenRemainingAboveThreshold_DoesNotAppendItems()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var toolRunner = new FakeYouTubeToolRunner((_, _, preferredQueueLength) => new ResolvedYouTubeQueue(
                "https://www.youtube.com/playlist?list=PL123",
                "Playlist",
                Enumerable.Range(1, Math.Max(4, preferredQueueLength))
                    .Select(index => new ResolvedYouTubeSourceItem(
                        $"https://www.youtube.com/watch?v=track{index}",
                        $"https://audio.example/{index}",
                        $"Track {index}"))
                    .ToList(),
                YouTubePlaybackMode.PlaylistOrdered,
                true));

            var repo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
            var service = CreateService(tempRoot, toolRunner, CreateScopeFactory(repo.Object));
            var session = await service.PreparePlaybackAsync(
                "https://www.youtube.com/playlist?list=PL123",
                YouTubePlaybackMode.PlaylistOrdered,
                preferredQueueLength: 4);

            await service.ActivateSessionAsync(session.SessionId, "1.2.3.4");

            repo.Setup(r => r.GetCurrentStationAsync("1.2.3.4", It.IsAny<CancellationToken>()))
                .ReturnsAsync("x-rincon-queue:RINCON_TEST#0");
            repo.Setup(r => r.GetCurrentTrackNumberAsync("1.2.3.4", It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
            repo.Setup(r => r.GetQueue("1.2.3.4", 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SonosQueuePage(
                    Enumerable.Range(0, 4)
                        .Select(index => new SonosQueueItem(index, $"Track {index + 1}", null, null, $"http://sonos-control.local:5107/api/youtube-audio/{session.SessionId}/{index}"))
                        .ToArray(),
                    0,
                    4,
                    4));

            await service.MaintainSessionsAsync();

            repo.Verify(r => r.AddUriToQueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.Equal(1, toolRunner.ResolveCallCount);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task CleanupExpiredSessionsAsync_WhenSessionMarkedStale_DeletesTempFilesAfterTtl()
    {
        var tempRoot = CreateTempRoot();

        try
        {
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var toolRunner = new FakeYouTubeToolRunner((_, _, _) => new ResolvedYouTubeQueue(
                "https://www.youtube.com/playlist?list=PL123",
                "Playlist",
                new List<ResolvedYouTubeSourceItem>
                {
                    new("https://www.youtube.com/watch?v=track1", "https://audio.example/1", "Track 1"),
                    new("https://www.youtube.com/watch?v=track2", "https://audio.example/2", "Track 2")
                },
                YouTubePlaybackMode.PlaylistOrdered,
                true));

            var repo = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
            var service = CreateService(tempRoot, toolRunner, CreateScopeFactory(repo.Object), timeProvider);
            var session = await service.PreparePlaybackAsync(
                "https://www.youtube.com/playlist?list=PL123",
                YouTubePlaybackMode.PlaylistOrdered,
                preferredQueueLength: 2);

            await service.ActivateSessionAsync(session.SessionId, "1.2.3.4");
            var openResult = await service.OpenPlaybackAsync(session.SessionId, 1);

            Assert.NotNull(openResult);
            Assert.NotNull(openResult!.FilePath);
            Assert.True(File.Exists(openResult.FilePath));

            repo.Setup(r => r.GetCurrentStationAsync("1.2.3.4", It.IsAny<CancellationToken>()))
                .ReturnsAsync("x-rincon-mp3radio://other-source");

            await service.MaintainSessionsAsync();

            timeProvider.Advance(TimeSpan.FromMinutes(11));
            await service.CleanupExpiredSessionsAsync();

            Assert.False(File.Exists(openResult.FilePath));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static YouTubePlaybackService CreateService(
        string tempRoot,
        IYouTubeToolRunner toolRunner,
        IServiceScopeFactory? scopeFactory = null,
        TimeProvider? timeProvider = null)
    {
        var options = Options.Create(new YouTubePlaybackOptions
        {
            PublicBaseUrl = "http://sonos-control.local:5107",
            ArtifactDirectory = tempRoot,
            SessionTtlMinutes = 10,
            QueueTopUpThreshold = 2,
            QueueTopUpBatchSize = 2,
            MaxAutoQueueItemsPerSession = 10
        });

        scopeFactory ??= CreateScopeFactory(Mock.Of<ISonosConnectorRepo>());

        return new YouTubePlaybackService(
            toolRunner,
            options,
            new FakeEnvironment(tempRoot),
            scopeFactory,
            NullLogger<YouTubePlaybackService>.Instance,
            timeProvider);
    }

    private static IServiceScopeFactory CreateScopeFactory(ISonosConnectorRepo connectorRepo)
    {
        var services = new ServiceCollection();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(value => value.ISonosConnectorRepo).Returns(connectorRepo);
        unitOfWork.SetupGet(value => value.ISettingsRepo).Returns(Mock.Of<ISettingsRepo>());
        unitOfWork.SetupGet(value => value.IHolidayRepo).Returns(Mock.Of<IHolidayRepo>());
        services.AddScoped(_ => unitOfWork.Object);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"yt-playback-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void DeleteTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class FakeYouTubeToolRunner : IYouTubeToolRunner
    {
        private readonly Func<string, YouTubePlaybackMode?, int, ResolvedYouTubeQueue> _resolver;
        private readonly bool _failStream;

        public FakeYouTubeToolRunner(Func<string, YouTubePlaybackMode?, int, ResolvedYouTubeQueue> resolver, bool failStream = false)
        {
            _resolver = resolver;
            _failStream = failStream;
        }

        public int ResolveCallCount { get; private set; }

        public Task<ResolvedYouTubeQueue> ResolveQueueAsync(string sourceUrl, YouTubePlaybackMode? playbackMode, int preferredQueueLength, CancellationToken cancellationToken)
        {
            ResolveCallCount++;
            return Task.FromResult(_resolver(sourceUrl, playbackMode, preferredQueueLength));
        }

        public async Task<string> MaterializeAudioAsync(ResolvedYouTubeSourceItem source, string artifactDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(artifactDirectory);
            var path = Path.Combine(artifactDirectory, $"{Guid.NewGuid():N}.mp3");
            await File.WriteAllTextAsync(path, $"fake-audio:{source.Title}", cancellationToken);
            return path;
        }

        public Task<TranscodedAudioStream> OpenTranscodedStreamAsync(ResolvedYouTubeSourceItem source, CancellationToken cancellationToken)
        {
            if (_failStream)
            {
                throw new InvalidOperationException("stream failed");
            }

            return Task.FromResult(new TranscodedAudioStream(new MemoryStream([1, 2, 3]), () => ValueTask.CompletedTask));
        }
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public FakeEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            WebRootPath = contentRootPath;
            ApplicationName = "SonosControl.Tests";
            EnvironmentName = "Development";
            WebRootFileProvider = new NullFileProvider();
            ContentRootFileProvider = new NullFileProvider();
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by)
        {
            _utcNow = _utcNow.Add(by);
        }
    }
}
