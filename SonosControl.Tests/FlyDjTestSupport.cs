using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace SonosControl.Tests;

/// <summary>Records requests and answers them from a callback, for sidecar client tests.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Body, IReadOnlyDictionary<string, string> Headers);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri, body, headers));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
{
    public string? LastName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastName = name;
        return client;
    }
}

/// <summary>
/// A fake clock that knows how many of its timers are waiting. The conductor loop sleeps on exactly one
/// clock timer between steps, so "a timer is pending" means the loop has finished its step and is parked;
/// an advance sent before that point can be lost, and one sent after it always fires the timer.
/// </summary>
internal sealed class FlyDjTestClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private int _pending;
    private long _created;

    /// <summary>Timers that have been created and have neither fired nor been disposed.</summary>
    public int PendingTimers => Volatile.Read(ref _pending);

    /// <summary>Every timer ever created, so a caller can tell a new wait from the one it already saw.</summary>
    public long TimersCreated => Interlocked.Read(ref _created);

    /// <summary>Waits for a pending timer, so the advance cannot land before the code under test has asked to be woken.</summary>
    public async Task AdvanceWhenParkedAsync(TimeSpan by, int timeoutMs = 10000)
    {
        var watch = global::System.Diagnostics.Stopwatch.StartNew();
        while (PendingTimers == 0)
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Nothing was waiting on the clock to advance by {by}.");
            await Task.Delay(1);
        }
        Advance(by);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var oneShot = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero;
        var tracker = new TrackedTimer(this);
        tracker.Inner = base.CreateTimer(_ =>
        {
            if (oneShot) tracker.Release(); // one-shot timers are finished the moment they fire
            callback(state);
        }, null, dueTime, period);
        // Counted only once the base clock has registered the timer, so "pending" never reports a timer
        // that an advance would still miss.
        Interlocked.Increment(ref _created);
        Interlocked.Increment(ref _pending);
        return tracker;
    }

    private sealed class TrackedTimer(FlyDjTestClock clock) : ITimer
    {
        private int _released;

        public ITimer Inner { get; set; } = null!;

        public void Release()
        {
            // A timer that fires on creation is released before it is counted; the count then nets out to zero.
            if (Interlocked.Exchange(ref _released, 1) == 0) Interlocked.Decrement(ref clock._pending);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => Inner.Change(dueTime, period);

        public void Dispose()
        {
            Release();
            Inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Release();
            return Inner.DisposeAsync();
        }
    }
}
