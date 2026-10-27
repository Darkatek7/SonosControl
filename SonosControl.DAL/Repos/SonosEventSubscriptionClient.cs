using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.DAL.Repos;

/// <summary>Sends UPnP GENA SUBSCRIBE and UNSUBSCRIBE requests to Sonos speakers.</summary>
public sealed class SonosEventSubscriptionClient(IHttpClientFactory httpClientFactory, ILogger<SonosEventSubscriptionClient>? logger = null)
    : ISonosEventSubscriptionClient
{
    private static readonly HttpMethod Subscribe = new("SUBSCRIBE");
    private static readonly HttpMethod Unsubscribe = new("UNSUBSCRIBE");
    private readonly ILogger<SonosEventSubscriptionClient> _logger = logger ?? NullLogger<SonosEventSubscriptionClient>.Instance;

    public async Task<SonosEventSubscription> SubscribeAsync(string ip, SonosEventService service, Uri callback, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(Subscribe, ip, service);
        request.Headers.TryAddWithoutValidation("CALLBACK", $"<{callback.AbsoluteUri}>");
        request.Headers.TryAddWithoutValidation("NT", "upnp:event");
        request.Headers.TryAddWithoutValidation("TIMEOUT", FormatTimeout(timeout));
        using var response = await SendAsync(request, ip, service, "Subscribe", cancellationToken);
        var sid = Header(response, "SID");
        if (string.IsNullOrWhiteSpace(sid))
            throw Fail(ip, service, "Subscribe", response.StatusCode, "The speaker accepted the subscription without a SID.");
        return new SonosEventSubscription(sid, ParseTimeout(Header(response, "TIMEOUT"), timeout));
    }

    public async Task<SonosEventSubscription?> RenewAsync(string ip, SonosEventService service, string sid, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(Subscribe, ip, service);
        request.Headers.TryAddWithoutValidation("SID", sid);
        request.Headers.TryAddWithoutValidation("TIMEOUT", FormatTimeout(timeout));
        try
        {
            using var response = await SendAsync(request, ip, service, "Renew", cancellationToken);
            return new SonosEventSubscription(Header(response, "SID") ?? sid, ParseTimeout(Header(response, "TIMEOUT"), timeout));
        }
        catch (SonosCommandException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public async Task<bool> UnsubscribeAsync(string ip, SonosEventService service, string sid, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(Unsubscribe, ip, service);
        request.Headers.TryAddWithoutValidation("SID", sid);
        try
        {
            using var response = await SendAsync(request, ip, service, "Unsubscribe", cancellationToken);
            return true;
        }
        catch (SonosCommandException)
        {
            return false;
        }
    }

    internal static string EventPath(SonosEventService service) => service switch
    {
        SonosEventService.AVTransport => "/MediaRenderer/AVTransport/Event",
        SonosEventService.RenderingControl => "/MediaRenderer/RenderingControl/Event",
        SonosEventService.ZoneGroupTopology => "/ZoneGroupTopology/Event",
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, null)
    };

    internal static TimeSpan ParseTimeout(string? header, TimeSpan requested)
    {
        // Speakers answer "Second-<n>"; anything else falls back to the requested lifetime.
        const string prefix = "Second-";
        return header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(header[prefix.Length..], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : requested;
    }

    private static string FormatTimeout(TimeSpan timeout) => $"Second-{Math.Max(1, (int)timeout.TotalSeconds)}";

    private static HttpRequestMessage CreateRequest(HttpMethod method, string ip, SonosEventService service)
        => new(method, $"http://{ip}:1400{EventPath(service)}");

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string ip, SonosEventService service, string action, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(nameof(SonosEventSubscriptionClient)).SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw Fail(ip, service, action, null, ex.Message, ex);
        }

        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        var reason = response.ReasonPhrase ?? "The speaker rejected the request.";
        response.Dispose();
        throw Fail(ip, service, action, status, reason);
    }

    private SonosCommandException Fail(string ip, SonosEventService service, string action, HttpStatusCode? status, string description, Exception? inner = null)
    {
        var failure = new SonosCommandException(ip, service.ToString(), action, status, null, description, inner);
        // A lost subscription on renewal is routine, so it does not warrant a warning.
        if (status != HttpStatusCode.PreconditionFailed)
            _logger.LogWarning(failure, "Sonos event {Action} for {Service} failed on {SpeakerIp}, HTTP {StatusCode}: {Description}.",
                action, service, ip, status, description);
        return failure;
    }
}
