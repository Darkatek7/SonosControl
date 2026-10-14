using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class PlaybackSnapshotStoreTests
{
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
