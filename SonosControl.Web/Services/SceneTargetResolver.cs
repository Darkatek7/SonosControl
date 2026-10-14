using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

/// <summary>Resolves explicit scene selections while retaining compatibility with legacy scenes.</summary>
public static class SceneTargetResolver
{
    public static IReadOnlyList<string> Resolve(Scene scene, SonosSettings settings)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);

        IEnumerable<string?> ips = scene.Actions?.Count > 0
            ? scene.Actions.Where(action => action.IncludeInPlayback).Select(action => action.SpeakerIp)
            : scene.SpeakerIps?.Any(ip => !string.IsNullOrWhiteSpace(ip)) == true
                ? scene.SpeakerIps
                : settings.Speakers?.Select(speaker => speaker.IpAddress) ?? Enumerable.Empty<string>();

        return ips.Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
