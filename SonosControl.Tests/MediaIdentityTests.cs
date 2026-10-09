using SonosControl.DAL.Models;
using Xunit;

namespace SonosControl.Tests;

public class MediaIdentityTests
{
    [Theory]
    [InlineData("https://youtube.com/watch?v=AAA", "https://youtube.com/watch?v=BBB", false)]
    [InlineData("https://music.youtube.com/playlist?list=AAA", "https://music.youtube.com/playlist?list=BBB", false)]
    [InlineData("https://youtu.be/AAA", "https://www.youtube.com/watch?v=AAA", true)]
    [InlineData("https://youtube.com/watch?v=AAA&list=BBB", "https://youtube.com/watch?v=AAA&list=CCC", false)]
    [InlineData("x-rincon-mp3radio://EXAMPLE.com/live", "https://example.com/live/", true)]
    [InlineData("https://example.com/live?id=1", "https://example.com/live?id=2", false)]
    [InlineData("https://example.com/live", "https://example.com/live-longer", false)]
    [InlineData("https://example.com/AAA", "https://example.com/aaa", false)]
    [InlineData("https://example.com:8080/live", "https://example.com/live", false)]
    public void Matches_UsesContentIdentity(string first, string second, bool expected)
        => Assert.Equal(expected, MediaIdentity.Matches(first, second));
}
