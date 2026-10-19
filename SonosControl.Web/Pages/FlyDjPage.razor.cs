using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Services;
using SonosControl.Web.Services.FlyDj;

namespace SonosControl.Web.Pages;

public partial class FlyDjPage : ComponentBase
{
    private const int HistoryLimit = 60;
    private static readonly TimeSpan MinSampleSpacing = TimeSpan.FromSeconds(1);
    private static readonly string[] OutputKeys = ["approach", "startle", "arousal"];

    [Inject] private IOptions<FlyDjOptions> Options { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private ISettingsRepo SettingsRepo { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthProvider { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ConfiguredTimeZoneService AppTimeZone { get; set; } = default!;
    [Inject] private TimeProvider Time { get; set; } = default!;

    private enum BrainMode { Loading, Webgl, Fallback }

    private sealed record ActivitySample(DateTimeOffset At, IReadOnlyDictionary<string, double> Rates, double? Engagement);

    private sealed record BrainInit(int Id, bool Webgl);

    private ElementReference _canvas;
    private ElementReference _tooltip;

    private bool _featureEnabled;
    private bool _canEdit;
    private bool _initialized;
    private bool _firstRefreshDone;
    private bool _disposed;
    private string? _userName;

    private FlyDjStateStore? _store;
    private IFlyDjConductor? _conductor;
    private IDisposable? _viewer;
    private FlyDjDashboardState _state = FlyDjDashboardState.Initial(DateTimeOffset.UnixEpoch);
    private readonly List<ActivitySample> _history = [];

    private bool _toggleBusy;
    private string? _actionError;

    private BrainMode _brainMode = BrainMode.Loading;
    private string _geometryState = "loading";
    private string _streamState = "idle";
    private IJSObjectReference? _module;
    private DotNetObjectReference<FlyDjPage>? _self;
    private int _brainId;
    private bool _lastReachableSent = true;

    private SonosSettings? _settings;
    private string? _settingsError;
    private readonly HashSet<string> _draftRooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _draftStations = new(StringComparer.OrdinalIgnoreCase);
    private int _draftVolume = FlyDjSettings.DefaultVolume;
    private int _draftMinDwell = FlyDjSettings.DefaultMinDwellMinutes;
    private int _draftMaxDwell = FlyDjSettings.DefaultMaxDwellMinutes;
    private int _draftCooldown = FlyDjSettings.DefaultYieldCooldownMinutes;
    private bool _draftAutoResume;
    private string _baseline = string.Empty;
    private bool _saving;
    private bool _saveFailed;
    private string? _saveMessage;

    protected override async Task OnInitializedAsync()
    {
        _featureEnabled = Options.Value.Enabled;
        if (!_featureEnabled) return;

        _store = Services.GetService(typeof(FlyDjStateStore)) as FlyDjStateStore;
        _conductor = Services.GetService(typeof(IFlyDjConductor)) as IFlyDjConductor;
        if (_store is null || _conductor is null)
        {
            _featureEnabled = false;
            return;
        }

        _state = _store.Current;
        Capture(_state);
        _store.Changed += OnStoreChanged;

        var auth = await AuthProvider.GetAuthenticationStateAsync();
        _userName = auth.User.Identity?.Name;
        _canEdit = FlyDjServiceCollectionExtensions.AllowedRoles.Split(',').Any(auth.User.IsInRole);
        await LoadSettingsAsync();
        _initialized = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        if (!_featureEnabled)
        {
            Navigation.NavigateTo("/", replace: true);
            return;
        }

        // Prerendering never reaches this point, so a viewer is only attached for a live circuit.
        _viewer = _store!.AttachViewer();
        try { await _store.RefreshAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* the refresher reports failures through the state */ }
        if (_disposed) return;
        _firstRefreshDone = true;
        _state = _store.Current;
        Capture(_state);
        StateHasChanged();
        await InitializeBrainAsync();
    }

    // ---- live state ---------------------------------------------------------------------

    private void OnStoreChanged() => _ = InvokeAsync(HandleStoreChangedAsync);

    private async Task HandleStoreChangedAsync()
    {
        if (_disposed || _store is null) return;
        _state = _store.Current;
        Capture(_state);
        StateHasChanged();
        await SyncReachabilityAsync();
    }

    private void Capture(FlyDjDashboardState state)
    {
        if (state.Simulation is not { } simulation) return;
        if (_history.Count > 0 && state.UpdatedUtc - _history[^1].At < MinSampleSpacing) return;
        _history.Add(new ActivitySample(state.UpdatedUtc, simulation.PopulationRates, simulation.Engagement));
        if (_history.Count > HistoryLimit) _history.RemoveRange(0, _history.Count - HistoryLimit);
    }

    private async Task SyncReachabilityAsync()
    {
        if (_module is null || _brainId == 0 || _lastReachableSent == _state.SidecarReachable) return;
        _lastReachableSent = _state.SidecarReachable;
        try { await _module.InvokeVoidAsync("setReachable", _brainId, _state.SidecarReachable); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (ObjectDisposedException) { }
    }

    // ---- 3D view ------------------------------------------------------------------------

    private async Task InitializeBrainAsync()
    {
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/flydj-brain.js");
            if (_disposed) return;
            _self = DotNetObjectReference.Create(this);
            _lastReachableSent = _state.SidecarReachable;
            var result = await _module.InvokeAsync<BrainInit?>("init", _canvas, new
            {
                dotNet = _self,
                tooltip = _tooltip,
                reachable = _state.SidecarReachable
            });
            if (_disposed)
            {
                if (result is { Id: not 0 }) await _module.InvokeVoidAsync("dispose", result.Id);
                return;
            }
            _brainId = result?.Id ?? 0;
            _brainMode = result is { Webgl: true } ? BrainMode.Webgl : BrainMode.Fallback;
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or InvalidOperationException or ObjectDisposedException)
        {
            _brainMode = BrainMode.Fallback;
        }
        if (!_disposed) StateHasChanged();
    }

    [JSInvokable]
    public Task OnBrainStatus(string geometry, string stream)
    {
        return InvokeAsync(() =>
        {
            if (_disposed) return;
            _geometryState = geometry;
            _streamState = stream;
            StateHasChanged();
        });
    }

    private string BrainModeAttribute => _brainMode switch
    {
        BrainMode.Webgl => "webgl",
        BrainMode.Fallback => "fallback",
        _ => "loading"
    };

    private string? OverlayText
    {
        get
        {
            if (_brainMode == BrainMode.Fallback) return null;
            if (!_firstRefreshDone) return "Connecting to the FlyDJ service...";
            if (!_state.SidecarReachable) return "FlyDJ service offline";
            if (_state.Model is { } model && string.Equals(model.Status, "building", StringComparison.OrdinalIgnoreCase))
                return $"The brain model is being built ({FormatNumber(Math.Clamp(model.BuildProgress, 0, 1) * 100, "0")}%)";
            if (_brainMode == BrainMode.Webgl && _geometryState == "failed") return "Brain geometry unavailable. Retrying...";
            if (_brainMode == BrainMode.Webgl && _geometryState != "ready") return "Loading the brain...";
            if (string.Equals(_state.Simulation?.Mode, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsRunning) return "Simulation paused. Turn on FlyDJ to watch it think.";
                return _state.Status == FlyDjConductorStatus.Yielded
                    ? _state.YieldedSpeakers.Count > 0 ? "Resting while you are in control. The simulation is paused and uses no CPU." : "Waiting for scheduled playback to end. The simulation is paused."
                    : "Simulation paused for a moment...";
            }
            if (_brainMode == BrainMode.Webgl && _streamState is "offline" or "connecting") return "Connecting to the live view...";
            return null;
        }
    }

    // ---- start and stop -----------------------------------------------------------------

    private bool IsRunning => _conductor?.IsRunning ?? false;

    private IReadOnlyList<SonosSpeaker> SavedTargetRooms
    {
        get
        {
            if (_settings is null) return [];
            var wanted = _settings.FlyDj?.TargetSpeakerIps ?? [];
            return _settings.Speakers.Where(speaker => wanted.Contains(speaker.IpAddress, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    private IReadOnlyList<TuneInStation> LibraryStations =>
        _settings?.Stations.Where(station => !string.IsNullOrWhiteSpace(station.Url)).ToList() ?? [];

    private int SavedCandidateCount
    {
        get
        {
            var library = LibraryStations;
            var chosen = _settings?.FlyDj?.CandidateStationUrls ?? [];
            return chosen.Count == 0
                ? library.Count
                : library.Count(station => chosen.Contains(station.Url, StringComparer.OrdinalIgnoreCase));
        }
    }

    private string? ToggleDisabledReason
    {
        get
        {
            if (IsRunning) return null;
            if (!_canEdit) return "Only admins and operators can start FlyDJ.";
            if (_settings is null) return "Loading your settings...";
            if (SavedTargetRooms.Count == 0) return "Choose at least one target room in Settings first.";
            if (SavedCandidateCount == 0) return "Add radio stations to the Library first, or pick candidates in Settings.";
            if (_firstRefreshDone && !_state.SidecarReachable) return "The FlyDJ service is not reachable right now.";
            return null;
        }
    }

    private string ToggleHint
    {
        get
        {
            if (_toggleBusy) return IsRunning ? "Stopping..." : "Starting...";
            if (ToggleDisabledReason is { } reason) return reason;
            if (IsRunning && _state.Status == FlyDjConductorStatus.Yielded)
            {
                if (_state.YieldedSpeakers.Count == 0) return "Schedules currently own the selected rooms. FlyDJ waits until they end.";
                return _state.NextDecisionAtUtc is { } resume
                    ? $"You took over, so FlyDJ is resting until {FormatClock(resume)}. Switch it off to stop for good."
                    : "You took over, so FlyDJ is resting. Switch it off to stop for good.";
            }
            if (IsRunning)
            {
                if (_state.IsScheduled) return "FlyDJ is playing for a schedule, using that schedule's rooms and volumes. Switching it off skips the current FlyDJ schedule occurrence.";
                var rooms = string.Join(", ", SavedTargetRooms.Select(room => room.Name));
                return $"FlyDJ is choosing music for {rooms}. Anything you play by hand takes priority.";
            }
            return $"Starts auditioning your stations and plays the winner on {string.Join(", ", SavedTargetRooms.Select(room => room.Name))}.";
        }
    }

    private async Task ToggleAsync()
    {
        if (_conductor is null || _toggleBusy || ToggleDisabledReason is not null) return;
        _toggleBusy = true;
        _actionError = null;
        try
        {
            if (_conductor.IsRunning) await _conductor.StopAsync(_userName);
            else await _conductor.StartAsync(_userName);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _actionError = $"FlyDJ could not be {(IsRunning ? "stopped" : "started")}: {exception.Message}";
        }
        finally
        {
            _toggleBusy = false;
        }
    }

    private string PillLabel => _state.Status switch
    {
        FlyDjConductorStatus.Starting => "Starting",
        FlyDjConductorStatus.Auditioning => "Auditioning stations",
        FlyDjConductorStatus.Playing => string.IsNullOrWhiteSpace(_state.CurrentStationName) ? "Playing" : $"Playing {_state.CurrentStationName}",
        FlyDjConductorStatus.Yielded => _state.YieldedSpeakers.Count > 0 ? "Yielded to manual control" : "Waiting for schedules",
        FlyDjConductorStatus.Error => "Error",
        _ => "Stopped"
    };

    private string PillClass => _state.Status switch
    {
        FlyDjConductorStatus.Playing or FlyDjConductorStatus.Auditioning => "is-ready",
        FlyDjConductorStatus.Yielded or FlyDjConductorStatus.Starting => "is-warn",
        FlyDjConductorStatus.Error => "is-error",
        _ => "is-idle"
    };

    // ---- activity and statistics --------------------------------------------------------

    private IEnumerable<(string Key, double Rate)> OutputPopulations
    {
        get
        {
            var rates = _state.Simulation?.PopulationRates;
            return OutputKeys.Select(key => (key, rates is not null && rates.TryGetValue(key, out var rate) ? rate : 0d));
        }
    }

    private double MaxRate
    {
        get
        {
            var max = 1d;
            foreach (var sample in _history)
                foreach (var key in OutputKeys)
                    if (sample.Rates.TryGetValue(key, out var rate) && rate > max) max = rate;
            foreach (var (_, rate) in OutputPopulations) if (rate > max) max = rate;
            return max;
        }
    }

    private string SparkPoints(string key)
    {
        var max = MaxRate;
        var values = _history.Select(sample => sample.Rates.TryGetValue(key, out var rate) ? rate : 0d).ToList();
        return BuildPoints(values, 0, max);
    }

    private string EngagementSparkPoints()
    {
        var values = _history.Select(sample => sample.Engagement).Where(value => value.HasValue && double.IsFinite(value.Value)).Select(value => value!.Value).ToList();
        if (values.Count < 2) return string.Empty;
        var min = Math.Min(0, values.Min());
        var max = Math.Max(1, values.Max());
        return BuildPoints(values, min, max);
    }

    private static string BuildPoints(IReadOnlyList<double> values, double min, double max)
    {
        if (values.Count < 2) return string.Empty;
        var span = Math.Max(max - min, 0.0001);
        var step = 120d / (values.Count - 1);
        return string.Join(" ", values.Select((value, index) =>
        {
            var x = index * step;
            var y = 27 - Math.Clamp((value - min) / span, 0, 1) * 26;
            return string.Create(CultureInfo.InvariantCulture, $"{x:0.#},{y:0.#}");
        }));
    }

    private static string SafeKey(string key)
    {
        var chars = key.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray();
        return chars.Length == 0 ? "other" : new string(chars).ToLowerInvariant();
    }

    private static string ChipStyle(string key) => $"background: var(--flydj-pop-{SafeKey(key)}, var(--flydj-pop-other));";

    private static string SparkStyle(string key) => $"color: var(--flydj-pop-{SafeKey(key)}, var(--flydj-pop-other));";

    private string BarStyle(string key, double rate)
    {
        var percent = Math.Clamp(rate / MaxRate * 100, 0, 100);
        return string.Create(CultureInfo.InvariantCulture, $"width: {percent:0.#}%; background: var(--flydj-pop-{SafeKey(key)}, var(--flydj-pop-other));");
    }

    private static string PopulationShortLabel(string key) => key switch
    {
        "approach" => "Approach",
        "startle" => "Startle",
        "arousal" => "Arousal",
        _ => key
    };

    private static string ModeLabel(string? mode) => mode?.ToLowerInvariant() switch
    {
        "stopped" => "Stopped (idle)",
        "bursting" => "Bursting (short auditions)",
        "continuous" => "Continuous (live view)",
        null or "" => "-",
        var other => other
    };

    private static string ModelStatusLabel(FlyDjModelInfo info) =>
        string.Equals(info.Status, "building", StringComparison.OrdinalIgnoreCase)
            ? $"Building ({FormatNumber(Math.Clamp(info.BuildProgress, 0, 1) * 100, "0")}%)"
            : char.ToUpperInvariant(info.Status[0]) + info.Status[1..];

    // ---- formatting ---------------------------------------------------------------------

    private static string FormatNumber(double value, string format = "0.##") =>
        double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "-";

    private static string FormatRate(double value) => $"{FormatNumber(value, "0.0")} Hz";

    private static string FormatPercent(double probability) =>
        $"{(probability * 100).ToString("0", CultureInfo.InvariantCulture)}%";

    private static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private string FormatClock(DateTimeOffset utc) => AppTimeZone.Format(utc, "HH:mm");

    private string FormatRelative(DateTimeOffset target)
    {
        var delta = target - Time.GetUtcNow();
        var minutes = (int)Math.Round(Math.Abs(delta.TotalMinutes));
        if (Math.Abs(delta.TotalSeconds) < 45) return "now";
        var text = minutes >= 90 ? $"{Math.Round(minutes / 60d, 1).ToString("0.#", CultureInfo.InvariantCulture)} h" : $"{Math.Max(1, minutes)} min";
        return delta > TimeSpan.Zero ? $"in {text}" : $"{text} ago";
    }

    private string RoomName(string ip) =>
        _settings?.Speakers.FirstOrDefault(speaker => string.Equals(speaker.IpAddress, ip, StringComparison.OrdinalIgnoreCase))?.Name ?? ip;

    private static bool IsChosen(FlyDjDecisionView decision, FlyDjCandidateScore candidate) =>
        string.Equals(candidate.Url, decision.StationUrl, StringComparison.OrdinalIgnoreCase);

    private static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // ---- settings -----------------------------------------------------------------------

    private int MaxVolume => Math.Clamp(_settings?.MaxVolume ?? 100, 0, 100);

    private bool IsDirty => _settings is not null && Signature() != _baseline;

    private string Signature() => string.Join("|",
        string.Join(",", _draftRooms.Order(StringComparer.OrdinalIgnoreCase)),
        _draftVolume,
        string.Join(",", _draftStations.Order(StringComparer.OrdinalIgnoreCase)),
        _draftMinDwell, _draftMaxDwell, _draftCooldown, _draftAutoResume);

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await SettingsRepo.GetSettings() ?? new SonosSettings();
            _settingsError = null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _settingsError = "Settings could not be loaded.";
            return;
        }
        ResetDraft();
    }

    private void ResetDraft()
    {
        var flyDj = _settings?.FlyDj ?? new FlyDjSettings();
        _draftRooms.Clear();
        foreach (var ip in flyDj.TargetSpeakerIps) _draftRooms.Add(ip);
        _draftStations.Clear();
        foreach (var url in flyDj.CandidateStationUrls) _draftStations.Add(url);
        _draftVolume = Math.Clamp(flyDj.Volume, 0, MaxVolume);
        _draftMinDwell = flyDj.MinDwellMinutes;
        _draftMaxDwell = flyDj.MaxDwellMinutes;
        _draftCooldown = flyDj.YieldCooldownMinutes;
        _draftAutoResume = flyDj.AutoResume;
        _baseline = Signature();
    }

    private Task DiscardChanges()
    {
        ResetDraft();
        _saveMessage = null;
        return Task.CompletedTask;
    }

    private static void SetMember(HashSet<string> set, string value, ChangeEventArgs args)
    {
        if (args.Value is true) set.Add(value);
        else set.Remove(value);
    }

    private void SetVolume(int volume) => _draftVolume = Math.Clamp(volume, 0, MaxVolume);

    private async Task SaveAsync()
    {
        if (!_canEdit || _saving || _settings is null || !IsDirty) return;
        _saving = true;
        _saveMessage = null;
        _saveFailed = false;
        var rooms = _draftRooms.ToList();
        var stations = _draftStations.ToList();
        var volume = _draftVolume;
        var minDwell = _draftMinDwell;
        var maxDwell = _draftMaxDwell;
        var cooldown = _draftCooldown;
        var autoResume = _draftAutoResume;
        try
        {
            var updated = await SettingsRepo.UpdateSettingsAsync(settings =>
            {
                settings.FlyDj ??= new FlyDjSettings();
                var flyDj = settings.FlyDj;
                flyDj.TargetSpeakerIps = settings.Speakers
                    .Where(speaker => rooms.Contains(speaker.IpAddress, StringComparer.OrdinalIgnoreCase))
                    .Select(speaker => speaker.IpAddress).ToList();
                flyDj.CandidateStationUrls = settings.Stations
                    .Where(station => stations.Contains(station.Url, StringComparer.OrdinalIgnoreCase))
                    .Select(station => station.Url).ToList();
                flyDj.Volume = Math.Clamp(volume, 0, Math.Clamp(settings.MaxVolume, 0, 100));
                flyDj.MinDwellMinutes = minDwell;
                flyDj.MaxDwellMinutes = maxDwell;
                flyDj.YieldCooldownMinutes = cooldown;
                flyDj.AutoResume = autoResume;
                flyDj.Normalize();
            });
            _settings = updated ?? _settings;
            ResetDraft();
            _saveMessage = "FlyDJ settings saved.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _saveFailed = true;
            _saveMessage = $"Settings could not be saved: {exception.Message}";
        }
        finally
        {
            _saving = false;
        }
    }

    // ---- teardown -----------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_store is not null) _store.Changed -= OnStoreChanged;
        _viewer?.Dispose();
        _viewer = null;
        if (_module is not null)
        {
            try
            {
                if (_brainId != 0) await _module.InvokeVoidAsync("dispose", _brainId);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        _self?.Dispose();
    }
}
