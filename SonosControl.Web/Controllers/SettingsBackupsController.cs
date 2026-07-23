using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/settings/backups")]
[Authorize(Roles = "admin,superadmin")]
public sealed class SettingsBackupsController : ControllerBase
{
    private readonly ISettingsBackupService _backups;
    private readonly ILogger<SettingsBackupsController> _logger;

    public SettingsBackupsController(
        ISettingsBackupService backups,
        ILogger<SettingsBackupsController> logger)
    {
        _backups = backups;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SettingsBackupInfo>>> List(CancellationToken cancellationToken)
        => Ok(await _backups.ListAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<SettingsBackupInfo>> Create(CancellationToken cancellationToken)
    {
        var created = await _backups.CreateAsync(cancellationToken);
        return created is null ? NotFound("config.json was not found.") : Ok(created);
    }

    [HttpGet("{fileName}")]
    public async Task<IActionResult> Download(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _backups.OpenReadAsync(fileName, cancellationToken);
            return result is null
                ? NotFound()
                : File(result.Value.Stream, "application/json", result.Value.FileName);
        }
        catch (SettingsBackupException ex)
        {
            return Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }

    [HttpPost("{fileName}/restore")]
    public async Task<IActionResult> Restore(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _backups.RestoreAsync(fileName, cancellationToken);
            return Ok(new { restored = result.FileName, safetyBackup = result.SafetyBackup });
        }
        catch (SettingsBackupException ex)
        {
            _logger.LogWarning(ex, "Settings restore rejected for {FileName}.", fileName);
            return Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }

    [HttpPost("import")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Import([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return BadRequest("A non-empty JSON file is required.");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _backups.ImportAsync(file.FileName, stream, file.Length, cancellationToken);
            return Ok(new { imported = result.FileName, safetyBackup = result.SafetyBackup });
        }
        catch (SettingsBackupException ex)
        {
            _logger.LogWarning(ex, "Settings import rejected for {FileName}.", file.FileName);
            return Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }

    [HttpDelete("{fileName}")]
    public async Task<IActionResult> Delete(string fileName, CancellationToken cancellationToken)
    {
        try
        {
            return await _backups.DeleteAsync(fileName, cancellationToken) ? NoContent() : NotFound();
        }
        catch (SettingsBackupException ex)
        {
            return Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }
}
