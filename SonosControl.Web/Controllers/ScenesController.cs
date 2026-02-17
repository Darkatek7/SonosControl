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
    private readonly IUnitOfWork _uow;
    private readonly ISceneOrchestrationService _sceneOrchestrationService;
    private readonly ActionLogger _actionLogger;

    public ScenesController(
        IUnitOfWork uow,
        ISceneOrchestrationService sceneOrchestrationService,
        ActionLogger actionLogger)
    {
        _uow = uow;
        _sceneOrchestrationService = sceneOrchestrationService;
        _actionLogger = actionLogger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Scene>>> GetAll()
    {
        var settings = await _uow.ISettingsRepo.GetSettings();
        settings ??= new SonosSettings();
        settings.Scenes ??= new();

        return Ok(settings.Scenes.OrderBy(s => s.Name).ThenBy(s => s.Id).ToList());
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Scene>> GetById(string id)
    {
        var settings = await _uow.ISettingsRepo.GetSettings();
        var scene = settings?.Scenes?.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (scene is null)
        {
            return NotFound();
        }

        return Ok(scene);
    }

    [HttpPost]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<Scene>> Create([FromBody] Scene scene)
    {
        if (scene is null)
        {
            return BadRequest("Scene payload is required.");
        }

        if (string.IsNullOrWhiteSpace(scene.Name))
        {
            return BadRequest("Scene name is required.");
        }

        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.Scenes ??= new();

        if (settings.Scenes.Any(s => string.Equals(s.Name, scene.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return Conflict($"A scene named '{scene.Name}' already exists.");
        }

        scene.Id = string.IsNullOrWhiteSpace(scene.Id) ? Guid.NewGuid().ToString("N") : scene.Id.Trim();
        scene.LastModifiedUtc = DateTime.UtcNow;
        settings.Scenes.Add(scene);

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("SceneCreated", $"{scene.Name} ({scene.Id})");

        return CreatedAtAction(nameof(GetById), new { id = scene.Id }, scene);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<Scene>> Update(string id, [FromBody] Scene scene)
    {
        if (scene is null)
        {
            return BadRequest("Scene payload is required.");
        }

        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.Scenes ??= new();

        var existing = settings.Scenes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return NotFound();
        }

        var duplicateNameExists = settings.Scenes.Any(s =>
            !string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Name, scene.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicateNameExists)
        {
            return Conflict($"A scene named '{scene.Name}' already exists.");
        }

        existing.Name = scene.Name;
        existing.Description = scene.Description;
        existing.Enabled = scene.Enabled;
        existing.SourceType = scene.SourceType;
        existing.SourceUrl = scene.SourceUrl;
        existing.IsSyncedPlayback = scene.IsSyncedPlayback;
        existing.MasterSpeakerIp = scene.MasterSpeakerIp;
        existing.TimerMinutes = scene.TimerMinutes;
        existing.SpeakerIps = scene.SpeakerIps ?? new();
        existing.Actions = scene.Actions ?? new();
        existing.LastModifiedUtc = DateTime.UtcNow;

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("SceneUpdated", $"{existing.Name} ({existing.Id})");

        return Ok(existing);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<IActionResult> Delete(string id)
    {
        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.Scenes ??= new();

        var scene = settings.Scenes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (scene is null)
        {
            return NotFound();
        }

        settings.Scenes.Remove(scene);

        settings.ScheduleWindows ??= new();
        foreach (var window in settings.ScheduleWindows.Where(w => string.Equals(w.SceneId, id, StringComparison.OrdinalIgnoreCase)))
        {
            window.SceneId = null;
            window.LastModifiedUtc = DateTime.UtcNow;
        }

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("SceneDeleted", $"{scene.Name} ({scene.Id})");

        return NoContent();
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
