using Microsoft.AspNetCore.Components.Authorization;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services;

public sealed class PlaybackUiStateService : IDisposable
{
    private readonly ISettingsRepo _settingsRepo;
    private readonly ISonosConnectorRepo _sonosConnectorRepo;
    private readonly ILogger<PlaybackUiStateService> _logger;
    private readonly ApplicationDbContext _db;
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly INotificationService _notificationService;
    private readonly ManualPlaybackService? _manualPlayback;
    private readonly PlaybackSnapshotStore? _snapshotStore;
    private readonly PlaybackSnapshotMonitorService? _snapshotMonitor;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _volumeUpdateLock = new(1, 1);
    private readonly SemaphoreSlim _logLock = new(1, 1);
    private readonly object _volumeSync = new();
    private CancellationTokenSource? _volumeDebounceCts;
    private long _volumeGeneration;
    private long _activeSpeakerGeneration;
    private bool _subscribed;
    private bool _initialized;
    private bool _disposed;
    private SynchronizationContext? _syncContext;

    private SonosSettings? _settings;
    private SonosTrackInfo? _trackInfo;
    private string? _currentStationUrl;
    private PlaybackRetryCommand? _retryCommand;
    private string? _retrySpeakerIp;

    public PlaybackUiStateService(
        ISettingsRepo settingsRepo, ISonosConnectorRepo sonosConnectorRepo,
        ILogger<PlaybackUiStateService> logger,
        ApplicationDbContext db,
        AuthenticationStateProvider authenticationStateProvider,
        INotificationService notificationService,
        ManualPlaybackService? manualPlayback = null,
        PlaybackSnapshotStore? snapshotStore = null,
        PlaybackSnapshotMonitorService? snapshotMonitor = null)
    {
        _settingsRepo = settingsRepo;
        _sonosConnectorRepo = sonosConnectorRepo;
        _logger = logger;
        _db = db;
        _authenticationStateProvider = authenticationStateProvider;
        _notificationService = notificationService;
        _manualPlayback = manualPlayback;
        _snapshotStore = snapshotStore;
        _snapshotMonitor = snapshotMonitor;
    }

    public event Action? StateChanged;

    public IReadOnlyList<SonosSpeaker> Speakers => _settings?.Speakers ?? new List<SonosSpeaker>();
    public string ActiveSpeakerIp { get; private set; } = "";
    public string ActiveSpeakerName { get; private set; } = "No speaker";
    public string CurrentStationDisplay { get; private set; } = "No source";
    public string CurrentTrack { get; private set; } = "No metadata available";
    private string? _trackArtUrl;
    private string? _failedArtUrl;
    public string? CurrentTrackArtUrl => _trackArtUrl == _failedArtUrl ? null : _trackArtUrl;

