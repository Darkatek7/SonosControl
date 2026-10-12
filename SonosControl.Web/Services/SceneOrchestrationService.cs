using Microsoft.Extensions.DependencyInjection;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;

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
    bool RecoveryActivated = false,
    SceneCommandFailure? CommandFailure = null);

public sealed record SceneCommandFailure(string SpeakerIp, string Service, string Action, int? HttpStatus, int? UpnpErrorCode, string Description);

public sealed class SceneOrchestrationService : ISceneOrchestrationService
{
    private const int MaxRecoveryRetryCount = 10;
    private const int MaxRecoveryRetryDelaySeconds = 300;

    private readonly ISettingsRepo _settingsRepo;
    private readonly ISonosConnectorRepo _sonosConnectorRepo;
    private readonly INotificationService _notificationService;
    private readonly ActionLogger _actionLogger;
    private readonly ILogger<SceneOrchestrationService> _logger;
    private readonly SceneTimerService _sceneTimers;

    public SceneOrchestrationService(
        ISettingsRepo settingsRepo, ISonosConnectorRepo sonosConnectorRepo,
        SceneTimerService sceneTimers,
        INotificationService notificationService,
        ActionLogger actionLogger,
        ILogger<SceneOrchestrationService> logger)
    {
        _settingsRepo = settingsRepo;
        _sonosConnectorRepo = sonosConnectorRepo;
        _sceneTimers = sceneTimers;
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
        var draft = SettingsChanges.MergeEntity(scene, scene, scene);
        var settings = await _settingsRepo.GetSettings() ?? throw new InvalidOperationException("Settings could not be loaded.");
        SettingsEditingService.ValidateScene(draft, settings);
        if (!draft.Enabled) return new SceneApplyResult(false, $"Scene '{draft.Name}' is disabled.", draft.Id, Array.Empty<string>());
        return await ApplySceneInternalAsync(draft, performedBy, allowRecovery: true, cancellationToken);
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

        var settings = await _settingsRepo.GetSettings();
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
        var settings = await _settingsRepo.GetSettings();
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
            await using var lease = await _sceneTimers.BeginSceneAsync(targetSpeakers, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await Task.WhenAll(targetSpeakers.Select(ip => _sonosConnectorRepo.UngroupSpeaker(ip, cancellationToken)));

            foreach (var ip in targetSpeakers)
            {
                var volume = ResolveVolume(scene, settings, ip);
                if (volume.HasValue)
                {
                    await _sonosConnectorRepo.SetSpeakerVolume(ip, Math.Clamp(volume.Value, 0, settings.MaxVolume), cancellationToken);
                }
            }

            var masterIp = ResolveMasterSpeaker(scene, targetSpeakers);
            var playbackTargets = targetSpeakers;

            if (scene.IsSyncedPlayback && targetSpeakers.Count > 1)
            {
                var slaveIps = targetSpeakers.Where(ip => !string.Equals(ip, masterIp, StringComparison.OrdinalIgnoreCase));
                var grouped = await _sonosConnectorRepo.CreateGroup(masterIp, slaveIps, cancellationToken);
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

                // A fallback source uses the current group under the same leases.
                // A recovery scene acquires its own (possibly different) target speakers.
                if (sourceFailureRule?.ActionType == AutomationActionType.ApplyScene) await lease.DisposeAsync();
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

            await _actionLogger.LogAsync("SceneApplied", $"{scene.Name} ({scene.Id}) on {string.Join(", ", targetSpeakers)}", performedBy);
            await _notificationService.SendNotificationAsync(
                $"Scene '{scene.Name}' applied on {targetSpeakers.Count} speaker(s).",
                performedBy);

            if (!recoveryActivated || sourceFailureRule?.ActionType == AutomationActionType.PlayFallbackSource) _sceneTimers.Schedule(scene.Id, scene.Name, targetSpeakers, scene.TimerMinutes);
            return new SceneApplyResult(true, "Scene applied.", scene.Id, targetSpeakers, recoveryActivated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply scene {SceneId}.", scene.Id);
            if (performedBy != "automation-scheduler")
                await _actionLogger.LogAsync("SceneApplyFailed", $"{scene.Name} ({scene.Id}): {ex.Message}", performedBy);
            var failure = ex is SonosCommandException command
                ? new SceneCommandFailure(command.SpeakerIp, command.Service, command.Action, command.StatusCode.HasValue ? (int)command.StatusCode.Value : null, command.UpnpErrorCode, command.ErrorDescription)
                : null;
            return new SceneApplyResult(false, $"Failed to apply scene: {ex.Message}", scene.Id, targetSpeakers, recoveryActivated, failure);
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

        var selectionMode = scene.SourceType == SceneSourceType.None
            ? SceneSourceSelectionMode.Resume
            : scene.SourceSelectionMode;

        if (selectionMode == SceneSourceSelectionMode.Resume)
        {
            await Task.WhenAll(playbackTargets.Select(ip => _sonosConnectorRepo.StartPlaying(ip, cancellationToken)));
            return;
        }

        var sourceUrl = selectionMode == SceneSourceSelectionMode.Random
            ? SelectRandomSourceUrl(scene.SourceType, settings)
            : scene.SourceUrl;

        if (selectionMode == SceneSourceSelectionMode.Random && string.IsNullOrWhiteSpace(sourceUrl))
        {
            await Task.WhenAll(playbackTargets.Select(ip => _sonosConnectorRepo.StartPlaying(ip, cancellationToken)));
            return;
        }

        switch (scene.SourceType)
        {
            case SceneSourceType.None:
                await Task.WhenAll(playbackTargets.Select(ip => _sonosConnectorRepo.StartPlaying(ip, cancellationToken)));
                return;
            case SceneSourceType.Station:
                EnsureSourceUrl(scene, sourceUrl);
                await Task.WhenAll(playbackTargets.Select(ip => _sonosConnectorRepo.SetTuneInStationAsync(ip, sourceUrl!, cancellationToken)));
                return;
            case SceneSourceType.Spotify:
                EnsureSourceUrl(scene, sourceUrl);
                await Task.WhenAll(playbackTargets.Select(ip => _sonosConnectorRepo.PlaySpotifyTrackAsync(ip, sourceUrl!, settings.AutoPlayStationUrl, cancellationToken)));
                return;
            case SceneSourceType.YouTubeMusic:
                EnsureSourceUrl(scene, sourceUrl);
                await Task.WhenAll(playbackTargets.Select(ip => _sonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, sourceUrl!, settings.AutoPlayStationUrl, cancellationToken)));
                return;
            case SceneSourceType.YouTube:
                EnsureSourceUrl(scene, sourceUrl);
                var youTubeEntry = settings.YouTubeCollections
                    .FirstOrDefault(entry => string.Equals(entry.Url?.Trim(), sourceUrl!.Trim(), StringComparison.OrdinalIgnoreCase));
                var playbackMode = YouTubePlaybackModeResolver.GetEffectiveMode(sourceUrl, youTubeEntry?.PlaybackMode);
                var preferredQueueLength = YouTubePlaybackModeResolver.GetEffectiveQueueLength(youTubeEntry?.PreferredQueueLength);
                await Task.WhenAll(playbackTargets.Select(ip => _sonosConnectorRepo.PlayYouTubeAudioAsync(ip, sourceUrl!, playbackMode, preferredQueueLength, cancellationToken)));
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
                    SourceSelectionMode = SceneSourceSelectionMode.Specific,
                    SourceType = recoveryRule.FallbackSourceType,
                    SourceUrl = recoveryRule.FallbackUrl,
                    IsSyncedPlayback = scene.IsSyncedPlayback
                };

                await ExecutePlaybackAsync(fallbackScene, playbackTargets, settings, cancellationToken);
                await _actionLogger.LogAsync("RecoveryActivated", $"Scene {scene.Id} failed, fallback source started.");
                return true;
            default:
                return false;
        }
    }

    private static string? SelectRandomSourceUrl(SceneSourceType sourceType, SonosSettings settings)
    {
        var candidates = sourceType switch
        {
            SceneSourceType.Station => settings.Stations?.Select(item => item.Url),
            SceneSourceType.Spotify => settings.SpotifyTracks?.Select(item => item.Url),
            SceneSourceType.YouTubeMusic => settings.YouTubeMusicCollections?.Select(item => item.Url),
            SceneSourceType.YouTube => settings.YouTubeCollections?.Select(item => item.Url),
            _ => Enumerable.Empty<string?>()
        };

        var available = candidates?
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .ToList() ?? new List<string>();

        return available.Count == 0 ? null : available[Random.Shared.Next(available.Count)];
    }

    private static void EnsureSourceUrl(Scene scene, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new InvalidOperationException($"Scene '{scene.Name}' requires a source URL.");
        }
    }

}
