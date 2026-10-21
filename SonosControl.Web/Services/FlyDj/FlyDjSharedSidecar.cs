namespace SonosControl.Web.Services.FlyDj;

/// <summary>One sidecar, shared fresh probes and a single selected live picture for independent schedules.</summary>
internal sealed class FlyDjSharedSidecar(IFlyDjClient client, TimeProvider time)
{
    private IFlyDjClient RawClient => client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly Dictionary<string, FlyDjAuditionResult> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _listening = new(StringComparer.Ordinal);
    private string _selected = "standalone";

    public IFlyDjClient ForSession(string key) => new Session(this, key);

    public async Task SelectAsync(string key, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_selected == key) return;
            _selected = key;
            if (_running.Count > 0) await client.ListenAsync(_listening.GetValueOrDefault(key), token);
        }
        finally { _gate.Release(); }
    }

    private async Task<FlyDjAuditionResponse> AuditionAsync(FlyDjAuditionRequest request, CancellationToken token)
    {
        // Each request contains at most two stations; serialising requests also bounds cross-schedule concurrency.
        await _probeGate.WaitAsync(token);
        try
        {
            var now = time.GetUtcNow();
            bool Reusable(FlyDjAuditionCandidate candidate) => request.ReuseWithin > TimeSpan.Zero
                && _samples.TryGetValue(candidate.Url, out var sample) && sample.SampledAtUtc is { } at
                && at <= now.AddSeconds(5) && now - at < request.ReuseWithin;
            var missing = request.Candidates.Where(candidate => !Reusable(candidate)).ToArray();
            FlyDjAuditionResponse? response = null;
            if (missing.Length > 0)
            {
                response = await client.AuditionAsync(request with { Candidates = missing }, token);
                foreach (var candidate in missing)
                    _samples[candidate.Url] = response.Results.FirstOrDefault(result => result.Id == candidate.Id)
                        ?? new FlyDjAuditionResult(candidate.Id, false, "The sidecar omitted this audio measurement", null, null, null)
                        { SampleId = Guid.NewGuid().ToString("N"), SampledAtUtc = time.GetUtcNow() };
            }
            var results = request.Candidates.Select(candidate => _samples.TryGetValue(candidate.Url, out var result)
                ? result with { Id = candidate.Id } : new FlyDjAuditionResult(candidate.Id, false, "Missing audio measurement", null, null, null)).ToArray();
            if (_samples.Count > 1024)
                foreach (var key in _samples.OrderBy(pair => pair.Value.SampledAtUtc).Take(_samples.Count - 1024).Select(pair => pair.Key).ToArray()) _samples.Remove(key);
            return new(results, response?.BioMs ?? request.BioMs, response?.Baseline ?? new Dictionary<string, double>());
        }
        finally { _probeGate.Release(); }
    }

    private sealed class Session(FlyDjSharedSidecar owner, string key) : IFlyDjClient
    {
        public Task<FlyDjModelInfo> GetModelAsync(CancellationToken cancellationToken = default) => owner.RawClient.GetModelAsync(cancellationToken);
        public Task<FlyDjSimulationState> GetStateAsync(CancellationToken cancellationToken = default) => owner.RawClient.GetStateAsync(cancellationToken);
        public Task<FlyDjGeometryResult> GetGeometryAsync(string? etag = null, CancellationToken cancellationToken = default) => owner.RawClient.GetGeometryAsync(etag, cancellationToken);
        public Task<FlyDjHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => owner.RawClient.CheckHealthAsync(cancellationToken);
        public Task<FlyDjAuditionResponse> AuditionAsync(FlyDjAuditionRequest request, CancellationToken cancellationToken = default) => owner.AuditionAsync(request, cancellationToken);
        public async Task<FlyDjSimulationState> StartAsync(CancellationToken cancellationToken = default)
        {
            await owner._gate.WaitAsync(cancellationToken);
            try
            {
                var state = await owner.RawClient.StartAsync(cancellationToken);
                owner._running.Add(key);
                return state;
            }
            finally { owner._gate.Release(); }
        }
        public async Task<FlyDjSimulationState> StopAsync(CancellationToken cancellationToken = default)
        {
            await owner._gate.WaitAsync(cancellationToken);
            try
            {
                owner._running.Remove(key);
                owner._listening.Remove(key);
                if (owner._running.Count > 0 && owner._selected == key) await owner.RawClient.ListenAsync(null, cancellationToken);
                return owner._running.Count == 0 ? await owner.RawClient.StopAsync(cancellationToken) : await owner.RawClient.GetStateAsync(cancellationToken);
            }
            finally { owner._gate.Release(); }
        }
        public async Task<FlyDjListenResponse> ListenAsync(string? url, CancellationToken cancellationToken = default)
        {
            await owner._gate.WaitAsync(cancellationToken);
            try
            {
                owner._listening[key] = url;
                return owner._selected == key ? await owner.RawClient.ListenAsync(url, cancellationToken) : new(true, null, null, null, null);
            }
            finally { owner._gate.Release(); }
        }
    }
}
