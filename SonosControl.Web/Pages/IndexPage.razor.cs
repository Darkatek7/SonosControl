using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Authorization;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;
using SonosControl.Web.Pages.Index.Components;
using SonosControl.Web.Services;

namespace SonosControl.Web.Pages;

public partial class IndexPage : IAsyncDisposable
{
    public class SpeakerStatusViewModel
    {
        public string? Name { get; set; }
        public string? IpAddress { get; set; }
        public string? Uuid { get; set; }
        public bool IsPlaying { get; set; }
        public string? Media { get; set; }
        public string? MasterUuid { get; set; }
        public string? RawStationUrl { get; set; }
        public List<string> GroupMembers { get; set; } = new();
    }

    private const int FastRefreshIntervalSeconds = 3;
    private const int DefaultSlowLaneSeconds = 15;

    private readonly object _refreshSync = new();
    private readonly SemaphoreSlim _refreshLoopLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _activeRefreshCts;
    private bool _refreshPending;
    private bool _refreshPendingSlowLane;
    private DateTime? _lastSuccessfulRefreshUtc;
    private DateTime? _lastSlowLaneRefreshUtc;
    private int _consecutiveRefreshFailures;
    private bool _isRefreshRunning;
    private bool _disposed;
    private string? _commandStatusMessage;
    private bool _commandStatusIsError;
    private CancellationTokenSource? _commandStatusCts;

    private List<SpeakerStatusViewModel> _speakerStatuses = new();
    private SonosSettings? _settings;
    private bool _playbackStateSubscribed;
    private bool _isPlaying;
    private string? _loadingMediaUrl;

    private string SelectedSpeakerIp => PlaybackState.ActiveSpeakerIp;

    private string Greeting => AppTimeZone.Now.Hour switch
    {
        >= 5 and < 12 => "Good morning",
        >= 12 and < 18 => "Good afternoon",
        _ => "Good evening"
    };

    private Task SetActiveSpeaker(string ip) => PlaybackState.SetActiveSpeakerAsync(ip);
    private Task SetActiveVolume(int volume) => PlaybackState.SetVolumeAsync(volume);
    private Task TogglePlayback() => PlaybackState.TogglePlaybackAsync();


    private int MaxVolumeLimit => Math.Clamp(_settings?.MaxVolume ?? 100, 0, 100);

    private bool isAuthenticated;

    private Timer? _stationUpdateTimer;

    private int SlowLaneSeconds => Math.Clamp(Configuration.GetValue<int?>("Dashboard:SlowLaneSeconds") ?? DefaultSlowLaneSeconds, 10, 120);
    private string RefreshTelemetryLabel
    {
        get
        {
            if (_isRefreshRunning)
            {
                return "Syncing live state...";
            }

            if (!_lastSuccessfulRefreshUtc.HasValue)
            {
                return "Waiting for first successful refresh";
            }

            var elapsed = DateTime.UtcNow - _lastSuccessfulRefreshUtc.Value;
            if (elapsed.TotalSeconds < 5)
            {
                return "Updated just now";
            }

            return $"Last update {Math.Round(elapsed.TotalSeconds)}s ago";
        }
    }

    private List<TuneInStation> _stations =>
        (_settings?.Stations ?? new List<TuneInStation>())
        .OrderBy(s => s.Name)
        .ToList();

    private List<SpotifyObject> _tracks =>
        (_settings?.SpotifyTracks ?? new List<SpotifyObject>())
        .OrderBy(t => t.Name)
        .ToList();

    private List<YouTubeObject> _youTubeVideos =>
        (_settings?.YouTubeCollections ?? new List<YouTubeObject>())
        .OrderBy(t => t.Name)
        .ToList();

    private List<YouTubeMusicObject> _youTubeCollections =>
        (_settings?.YouTubeMusicCollections ?? new List<YouTubeMusicObject>())
        .OrderBy(t => t.Name)
        .ToList();

    private IReadOnlyList<HomeLibrarySource> _homeLibraryItems = Array.Empty<HomeLibrarySource>();

