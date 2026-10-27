namespace SonosControl.Web.Services;

public enum PlaybackCommandOrigin
{
    Manual,
    Automation,
    SleepTimer,
    Volume
}

/// <summary>Coordinates playback writes by speaker and lets manual work preempt overlapping automation.</summary>
public sealed class PlaybackCommandCoordinator(TimeProvider time)
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _legacyGate = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _speakerGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Operation> _automations = [];
    private readonly HashSet<MandatoryStop> _mandatoryStops = [];
    private readonly Dictionary<string, DateTimeOffset> _manualStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _manualSync = new();

    public async Task<IAsyncDisposable?> EnterAsync(bool wait, CancellationToken cancellationToken = default)
    {
        if (wait) await _legacyGate.WaitAsync(cancellationToken);
        else if (!await _legacyGate.WaitAsync(0, cancellationToken)) return null;
        return new LegacyLease(_legacyGate);
    }

    public async Task<PlaybackCommandLease?> EnterAutomationAsync(
        IEnumerable<string> targets,
        DateTimeOffset occurrenceStart,
        CancellationToken cancellationToken = default)
    {
        var speakers = Normalize(targets);
        if (speakers.Length == 0) return null;
        var operation = new Operation(speakers, cancellationToken);
        var blocked = false;
        await _sync.WaitAsync(cancellationToken);
        try
        {
            lock (_manualSync)
                blocked = speakers.Any(ip => _manualStarts.TryGetValue(ip, out var started) && started >= occurrenceStart);
            if (!blocked) _automations.Add(operation);
        }
        finally { _sync.Release(); }
        if (blocked)
        {
            operation.Cancellation.Dispose();
            return null;
        }

        try
        {
            operation.Gates = await AcquireGatesAsync(speakers, operation.Token);
            return new PlaybackCommandLease(this, operation, PlaybackCommandOrigin.Automation, speakers, operation.Token);
        }
        catch
        {
            await FinishAutomationAsync(operation);
            throw;
        }
    }

    /// <summary>Acquires room ownership for a mandatory stop, even after a manual override.</summary>
    public async Task<PlaybackCommandLease> EnterMandatoryStopAsync(
        IEnumerable<string> targets,
        CancellationToken cancellationToken = default)
    {
        var speakers = Normalize(targets);
        if (speakers.Length == 0) throw new ArgumentException("At least one speaker target is required.", nameof(targets));
        var overlapping = new List<Operation>();
        var stop = new MandatoryStop(speakers);
        await _sync.WaitAsync(cancellationToken);
        try
        {
            foreach (var operation in _automations.Where(item => item.Targets.Overlaps(speakers)).ToList())
            {
                operation.Preempted = true;
                overlapping.Add(operation);
            }
            _mandatoryStops.Add(stop);
        }
        finally { _sync.Release(); }

        try
        {
            await Task.WhenAll(overlapping.Select(item => item.Cancellation.CancelAsync()));
            await Task.WhenAll(overlapping.Select(item => item.Finished.Task)).WaitAsync(cancellationToken);
            var gates = await AcquireGatesAsync(speakers, cancellationToken);
            return new PlaybackCommandLease(this, null, PlaybackCommandOrigin.SleepTimer, speakers, cancellationToken, gates, stop);
        }
        catch
        {
            await FinishMandatoryStopAsync(stop);
            throw;
        }
    }

    public async Task<PlaybackCommandLease> EnterManualAsync(
        IEnumerable<string> targets,
        PlaybackCommandOrigin origin = PlaybackCommandOrigin.Manual,
        CancellationToken cancellationToken = default)
    {
        if (origin == PlaybackCommandOrigin.Automation) throw new ArgumentException("Use EnterAutomationAsync for automation.", nameof(origin));
        var speakers = Normalize(targets);
        if (speakers.Length == 0) throw new ArgumentException("At least one speaker target is required.", nameof(targets));
        var overlapping = new List<Operation>();
        List<MandatoryStop> yielding;
        var started = time.GetUtcNow();
        await _sync.WaitAsync(cancellationToken);
        try
        {
            lock (_manualSync)
                foreach (var ip in speakers) _manualStarts[ip] = started;
            foreach (var operation in _automations.Where(item => item.Targets.Overlaps(speakers)).ToList())
            {
                operation.Preempted = true;
                overlapping.Add(operation);
            }
            yielding = _mandatoryStops.Where(item => item.Targets.Overlaps(speakers)).ToList();
        }
        finally { _sync.Release(); }

        // A boundary stop keeps its room and still pauses, but its fade-out yields to the queued request.
        await Task.WhenAll(yielding.Select(item => item.Yield.CancelAsync()));
        // Claims are visible before cancellation, so a concurrent scheduler poll cannot reacquire the room.
        await Task.WhenAll(overlapping.Select(item => item.Cancellation.CancelAsync()));
        await Task.WhenAll(overlapping.Select(item => item.Finished.Task)).WaitAsync(cancellationToken);
        var gates = await AcquireGatesAsync(speakers, cancellationToken);
        // The manual command is actionable only after all preempted operations release their gates.
        // Refresh its occurrence timestamp at that boundary so an intervening schedule window is
        // suppressed too, while retaining the early claim that prevents the old occurrence racing back in.
        var acquiredAt = time.GetUtcNow();
        lock (_manualSync)
            foreach (var ip in speakers) _manualStarts[ip] = acquiredAt;
        return new PlaybackCommandLease(this, null, origin, speakers, cancellationToken, gates);
    }

    /// <summary>Serializes a room's volume write without claiming its playback or cancelling automation.</summary>
    public async Task<PlaybackCommandLease> EnterVolumeAsync(string speakerIp, CancellationToken cancellationToken = default)
    {
        var speakers = Normalize([speakerIp]);
        if (speakers.Length == 0) throw new ArgumentException("A speaker target is required.", nameof(speakerIp));
        var gates = await AcquireGatesAsync(speakers, cancellationToken);
        return new PlaybackCommandLease(this, null, PlaybackCommandOrigin.Volume, speakers, cancellationToken, gates);
    }

    public void TakeManualControl(IEnumerable<string> speakers)
    {
        var now = time.GetUtcNow();
        lock (_manualSync)
            foreach (var ip in Normalize(speakers)) _manualStarts[ip] = now;
    }

    public IReadOnlyList<string> ManualSpeakersSince(DateTimeOffset since)
    {
        lock (_manualSync) return _manualStarts.Where(pair => pair.Value >= since).Select(pair => pair.Key).ToList();
    }

    public DateTimeOffset? LastManualStart(string speakerIp)
    {
        lock (_manualSync) return _manualStarts.TryGetValue(speakerIp.Trim(), out var started) ? started : null;
    }

    private async Task<SemaphoreSlim[]> AcquireGatesAsync(IEnumerable<string> speakers, CancellationToken token)
    {
        var gates = new List<SemaphoreSlim>();
        try
        {
            foreach (var ip in speakers)
            {
                await _sync.WaitAsync(token);
                SemaphoreSlim gate;
                try
                {
                    if (!_speakerGates.TryGetValue(ip, out gate!)) _speakerGates[ip] = gate = new SemaphoreSlim(1, 1);
                }
                finally { _sync.Release(); }
                await gate.WaitAsync(token);
                gates.Add(gate);
            }
            return gates.ToArray();
        }
        catch
        {
            foreach (var gate in gates.AsEnumerable().Reverse()) gate.Release();
            throw;
        }
    }

    private async Task FinishAutomationAsync(Operation operation)
    {
        await _sync.WaitAsync();
        try
        {
            if (!_automations.Remove(operation)) return;
            foreach (var gate in operation.Gates.Reverse()) gate.Release();
            operation.Cancellation.Dispose();
            operation.Finished.TrySetResult();
        }
        finally { _sync.Release(); }
    }

    private async Task FinishMandatoryStopAsync(MandatoryStop stop)
    {
        await _sync.WaitAsync();
        try { _mandatoryStops.Remove(stop); }
        finally { _sync.Release(); }
        // Not disposed: a concurrent manual request may still cancel it after reading the set.
    }

    private static string[] Normalize(IEnumerable<string> speakers)
        => speakers.Where(ip => !string.IsNullOrWhiteSpace(ip)).Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    internal sealed class Operation(string[] targets, CancellationToken callerToken)
    {
        public HashSet<string> Targets { get; } = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        public CancellationToken Token => Cancellation.Token;
        public SemaphoreSlim[] Gates { get; set; } = [];
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Preempted { get; set; }
    }

    internal sealed class MandatoryStop(string[] targets)
    {
        public HashSet<string> Targets { get; } = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource Yield { get; } = new();
    }

    public sealed class PlaybackCommandLease : IAsyncDisposable
    {
        private readonly PlaybackCommandCoordinator _owner;
        private readonly Operation? _operation;
        private readonly MandatoryStop? _stop;
        private SemaphoreSlim[] _manualGates;
        internal PlaybackCommandLease(PlaybackCommandCoordinator owner, Operation? operation,
            PlaybackCommandOrigin origin, string[] speakers, CancellationToken token, SemaphoreSlim[]? gates = null, MandatoryStop? stop = null)
        {
            _owner = owner; _operation = operation; Origin = origin; Speakers = speakers; CancellationToken = token; _manualGates = gates ?? []; _stop = stop;
        }
        /// <summary>Cancelled when a queued manual request asks a mandatory stop to cut its fade short; the stop itself must still run.</summary>
        public CancellationToken YieldToken => _stop?.Yield.Token ?? CancellationToken.None;
        public PlaybackCommandOrigin Origin { get; }
        public IReadOnlyList<string> Speakers { get; }
        public CancellationToken CancellationToken { get; }
        public bool WasPreempted => _operation?.Preempted ?? false;
        public async ValueTask DisposeAsync()
        {
            if (_operation is not null) await _owner.FinishAutomationAsync(_operation);
            foreach (var gate in Interlocked.Exchange(ref _manualGates, []).Reverse()) gate.Release();
            if (_stop is not null) await _owner.FinishMandatoryStopAsync(_stop);
        }
    }

    private sealed class LegacyLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Transfers a playback operation's room ownership when a recovery scene targets another room.</summary>
