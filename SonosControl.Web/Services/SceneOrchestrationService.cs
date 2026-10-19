using Microsoft.Extensions.DependencyInjection;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.DAL.Repos;

namespace SonosControl.Web.Services;

public interface ISceneOrchestrationService
{
    Task<SceneApplyResult> ApplySceneByIdAsync(string sceneId, string? performedBy = null, CancellationToken cancellationToken = default);
    Task<SceneApplyResult> ApplySceneAsync(Scene scene, string? performedBy = null, CancellationToken cancellationToken = default);
    Task<SceneApplyResult> ApplySceneByIdAsync(string sceneId, string? performedBy, PlaybackCommandOrigin origin, Action<IReadOnlyCollection<string>>? recordTargets, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? targetOverride = null, bool allowRecovery = true);
    Task<SceneApplyResult> ApplySceneByIdWithContextAsync(string sceneId, string? performedBy, SceneExecutionContext context, Action<IReadOnlyCollection<string>>? recordTargets, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? targetOverride = null, bool allowRecovery = true);
    Task<SceneApplyResult> ApplySceneWithContextAsync(Scene scene, string? performedBy, SceneExecutionContext context, Action<IReadOnlyCollection<string>>? recordTargets, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? targetOverride = null, bool allowRecovery = true);
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
    private readonly PlaybackCommandCoordinator? _commands;
    private readonly PlaybackSnapshotMonitorService? _playbackSnapshots;
    private readonly ManualAutoStopService? _autoStop;

    public SceneOrchestrationService(
        ISettingsRepo settingsRepo, ISonosConnectorRepo sonosConnectorRepo,
        SceneTimerService sceneTimers,
        INotificationService notificationService,
        ActionLogger actionLogger,
        ILogger<SceneOrchestrationService> logger,
        PlaybackCommandCoordinator? commands = null,
        PlaybackSnapshotMonitorService? playbackSnapshots = null,
        ManualAutoStopService? autoStop = null)
    {
        _settingsRepo = settingsRepo;
        _sonosConnectorRepo = sonosConnectorRepo;
        _sceneTimers = sceneTimers;
        _notificationService = notificationService;
        _actionLogger = actionLogger;
        _logger = logger;
        _commands = commands;
        _playbackSnapshots = playbackSnapshots;
        _autoStop = autoStop;
    }

    public async Task<SceneApplyResult> ApplySceneByIdAsync(string sceneId, string? performedBy = null, CancellationToken cancellationToken = default)
    {
        return await ApplySceneByIdAsync(sceneId, performedBy, PlaybackCommandOrigin.Manual, null, cancellationToken);
    }

