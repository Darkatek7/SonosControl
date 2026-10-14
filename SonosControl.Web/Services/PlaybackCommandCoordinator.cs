namespace SonosControl.Web.Services;

public sealed class PlaybackCommandCoordinator(TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _manualStarts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IAsyncDisposable?> EnterAsync(bool wait, CancellationToken cancellationToken = default)
    {
        if (wait) await _gate.WaitAsync(cancellationToken);
        else if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        return new Lease(_gate);
    }

    // Accessed while a command owns the gate.
    public void TakeManualControl(IEnumerable<string> speakers)
    {
        foreach (var ip in speakers) _manualStarts[ip] = time.GetUtcNow();
    }

    public IReadOnlyList<string> ManualSpeakersSince(DateTimeOffset since)
        => _manualStarts.Where(pair => pair.Value >= since).Select(pair => pair.Key).ToList();

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private bool _released;
        public ValueTask DisposeAsync()
        {
            if (!_released) { _released = true; gate.Release(); }
            return ValueTask.CompletedTask;
        }
    }
}