public sealed class SceneExecutionContext : IAsyncDisposable
{
    private readonly PlaybackCommandCoordinator _coordinator;
    private readonly PlaybackCommandOrigin _origin;
    private readonly DateTimeOffset _occurrenceStart;
    private readonly CancellationToken _callerToken;
    private PlaybackCommandCoordinator.PlaybackCommandLease? _lease;
    private CancellationTokenSource? _transferCancellation;
    private bool _wasPreempted;

    public SceneExecutionContext(PlaybackCommandCoordinator coordinator, PlaybackCommandOrigin origin,
        DateTimeOffset occurrenceStart, CancellationToken callerToken,
        PlaybackCommandCoordinator.PlaybackCommandLease? initialLease = null)
    {
        _coordinator = coordinator;
        _origin = origin;
        _occurrenceStart = occurrenceStart;
        _callerToken = callerToken;
        _lease = initialLease;
    }

    public CancellationToken CancellationToken => _lease?.CancellationToken ?? _callerToken;
    public PlaybackCommandCoordinator.PlaybackCommandLease? CurrentLease => _lease;
    public bool WasPreempted => _wasPreempted || _lease?.WasPreempted == true;

    public async Task<bool> ReplaceTargetsAsync(IEnumerable<string> targets, CancellationToken cancellationToken)
    {
        var nextTargets = targets.Where(ip => !string.IsNullOrWhiteSpace(ip)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var previousToken = _lease?.CancellationToken ?? cancellationToken;
        await ReleaseCurrentAsync();
        if (nextTargets.Length == 0) return false;
        _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(_callerToken, previousToken);
        _lease = _origin == PlaybackCommandOrigin.Automation
            ? await _coordinator.EnterAutomationAsync(nextTargets, _occurrenceStart, _transferCancellation.Token)
            : await _coordinator.EnterManualAsync(nextTargets, _origin, _transferCancellation.Token);
        if (_lease is null && _origin == PlaybackCommandOrigin.Automation)
        {
            _wasPreempted = true;
            _transferCancellation.Dispose();
            _transferCancellation = null;
            throw new OperationCanceledException("Recovery playback was preempted by manual playback.", _callerToken);
        }
        return _lease is not null;
    }

    public async Task ReleaseCurrentAsync()
    {
        var current = Interlocked.Exchange(ref _lease, null);
        if (current is not null)
        {
            _wasPreempted |= current.WasPreempted;
            await current.DisposeAsync();
        }
        _transferCancellation?.Dispose();
        _transferCancellation = null;
    }

    public async ValueTask DisposeAsync() => await ReleaseCurrentAsync();
}
