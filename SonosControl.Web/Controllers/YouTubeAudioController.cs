using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Interfaces;

namespace SonosControl.Web.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/youtube-audio")]
public sealed class YouTubeAudioController : ControllerBase
{
    private readonly IYouTubePlaybackService _playbackService;

    public YouTubeAudioController(IYouTubePlaybackService playbackService)
    {
        _playbackService = playbackService;
    }

    [HttpGet("{sessionId}")]
    public async Task<IActionResult> GetAudioAsync(string sessionId, CancellationToken cancellationToken)
    {
        var result = await _playbackService.OpenPlaybackAsync(sessionId, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        HttpContext.Response.OnCompleted(() => result.DisposeAsync().AsTask());

        if (!string.IsNullOrWhiteSpace(result.FilePath))
        {
            return PhysicalFile(result.FilePath, result.ContentType, enableRangeProcessing: true);
        }

        if (result.Stream is null)
        {
            await result.DisposeAsync();
            return NotFound();
        }

        return File(result.Stream, result.ContentType);
    }
}
