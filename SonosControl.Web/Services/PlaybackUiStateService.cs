using Microsoft.AspNetCore.Components.Authorization;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services;

public sealed class PlaybackUiStateService
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<PlaybackUiStateService> _logger;
    private readonly ApplicationDbContext _db;
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly INotificationService _notificationService;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private SonosSettings? _settings;

    public PlaybackUiStateService(
        IUnitOfWork uow,
        ILogger<PlaybackUiStateService> logger,
        ApplicationDbContext db,
        AuthenticationStateProvider authenticationStateProvider,
        INotificationService notificationService)
    {
        _uow = uow;
        _logger = logger;
        _db = db;
        _authenticationStateProvider = authenticationStateProvider;
        _notificationService = notificationService;
    }

    public event Action? StateChanged;

    public IReadOnlyList<SonosSpeaker> Speakers => _settings?.Speakers ?? new List<SonosSpeaker>();
    public string ActiveSpeakerIp { get; private set; } = "";
    public string ActiveSpeakerName { get; private set; } = "No speaker";
    public string CurrentStationDisplay { get; private set; } = "No source";
    public string CurrentTrack { get; private set; } = "No metadata available";
    public string? CurrentTrackArtUrl { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsSyncing { get; private set; }
    public bool IsStale { get; private set; }
    public int Volume { get; private set; }
    public int MaxVolume { get; private set; } = 100;
    public DateTime? LastSuccessfulRefreshUtc { get; private set; }

    public async Task InitializeAsync()
    {
        _settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        _settings.YouTubeMusicCollections ??= new List<YouTubeMusicObject>();

        var configuredIp = _settings.IP_Adress;
        var activeSpeaker = _settings.Speakers.FirstOrDefault(s => s.IpAddress == configuredIp)
            ?? _settings.Speakers.FirstOrDefault();

        if (activeSpeaker is not null)
        {
            ActiveSpeakerIp = activeSpeaker.IpAddress;
            ActiveSpeakerName = activeSpeaker.Name;
            _settings.IP_Adress = activeSpeaker.IpAddress;
        }

        Volume = Math.Clamp(_settings.Volume, 0, MaxVolume);
        MaxVolume = Math.Clamp(_settings.MaxVolume, 0, 100);
        await RefreshAsync();
    }

    public async Task SetActiveSpeakerAsync(string speakerIp)
    {
        if (string.IsNullOrWhiteSpace(speakerIp) || ActiveSpeakerIp == speakerIp)
        {
            return;
        }

        _settings ??= await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        var speaker = _settings.Speakers.FirstOrDefault(s => s.IpAddress == speakerIp);
        ActiveSpeakerIp = speakerIp;
        ActiveSpeakerName = speaker?.Name ?? speakerIp;
        _settings.IP_Adress = speakerIp;
        await _uow.ISettingsRepo.WriteSettings(_settings);
        NotifyStateChanged();
        await RefreshAsync();
    }

    public async Task TogglePlaybackAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveSpeakerIp))
        {
            return;
        }

        IsLoading = true;
        NotifyStateChanged();

        try
        {
            if (IsPlaying)
            {
                await _uow.ISonosConnectorRepo.PausePlaying(ActiveSpeakerIp);
                IsPlaying = false;
            }
            else
            {
                await _uow.ISonosConnectorRepo.StartPlaying(ActiveSpeakerIp);
                IsPlaying = true;
            }

            IsStale = false;
            LastSuccessfulRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            IsStale = true;
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
        Volume = clamped;
        _settings.Volume = clamped;
        NotifyStateChanged();

        try
        {
            await _uow.ISonosConnectorRepo.SetVolume(ActiveSpeakerIp, clamped);
            await _uow.ISettingsRepo.WriteSettings(_settings);
            IsStale = false;
        }
        catch (Exception ex)
        {
            IsStale = true;
            _logger.LogWarning(ex, "Failed to set volume for {SpeakerIp}", ActiveSpeakerIp);
            NotifyStateChanged();
        }
    }

    public async Task<PlaybackCommandResult> SyncPlayAsync()
    {
        if (IsSyncing)
        {
            return new PlaybackCommandResult(false, "Sync Play is already running.", true);
        }

        if (_settings is null)
        {
            _settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        }

        if (_settings.Speakers is null || !_settings.Speakers.Any() || string.IsNullOrWhiteSpace(ActiveSpeakerIp))
        {
            return new PlaybackCommandResult(false, "No active speaker available for Sync Play.", true);
        }

        IsSyncing = true;
        NotifyStateChanged();

        try
        {
            if (!await IsSpeakerReachableAsync(ActiveSpeakerIp))
            {
                return new PlaybackCommandResult(false, "Selected master speaker is offline.", true);
            }

            var allSlaveIps = _settings.Speakers
                .Where(s => !string.Equals(s.IpAddress, ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.IpAddress)
                .ToList();

            var reachableSlaveIps = await FilterReachableSpeakerIpsAsync(allSlaveIps);
            await ApplyLegacySyncAsync(ActiveSpeakerIp, reachableSlaveIps);

            var skippedCount = allSlaveIps.Count - reachableSlaveIps.Count;
            var details = skippedCount > 0
                ? $"Reachable slaves: {string.Join(", ", reachableSlaveIps)}. Skipped offline: {skippedCount}."
                : "All reachable speakers";

            await AddLogAsync("Sync Play Started (Cloned URI)", details);
            await _notificationService.SendNotificationAsync(
                "Sync Play triggered for all speakers (Legacy sync)",
                await GetCurrentUserAsync());

            await RefreshAsync();

            return skippedCount > 0
                ? new PlaybackCommandResult(true, $"Sync Play started. Skipped {skippedCount} offline speaker(s).")
                : new PlaybackCommandResult(true, "Sync Play started for all speakers.");
        }
        catch (Exception ex)
        {
            IsStale = true;
            _logger.LogWarning(ex, "Sync Play failed for {SpeakerIp}", ActiveSpeakerIp);
            return new PlaybackCommandResult(false, "Sync Play failed. Please retry.", true);
        }
        finally
        {
            IsSyncing = false;
            NotifyStateChanged();
        }
    }

    public async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveSpeakerIp))
        {
            NotifyStateChanged();
            return;
        }

        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        IsLoading = true;
        NotifyStateChanged();

        try
        {
            _settings ??= await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
            MaxVolume = Math.Clamp(_settings.MaxVolume, 0, 100);
            ActiveSpeakerName = _settings.Speakers.FirstOrDefault(s => s.IpAddress == ActiveSpeakerIp)?.Name ?? ActiveSpeakerIp;

            var volumeTask = _uow.ISonosConnectorRepo.GetVolume(ActiveSpeakerIp);
            var stationTask = _uow.ISonosConnectorRepo.GetCurrentStationAsync(ActiveSpeakerIp);
            var trackInfoTask = _uow.ISonosConnectorRepo.GetTrackInfoAsync(ActiveSpeakerIp);
            var playingTask = _uow.ISonosConnectorRepo.IsPlaying(ActiveSpeakerIp);

            await Task.WhenAll(volumeTask, stationTask, trackInfoTask, playingTask);

            Volume = Math.Clamp(volumeTask.Result, 0, MaxVolume);
            _settings.Volume = Volume;
            CurrentStationDisplay = ResolveStationDisplay(stationTask.Result);
            ApplyTrackInfo(trackInfoTask.Result);
            IsPlaying = playingTask.Result;
            LastSuccessfulRefreshUtc = DateTime.UtcNow;
            IsStale = false;
        }
        catch (Exception ex)
        {
            IsStale = true;
            _logger.LogDebug(ex, "Failed to refresh playback UI state for {SpeakerIp}", ActiveSpeakerIp);
        }
        finally
        {
            IsLoading = false;
            _refreshLock.Release();
            NotifyStateChanged();
        }
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
            .Concat((_settings?.YouTubeMusicCollections ?? new List<YouTubeMusicObject>()).Select(s => (s.Name, s.Url)));

        foreach (var source in allSources)
        {
            if (!string.IsNullOrWhiteSpace(source.Url)
                && currentStationUrl.Contains(source.Url, StringComparison.OrdinalIgnoreCase))
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

    private void ApplyTrackInfo(SonosTrackInfo? trackInfo)
    {
        CurrentTrackArtUrl = null;

        if (trackInfo is null || !trackInfo.IsValidMetadata())
        {
            CurrentTrack = "No metadata available";
            return;
        }

        CurrentTrack = trackInfo.GetDisplayString();
        CurrentTrackArtUrl = string.IsNullOrWhiteSpace(trackInfo.AlbumArtUri) ? null : trackInfo.AlbumArtUri;
    }

    private async Task<bool> IsSpeakerReachableAsync(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return false;
        }

        var uuid = await _uow.ISonosConnectorRepo.GetSpeakerUUID(ip);
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
        var currentUri = await _uow.ISonosConnectorRepo.GetCurrentStationAsync(masterIp);
        if (string.IsNullOrWhiteSpace(currentUri) || currentUri.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not read current media from master speaker {masterIp}.");
        }

        var tasks = slaveIps.Select(async slaveIp =>
        {
            if (currentUri.StartsWith("x-rincon-mp3radio://", StringComparison.OrdinalIgnoreCase)
                || currentUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                await _uow.ISonosConnectorRepo.SetTuneInStationAsync(slaveIp, currentUri);
            }

            await _uow.ISonosConnectorRepo.StartPlaying(slaveIp);
        });

        await Task.WhenAll(tasks);
    }

    private async Task AddLogAsync(string action, string? details = null)
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
