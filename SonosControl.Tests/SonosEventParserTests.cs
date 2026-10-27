using System.Net;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;
using Xunit;

namespace SonosControl.Tests;

public class SonosEventParserTests
{
    internal const string RadioDidl = "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\"><item id=\"-1\" parentID=\"-1\" restricted=\"true\"><res protocolInfo=\"x-rincon-mp3radio:*:*:*\">x-rincon-mp3radio://stream.example/live</res><r:streamContent>Daft Punk - One More Time</r:streamContent><dc:title>Radio &amp; Friends</dc:title><upnp:class>object.item</upnp:class><upnp:albumArtURI>/getaa?s=1&amp;u=x-rincon-mp3radio</upnp:albumArtURI></item></DIDL-Lite>";

    // Shape of the initial NOTIFY a speaker sends after SUBSCRIBE: LastChange is escaped once inside the
    // property set, and CurrentTrackMetaData is escaped again inside LastChange.
    private const string InitialAvTransportBody = """
<e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0"><e:property><LastChange>&lt;Event xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/AVT/&quot; xmlns:r=&quot;urn:schemas-rinconnetworks-com:metadata-1-0/&quot;&gt;&lt;InstanceID val=&quot;0&quot;&gt;&lt;TransportState val=&quot;PLAYING&quot;/&gt;&lt;CurrentPlayMode val=&quot;NORMAL&quot;/&gt;&lt;NumberOfTracks val=&quot;1&quot;/&gt;&lt;CurrentTrack val=&quot;1&quot;/&gt;&lt;CurrentTrackURI val=&quot;x-rincon-mp3radio://stream.example/live?a=1&amp;amp;b=2&quot;/&gt;&lt;CurrentTrackDuration val=&quot;&quot;/&gt;&lt;CurrentTrackMetaData val=&quot;&amp;lt;DIDL-Lite xmlns:dc=&amp;quot;http://purl.org/dc/elements/1.1/&amp;quot; xmlns:upnp=&amp;quot;urn:schemas-upnp-org:metadata-1-0/upnp/&amp;quot; xmlns:r=&amp;quot;urn:schemas-rinconnetworks-com:metadata-1-0/&amp;quot;&amp;gt;&amp;lt;item id=&amp;quot;-1&amp;quot;&amp;gt;&amp;lt;r:streamContent&amp;gt;AC/DC - T.N.T.&amp;lt;/r:streamContent&amp;gt;&amp;lt;dc:title&amp;gt;Rock &amp;amp;amp; Roll Radio&amp;lt;/dc:title&amp;gt;&amp;lt;/item&amp;gt;&amp;lt;/DIDL-Lite&amp;gt;&quot;/&gt;&lt;r:NextTrackURI val=&quot;&quot;/&gt;&lt;AVTransportURI val=&quot;x-rincon-mp3radio://stream.example/live?a=1&amp;amp;b=2&quot;/&gt;&lt;CurrentMediaDuration val=&quot;&quot;/&gt;&lt;TransportStatus val=&quot;OK&quot;/&gt;&lt;r:SleepTimerGeneration val=&quot;0&quot;/&gt;&lt;/InstanceID&gt;&lt;/Event&gt;</LastChange></e:property></e:propertyset>
""";

    private const string RenderingControlBody = """
<e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0"><e:property><LastChange>&lt;Event xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/RCS/&quot;&gt;&lt;InstanceID val=&quot;0&quot;&gt;&lt;Volume channel=&quot;LF&quot; val=&quot;100&quot;/&gt;&lt;Volume channel=&quot;Master&quot; val=&quot;23&quot;/&gt;&lt;Volume channel=&quot;RF&quot; val=&quot;100&quot;/&gt;&lt;Mute channel=&quot;Master&quot; val=&quot;0&quot;/&gt;&lt;Bass val=&quot;0&quot;/&gt;&lt;/InstanceID&gt;&lt;/Event&gt;</LastChange></e:property></e:propertyset>
""";

    /// <summary>Wraps an AVTransport InstanceID body the way a speaker escapes it in a NOTIFY request.</summary>
    internal static string AvTransportBody(string instanceContent)
        => PropertySet($"<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/AVT/\" xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\"><InstanceID val=\"0\">{instanceContent}</InstanceID></Event>");

    internal static string Attribute(string value) => WebUtility.HtmlEncode(value);

    private static string PropertySet(string lastChange)
        => $"<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><LastChange>{WebUtility.HtmlEncode(lastChange)}</LastChange></e:property></e:propertyset>";

