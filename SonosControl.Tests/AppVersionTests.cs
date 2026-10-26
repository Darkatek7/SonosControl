using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.0+0123456789abcdef", "1.2.0 (0123456)")]
    [InlineData("1.3.0-main.4+abc", "1.3.0-main.4 (abc)")]
    [InlineData("1.2.0+", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void FormatShortensTheSourceRevision(string? informationalVersion, string expected)
    {
        Assert.Equal(expected, AppVersion.Format(informationalVersion));
    }
}
