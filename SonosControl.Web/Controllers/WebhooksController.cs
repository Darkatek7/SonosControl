using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;

namespace SonosControl.Web.Controllers;

[ApiController]
[Route("api/webhooks")]
public sealed class WebhooksController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, IdempotencyEntry> IdempotencyKeys = new(StringComparer.OrdinalIgnoreCase);

    private readonly IConfiguration _configuration;
    private readonly IUnitOfWork _uow;
    private readonly ISceneOrchestrationService _sceneOrchestrationService;
    private readonly ActionLogger _actionLogger;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IConfiguration configuration,
        IUnitOfWork uow,
        ISceneOrchestrationService sceneOrchestrationService,
        ActionLogger actionLogger,
        ILogger<WebhooksController> logger)
    {
        _configuration = configuration;
        _uow = uow;
        _sceneOrchestrationService = sceneOrchestrationService;
        _actionLogger = actionLogger;
        _logger = logger;
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Execute([FromBody] WebhookActionRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Action))
        {
            return BadRequest("Action is required.");
        }

        var configuredApiKey = _configuration["Webhook:ApiKey"];
        if (string.IsNullOrWhiteSpace(configuredApiKey))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Webhook API key is not configured.");
        }

        if (!Request.Headers.TryGetValue("X-API-Key", out var providedKey)
            || !ApiKeysMatch(providedKey.ToString(), configuredApiKey))
        {
            return Unauthorized();
        }

        CleanupExpiredIdempotencyKeys();

        string? reservedIdempotencyKey = null;
        if (Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey)
            && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var key = idempotencyKey.ToString().Trim();
            if (key.Length > 200)
            {
                return BadRequest("Idempotency-Key may not exceed 200 characters.");
            }

            var reservation = ReserveIdempotencyKey(key);
            if (reservation == IdempotencyReservation.Completed)
            {
                return Ok(new
                {
                    duplicate = true,
                    message = "Request already processed."
                });
            }

            if (reservation == IdempotencyReservation.InProgress)
            {
                Response.Headers.RetryAfter = "2";
                return Conflict(new
                {
                    duplicate = true,
                    inProgress = true,
                    message = "An identical request is still being processed."
                });
            }

            reservedIdempotencyKey = key;
        }

        try
        {
            var result = await ExecuteActionAsync(request, cancellationToken);
            if (reservedIdempotencyKey is not null)
            {
                if (IsSuccessful(result))
                {
                    IdempotencyKeys[reservedIdempotencyKey] = new IdempotencyEntry(IdempotencyState.Completed, DateTime.UtcNow);
                }
                else
                {
                    IdempotencyKeys.TryRemove(reservedIdempotencyKey, out _);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            if (reservedIdempotencyKey is not null)
            {
                IdempotencyKeys.TryRemove(reservedIdempotencyKey, out _);
            }

            _logger.LogError(ex, "Unhandled webhook action failure for {Action}.", request.Action);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                error = "The webhook action could not be completed."
            });
        }
    }

    private async Task<IActionResult> ExecuteActionAsync(WebhookActionRequest request, CancellationToken cancellationToken)
    {
        var action = request.Action.Trim().ToLowerInvariant();
        switch (action)
        {
            case "apply-scene":
                if (string.IsNullOrWhiteSpace(request.SceneId))
                {
                    return BadRequest("sceneId is required for apply-scene.");
                }

                var sceneResult = await _sceneOrchestrationService.ApplySceneByIdAsync(request.SceneId, "webhook", cancellationToken);
                if (!sceneResult.Success)
                {
                    return BadRequest(sceneResult);
                }

                await _actionLogger.LogAsync("WebhookAction", $"apply-scene:{request.SceneId}");
                return Ok(sceneResult);
            case "play":
            case "pause":
            case "next":
                return await ExecuteTransportActionAsync(action, request.SpeakerIp, cancellationToken);
            case "play-source":
                return await ExecutePlaySourceAsync(request, cancellationToken);
            default:
                return BadRequest($"Unsupported action '{request.Action}'.");
        }
    }

    private async Task<IActionResult> ExecuteTransportActionAsync(string action, string? speakerIp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(speakerIp))
        {
            return BadRequest("speakerIp is required for transport actions.");
        }

        try
        {
            switch (action)
            {
                case "play":
                    await _uow.ISonosConnectorRepo.StartPlaying(speakerIp);
                    break;
                case "pause":
                    await _uow.ISonosConnectorRepo.PausePlaying(speakerIp);
                    break;
                case "next":
                    await _uow.ISonosConnectorRepo.NextTrack(speakerIp, cancellationToken);
                    break;
            }

            await _actionLogger.LogAsync("WebhookAction", $"{action}:{speakerIp}");
            return Ok(new { success = true, action, speakerIp });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook transport action {Action} failed for speaker {SpeakerIp}.", action, speakerIp);
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "The speaker did not complete the transport action." });
        }
    }

    private async Task<IActionResult> ExecutePlaySourceAsync(WebhookActionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SpeakerIp))
        {
            return BadRequest("speakerIp is required for play-source.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            return BadRequest("sourceUrl is required for play-source.");
        }

        var sourceType = request.SourceType?.Trim().ToLowerInvariant();
        if (sourceType is not ("station" or "spotify" or "youtube"))
        {
            return BadRequest("sourceType must be station, spotify, or youtube.");
        }

        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();

        try
        {
            switch (sourceType)
            {
                case "station":
                    await _uow.ISonosConnectorRepo.SetTuneInStationAsync(request.SpeakerIp, request.SourceUrl, cancellationToken);
                    break;
                case "spotify":
                    await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(request.SpeakerIp, request.SourceUrl, settings.AutoPlayStationUrl, cancellationToken);
                    break;
                case "youtube":
                    await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(request.SpeakerIp, request.SourceUrl, settings.AutoPlayStationUrl, cancellationToken);
                    break;
            }

            await _actionLogger.LogAsync("WebhookAction", $"play-source:{sourceType}:{request.SpeakerIp}");
            return Ok(new { success = true, sourceType, request.SpeakerIp, request.SourceUrl });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook play-source action failed for speaker {SpeakerIp} and source type {SourceType}.", request.SpeakerIp, sourceType);
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "The speaker did not start the requested source." });
        }
    }

    private static bool ApiKeysMatch(string provided, string configured)
    {
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        return CryptographicOperations.FixedTimeEquals(providedHash, configuredHash);
    }

    private static IdempotencyReservation ReserveIdempotencyKey(string key)
    {
        while (true)
        {
            var now = DateTime.UtcNow;
            if (IdempotencyKeys.TryAdd(key, new IdempotencyEntry(IdempotencyState.InProgress, now)))
            {
                return IdempotencyReservation.Reserved;
            }

            if (!IdempotencyKeys.TryGetValue(key, out var existing))
            {
                continue;
            }

            if (existing.UpdatedUtc < now.AddMinutes(-30))
            {
                IdempotencyKeys.TryRemove(new KeyValuePair<string, IdempotencyEntry>(key, existing));
                continue;
            }

            return existing.State == IdempotencyState.Completed
                ? IdempotencyReservation.Completed
                : IdempotencyReservation.InProgress;
        }
    }

    private static bool IsSuccessful(IActionResult result)
    {
        var statusCode = result switch
        {
            ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
            StatusCodeResult statusResult => statusResult.StatusCode,
            _ => StatusCodes.Status200OK
        };

        return statusCode is >= 200 and < 300;
    }

    private static void CleanupExpiredIdempotencyKeys()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var entry in IdempotencyKeys)
        {
            if (entry.Value.UpdatedUtc < cutoff)
            {
                IdempotencyKeys.TryRemove(new KeyValuePair<string, IdempotencyEntry>(entry.Key, entry.Value));
            }
        }
    }

    private enum IdempotencyState
    {
        InProgress,
        Completed
    }

    private enum IdempotencyReservation
    {
        Reserved,
        InProgress,
        Completed
    }

    private sealed record IdempotencyEntry(IdempotencyState State, DateTime UpdatedUtc);

    public sealed class WebhookActionRequest
    {
        public string Action { get; set; } = string.Empty;
        public string? SceneId { get; set; }
        public string? SpeakerIp { get; set; }
        public string? SourceType { get; set; }
        public string? SourceUrl { get; set; }
    }
}