    [Fact]
    public void Parse_InitialAvTransportEvent_ReadsTransportAndTrackState()
    {
        var result = SonosEventParser.Parse(SonosEventService.AVTransport, InitialAvTransportBody);

        Assert.NotNull(result);
        Assert.Equal("PLAYING", result.TransportState);
        Assert.Equal("x-rincon-mp3radio://stream.example/live?a=1&b=2", result.TransportUri);
        Assert.Equal("x-rincon-mp3radio://stream.example/live?a=1&b=2", result.TrackUri);
        Assert.Equal(1, result.TrackNumber);
        Assert.Null(result.Volume);
        var track = SonosMetadataParser.ParseTrackMetadata(result.TrackMetadata!, "192.0.2.10");
        Assert.Equal("Rock & Roll Radio", track.Title);
        Assert.Equal("AC/DC - T.N.T.", track.StreamContent);
    }

    [Fact]
    public void Parse_RecordedPlay1InitialEvent_ReadsRadioState()
    {
        var result = SonosEventParser.Parse(SonosEventService.AVTransport, SonosEventFixtures.Play1AvTransportInitial);

        Assert.NotNull(result);
        Assert.Equal("PLAYING", result.TransportState);
        Assert.Equal("x-rincon-mp3radio://radiorecord.hostingradio.ru/summerparty96.aacp", result.TransportUri);
        Assert.Equal("aac://http://radiorecord.hostingradio.ru/summerparty96.aacp", result.TrackUri);
        Assert.Equal(1, result.TrackNumber);
        var track = SonosMetadataParser.ParseTrackMetadata(result.TrackMetadata!, "192.0.2.10");
        Assert.Equal("summerparty96.aacp", track.Title);
        Assert.Equal("FISHER/LAUREL - Staring At The Sun", track.StreamContent);
        Assert.Equal("FISHER/LAUREL - Staring At The Sun", track.GetDisplayString());
        Assert.Null(track.AlbumArtUri);
    }

    [Fact]
    public void Parse_RecordedStreamTitleChange_OmitsTransportUri()
    {
        var result = SonosEventParser.Parse(SonosEventService.AVTransport, SonosEventFixtures.Play1AvTransportStreamTitleChange);

        Assert.NotNull(result);
        Assert.Equal("PLAYING", result.TransportState);
        Assert.Null(result.TransportUri);
        Assert.Equal("aac://http://radiorecord.hostingradio.ru/summerparty96.aacp", result.TrackUri);
        Assert.Equal("DEXTER TROY - Abducted", SonosMetadataParser.ParseTrackMetadata(result.TrackMetadata!, "192.0.2.10").StreamContent);
    }

    public static TheoryData<string, string, string?> RecordedPlaybackSequence => new()
    {
        { SonosEventFixtures.Era100AvTransportStoppedAfterPause, "STOPPED", null },
        // Sonos puts a placeholder into streamContent while it connects to the stream.
        { SonosEventFixtures.Era100AvTransportTransitioningAfterPlay, "TRANSITIONING", "ZPSTR_CONNECTING" },
        { SonosEventFixtures.Era100AvTransportPlayingWithoutStreamTitle, "PLAYING", null }
    };

    [Theory]
    [MemberData(nameof(RecordedPlaybackSequence))]
    public void Parse_RecordedPauseAndPlay_ReportsEachTransportState(string body, string expectedState, string? expectedStreamContent)
    {
        var result = SonosEventParser.Parse(SonosEventService.AVTransport, body);

        Assert.NotNull(result);
        Assert.Equal(expectedState, result.TransportState);
        Assert.Null(result.TransportUri);
        Assert.Equal("aac://http://radiorecord.hostingradio.ru/summerparty96.aacp", result.TrackUri);
        var track = SonosMetadataParser.ParseTrackMetadata(result.TrackMetadata!, "192.0.2.10");
        Assert.Equal("summerparty96.aacp", track.Title);
        Assert.Equal(expectedStreamContent, string.IsNullOrEmpty(track.StreamContent) ? null : track.StreamContent);
    }

    public static TheoryData<string, int> RecordedVolumeEvents => new()
    {
        { SonosEventFixtures.Play1RenderingControlInitial, 14 },
        { SonosEventFixtures.Era100RenderingControlInitial, 8 },
        { SonosEventFixtures.Era100RenderingControlVolumeChange, 10 }
    };

    [Theory]
    [MemberData(nameof(RecordedVolumeEvents))]
    public void Parse_RecordedRenderingControlEvents_ReadMasterVolume(string body, int expected)
    {
        var result = SonosEventParser.Parse(SonosEventService.RenderingControl, body);

        Assert.Equal(expected, result?.Volume);
    }

