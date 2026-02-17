using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/devices")]
[Authorize(Roles = "admin,operator,superadmin")]
public sealed class DevicesController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly ISonosDeviceDiscoveryService _discoveryService;
    private readonly IDeviceHealthSnapshotStore _healthSnapshotStore;
    private readonly ActionLogger _actionLogger;

    public DevicesController(
        IUnitOfWork uow,
        ISonosDeviceDiscoveryService discoveryService,
        IDeviceHealthSnapshotStore healthSnapshotStore,
        ActionLogger actionLogger)
    {
        _uow = uow;
        _discoveryService = discoveryService;
        _healthSnapshotStore = healthSnapshotStore;
        _actionLogger = actionLogger;
    }

    [HttpGet("discovery")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<IReadOnlyList<SonosSpeaker>>> Discover([FromQuery] int timeoutSeconds = 4, CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 20));
        var discovered = await _discoveryService.DiscoverAsync(timeout, cancellationToken);
        return Ok(discovered);
    }

    [HttpPost("discovery/import")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<IActionResult> DiscoverAndImport([FromQuery] int timeoutSeconds = 4, CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 20));
        var discovered = await _discoveryService.DiscoverAsync(timeout, cancellationToken);

        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.Speakers ??= new();

        var existingIps = settings.Speakers.Select(s => s.IpAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = 0;

        foreach (var speaker in discovered)
        {
            if (string.IsNullOrWhiteSpace(speaker.IpAddress) || existingIps.Contains(speaker.IpAddress))
            {
                continue;
            }

            settings.Speakers.Add(new SonosSpeaker
            {
                Name = speaker.Name,
                IpAddress = speaker.IpAddress,
                Uuid = speaker.Uuid
            });
            existingIps.Add(speaker.IpAddress);
            imported++;
        }

        if (imported > 0)
        {
            await _uow.ISettingsRepo.WriteSettings(settings);
        }

        await _actionLogger.LogAsync("SpeakersDiscovered", $"Discovered {discovered.Count} speakers, imported {imported}.");
        return Ok(new
        {
            discovered = discovered.Count,
            imported
        });
    }

    [HttpGet("health")]
    public ActionResult<IReadOnlyList<DeviceHealthStatus>> Health()
    {
        var statuses = _healthSnapshotStore.GetSnapshot()
            .OrderBy(s => s.SpeakerName)
            .ThenBy(s => s.SpeakerIp)
            .ToList();
        return Ok(statuses);
    }
}
