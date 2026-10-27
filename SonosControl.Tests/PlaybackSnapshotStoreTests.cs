using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class PlaybackSnapshotStoreTests
{
    private static PlaybackSnapshot Snapshot(string ip, bool playing, int volume, DateTimeOffset at)
        => new(ip, "Office", playing, volume, "uri", new SonosTrackInfo { Title = "Song" }, false, at, at, null);

    [Fact]
    public void TryApplyEventMergesIntoTheCurrentSnapshotAndMarksItFresh()
    {
        var store = new PlaybackSnapshotStore();
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Set(Snapshot("10.0.0.1", true, 30, earlier) with { IsStale = true, Error = "Playback status is delayed" });
        var changed = 0;
        store.Changed += () => changed++;
        var now = DateTimeOffset.UtcNow;

        Assert.True(store.TryApplyEvent("10.0.0.1", current => current with { Volume = 12 }, now));

        var result = store.Get("10.0.0.1")!;
        Assert.Equal(12, result.Volume);
        Assert.True(result.IsPlaying);
        Assert.False(result.IsStale);
        Assert.Null(result.Error);
        Assert.Equal(now, result.LastSuccessfulRefreshUtc);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void TryApplyEventNeedsAnExistingSnapshotOfAConfiguredSpeaker()
    {
        var store = new PlaybackSnapshotStore();
        var now = DateTimeOffset.UtcNow;
        Assert.False(store.TryApplyEvent("10.0.0.1", current => current, now));

        store.Set(Snapshot("10.0.0.2", true, 30, now));
        store.RemoveExcept(["10.0.0.1"]);

        Assert.False(store.TryApplyEvent("10.0.0.2", current => current, now));
        Assert.Null(store.Get("10.0.0.2"));
    }

    [Fact]
    public void TryApplyEventWinsOverAnOlderProbeButNotOverANewerOne()
    {
        var store = new PlaybackSnapshotStore();
        var now = DateTimeOffset.UtcNow;
        store.Set(Snapshot("10.0.0.1", true, 30, now));
        var olderProbe = store.BeginProbe("10.0.0.1");

        store.TryApplyEvent("10.0.0.1", current => current with { IsPlaying = false }, now);
        Assert.False(store.TrySet(Snapshot("10.0.0.1", true, 30, now), olderProbe));
        Assert.False(store.Get("10.0.0.1")!.IsPlaying);

        var newerProbe = store.BeginProbe("10.0.0.1");
        Assert.True(store.TrySet(Snapshot("10.0.0.1", true, 31, now), newerProbe));
        Assert.Equal(31, store.Get("10.0.0.1")!.Volume);
    }

    [Fact]
    public void ConsumersCannotMutateStoredTrackMetadataAndExplicitSettingsChangesBroadcast()
    {
        var store = new PlaybackSnapshotStore();
        var changed = 0;
        var settingsChanged = 0;
        store.Changed += () => changed++;
        store.SettingsChanged += () => settingsChanged++;
        store.Set(new PlaybackSnapshot("10.0.0.1", "Office", true, 30, "uri",
            new SonosTrackInfo { Title = "Original" }, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

        var copy = store.Get("10.0.0.1")!;
        copy.TrackInfo!.Title = "Mutated";
        store.PublishSettingsChanged();

        Assert.Equal("Original", store.Get("10.0.0.1")?.TrackInfo?.Title);
        Assert.Equal(1, changed);
        Assert.Equal(1, settingsChanged);
    }

    [Fact]
    public void OlderProbeCannotReplaceNewerSnapshotAndStaleTransitionNotifiesOnce()
    {
        var store = new PlaybackSnapshotStore();
        var changed = 0;
        store.Changed += () => changed++;
        var olderProbe = store.BeginProbe("10.0.0.1");
        var newerProbe = store.BeginProbe("10.0.0.1");
        var now = DateTimeOffset.UtcNow;

        Assert.False(store.TrySet(new PlaybackSnapshot("10.0.0.1", "Office", false, 10, "old", null,
            false, now, now, null), olderProbe));
        Assert.True(store.TrySet(new PlaybackSnapshot("10.0.0.1", "Office", true, 20, "new", null,
            false, now, now, null), newerProbe));
        store.MarkExpiredStale(now.AddSeconds(16), TimeSpan.FromSeconds(15));
        store.MarkExpiredStale(now.AddSeconds(32), TimeSpan.FromSeconds(15));

        Assert.Equal("new", store.Get("10.0.0.1")?.CurrentStationUrl);
        Assert.True(store.Get("10.0.0.1")?.IsStale);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void RemovedConfigurationInvalidatesProbeEvenWhenItHadNoSnapshot()
    {
        var store = new PlaybackSnapshotStore();
        var initial = store.BeginConfigurationRead();
        Assert.True(store.TryApplyConfiguration(initial.Sequence, initial.MutationVersion, ["10.0.0.1"], out var epoch));
        var queuedProbe = store.BeginProbe("10.0.0.1", epoch);
        var newerRead = store.BeginConfigurationRead();
        Assert.True(store.TryApplyConfiguration(newerRead.Sequence, newerRead.MutationVersion, [], out var nextEpoch));

        Assert.NotEqual(epoch, nextEpoch);
        Assert.False(store.IsConfigured("10.0.0.1", epoch));
        Assert.False(store.TrySet(new PlaybackSnapshot("10.0.0.1", "Office", true, 10, null, null,
            false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null), queuedProbe, epoch));
        Assert.Null(store.Get("10.0.0.1"));
    }

    [Fact]
    public void OlderConfigurationReadCannotRestoreRemovedSpeaker()
    {
        var store = new PlaybackSnapshotStore();
        var slowRead = store.BeginConfigurationRead();
        var newerRead = store.BeginConfigurationRead();
        Assert.True(store.TryApplyConfiguration(newerRead.Sequence, newerRead.MutationVersion, [], out _));

        Assert.False(store.TryApplyConfiguration(slowRead.Sequence, slowRead.MutationVersion, ["10.0.0.9"], out _));
        Assert.Empty(store.GetAll());
        Assert.False(store.IsConfigured("10.0.0.9", store.ConfigurationEpoch));
    }
}
