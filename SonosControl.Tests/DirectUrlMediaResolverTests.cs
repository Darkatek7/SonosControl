using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class DirectUrlMediaResolverTests
{
    [Theory]
    [InlineData("https://open.spotify.com/track/123", "spotify")]
    [InlineData("spotify:track:123", "spotify")]
    [InlineData("https://www.youtube.com/watch?v=abc123", "youtube")]
    [InlineData("https://youtu.be/abc123", "youtube")]
    [InlineData("https://music.youtube.com/watch?v=abc123", "youtubemusic")]
    [InlineData("https://stream.example/live", "station")]
    [InlineData("stream.example/live", "station")]
    public void Resolve_ClassifiesSupportedUrls(string rawUrl, string expectedMediaType)
    {
        var result = DirectUrlMediaResolver.Resolve(rawUrl);

        Assert.True(result.IsValid);
        Assert.Equal(expectedMediaType, result.MediaType);
        Assert.Equal(rawUrl.Trim(), result.NormalizedUrl);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/audio.mp3")]
    public void Resolve_RejectsInvalidInput(string rawUrl)
    {
        var result = DirectUrlMediaResolver.Resolve(rawUrl);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }
}
