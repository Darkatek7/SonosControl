using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public interface ISceneOrchestrationService
{
    Task<SceneApplyResult> ApplySceneByIdAsync(string sceneId, string? performedBy = null, CancellationToken cancellationToken = default);
    Task<SceneApplyResult> ApplySceneAsync(Scene scene, string? performedBy = null, CancellationToken cancellationToken = default);
}

public sealed record SceneApplyResult(
    bool Success,
    string Message,
    string? SceneId,
    IReadOnlyList<string> TargetSpeakers,
    bool RecoveryActivated = false);

public sealed class SceneOrchestrationService : ISceneOrchestrationService
{
    private const int MaxRecoveryRetryCount = 10;
    private const int MaxRecoveryRetryDelaySeconds = 300;

    private readonly IUnitOfWork _uow;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotificationService _notificationService;
    private readonly ActionLogger _actionLogger;
    private readonly ILogger<SceneOrchestrationService> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sceneTimerCancellations = new(StringComparer.OrdinalIgnoreCase);

    public SceneOrchestrationService(
        IUnitOfWork uow,
        IServiceScopeFactory scopeFactory,
        INotificationService notificationService,
        ActionLogger actionLogger,
        ILogger<SceneOrchestrationService> logger)
    {
        _uow = uow;
        _scopeFactory = scopeFactory;
        _notificationService = notificationService;
        _actionLogger = actionLogger;
        _logger = logger;
    }

    public async Task<SceneApplyResult> ApplySceneByIdAsync(string sceneId, string? performedBy = null, CancellationToken cancellationToken = default)
    {
        return await ApplySceneByIdInternalAsync(sceneId, performedBy, allowRecovery: true, cancellationToken);
    }

    public async Task<SceneApplyResult> ApplySceneAsync(Scene scene, string? performedBy = null, CancellationToken cancellationToken = default)
    {
        return await ApplySceneInternalAsync(scene, performedBy, allowRecovery: true, cancellationToken);
    }

    private async Task<SceneApplyResult> ApplySceneByIdInternalAsync(
        string sceneId,
        string? performedBy,
        bool allowRecovery,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return new SceneApplyResult(false, "Scene id is required.", null, Array.Empty<string>());
        }

        var settings = await _uow.ISettingsRepo.GetSettings();
        if (settings is null)
        {
            return new SceneApplyResult(false, "Settings could not be loaded.", sceneId, Array.Empty<string>());
        }

        settings.Scenes ??= new();
        var scene = settings.Scenes.FirstOrDefault(s => string.Equals(s.Id, sceneId, StringComparison.OrdinalIgnoreCase));
        if (scene is null)
        {
            return new SceneApplyResult(false, $"Scene '{sceneId}' not found.", sceneId, Array.Empty<string>());
        }

        if (!scene.Enabled)
        {
            return new SceneApplyResult(false, $"Scene '{scene.Name}' is disabled.", sceneId, Array.Empty<string>());
        }

