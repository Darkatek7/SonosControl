using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, DateTime> IdempotencyKeys = new(StringComparer.OrdinalIgnoreCase);

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
            || !string.Equals(providedKey.ToString(), configuredApiKey, StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        CleanupExpiredIdempotencyKeys();

        if (Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey)
            && !string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (!IdempotencyKeys.TryAdd(idempotencyKey.ToString(), DateTime.UtcNow))
            {
                return Ok(new
                {
                    duplicate = true,
                    message = "Request already processed."
                });
            }
        }

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
            _logger.LogWarning(ex, "Webhook transport action failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = ex.Message });
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
            _logger.LogWarning(ex, "Webhook play-source action failed.");
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = ex.Message });
        }
    }

    private static void CleanupExpiredIdempotencyKeys()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var entry in IdempotencyKeys)
        {
            if (entry.Value < cutoff)
            {
                IdempotencyKeys.TryRemove(entry.Key, out _);
            }
        }
    }

    public sealed class WebhookActionRequest
    {
        public string Action { get; set; } = string.Empty;
        public string? SceneId { get; set; }
        public string? SpeakerIp { get; set; }
        public string? SourceType { get; set; }
        public string? SourceUrl { get; set; }
    }
}
