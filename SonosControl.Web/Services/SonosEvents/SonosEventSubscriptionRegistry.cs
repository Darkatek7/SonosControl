using System.Collections.Concurrent;
using System.Security.Cryptography;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services.SonosEvents;

public enum SonosEventSequence
{
    Next,
    Gap,
    Stale
}

/// <summary>One subscription slot. The token in the callback path exists before SUBSCRIBE is sent.</summary>
public sealed class SonosEventRegistration(string token, string speakerIp, string? speakerUuid, SonosEventService service)
{
    private readonly object _sync = new();
    private string? _sid;
    private long _lastSequence = -1;
    private DateTimeOffset? _lastEventUtc;

    public string Token { get; } = token;
    public string SpeakerIp { get; } = speakerIp;
    public string? SpeakerUuid { get; } = speakerUuid;
    public SonosEventService Service { get; } = service;

    public DateTimeOffset? LastEventUtc { get { lock (_sync) return _lastEventUtc; } }

    public void SetSid(string sid)
    {
        lock (_sync) _sid = sid;
    }

    /// <summary>
    /// The initial NOTIFY can arrive before the SUBSCRIBE response, so an unknown SID is accepted
    /// until the response has been recorded.
    /// </summary>
    public bool MatchesSid(string? sid)
    {
        lock (_sync) return _sid is null || string.Equals(_sid, sid?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public SonosEventSequence Observe(long sequence, DateTimeOffset receivedUtc)
    {
        lock (_sync)
        {
            if (_lastSequence >= 0 && sequence <= _lastSequence) return SonosEventSequence.Stale;
            var result = _lastSequence < 0 ? (sequence == 0 ? SonosEventSequence.Next : SonosEventSequence.Gap)
                : sequence == _lastSequence + 1 ? SonosEventSequence.Next : SonosEventSequence.Gap;
            _lastSequence = sequence;
            _lastEventUtc = receivedUtc;
            return result;
        }
    }
}

/// <summary>Routes NOTIFY callbacks to the subscription that owns their token.</summary>
public sealed class SonosEventSubscriptionRegistry
{
    private readonly ConcurrentDictionary<string, SonosEventRegistration> _registrations = new(StringComparer.Ordinal);

    public SonosEventRegistration Register(string speakerIp, string? speakerUuid, SonosEventService service)
    {
        while (true)
        {
            var registration = new SonosEventRegistration(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                speakerIp, NormalizeUuid(speakerUuid), service);
            if (_registrations.TryAdd(registration.Token, registration)) return registration;
        }
    }

    public bool TryGet(string token, out SonosEventRegistration registration)
        => _registrations.TryGetValue(token, out registration!);

    public bool IsActive(SonosEventRegistration registration)
        => _registrations.TryGetValue(registration.Token, out var current) && ReferenceEquals(current, registration);

    public void Remove(SonosEventRegistration registration)
        => ((ICollection<KeyValuePair<string, SonosEventRegistration>>)_registrations).Remove(new(registration.Token, registration));

    public string? FindSpeakerIp(string speakerUuid)
        => _registrations.Values.FirstOrDefault(item => string.Equals(item.SpeakerUuid, NormalizeUuid(speakerUuid), StringComparison.OrdinalIgnoreCase))?.SpeakerIp;

    public string? FindSpeakerUuid(string speakerIp)
        => _registrations.Values.FirstOrDefault(item => string.Equals(item.SpeakerIp, speakerIp, StringComparison.OrdinalIgnoreCase))?.SpeakerUuid;

    public DateTimeOffset? LastEventUtc(string speakerIp)
        => _registrations.Values.Where(item => string.Equals(item.SpeakerIp, speakerIp, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.LastEventUtc).Max();

    internal static string? NormalizeUuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? value[5..] : value;
    }
}