        return await ApplySceneInternalAsync(scene, performedBy, allowRecovery, cancellationToken);
    }

    private async Task<SceneApplyResult> ApplySceneInternalAsync(
        Scene scene,
        string? performedBy,
        bool allowRecovery,
        CancellationToken cancellationToken)
    {
        var settings = await _uow.ISettingsRepo.GetSettings();
        if (settings is null)
        {
            return new SceneApplyResult(false, "Settings could not be loaded.", scene.Id, Array.Empty<string>());
        }

        settings.Speakers ??= new();
        var targetSpeakers = ResolveTargetSpeakers(scene, settings).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!targetSpeakers.Any())
        {
            return new SceneApplyResult(false, "No target speakers available for scene.", scene.Id, Array.Empty<string>());
        }

        bool recoveryActivated = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.WhenAll(targetSpeakers.Select(ip => _uow.ISonosConnectorRepo.UngroupSpeaker(ip, cancellationToken)));

            foreach (var ip in targetSpeakers)
            {
                var volume = ResolveVolume(scene, settings, ip);
                if (volume.HasValue)
                {
                    await _uow.ISonosConnectorRepo.SetSpeakerVolume(ip, Math.Clamp(volume.Value, 0, settings.MaxVolume), cancellationToken);
                }
            }

            var masterIp = ResolveMasterSpeaker(scene, targetSpeakers);
            var playbackTargets = targetSpeakers;

            if (scene.IsSyncedPlayback && targetSpeakers.Count > 1)
            {
                var slaveIps = targetSpeakers.Where(ip => !string.Equals(ip, masterIp, StringComparison.OrdinalIgnoreCase));
                var grouped = await _uow.ISonosConnectorRepo.CreateGroup(masterIp, slaveIps, cancellationToken);
                playbackTargets = grouped ? new List<string> { masterIp } : targetSpeakers;
            }

            settings.AutomationRules ??= new();
            var sourceFailureRule = allowRecovery
                ? settings.AutomationRules
                    .Where(r => r.Enabled && r.TriggerType == AutomationTriggerType.SourceFailure)
                    .OrderBy(r => r.LastModifiedUtc)
                    .FirstOrDefault()
                : null;

            var retryCount = Math.Clamp(sourceFailureRule?.RetryCount ?? 0, 0, MaxRecoveryRetryCount);
            var retryDelaySeconds = Math.Clamp(sourceFailureRule?.RetryDelaySeconds ?? 0, 0, MaxRecoveryRetryDelaySeconds);
            var retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);

            var playbackResult = await ExecutePlaybackWithRetriesAsync(
                scene,
                playbackTargets,
                settings,
                retryCount,
                retryDelay,
                cancellationToken);

            if (!playbackResult.Success)
            {
                if (playbackResult.LastError is null)
                {
                    throw new InvalidOperationException($"Scene '{scene.Name}' playback failed with an unknown error.");
                }

                var recovered = await TryRecoverPlaybackAsync(
                    scene,
                    settings,
                    playbackTargets,
                    playbackResult.LastError,
                    sourceFailureRule,
                    cancellationToken);

                if (!recovered)
                {
                    throw playbackResult.LastError;
                }

                recoveryActivated = true;
            }
            else if (playbackResult.AttemptCount > 1)
            {
                await _actionLogger.LogAsync("SceneRetrySucceeded", $"Scene {scene.Id} succeeded after {playbackResult.AttemptCount} attempts.");
            }

            await _actionLogger.LogAsync("SceneApplied", $"{scene.Name} ({scene.Id}) on {string.Join(", ", targetSpeakers)}");
            await _notificationService.SendNotificationAsync(
                $"Scene '{scene.Name}' applied on {targetSpeakers.Count} speaker(s).",
                performedBy);

            ScheduleSceneTimerIfNeeded(scene, targetSpeakers);
            return new SceneApplyResult(true, "Scene applied.", scene.Id, targetSpeakers, recoveryActivated);
        }
        catch (OperationCanceledException)
        {
            return new SceneApplyResult(false, "Scene application was cancelled.", scene.Id, targetSpeakers, recoveryActivated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply scene {SceneId}.", scene.Id);
            await _actionLogger.LogAsync("SceneApplyFailed", $"{scene.Name} ({scene.Id}): {ex.Message}");
            return new SceneApplyResult(false, $"Failed to apply scene: {ex.Message}", scene.Id, targetSpeakers, recoveryActivated);
        }
    }

    private sealed record PlaybackExecutionResult(bool Success, Exception? LastError, int AttemptCount);

    private async Task<PlaybackExecutionResult> ExecutePlaybackWithRetriesAsync(
        Scene scene,
        IReadOnlyList<string> playbackTargets,
        SonosSettings settings,
        int retryCount,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ExecutePlaybackAsync(scene, playbackTargets, settings, cancellationToken);
                return new PlaybackExecutionResult(true, null, attempt + 1);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;

                if (attempt == retryCount)
                {
                    break;
                }

                _logger.LogWarning(
                    ex,
                    "Scene {SceneId} playback attempt {Attempt}/{TotalAttempts} failed. Retrying in {RetryDelaySeconds} second(s).",
                    scene.Id,
                    attempt + 1,
                    retryCount + 1,
                    retryDelay.TotalSeconds);

                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, cancellationToken);
                }
            }
        }

        return new PlaybackExecutionResult(false, lastError, retryCount + 1);
    }

    private static IReadOnlyList<string> ResolveTargetSpeakers(Scene scene, SonosSettings settings)
    {
        if (scene.Actions?.Any(a => !string.IsNullOrWhiteSpace(a.SpeakerIp) && a.IncludeInPlayback) == true)
        {
            return scene.Actions
                .Where(a => a.IncludeInPlayback)
                .Select(a => a.SpeakerIp.Trim())
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .ToList();
        }

        if (scene.SpeakerIps?.Any(ip => !string.IsNullOrWhiteSpace(ip)) == true)
        {
            return scene.SpeakerIps
                .Select(ip => ip.Trim())
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .ToList();
        }

        return settings.Speakers
            .Select(s => s.IpAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .ToList();
    }

    private static string ResolveMasterSpeaker(Scene scene, IReadOnlyList<string> targetSpeakers)
    {
        if (!string.IsNullOrWhiteSpace(scene.MasterSpeakerIp)
            && targetSpeakers.Contains(scene.MasterSpeakerIp, StringComparer.OrdinalIgnoreCase))
        {
            return scene.MasterSpeakerIp;
        }

        var explicitMaster = scene.Actions
            .FirstOrDefault(a => a.IsMaster && !string.IsNullOrWhiteSpace(a.SpeakerIp))
            ?.SpeakerIp;

        if (!string.IsNullOrWhiteSpace(explicitMaster)
            && targetSpeakers.Contains(explicitMaster, StringComparer.OrdinalIgnoreCase))
        {
            return explicitMaster;
        }

        return targetSpeakers[0];
    }

    private static int? ResolveVolume(Scene scene, SonosSettings settings, string speakerIp)
    {
        var volumeFromAction = scene.Actions
            .FirstOrDefault(a => string.Equals(a.SpeakerIp, speakerIp, StringComparison.OrdinalIgnoreCase))
            ?.Volume;

        if (volumeFromAction.HasValue)
        {
            return volumeFromAction.Value;
        }

        var speakerDefault = settings.Speakers
            .FirstOrDefault(s => string.Equals(s.IpAddress, speakerIp, StringComparison.OrdinalIgnoreCase))
            ?.StartupVolume;

        return speakerDefault ?? settings.Volume;
    }

    private async Task ExecutePlaybackAsync(
        Scene scene,
        IReadOnlyList<string> playbackTargets,
        SonosSettings settings,
        CancellationToken cancellationToken)
    {
        if (!playbackTargets.Any())
        {
            return;
        }

        switch (scene.SourceType)
        {
            case SceneSourceType.None:
                await Task.WhenAll(playbackTargets.Select(ip => _uow.ISonosConnectorRepo.StartPlaying(ip)));
                return;
            case SceneSourceType.Station:
                EnsureSourceUrl(scene);
                await Task.WhenAll(playbackTargets.Select(ip => _uow.ISonosConnectorRepo.SetTuneInStationAsync(ip, scene.SourceUrl!, cancellationToken)));
                return;
            case SceneSourceType.Spotify:
                EnsureSourceUrl(scene);
                await Task.WhenAll(playbackTargets.Select(ip => _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(ip, scene.SourceUrl!, settings.AutoPlayStationUrl, cancellationToken)));
                return;
            case SceneSourceType.YouTubeMusic:
                EnsureSourceUrl(scene);
                await Task.WhenAll(playbackTargets.Select(ip => _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, scene.SourceUrl!, settings.AutoPlayStationUrl, cancellationToken)));
                return;
            default:
                throw new InvalidOperationException($"Unsupported source type '{scene.SourceType}'.");
        }
    }

    private async Task<bool> TryRecoverPlaybackAsync(
        Scene scene,
        SonosSettings settings,
        IReadOnlyList<string> playbackTargets,
        Exception originalError,
        AutomationRule? recoveryRule,
        CancellationToken cancellationToken)
    {
        if (recoveryRule is null || recoveryRule.ActionType == AutomationActionType.None)
        {
            return false;
        }

        _logger.LogWarning(
            originalError,
            "Scene source failed for {SceneId}. Trying recovery rule {RuleId}.",
            scene.Id,
            recoveryRule.Id);

        switch (recoveryRule.ActionType)
        {
            case AutomationActionType.ApplyScene:
                if (string.IsNullOrWhiteSpace(recoveryRule.SceneId))
                {
                    return false;
                }

                if (string.Equals(recoveryRule.SceneId, scene.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Recovery rule {RuleId} points to the same scene {SceneId}; skipping to avoid recursion.", recoveryRule.Id, scene.Id);
                    return false;
                }

                var applyResult = await ApplySceneByIdInternalAsync(
                    recoveryRule.SceneId,
                    performedBy: "automation-recovery",
                    allowRecovery: false,
                    cancellationToken);

                if (!applyResult.Success)
                {
                    return false;
                }

                await _actionLogger.LogAsync("RecoveryActivated", $"Scene {scene.Id} failed, recovered via scene {recoveryRule.SceneId}.");
                return true;
            case AutomationActionType.PlayFallbackSource:
                if (string.IsNullOrWhiteSpace(recoveryRule.FallbackUrl) || recoveryRule.FallbackSourceType == SceneSourceType.None)
                {
                    return false;
                }

                var fallbackScene = new Scene
                {
                    Id = $"fallback-{Guid.NewGuid():N}",
                    Name = "Fallback Recovery",
                    SourceType = recoveryRule.FallbackSourceType,
                    SourceUrl = recoveryRule.FallbackUrl,
                    IsSyncedPlayback = scene.IsSyncedPlayback,
                    SpeakerIps = playbackTargets.ToList(),
                    Actions = scene.Actions
                };

                await ExecutePlaybackAsync(fallbackScene, playbackTargets, settings, cancellationToken);
                await _actionLogger.LogAsync("RecoveryActivated", $"Scene {scene.Id} failed, fallback source started.");
                return true;
            default:
                return false;
        }
    }

    private static void EnsureSourceUrl(Scene scene)
    {
        if (string.IsNullOrWhiteSpace(scene.SourceUrl))
        {
            throw new InvalidOperationException($"Scene '{scene.Name}' requires a source URL.");
        }
    }

    private void ScheduleSceneTimerIfNeeded(Scene scene, IReadOnlyList<string> targetSpeakers)
    {
        if (!scene.TimerMinutes.HasValue || scene.TimerMinutes <= 0 || !targetSpeakers.Any())
        {
            return;
        }

        if (_sceneTimerCancellations.TryRemove(scene.Id, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _sceneTimerCancellations[scene.Id] = cts;
        var token = cts.Token;
        var minutes = scene.TimerMinutes.Value;
        var speakerIps = targetSpeakers.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), token);
                using var scope = _scopeFactory.CreateScope();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var actionLogger = scope.ServiceProvider.GetRequiredService<ActionLogger>();

                await Task.WhenAll(speakerIps.Select(ip => uow.ISonosConnectorRepo.PausePlaying(ip)));
                await actionLogger.LogAsync("SceneTimerCompleted", $"{scene.Name} ({scene.Id}) paused after {minutes} minute(s).");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scene timer failed for {SceneId}.", scene.Id);
            }
            finally
            {
                if (_sceneTimerCancellations.TryRemove(scene.Id, out var removal))
                {
                    removal.Dispose();
                }
            }
        }, token);
    }
}