    public async Task<SceneApplyResult> ApplySceneByIdAsync(string sceneId, string? performedBy, PlaybackCommandOrigin origin,
        Action<IReadOnlyCollection<string>>? recordTargets, CancellationToken cancellationToken = default, IReadOnlyCollection<string>? targetOverride = null, bool allowRecovery = true)
    {
        for (var planAttempt = 0; planAttempt < 2; planAttempt++)
        {
            var loaded = await _settingsRepo.GetSettings();
            var scene = loaded?.Scenes?.FirstOrDefault(item => string.Equals(item.Id, sceneId, StringComparison.OrdinalIgnoreCase));
            if (loaded is null) return new SceneApplyResult(false, "Settings could not be loaded.", sceneId, Array.Empty<string>());
            if (scene is null) return new SceneApplyResult(false, $"Scene '{sceneId}' not found.", sceneId, Array.Empty<string>());
            // Freeze both the scene and its companion settings before waiting for ownership.
            var settings = SettingsChanges.MergeEntity(loaded, loaded, loaded);
            var preparedScene = SettingsChanges.MergeEntity(scene, scene, scene);
            var targets = await ResolveExecutionTargetsAsync(preparedScene, settings, targetOverride, allowRecovery: false, cancellationToken);
            await using var commandLease = origin == PlaybackCommandOrigin.Automation || _commands is null || targets.Count == 0
                ? null
                : await _commands.EnterManualAsync(targets, origin, cancellationToken);
            var changedWhileWaiting = false;
            if (origin != PlaybackCommandOrigin.Automation && _commands is not null && targets.Count > 0)
            {
                var current = await _settingsRepo.GetSettings();
                var currentScene = current?.Scenes?.FirstOrDefault(item => string.Equals(item.Id, sceneId, StringComparison.OrdinalIgnoreCase));
                changedWhileWaiting = current is null || currentScene is null || currentScene.LastModifiedUtc != preparedScene.LastModifiedUtc;
                if (!changedWhileWaiting)
                {
                    var currentTargets = await ResolveExecutionTargetsAsync(currentScene!, current!, targetOverride, allowRecovery: false, cancellationToken);
                    changedWhileWaiting = !targets.Order(StringComparer.OrdinalIgnoreCase)
                        .SequenceEqual(currentTargets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                }
            }
            if (changedWhileWaiting)
            {
                if (planAttempt == 0) continue;
                return new SceneApplyResult(false, "Scene or speaker group kept changing while waiting for playback ownership. Apply it again.", sceneId, Array.Empty<string>());
            }
            if (!preparedScene.Enabled) return new SceneApplyResult(false, $"Scene '{preparedScene.Name}' is disabled.", sceneId, Array.Empty<string>());
            await using var context = _commands is null || commandLease is null
                ? null
                : new SceneExecutionContext(_commands, origin, DateTimeOffset.MinValue, cancellationToken, commandLease);
            var result = await ApplySceneInternalAsync(preparedScene, settings, performedBy, allowRecovery, cancellationToken, recordTargets, targetOverride, context);
            if (result.Success && origin != PlaybackCommandOrigin.Automation && _autoStop is not null)
                await _autoStop.RegisterSuccessfulStartAsync(result.TargetSpeakers, cancellationToken);
            return result;
        }
        throw new InvalidOperationException("Unable to prepare scene playback.");
    }

    public async Task<SceneApplyResult> ApplySceneByIdWithContextAsync(string sceneId, string? performedBy, SceneExecutionContext context,
        Action<IReadOnlyCollection<string>>? recordTargets, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? targetOverride = null, bool allowRecovery = true)
    {
        var loaded = await _settingsRepo.GetSettings();
        var scene = loaded?.Scenes?.FirstOrDefault(item => string.Equals(item.Id, sceneId, StringComparison.OrdinalIgnoreCase));
        if (loaded is null) return new SceneApplyResult(false, "Settings could not be loaded.", sceneId, Array.Empty<string>());
        if (scene is null) return new SceneApplyResult(false, $"Scene '{sceneId}' not found.", sceneId, Array.Empty<string>());
        if (!scene.Enabled) return new SceneApplyResult(false, $"Scene '{scene.Name}' is disabled.", sceneId, Array.Empty<string>());
        var settings = SettingsChanges.MergeEntity(loaded, loaded, loaded);
        var preparedScene = SettingsChanges.MergeEntity(scene, scene, scene);
        if (context.CurrentLease is null)
            return new SceneApplyResult(false, "Playback ownership is no longer available.", sceneId, Array.Empty<string>());
        var resolvedTargets = await ResolveExecutionTargetsAsync(preparedScene, settings, targetOverride, allowRecovery: false, cancellationToken);
        if (!context.CurrentLease.Speakers.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(resolvedTargets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            await context.ReplaceTargetsAsync(resolvedTargets, cancellationToken);
            var revalidated = await ResolveExecutionTargetsAsync(preparedScene, settings, targetOverride, allowRecovery: false, cancellationToken);
            if (context.CurrentLease is null || !context.CurrentLease.Speakers.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(revalidated.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                return new SceneApplyResult(false, "Speaker group kept changing while waiting for playback ownership. Apply it again.", sceneId, Array.Empty<string>());
        }
        return await ApplySceneInternalAsync(preparedScene, settings, performedBy, allowRecovery, cancellationToken, recordTargets, targetOverride, context);
    }

    public async Task<SceneApplyResult> ApplySceneWithContextAsync(Scene scene, string? performedBy, SceneExecutionContext context,
        Action<IReadOnlyCollection<string>>? recordTargets, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? targetOverride = null, bool allowRecovery = true)
    {
        var loaded = await _settingsRepo.GetSettings();
        if (loaded is null) return new SceneApplyResult(false, "Settings could not be loaded.", scene.Id, Array.Empty<string>());
        var settings = SettingsChanges.MergeEntity(loaded, loaded, loaded);
        var preparedScene = SettingsChanges.MergeEntity(scene, scene, scene);
        if (!preparedScene.Enabled) return new SceneApplyResult(false, $"Scene '{preparedScene.Name}' is disabled.", preparedScene.Id, Array.Empty<string>());
        SettingsEditingService.ValidateScene(preparedScene, settings, checkUniqueName: false);
        if (context.CurrentLease is null)
            return new SceneApplyResult(false, "Playback ownership is no longer available.", preparedScene.Id, Array.Empty<string>());
        var targets = await ResolveExecutionTargetsAsync(preparedScene, settings, targetOverride, allowRecovery: false, cancellationToken);
        if (!context.CurrentLease.Speakers.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(targets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            await context.ReplaceTargetsAsync(targets, cancellationToken);
            var revalidated = await ResolveExecutionTargetsAsync(preparedScene, settings, targetOverride, allowRecovery: false, cancellationToken);
            if (context.CurrentLease is null || !context.CurrentLease.Speakers.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(revalidated.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                return new SceneApplyResult(false, "Speaker group kept changing while waiting for playback ownership. Apply it again.", preparedScene.Id, Array.Empty<string>());
        }
        return await ApplySceneInternalAsync(preparedScene, settings, performedBy, allowRecovery, cancellationToken, recordTargets, targetOverride, context);
    }

    public async Task<SceneApplyResult> ApplySceneAsync(Scene scene, string? performedBy = null, CancellationToken cancellationToken = default)
    {
        var draft = SettingsChanges.MergeEntity(scene, scene, scene);
        for (var planAttempt = 0; planAttempt < 2; planAttempt++)
        {
            var loaded = await _settingsRepo.GetSettings() ?? throw new InvalidOperationException("Settings could not be loaded.");
            var settings = SettingsChanges.MergeEntity(loaded, loaded, loaded);
            SettingsEditingService.ValidateScene(draft, settings, checkUniqueName: false);
            if (!draft.Enabled) return new SceneApplyResult(false, $"Scene '{draft.Name}' is disabled.", draft.Id, Array.Empty<string>());
            var targets = await ResolveExecutionTargetsAsync(draft, settings, null, allowRecovery: false, cancellationToken);
            await using var commandLease = _commands is null || targets.Count == 0
                ? null
                : await _commands.EnterManualAsync(targets, PlaybackCommandOrigin.Manual, cancellationToken);
            var changedWhileWaiting = false;
            if (_commands is not null && targets.Count > 0)
            {
                var current = await _settingsRepo.GetSettings();
                if (current is null) return new SceneApplyResult(false, "Settings could not be loaded.", draft.Id, Array.Empty<string>());
                var currentTargets = await ResolveExecutionTargetsAsync(draft, current, null, allowRecovery: false, cancellationToken);
                changedWhileWaiting = !targets.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(currentTargets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            }
            if (changedWhileWaiting)
            {
                if (planAttempt == 0) continue;
                return new SceneApplyResult(false, "Speaker group kept changing while waiting for playback ownership. Apply it again.", draft.Id, Array.Empty<string>());
            }
            await using var context = _commands is null || commandLease is null
                ? null
                : new SceneExecutionContext(_commands, PlaybackCommandOrigin.Manual, DateTimeOffset.MinValue, cancellationToken, commandLease);
            var result = await ApplySceneInternalAsync(draft, settings, performedBy, allowRecovery: true, cancellationToken, null, null, context);
            if (result.Success && _autoStop is not null)
                await _autoStop.RegisterSuccessfulStartAsync(result.TargetSpeakers, cancellationToken);
            return result;
        }
        throw new InvalidOperationException("Unable to prepare scene playback.");
    }

    private async Task<SceneApplyResult> ApplySceneByIdInternalAsync(
        string sceneId,
        string? performedBy,
        bool allowRecovery,
        CancellationToken cancellationToken,
        Action<IReadOnlyCollection<string>>? recordTargets = null,
        IReadOnlyCollection<string>? targetOverride = null)
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

        return await ApplySceneInternalAsync(scene, settings, performedBy, allowRecovery, cancellationToken, recordTargets, targetOverride, null);
    }

    private async Task<SceneApplyResult> ApplySceneInternalAsync(
        Scene scene,
        SonosSettings settings,
        string? performedBy,
        bool allowRecovery,
        CancellationToken cancellationToken,
        Action<IReadOnlyCollection<string>>? recordTargets,
        IReadOnlyCollection<string>? targetOverride,
        SceneExecutionContext? executionContext)
    {
        settings.Speakers ??= new();
        var targetSpeakers = SceneTargetResolver.Resolve(scene, settings).ToList();
        if (targetOverride is not null)
            targetSpeakers = targetSpeakers.Intersect(targetOverride, StringComparer.OrdinalIgnoreCase).ToList();
        if (!targetSpeakers.Any())
        {
            return new SceneApplyResult(false, "Select at least one speaker.", scene.Id, Array.Empty<string>());
        }

        bool recoveryActivated = false;
        string? groupingWarning = null;
        IReadOnlyList<string> appliedTargets = targetSpeakers;

        try
        {
            await using var lease = await _sceneTimers.BeginSceneAsync(targetSpeakers, cancellationToken);
            var commandToken = executionContext?.CancellationToken ?? cancellationToken;
            commandToken.ThrowIfCancellationRequested();

            // Ownership starts with the first side effect, not merely target validation.
            recordTargets?.Invoke(targetSpeakers);
            await Task.WhenAll(targetSpeakers.Select(ip => _sonosConnectorRepo.UngroupSpeaker(ip, commandToken)));

            foreach (var ip in targetSpeakers)
            {
                var volume = ResolveVolume(scene, settings, ip);
                if (volume.HasValue)
                {
                    await _sonosConnectorRepo.SetSpeakerVolume(ip, Math.Clamp(volume.Value, 0, settings.MaxVolume), commandToken);
                }
            }

            var masterIp = ResolveMasterSpeaker(scene, targetSpeakers);
            var playbackTargets = targetSpeakers;

            if (scene.IsSyncedPlayback && targetSpeakers.Count > 1)
            {
                var slaveIps = targetSpeakers.Where(ip => !string.Equals(ip, masterIp, StringComparison.OrdinalIgnoreCase));
                var grouped = false;
                try
                {
                    grouped = await _sonosConnectorRepo.CreateGroup(masterIp, slaveIps, commandToken);
                }
                catch (SonosCommandException ex) when (ex.Action == "SetAVTransportURI" && ex.UpnpErrorCode == 501)
                {
                    _logger.LogWarning(ex, "Synchronized grouping is unavailable for scene {SceneId}.", scene.Id);
                }
                if (!grouped)
                {
                    // Clear any members joined before the failure, then play each target directly.
                    await Task.WhenAll(targetSpeakers.Select(ip => _sonosConnectorRepo.UngroupSpeaker(ip, commandToken)));
                    groupingWarning = "Synchronized grouping is unavailable; speakers are playing independently. Check that they belong to the same Sonos system.";
                    await _actionLogger.LogAsync("SceneGroupingUnavailable", $"{scene.Name} ({scene.Id}): {groupingWarning}", performedBy);
                }
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
                commandToken);

            if (!playbackResult.Success)
            {
                if (playbackResult.LastError is null)
                {
                    throw new InvalidOperationException($"Scene '{scene.Name}' playback failed with an unknown error.");
                }

                // A fallback source uses the current group under the same leases.
                // A recovery scene acquires its own (possibly different) target speakers.
                if (sourceFailureRule?.ActionType == AutomationActionType.ApplyScene) await lease.DisposeAsync();
                var recoveryTouchedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void RecordRecoveryTargets(IReadOnlyCollection<string> targets)
                {
                    recoveryTouchedTargets.UnionWith(targets);
                    recordTargets?.Invoke(targets);
                }
                var recoveredTargets = await TryRecoverPlaybackAsync(
                    scene,
                    settings,
                    playbackTargets,
                    playbackResult.LastError,
                    sourceFailureRule,
                    commandToken,
                    RecordRecoveryTargets,
                    null,
                    executionContext);

                if (recoveredTargets is null)
                {
                    throw playbackResult.LastError;
                }

                recoveryActivated = true;
                appliedTargets = recoveredTargets;
            }
            else if (playbackResult.AttemptCount > 1)
            {
                await _actionLogger.LogAsync("SceneRetrySucceeded", $"Scene {scene.Id} succeeded after {playbackResult.AttemptCount} attempts.");
            }

            await _actionLogger.LogAsync("SceneApplied", $"{scene.Name} ({scene.Id}) on {string.Join(", ", appliedTargets)}", performedBy);
            // FlyDJ switches stations every few minutes; its own log and page already report each pick.
            if (!string.Equals(performedBy, FlyDj.FlyDjConductorService.DefaultActor, StringComparison.Ordinal))
                await _notificationService.SendNotificationAsync(
                    $"Scene '{scene.Name}' applied on {appliedTargets.Count} speaker(s). {groupingWarning}".TrimEnd(),
                    performedBy);
            if (_playbackSnapshots is not null)
                await _playbackSnapshots.RefreshNowAsync(commandToken);

            if (!recoveryActivated || sourceFailureRule?.ActionType == AutomationActionType.PlayFallbackSource) _sceneTimers.Schedule(scene.Id, scene.Name, targetSpeakers, scene.TimerMinutes);
            return new SceneApplyResult(true, groupingWarning is null ? "Scene applied." : $"Scene applied. {groupingWarning}", scene.Id, appliedTargets, recoveryActivated);
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

    private async Task<IReadOnlyCollection<string>> ResolveExecutionTargetsAsync(
        Scene scene, SonosSettings settings, IReadOnlyCollection<string>? targetOverride, bool allowRecovery, CancellationToken cancellationToken)
    {
        var targets = SceneTargetResolver.Resolve(scene, settings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetOverride is not null) targets.IntersectWith(targetOverride);

        if (allowRecovery)
        {
            var recovery = settings.AutomationRules?
                .Where(rule => rule.Enabled && rule.TriggerType == AutomationTriggerType.SourceFailure && rule.ActionType == AutomationActionType.ApplyScene)
                .OrderBy(rule => rule.LastModifiedUtc)
                .FirstOrDefault();
            var recoveryScene = recovery is null ? null : settings.Scenes?.FirstOrDefault(item =>
                string.Equals(item.Id, recovery.SceneId, StringComparison.OrdinalIgnoreCase));
            if (recoveryScene is not null)
                targets.UnionWith(SceneTargetResolver.Resolve(recoveryScene, settings));
        }

        foreach (var ip in targets.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var member in await _sonosConnectorRepo.GetAllSpeakersInGroup(ip, cancellationToken) ?? [])
                    if (!string.IsNullOrWhiteSpace(member)) targets.Add(member.Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not resolve speaker group for playback target {SpeakerIp}.", ip);
            }
        }
        return targets.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

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

    private async Task<IReadOnlyList<string>?> TryRecoverPlaybackAsync(
        Scene scene,
        SonosSettings settings,
        IReadOnlyList<string> playbackTargets,
        Exception originalError,
        AutomationRule? recoveryRule,
        CancellationToken cancellationToken,
        Action<IReadOnlyCollection<string>>? recordTargets,
        IReadOnlyCollection<string>? targetOverride,
        SceneExecutionContext? executionContext)
    {
        if (recoveryRule is null || recoveryRule.ActionType == AutomationActionType.None)
        {
            return null;
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
                    return null;
                }

                if (string.Equals(recoveryRule.SceneId, scene.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Recovery rule {RuleId} points to the same scene {SceneId}; skipping to avoid recursion.", recoveryRule.Id, scene.Id);
                    return null;
                }

                SceneApplyResult applyResult;
                if (executionContext is not null)
                {
                    var recoveryScene = settings.Scenes?.FirstOrDefault(item =>
                        string.Equals(item.Id, recoveryRule.SceneId, StringComparison.OrdinalIgnoreCase));
                    if (recoveryScene is null || !recoveryScene.Enabled) return null;
                    var recoveryTargets = await ResolveExecutionTargetsAsync(recoveryScene, settings, null, allowRecovery: false, cancellationToken);
                    if (!await executionContext.ReplaceTargetsAsync(recoveryTargets, cancellationToken)) return null;
                    applyResult = await ApplySceneInternalAsync(
                        SettingsChanges.MergeEntity(recoveryScene, recoveryScene, recoveryScene), settings,
                        performedBy: "automation-recovery", allowRecovery: false,
                        executionContext.CancellationToken, recordTargets, null, executionContext);
                }
                else
                {
                    applyResult = await ApplySceneByIdInternalAsync(
                        recoveryRule.SceneId, performedBy: "automation-recovery", allowRecovery: false,
                        cancellationToken, recordTargets, null);
                }

                if (!applyResult.Success) return null;

                await _actionLogger.LogAsync("RecoveryActivated", $"Scene {scene.Id} failed, recovered via scene {recoveryRule.SceneId}.");
                return applyResult.TargetSpeakers;
            case AutomationActionType.PlayFallbackSource:
                if (string.IsNullOrWhiteSpace(recoveryRule.FallbackUrl) || recoveryRule.FallbackSourceType == SceneSourceType.None)
                {
                    return null;
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

                recordTargets?.Invoke(playbackTargets);
                await ExecutePlaybackAsync(fallbackScene, playbackTargets, settings, cancellationToken);
                await _actionLogger.LogAsync("RecoveryActivated", $"Scene {scene.Id} failed, fallback source started.");
                return playbackTargets;
            default:
                return null;
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
