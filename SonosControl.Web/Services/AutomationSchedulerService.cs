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
    private readonly SemaphoreSlim _evaluationLock = new(1, 1);

    private string? _activeWindowId;
    private DateTime _activeWindowAppliedUtc;

    public AutomationSchedulerService(
        IServiceScopeFactory scopeFactory,
        ISettingsSchemaMigrationService migrationService,
        AutomationRuntimeStatus runtimeStatus,
        ILogger<AutomationSchedulerService> logger,
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null)
    {
        _scopeFactory = scopeFactory;
        _migrationService = migrationService;
        _runtimeStatus = runtimeStatus;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        if (!await _evaluationLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var migration = await _migrationService.MigrateIfRequiredAsync(cancellationToken);
            if (!migration.Success)
            {
                _activeWindowId = null;
                _activeWindowAppliedUtc = default;
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var sceneOrchestration = scope.ServiceProvider.GetRequiredService<ISceneOrchestrationService>();
            var actionLogger = scope.ServiceProvider.GetRequiredService<ActionLogger>();

            var settings = await uow.ISettingsRepo.GetSettings();
            if (settings is null || settings.SettingsSchemaVersion < SonosSettings.CurrentSettingsSchemaVersion)
            {
                return;
            }

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
            var activeWindow = ScheduleWindowEvaluator.SelectActiveWindow(eligibleWindows, localNow);

            if (activeWindow is null)
            {
                if (!string.IsNullOrWhiteSpace(_activeWindowId))
                {
                    var oldWindow = settings.ScheduleWindows.FirstOrDefault(window =>
                        string.Equals(window.Id, _activeWindowId, StringComparison.OrdinalIgnoreCase));
                    await StopWindowPlaybackAsync(oldWindow, settings, uow, actionLogger, cancellationToken);
                    _activeWindowId = null;
                    _activeWindowAppliedUtc = default;
                }

                _runtimeStatus.RecordEvaluation(null, utcNow);
                return;
            }

            var shouldApply = !string.Equals(_activeWindowId, activeWindow.Id, StringComparison.OrdinalIgnoreCase)
                              || activeWindow.LastModifiedUtc > _activeWindowAppliedUtc;
            if (!shouldApply)
            {
                _runtimeStatus.RecordEvaluation(activeWindow.Name, utcNow);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_activeWindowId)
                && !string.Equals(_activeWindowId, activeWindow.Id, StringComparison.OrdinalIgnoreCase))
            {
                var oldWindow = settings.ScheduleWindows.FirstOrDefault(window =>
                    string.Equals(window.Id, _activeWindowId, StringComparison.OrdinalIgnoreCase));
                var newTargets = ResolveWindowTargetSpeakers(activeWindow, settings);
                await StopWindowPlaybackAsync(oldWindow, settings, uow, actionLogger, cancellationToken, newTargets);
            }

            var result = await sceneOrchestration.ApplySceneByIdAsync(
                activeWindow.SceneId!,
                performedBy: "automation-scheduler",
                cancellationToken);

            if (!result.Success)
            {
                var message = $"Schedule '{activeWindow.Name}' could not apply scene: {result.Message}";
                await actionLogger.LogAsync("ScheduleTriggerFailed", message);
                _runtimeStatus.RecordEvaluation(null, utcNow, message);
                return;
            }

            if (activeWindow.FadeInSeconds > 0 && result.TargetSpeakers.Count > 0)
            {
                await ApplyFadeInAsync(result.TargetSpeakers, activeWindow.FadeInSeconds, uow, cancellationToken);
            }

            await actionLogger.LogAsync(
                "ScheduleTriggered",
                $"Schedule '{activeWindow.Name}' triggered scene '{activeWindow.SceneId}'.");

            _activeWindowId = activeWindow.Id;
            _activeWindowAppliedUtc = utcNow.UtcDateTime;
            _runtimeStatus.RecordEvaluation(activeWindow.Name, utcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtimeStatus.RecordEvaluation(null, _timeProvider.GetUtcNow(), ex.Message);
            throw;
        }
        finally
        {
            _evaluationLock.Release();
        }
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
        IUnitOfWork uow,
        ActionLogger actionLogger,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludeSpeakerIps = null)
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

        if ((window?.FadeOutSeconds ?? 0) > 0)
        {
            await ApplyFadeOutAsync(targetSpeakers, window!.FadeOutSeconds, uow, cancellationToken);
        }

        await Task.WhenAll(targetSpeakers.Select(ip => uow.ISonosConnectorRepo.PausePlaying(ip)));
        await actionLogger.LogAsync(
            "ScheduleStopped",
            window is null ? "Scheduled playback stopped." : $"Schedule '{window.Name}' stopped.");
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
        IUnitOfWork uow,
        CancellationToken cancellationToken)
    {
        var steps = Math.Clamp(fadeOutSeconds, 2, 20);
        var delay = TimeSpan.FromSeconds((double)fadeOutSeconds / steps);
        var startingVolumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in targetSpeakers)
        {
            try
            {
                startingVolumes[ip] = await uow.ISonosConnectorRepo.GetVolume(ip);
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
                return uow.ISonosConnectorRepo.SetSpeakerVolume(
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
        IUnitOfWork uow,
        CancellationToken cancellationToken)
    {
        var steps = Math.Clamp(fadeInSeconds, 2, 20);
        var delay = TimeSpan.FromSeconds((double)fadeInSeconds / steps);
        var targetVolumes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ip in targetSpeakers)
        {
            try
            {
                targetVolumes[ip] = await uow.ISonosConnectorRepo.GetVolume(ip);
            }
            catch
            {
                targetVolumes[ip] = 15;
            }
        }

        await Task.WhenAll(targetSpeakers.Select(ip =>
            uow.ISonosConnectorRepo.SetSpeakerVolume(ip, 1, cancellationToken)));

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ratio = (double)step / steps;
            await Task.WhenAll(targetSpeakers.Select(ip =>
            {
                var target = targetVolumes.TryGetValue(ip, out var value) ? value : 15;
                return uow.ISonosConnectorRepo.SetSpeakerVolume(
                    ip,
                    Math.Max(1, (int)Math.Round(target * ratio)),
                    cancellationToken);
            }));
            await Task.Delay(delay, cancellationToken);
        }
    }
}
