using System.Reflection;

namespace SonosControl.Web.Services;

/// <summary>Build version for display, e.g. "1.2.0 (abc1234)". Docker builds set it from the release tag.</summary>
public static class AppVersion
{
    public static string Current { get; } = Format(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "unknown";
        var separator = informationalVersion.IndexOf('+');
        if (separator < 0) return informationalVersion;

        var version = informationalVersion[..separator];
        var revision = informationalVersion[(separator + 1)..];
        return revision.Length == 0 ? version : $"{version} ({revision[..Math.Min(7, revision.Length)]})";
    }
}
