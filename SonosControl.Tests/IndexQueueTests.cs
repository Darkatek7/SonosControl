using System;
using Xunit;

namespace SonosControl.Tests;

public static class QueueItemFormatter
{
    public static string FormatTitle(string title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Unknown track";
        }

        if (string.IsNullOrWhiteSpace(artist))
        {
            return title.Trim();
        }

        var trimmedTitle = title.Trim();
        var trimmedArtist = artist.Trim();

        if (trimmedTitle.Contains(trimmedArtist, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedTitle;
        }

        return $"{trimmedArtist} - {trimmedTitle}";
    }
}

public class QueueItemFormatterTests
{
    [Theory]
    [InlineData("Skyline", "Neon Dreams", "Neon Dreams - Skyline")]
    [InlineData("Morning Briefing", null, "Morning Briefing")]
    [InlineData("Madison Beer - lovergirl (Official Music Video)", "Madison Beer", "Madison Beer - lovergirl (Official Music Video)")]
    [InlineData("", null, "Unknown track")]
    public void FormatTitle_CombinesArtistAndTitle_WithoutDuplicating(string title, string? artist, string expected)
    {
        Assert.Equal(expected, QueueItemFormatter.FormatTitle(title, artist));
    }
}
