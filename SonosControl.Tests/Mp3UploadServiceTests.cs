using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SonosControl.Web.Controllers;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class Mp3UploadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sonos-mp3-test-{Guid.NewGuid():N}");
    private readonly TestClock _clock = new();
    internal static readonly byte[] Mp3Bytes = [0xff, 0xfb, 0x90, 0x00, 1, 2, 3, 4];

    internal static Mp3UploadService CreateService(string root, TimeProvider? clock = null,
        long maxSize = 100 * 1024 * 1024, string baseUrl = "http://sonos.local:5107/app")
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(root);
        return new Mp3UploadService(environment.Object,
            Options.Create(new Mp3UploadOptions { MaxFileSizeBytes = maxSize }),
            Options.Create(new YouTubePlaybackOptions { PublicBaseUrl = baseUrl }),
            clock ?? TimeProvider.System, NullLogger<Mp3UploadService>.Instance);
    }

    [Fact]
    public async Task SaveAndOpen_PreservesBytes_UsesGeneratedNames_AndSupportsRanges()
    {
        var service = CreateService(_root, _clock);
        using var source = new MemoryStream(Mp3Bytes);
        var upload = await service.SaveAsync(source, "../../my song.MP3", 15);
        Assert.Equal("my song", upload.Title);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(15), upload.ExpiresAtUtc);
        Assert.StartsWith("http://sonos.local:5107/app/api/mp3-audio/", upload.StreamUrl);
        Assert.DoesNotContain("my song", upload.StreamUrl);

        var controller = new Mp3AudioController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var result = Assert.IsType<FileStreamResult>(controller.GetAudio(upload.Id));
        await using var stream = result.FileStream;
        Assert.True(result.EnableRangeProcessing);
        Assert.True(stream.CanSeek);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(Mp3Bytes, copy.ToArray());
    }

    [Fact]
    public async Task Cleanup_ExpiresAtDeadline_PreservesFreshFiles_AndSurvivesRestart()
    {
        var service = CreateService(_root, _clock);
        var expired = await service.SaveAsync(new MemoryStream(Mp3Bytes), "first.mp3", 1);
        var fresh = await service.SaveAsync(new MemoryStream(Mp3Bytes), "second.mp3", 5);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(service.OpenRead(expired.Id));

        var restarted = CreateService(_root, _clock);
        restarted.CleanupExpiredFiles();
        var remaining = Directory.GetFiles(_root, "*.mp3", SearchOption.AllDirectories);
        Assert.Single(remaining);
        Assert.Contains(fresh.Id, remaining[0]);
        await using var stream = restarted.OpenRead(fresh.Id);
        Assert.NotNull(stream);
        Assert.IsType<NotFoundResult>(new Mp3AudioController(restarted).GetAudio(expired.Id));
    }

    [Theory]
    [InlineData("audio.wav", 10, false)]
    [InlineData("audio.mp3", 0, false)]
    [InlineData("audio.mp3", 10081, false)]
    [InlineData("audio.mp3", 10, true)]
    public async Task Save_RejectsInvalidFilesOrRetention_WithoutLeavingArtifacts(string name, int retention, bool invalidHeader)
    {
        var service = CreateService(_root, _clock);
        using var stream = new MemoryStream(invalidHeader ? [1, 2, 3, 4] : Mp3Bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(stream, name, retention));
        Assert.True(!Directory.Exists(_root) || Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length == 0);
    }

    [Fact]
    public async Task Save_EnforcesLimitWhileStreaming_AndRemovesPartialFiles()
    {
        var service = CreateService(_root, _clock, maxSize: 4);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(new MemoryStream(Mp3Bytes), "audio.mp3", 10));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Save_CancelledUpload_LeavesNoPartialFiles()
    {
        var service = CreateService(_root, _clock);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(new MemoryStream(Mp3Bytes), "audio.mp3", 10, cts.Token));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:5107")]
    [InlineData("ftp://sonos.local")]
    public async Task Save_RequiresReachableHttpBaseUrl(string baseUrl)
    {
        var service = CreateService(_root, _clock, baseUrl: baseUrl);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(new MemoryStream(Mp3Bytes), "audio.mp3", 10));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("../../secret")]
    [InlineData("253402300800000-0123456789abcdef0123456789abcdef")]
    [InlineData("0-0123456789abcdef0123456789abcdef")]
    public void OpenRead_InvalidMissingOrExpiredIds_ReturnNotFound(string id)
    {
        var service = CreateService(_root, _clock);
        Assert.Null(service.OpenRead(id));
        service.Delete(id);
    }

    [Fact]
    public async Task Cleanup_RemovesAbandonedUploads_AndAllowsOpenPlaybackToFinish()
    {
        var service = CreateService(_root, _clock);
        var upload = await service.SaveAsync(new MemoryStream(Mp3Bytes), "audio.mp3", 1);
        await using var stream = service.OpenRead(upload.Id);
        Assert.NotNull(stream);
        var directory = Path.Combine(_root, "artifacts", "mp3-uploads");
        var abandoned = Path.Combine(directory, $"{Guid.NewGuid():N}.upload");
        await File.WriteAllTextAsync(abandoned, "partial");
        File.SetLastWriteTimeUtc(abandoned, _clock.GetUtcNow().UtcDateTime.AddDays(-2));
        _clock.Advance(TimeSpan.FromMinutes(1));
        service.CleanupExpiredFiles();
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Equal(0xff, stream.ReadByte());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
