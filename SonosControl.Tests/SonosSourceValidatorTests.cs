using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests;

public class SonosSourceValidatorTests
{
    [Theory]
    [InlineData("radio", "radio.example.com/live.mp3")]
    [InlineData("radio", "https://radio.example.com/live.mp3")]
    [InlineData("radio", "x-sonosapi-stream:s123?sid=254")]
    [InlineData("radio", "x-rincon-mp3radio://radio.example.com/live.mp3")]
    [InlineData("spotify", "spotify:track:abc123")]
    [InlineData("spotify", "https://open.spotify.com/intl-de/playlist/abc123?si=test")]
    [InlineData("youtubemusic", "ytm:playlist:PL123")]
    [InlineData("youtubemusic", "https://music.youtube.com/watch?v=abc123")]
    [InlineData("youtube", "https://youtu.be/abc123?list=PL123")]
    [InlineData("youtube", "https://www.youtube.com/playlist?list=PL123")]
    public void SupportedPlaybackFormatsPassOffline(string type, string url)
        => Assert.Null(SonosSourceValidator.Validate(type, url));

    [Theory]
    [InlineData("radio", "")]
    [InlineData("radio", "not a stream")]
    [InlineData("spotify", "https://example.com/track/abc")]
    [InlineData("youtubemusic", "https://example.com/watch?v=abc")]
    [InlineData("youtube", "https://example.com/watch?v=abc")]
    [InlineData("unknown", "https://example.com")]
    public void InvalidSourcesReturnAFieldError(string type, string url)
        => Assert.False(string.IsNullOrWhiteSpace(SonosSourceValidator.Validate(type, url)));

    [Fact]
    public void SharedYouTubeNormalizationPreservesPlaylistAndRadioContext()
    {
        Assert.Equal("https://www.youtube.com/watch?v=abc123&list=PL123&start_radio=1",
            YouTubeSourceUrl.Normalize("https://youtu.be/abc123?start_radio=1&list=PL123"));
    }
}
