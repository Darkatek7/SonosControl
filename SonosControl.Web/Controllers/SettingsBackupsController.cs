using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/settings/backups")]
[Authorize(Roles = "admin,superadmin")]
public sealed class SettingsBackupsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IUnitOfWork _uow;
    private readonly ActionLogger _actionLogger;
    private readonly ILogger<SettingsBackupsController> _logger;
    private readonly string _dataDirectory;

    private string BackupDirectory => Path.Combine(_dataDirectory, "backups");

    public SettingsBackupsController(
        IUnitOfWork uow,
        ActionLogger actionLogger,
        ILogger<SettingsBackupsController> logger,
        IWebHostEnvironment environment)
    {
        _uow = uow;
        _actionLogger = actionLogger;
        _logger = logger;
        _dataDirectory = Path.Combine(environment.ContentRootPath, "Data");
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<BackupFileInfo>> List()
    {
        Directory.CreateDirectory(BackupDirectory);
        var backups = Directory.EnumerateFiles(BackupDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select(file => new BackupFileInfo(file.Name, file.CreationTimeUtc, file.Length))
            .ToList();

        return Ok(backups);
    }

    [HttpPost]
    public async Task<ActionResult<BackupFileInfo>> Create()
    {
        var fileName = await _uow.ISettingsRepo.CreateVersionedBackupAsync(
            "manual",
            HttpContext.RequestAborted);
        if (fileName is null)
        {
            return NotFound("config.json was not found.");
        }

        var targetPath = Path.Combine(BackupDirectory, fileName);
        await _actionLogger.LogAsync("ConfigBackupCreated", fileName);
        var info = new FileInfo(targetPath);
        return Ok(new BackupFileInfo(info.Name, info.CreationTimeUtc, info.Length));
    }

    [HttpGet("{fileName}")]
    public IActionResult Download(string fileName)
    {
        var sanitized = Path.GetFileName(fileName);
        var path = Path.Combine(BackupDirectory, sanitized);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        var bytes = System.IO.File.ReadAllBytes(path);
        return File(bytes, "application/json", sanitized);
    }

    [HttpPost("{fileName}/restore")]
    public async Task<IActionResult> Restore(string fileName)
    {
        var sanitized = Path.GetFileName(fileName);
        var sourcePath = Path.Combine(BackupDirectory, sanitized);
        if (!System.IO.File.Exists(sourcePath))
        {
            return NotFound();
        }

        var rawJson = await System.IO.File.ReadAllTextAsync(sourcePath);
        var importedSettings = JsonSerializer.Deserialize<SonosSettings>(rawJson, JsonOptions);
        if (importedSettings is null)
        {
            return BadRequest("Backup file could not be deserialized.");
        }

        var safetyBackup = await _uow.ISettingsRepo.CreateVersionedBackupAsync(
            "pre-restore",
            HttpContext.RequestAborted);
        await _uow.ISettingsRepo.WriteSettings(importedSettings);
        await _actionLogger.LogAsync(
            "ConfigBackupRestored",
            $"{sanitized}; safety backup: {safetyBackup ?? "not required"}");

        _logger.LogInformation("Settings restored from backup {FileName}.", sanitized);
        return Ok(new { restored = sanitized, safetyBackup });
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromForm] IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("A non-empty JSON file is required.");
        }

        if (!string.Equals(Path.GetExtension(file.FileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Only .json files are supported.");
        }

        string rawJson;
        using (var stream = file.OpenReadStream())
        using (var reader = new StreamReader(stream))
        {
            rawJson = await reader.ReadToEndAsync();
        }

        var importedSettings = JsonSerializer.Deserialize<SonosSettings>(rawJson, JsonOptions);
        if (importedSettings is null)
        {
            return BadRequest("The uploaded file is not a valid Sonos settings JSON.");
        }

        var safetyBackup = await _uow.ISettingsRepo.CreateVersionedBackupAsync(
            "pre-import",
            HttpContext.RequestAborted);
        await _uow.ISettingsRepo.WriteSettings(importedSettings);
        await _actionLogger.LogAsync("ConfigImported", $"{file.FileName}; safety backup: {safetyBackup ?? "not required"}");
        _logger.LogInformation("Settings imported from {FileName}.", file.FileName);

        return Ok(new { imported = file.FileName, safetyBackup });
    }

    public sealed record BackupFileInfo(string FileName, DateTime CreatedUtc, long Bytes);

}
