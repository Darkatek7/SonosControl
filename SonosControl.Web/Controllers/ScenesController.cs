using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/scenes")]
[Authorize(Roles = "admin,operator,superadmin")]
public sealed class ScenesController : ControllerBase
{
    private readonly ISettingsRepo _settingsRepo;
    private readonly SettingsEditingService _editing;
    private readonly ISceneOrchestrationService _sceneOrchestrationService;
    private readonly ActionLogger _actionLogger;

    public ScenesController(
        ISettingsRepo settingsRepo, SettingsEditingService editing,
        ISceneOrchestrationService sceneOrchestrationService,
        ActionLogger actionLogger)
    {
        _settingsRepo = settingsRepo;
        _editing = editing;
        _sceneOrchestrationService = sceneOrchestrationService;
        _actionLogger = actionLogger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Scene>>> GetAll()
    {
        var settings = await _settingsRepo.GetSettings();
        settings ??= new SonosSettings();
        settings.Scenes ??= new();

        return Ok(settings.Scenes.OrderBy(s => s.Name).ThenBy(s => s.Id).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Scene>> GetById(string id)
    {
        var settings = await _settingsRepo.GetSettings();
        var scene = settings?.Scenes?.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (scene is null)
        {
            return NotFound();
        }

        return Ok(scene);
    }

    [HttpPost]
    [Authorize(Roles = "admin,operator,superadmin")]
    public async Task<ActionResult<Scene>> Create([FromBody] Scene scene)
    {
        if (scene is null) return BadRequest("Scene payload is required.");
        try
        {
            scene.Id = string.IsNullOrWhiteSpace(scene.Id) ? Guid.NewGuid().ToString("N") : scene.Id.Trim();
            var saved = await _editing.SaveSceneAsync(scene, create: true);
            var result = saved.Scenes.Single(item => item.Id == scene.Id);
            await _actionLogger.LogAsync("SceneCreated", $"{result.Name} ({result.Id})");
            return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
        }
        catch (SettingsEditException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "admin,operator,superadmin")]
    public async Task<ActionResult<Scene>> Update(string id, [FromBody] Scene scene)
    {
        if (scene is null) return BadRequest("Scene payload is required.");
        try
        {
            scene.Id = id;
            var saved = await _editing.SaveSceneAsync(scene, create: false);
            var result = saved.Scenes.Single(item => item.Id == id);
            await _actionLogger.LogAsync("SceneUpdated", $"{result.Name} ({result.Id})");
            return Ok(result);
        }
        catch (SettingsEditException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "admin,operator,superadmin")]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            await _editing.DeleteSceneAsync(id);
            await _actionLogger.LogAsync("SceneDeleted", id);
            return NoContent();
        }
        catch (SettingsEditException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }
    [HttpPost("{id}/apply")]
    public async Task<ActionResult<SceneApplyResult>> Apply(string id, CancellationToken cancellationToken)
    {
        var user = User.Identity?.Name;
        var result = await _sceneOrchestrationService.ApplySceneByIdAsync(id, user, cancellationToken);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
