using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Pages.Index.Components;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public class VolumeControlTests
{
    [Fact]
    public void VolumeControl_SynchronizesInputsAndClampsTypedValues()
    {
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        var changedValue = -1;
        var cut = ctx.RenderComponent<VolumeControl>(parameters => parameters
            .Add(component => component.Value, 25)
            .Add(component => component.MaxVolume, 80)
            .Add(component => component.IdPrefix, "test-volume")
            .Add(component => component.Label, "Office volume")
            .Add(component => component.ValueChanged, value => changedValue = value));

        var group = cut.Find("[data-qa='volume-control']");
        Assert.Equal("Office volume", group.GetAttribute("aria-label"));
        Assert.Equal("25", cut.Find("#test-volume-slider").GetAttribute("value"));
        Assert.Equal("25", cut.Find("#test-volume-number").GetAttribute("value"));
        Assert.Equal("80", cut.Find("#test-volume-number").GetAttribute("max"));

        cut.Find("#test-volume-number").Input(string.Empty);
        Assert.Equal(-1, changedValue);

        cut.Find("#test-volume-number").Input("95");
        Assert.Equal(80, changedValue);

        cut.SetParametersAndRender(parameters => parameters
            .Add(component => component.Value, changedValue)
            .Add(component => component.MaxVolume, 80)
            .Add(component => component.IdPrefix, "test-volume")
            .Add(component => component.Label, "Office volume")
            .Add(component => component.ValueChanged, value => changedValue = value));

        Assert.Equal("80", cut.Find("#test-volume-slider").GetAttribute("value"));
        Assert.Equal("80", cut.Find("#test-volume-number").GetAttribute("value"));
    }

    [Fact]
    public void RoomCard_UsesPlaybackVolumeAndConfiguredMaximum()
    {
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        var changedValue = -1;
        var speaker = new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.1" };
        var health = new DeviceHealthStatus
        {
            SpeakerIp = speaker.IpAddress,
            SpeakerName = speaker.Name,
            IsOnline = true,
            CurrentVolume = 12
        };

        var cut = ctx.RenderComponent<RoomCard>(parameters => parameters
            .Add(component => component.Speaker, speaker)
            .Add(component => component.Health, health)
            .Add(component => component.IsActive, true)
            .Add(component => component.ActiveVolume, 37)
            .Add(component => component.MaxVolume, 70)
            .Add(component => component.OnVolumeChanged, value => changedValue = value));

        Assert.Equal("37", cut.Find("#room-10-0-0-1-volume-slider").GetAttribute("value"));
        Assert.Equal("37", cut.Find("#room-10-0-0-1-volume-number").GetAttribute("value"));
        Assert.Equal("70", cut.Find("#room-10-0-0-1-volume-number").GetAttribute("max"));

        cut.Find("#room-10-0-0-1-volume-slider").Input("42");
        Assert.Equal(42, changedValue);
    }
}
