using SonosControl.DAL.Models;
using Xunit;

namespace SonosControl.Tests;

public class YouTubeQueueMetadataBuilderTests
{
    [Fact]
    public void Build_IncludesRichYouTubeMetadata()
    {
        var item = new YouTubePlaybackQueueItem
        {
            Title = "Lovergirl",
            Artist = "Sabrina Carpenter Channel",
            AlbumArtUrl = "https://images.example/cover.jpg",
            StreamContent = "Sabrina Carpenter Channel - Lovergirl",
            StreamUrl = "http://app/api/youtube-audio/session1/0"
        };

        var metadata = YouTubeQueueMetadataBuilder.Build(item);

        Assert.Contains("<dc:title>Lovergirl</dc:title>", metadata);
        Assert.Contains("<dc:creator>Sabrina Carpenter Channel</dc:creator>", metadata);
        Assert.Contains("<upnp:artist>Sabrina Carpenter Channel</upnp:artist>", metadata);
        Assert.Contains("<r:streamContent>Sabrina Carpenter Channel - Lovergirl</r:streamContent>", metadata);
        Assert.Contains("<upnp:albumArtURI>https://images.example/cover.jpg</upnp:albumArtURI>", metadata);
    }

    [Fact]
    public void Build_EscapesXmlSensitiveValues()
    {
        var metadata = YouTubeQueueMetadataBuilder.Build(
            "A & B <Live>",
            "http://app/api/youtube-audio/session1/0?title=one&two=true",
            "Channel \"One\"",
            "https://images.example/cover?x=1&y=2",
            "Channel \"One\" - A & B <Live>");

        Assert.Contains("A &amp; B &lt;Live&gt;", metadata);
        Assert.Contains("Channel &quot;One&quot;", metadata);
        Assert.Contains("title=one&amp;two=true", metadata);
        Assert.Contains("cover?x=1&amp;y=2", metadata);
    }

    [Fact]
    public void FormatStreamContent_DoesNotDuplicateArtistPrefix()
    {
        var streamContent = YouTubeQueueMetadataBuilder.FormatStreamContent(
            "Madison Beer - lovergirl (Official Music Video)",
            "Madison Beer");

        Assert.Equal("Madison Beer - lovergirl (Official Music Video)", streamContent);
    }
}
