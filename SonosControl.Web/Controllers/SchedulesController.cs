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

    public SchedulesController(
        ISettingsRepo settingsRepo, SettingsEditingService editing,
        ActionLogger actionLogger,
        ConfiguredTimeZoneService appTimeZone)
    {
        _settingsRepo = settingsRepo;
        _editing = editing;
        _actionLogger = actionLogger;
        _appTimeZone = appTimeZone;
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

    [HttpPost]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<ScheduleWindow>> Create([FromBody] ScheduleWindow window)
    {
        if (window is null) return BadRequest("ScheduleWindow payload is required.");
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
