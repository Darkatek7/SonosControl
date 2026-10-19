using System.Text.Json.Serialization;

namespace SonosControl.Web.Services.FlyDj;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlyDjConductorStatus
{
    Stopped,
    Starting,
    Auditioning,
    Playing,
    Yielded,
    Error
}

/// <summary>A room FlyDJ stepped away from because someone (or a schedule) took over.</summary>
public sealed record FlyDjYieldedSpeaker(string SpeakerIp, string SpeakerName, DateTimeOffset UntilUtc, string Reason);

/// <summary>One audited station with the evidence behind its probability of being chosen.</summary>
/// <remarks>A failed audition has <see cref="Error"/> set, no brain score and probability zero.</remarks>
public sealed record FlyDjCandidateScore(
    string Name,
    string Url,
    double? BrainScore,
    double PriorScore,
    double FinalScore,
    double Probability,
    FlyDjAudioFeatures? Features,
    string? Error);

public sealed record FlyDjDecisionView(
    long Id,
    DateTimeOffset DecidedAtUtc,
    string StationName,
    string StationUrl,
    IReadOnlyList<string> SpeakerIps,
    string Reason,
    IReadOnlyList<FlyDjCandidateScore> Candidates,
    double? DwellSeconds,
    bool OverriddenByUser);

/// <summary>Everything the FlyDJ page needs; published by <see cref="FlyDjStateStore"/>.</summary>
public sealed record FlyDjDashboardState(
    FlyDjConductorStatus Status,
    string? StatusMessage,
    bool SidecarReachable,
    FlyDjModelInfo? Model,
    FlyDjSimulationState? Simulation,
    string? CurrentStationName,
    string? CurrentStationUrl,
    IReadOnlyList<string> ActiveSpeakerIps,
    IReadOnlyList<FlyDjYieldedSpeaker> YieldedSpeakers,
    FlyDjDecisionView? LastDecision,
    IReadOnlyList<FlyDjDecisionView> RecentDecisions,
    DateTimeOffset? NextDecisionAtUtc,
    DateTimeOffset UpdatedUtc)
{
    public bool IsScheduled { get; init; }

    public static FlyDjDashboardState Initial(DateTimeOffset now) => new(
        FlyDjConductorStatus.Stopped, null, false, null, null, null, null,
        Array.Empty<string>(), Array.Empty<FlyDjYieldedSpeaker>(), null, Array.Empty<FlyDjDecisionView>(), null, now);
}

/// <summary>Result of a conditional geometry download.</summary>
public sealed record FlyDjGeometryResult(byte[]? Content, string? ETag, bool NotModified);

/// <summary>Outcome of the sidecar health probe. Never thrown, so callers can degrade gracefully.</summary>
public sealed record FlyDjHealth(bool Reachable, bool Ok, string? ModelStatus, string? Message);
