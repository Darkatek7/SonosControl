using System.ComponentModel.DataAnnotations;

namespace SonosControl.Web.Models;

/// <summary>One FlyDJ station choice, with its audition evidence and the listener's reaction.</summary>
public sealed class FlyDjDecision
{
    public long Id { get; set; }
    public DateTime DecidedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(256)]
    public string StationName { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    public string StationUrl { get; set; } = string.Empty;

    /// <summary>JSON array of the speaker IPs the station was started on.</summary>
    public string SpeakerIpsJson { get; set; } = "[]";

    /// <summary>JSON array of the scored candidates considered for this decision.</summary>
    public string CandidatesJson { get; set; } = "[]";

    [MaxLength(1024)]
    public string Reason { get; set; } = string.Empty;

    public double BrainWeight { get; set; }
    public double PriorWeight { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public double? DwellSeconds { get; set; }
    public bool OverriddenByUser { get; set; }
}
