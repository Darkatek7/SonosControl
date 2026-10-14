using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SceneTargetResolverTests
{
    [Fact]
    public void ExplicitActionsAreAuthoritativeEvenWhenEverySpeakerIsExcluded()
    {
        var scene = new Scene
        {
            SpeakerIps = ["legacy"],
            Actions = [new() { SpeakerIp = "excluded", IncludeInPlayback = false }]
        };
        var settings = new SonosSettings { Speakers = [new() { IpAddress = "configured" }] };

        Assert.Empty(SceneTargetResolver.Resolve(scene, settings));
    }

    [Fact]
    public void LegacySpeakerIpsAreTrimmedAndDeduplicatedCaseInsensitively()
    {
        var scene = new Scene { SpeakerIps = [" A ", "a", "", "B"] };

        Assert.Equal(new[] { "A", "B" }, SceneTargetResolver.Resolve(scene, new SonosSettings()));
    }

    [Fact]
    public void LegacySceneWithoutTargetsRetainsHistoricalAllConfiguredBehavior()
    {
        var settings = new SonosSettings { Speakers = [new() { IpAddress = " A " }, new() { IpAddress = "a" }, new() { IpAddress = "B" }] };

        Assert.Equal(new[] { "A", "B" }, SceneTargetResolver.Resolve(new Scene(), settings));
    }
}