    [Fact]
    public void Parse_TrackChangeOnly_LeavesUnreportedFieldsNull()
    {
        var body = AvTransportBody($"<CurrentTrack val=\"4\"/><CurrentTrackMetaData val=\"{Attribute(RadioDidl)}\"/>");

        var result = SonosEventParser.Parse(SonosEventService.AVTransport, body);

        Assert.NotNull(result);
        Assert.Null(result.TransportState);
        Assert.Null(result.TransportUri);
        Assert.Null(result.TrackUri);
        Assert.Equal(4, result.TrackNumber);
        Assert.Equal(RadioDidl, result.TrackMetadata);
        Assert.True(result.HasValues);
    }

    [Theory]
    [InlineData("TRANSITIONING")]
    [InlineData("PAUSED_PLAYBACK")]
    [InlineData("STOPPED")]
    public void Parse_ReportsTransportStateVerbatim(string state)
    {
        var result = SonosEventParser.Parse(SonosEventService.AVTransport, AvTransportBody($"<TransportState val=\"{state}\"/>"));

        Assert.Equal(state, result?.TransportState);
    }

    [Fact]
    public void Parse_GroupMember_ReportsCoordinatorUri()
    {
        var body = AvTransportBody("<TransportState val=\"PLAYING\"/><AVTransportURI val=\"x-rincon:RINCON_000E58000001400\"/>");

        var result = SonosEventParser.Parse(SonosEventService.AVTransport, body);

        Assert.Equal("x-rincon:RINCON_000E58000001400", result?.TransportUri);
    }

    [Fact]
    public void Parse_YouTubeSessionTrack_KeepsAppStreamUri()
    {
        var body = AvTransportBody("<CurrentTrack val=\"3\"/><CurrentTrackURI val=\"http://192.0.2.50:8080/api/youtube-audio/session1/2\"/>");

        var result = SonosEventParser.Parse(SonosEventService.AVTransport, body);

        Assert.Equal("http://192.0.2.50:8080/api/youtube-audio/session1/2", result?.TrackUri);
        Assert.True(SonosMetadataParser.TryParseYouTubePlaybackUri(result?.TrackUri, out var sessionId, out var itemIndex));
        Assert.Equal("session1", sessionId);
        Assert.Equal(2, itemIndex);
    }

    [Fact]
    public void Parse_EmptyAndNotImplementedValues_AreReportedAsEmpty()
    {
        var body = AvTransportBody("<CurrentTrackMetaData val=\"\"/><CurrentTrackURI val=\"NOT_IMPLEMENTED\"/><CurrentTrack val=\"NOT_IMPLEMENTED\"/>");

        var result = SonosEventParser.Parse(SonosEventService.AVTransport, body);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.TrackMetadata);
        Assert.Equal(string.Empty, result.TrackUri);
        Assert.Null(result.TrackNumber);
    }

    [Fact]
    public void Parse_RenderingControl_ReadsMasterVolumeOnly()
    {
        var result = SonosEventParser.Parse(SonosEventService.RenderingControl, RenderingControlBody);

        Assert.NotNull(result);
        Assert.Equal(23, result.Volume);
        Assert.Null(result.TransportState);
        Assert.Null(result.TrackMetadata);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("loud")]
    public void Parse_RenderingControl_IgnoresInvalidVolume(string value)
    {
        var body = PropertySet($"<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/RCS/\"><InstanceID val=\"0\"><Volume channel=\"Master\" val=\"{value}\"/></InstanceID></Event>");

        var result = SonosEventParser.Parse(SonosEventService.RenderingControl, body);

        Assert.NotNull(result);
        Assert.Null(result.Volume);
        Assert.False(result.HasValues);
    }

    [Fact]
    public void Parse_IgnoresOtherInstances()
    {
        var body = PropertySet("<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/AVT/\"><InstanceID val=\"1\"><TransportState val=\"PLAYING\"/></InstanceID></Event>");

        Assert.Null(SonosEventParser.Parse(SonosEventService.AVTransport, body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><LastChange>&lt;Event&gt;</LastChange></e:property></e:propertyset>")]
    [InlineData("<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><ZoneGroupState>x</ZoneGroupState></e:property></e:propertyset>")]
    public void Parse_UnreadableBodies_ReturnNull(string? body)
    {
        Assert.Null(SonosEventParser.Parse(SonosEventService.AVTransport, body));
    }

    [Fact]
    public void Parse_RejectsDocumentTypeDefinitions()
    {
        const string body = "<!DOCTYPE e [<!ENTITY x \"PLAYING\">]><e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><LastChange>&x;</LastChange></e:property></e:propertyset>";

        Assert.Null(SonosEventParser.Parse(SonosEventService.AVTransport, body));
    }

    [Fact]
    public void Parse_RejectsOversizedBodies()
    {
        var body = AvTransportBody($"<TransportState val=\"PLAYING\"/><r:Padding val=\"{new string('x', SonosEventParser.MaxBodyLength)}\"/>");

        Assert.Null(SonosEventParser.Parse(SonosEventService.AVTransport, body));
    }
}
