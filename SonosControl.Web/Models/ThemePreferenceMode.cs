namespace SonosControl.Web.Models;

public enum ThemePreferenceMode
{
    System,
    Light,
    Dark
}

public static class ThemePreferenceModeExtensions
{
    public static string ToIdentifier(this ThemePreferenceMode mode)
        => mode switch
        {
            ThemePreferenceMode.Light => "light",
            ThemePreferenceMode.Dark => "dark",
            _ => "system",
        };

    public static ThemePreferenceMode FromIdentifier(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemePreferenceMode.Light,
            "dark" => ThemePreferenceMode.Dark,
            _ => ThemePreferenceMode.System,
        };
    }
}
