using System.Collections.Concurrent;
using SonosControl.DAL.Interfaces;

namespace SonosControl.Web.Services;

/// <summary>Serializes scene commands and timer expiry for each speaker.</summary>
public sealed class SceneTimerService(IServiceScopeFactory scopes, TimeProvider time, ILogger<SceneTimerService> logger)
    : IHostedService, IAsyncDisposable
{
    private sealed class TimerEntry(CancellationTokenSource cancellation)
    {
        private readonly object _sync = new();
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public void Cancel() { lock (_sync) { if (!_disposed) Cancellation.Cancel(); } }
        public void Dispose() { lock (_sync) { _disposed = true; Cancellation.Dispose(); } }
        private bool _disposed;
    }
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _speakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TimerEntry> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Task> _tasks = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lifecycle = new();
    private long _generation;
    private int _disposed;

    public async Task<IAsyncDisposable> BeginSceneAsync(IEnumerable<string> targets, CancellationToken cancellationToken)
    {
        var acquired = new List<SemaphoreSlim>();
        try
        {
            foreach (var ip in targets.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var gate = _speakers.GetOrAdd(ip, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                acquired.Add(gate);
            }
            foreach (var ip in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                if (_timers.TryRemove(ip, out var previous)) previous.Cancel();
            return new Lease(acquired);
        }
        catch { foreach (var gate in acquired.AsEnumerable().Reverse()) gate.Release(); throw; }
    }

    // Called while the scene owns the speaker leases.
    public void Schedule(string sceneId, string sceneName, IEnumerable<string> targets, int? minutes)
    {
        lock (_lifecycle)
        {
            if (minutes is null or <= 0 || _stopping.IsCancellationRequested) return;
            foreach (var ip in targets.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var entry = new TimerEntry(CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
                _timers[ip] = entry;
                var generation = Interlocked.Increment(ref _generation);
                var task = RunTimerAsync(ip, sceneId, sceneName, minutes.Value, entry);
                _tasks[generation] = task;
                _ = RemoveCompletedAsync(generation, task);
            }
        }
    }

    private async Task RemoveCompletedAsync(long generation, Task task)
    {
        await task;
        _tasks.TryRemove(generation, out _);
    }

    private async Task RunTimerAsync(string ip, string sceneId, string sceneName, int minutes, TimerEntry entry)
    {
        var token = entry.Cancellation.Token;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(minutes), time, token);
            var gate = _speakers[ip];
            await gate.WaitAsync(token);
            try
            {
                if (!_timers.TryGetValue(ip, out var current) || !ReferenceEquals(current, entry)) return;
                token.ThrowIfCancellationRequested();
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ISonosConnectorRepo>().PausePlaying(ip);
                await scope.ServiceProvider.GetRequiredService<ActionLogger>().LogAsync("SceneTimerCompleted", $"{sceneName} ({sceneId}) paused {ip} after {minutes} minute(s).");
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Scene timer failed for {SceneId} on {SpeakerIp}.", sceneId, ip); }
        finally
        {
            ((ICollection<KeyValuePair<string, TimerEntry>>)_timers).Remove(new(ip, entry));
            entry.Dispose();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task cancellation;
        Task[] running;
        lock (_lifecycle)
        {
            cancellation = _stopping.CancelAsync();
            running = _tasks.Values.ToArray();
        }
        await cancellation;
        await Task.WhenAll(running).WaitAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None);
        _stopping.Dispose();
    }

    private sealed class Lease(List<SemaphoreSlim> gates) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                foreach (var gate in gates.AsEnumerable().Reverse()) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
