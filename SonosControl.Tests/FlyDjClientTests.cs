using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SonosControl.Web.Services.FlyDj;
using Xunit;

namespace SonosControl.Tests;

public class FlyDjClientTests
{
    private const string ModelJson = """
        {
          "dataVersion": "flywire-783", "scope": "auditory", "status": "ready", "buildProgress": 1.0, "message": null,
          "neuronCount": 7984, "connectionCount": 912345, "synapseCount": 4567890,
          "populations": [
            {"id": 1, "key": "jo_a", "label": "JO-A (high-frequency sound)", "role": "input", "neuronCount": 120},
            {"id": 5, "key": "approach", "label": "Approach / courtship song", "role": "output", "neuronCount": 40}
          ],
          "attribution": [{"text": "Dorkenwald et al. 2024", "url": "https://doi.org/10.1038/s41586-024-07558-y"}],
          "shuffleControl": {"realResponse": 2.41, "shuffledResponse": 0.37, "ratio": 6.5, "trials": 8}
        }
        """;

    private const string StateJson = """
        {
          "mode": "bursting", "running": true, "viewers": 0, "bioTimeMs": 123456.7, "realtimeFactor": 0.92,
          "cpuMsPerBioSecond": 310.5, "spikesPerSecond": 1840.0, "engagement": 0.84,
          "populationRates": {"approach": 14.2, "startle": 3.1, "arousal": 22.0},
          "listeningUrl": "stream.example.com/live.mp3",
          "lastFeatures": {"low": 0.41, "mid": 0.33, "high": 0.26, "loudness": 0.62, "onsetRate": 2.1, "centroidHz": 1840.0},
          "modelStatus": "ready"
        }
        """;

    private static (FlyDjClient Client, StubHttpHandler Handler, SingleClientFactory Factory) Create(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://flydj:8090/") };
        var factory = new SingleClientFactory(http);
        return (new FlyDjClient(factory), handler, factory);
    }

    [Fact]
    public async Task ModelAndStateDeserializeTheDocumentedCamelCasePayloads()
    {
        var (client, handler, factory) = Create((request, _) =>
            StubHttpHandler.Json(request.RequestUri!.AbsolutePath.EndsWith("model") ? ModelJson : StateJson));

        var model = await client.GetModelAsync();
        var state = await client.GetStateAsync();

        Assert.Equal("FlyDj", factory.LastName);
        Assert.Equal("/v1/model", handler.Requests[0].Uri!.AbsolutePath);
        Assert.Equal("/v1/state", handler.Requests[1].Uri!.AbsolutePath);
        Assert.Equal("flywire-783", model.DataVersion);
        Assert.Equal(7984, model.NeuronCount);
        Assert.Equal(4567890L, model.SynapseCount);
        Assert.Equal("approach", model.Populations[1].Key);
        Assert.Equal(6.5, model.ShuffleControl!.Ratio);
        Assert.Equal("bursting", state.Mode);
        Assert.Equal(0.84, state.Engagement);
        Assert.Equal(14.2, state.PopulationRates["approach"]);
        Assert.Equal(1840.0, state.LastFeatures!.CentroidHz);
        Assert.Equal("ready", state.ModelStatus);
    }

