using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class PlaybackSnapshotMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    private static PlaybackSnapshot Snapshot(bool playing = true, int? volume = 8, string? station = "x-rincon-mp3radio://radio.test/live",
        SonosTrackInfo? track = null)
        => new("10.0.0.1", "Office", playing, volume, station,
            track ?? new SonosTrackInfo { Title = "live.aacp", StreamContent = "Artist - Song" }, false, Now, Now, null);

    [Fact]
    public void ApplyEvent_KeepsFieldsTheEventDidNotReport()
    {
        var result = PlaybackSnapshotMerge.ApplyEvent(Snapshot(), new SonosPlaybackEvent(SonosEventService.RenderingControl) { Volume = 10 }, null);

        Assert.Equal(10, result.Volume);
        Assert.True(result.IsPlaying);
        Assert.Equal("x-rincon-mp3radio://radio.test/live", result.CurrentStationUrl);
        Assert.Equal("Artist - Song", result.TrackInfo?.StreamContent);
    }

    [Theory]
    [InlineData("PLAYING", true)]
    [InlineData("TRANSITIONING", true)]
    [InlineData("STOPPED", false)]
    [InlineData("PAUSED_PLAYBACK", false)]
    public void ApplyEvent_MapsTransportState(string state, bool expected)
    {
        var result = PlaybackSnapshotMerge.ApplyEvent(Snapshot(playing: !expected),
            new SonosPlaybackEvent(SonosEventService.AVTransport) { TransportState = state }, null);

        Assert.Equal(expected, result.IsPlaying);
    }

    [Fact]
    public void ApplyEvent_IgnoresEmptyTransportStateAndAppliesEmptyTransportUri()
    {
        var result = PlaybackSnapshotMerge.ApplyEvent(Snapshot(),
            new SonosPlaybackEvent(SonosEventService.AVTransport) { TransportState = "", TransportUri = "" }, null);

        Assert.True(result.IsPlaying);
        Assert.Equal(string.Empty, result.CurrentStationUrl);
    }

    [Fact]
    public void KeepStreamTitle_KeepsRadioTitleWhileTheStreamReconnects()
    {
        var previous = new SonosTrackInfo { Title = "live.aacp", StreamContent = "Artist - Song" };

        Assert.Same(previous, PlaybackSnapshotMerge.KeepStreamTitle(previous, new SonosTrackInfo { Title = "live.aacp" }));
    }

    [Theory]
    [InlineData("other.aacp", null)]
    [InlineData("live.aacp", "Artist - Next Song")]
    public void KeepStreamTitle_TakesNewTrackWhenTitleOrStreamTitleChanged(string title, string? streamContent)
    {
        var previous = new SonosTrackInfo { Title = "live.aacp", StreamContent = "Artist - Song" };
        var next = new SonosTrackInfo { Title = title, StreamContent = streamContent };

        Assert.Same(next, PlaybackSnapshotMerge.KeepStreamTitle(previous, next));
    }

    [Fact]
    public void KeepStreamTitle_DoesNotApplyToTracksWithoutStreamTitle()
    {
        var previous = new SonosTrackInfo { Title = "Song", Artist = "First" };
        var next = new SonosTrackInfo { Title = "Song", Artist = "Second" };

        Assert.Same(next, PlaybackSnapshotMerge.KeepStreamTitle(previous, next));
    }

    [Fact]
    public void FollowCoordinator_TakesPlaybackButKeepsVolumeAndSource()
    {
        var member = Snapshot(playing: false, volume: 5, station: "x-rincon:RINCON_C");
        var coordinator = Snapshot(playing: true, volume: 30, track: new SonosTrackInfo { Title = "Song", Artist = "Band" });

        var result = PlaybackSnapshotMerge.FollowCoordinator(member, coordinator);

        Assert.True(result.IsPlaying);
        Assert.Equal("Song", result.TrackInfo?.Title);
        Assert.Equal(5, result.Volume);
        Assert.Equal("x-rincon:RINCON_C", result.CurrentStationUrl);
    }
}
