using Bunit;
using SonosControl.DAL.Models;
using SonosControl.Web.Pages.Index.Components;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class IndexPageLifecycleTests
{
    [Fact]
    public void RoomCardUsesSharedPlaybackSnapshotInsteadOfItsHealthPollPlayingValue()
    {
        using var context = new BunitContext();
        RepositoryTestServices.AddDefaults(context.Services);
        var cut = context.Render<RoomCard>(parameters => parameters
            .Add(component => component.Speaker, new SonosSpeaker { Name = "Office", IpAddress = "192.0.2.30" })
            .Add(component => component.Health, new DeviceHealthStatus
            {
                SpeakerIp = "192.0.2.30",
                SpeakerName = "Office",
                IsOnline = true,
                IsPlaying = false
            })
            .Add(component => component.Playback, new PlaybackSnapshot(
                "192.0.2.30", "Office", true, 28, "https://radio.test/live", null,
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null))
            .Add(component => component.IsActive, true)
            .Add(component => component.ActiveVolume, 28));

        Assert.Contains("Playing", cut.Find(".room-card__status").TextContent);
        Assert.Equal("Pause playback", cut.Find(".room-card__play").GetAttribute("aria-label"));
    }

    [Fact]
    public void StalePlaybackSnapshotOverridesHealthyButOutdatedHealthStatus()
    {
        using var context = new BunitContext();
        RepositoryTestServices.AddDefaults(context.Services);
        var cut = context.Render<RoomCard>(parameters => parameters
            .Add(component => component.Speaker, new SonosSpeaker { Name = "Office", IpAddress = "192.0.2.30" })
            .Add(component => component.Health, new DeviceHealthStatus { SpeakerIp = "192.0.2.30", SpeakerName = "Office", IsOnline = true, IsPlaying = true })
            .Add(component => component.Playback, new PlaybackSnapshot("192.0.2.30", "Office", true, 28, null, null,
                true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-20), "Playback status unavailable")));

        Assert.Contains("Playback status unavailable", cut.Find(".room-card__status").TextContent);
        Assert.Contains("is-unknown", cut.Find("article").ClassList);
        Assert.True(cut.Find("button.room-card__activate").HasAttribute("disabled"));
    }
}
