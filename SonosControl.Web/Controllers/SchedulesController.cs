using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/schedules")]
[Authorize(Roles = "admin,operator,superadmin")]
public sealed class SchedulesController : ControllerBase
{
    private readonly ISettingsRepo _settingsRepo;
    private readonly SettingsEditingService _editing;
    private readonly ActionLogger _actionLogger;
    private readonly ConfiguredTimeZoneService _appTimeZone;
    private readonly ISceneOrchestrationService _sceneOrchestrationService;

    public SchedulesController(
        ISettingsRepo settingsRepo, SettingsEditingService editing,
        ActionLogger actionLogger,
        ConfiguredTimeZoneService appTimeZone,
        ISceneOrchestrationService? sceneOrchestrationService = null)
    {
        _settingsRepo = settingsRepo;
        _editing = editing;
        _actionLogger = actionLogger;
        _appTimeZone = appTimeZone;
        _sceneOrchestrationService = sceneOrchestrationService!;
    }

    [HttpPost("{id}/apply")]
    public async Task<ActionResult<SceneApplyResult>> Apply(string id, CancellationToken cancellationToken)
    {
        var settings = await _settingsRepo.GetSettings() ?? new SonosSettings();
        var window = settings.ScheduleWindows.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (window?.Playback is null) return NotFound();
        var result = await _sceneOrchestrationService.ApplySceneAsync(window.Playback, User.Identity?.Name, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScheduleWindow>>> GetAll()
    {
        var settings = await _settingsRepo.GetSettings() ?? new SonosSettings();
        settings.ScheduleWindows ??= new();

        var windows = settings.ScheduleWindows
            .OrderBy(w => w.Priority)
            .ThenBy(w => w.Name)
            .ToList();

        return Ok(windows);
    }

    [HttpGet("active")]
    public async Task<ActionResult<ScheduleWindow>> GetActive()
    {
        var settings = await _settingsRepo.GetSettings() ?? new SonosSettings();
        settings.ScheduleWindows ??= new();

        var now = _appTimeZone.Now;
        var active = ScheduleWindowEvaluator.SelectActiveWindow(settings.ScheduleWindows, now, settings);

        if (active is null)
        {
            return NoContent();
        }

        return Ok(active);
    }

    // The legacy /active endpoint remains a single-window summary. This endpoint
    // reports the actual winning schedule for every selected room.
    [HttpGet("active/rooms")]
    public async Task<ActionResult<IReadOnlyDictionary<string, ScheduleWindow>>> GetActiveRooms()
    {
        var settings = await _settingsRepo.GetSettings() ?? new SonosSettings();
        var active = new Dictionary<string, ScheduleWindow>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in ScheduleWindowOrdering.Order(settings.ScheduleWindows.Where(window =>
            window.HasTimeSchedule && window.Playback is { Enabled: true }
            && ScheduleWindowEvaluator.IsWindowActive(window, _appTimeZone.Now, settings))))
        {
            foreach (var ip in SceneTargetResolver.Resolve(window.Playback!, settings))
                active.TryAdd(ip, window);
        }
        return Ok(active);
    }

    [HttpPost]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<ScheduleWindow>> Create([FromBody] ScheduleWindow window)
    {
        if (window is null) return BadRequest("ScheduleWindow payload is required.");
        if (!ApiIdValidator.IsValidOptionalId(window.Id)) return BadRequest(ApiIdValidator.Message);
        try
        {
            window.Id = string.IsNullOrWhiteSpace(window.Id) ? Guid.NewGuid().ToString("N") : window.Id.Trim();
            var saved = await _editing.SaveWindowAsync(window, create: true);
            var result = saved.ScheduleWindows.Single(item => item.Id == window.Id);
            await _actionLogger.LogAsync("ScheduleWindowCreated", $"{result.Name} ({result.Id})");
            return CreatedAtAction(nameof(GetAll), new { id = result.Id }, result);
        }
        catch (SettingsEditException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<ScheduleWindow>> Update(string id, [FromBody] ScheduleWindow window)
    {
        if (window is null) return BadRequest("ScheduleWindow payload is required.");
        try
        {
            window.Id = id;
            var saved = await _editing.SaveWindowAsync(window, create: false);
            var result = saved.ScheduleWindows.Single(item => item.Id == id);
            await _actionLogger.LogAsync("ScheduleWindowUpdated", $"{result.Name} ({result.Id})");
            return Ok(result);
        }
        catch (SettingsEditException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            await _editing.DeleteWindowAsync(id);
            await _actionLogger.LogAsync("ScheduleWindowDeleted", id);
            return NoContent();
        }
        catch (SettingsEditException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }
}

/// <summary>
/// Caller supplied ids end up inside persisted auto-stop rule keys (128 character column), so
/// they are limited to a short, delimiter-free alphabet.
/// </summary>
internal static class ApiIdValidator
{
    public const int MaxLength = 64;
    public const string Message = "Id may only contain letters, digits, '-', '_' and '.' and must be at most 64 characters.";

    public static bool IsValidOptionalId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return true;
        var trimmed = id.Trim();
        return trimmed.Length <= MaxLength
            && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }
}
