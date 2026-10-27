using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

/// <summary>Rules for folding partial speaker observations into a playback snapshot.</summary>
public static class PlaybackSnapshotMerge
{
    /// <summary>Applies the fields an event reported and keeps everything else.</summary>
    public static PlaybackSnapshot ApplyEvent(PlaybackSnapshot current, SonosPlaybackEvent playbackEvent, SonosTrackInfo? track)
    {
        var next = current;
        if (!string.IsNullOrEmpty(playbackEvent.TransportState))
            next = next with { IsPlaying = IsPlayingState(playbackEvent.TransportState) };
        if (playbackEvent.Volume is { } volume)
            next = next with { Volume = volume };
        if (playbackEvent.TransportUri is not null)
            next = next with { CurrentStationUrl = playbackEvent.TransportUri };
        if (track is not null)
            next = next with { TrackInfo = KeepStreamTitle(current.TrackInfo, track) };
        return next;
    }

    private const string GroupMemberPrefix = "x-rincon:";

    /// <summary>The coordinator UUID of a group member's transport URI (<c>x-rincon:RINCON_...</c>), or null.</summary>
    public static string? CoordinatorUuid(string? transportUri)
        => transportUri?.StartsWith(GroupMemberPrefix, StringComparison.OrdinalIgnoreCase) == true
            ? NormalizeUuid(transportUri[GroupMemberPrefix.Length..])
            : null;

    public static string? NormalizeUuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? value[5..] : value;
    }

    /// <summary>A group member plays whatever its coordinator plays; volume and source URI stay its own.</summary>
    public static PlaybackSnapshot FollowCoordinator(PlaybackSnapshot member, PlaybackSnapshot coordinator)
        => member with { IsPlaying = coordinator.IsPlaying, TrackInfo = coordinator.TrackInfo };

    /// <summary>
    /// TRANSITIONING lasts about two seconds after Play or a skip, so it counts as playing instead of
    /// flashing a paused state.
    /// </summary>
    public static bool IsPlayingState(string transportState)
        => string.Equals(transportState, "PLAYING", StringComparison.OrdinalIgnoreCase)
            || string.Equals(transportState, "TRANSITIONING", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps the previous radio title while a stream reconnects. Sonos then reports the same stream
    /// file name as title but no stream title yet.
    /// </summary>
    public static SonosTrackInfo? KeepStreamTitle(SonosTrackInfo? previous, SonosTrackInfo? next)
        => next is not null && string.IsNullOrWhiteSpace(next.StreamContent)
            && !string.IsNullOrWhiteSpace(previous?.StreamContent)
            && string.Equals(previous.Title, next.Title, StringComparison.Ordinal)
            ? previous
            : next;
}
