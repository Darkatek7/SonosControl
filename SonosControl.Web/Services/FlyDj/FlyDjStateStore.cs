namespace SonosControl.Web.Services.FlyDj;

/// <summary>
/// Process-wide FlyDJ dashboard state shared by all UI circuits. The conductor publishes its
/// decisions here; while at least one viewer is attached a light refresher also mirrors the
/// sidecar's simulation state (every 2 s) and model info (every 30 s, faster until ready).
/// With no viewers nothing polls.
/// </summary>
public sealed class FlyDjStateStore : IDisposable
{
    public static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ModelInterval = TimeSpan.FromSeconds(30);

    private readonly IFlyDjClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<FlyDjStateStore> _logger;
    private readonly object _sync = new();
    private FlyDjDashboardState _current;
    private int _viewers;
    private CancellationTokenSource? _refresherCancellation;
    private DateTimeOffset? _lastModelFetchUtc;
    private bool _disposed;

    public FlyDjStateStore(IFlyDjClient client, TimeProvider time, ILogger<FlyDjStateStore> logger)
    {
        _client = client;
        _time = time;
        _logger = logger;
        _current = FlyDjDashboardState.Initial(time.GetUtcNow());
    }

    public event Action? Changed;

    public FlyDjDashboardState Current
    {
        get { lock (_sync) return _current; }
    }

    public int ViewerCount
    {
        get { lock (_sync) return _viewers; }
    }

    /// <summary>Applies a change atomically and notifies subscribers.</summary>
    public void Update(Func<FlyDjDashboardState, FlyDjDashboardState> mutate)
    {
        lock (_sync) _current = mutate(_current) with { UpdatedUtc = _time.GetUtcNow() };
        Changed?.Invoke();
    }

    /// <summary>Registers an interested UI. Dispose the result when the view goes away.</summary>
    public IDisposable AttachViewer()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (++_viewers == 1)
            {
                var cancellation = new CancellationTokenSource();
                _refresherCancellation = cancellation;
                _lastModelFetchUtc = null;
                _ = Task.Run(() => RefreshLoopAsync(cancellation.Token));
            }
        }
        return new Viewer(this);
    }

    private void DetachViewer()
    {
        CancellationTokenSource? cancel = null;
        lock (_sync)
        {
            if (_viewers == 0) return;
            if (--_viewers == 0)
            {
                cancel = _refresherCancellation;
                _refresherCancellation = null;
            }
        }
        cancel?.Cancel();
    }

    /// <summary>One refresher tick; public so tests (and a first paint) can drive it directly.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var simulation = await _client.GetStateAsync(cancellationToken);
            Update(state => state with { SidecarReachable = true, Simulation = simulation });
            var now = _time.GetUtcNow();
            var model = Current.Model;
            var due = model is null || !string.Equals(model.Status, "ready", StringComparison.OrdinalIgnoreCase)
                || _lastModelFetchUtc is null || now - _lastModelFetchUtc.Value >= ModelInterval;
            if (due)
            {
                var info = await _client.GetModelAsync(cancellationToken);
                _lastModelFetchUtc = now;
                Update(state => state with { Model = info });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FlyDjException ex)
        {
            _logger.LogDebug(ex, "FlyDJ sidecar state refresh failed.");
            Update(state => state with { SidecarReachable = false, Simulation = null });
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshAsync(cancellationToken);
                await Task.Delay(StateInterval, _time, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlyDJ state refresher stopped unexpectedly.");
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancel;
        lock (_sync)
        {
            _disposed = true;
            cancel = _refresherCancellation;
            _refresherCancellation = null;
            _viewers = 0;
        }
        cancel?.Cancel();
    }

    private sealed class Viewer(FlyDjStateStore owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.DetachViewer();
        }
    }
}
