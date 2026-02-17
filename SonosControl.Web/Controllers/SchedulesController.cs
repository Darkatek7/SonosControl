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
    private readonly IUnitOfWork _uow;
    private readonly ActionLogger _actionLogger;

    public SchedulesController(IUnitOfWork uow, ActionLogger actionLogger)
    {
        _uow = uow;
        _actionLogger = actionLogger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScheduleWindow>>> GetAll()
    {
        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
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
        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.ScheduleWindows ??= new();

        var now = DateTimeOffset.Now;
        var active = ScheduleWindowEvaluator.SelectActiveWindow(settings.ScheduleWindows, now);

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
        if (window is null)
        {
            return BadRequest("Schedule payload is required.");
        }

        var validation = ValidateWindow(window);
        if (validation is not null)
        {
            return BadRequest(validation);
        }

        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.ScheduleWindows ??= new();

        window.Id = string.IsNullOrWhiteSpace(window.Id) ? Guid.NewGuid().ToString("N") : window.Id.Trim();
        window.LastModifiedUtc = DateTime.UtcNow;
        settings.ScheduleWindows.Add(window);

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("ScheduleWindowCreated", $"{window.Name} ({window.Id})");

        return CreatedAtAction(nameof(GetAll), new { id = window.Id }, window);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<ScheduleWindow>> Update(string id, [FromBody] ScheduleWindow window)
    {
        if (window is null)
        {
            return BadRequest("Schedule payload is required.");
        }

        var validation = ValidateWindow(window);
        if (validation is not null)
        {
            return BadRequest(validation);
        }

        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.ScheduleWindows ??= new();

        var existing = settings.ScheduleWindows.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return NotFound();
        }

        existing.Name = window.Name;
        existing.IsEnabled = window.IsEnabled;
        existing.Priority = window.Priority;
        existing.StartTime = window.StartTime;
        existing.StopTime = window.StopTime;
        existing.RecurrenceType = window.RecurrenceType;
        existing.DaysOfWeek = window.DaysOfWeek ?? new();
        existing.StartDate = window.StartDate;
        existing.EndDate = window.EndDate;
        existing.SceneId = window.SceneId;
        existing.FadeInSeconds = Math.Max(0, window.FadeInSeconds);
        existing.FadeOutSeconds = Math.Max(0, window.FadeOutSeconds);
        existing.LastModifiedUtc = DateTime.UtcNow;

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("ScheduleWindowUpdated", $"{existing.Name} ({existing.Id})");

        return Ok(existing);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<IActionResult> Delete(string id)
    {
        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.ScheduleWindows ??= new();

        var existing = settings.ScheduleWindows.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return NotFound();
        }

        settings.ScheduleWindows.Remove(existing);
        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("ScheduleWindowDeleted", $"{existing.Name} ({existing.Id})");

        return NoContent();
    }

    private static string? ValidateWindow(ScheduleWindow window)
    {
        if (string.IsNullOrWhiteSpace(window.Name))
        {
            return "Schedule window name is required.";
        }

        if (window.Priority < 0 || window.Priority > 1000)
        {
            return "Schedule window priority must be between 0 and 1000.";
        }

        if (window.RecurrenceType == ScheduleRecurrenceType.CustomDays
            && (window.DaysOfWeek is null || window.DaysOfWeek.Count == 0))
        {
            return "Custom day recurrence requires at least one day.";
        }

        if (window.StartDate.HasValue && window.EndDate.HasValue && window.EndDate < window.StartDate)
        {
            return "EndDate must be on or after StartDate.";
        }

        if (window.FadeInSeconds < 0 || window.FadeOutSeconds < 0)
        {
            return "Fade values cannot be negative.";
        }

        return null;
    }
}