    public void ReportArtworkFailure(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url != _trackArtUrl) return;
        _failedArtUrl = url;
        NotifyStateChanged();
    }
    public bool IsPlaying { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsSkipping { get; private set; }
    public bool IsSyncing { get; private set; }
    public bool IsStale { get; private set; }
    public bool HasActiveSpeaker => !string.IsNullOrWhiteSpace(ActiveSpeakerIp);
    public string? PlaybackErrorMessage { get; private set; }
    public int Volume { get; private set; }
    public int MaxVolume { get; private set; } = 100;
    public DateTime? LastSuccessfulRefreshUtc { get; private set; }
    public IReadOnlyList<PlaybackSnapshot> PlaybackSnapshots => _snapshotStore?.GetAll() ?? Array.Empty<PlaybackSnapshot>();

    public async Task InitializeAsync()
    {
        await _initializeLock.WaitAsync();
        try
        {
            var refreshPlayback = !_initialized;
            await InitializeCoreAsync(refreshPlayback);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    private async Task InitializeCoreAsync(bool forcePlaybackRefresh)
    {
        _syncContext ??= SynchronizationContext.Current;
        if (!_subscribed && _snapshotStore is not null)
        {
            _snapshotStore.Changed += HandleSnapshotsChanged;
            _snapshotStore.SettingsChanged += HandleSettingsChanged;
            _subscribed = true;
        }
        _settings = await _settingsRepo.GetSettings() ?? new SonosSettings();
        _settings.YouTubeCollections ??= new List<YouTubeObject>();
        _settings.YouTubeMusicCollections ??= new List<YouTubeMusicObject>();

        var configuredIp = _settings.IP_Adress;
        var activeSpeaker = _settings.Speakers.FirstOrDefault(s => s.IpAddress == configuredIp)
            ?? _settings.Speakers.FirstOrDefault();

        if (activeSpeaker is not null)
        {
            if (!string.Equals(ActiveSpeakerIp, activeSpeaker.IpAddress, StringComparison.OrdinalIgnoreCase))
            {
                CancelPendingVolumeUpdate();
                Interlocked.Increment(ref _activeSpeakerGeneration);
                ClearPlaybackSnapshot();
            }
            ActiveSpeakerIp = activeSpeaker.IpAddress;
            ActiveSpeakerName = activeSpeaker.Name;
            _settings.IP_Adress = activeSpeaker.IpAddress;
        }
        else
        {
            CancelPendingVolumeUpdate();
            if (!string.IsNullOrWhiteSpace(ActiveSpeakerIp)) Interlocked.Increment(ref _activeSpeakerGeneration);
            ActiveSpeakerIp = "";
            ActiveSpeakerName = "No speaker";
            ClearPlaybackError();
            ClearPlaybackSnapshot();
        }

        MaxVolume = Math.Clamp(_settings.MaxVolume, 0, 100);
        Volume = Math.Clamp(_settings.Volume, 0, MaxVolume);
        NotifyStateChanged();
        HandleSnapshotsChanged();
        if (forcePlaybackRefresh || _snapshotStore?.Get(ActiveSpeakerIp) is not { IsStale: false })
            await RefreshAsync();
    }

    public async Task SetActiveSpeakerAsync(string speakerIp)
    {
        if (string.IsNullOrWhiteSpace(speakerIp) || ActiveSpeakerIp == speakerIp)
        {
            return;
        }

        CancelPendingVolumeUpdate();
        ClearPlaybackError();
        _settings ??= await _settingsRepo.GetSettings() ?? new SonosSettings();
        var speaker = _settings.Speakers.FirstOrDefault(s => s.IpAddress == speakerIp);
        ActiveSpeakerIp = speakerIp;
        Interlocked.Increment(ref _activeSpeakerGeneration);
        ClearPlaybackSnapshot();
        ActiveSpeakerName = speaker?.Name ?? speakerIp;
        _settings = await _settingsRepo.UpdateSettingsAsync(current => current.IP_Adress = speakerIp);
        NotifyStateChanged();
        HandleSnapshotsChanged();
        await RefreshAsync();
    }

    public Task TogglePlaybackAsync() => SetPlaybackAsync(!IsPlaying);

    private async Task SetPlaybackAsync(bool shouldPlay)
    {
        if (!HasActiveSpeaker || IsLoading || IsSkipping)
        {
            return;
        }

        var speakerIp = ActiveSpeakerIp;
        ClearPlaybackError();
        IsLoading = true;
        NotifyStateChanged();

        try
        {
            await using var manualLease = _manualPlayback is null ? null : await _manualPlayback.BeginAsync(speakerIp);
            if (!shouldPlay)
            {
                await _sonosConnectorRepo.PausePlaying(speakerIp);
            }
            else
            {
                await _sonosConnectorRepo.StartPlaying(speakerIp);
                if (_manualPlayback is not null) await _manualPlayback.PlaybackSucceededAsync([speakerIp]);
            }

            await TryLogPlaybackCommandAsync(shouldPlay ? "Playback Started" : "Playback Paused", speakerIp);

            var stillActive = string.Equals(speakerIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase);
            if (stillActive || _snapshotMonitor is not null) await RefreshSharedPlaybackAsync();
            if (string.Equals(speakerIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase) && _snapshotMonitor is null)
            {
                IsPlaying = shouldPlay;
                IsStale = false;
                LastSuccessfulRefreshUtc = DateTime.UtcNow;
                PublishCurrentSnapshot();
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(speakerIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
            {
                IsStale = true;
                PublishCurrentSnapshot(error: "Playback status unavailable");
            }
            SetPlaybackError(
                shouldPlay ? "Playback could not be started. Check the speaker connection and try again." : "Playback could not be paused. Check the speaker connection and try again.",
                shouldPlay ? PlaybackRetryCommand.Play : PlaybackRetryCommand.Pause,
                speakerIp);
            _logger.LogWarning(ex, "Failed to toggle playback for {SpeakerIp}", ActiveSpeakerIp);
        }
        finally
        {
            IsLoading = false;
            NotifyStateChanged();
        }
    }

    public async Task SetVolumeAsync(int volume)
    {
        if (_settings is null || string.IsNullOrWhiteSpace(ActiveSpeakerIp))
        {
            return;
        }

        var clamped = Math.Clamp(volume, 0, MaxVolume);
        var requestedVolume = clamped;
        Volume = clamped;
        NotifyStateChanged();

        CancellationToken token;
        long generation;
        string targetSpeakerIp;
        lock (_volumeSync)
        {
            generation = ++_volumeGeneration;
            targetSpeakerIp = ActiveSpeakerIp;
            _volumeDebounceCts?.Cancel();
            _volumeDebounceCts?.Dispose();
            _volumeDebounceCts = new CancellationTokenSource();
            token = _volumeDebounceCts.Token;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(160), token);
            await _volumeUpdateLock.WaitAsync(token);
            try
            {
                if (generation != Interlocked.Read(ref _volumeGeneration)
                    || !string.Equals(targetSpeakerIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var latest = await _settingsRepo.GetSettings();
                var finalVolume = Math.Clamp(requestedVolume, 0, Math.Clamp(latest?.MaxVolume ?? MaxVolume, 0, 100));
                await using (var volumeLease = _manualPlayback is null ? null : await _manualPlayback.BeginVolumeAsync(targetSpeakerIp, token))
                {
                    await _sonosConnectorRepo.SetVolume(targetSpeakerIp, finalVolume);
                    await TryLogPlaybackCommandAsync("Volume Changed", $"{targetSpeakerIp}: {finalVolume}% (playback ownership unchanged)");
                    await RefreshSharedPlaybackAsync(token);
                }

                if (generation == Interlocked.Read(ref _volumeGeneration)
                    && string.Equals(targetSpeakerIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
                {
                    _settings = await _settingsRepo.UpdateSettingsAsync(current => current.Volume = finalVolume);
                    MaxVolume = Math.Clamp(_settings.MaxVolume, 0, 100);
                    if (_snapshotMonitor is null)
                    {
                        Volume = Math.Clamp(_settings.Volume, 0, MaxVolume);
                        IsStale = false;
                        PublishCurrentSnapshot();
                    }
                    else
                    {
                        ApplyLatestSnapshot();
                        if (!IsStale)
                        {
                            Volume = Math.Clamp(finalVolume, 0, MaxVolume);
                            PublishCurrentSnapshot();
                        }
                    }
                }
            }
            finally
            {
                _volumeUpdateLock.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer slider value superseded this device call.
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _volumeGeneration)
                && string.Equals(targetSpeakerIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
            {
                IsStale = true;
                PublishCurrentSnapshot(error: "Playback status unavailable");
            }
            _logger.LogWarning(ex, "Failed to set volume for {SpeakerIp}", ActiveSpeakerIp);
            NotifyStateChanged();
        }
    }

    private void CancelPendingVolumeUpdate()
    {
        lock (_volumeSync)
        {
            ++_volumeGeneration;
            _volumeDebounceCts?.Cancel();
            _volumeDebounceCts?.Dispose();
            _volumeDebounceCts = null;
        }
    }

    public async Task SkipNextAsync()
    {
        if (!HasActiveSpeaker || IsSkipping || IsLoading)
        {
            return;
        }

        var speakerIp = ActiveSpeakerIp;
        ClearPlaybackError();
        IsSkipping = true;
        NotifyStateChanged();

        try
        {
            await using var manualLease = _manualPlayback is null ? null : await _manualPlayback.BeginAsync(speakerIp);
            await _sonosConnectorRepo.NextTrack(speakerIp);
            await TryLogPlaybackCommandAsync("Next Track", speakerIp);
            var stillActive = string.Equals(speakerIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase);
            if (stillActive || _snapshotMonitor is not null)
            {
                await RefreshSharedPlaybackAsync();
                if (stillActive && _snapshotMonitor is null) IsStale = false;
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(speakerIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
            {
                IsStale = true;
                PublishCurrentSnapshot(error: "Playback status unavailable");
            }
            SetPlaybackError("The next track could not be played. Check the speaker connection and try again.", PlaybackRetryCommand.Skip, speakerIp);
            _logger.LogWarning(ex, "Failed to skip to the next track for {SpeakerIp}", ActiveSpeakerIp);
        }
        finally
        {
            IsSkipping = false;
            NotifyStateChanged();
        }
    }

    public Task RetryLastPlaybackCommandAsync()
    {
        if (!HasActiveSpeaker || IsLoading || IsSkipping || _retrySpeakerIp != ActiveSpeakerIp)
        {
            return Task.CompletedTask;
        }

        return _retryCommand switch
        {
            PlaybackRetryCommand.Play => SetPlaybackAsync(true),
            PlaybackRetryCommand.Pause => SetPlaybackAsync(false),
            PlaybackRetryCommand.Skip => SkipNextAsync(),
            _ => Task.CompletedTask
        };
    }

    private void SetPlaybackError(string message, PlaybackRetryCommand command, string speakerIp)
    {
        if (speakerIp != ActiveSpeakerIp) return;
        PlaybackErrorMessage = message;
        _retryCommand = command;
        _retrySpeakerIp = speakerIp;
    }

    private void ClearPlaybackError()
    {
        PlaybackErrorMessage = null;
        _retryCommand = null;
        _retrySpeakerIp = null;
    }

    private enum PlaybackRetryCommand { Play, Pause, Skip }

    public async Task<PlaybackCommandResult> SyncPlayAsync()
    {
        if (IsSyncing)
        {
            return new PlaybackCommandResult(false, "Sync Play is already running.", true);
        }

        if (_settings is null)
        {
            _settings = await _settingsRepo.GetSettings() ?? new SonosSettings();
        }

        if (_settings.Speakers is null || !_settings.Speakers.Any() || string.IsNullOrWhiteSpace(ActiveSpeakerIp))
        {
            return new PlaybackCommandResult(false, "No active speaker available for Sync Play.", true);
        }

        var masterIp = ActiveSpeakerIp;
        IsSyncing = true;
        NotifyStateChanged();

        try
        {
            if (!await IsSpeakerReachableAsync(masterIp))
            {
                return new PlaybackCommandResult(false, "Selected master speaker is offline.", true);
            }

            var allSlaveIps = _settings.Speakers
                .Where(s => !string.Equals(s.IpAddress, masterIp, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.IpAddress)
                .ToList();

            var reachableSlaveIps = await FilterReachableSpeakerIpsAsync(allSlaveIps);
            var targets = new[] { masterIp }.Concat(reachableSlaveIps).ToArray();
            await using (var manualLease = _manualPlayback is null ? null : await _manualPlayback.BeginTargetsAsync(targets))
            {
                await ApplyLegacySyncAsync(masterIp, reachableSlaveIps);
                // The master keeps playing untouched; only the slaves were actually started, so only they
                // get a new grace period. Re-registering the master would push its stop deadline out.
                if (_manualPlayback is not null && reachableSlaveIps.Count > 0)
                    await _manualPlayback.PlaybackSucceededAsync(reachableSlaveIps);
            }

            var skippedCount = allSlaveIps.Count - reachableSlaveIps.Count;
            var details = skippedCount > 0
                ? $"Reachable slaves: {string.Join(", ", reachableSlaveIps)}. Skipped offline: {skippedCount}."
                : "All reachable speakers";

            await AddLogAsync("Sync Play Started (Cloned URI)", details);
            await _notificationService.SendNotificationAsync(
                "Sync Play triggered for all speakers (Legacy sync)",
                await GetCurrentUserAsync());

            await RefreshSharedPlaybackAsync();

            return skippedCount > 0
                ? new PlaybackCommandResult(true, $"Sync Play started. Skipped {skippedCount} offline speaker(s).")
                : new PlaybackCommandResult(true, "Sync Play started for all speakers.");
        }
        catch (Exception ex)
        {
            if (string.Equals(masterIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
            {
                IsStale = true;
                PublishCurrentSnapshot(error: "Playback status unavailable");
            }
            _logger.LogWarning(ex, "Sync Play failed for {SpeakerIp}", masterIp);
            return new PlaybackCommandResult(false, "Sync Play failed. Please retry.", true);
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged();
        }
    }

    public async Task RefreshAsync(bool reloadSettings = true)
    {
        if (string.IsNullOrWhiteSpace(ActiveSpeakerIp))
        {
            NotifyStateChanged();
            return;
        }

        await _refreshLock.WaitAsync();
        var targetIp = ActiveSpeakerIp;
        var generation = Interlocked.Read(ref _activeSpeakerGeneration);
        if (string.IsNullOrWhiteSpace(targetIp))
        {
            _refreshLock.Release();
            NotifyStateChanged();
            return;
        }

        IsLoading = true;
        NotifyStateChanged();
        var snapshotGeneration = _snapshotStore?.BeginProbe(targetIp) ?? 0;

        try
        {
            if (_snapshotMonitor is not null)
            {
                await _snapshotMonitor.RefreshNowAsync();
                if (generation == Interlocked.Read(ref _activeSpeakerGeneration)
                    && string.Equals(targetIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
                    ApplyLatestSnapshot();
                return;
            }

            if (reloadSettings || _settings is null)
                _settings = await _settingsRepo.GetSettings() ?? new SonosSettings();
            _settings.YouTubeCollections ??= [];
            _settings.YouTubeMusicCollections ??= [];
            MaxVolume = Math.Clamp(_settings.MaxVolume, 0, 100);
            ActiveSpeakerName = _settings.Speakers.FirstOrDefault(s => s.IpAddress == targetIp)?.Name ?? targetIp;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var volumeTask = _sonosConnectorRepo.GetVolume(targetIp, timeout.Token);
            var stationTask = _sonosConnectorRepo.GetCurrentStationAsync(targetIp, timeout.Token);
            var trackInfoTask = _sonosConnectorRepo.GetTrackInfoAsync(targetIp, timeout.Token);
            var playingTask = _sonosConnectorRepo.IsPlaying(targetIp, timeout.Token);

            await Task.WhenAll(volumeTask, stationTask, trackInfoTask, playingTask).WaitAsync(timeout.Token);

            if (generation != Interlocked.Read(ref _activeSpeakerGeneration)
                || !string.Equals(targetIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase)
                || (_snapshotStore is not null && !_snapshotStore.IsCurrentProbe(targetIp, snapshotGeneration))) return;
            Volume = Math.Clamp(volumeTask.Result, 0, MaxVolume);
            _settings.Volume = Volume;
            CurrentStationDisplay = ResolveStationDisplay(stationTask.Result);
            _currentStationUrl = stationTask.Result;
            _trackInfo = trackInfoTask.Result;
            ApplyTrackInfo(trackInfoTask.Result);
            IsPlaying = playingTask.Result;
            LastSuccessfulRefreshUtc = DateTime.UtcNow;
            IsStale = false;
            PublishCurrentSnapshot(snapshotGeneration);
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _activeSpeakerGeneration)
                && string.Equals(targetIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase)
                && (_snapshotStore is null || _snapshotStore.IsCurrentProbe(targetIp, snapshotGeneration)))
            {
                IsStale = true;
                PublishCurrentSnapshot(snapshotGeneration, "Playback status unavailable");
            }
            _logger.LogDebug(ex, "Failed to refresh playback UI state for {SpeakerIp}", targetIp);
        }
        finally
        {
            IsLoading = false;
            _refreshLock.Release();
            NotifyStateChanged();
        }
    }

    private async Task RefreshSharedPlaybackAsync(CancellationToken cancellationToken = default)
    {
        if (_snapshotMonitor is not null)
        {
            await _snapshotMonitor.RefreshNowAsync(cancellationToken);
            return;
        }
        await RefreshAsync(reloadSettings: false);
    }

    private void HandleSnapshotsChanged()
    {
        if (_disposed) return;
        if (_syncContext is { } context && !ReferenceEquals(SynchronizationContext.Current, context))
        {
            context.Post(_ => ApplyLatestSnapshot(), null);
            return;
        }
        ApplyLatestSnapshot();
    }

    private void ApplyLatestSnapshot()
    {
        var snapshot = _snapshotStore?.Get(ActiveSpeakerIp);
        if (snapshot is null) { ClearPlaybackSnapshot(); NotifyStateChanged(); return; }
        ActiveSpeakerName = snapshot.SpeakerName;
        IsPlaying = snapshot.IsPlaying;
        if (snapshot.Volume is int volume) Volume = Math.Clamp(volume, 0, MaxVolume);
        _currentStationUrl = snapshot.CurrentStationUrl;
        CurrentStationDisplay = ResolveStationDisplay(snapshot.CurrentStationUrl);
        _trackInfo = snapshot.TrackInfo;
        ApplyTrackInfo(snapshot.TrackInfo);
        IsStale = snapshot.IsStale;
        LastSuccessfulRefreshUtc = snapshot.LastSuccessfulRefreshUtc?.UtcDateTime;
        NotifyStateChanged();
    }

    private void ClearPlaybackSnapshot()
    {
        IsPlaying = false;
        IsStale = false;
        CurrentStationDisplay = "No source";
        CurrentTrack = "No metadata available";
        _currentStationUrl = null;
        _trackInfo = null;
        _trackArtUrl = null;
        _failedArtUrl = null;
        LastSuccessfulRefreshUtc = null;
    }

    private void PublishCurrentSnapshot(long? generation = null, string? error = null)
    {
        if (_snapshotStore is null || string.IsNullOrWhiteSpace(ActiveSpeakerIp)) return;
        var currentGeneration = generation ?? _snapshotStore.BeginProbe(ActiveSpeakerIp);
        _snapshotStore.TrySet(new PlaybackSnapshot(ActiveSpeakerIp, ActiveSpeakerName, IsPlaying, Volume,
            _currentStationUrl, _trackInfo, IsStale, DateTimeOffset.UtcNow,
            LastSuccessfulRefreshUtc is { } successful ? new DateTimeOffset(successful, TimeSpan.Zero) : null, error), currentGeneration);
    }

    private void HandleSettingsChanged()
    {
        if (_disposed || !_initialized) return;
        if (_syncContext is { } context && !ReferenceEquals(SynchronizationContext.Current, context))
        {
            context.Post(_ => _ = RefreshSettingsAsync(), null);
            return;
        }
        _ = RefreshSettingsAsync();
    }

    private async Task RefreshSettingsAsync()
    {
        try
        {
            _settings = await _settingsRepo.GetSettings() ?? new SonosSettings();
            _settings.YouTubeCollections ??= [];
            _settings.YouTubeMusicCollections ??= [];
            MaxVolume = Math.Clamp(_settings.MaxVolume, 0, 100);
            _settings.Speakers ??= [];
            var active = _settings.Speakers.FirstOrDefault(speaker => string.Equals(speaker.IpAddress, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase));
            if (active is null)
            {
                active = _settings.Speakers.FirstOrDefault(speaker => string.Equals(speaker.IpAddress, _settings.IP_Adress, StringComparison.OrdinalIgnoreCase))
                    ?? _settings.Speakers.FirstOrDefault();
                var nextIp = active?.IpAddress ?? string.Empty;
                if (!string.Equals(nextIp, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
                {
                    CancelPendingVolumeUpdate();
                    Interlocked.Increment(ref _activeSpeakerGeneration);
                    ActiveSpeakerIp = nextIp;
                    Volume = Math.Clamp(_settings.Volume, 0, MaxVolume);
                    ClearPlaybackSnapshot();
                }
            }
            ActiveSpeakerName = active?.Name ?? "No speaker";
            HandleSnapshotsChanged();
            if (active is not null && _snapshotStore?.Get(active.IpAddress) is null) await RefreshAsync();
            else if (active is null) NotifyStateChanged();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not refresh playback UI settings."); }
    }

    public void Dispose()
    {
        _disposed = true;
        if (!_subscribed || _snapshotStore is null) return;
        _snapshotStore.Changed -= HandleSnapshotsChanged;
        _snapshotStore.SettingsChanged -= HandleSettingsChanged;
        _subscribed = false;
    }

    private string ResolveStationDisplay(string? currentStationUrl)
    {
        if (string.IsNullOrWhiteSpace(currentStationUrl))
        {
            return "No source";
        }

        var allSources = (_settings?.Stations ?? new List<TuneInStation>())
            .Select(s => (s.Name, s.Url))
            .Concat((_settings?.SpotifyTracks ?? new List<SpotifyObject>()).Select(s => (s.Name, s.Url)))
            .Concat((_settings?.YouTubeCollections ?? new List<YouTubeObject>()).Select(s => (s.Name, s.Url)))
            .Concat((_settings?.YouTubeMusicCollections ?? new List<YouTubeMusicObject>()).Select(s => (s.Name, s.Url)));

        foreach (var source in allSources)
        {
            if (IsSameStoredMedia(currentStationUrl, source.Url))
            {
                return source.Name;
            }
        }

        if (currentStationUrl.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            return "Source unavailable";
        }

        return currentStationUrl.Length > 44 ? $"{currentStationUrl[..41]}..." : currentStationUrl;
    }

    private static bool IsSameStoredMedia(string currentUri, string? storedUrl)
        => MediaIdentity.Matches(currentUri, storedUrl);

    private void ApplyTrackInfo(SonosTrackInfo? trackInfo)
    {
        if (trackInfo is null || !trackInfo.IsValidMetadata())
        {
            CurrentTrack = "No metadata available";
            _trackArtUrl = null;
            return;
        }

        CurrentTrack = trackInfo.GetDisplayString();
        var artUrl = string.IsNullOrWhiteSpace(trackInfo.AlbumArtUri) ? null : trackInfo.AlbumArtUri;
        if (artUrl != _trackArtUrl && artUrl is not null) _failedArtUrl = null;
        _trackArtUrl = artUrl;
    }

    private async Task<bool> IsSpeakerReachableAsync(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return false;
        }

        var uuid = await _sonosConnectorRepo.GetSpeakerUUID(ip);
        return !string.IsNullOrWhiteSpace(uuid);
    }

    private async Task<List<string>> FilterReachableSpeakerIpsAsync(IEnumerable<string> speakerIps)
    {
        var reachable = new List<string>();

        foreach (var ip in speakerIps.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await IsSpeakerReachableAsync(ip))
            {
                reachable.Add(ip);
            }
        }

        return reachable;
    }

    private async Task ApplyLegacySyncAsync(string masterIp, IEnumerable<string> slaveIps)
    {
        var currentUri = await _sonosConnectorRepo.GetCurrentStationAsync(masterIp);
        if (string.IsNullOrWhiteSpace(currentUri) || currentUri.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not read current media from master speaker {masterIp}.");
        }

        var tasks = slaveIps.Select(async slaveIp =>
        {
            if (currentUri.StartsWith("x-rincon-mp3radio://", StringComparison.OrdinalIgnoreCase)
                || currentUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                await _sonosConnectorRepo.SetTuneInStationAsync(slaveIp, currentUri);
            }

            await _sonosConnectorRepo.StartPlaying(slaveIp);
        });

        await Task.WhenAll(tasks);
    }

    private async Task AddLogAsync(string action, string? details = null)
    {
        await _logLock.WaitAsync();
        try
        {
            var log = new LogEntry
            {
                Action = action,
                PerformedBy = await GetCurrentUserAsync(),
                Timestamp = DateTime.UtcNow,
                Details = details
            };

            _db.Logs.Add(log);
            await _db.SaveChangesAsync();
        }
        finally { _logLock.Release(); }
    }

    private async Task TryLogPlaybackCommandAsync(string action, string details)
    {
        try { await AddLogAsync(action, details); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not audit playback command {Action}.", action); }
    }

    private async Task<string> GetCurrentUserAsync()
    {
        var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
        return authState.User.Identity?.Name ?? "Unknown";
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }
}

public sealed record PlaybackCommandResult(bool Success, string Message, bool IsError = false);
