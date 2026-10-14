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
    private DateTimeOffset _ownedOccurrenceStart;

    private string? _activeWindowId;
    private AttemptKey? _appliedAttempt;
    private AttemptKey? _failedAttempt;
    private int _failureCount;
    private string? _failureMessage;
    private DateTimeOffset? _nextRetryUtc;
    private static readonly int[] RetrySeconds = [30, 60, 120, 300];
    private sealed record AttemptKey(string WindowId, DateTime WindowRevision, string SceneId, DateTime SceneRevision, DateOnly StartDate);

    public AutomationSchedulerService(
        IServiceScopeFactory scopeFactory,
        ISettingsSchemaMigrationService migrationService,
        AutomationRuntimeStatus runtimeStatus,
        ILogger<AutomationSchedulerService> logger,
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null,
        PlaybackCommandCoordinator? commands = null)
    {
        _scopeFactory = scopeFactory;
        _migrationService = migrationService;
        _runtimeStatus = runtimeStatus;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _commands = commands ?? new PlaybackCommandCoordinator(_timeProvider);
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
        await using var commandLease = await _commands.EnterAsync(false, cancellationToken);
        if (commandLease is null)
        {
            return;
        }

        try
        {
            var migration = await _migrationService.MigrateIfRequiredAsync(cancellationToken);
            if (!migration.Success)
            {
                _activeWindowId = null;
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
                if (!string.IsNullOrWhiteSpace(_activeWindowId))
                {
                    var oldWindow = settings.ScheduleWindows.FirstOrDefault(window =>
                        string.Equals(window.Id, _activeWindowId, StringComparison.OrdinalIgnoreCase));
                    await StopWindowPlaybackAsync(oldWindow, settings, sonosConnectorRepo, actionLogger, cancellationToken,
                        excludeSpeakerIps: _commands.ManualSpeakersSince(_ownedOccurrenceStart),
                        skipFadeOut: settings.ExcludesAutomationDate(DateOnly.FromDateTime(localNow.DateTime)));
                    _activeWindowId = null;
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
            if (ResolveWindowTargetSpeakers(activeWindow, settings).Any(ip => manualSpeakers.Contains(ip, StringComparer.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrWhiteSpace(_activeWindowId)
                    && !string.Equals(_activeWindowId, activeWindow.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var oldWindow = settings.ScheduleWindows.FirstOrDefault(window =>
                        string.Equals(window.Id, _activeWindowId, StringComparison.OrdinalIgnoreCase));
                    await StopWindowPlaybackAsync(oldWindow, settings, sonosConnectorRepo, actionLogger, cancellationToken,
                        excludeSpeakerIps: _commands.ManualSpeakersSince(_ownedOccurrenceStart));
                    _activeWindowId = null;
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
                if (!string.IsNullOrWhiteSpace(_activeWindowId)
                    && !string.Equals(_activeWindowId, activeWindow.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var oldWindow = settings.ScheduleWindows.FirstOrDefault(window =>
                        string.Equals(window.Id, _activeWindowId, StringComparison.OrdinalIgnoreCase));
                    var newTargets = ResolveWindowTargetSpeakers(activeWindow, settings);
                    await StopWindowPlaybackAsync(oldWindow, settings, sonosConnectorRepo, actionLogger, cancellationToken,
                        newTargets.Concat(_commands.ManualSpeakersSince(_ownedOccurrenceStart)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                }
                // Retain attempted targets so a partially applied scene is stopped at the window boundary.
                _activeWindowId = activeWindow.Id;
                _ownedOccurrenceStart = occurrenceStart;
                var result = await sceneOrchestration.ApplySceneByIdAsync(
                    activeWindow.SceneId!, performedBy: "automation-scheduler", cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!result.Success)
                {
                    await RecordFailureAsync(attempt, activeWindow.Name, result.Message, actionLogger);
                    return;
                }
                if (activeWindow.FadeInSeconds > 0 && result.TargetSpeakers.Count > 0)
                    await ApplyFadeInAsync(result.TargetSpeakers, activeWindow.FadeInSeconds, sonosConnectorRepo, cancellationToken);

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

    private static async Task StopWindowPlaybackAsync(
        ScheduleWindow? window,
        SonosSettings settings,
        ISonosConnectorRepo sonosConnectorRepo,
        ActionLogger actionLogger,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludeSpeakerIps = null,
        bool skipFadeOut = false)
    {
        var targetSpeakers = ResolveWindowTargetSpeakers(window, settings).ToList();
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

        if (!skipFadeOut && (window?.FadeOutSeconds ?? 0) > 0)
        {
            await ApplyFadeOutAsync(targetSpeakers, window!.FadeOutSeconds, sonosConnectorRepo, cancellationToken);
        }

        await Task.WhenAll(targetSpeakers.Select(ip => sonosConnectorRepo.PausePlaying(ip)));
        await actionLogger.LogAsync(
            "ScheduleStopped",
            window is null ? "Scheduled playback stopped." : $"Schedule '{window.Name}' stopped.", "automation-scheduler");
    }

    private static IReadOnlyCollection<string> ResolveWindowTargetSpeakers(ScheduleWindow? window, SonosSettings settings)
    {
        settings.Speakers ??= new();
        settings.Scenes ??= new();

        if (window is not null && !string.IsNullOrWhiteSpace(window.SceneId))
        {
            var scene = settings.Scenes.FirstOrDefault(item =>
                string.Equals(item.Id, window.SceneId, StringComparison.OrdinalIgnoreCase));
            if (scene?.Actions?.Any(action => action.IncludeInPlayback && !string.IsNullOrWhiteSpace(action.SpeakerIp)) == true)
            {
                return scene.Actions
                    .Where(action => action.IncludeInPlayback && !string.IsNullOrWhiteSpace(action.SpeakerIp))
                    .Select(action => action.SpeakerIp.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (scene?.SpeakerIps?.Any(ip => !string.IsNullOrWhiteSpace(ip)) == true)
            {
                return scene.SpeakerIps
                    .Where(ip => !string.IsNullOrWhiteSpace(ip))
                    .Select(ip => ip.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return settings.Speakers
            .Select(speaker => speaker.IpAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
                startingVolumes[ip] = await sonosConnectorRepo.GetVolume(ip);
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
                targetVolumes[ip] = await sonosConnectorRepo.GetVolume(ip);
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
