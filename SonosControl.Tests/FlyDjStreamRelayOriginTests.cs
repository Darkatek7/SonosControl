using Microsoft.AspNetCore.Http;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public class FlyDjStreamRelayOriginTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("http://sonos.local:8080", null, true)]
    [InlineData("http://evil.example", null, false)]
    [InlineData("not a url", null, false)]
    [InlineData("https://music.example.com", "music.example.com", true)]
    [InlineData("https://evil.example", "music.example.com", false)]
    public void IsSameOrigin_AcceptsOnlyTheAppsOwnHost(string? origin, string? forwardedHost, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("sonos.local:8080");
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (forwardedHost is not null) context.Request.Headers["X-Forwarded-Host"] = forwardedHost;

        Assert.Equal(expected, FlyDjStreamRelay.IsSameOrigin(context.Request));
    }
}
