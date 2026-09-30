using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Shared;
using Xunit;

namespace SonosControl.Tests;

public sealed class Mp3UploadPlayerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sonos-mp3-ui-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UploadAndPlay_UsesSelectedSpeaker_AndDeletesUploadWhenPlaybackFails(bool failPlayback)
    {
        using var ctx = new TestContext();
        var uploads = Mp3UploadServiceTests.CreateService(_root);
        ctx.Services.AddSingleton(uploads);
        var connector = new Mock<ISonosConnectorRepo>(MockBehavior.Strict);
        connector.Setup(c => c.PlayAudioFileAsync("10.0.0.2", It.IsAny<string>(), "song", It.IsAny<CancellationToken>()))
            .Returns(failPlayback ? Task.FromException(new HttpRequestException("offline")) : Task.CompletedTask);
        var uow = new Mock<IUnitOfWork>();
        uow.SetupGet(u => u.ISonosConnectorRepo).Returns(connector.Object);
        ctx.Services.AddSingleton(uow.Object);
        var cut = ctx.RenderComponent<Mp3UploadPlayer>(p => p
            .Add(c => c.Speakers, new[]
            {
                new SonosSpeaker { Name = "Kitchen", IpAddress = "10.0.0.1" },
                new SonosSpeaker { Name = "Office", IpAddress = "10.0.0.2" }
            })
            .Add(c => c.ActiveSpeakerIp, "10.0.0.1"));
        Assert.True(cut.Find("button").HasAttribute("disabled"));
        cut.Find("#mp3-speaker").Change("10.0.0.2");
        cut.Find("#mp3-retention").Change("15");
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Mp3UploadServiceTests.Mp3Bytes, "song.mp3", contentType: "audio/mpeg"));
        cut.Find("button").Click();
        cut.WaitForAssertion(() =>
        {
            connector.Verify(c => c.PlayAudioFileAsync("10.0.0.2", It.Is<string>(url => url.Contains("/api/mp3-audio/")), "song", It.IsAny<CancellationToken>()), Times.Once);
            Assert.Contains(failPlayback ? "Upload or playback failed" : "Automatically deleted after 15 minutes", cut.Find("[role='status']").TextContent);
            Assert.Equal(failPlayback ? 0 : 1, Directory.GetFiles(_root, "*.mp3", SearchOption.AllDirectories).Length);
        });
    }

    [Fact]
    public void Upload_RejectsWrongExtension_AndHasAccessibleControls()
    {
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(Mp3UploadServiceTests.CreateService(_root));
        ctx.Services.AddSingleton(Mock.Of<IUnitOfWork>());
        var cut = ctx.RenderComponent<Mp3UploadPlayer>();
        foreach (var id in new[] { "mp3-file", "mp3-speaker", "mp3-retention" })
        {
            Assert.NotNull(cut.Find($"label[for='{id}']"));
        }
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("wrong", "song.txt"));
        Assert.Contains("Choose a non-empty MP3", cut.Find("[role='status']").TextContent);
        Assert.True(cut.Find("button").HasAttribute("disabled"));
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
