using SonosControl.DAL.Interfaces;

namespace SonosControl.Web.Services;

public sealed record SleepTimerSnapshot(
    Guid Id,
    string SpeakerIp,
    string SpeakerName,
    DateTimeOffset EndsUtc,
    bool IsRunning,
    string? Error);

/// <summary>A process-local timer whose lifetime is independent of Blazor circuits.</summary>
public sealed class SleepTimerService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SleepTimerService> logger,
    PlaybackSnapshotMonitorService? snapshotMonitor = null) : IHostedService, IAsyncDisposable, IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenSource? _timerCancellation;
    private SleepTimerSnapshot? _snapshot;
    private readonly Dictionary<Guid, Task> _timerTasks = [];
    private int _disposed;

    public event Action? Changed;

    public SleepTimerSnapshot? GetSnapshot()
    {
        lock (_sync) return _snapshot;
    }

    public SleepTimerSnapshot Start(string speakerIp, string speakerName, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(speakerIp)) throw new ArgumentException("A speaker is required.", nameof(speakerIp));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(12)) throw new ArgumentOutOfRangeException(nameof(duration));

        SleepTimerSnapshot snapshot;
        CancellationTokenSource? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_stopping.IsCancellationRequested) throw new InvalidOperationException("The sleep timer service is stopping.");
            previous = _timerCancellation;
            _timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            snapshot = new SleepTimerSnapshot(Guid.NewGuid(), speakerIp, speakerName,
                timeProvider.GetUtcNow().Add(duration), true, null);
            _snapshot = snapshot;
            _timerTasks[snapshot.Id] = RunAsync(snapshot, _timerCancellation);
        }
        CancelAndDispose(previous);
        Changed?.Invoke();
        return snapshot;
    }

    public bool Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_snapshot is null || !_snapshot.IsRunning) return false;
            cancellation = _timerCancellation;
            _timerCancellation = null;
            _snapshot = null;
        }
        CancelAndDispose(cancellation);
        Changed?.Invoke();
        return true;
    }

    private async Task RunAsync(SleepTimerSnapshot timer, CancellationTokenSource owner)
    {
        var token = owner.Token;
        string? error = null;
        try
        {
            var delay = timer.EndsUtc - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, owner.Token).ConfigureAwait(false);
            owner.Token.ThrowIfCancellationRequested();
            using (var scope = scopeFactory.CreateScope())
            {
                var manualPlayback = scope.ServiceProvider.GetRequiredService<ManualPlaybackService>();
                var connector = scope.ServiceProvider.GetRequiredService<ISonosConnectorRepo>();
                await using var lease = await manualPlayback.BeginAsync(timer.SpeakerIp, owner.Token, PlaybackCommandOrigin.SleepTimer).ConfigureAwait(false);
                owner.Token.ThrowIfCancellationRequested();
                await connector.PausePlaying(timer.SpeakerIp, owner.Token).ConfigureAwait(false);
            }
            Complete(timer, owner, null);
            if (snapshotMonitor is not null)
            {
                try { await snapshotMonitor.RefreshNowAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { logger.LogDebug(ex, "Immediate playback refresh after sleep timer failed for {SpeakerIp}.", timer.SpeakerIp); }
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sleep timer expiry failed for {SpeakerIp}.", timer.SpeakerIp);
            error = "The sleep timer could not pause the room. Check the speaker connection and try again.";
        }

        Complete(timer, owner, error);
        lock (_sync) _timerTasks.Remove(timer.Id);
    }

    private void Complete(SleepTimerSnapshot timer, CancellationTokenSource owner, string? error)
    {
        var changed = false;
        lock (_sync)
        {
            if (ReferenceEquals(_timerCancellation, owner) && _snapshot?.Id == timer.Id)
            {
                _timerCancellation = null;
                _snapshot = timer with { IsRunning = false, Error = error };
                changed = true;
            }
        }
        try { owner.Dispose(); }
        catch (ObjectDisposedException) { }
        if (changed) Changed?.Invoke();
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] timers;
        CancellationTokenSource? owner;
        lock (_sync) { timers = _timerTasks.Values.ToArray(); owner = _timerCancellation; _timerCancellation = null; }
        CancelAndDispose(owner);
        if (timers.Length > 0) await Task.WhenAll(timers).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None);
        _stopping.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
