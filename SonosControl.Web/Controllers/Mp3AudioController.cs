using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[AllowAnonymous] // Sonos fetches audio without the user's authentication cookie.
[Route("api/mp3-audio")]
public sealed class Mp3AudioController(Mp3UploadService uploads) : ControllerBase
{
    [HttpGet("{id}.mp3")]
    [HttpHead("{id}.mp3")]
    public IActionResult GetAudio(string id)
    {
        var stream = uploads.OpenRead(id);
        if (stream is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(stream, "audio/mpeg", enableRangeProcessing: true);
    }
}