    [Fact]
    public async Task StartAndStopPostWithoutBodyAndReturnTheState()
    {
        var (client, handler, _) = Create((_, _) => StubHttpHandler.Json(StateJson));

        var started = await client.StartAsync();
        await client.StopAsync();

        Assert.True(started.Running);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Post, request.Method));
        Assert.Equal("/v1/run/start", handler.Requests[0].Uri!.AbsolutePath);
        Assert.Equal("/v1/run/stop", handler.Requests[1].Uri!.AbsolutePath);
        Assert.Null(handler.Requests[0].Body);
    }

    [Fact]
    public async Task AuditionPostsCamelCaseCandidatesAndParsesResults()
    {
        const string response = """
            {"results": [
               {"id": "st-1", "ok": true, "error": null, "score": 1.27,
                "features": {"low": 0.41, "mid": 0.33, "high": 0.26, "loudness": 0.62, "onsetRate": 2.1, "centroidHz": 1840.0},
                "populationRates": {"approach": 14.2}},
               {"id": "st-2", "ok": false, "error": "fetch_failed", "score": null, "features": null, "populationRates": null}],
             "bioMs": 500, "baseline": {"approach": 2.0, "startle": 1.1, "arousal": 4.0}}
            """;
        var (client, handler, _) = Create((_, _) => StubHttpHandler.Json(response));

        var result = await client.AuditionAsync(new FlyDjAuditionRequest(
            [new FlyDjAuditionCandidate("st-1", "stream.example.com/live.mp3")], 500));

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("/v1/audition", handler.Requests[0].Uri!.AbsolutePath);
        Assert.Equal(500, body.RootElement.GetProperty("bioMs").GetInt32());
        var candidate = body.RootElement.GetProperty("candidates")[0];
        Assert.Equal("st-1", candidate.GetProperty("id").GetString());
        Assert.Equal("stream.example.com/live.mp3", candidate.GetProperty("url").GetString());
        Assert.Equal(1.27, result.Results[0].Score);
        Assert.Equal(0.41, result.Results[0].Features!.Low);
        Assert.False(result.Results[1].Ok);
        Assert.Equal("fetch_failed", result.Results[1].Error);
        Assert.Null(result.Results[1].Score);
        Assert.Equal(1.1, result.Baseline["startle"]);
    }

    [Fact]
    public async Task ListenSendsTheUrlOrNullToClear()
    {
        var (client, handler, _) = Create((_, _) => StubHttpHandler.Json(
            """{"ok": true, "error": null, "engagement": 0.84, "features": null, "populationRates": {"approach": 1.0}}"""));

        var listen = await client.ListenAsync("stream.example.com/live.mp3");
        await client.ListenAsync(null);

        Assert.Equal("/v1/listen", handler.Requests[0].Uri!.AbsolutePath);
        using var first = JsonDocument.Parse(handler.Requests[0].Body!);
        using var second = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("stream.example.com/live.mp3", first.RootElement.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("url").ValueKind);
        Assert.Equal(0.84, listen.Engagement);
    }

    [Fact]
    public async Task NotRunningConflictMapsToATypedException()
    {
        var (client, _, _) = Create((_, _) => StubHttpHandler.Json(
            """{"error": "not_running", "message": "Simulation is stopped."}""", HttpStatusCode.Conflict));

        var failure = await Assert.ThrowsAsync<FlyDjNotRunningException>(() => client.ListenAsync("x"));
        Assert.Equal("Simulation is stopped.", failure.Message);
        await Assert.ThrowsAsync<FlyDjNotRunningException>(() => client.AuditionAsync(new FlyDjAuditionRequest([], 500)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ServerErrorsAndRejectedTokensMapToUnavailable(HttpStatusCode status)
    {
        var (client, _, _) = Create((_, _) => StubHttpHandler.Json("""{"error": "boom", "message": "Nope."}""", status));
        await Assert.ThrowsAsync<FlyDjUnavailableException>(() => client.GetStateAsync());
    }

    [Fact]
    public async Task OtherClientErrorsMapToAnApiException()
    {
        var (client, _, _) = Create((_, _) => StubHttpHandler.Json(
            """{"error": "too_many_candidates", "message": "At most 8."}""", HttpStatusCode.BadRequest));

        var failure = await Assert.ThrowsAsync<FlyDjApiException>(() => client.AuditionAsync(new FlyDjAuditionRequest([], 500)));
        Assert.Equal(400, failure.StatusCode);
        Assert.Equal("too_many_candidates", failure.Code);
    }

    [Fact]
    public async Task TransportFailuresAndTimeoutsMapToUnavailable()
    {
        var refused = new FlyDjClient(new SingleClientFactory(new HttpClient(new ThrowingHandler(new HttpRequestException("refused")))
        { BaseAddress = new Uri("http://flydj:8090/") }));
        var slow = new FlyDjClient(new SingleClientFactory(new HttpClient(new ThrowingHandler(new TaskCanceledException("timeout")))
        { BaseAddress = new Uri("http://flydj:8090/") }));

        await Assert.ThrowsAsync<FlyDjUnavailableException>(() => refused.GetStateAsync());
        await Assert.ThrowsAsync<FlyDjUnavailableException>(() => slow.GetModelAsync());
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsUnavailable()
    {
        var client = new FlyDjClient(new SingleClientFactory(new HttpClient(new ThrowingHandler(new TaskCanceledException()))
        { BaseAddress = new Uri("http://flydj:8090/") }));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStateAsync(cancellation.Token));
    }

    [Fact]
    public async Task GeometryReturnsBytesAndEtagThenNotModifiedWhenTheEtagMatches()
    {
        var bytes = Encoding.ASCII.GetBytes("FDJG-payload");
        var (client, handler, _) = Create((request, _) =>
        {
            if (request.Headers.TryGetValues("If-None-Match", out var values) && values.Contains("\"abc123\""))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"");
            return response;
        });

        var fresh = await client.GetGeometryAsync();
        var cached = await client.GetGeometryAsync(fresh.ETag);

        Assert.False(fresh.NotModified);
        Assert.Equal(bytes, fresh.Content);
        Assert.Equal("\"abc123\"", fresh.ETag);
        Assert.True(cached.NotModified);
        Assert.Null(cached.Content);
        Assert.Equal("/v1/model/geometry", handler.Requests[0].Uri!.AbsolutePath);
        Assert.False(handler.Requests[0].Headers.ContainsKey("If-None-Match"));
        Assert.Equal("\"abc123\"", handler.Requests[1].Headers["If-None-Match"]);
    }

    [Fact]
    public async Task HealthReportsReachabilityWithoutThrowing()
    {
        var (healthy, _, _) = Create((_, _) => StubHttpHandler.Json("""{"status": "ok", "modelStatus": "ready"}"""));
        var (failing, _, _) = Create((_, _) => StubHttpHandler.Json("""{"status": "error", "modelStatus": "error"}""", HttpStatusCode.ServiceUnavailable));
        var down = new FlyDjClient(new SingleClientFactory(new HttpClient(new ThrowingHandler(new HttpRequestException("refused")))
        { BaseAddress = new Uri("http://flydj:8090/") }));

        var ok = await healthy.CheckHealthAsync();
        var bad = await failing.CheckHealthAsync();
        var unreachable = await down.CheckHealthAsync();

        Assert.True(ok.Reachable && ok.Ok);
        Assert.Equal("ready", ok.ModelStatus);
        Assert.True(bad.Reachable);
        Assert.False(bad.Ok);
        Assert.Equal("error", bad.ModelStatus);
        Assert.False(unreachable.Reachable);
    }

    [Fact]
    public async Task RegisteredHttpClientCarriesTheBaseAddressTimeoutAndTokenHeader()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json(StateJson));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddFlyDj(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlyDj:Enabled"] = "true",
            ["FlyDj:ServiceUrl"] = "http://sidecar.local:9000",
            ["FlyDj:SharedSecret"] = "s3cret",
            ["FlyDj:RequestTimeoutSeconds"] = "12",
            ["BackgroundServices:Enabled"] = "false"
        }).Build());
        services.AddHttpClient(FlyDjClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IFlyDjClient>().GetStateAsync();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(FlyDjClient.HttpClientName);

        Assert.Equal("http://sidecar.local:9000/v1/state", handler.Requests[0].Uri!.ToString());
        Assert.Equal("s3cret", handler.Requests[0].Headers[FlyDjClient.TokenHeader]);
        Assert.Equal(TimeSpan.FromSeconds(12), http.Timeout);
    }

    [Fact]
    public async Task NoTokenHeaderIsSentWithoutASharedSecret()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json(StateJson));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddFlyDj(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FlyDj:Enabled"] = "true",
            ["FlyDj:SharedSecret"] = "",
            ["BackgroundServices:Enabled"] = "false"
        }).Build());
        services.AddHttpClient(FlyDjClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IFlyDjClient>().GetStateAsync();

        Assert.Equal("http://flydj:8090/v1/state", handler.Requests[0].Uri!.ToString());
        Assert.False(handler.Requests[0].Headers.ContainsKey(FlyDjClient.TokenHeader));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
