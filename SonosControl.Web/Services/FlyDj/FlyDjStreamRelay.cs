using System.Net.WebSockets;
using Microsoft.Extensions.Options;

namespace SonosControl.Web.Services.FlyDj;

/// <summary>
/// Relays the sidecar's binary spike stream to an authenticated browser. The browser never talks
/// to the sidecar, so the sidecar can stay on the internal network; data from the browser is ignored.
/// </summary>
public static class FlyDjStreamRelay
{
    private const int BufferSize = 64 * 1024;
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("A WebSocket upgrade request is required.", context.RequestAborted);
            return;
        }

        // Browsers always send Origin on WebSocket upgrades; refuse cross-site pages riding on the auth cookie.
        if (!IsSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<FlyDjOptions>>().Value;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SonosControl.Web.Services.FlyDj.StreamRelay");
        var aborted = context.RequestAborted;

        using var upstream = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(options.SharedSecret))
            upstream.Options.SetRequestHeader(FlyDjClient.TokenHeader, options.SharedSecret);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 60)));
            await upstream.ConnectAsync(BuildStreamUri(options.ServiceUrl), connectTimeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException or UriFormatException)
        {
            if (aborted.IsCancellationRequested) return;
            logger.LogWarning(ex, "FlyDJ live stream could not connect to the sidecar.");
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var browser = await context.WebSockets.AcceptWebSocketAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var token = linked.Token;
        try
        {
            var forward = PumpAsync(upstream, browser, forwardData: true, token);
            var drain = PumpAsync(browser, upstream, forwardData: false, token);
            var first = await Task.WhenAny(forward, drain);
            // Say goodbye while the sockets are still usable; cancelling a pending receive would abort them instead.
            if (ReferenceEquals(first, forward))
            {
                await CloseOutputQuietlyAsync(browser);
                await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(2), aborted));
            }
            else
            {
                await CloseOutputQuietlyAsync(upstream);
                await Task.WhenAny(forward, Task.Delay(TimeSpan.FromSeconds(2), aborted));
            }
            await CloseOutputQuietlyAsync(browser);
            await CloseOutputQuietlyAsync(upstream);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            logger.LogDebug(ex, "FlyDJ live stream relay ended.");
        }
        finally
        {
            await linked.CancelAsync();
        }
    }

    public static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
        // Reverse proxies that rewrite Host usually forward the public host instead.
        var forwardedHost = request.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim();
        return string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            || (forwardedHost.Length > 0 && string.Equals(originUri.Authority, forwardedHost, StringComparison.OrdinalIgnoreCase));
    }

    public static Uri BuildStreamUri(string serviceUrl)
    {
        var baseUri = new UriBuilder(string.IsNullOrWhiteSpace(serviceUrl) ? "http://flydj:8090" : serviceUrl.Trim());
        baseUri.Scheme = baseUri.Scheme.ToLowerInvariant() switch
        {
            "https" or "wss" => "wss",
            _ => "ws"
        };
        baseUri.Path = baseUri.Path.TrimEnd('/') + "/v1/stream";
        return baseUri.Uri;
    }

    /// <summary>Copies messages from <paramref name="source"/> until it closes; with <paramref name="forwardData"/> false, data is discarded.</summary>
    private static async Task PumpAsync(WebSocket source, WebSocket destination, bool forwardData, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        using var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested && source.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (!forwardData) continue;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) return;
            if (!result.EndOfMessage) continue;
            if (destination.State != WebSocketState.Open) return;
            await destination.SendAsync(new ArraySegment<byte>(message.GetBuffer(), 0, (int)message.Length), result.MessageType, true, cancellationToken);
            message.SetLength(0);
        }
    }

    private static async Task CloseOutputQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException) { }
    }
}
