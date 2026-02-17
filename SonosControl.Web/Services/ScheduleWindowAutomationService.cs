using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using Microsoft.Extensions.DependencyInjection;

namespace SonosControl.Web.Services;

public sealed class ScheduleWindowAutomationService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduleWindowAutomationService> _logger;
    private readonly TimeProvider _timeProvider;

    private string? _activeWindowId;
    private DateTime _activeWindowAppliedUtc;

    public ScheduleWindowAutomationService(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduleWindowAutomationService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduleWindowAutomationService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateWindowsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Schedule window evaluation failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task EvaluateWindowsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sceneOrchestration = scope.ServiceProvider.GetRequiredService<ISceneOrchestrationService>();
        var actionLogger = scope.ServiceProvider.GetRequiredService<ActionLogger>();

        var settings = await uow.ISettingsRepo.GetSettings();
        if (settings is null)
        {
            return;
        }

        settings.ScheduleWindows ??= new();
        settings.Scenes ??= new();

        var now = _timeProvider.GetLocalNow();
        var activeWindow = ScheduleWindowEvaluator.SelectActiveWindow(settings.ScheduleWindows, now);

        if (activeWindow is null)
        {
            if (!string.IsNullOrWhiteSpace(_activeWindowId))
            {
                var oldWindow = settings.ScheduleWindows.FirstOrDefault(w => w.Id == _activeWindowId);
                await StopWindowPlaybackAsync(oldWindow, settings, uow, actionLogger, cancellationToken);
                _activeWindowId = null;
                _activeWindowAppliedUtc = default;
            }

            return;
        }

        var shouldApply = !string.Equals(_activeWindowId, activeWindow.Id, StringComparison.OrdinalIgnoreCase)
                          || activeWindow.LastModifiedUtc > _activeWindowAppliedUtc;
        if (!shouldApply)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_activeWindowId)
            && !string.Equals(_activeWindowId, activeWindow.Id, StringComparison.OrdinalIgnoreCase))
        {
            var oldWindow = settings.ScheduleWindows.FirstOrDefault(w => w.Id == _activeWindowId);
            var newWindowTargets = ResolveWindowTargetSpeakers(activeWindow, settings);
            await StopWindowPlaybackAsync(oldWindow, settings, uow, actionLogger, cancellationToken, newWindowTargets);
        }

        if (!string.IsNullOrWhiteSpace(activeWindow.SceneId))
        {
            var result = await sceneOrchestration.ApplySceneByIdAsync(activeWindow.SceneId, performedBy: "automation-scheduler", cancellationToken);
            if (result.Success)
            {
                if (activeWindow.FadeInSeconds > 0 && result.TargetSpeakers.Count > 0)
                {
                    await ApplyFadeInAsync(result.TargetSpeakers, activeWindow.FadeInSeconds, uow, cancellationToken);
                }

                await actionLogger.LogAsync("ScheduleTriggered", $"Window '{activeWindow.Name}' triggered scene '{activeWindow.SceneId}'.");
            }
            else
            {
                await actionLogger.LogAsync("ScheduleTriggerFailed", $"Window '{activeWindow.Name}' failed: {result.Message}");
            }
        }
        else
        {
            await actionLogger.LogAsync("ScheduleTriggered", $"Window '{activeWindow.Name}' became active with no scene assigned.");
        }

        _activeWindowId = activeWindow.Id;
        _activeWindowAppliedUtc = _timeProvider.GetUtcNow().UtcDateTime;
    }

    private async Task StopWindowPlaybackAsync(
        ScheduleWindow? window,
        SonosSettings settings,
        IUnitOfWork uow,
        ActionLogger actionLogger,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludeSpeakerIps = null)
    {
        var targetSpeakers = ResolveWindowTargetSpeakers(window, settings).ToList();

        if (excludeSpeakerIps is not null && excludeSpeakerIps.Count > 0)
        {
            targetSpeakers = targetSpeakers
                .Where(ip => !excludeSpeakerIps.Contains(ip, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        if (!targetSpeakers.Any())
        {
            return;
        }

        if ((window?.FadeOutSeconds ?? 0) > 0)
        {
            await ApplyFadeOutAsync(targetSpeakers, window!.FadeOutSeconds, uow, cancellationToken);
        }

        await Task.WhenAll(targetSpeakers.Select(ip => uow.ISonosConnectorRepo.PausePlaying(ip)));
        await actionLogger.LogAsync("ScheduleWindowStopped", window is null
            ? "Scheduler window stopped."
            : $"Window '{window.Name}' stopped.");
    }

    private static IReadOnlyCollection<string> ResolveWindowTargetSpeakers(ScheduleWindow? window, SonosSettings settings)
    {
        settings.Speakers ??= new();
        settings.Scenes ??= new();

        if (window is not null && !string.IsNullOrWhiteSpace(window.SceneId))
        {
            var scene = settings.Scenes.FirstOrDefault(s => string.Equals(s.Id, window.SceneId, StringComparison.OrdinalIgnoreCase));
            if (scene is not null)
            {
                if (scene.Actions?.Any(a => a.IncludeInPlayback && !string.IsNullOrWhiteSpace(a.SpeakerIp)) == true)
                {
                    return scene.Actions
                        .Where(a => a.IncludeInPlayback && !string.IsNullOrWhiteSpace(a.SpeakerIp))
                        .Select(a => a.SpeakerIp.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                if (scene.SpeakerIps?.Any(ip => !string.IsNullOrWhiteSpace(ip)) == true)
                {
                    return scene.SpeakerIps
                        .Where(ip => !string.IsNullOrWhiteSpace(ip))
                        .Select(ip => ip.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
        }

        return settings.Speakers
            .Select(s => s.IpAddress)
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

            var volumeTasks = targetSpeakers.Select(ip =>
            {
                var start = startingVolumes.TryGetValue(ip, out var value) ? value : 10;
                var next = Math.Max(0, (int)Math.Round(start * ratio));
                return uow.ISonosConnectorRepo.SetSpeakerVolume(ip, next, cancellationToken);
            });

            await Task.WhenAll(volumeTasks);
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

        await Task.WhenAll(targetSpeakers.Select(ip => uow.ISonosConnectorRepo.SetSpeakerVolume(ip, 1, cancellationToken)));

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ratio = (double)step / steps;

            var tasks = targetSpeakers.Select(ip =>
            {
                var target = targetVolumes.TryGetValue(ip, out var value) ? value : 15;
                var next = Math.Max(1, (int)Math.Round(target * ratio));
                return uow.ISonosConnectorRepo.SetSpeakerVolume(ip, next, cancellationToken);
            });

            await Task.WhenAll(tasks);
            await Task.Delay(delay, cancellationToken);
        }
    }
}
