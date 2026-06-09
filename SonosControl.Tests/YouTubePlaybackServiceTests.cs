using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class YouTubePlaybackServiceTests
{
    [Fact]
    public async Task OpenPlaybackAsync_WhenPlaylistSession_MaterializesFileAndCleanupDeletesIt()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"yt-playback-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var toolRunner = new FakeYouTubeToolRunner(isPlaylist: true);
            var options = Options.Create(new YouTubePlaybackOptions
            {
                PublicBaseUrl = "http://sonos-control.local:5107",
                ArtifactDirectory = tempRoot,
                SessionTtlMinutes = 5
            });

            var service = new YouTubePlaybackService(
                toolRunner,
                options,
                new FakeEnvironment(tempRoot),
                NullLogger<YouTubePlaybackService>.Instance,
                timeProvider);

            var session = await service.PreparePlaybackAsync("https://www.youtube.com/playlist?list=PL123");
            Assert.True(session.UsesTempFile);
            Assert.Contains("/api/youtube-audio/", session.StreamUrl);

            var openResult = await service.OpenPlaybackAsync(session.SessionId);
            Assert.NotNull(openResult);
            Assert.NotNull(openResult!.FilePath);
            Assert.True(File.Exists(openResult.FilePath));

            timeProvider.Advance(TimeSpan.FromMinutes(6));
            await service.CleanupExpiredSessionsAsync();

            Assert.False(File.Exists(openResult.FilePath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class FakeYouTubeToolRunner : IYouTubeToolRunner
    {
        private readonly bool _isPlaylist;

        public FakeYouTubeToolRunner(bool isPlaylist)
        {
            _isPlaylist = isPlaylist;
        }

        public Task<ResolvedYouTubeSource> ResolveAsync(string sourceUrl, CancellationToken cancellationToken)
            => Task.FromResult(new ResolvedYouTubeSource(sourceUrl, "https://www.youtube.com/watch?v=abc123xyz00", "https://audio.example/test", "Test Video", _isPlaylist));

        public async Task<string> MaterializeAudioAsync(ResolvedYouTubeSource source, string artifactDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(artifactDirectory);
            var path = Path.Combine(artifactDirectory, $"{Guid.NewGuid():N}.mp3");
            await File.WriteAllTextAsync(path, "fake-audio", cancellationToken);
            return path;
        }

        public Task<TranscodedAudioStream> OpenTranscodedStreamAsync(ResolvedYouTubeSource source, CancellationToken cancellationToken)
            => Task.FromResult(new TranscodedAudioStream(new MemoryStream([1, 2, 3]), () => ValueTask.CompletedTask));
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
