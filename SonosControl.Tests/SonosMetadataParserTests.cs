using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests;

public class SonosMetadataParserTests
{
    [Fact]
    public void ParseTrackMetadata_RadioStream_ReadsStreamContentAndRewritesRelativeArt()
    {
        var track = SonosMetadataParser.ParseTrackMetadata(SonosEventParserTests.RadioDidl, "192.0.2.10");

        Assert.Equal("Radio & Friends", track.Title);
        Assert.Equal("Daft Punk - One More Time", track.StreamContent);
        Assert.Equal("http://192.0.2.10:1400/getaa?s=1&u=x-rincon-mp3radio", track.AlbumArtUri);
        Assert.Equal(string.Empty, track.Artist);
        Assert.Equal(string.Empty, track.Album);
    }

    [Fact]
    public void ParseTrackMetadata_KeepsAbsoluteArt()
    {
        const string didl = "<DIDL-Lite><item><dc:title>Song</dc:title><upnp:albumArtURI>https://art.example/cover.jpg</upnp:albumArtURI></item></DIDL-Lite>";

        Assert.Equal("https://art.example/cover.jpg", SonosMetadataParser.ParseTrackMetadata(didl, "192.0.2.10").AlbumArtUri);
    }

    [Fact]
    public void ParseTrackMetadata_PrefersUpnpArtistOverCreator()
    {
        const string didl = "<DIDL-Lite><item><dc:title>Song</dc:title><dc:creator>Creator</dc:creator><upnp:artist>Artist</upnp:artist><upnp:album>Album</upnp:album></item></DIDL-Lite>";

        var track = SonosMetadataParser.ParseTrackMetadata(didl, "192.0.2.10");

        Assert.Equal("Artist", track.Artist);
        Assert.Equal("Album", track.Album);
    }

    [Fact]
    public void ParseTrackMetadata_FallsBackToCreatorAndDecodesEntities()
    {
        const string didl = "<DIDL-Lite><item><dc:title>Youssou N&apos;dour - 7 Seconds</dc:title><dc:creator>A&amp;B</dc:creator></item></DIDL-Lite>";

        var track = SonosMetadataParser.ParseTrackMetadata(didl, "192.0.2.10");

        Assert.Equal("Youssou N'dour - 7 Seconds", track.Title);
        Assert.Equal("A&B", track.Artist);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<DIDL-Lite><item><upnp:albumArtURI> </upnp:albumArtURI></item></DIDL-Lite>")]
    public void ParseTrackMetadata_EmptyMetadata_ReturnsEmptyTrack(string didl)
    {
        var track = SonosMetadataParser.ParseTrackMetadata(didl, "192.0.2.10");

        Assert.Equal(string.Empty, track.Title);
        Assert.Equal(string.Empty, track.Artist);
        Assert.Null(track.StreamContent);
        Assert.Null(track.AlbumArtUri);
        Assert.False(track.IsValidMetadata());
    }

    [Theory]
    [InlineData("ZPSTR_CONNECTING")]
    [InlineData("ZPSTR_BUFFERING")]
    public void ParseTrackMetadata_DropsConnectionPlaceholders(string placeholder)
    {
        var didl = $"<DIDL-Lite><item><r:streamContent>{placeholder}</r:streamContent><dc:title>live.aacp</dc:title></item></DIDL-Lite>";

        var track = SonosMetadataParser.ParseTrackMetadata(didl, "192.0.2.10");

        Assert.Null(track.StreamContent);
        Assert.DoesNotContain("ZPSTR", track.GetDisplayString());
    }
}
