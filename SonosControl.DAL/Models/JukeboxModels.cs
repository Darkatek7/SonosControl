namespace SonosControl.DAL.Models;

public class JukeboxSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxSuggestionsPerUserPerHour { get; set; } = 5;
    public int MaxVotesPerUserPerHour { get; set; } = 20;
    public int PlayedSuggestionRetentionDays { get; set; } = 14;
}

public class JukeboxSuggestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ResourceUri { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string SuggestedBy { get; set; } = "Unknown";
    public DateTime SuggestedUtc { get; set; } = DateTime.UtcNow;
    public List<JukeboxVote> Votes { get; set; } = new();
    public bool IsPlayed { get; set; }
    public DateTime? PlayedUtc { get; set; }
    public string? PlayedOnSpeakerIp { get; set; }
}

public class JukeboxVote
{
    public string UserName { get; set; } = "Unknown";
    public DateTime VotedUtc { get; set; } = DateTime.UtcNow;
}
