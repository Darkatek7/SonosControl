using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public interface IDeviceHealthSnapshotStore
{
    IReadOnlyList<DeviceHealthStatus> GetSnapshot();
    void Replace(IEnumerable<DeviceHealthStatus> statuses);
}

public sealed class DeviceHealthSnapshotStore : IDeviceHealthSnapshotStore
{
    private readonly object _sync = new();
    private IReadOnlyList<DeviceHealthStatus> _snapshot = Array.Empty<DeviceHealthStatus>();

    public IReadOnlyList<DeviceHealthStatus> GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot.Select(Clone).ToList();
        }
    }

    public void Replace(IEnumerable<DeviceHealthStatus> statuses)
    {
        lock (_sync)
        {
            _snapshot = statuses.Select(Clone).ToList();
        }
    }

    private static DeviceHealthStatus Clone(DeviceHealthStatus status)
    {
        return new DeviceHealthStatus
        {
            SpeakerIp = status.SpeakerIp,
            SpeakerName = status.SpeakerName,
            SpeakerUuid = status.SpeakerUuid,
            IsOnline = status.IsOnline,
            IsPlaying = status.IsPlaying,
            CurrentVolume = status.CurrentVolume,
            LastSeenUtc = status.LastSeenUtc,
            LastCheckedUtc = status.LastCheckedUtc,
            ConsecutiveFailures = status.ConsecutiveFailures,
            LastError = status.LastError,
            LastLatencyMs = status.LastLatencyMs
        };
    }
}

public sealed class DeviceHealthMonitorService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceHealthSnapshotStore _snapshotStore;
    private readonly ILogger<DeviceHealthMonitorService> _logger;
    private readonly TimeProvider _timeProvider;

    public DeviceHealthMonitorService(
        IServiceScopeFactory scopeFactory,
        IDeviceHealthSnapshotStore snapshotStore,
        ILogger<DeviceHealthMonitorService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _snapshotStore = snapshotStore;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeviceHealthMonitorService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshStatusesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Device health monitor cycle failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RefreshStatusesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var actionLogger = scope.ServiceProvider.GetRequiredService<ActionLogger>();

        var settings = await uow.ISettingsRepo.GetSettings();
        if (settings?.Speakers is null || settings.Speakers.Count == 0)
        {
            _snapshotStore.Replace(Array.Empty<DeviceHealthStatus>());
            return;
        }

        var previous = _snapshotStore.GetSnapshot().ToDictionary(s => s.SpeakerIp, StringComparer.OrdinalIgnoreCase);
        var currentStatuses = new List<DeviceHealthStatus>();

        foreach (var speaker in settings.Speakers)
        {
            var status = await ProbeSpeakerAsync(speaker, uow, previous, cancellationToken);
            currentStatuses.Add(status);

            if (previous.TryGetValue(status.SpeakerIp, out var oldStatus)
                && oldStatus.IsOnline
                && !status.IsOnline)
            {
                await actionLogger.LogAsync("DeviceOffline", $"{status.SpeakerName} ({status.SpeakerIp}) is offline.");
            }
        }

        _snapshotStore.Replace(currentStatuses);
    }

    private async Task<DeviceHealthStatus> ProbeSpeakerAsync(
        SonosSpeaker speaker,
        IUnitOfWork uow,
        IReadOnlyDictionary<string, DeviceHealthStatus> previous,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var status = new DeviceHealthStatus
        {
            SpeakerIp = speaker.IpAddress,
            SpeakerName = speaker.Name,
            SpeakerUuid = speaker.Uuid,
            LastCheckedUtc = now
        };

        if (previous.TryGetValue(speaker.IpAddress, out var prior))
        {
            status.ConsecutiveFailures = prior.ConsecutiveFailures;
            status.LastSeenUtc = prior.LastSeenUtc;
        }
        else
        {
            status.LastSeenUtc = now;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var playingTask = uow.ISonosConnectorRepo.IsPlaying(speaker.IpAddress);
            var volumeTask = uow.ISonosConnectorRepo.GetVolume(speaker.IpAddress);
            var uuidTask = string.IsNullOrWhiteSpace(speaker.Uuid)
                ? uow.ISonosConnectorRepo.GetSpeakerUUID(speaker.IpAddress, cancellationToken)
                : Task.FromResult<string?>(speaker.Uuid);

            await Task.WhenAll(playingTask, volumeTask, uuidTask);

            status.IsOnline = true;
            status.IsPlaying = playingTask.Result;
            status.CurrentVolume = volumeTask.Result;
            status.SpeakerUuid = uuidTask.Result ?? speaker.Uuid;
            status.ConsecutiveFailures = 0;
            status.LastSeenUtc = now;
            status.LastError = null;
        }
        catch (Exception ex)
        {
            status.IsOnline = false;
            status.IsPlaying = false;
            status.CurrentVolume = null;
            status.ConsecutiveFailures = status.ConsecutiveFailures + 1;
            status.LastError = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            status.LastLatencyMs = stopwatch.ElapsedMilliseconds;
        }

        return status;
    }
}
