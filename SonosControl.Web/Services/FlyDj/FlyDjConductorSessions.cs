using System.Collections.Concurrent;
using System.Text.Json;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services.FlyDj;

public sealed partial class FlyDjConductorService
{
    private readonly FlyDjStateStore? _dashboard;
    private readonly FlyDjSharedSidecar _shared;
    private readonly ConcurrentDictionary<string, FlyDjConductorService> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _manager = new(1, 1);
    private readonly HashSet<string> _suppressedManaged = new(StringComparer.Ordinal);
    private IReadOnlyList<FlyDjSchedulePlayback> _managedSchedules = [];
    private readonly bool _isWorker;
    private readonly string? _sessionKey;
    private string _sessionName = "Standalone / shared schedule";
    private IReadOnlyList<string>? _poolOverride;
    private string _selectedSession = "standalone";

    private FlyDjConductorService(FlyDjConductorService parent, FlyDjSchedulePlayback schedule)
        : this(parent._time, parent._scopes, parent._commands, parent._snapshots, parent._state,
            parent._client, parent._logger, parent._scheduleOwnership, parent._autoStop)
    {
        _dashboard = null;
        _isWorker = true;
        _sessionKey = schedule.Key;
        _sessionName = schedule.Window.Name;
        _poolOverride = schedule.Window.FlyDjCandidateStationUrls?.ToArray();
        _shared = parent._shared;
        _client = _shared.ForSession(schedule.Key);
        _state.Changed += parent.PublishDashboard;
    }

    public async Task StartAsync(string? performedBy, CancellationToken cancellationToken = default)
    {
        if (_isWorker) { await StartOwnAsync(performedBy, cancellationToken); return; }
        await _manager.WaitAsync(cancellationToken);
        try
        {
            await StartOwnAsync(performedBy, cancellationToken);
            foreach (var worker in _workers.Values)
            {
                // Explicitly switching back on resumes these occurrences as well as the standalone session.
                worker._suppressedSchedules.Clear();
            }
        }
        finally { _manager.Release(); }
        await UpdateSchedulesAsync(_managedSchedules, cancellationToken);
    }

    public async Task StopAsync(string? performedBy, CancellationToken cancellationToken = default)
    {
        if (_isWorker) { await StopOwnAsync(performedBy, cancellationToken); return; }
        await _manager.WaitAsync(cancellationToken);
        try { await StopOwnAsync(performedBy, cancellationToken); }
        finally { _manager.Release(); }
    }

    public async Task UpdateSchedulesAsync(IReadOnlyList<FlyDjSchedulePlayback> schedules, CancellationToken cancellationToken = default)
    {
        if (_isWorker) { await UpdateOwnSchedulesAsync(schedules, cancellationToken); return; }
        await _manager.WaitAsync(cancellationToken);
        try
        {
            _managedSchedules = schedules.ToArray();
            _suppressedManaged.IntersectWith(schedules.Select(schedule => schedule.Key));
            var enabled = schedules.Where(schedule => !_suppressedManaged.Contains(schedule.Key)).ToArray();
            var independent = enabled.Where(schedule => schedule.Window.FlyDjCandidateStationUrls is not null || schedule.Window.FlyDjPrewarm).ToArray();
            var keys = independent.Select(schedule => schedule.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _workers.Keys.Where(key => !keys.Contains(key)).ToArray())
                if (_workers.TryRemove(key, out var removed))
                {
                    await removed.StopOwnAsync(DefaultActor, cancellationToken);
                    await removed.StopAsync(cancellationToken);
                    removed.Dispose();
                }
            foreach (var schedule in independent)
            {
                if (!_workers.TryGetValue(schedule.Key, out var worker))
                {
                    worker = new(this, schedule);
                    _workers[schedule.Key] = worker;
                    if (_hostStarted) await worker.StartAsync(_shutdown.Token);
                }
                var nextPool = schedule.Window.FlyDjCandidateStationUrls;
                if (JsonSerializer.Serialize(nextPool, Json) != JsonSerializer.Serialize(worker._poolOverride, Json) && worker._running)
                    await worker.StopSessionAsync(DefaultActor, cancellationToken, preserveYielded: true);
                worker._poolOverride = nextPool?.ToArray();
                worker._sessionName = schedule.Window.Name;
                await worker.UpdateOwnSchedulesAsync([schedule], cancellationToken);
            }
            await UpdateOwnSchedulesAsync(enabled.Where(schedule => !keys.Contains(schedule.Key)).ToArray(), cancellationToken);
            await SelectAvailableSessionAsync(cancellationToken);
            PublishDashboard();
        }
        finally { _manager.Release(); }
    }

    public async Task SelectSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId != "standalone" && !_workers.ContainsKey(sessionId)) throw new ArgumentException("The FlyDJ session is no longer active.", nameof(sessionId));
        _selectedSession = sessionId;
        await _shared.SelectAsync(sessionId, cancellationToken);
        if (_dashboard is not null) await _dashboard.RefreshAsync(cancellationToken);
        PublishDashboard();
    }

    private async Task SelectAvailableSessionAsync(CancellationToken token)
    {
        if (_selectedSession == "standalone" && !_running && _workers.Count > 0)
            _selectedSession = _workers.Keys.Order(StringComparer.Ordinal).First();
        else if (_selectedSession != "standalone" && !_workers.ContainsKey(_selectedSession))
            _selectedSession = _running || _workers.IsEmpty ? "standalone" : _workers.Keys.Order(StringComparer.Ordinal).First();
        await _shared.SelectAsync(_selectedSession, token);
    }

    private void PublishDashboard()
    {
        if (_dashboard is null) return;
        var workers = _workers.ToArray();
        var selected = _selectedSession != "standalone" && _workers.TryGetValue(_selectedSession, out var worker) ? worker._state.Current : _state.Current;
        var views = workers.Select(pair => new FlyDjSessionView(pair.Key, pair.Value._sessionName, pair.Value._state.Current.Status,
            pair.Value._state.Current.IsPrewarming, pair.Value._state.Current.CurrentStationName, pair.Value._state.Current.ActiveSpeakerIps)).ToList();
        if (_running || views.Count == 0)
        {
            var own = _state.Current;
            var name = _schedules.Count == 1 ? _schedules[0].Window.Name : _sessionName;
            views.Insert(0, new("standalone", name, own.Status, own.IsPrewarming, own.CurrentStationName, own.ActiveSpeakerIps));
        }
        _dashboard.Update(current => selected with
        {
            Model = selected.Model ?? current.Model,
            Simulation = current.Simulation,
            SidecarReachable = selected.Status == FlyDjConductorStatus.Stopped ? current.SidecarReachable : selected.SidecarReachable,
            Sessions = views,
            SelectedSessionId = _selectedSession
        });
    }
}
