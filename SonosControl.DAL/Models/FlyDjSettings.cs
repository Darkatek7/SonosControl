namespace SonosControl.DAL.Models;

/// <summary>Persisted preferences of the optional FlyDJ connectome radio DJ.</summary>
public class FlyDjSettings
{
    public const int DefaultVolume = 20;
    public const int DefaultMinDwellMinutes = 8;
    public const int DefaultMaxDwellMinutes = 45;
    public const int DefaultYieldCooldownMinutes = 60;
    public const int DefaultEngagementCheckSeconds = 30;

    public List<string> TargetSpeakerIps { get; set; } = new();
    public int Volume { get; set; } = DefaultVolume;

    /// <summary>Library station URLs FlyDJ may play. Empty means every library station.</summary>
    public List<string> CandidateStationUrls { get; set; } = new();

    public int MinDwellMinutes { get; set; } = DefaultMinDwellMinutes;
    public int MaxDwellMinutes { get; set; } = DefaultMaxDwellMinutes;
    public int YieldCooldownMinutes { get; set; } = DefaultYieldCooldownMinutes;
    public int EngagementCheckSeconds { get; set; } = DefaultEngagementCheckSeconds;
    public bool AutoResume { get; set; }
    public bool WasRunning { get; set; }

    public void Normalize()
    {
        TargetSpeakerIps ??= new();
        CandidateStationUrls ??= new();
        TargetSpeakerIps = TargetSpeakerIps.Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        CandidateStationUrls = CandidateStationUrls.Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Volume = Math.Clamp(Volume, 0, 100);
        MinDwellMinutes = Math.Clamp(MinDwellMinutes, 1, 240);
        MaxDwellMinutes = Math.Clamp(MaxDwellMinutes, MinDwellMinutes, 600);
        YieldCooldownMinutes = Math.Clamp(YieldCooldownMinutes, 0, 1440);
        EngagementCheckSeconds = Math.Clamp(EngagementCheckSeconds, 5, 600);
    }
}
