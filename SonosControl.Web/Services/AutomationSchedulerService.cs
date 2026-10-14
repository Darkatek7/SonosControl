using Microsoft.Extensions.DependencyInjection;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public interface IAutomationScheduler
{
    AutomationRuntimeSnapshot Status { get; }
    Task EvaluateNowAsync(CancellationToken cancellationToken = default);
}

public sealed class AutomationSchedulerService : BackgroundService, IAutomationScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISettingsSchemaMigrationService _migrationService;
    private readonly AutomationRuntimeStatus _runtimeStatus;
    private readonly ILogger<AutomationSchedulerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _timeZone;
    private readonly PlaybackCommandCoordinator _commands;
    private readonly PlaybackSnapshotMonitorService? _playbackSnapshots;
    private ActiveOccurrence? _ownedOccurrence;
    private int _evaluating;
    private AttemptKey? _appliedAttempt;
    private AttemptKey? _failedAttempt;
    private int _failureCount;
    private string? _failureMessage;
    private DateTimeOffset? _nextRetryUtc;
    private static readonly int[] RetrySeconds = [30, 60, 120, 300];
    private sealed record AttemptKey(string WindowId, DateTime WindowRevision, string SceneId, DateTime SceneRevision, DateOnly StartDate);
    private sealed record ActiveOccurrence(AttemptKey Attempt, DateTimeOffset StartUtc, string Name, int FadeOutSeconds, bool IsSynced, IReadOnlyCollection<string> Targets);

    public AutomationSchedulerService(
        IServiceScopeFactory scopeFactory,
        ISettingsSchemaMigrationService migrationService,
        AutomationRuntimeStatus runtimeStatus,
        ILogger<AutomationSchedulerService> logger,
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null,
        PlaybackCommandCoordinator? commands = null,
        PlaybackSnapshotMonitorService? playbackSnapshots = null)
    {
        _scopeFactory = scopeFactory;
        _migrationService = migrationService;
        _runtimeStatus = runtimeStatus;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _commands = commands ?? new PlaybackCommandCoordinator(_timeProvider);
        _playbackSnapshots = playbackSnapshots;
        _timeZone = timeZone ?? ResolveTimeZone(configuration["Automation:TimeZone"]);
    }

    public AutomationRuntimeSnapshot Status => _runtimeStatus.Snapshot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Canonical automation scheduler started in timezone {TimeZone}.", _timeZone.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateNowAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _runtimeStatus.RecordEvaluation(null, _timeProvider.GetUtcNow(), ex.Message);
                _logger.LogWarning(ex, "Canonical schedule evaluation failed.");
            }

            await Task.Delay(PollInterval, _timeProvider, stoppingToken);
        }
    }

    public async Task EvaluateNowAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _evaluating, 1) != 0) return;
        try
        {
            var migration = await _migrationService.MigrateIfRequiredAsync(cancellationToken);
            if (!migration.Success)
            {
                _ownedOccurrence = null;
                _appliedAttempt = null;
                ResetFailure();
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<ISettingsRepo>();
            var sonosConnectorRepo = scope.ServiceProvider.GetRequiredService<ISonosConnectorRepo>();
            var sceneOrchestration = scope.ServiceProvider.GetRequiredService<ISceneOrchestrationService>();
            var actionLogger = scope.ServiceProvider.GetRequiredService<ActionLogger>();

            var settings = await settingsRepo.GetSettings();
            if (settings is null || settings.SettingsSchemaVersion < SonosSettings.CurrentSettingsSchemaVersion)
            {
                return;
            }

            settings.NormalizeAutomationDateExceptions();
            settings.ScheduleWindows ??= new();
            settings.Scenes ??= new();
            var validSceneIds = settings.Scenes
                .Where(scene => scene.Enabled && !string.IsNullOrWhiteSpace(scene.Id))
                .Select(scene => scene.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var eligibleWindows = settings.ScheduleWindows
                .Where(window => window.IsEnabled)
                .Where(window => !string.IsNullOrWhiteSpace(window.SceneId) && validSceneIds.Contains(window.SceneId))
                .ToList();

            var utcNow = _timeProvider.GetUtcNow();
            var localNow = TimeZoneInfo.ConvertTime(utcNow, _timeZone);
            var activeWindow = ScheduleWindowEvaluator.SelectActiveWindow(eligibleWindows, localNow, settings);

            if (activeWindow is null)
            {
                if (_ownedOccurrence is not null)
                {
                    await StopOwnedPlaybackAsync(_ownedOccurrence, sonosConnectorRepo, actionLogger, cancellationToken,
                        excludeSpeakerIps: _commands.ManualSpeakersSince(_ownedOccurrence.StartUtc),
                        skipFadeOut: settings.ExcludesAutomationDate(DateOnly.FromDateTime(localNow.DateTime)));
                    _ownedOccurrence = null;
                    _appliedAttempt = null;
                    ResetFailure();
                }

                ResetFailure();
                _runtimeStatus.RecordEvaluation(null, utcNow);
                return;
            }

            var scene = settings.Scenes.Single(item => string.Equals(item.Id, activeWindow.SceneId, StringComparison.OrdinalIgnoreCase));
            var startDate = DateOnly.FromDateTime(localNow.DateTime);
            if (activeWindow.StopTime <= activeWindow.StartTime && TimeOnly.FromDateTime(localNow.DateTime) < activeWindow.StopTime)
                startDate = startDate.AddDays(-1);
            var attempt = new AttemptKey(activeWindow.Id, activeWindow.LastModifiedUtc, scene.Id, scene.LastModifiedUtc, startDate);
            var localStart = startDate.ToDateTime(activeWindow.StartTime);
            while (_timeZone.IsInvalidTime(localStart)) localStart = localStart.AddMinutes(1);
            var occurrenceStart = _timeZone.IsAmbiguousTime(localStart)
                ? new DateTimeOffset(localStart, _timeZone.GetAmbiguousTimeOffsets(localStart).Max()).ToUniversalTime()
                : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, _timeZone));
            var manualSpeakers = _commands.ManualSpeakersSince(occurrenceStart);
            var configuredTargets = ResolveWindowTargetSpeakers(activeWindow, settings);
            var targetGroups = await ResolveAffectedGroupsAsync(configuredTargets, sonosConnectorRepo, cancellationToken);
            var manualAffectedGroups = targetGroups.Values.Where(group => group.Any(ip => manualSpeakers.Contains(ip, StringComparer.OrdinalIgnoreCase)))
                .SelectMany(group => group).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activeTargets = configuredTargets.Where(ip => !targetGroups[ip].Any(member => manualSpeakers.Contains(member, StringComparer.OrdinalIgnoreCase))).ToList();
            var protectedTargets = manualSpeakers.Concat(manualAffectedGroups)
                .Concat(activeTargets.SelectMany(ip => targetGroups[ip])).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (scene.IsSyncedPlayback && activeTargets.Count != configuredTargets.Count)
            {
                if (_ownedOccurrence is not null && _ownedOccurrence.Attempt != attempt)
                {
                    await StopOwnedPlaybackAsync(_ownedOccurrence, sonosConnectorRepo, actionLogger, cancellationToken,
                        excludeSpeakerIps: protectedTargets);
                    _ownedOccurrence = null;
                    _appliedAttempt = null;
                }
                ResetFailure();
                _runtimeStatus.RecordEvaluation(null, utcNow);
                return;
            }
            activeTargets = activeTargets.Where(ip => !manualSpeakers.Contains(ip, StringComparer.OrdinalIgnoreCase)).ToList();
            if (activeTargets.Count == 0)
            {
                if (_ownedOccurrence is not null && _ownedOccurrence.Attempt != attempt)
                {
                    await StopOwnedPlaybackAsync(_ownedOccurrence, sonosConnectorRepo, actionLogger, cancellationToken,
                        excludeSpeakerIps: protectedTargets);
                    _ownedOccurrence = null;
                    _appliedAttempt = null;
                }
                ResetFailure();
                _runtimeStatus.RecordEvaluation(null, utcNow);
                return;
            }
            if (_failedAttempt != attempt) ResetFailure();
            if (_nextRetryUtc.HasValue && utcNow < _nextRetryUtc.Value)
            {
                _runtimeStatus.RecordEvaluation(null, utcNow, _failureMessage, _nextRetryUtc);
                return;
            }
            if (_appliedAttempt == attempt)
            {
                _runtimeStatus.RecordEvaluation(activeWindow.Name, utcNow);
                return;
            }

            try
            {
                if (_ownedOccurrence is not null && _ownedOccurrence.Attempt != attempt)
                {
                    await StopOwnedPlaybackAsync(_ownedOccurrence, sonosConnectorRepo, actionLogger, cancellationToken,
                        activeTargets.SelectMany(ip => targetGroups[ip]).Concat(protectedTargets).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                }
                // Capture targets and fade policy before execution so failure, edits, and deletion cannot erase ownership.
                var inheritedTargets = _ownedOccurrence?.Attempt == attempt
                    ? _ownedOccurrence.Targets
                    : Array.Empty<string>();
                _ownedOccurrence = new ActiveOccurrence(attempt, occurrenceStart, activeWindow.Name, activeWindow.FadeOutSeconds, scene.IsSyncedPlayback, inheritedTargets.ToArray());
                var touchedTargets = inheritedTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var touchedSync = new object();
                void RecordTargets(IReadOnlyCollection<string> targets)
                {
                    lock (touchedSync)
                    {
                        touchedTargets.UnionWith(targets);
                        _ownedOccurrence = _ownedOccurrence! with { Targets = touchedTargets.ToList() };
                    }
                }
                var failedResults = new List<SceneApplyResult>();
                var completedAny = 0;
                var allowRecovery = true;
                List<IReadOnlyCollection<string>> partitions = scene.IsSyncedPlayback
                    ? [activeTargets]
                    : activeTargets.Select(ip => (IReadOnlyCollection<string>)[ip]).ToList();
                async Task ApplyPartitionAsync(IReadOnlyCollection<string> partition)
                {
                    var partitionLeaseTargets = partition.SelectMany(ip => targetGroups[ip]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var commandLease = await _commands.EnterAutomationAsync(partitionLeaseTargets, occurrenceStart, cancellationToken);
                    if (commandLease is null) return;
                    await using var commandOwnership = new SceneExecutionContext(_commands, PlaybackCommandOrigin.Automation,
                        occurrenceStart, cancellationToken, commandLease);
                    try
                    {
                        // Each independent room uses its own scoped orchestration/logger/DbContext.
                        using var partitionScope = _scopeFactory.CreateScope();
                        var partitionOrchestration = partitionScope.ServiceProvider.GetRequiredService<ISceneOrchestrationService>();
                        var result = await partitionOrchestration.ApplySceneByIdWithContextAsync(activeWindow.SceneId!, "automation-scheduler", commandOwnership,
                            RecordTargets, commandLease.CancellationToken, partition, allowRecovery);
                        cancellationToken.ThrowIfCancellationRequested();
                        commandOwnership.CancellationToken.ThrowIfCancellationRequested();
                        RecordTargets(result.TargetSpeakers);
                        if (!result.Success) lock (failedResults) failedResults.Add(result);
                        else
                        {
                            Interlocked.Exchange(ref completedAny, 1);
                            if (activeWindow.FadeInSeconds > 0 && result.TargetSpeakers.Count > 0 && commandOwnership.CurrentLease is not null)
                            {
                                await ApplyFadeInAsync(result.TargetSpeakers, activeWindow.FadeInSeconds, sonosConnectorRepo, commandOwnership.CancellationToken);
                                if (_playbackSnapshots is not null)
                                    await _playbackSnapshots.RefreshNowAsync(commandOwnership.CancellationToken);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (commandOwnership.WasPreempted && !scene.IsSyncedPlayback)
                    {
                        return;
                    }
                }
                await Task.WhenAll(partitions.Select(ApplyPartitionAsync));
                if (failedResults.Count > 0)
                {
                    await RecordFailureAsync(attempt, activeWindow.Name, string.Join("; ", failedResults.Select(result => result.Message)), actionLogger);
                    return;
                }
                if (completedAny == 0)
                {
                    ResetFailure();
                    _runtimeStatus.RecordEvaluation(null, _timeProvider.GetUtcNow());
                    return;
                }

                if (_failureCount > 0)
                    await actionLogger.LogAsync("ScheduleRecovered", $"Schedule '{activeWindow.Name}' recovered after {_failureCount} failed start(s).", "automation-scheduler");
                await actionLogger.LogAsync("ScheduleTriggered", $"Schedule '{activeWindow.Name}' triggered scene '{activeWindow.SceneId}'.", "automation-scheduler");
                _appliedAttempt = attempt;
                ResetFailure();
                _runtimeStatus.RecordEvaluation(activeWindow.Name, _timeProvider.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(attempt, activeWindow.Name, ex.Message, actionLogger);
                _logger.LogWarning(ex, "Schedule {WindowId} start failed.", activeWindow.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtimeStatus.RecordEvaluation(null, _timeProvider.GetUtcNow(), ex.Message);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Overlapping manual playback preempted this occurrence. It is not a scheduler failure.
            _runtimeStatus.RecordEvaluation(null, _timeProvider.GetUtcNow());
        }
        finally { Volatile.Write(ref _evaluating, 0); }
    }

    private async Task RecordFailureAsync(AttemptKey attempt, string windowName, string error, ActionLogger actionLogger)
    {
        var message = $"Schedule '{windowName}' could not apply scene: {error}";
        if (_failureMessage != message)
            await actionLogger.LogAsync("ScheduleTriggerFailed", message, "automation-scheduler");
        _failedAttempt = attempt;
        _failureCount++;
        _failureMessage = message;
        var now = _timeProvider.GetUtcNow();
        _nextRetryUtc = now.AddSeconds(RetrySeconds[Math.Min(_failureCount - 1, RetrySeconds.Length - 1)]);
        _runtimeStatus.RecordEvaluation(null, now, message, _nextRetryUtc);
    }

    private void ResetFailure()
    {
        _failedAttempt = null;
        _failureCount = 0;
        _failureMessage = null;
        _nextRetryUtc = null;
    }

    private static TimeZoneInfo ResolveTimeZone(string? configuredId)
    {
        var id = string.IsNullOrWhiteSpace(configuredId) ? "Europe/Vienna" : configuredId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private async Task StopOwnedPlaybackAsync(
        ActiveOccurrence occurrence,
        ISonosConnectorRepo sonosConnectorRepo,
        ActionLogger actionLogger,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludeSpeakerIps = null,
        bool skipFadeOut = false)
    {
        var targetSpeakers = occurrence.Targets.ToList();
        if (excludeSpeakerIps?.Count > 0)
        {
            targetSpeakers = targetSpeakers
                .Where(ip => !excludeSpeakerIps.Contains(ip, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        if (targetSpeakers.Count == 0)
        {
            return;
        }

        List<IReadOnlyCollection<string>> partitions = occurrence.IsSynced
            ? [targetSpeakers]
            : targetSpeakers.Select(ip => (IReadOnlyCollection<string>)[ip]).ToList();
        foreach (var partition in partitions)
        {
            var ownership = await _commands.EnterAutomationAsync(partition, occurrence.StartUtc, cancellationToken);
            if (ownership is null) continue;
            await using var lease = ownership;
            try
            {
                if (!skipFadeOut && occurrence.FadeOutSeconds > 0)
                    await ApplyFadeOutAsync(partition, occurrence.FadeOutSeconds, sonosConnectorRepo, lease.CancellationToken);
                lease.CancellationToken.ThrowIfCancellationRequested();
                await Task.WhenAll(partition.Select(ip => sonosConnectorRepo.PausePlaying(ip, lease.CancellationToken)));
            }
            catch (OperationCanceledException) when (lease.WasPreempted) { continue; }
        }
        await actionLogger.LogAsync(
            "ScheduleStopped",
            $"Schedule '{occurrence.Name}' stopped.", "automation-scheduler");
        if (_playbackSnapshots is not null)
            await _playbackSnapshots.RefreshNowAsync(cancellationToken);
    }

    private static IReadOnlyCollection<string> ResolveWindowTargetSpeakers(ScheduleWindow? window, SonosSettings settings)
    {
        settings.Speakers ??= new();
        settings.Scenes ??= new();

        if (window is not null && !string.IsNullOrWhiteSpace(window.SceneId))
        {
            var scene = settings.Scenes.FirstOrDefault(item =>
                string.Equals(item.Id, window.SceneId, StringComparison.OrdinalIgnoreCase));
            if (scene is not null) return SceneTargetResolver.Resolve(scene, settings);
        }

        return settings.Speakers
            .Select(speaker => speaker.IpAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<string, IReadOnlyCollection<string>>> ResolveAffectedGroupsAsync(
        IReadOnlyCollection<string> selectedTargets, ISonosConnectorRepo connector, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in selectedTargets)
        {
            var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ip };
            try
            {
                foreach (var member in await connector.GetAllSpeakersInGroup(ip, cancellationToken) ?? [])
                    if (!string.IsNullOrWhiteSpace(member)) group.Add(member.Trim());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not resolve current group for scheduled target {SpeakerIp}; playback will revalidate it before writes.", ip);
            }
            result[ip] = group.ToArray();
        }
        return result;
    }

    private static async Task ApplyFadeOutAsync(
        IReadOnlyCollection<string> targetSpeakers,
        int fadeOutSeconds,
        ISonosConnectorRepo sonosConnectorRepo,
        CancellationToken cancellationToken)
    {
        var steps = Math.Clamp(fadeOutSeconds, 2, 20);
        var delay = TimeSpan.FromSeconds((double)fadeOutSeconds / steps);
        var startingVolumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in targetSpeakers)
        {
            try
            {
                startingVolumes[ip] = await sonosConnectorRepo.GetVolume(ip, cancellationToken);
            }
            catch
            {
                startingVolumes[ip] = 10;
            }
        }

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ratio = (double)(steps - step) / steps;
            await Task.WhenAll(targetSpeakers.Select(ip =>
            {
                var start = startingVolumes.TryGetValue(ip, out var value) ? value : 10;
                return sonosConnectorRepo.SetSpeakerVolume(
                    ip,
                    Math.Max(0, (int)Math.Round(start * ratio)),
                    cancellationToken);
            }));
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static async Task ApplyFadeInAsync(
        IReadOnlyCollection<string> targetSpeakers,
        int fadeInSeconds,
        ISonosConnectorRepo sonosConnectorRepo,
        CancellationToken cancellationToken)
    {
        var steps = Math.Clamp(fadeInSeconds, 2, 20);
        var delay = TimeSpan.FromSeconds((double)fadeInSeconds / steps);
        var targetVolumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in targetSpeakers)
        {
            try
            {
                targetVolumes[ip] = await sonosConnectorRepo.GetVolume(ip, cancellationToken);
            }
            catch
            {
                targetVolumes[ip] = 15;
            }
        }

        await Task.WhenAll(targetSpeakers.Select(ip =>
            sonosConnectorRepo.SetSpeakerVolume(ip, 1, cancellationToken)));

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ratio = (double)step / steps;
            await Task.WhenAll(targetSpeakers.Select(ip =>
            {
                var target = targetVolumes.TryGetValue(ip, out var value) ? value : 15;
                return sonosConnectorRepo.SetSpeakerVolume(
                    ip,
                    Math.Max(1, (int)Math.Round(target * ratio)),
                    cancellationToken);
            }));
            await Task.Delay(delay, cancellationToken);
        }
    }
}
