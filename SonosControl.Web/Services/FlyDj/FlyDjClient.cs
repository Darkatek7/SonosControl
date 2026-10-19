using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SonosControl.Web.Services.FlyDj;

public interface IFlyDjClient
{
    Task<FlyDjModelInfo> GetModelAsync(CancellationToken cancellationToken = default);
    Task<FlyDjSimulationState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<FlyDjSimulationState> StartAsync(CancellationToken cancellationToken = default);
    Task<FlyDjSimulationState> StopAsync(CancellationToken cancellationToken = default);

    /// <exception cref="FlyDjNotRunningException">The simulation is stopped.</exception>
    Task<FlyDjAuditionResponse> AuditionAsync(FlyDjAuditionRequest request, CancellationToken cancellationToken = default);

    /// <exception cref="FlyDjNotRunningException">The simulation is stopped.</exception>
    Task<FlyDjListenResponse> ListenAsync(string? url, CancellationToken cancellationToken = default);

    Task<FlyDjGeometryResult> GetGeometryAsync(string? etag = null, CancellationToken cancellationToken = default);
    Task<FlyDjHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>Typed HTTP client for the FlyDJ sidecar (contract: <c>flydj/API.md</c>).</summary>
public sealed class FlyDjClient(IHttpClientFactory httpClientFactory) : IFlyDjClient
{
    public const string HttpClientName = "FlyDj";
    public const string TokenHeader = "X-FlyDj-Token";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<FlyDjModelInfo> GetModelAsync(CancellationToken cancellationToken = default)
        => SendAsync<FlyDjModelInfo>(HttpMethod.Get, "v1/model", null, cancellationToken);

    public Task<FlyDjSimulationState> GetStateAsync(CancellationToken cancellationToken = default)
        => SendAsync<FlyDjSimulationState>(HttpMethod.Get, "v1/state", null, cancellationToken);

    public Task<FlyDjSimulationState> StartAsync(CancellationToken cancellationToken = default)
        => SendAsync<FlyDjSimulationState>(HttpMethod.Post, "v1/run/start", null, cancellationToken);

    public Task<FlyDjSimulationState> StopAsync(CancellationToken cancellationToken = default)
        => SendAsync<FlyDjSimulationState>(HttpMethod.Post, "v1/run/stop", null, cancellationToken);

    public Task<FlyDjAuditionResponse> AuditionAsync(FlyDjAuditionRequest request, CancellationToken cancellationToken = default)
        => SendAsync<FlyDjAuditionResponse>(HttpMethod.Post, "v1/audition", request, cancellationToken);

    public Task<FlyDjListenResponse> ListenAsync(string? url, CancellationToken cancellationToken = default)
        => SendAsync<FlyDjListenResponse>(HttpMethod.Post, "v1/listen", new FlyDjListenRequest(url), cancellationToken);

    public async Task<FlyDjGeometryResult> GetGeometryAsync(string? etag = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/model/geometry");
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await SendRawAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new FlyDjGeometryResult(null, etag, true);
        await EnsureSuccessAsync(response, cancellationToken);
        var content = await ReadAsync(() => response.Content.ReadAsByteArrayAsync(cancellationToken), cancellationToken);
        return new FlyDjGeometryResult(content, response.Headers.ETag?.ToString(), false);
    }

    public async Task<FlyDjHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "health");
            using var response = await SendRawAsync(request, cancellationToken);
            string? modelStatus = null;
            try
            {
                var body = await response.Content.ReadFromJsonAsync<HealthBody>(Json, cancellationToken);
                modelStatus = body?.ModelStatus;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException) { }
            return response.IsSuccessStatusCode
                ? new FlyDjHealth(true, true, modelStatus, null)
                : new FlyDjHealth(true, false, modelStatus, $"Health check answered {(int)response.StatusCode}.");
        }
        catch (FlyDjUnavailableException ex)
        {
            return new FlyDjHealth(false, false, null, ex.Message);
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var response = await SendRawAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var parsed = await ReadAsync(() => response.Content.ReadFromJsonAsync<T>(Json, cancellationToken), cancellationToken);
        return parsed ?? throw new FlyDjUnavailableException($"The FlyDJ service returned an empty response for {path}.");
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            throw new FlyDjUnavailableException("The FlyDJ service did not answer in time.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new FlyDjUnavailableException($"The FlyDJ service is unreachable: {ex.Message}", ex);
        }
    }

    private static async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try { return await read(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or OperationCanceledException or IOException)
        {
            throw new FlyDjUnavailableException($"The FlyDJ service sent an unreadable response: {ex.Message}", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        ErrorBody? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, cancellationToken); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or IOException) { }
        var message = string.IsNullOrWhiteSpace(error?.Message) ? $"The FlyDJ service answered {status}." : error!.Message!;
        if (response.StatusCode == HttpStatusCode.Conflict && string.Equals(error?.Error, "not_running", StringComparison.OrdinalIgnoreCase))
            throw new FlyDjNotRunningException(message);
        if (status >= 500 || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout)
            throw new FlyDjUnavailableException(message);
        throw new FlyDjApiException(status, error?.Error, message);
    }

    private sealed record HealthBody(string? Status, string? ModelStatus);
    private sealed record ErrorBody(string? Error, string? Message);
}
