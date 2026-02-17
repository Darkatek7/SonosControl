using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/jukebox")]
[Authorize(Roles = "admin,operator,superadmin")]
public sealed class JukeboxController : ControllerBase
{
    private readonly ICollaborativeJukeboxService _jukeboxService;

    public JukeboxController(ICollaborativeJukeboxService jukeboxService)
    {
        _jukeboxService = jukeboxService;
    }

    [HttpGet]
    public async Task<ActionResult<JukeboxState>> GetState(CancellationToken cancellationToken)
    {
        var state = await _jukeboxService.GetStateAsync(cancellationToken);
        return Ok(state);
    }

    [HttpPost("suggestions")]
    public async Task<ActionResult<JukeboxOperationResult>> Suggest(
        [FromBody] SuggestionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Suggestion payload is required.");
        }

        var result = await _jukeboxService.SuggestAsync(
            request.ResourceUri,
            request.Title,
            request.Artist,
            User.Identity?.Name,
            cancellationToken);

        return result.Success ? Ok(result) : ToError(result);
    }

    [HttpPost("suggestions/{id}/vote")]
    public async Task<ActionResult<JukeboxOperationResult>> Vote(string id, CancellationToken cancellationToken)
    {
        var result = await _jukeboxService.VoteAsync(id, User.Identity?.Name, cancellationToken);
        return result.Success ? Ok(result) : ToError(result);
    }

    [HttpDelete("suggestions/{id}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<JukeboxOperationResult>> RemoveSuggestion(string id, CancellationToken cancellationToken)
    {
        var result = await _jukeboxService.RemoveSuggestionAsync(id, User.Identity?.Name, cancellationToken);
        return result.Success ? Ok(result) : ToError(result);
    }

    [HttpPost("play-next")]
    public async Task<ActionResult<JukeboxOperationResult>> PlayNext(
        [FromBody] PlayWinnerRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _jukeboxService.PlayTopSuggestionAsync(request?.SpeakerIp, User.Identity?.Name, cancellationToken);
        return result.Success ? Ok(result) : ToError(result);
    }

    [HttpPut("settings")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<JukeboxOperationResult>> UpdateSettings(
        [FromBody] JukeboxSettings updatedSettings,
        CancellationToken cancellationToken)
    {
        if (updatedSettings is null)
        {
            return BadRequest("Jukebox settings payload is required.");
        }

        var result = await _jukeboxService.UpdateSettingsAsync(updatedSettings, User.Identity?.Name, cancellationToken);
        return result.Success ? Ok(result) : ToError(result);
    }

    private ActionResult<JukeboxOperationResult> ToError(JukeboxOperationResult result)
    {
        if (result.Message.Contains("limit reached", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, result);
        }

        if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(result);
        }

        return BadRequest(result);
    }

    public sealed class SuggestionRequest
    {
        public string ResourceUri { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Artist { get; set; }
    }

    public sealed class PlayWinnerRequest
    {
        public string? SpeakerIp { get; set; }
    }
}