    private sealed record DeviceHealthSummary(
        int Online,
        int Offline,
        int Unknown,
        IReadOnlyDictionary<string, DeviceHealthStatus> ByIp);

    private Scene? GetScene(string? sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return null;
        }

        return _settings?.Scenes?.FirstOrDefault(scene => string.Equals(scene.Id, sceneId, StringComparison.OrdinalIgnoreCase));
    }

    private string GetSceneName(string? sceneId)
        => GetScene(sceneId)?.Name ?? "No scene";

    private static string GetWindowTimeRange(ScheduleWindow window)
        => $"{window.StartTime:HH\\:mm}-{window.StopTime:HH\\:mm}";

    private string GetActiveAutomationDescription(ScheduleWindow? window, Scene? scene)
    {
        if (window is null)
        {
            return "No schedule window is active right now. Playback is under manual control.";
        }

        var sceneName = scene?.Name ?? "No linked scene";
        return $"Scene \"{sceneName}\" is active from {GetWindowTimeRange(window)} with synced room state visible here.";
    }

    private string GetSpeakerTargetSummary(Scene? scene)
    {
        if (scene?.SpeakerIps?.Any() != true && scene?.Actions?.Any(a => a.IncludeInPlayback) != true)
        {
            return "Rooms: auto";
        }

        var speakerIps = scene.SpeakerIps.Any()
            ? scene.SpeakerIps
            : scene.Actions.Where(action => action.IncludeInPlayback).Select(action => action.SpeakerIp).ToList();

        return $"{speakerIps.Count} room{(speakerIps.Count == 1 ? "" : "s")}";
    }

    private static string GetAutomationHandoffLabel(ScheduleWindow? window, DateTimeOffset nowLocal)
    {
        if (window is null)
        {
            return "Idle";
        }

        var now = TimeOnly.FromDateTime(nowLocal.DateTime);
        var stopDateTime = nowLocal.Date + window.StopTime.ToTimeSpan();
        if (window.StopTime <= window.StartTime && now >= window.StartTime)
        {
            stopDateTime = stopDateTime.AddDays(1);
        }

        var remaining = stopDateTime - nowLocal.DateTime;
        if (remaining <= TimeSpan.Zero)
        {
            return "Soon";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return $"{Math.Max(1, remaining.Minutes)}m";
    }

    private ScheduleWindow? GetNextScheduleWindow(DateTimeOffset nowLocal, ScheduleWindow? activeWindow)
    {
        if (_settings?.ScheduleWindows is null)
        {
            return null;
        }

        return _settings.ScheduleWindows
            .Where(window => window.IsEnabled)
            .Where(window => activeWindow is null || !string.Equals(window.Id, activeWindow.Id, StringComparison.OrdinalIgnoreCase))
            .Select(window => new { Window = window, NextStart = GetNextStart(window, nowLocal) })
            .Where(item => item.NextStart is not null)
            .OrderBy(item => item.NextStart)
            .ThenBy(item => item.Window.Priority)
            .Select(item => item.Window)
            .FirstOrDefault();
    }

    private DateTimeOffset? GetNextStart(ScheduleWindow window, DateTimeOffset nowLocal)
    {
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var date = DateOnly.FromDateTime(nowLocal.DateTime.AddDays(dayOffset));
            if (!IsDateAllowedForDashboard(window, date.DayOfWeek))
            {
                continue;
            }

            if (window.ExcludedDates?.Contains(date) == true)
            {
                continue;
            }

            if (window.StartDate.HasValue && date < window.StartDate.Value)
            {
                continue;
            }

            if (window.EndDate.HasValue && date > window.EndDate.Value)
            {
                continue;
            }

            var candidate = date.ToDateTime(window.StartTime);
            if (candidate >= nowLocal.DateTime)
            {
                return AppTimeZone.FromLocal(candidate);
            }
        }

        return null;
    }

    private static bool IsDateAllowedForDashboard(ScheduleWindow window, DayOfWeek day)
    {
        return window.RecurrenceType switch
        {
            ScheduleRecurrenceType.Daily => true,
            ScheduleRecurrenceType.Weekdays => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            ScheduleRecurrenceType.Weekends => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
            ScheduleRecurrenceType.CustomDays => window.DaysOfWeek?.Contains(day) == true,
            _ => false
        };
    }

    private static string GetWindowStatusLabel(ScheduleWindow window, ScheduleWindow? activeWindow)
        => activeWindow is not null && string.Equals(window.Id, activeWindow.Id, StringComparison.OrdinalIgnoreCase)
            ? "Active"
            : "Ready";

    private static string GetWindowStatusClass(ScheduleWindow window, ScheduleWindow? activeWindow)
        => activeWindow is not null && string.Equals(window.Id, activeWindow.Id, StringComparison.OrdinalIgnoreCase)
            ? "is-active"
            : "is-ready";

    private DeviceHealthSummary GetDeviceHealthSummary()
    {
        var snapshot = DeviceHealthSnapshotStore.GetSnapshot()
            .ToDictionary(status => status.SpeakerIp, StringComparer.OrdinalIgnoreCase);

        var speakers = _settings?.Speakers ?? new List<SonosSpeaker>();
        var online = speakers.Count(speaker => snapshot.TryGetValue(speaker.IpAddress, out var status) && status.IsOnline);
        var offline = speakers.Count(speaker => snapshot.TryGetValue(speaker.IpAddress, out var status) && !status.IsOnline);
        var unknown = Math.Max(0, speakers.Count - online - offline);

        return new DeviceHealthSummary(online, offline, unknown, snapshot);
    }

    private static string GetHealthClass(DeviceHealthStatus? status)
    {
        if (status is null)
        {
            return "is-unknown";
        }

        return status.IsOnline ? "is-online" : "is-offline";
    }

    private static string GetHealthMeta(DeviceHealthStatus? status)
    {
        if (status is null)
        {
            return "No snapshot";
        }

        if (!status.IsOnline)
        {
            return string.IsNullOrWhiteSpace(status.LastError) ? "Offline" : status.LastError;
        }

        return status.LastLatencyMs.HasValue ? $"Online · {status.LastLatencyMs.Value} ms" : "Online";
    }

    private static string GetMediaIcon(string mediaType)
    {
        return mediaType switch
        {
            "station" => "rss",
            "spotify" => "music",
            "youtube" => "play",
            "youtubemusic" => "play",
            _ => "music"
        };
    }

    private static string FormatMediaTypeLabel(string mediaType)
    {
        return mediaType switch
        {
            "station" => "Station",
            "spotify" => "Spotify",
            "youtube" => "YouTube",
            "youtubemusic" => "YouTube Music",
            _ => "Media"
        };
    }

    private async Task RequestPageRefreshAsync(bool forceSlowLane = false)
    {
        if (_disposeCts.IsCancellationRequested)
        {
            return;
        }

        lock (_refreshSync)
        {
            _refreshPending = true;
            _refreshPendingSlowLane |= forceSlowLane;
            _activeRefreshCts?.Cancel();
        }

        if (!await _refreshLoopLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            while (true)
            {
                bool runSlowLane;
                CancellationToken token;

                lock (_refreshSync)
                {
                    if (!_refreshPending)
                    {
                        break;
                    }

                    _refreshPending = false;
                    runSlowLane = _refreshPendingSlowLane;
                    _refreshPendingSlowLane = false;

                    _activeRefreshCts?.Dispose();
                    _activeRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                    token = _activeRefreshCts.Token;
                }

                _isRefreshRunning = true;
                await UpdatePageDataAsync(runSlowLane, token);

                if (_disposeCts.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _isRefreshRunning = false;

            lock (_refreshSync)
            {
                _activeRefreshCts?.Dispose();
                _activeRefreshCts = null;
            }

            _refreshLoopLock.Release();
        }
    }

    private async Task UpdatePageDataAsync(bool forceSlowLane = false, CancellationToken cancellationToken = default)
    {
        var cycleStartUtc = DateTime.UtcNow;
        var runSlowLane = forceSlowLane || ShouldRunSlowLane(cycleStartUtc);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            await UpdateSpeakerStatuses(cancellationToken);

            _lastSuccessfulRefreshUtc = DateTime.UtcNow;
            if (runSlowLane)
            {
                _lastSlowLaneRefreshUtc = _lastSuccessfulRefreshUtc;
            }

            _consecutiveRefreshFailures = 0;
            MetricsCollector.RecordDashboardRefreshResult(success: true, runSlowLane, DateTime.UtcNow - cycleStartUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _consecutiveRefreshFailures++;
            Logger.LogWarning(ex, "Dashboard refresh cycle failed.");
            MetricsCollector.RecordDashboardRefreshResult(success: false, runSlowLane, DateTime.UtcNow - cycleStartUtc);
        }

        if (!_disposeCts.IsCancellationRequested)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private bool ShouldRunSlowLane(DateTime cycleStartUtc)
    {
        if (!_lastSlowLaneRefreshUtc.HasValue)
        {
            return true;
        }

        return cycleStartUtc - _lastSlowLaneRefreshUtc.Value >= TimeSpan.FromSeconds(SlowLaneSeconds);
    }

    private void UpdatePageData(object? _ = null)
    {
        _ = InvokeAsync(() => RequestPageRefreshAsync());
    }

    private async Task UpdateSpeakerStatuses(CancellationToken cancellationToken)
    {
        if (_settings?.Speakers == null)
        {
            _speakerStatuses = new();
            return;
        }

        var tasks = _settings.Speakers.Select(speaker => BuildSpeakerStatusAsync(speaker, cancellationToken)).ToList();
        _speakerStatuses = CollapseGroupedSpeakerStatuses((await Task.WhenAll(tasks)).ToList());
    }

    private static List<SpeakerStatusViewModel> CollapseGroupedSpeakerStatuses(List<SpeakerStatusViewModel> statuses)
    {
        foreach (var root in statuses.Where(s => string.IsNullOrWhiteSpace(s.MasterUuid)))
        {
            root.GroupMembers = statuses
                .Where(child => !string.IsNullOrWhiteSpace(child.MasterUuid) && string.Equals(child.MasterUuid, root.Uuid, StringComparison.OrdinalIgnoreCase))
                .Select(child => child.Name ?? child.IpAddress ?? "Linked speaker")
                .OrderBy(name => name)
                .ToList();
        }

        return statuses
            .Where(status => string.IsNullOrWhiteSpace(status.MasterUuid)
                || !statuses.Any(root => string.Equals(root.Uuid, status.MasterUuid, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private async Task<SpeakerStatusViewModel> BuildSpeakerStatusAsync(SonosSpeaker speaker, CancellationToken cancellationToken)
    {
        try
        {
            var isPlayingTask = _uow.ISonosConnectorRepo.IsPlaying(speaker.IpAddress);
            var rawStationUrlTask = _uow.ISonosConnectorRepo.GetCurrentStationAsync(speaker.IpAddress, cancellationToken);
            await Task.WhenAll(isPlayingTask, rawStationUrlTask);

            var isPlaying = await isPlayingTask;
            var rawStationUrl = await rawStationUrlTask;
            var normalizedStationUrl = NormalizeStationUrl(rawStationUrl);
            var masterUuid = TryExtractMasterUuid(rawStationUrl);

            if (speaker.IpAddress == SelectedSpeakerIp)
            {
                _isPlaying = isPlaying;
            }

            return new SpeakerStatusViewModel
            {
                Name = speaker.Name,
                IpAddress = speaker.IpAddress,
                Uuid = speaker.Uuid,
                IsPlaying = isPlaying,
                Media = speaker.IpAddress == SelectedSpeakerIp
                    ? null
                    : ResolveSpeakerMedia(isPlaying, normalizedStationUrl, masterUuid),
                MasterUuid = masterUuid,
                RawStationUrl = rawStationUrl
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new SpeakerStatusViewModel
            {
                Name = speaker.Name,
                IpAddress = speaker.IpAddress,
                Uuid = speaker.Uuid,
                IsPlaying = false,
                Media = "Offline",
                MasterUuid = null,
                RawStationUrl = null
            };
        }
    }

    private string ResolveSpeakerMedia(bool isPlaying, string normalizedStationUrl, string? masterUuid)
    {
        if (!isPlaying)
        {
            return masterUuid is null ? string.Empty : "Grouped";
        }

        if (masterUuid is not null)
        {
            return "Grouped";
        }

        if (normalizedStationUrl.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        if (normalizedStationUrl.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube Music";
        }

        var matched = _stations.FirstOrDefault(s => IsSameStoredMedia(normalizedStationUrl, s.Url));

        return matched?.Name ?? "Playing stream";
    }

    private static string? TryExtractMasterUuid(string? rawStationUrl)
    {
        if (string.IsNullOrWhiteSpace(rawStationUrl) || !rawStationUrl.StartsWith("x-rincon-group:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = Regex.Match(rawStationUrl, @"(uuid:RINCON_[A-F0-9]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string NormalizeStationUrl(string? rawStationUrl)
        => rawStationUrl?.Replace("x-rincon-mp3radio://", "", StringComparison.OrdinalIgnoreCase).Trim() ?? string.Empty;

    private static bool IsSameStoredMedia(string currentUri, string? storedUrl)
    {
        var current = NormalizeMediaUriForMatching(currentUri);
        var stored = NormalizeMediaUriForMatching(storedUrl);

        return !string.IsNullOrWhiteSpace(current)
            && !string.IsNullOrWhiteSpace(stored)
            && (current.Contains(stored, StringComparison.OrdinalIgnoreCase)
                || stored.Contains(current, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeMediaUriForMatching(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return string.Empty;
        }

        var normalized = uri.Trim()
            .Replace("x-rincon-mp3radio://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase);

        var queryIndex = normalized.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
        {
            normalized = normalized[..queryIndex];
        }

        normalized = normalized.Trim().Trim('/');
        return normalized.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? normalized[4..]
            : normalized;
    }

    private void ShowCommandStatus(string message, bool isError = false, int autoHideMs = 3500)
    {
        _commandStatusMessage = message;
        _commandStatusIsError = isError;

        _commandStatusCts?.Cancel();
        _commandStatusCts?.Dispose();
        _commandStatusCts = new CancellationTokenSource();
        var token = _commandStatusCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(autoHideMs, token);
                if (!token.IsCancellationRequested)
                {
                    await InvokeAsync(() =>
                    {
                        _commandStatusMessage = null;
                        StateHasChanged();
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }, token);
    }

    private void TrackSonosCommandError(string commandName, Exception ex)
    {
        MetricsCollector.IncrementSonosCommandError(commandName);
        Logger.LogWarning(ex, "Sonos command {CommandName} failed for speaker {SpeakerIp}.", commandName, SelectedSpeakerIp);
    }

    private string GetActivePlaybackSpeakerIp()
        => PlaybackState.ActiveSpeakerIp;

    private YouTubeObject? FindYouTubeEntry(string url)
        => (_settings?.YouTubeCollections ?? new List<YouTubeObject>())
            .FirstOrDefault(entry => string.Equals(entry.Url?.Trim(), url.Trim(), StringComparison.OrdinalIgnoreCase));

    private static (YouTubePlaybackMode Mode, int PreferredQueueLength) GetYouTubePlaybackOptions(YouTubeObject? entry, string url)
    {
        return (
            YouTubePlaybackModeResolver.GetEffectiveMode(url, entry?.PlaybackMode),
            YouTubePlaybackModeResolver.GetEffectiveQueueLength(entry?.PreferredQueueLength));
    }

    private async Task PlayMediaItem(string url, string type)
    {
        if (string.IsNullOrEmpty(url) || _settings is null) return;
        _loadingMediaUrl = url;
        await InvokeAsync(StateHasChanged);

        try
        {
            var displayName = GetDisplayNameForMedia(url, type);
            await ExecuteMediaPlaybackAsync(url, type, displayName);
        }
        finally
        {
            _loadingMediaUrl = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ExecuteMediaPlaybackAsync(string url, string mediaType, string displayName)
    {
        if (_settings is null)
        {
            return;
        }

        var targetSpeakerIp = GetActivePlaybackSpeakerIp();
        if (string.IsNullOrWhiteSpace(targetSpeakerIp))
        {
            ShowCommandStatus("No active speaker selected.", isError: true, autoHideMs: 4500);
            return;
        }

        try
        {
            switch (mediaType)
            {
                case "station":
                    await _uow.ISonosConnectorRepo.SetTuneInStationAsync(targetSpeakerIp, url);
                    await AddLog("Station Changed", $"URL: {url}");
                    await NotificationService.SendNotificationAsync($"Station changed to {displayName} on {targetSpeakerIp}", await GetCurrentUserAsync());
                    break;
                case "spotify":
                    await _uow.ISonosConnectorRepo.PlaySpotifyTrackAsync(targetSpeakerIp, url);
                    await AddLog("Spotify Track Changed", $"URL: {url}");
                    await NotificationService.SendNotificationAsync($"Spotify track changed to {displayName} on {targetSpeakerIp}", await GetCurrentUserAsync());
                    break;
                case "youtube":
                    var youTubeEntry = FindYouTubeEntry(url);
                    var youTubeOptions = GetYouTubePlaybackOptions(youTubeEntry, url);
                    await _uow.ISonosConnectorRepo.PlayYouTubeAudioAsync(targetSpeakerIp, url, youTubeOptions.Mode, youTubeOptions.PreferredQueueLength);
                    await AddLog("YouTube Video Changed", $"URL: {url}");
                    await NotificationService.SendNotificationAsync($"YouTube video changed to {displayName} on {targetSpeakerIp}", await GetCurrentUserAsync());
                    break;
                case "youtubemusic":
                    await _uow.ISonosConnectorRepo.PlayYouTubeMusicTrackAsync(targetSpeakerIp, url, _settings.AutoPlayStationUrl);
                    await AddLog("YouTube Music Changed", $"URL: {url}");
                    await NotificationService.SendNotificationAsync($"YouTube Music changed to {displayName} on {targetSpeakerIp}", await GetCurrentUserAsync());
                    break;
                default:
                    ShowCommandStatus("Unsupported media URL.", isError: true, autoHideMs: 4500);
                    return;
            }

            await _uow.ISonosConnectorRepo.StartPlaying(targetSpeakerIp);
            ShowCommandStatus("Playback command sent.");
        }
        catch (Exception ex)
        {
            TrackSonosCommandError($"play_{mediaType}", ex);
            ShowCommandStatus("Playback command failed. Please retry.", isError: true, autoHideMs: 4500);
        }
    }

    private string GetDisplayNameForMedia(string url, string mediaType)
    {
        return mediaType switch
        {
            "spotify" => _tracks.FirstOrDefault(track => string.Equals(track.Url, url, StringComparison.OrdinalIgnoreCase))?.Name ?? url,
            "youtube" => _youTubeVideos.FirstOrDefault(video => string.Equals(video.Url, url, StringComparison.OrdinalIgnoreCase))?.Name ?? url,
            "youtubemusic" => _youTubeCollections.FirstOrDefault(collection => string.Equals(collection.Url, url, StringComparison.OrdinalIgnoreCase))?.Name ?? url,
            _ => _stations.FirstOrDefault(station => string.Equals(station.Url, url, StringComparison.OrdinalIgnoreCase))?.Name ?? url
        };
    }

    private async Task AddLog(string action, string? details = null)
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        var username = user.Identity?.Name ?? "Unknown";

        var log = new LogEntry
        {
            Action = action,
            PerformedBy = username,
            Timestamp = DateTime.UtcNow,
            Details = details
        };

        Db.Logs.Add(log);
        await Db.SaveChangesAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        isAuthenticated = user.Identity?.IsAuthenticated ?? false;
        if (!isAuthenticated)
        {
            Navigation.NavigateTo("/auth/login?", true);
            return;
        }

        _settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        _settings.YouTubeCollections ??= new List<YouTubeObject>();
        _settings.YouTubeMusicCollections ??= new List<YouTubeMusicObject>();
        _homeLibraryItems = await HomeLibrary.GetQuickAccessAsync(_settings, 6);
        await PlaybackState.InitializeAsync();
        _settings.IP_Adress = PlaybackState.ActiveSpeakerIp;

        PlaybackState.StateChanged += HandlePlaybackStateChanged;
        _playbackStateSubscribed = true;
    }

    private void HandlePlaybackStateChanged()
    {
        _ = InvokeAsync(async () =>
        {
            if (_settings is not null
                && !string.IsNullOrWhiteSpace(PlaybackState.ActiveSpeakerIp)
                && !string.Equals(_settings.IP_Adress, PlaybackState.ActiveSpeakerIp, StringComparison.OrdinalIgnoreCase))
            {
                _settings.IP_Adress = PlaybackState.ActiveSpeakerIp;
                await RequestPageRefreshAsync(forceSlowLane: true);
            }

            StateHasChanged();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && isAuthenticated && _settings != null)
        {
            try
            {
                if (_settings.Speakers.Any() && !(_settings.IP_Adress is "10.0.0.0"))
                {
                    await Task.WhenAll(
                        EnsureVolumeAsync(),
                        EnsureSpeakerUuids(),
                        RequestPageRefreshAsync(forceSlowLane: true)
                    );

                    _stationUpdateTimer = new Timer(UpdatePageData, null, FastRefreshIntervalSeconds * 1000, FastRefreshIntervalSeconds * 1000);
                    StateHasChanged();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error in OnAfterRenderAsync: {ex}");
            }
        }
    }

    private async Task EnsureSpeakerUuids()
    {
        if (_settings?.Speakers == null) return;

        bool anyUpdated = false;
        foreach (var speaker in _settings.Speakers)
        {
            if (string.IsNullOrEmpty(speaker.Uuid))
            {
                var uuid = await _uow.ISonosConnectorRepo.GetSpeakerUUID(speaker.IpAddress);
                if (!string.IsNullOrEmpty(uuid))
                {
                    speaker.Uuid = uuid;
                    anyUpdated = true;
                }
            }
        }

        if (anyUpdated)
        {
            await SaveSettings();
        }
    }

    private async Task EnsureVolumeAsync()
    {
        try
        {
            _settings!.Volume = await _uow.ISonosConnectorRepo.GetVolume(SelectedSpeakerIp);
        }
        catch
        {
            // Ignore if speaker is offline
        }

        var maxVolumeLimit = MaxVolumeLimit;
        if (_settings!.Volume > maxVolumeLimit)
        {
            _settings.Volume = maxVolumeLimit;
            await _uow.ISonosConnectorRepo.SetVolume(SelectedSpeakerIp, maxVolumeLimit);
        }

        await SaveSettings();
    }

    private async Task<string> GetCurrentUserAsync()
    {
        var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        return authState.User.Identity?.Name ?? "Unknown";
    }

    private Task SaveSettings()
        => _uow.ISettingsRepo.WriteSettings(_settings!);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_playbackStateSubscribed)
        {
            PlaybackState.StateChanged -= HandlePlaybackStateChanged;
            _playbackStateSubscribed = false;
        }

        _disposeCts.Cancel();

        _stationUpdateTimer?.Dispose();
        _stationUpdateTimer = null;

        lock (_refreshSync)
        {
            _activeRefreshCts?.Cancel();
            _refreshPending = false;
            _refreshPendingSlowLane = false;
        }

        await _refreshLoopLock.WaitAsync();
        _refreshLoopLock.Release();

        lock (_refreshSync)
        {
            _activeRefreshCts?.Dispose();
            _activeRefreshCts = null;
        }

        var commandStatusCts = _commandStatusCts;
        if (commandStatusCts is not null)
        {
            _commandStatusCts = null;
            commandStatusCts.Cancel();
            commandStatusCts.Dispose();
        }

        _disposeCts.Dispose();
        _refreshLoopLock.Dispose();

        await ValueTask.CompletedTask;
    }

}
