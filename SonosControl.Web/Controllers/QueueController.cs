using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/queue")]
[Authorize(Roles = "admin,operator,superadmin")]
public sealed class QueueController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly ActionLogger _actionLogger;

    public QueueController(IUnitOfWork uow, ActionLogger actionLogger)
    {
        _uow = uow;
        _actionLogger = actionLogger;
    }

    [HttpGet("{speakerIp}")]
    public async Task<ActionResult<SonosQueuePage>> GetQueue(string speakerIp, [FromQuery] int startIndex = 0, [FromQuery] int count = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(speakerIp))
        {
            return BadRequest("speakerIp is required.");
        }

        var page = await _uow.ISonosConnectorRepo.GetQueue(speakerIp, Math.Max(0, startIndex), Math.Clamp(count, 1, 500), cancellationToken);
        return Ok(page);
    }

    [HttpPost("{speakerIp}/remove/{index:int}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<IActionResult> RemoveQueueItem(string speakerIp, int index, CancellationToken cancellationToken)
    {
        if (index < 0)
        {
            return BadRequest("Queue index must be >= 0.");
        }

        var queueItems = await LoadAllQueueItemsAsync(speakerIp, cancellationToken);
        if (index >= queueItems.Count)
        {
            return NotFound($"Queue index {index} was not found.");
        }

        queueItems.RemoveAt(index);
        await RebuildQueueAsync(speakerIp, queueItems, cancellationToken);

        await _actionLogger.LogAsync("QueueModified", $"Removed item {index} from queue on {speakerIp}.");
        return Ok(new { success = true, remaining = queueItems.Count });
    }

    [HttpPost("{speakerIp}/move")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<IActionResult> MoveQueueItem(string speakerIp, [FromBody] QueueMoveRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("Move payload is required.");
        }

        if (request.FromIndex < 0 || request.ToIndex < 0)
        {
            return BadRequest("Queue indexes must be >= 0.");
        }

        var queueItems = await LoadAllQueueItemsAsync(speakerIp, cancellationToken);
        if (request.FromIndex >= queueItems.Count || request.ToIndex >= queueItems.Count)
        {
            return NotFound("Queue indexes are out of range.");
        }

        var movedItem = queueItems[request.FromIndex];
        queueItems.RemoveAt(request.FromIndex);
        queueItems.Insert(request.ToIndex, movedItem);

        await RebuildQueueAsync(speakerIp, queueItems, cancellationToken);
        await _actionLogger.LogAsync("QueueModified", $"Moved queue item from {request.FromIndex} to {request.ToIndex} on {speakerIp}.");

        return Ok(new { success = true });
    }

    [HttpGet("snapshots")]
    public async Task<ActionResult<IReadOnlyList<QueueSnapshot>>> GetSnapshots()
    {
        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.QueueSnapshots ??= new();
        return Ok(settings.QueueSnapshots.OrderByDescending(s => s.CreatedUtc).ToList());
    }

    [HttpPost("snapshots")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<ActionResult<QueueSnapshot>> SaveSnapshot([FromBody] SaveQueueSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SpeakerIp))
        {
            return BadRequest("speakerIp is required.");
        }

        var queueItems = await LoadAllQueueItemsAsync(request.SpeakerIp, cancellationToken);
        var snapshot = new QueueSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"Queue {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}" : request.Name.Trim(),
            SpeakerIp = request.SpeakerIp.Trim(),
            CreatedUtc = DateTime.UtcNow,
            Items = queueItems.Select(item => new QueueSnapshotItem
            {
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                ResourceUri = item.ResourceUri
            }).ToList()
        };

        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.QueueSnapshots ??= new();
        settings.QueueSnapshots.Add(snapshot);
        settings.QueueSnapshots = settings.QueueSnapshots
            .OrderByDescending(s => s.CreatedUtc)
            .Take(40)
            .ToList();

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("QueueSnapshotSaved", $"{snapshot.Name} ({snapshot.Id}) from {snapshot.SpeakerIp}");
        return Ok(snapshot);
    }

    [HttpPost("snapshots/{id}/restore")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<IActionResult> RestoreSnapshot(string id, [FromBody] RestoreQueueSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SpeakerIp))
        {
            return BadRequest("speakerIp is required.");
        }

        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.QueueSnapshots ??= new();

        var snapshot = settings.QueueSnapshots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            return NotFound("Snapshot not found.");
        }

        var items = snapshot.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.ResourceUri))
            .Select((item, index) => new SonosQueueItem(index, item.Title, item.Artist, item.Album, item.ResourceUri))
            .ToList();
        if (!items.Any())
        {
            return BadRequest("Snapshot has no restorable queue URIs.");
        }

        await RebuildQueueAsync(request.SpeakerIp.Trim(), items, cancellationToken);
        await _actionLogger.LogAsync("QueueSnapshotRestored", $"{snapshot.Name} ({snapshot.Id}) to {request.SpeakerIp}");

        return Ok(new { success = true, restoredItems = items.Count });
    }

    [HttpDelete("snapshots/{id}")]
    [Authorize(Roles = "admin,superadmin")]
    public async Task<IActionResult> DeleteSnapshot(string id)
    {
        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.QueueSnapshots ??= new();

        var snapshot = settings.QueueSnapshots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            return NotFound();
        }

        settings.QueueSnapshots.Remove(snapshot);
        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("QueueSnapshotDeleted", $"{snapshot.Name} ({snapshot.Id})");
        return NoContent();
    }

    private async Task<List<SonosQueueItem>> LoadAllQueueItemsAsync(string speakerIp, CancellationToken cancellationToken)
    {
        var allItems = new List<SonosQueueItem>();
        const int pageSize = 100;
        var start = 0;

        while (true)
        {
            var page = await _uow.ISonosConnectorRepo.GetQueue(speakerIp, start, pageSize, cancellationToken);
            if (page.Items.Count == 0)
            {
                break;
            }

            allItems.AddRange(page.Items);
            start = page.StartIndex + page.NumberReturned;
            if (!page.HasMore || page.NumberReturned <= 0)
            {
                break;
            }
        }

        return allItems;
    }

    private async Task RebuildQueueAsync(string speakerIp, IReadOnlyCollection<SonosQueueItem> items, CancellationToken cancellationToken)
    {
        await _uow.ISonosConnectorRepo.ClearQueue(speakerIp, cancellationToken);

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ResourceUri))
            {
                continue;
            }

            await _uow.ISonosConnectorRepo.AddUriToQueue(speakerIp, item.ResourceUri, metadata: null, enqueueAsNext: false, cancellationToken);
        }
    }

    public sealed class QueueMoveRequest
    {
        public int FromIndex { get; set; }
        public int ToIndex { get; set; }
    }

    public sealed class SaveQueueSnapshotRequest
    {
        public string SpeakerIp { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    public sealed class RestoreQueueSnapshotRequest
    {
        public string SpeakerIp { get; set; } = string.Empty;
    }
}
