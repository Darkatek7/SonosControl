using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public interface ICollaborativeJukeboxService
{
    Task<JukeboxState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<JukeboxOperationResult> SuggestAsync(
        string resourceUri,
        string? title,
        string? artist,
        string? performedBy,
        CancellationToken cancellationToken = default);
    Task<JukeboxOperationResult> VoteAsync(
        string suggestionId,
        string? performedBy,
        CancellationToken cancellationToken = default);
    Task<JukeboxOperationResult> PlayTopSuggestionAsync(
        string? speakerIp,
        string? performedBy,
        CancellationToken cancellationToken = default);
    Task<JukeboxOperationResult> RemoveSuggestionAsync(
        string suggestionId,
        string? performedBy,
        CancellationToken cancellationToken = default);
    Task<JukeboxOperationResult> UpdateSettingsAsync(
        JukeboxSettings updatedSettings,
        string? performedBy,
        CancellationToken cancellationToken = default);
}

public sealed record JukeboxState(
    JukeboxSettings Settings,
    IReadOnlyList<JukeboxSuggestion> Suggestions);

public sealed record JukeboxOperationResult(
    bool Success,
    string Message,
    JukeboxSuggestion? Suggestion = null);

public sealed class CollaborativeJukeboxService : ICollaborativeJukeboxService
{
    private const int MaxSuggestionsPerHourUpperBound = 50;
    private const int MaxVotesPerHourUpperBound = 200;
    private const int MaxRetentionDays = 90;
    private const int MaxSuggestionHistory = 500;

    private readonly IUnitOfWork _uow;
    private readonly ActionLogger _actionLogger;
    private readonly ILogger<CollaborativeJukeboxService> _logger;

    public CollaborativeJukeboxService(
        IUnitOfWork uow,
        ActionLogger actionLogger,
        ILogger<CollaborativeJukeboxService> logger)
    {
        _uow = uow;
        _actionLogger = actionLogger;
        _logger = logger;
    }

    public async Task<JukeboxState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = await LoadSettingsAsync(cancellationToken);

        if (Cleanup(settings))
        {
            await _uow.ISettingsRepo.WriteSettings(settings);
        }

        return BuildState(settings);
    }

    public async Task<JukeboxOperationResult> SuggestAsync(
        string resourceUri,
        string? title,
        string? artist,
        string? performedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceUri))
        {
            return new JukeboxOperationResult(false, "Resource URI is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var user = NormalizeUser(performedBy);
        var settings = await LoadSettingsAsync(cancellationToken);

        if (!settings.Jukebox.Enabled)
        {
            return new JukeboxOperationResult(false, "Collaborative Jukebox is disabled.");
        }

        Cleanup(settings);

        var now = DateTime.UtcNow;
        var suggestionLimit = Math.Clamp(settings.Jukebox.MaxSuggestionsPerUserPerHour, 1, MaxSuggestionsPerHourUpperBound);
        var suggestionsLastHour = settings.JukeboxSuggestions.Count(s =>
            string.Equals(s.SuggestedBy, user, StringComparison.OrdinalIgnoreCase)
            && s.SuggestedUtc >= now.AddHours(-1));
        if (suggestionsLastHour >= suggestionLimit)
        {
            return new JukeboxOperationResult(false, $"Suggestion limit reached ({suggestionLimit}/hour).");
        }

        var normalizedUri = resourceUri.Trim();
        var duplicatePending = settings.JukeboxSuggestions.Any(s =>
            !s.IsPlayed
            && string.Equals(s.ResourceUri, normalizedUri, StringComparison.OrdinalIgnoreCase));
        if (duplicatePending)
        {
            return new JukeboxOperationResult(false, "This URI is already queued in Jukebox.");
        }

        var suggestion = new JukeboxSuggestion
        {
            Id = Guid.NewGuid().ToString("N"),
            ResourceUri = normalizedUri,
            Title = NormalizeOptional(title),
            Artist = NormalizeOptional(artist),
            SuggestedBy = user,
            SuggestedUtc = now
        };

        settings.JukeboxSuggestions.Add(suggestion);
        TrimSuggestionHistory(settings.JukeboxSuggestions);

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("JukeboxSuggested", $"{suggestion.Title ?? suggestion.ResourceUri} by {user}");

        return new JukeboxOperationResult(true, "Suggestion added.", CloneSuggestion(suggestion));
    }

    public async Task<JukeboxOperationResult> VoteAsync(
        string suggestionId,
        string? performedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            return new JukeboxOperationResult(false, "Suggestion id is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var user = NormalizeUser(performedBy);
        var settings = await LoadSettingsAsync(cancellationToken);

        if (!settings.Jukebox.Enabled)
        {
            return new JukeboxOperationResult(false, "Collaborative Jukebox is disabled.");
        }

        Cleanup(settings);

        var suggestion = settings.JukeboxSuggestions.FirstOrDefault(s =>
            string.Equals(s.Id, suggestionId, StringComparison.OrdinalIgnoreCase));
        if (suggestion is null || suggestion.IsPlayed)
        {
            return new JukeboxOperationResult(false, "Suggestion not found.");
        }

        suggestion.Votes ??= new();
        var alreadyVoted = suggestion.Votes.Any(v =>
            string.Equals(v.UserName, user, StringComparison.OrdinalIgnoreCase));
        if (alreadyVoted)
        {
            return new JukeboxOperationResult(false, "You already voted for this suggestion.");
        }

        var now = DateTime.UtcNow;
        var voteLimit = Math.Clamp(settings.Jukebox.MaxVotesPerUserPerHour, 1, MaxVotesPerHourUpperBound);
        var votesLastHour = settings.JukeboxSuggestions
            .SelectMany(s => s.Votes ?? Enumerable.Empty<JukeboxVote>())
            .Count(v =>
                string.Equals(v.UserName, user, StringComparison.OrdinalIgnoreCase)
                && v.VotedUtc >= now.AddHours(-1));
        if (votesLastHour >= voteLimit)
        {
            return new JukeboxOperationResult(false, $"Vote limit reached ({voteLimit}/hour).");
        }

        suggestion.Votes.Add(new JukeboxVote
        {
            UserName = user,
            VotedUtc = now
        });

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("JukeboxVoted", $"{user} voted for {suggestion.Id}");

        return new JukeboxOperationResult(true, "Vote submitted.", CloneSuggestion(suggestion));
    }

    public async Task<JukeboxOperationResult> PlayTopSuggestionAsync(
        string? speakerIp,
        string? performedBy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = NormalizeUser(performedBy);
        var settings = await LoadSettingsAsync(cancellationToken);

        if (!settings.Jukebox.Enabled)
        {
            return new JukeboxOperationResult(false, "Collaborative Jukebox is disabled.");
        }

        Cleanup(settings);

        var targetSpeakerIp = ResolveTargetSpeakerIp(speakerIp, settings);
        if (string.IsNullOrWhiteSpace(targetSpeakerIp))
        {
            return new JukeboxOperationResult(false, "A configured speaker is required.");
        }

        var winner = GetPendingOrdered(settings.JukeboxSuggestions).FirstOrDefault();
        if (winner is null)
        {
            return new JukeboxOperationResult(false, "No pending Jukebox suggestions.");
        }

        if (string.IsNullOrWhiteSpace(winner.ResourceUri))
        {
            return new JukeboxOperationResult(false, "Winning suggestion has no playable URI.");
        }

        try
        {
            await _uow.ISonosConnectorRepo.AddUriToQueue(
                targetSpeakerIp,
                winner.ResourceUri,
                metadata: null,
                enqueueAsNext: true,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue winning Jukebox suggestion {SuggestionId}.", winner.Id);
            return new JukeboxOperationResult(false, $"Could not enqueue winner: {ex.Message}");
        }

        winner.IsPlayed = true;
        winner.PlayedUtc = DateTime.UtcNow;
        winner.PlayedOnSpeakerIp = targetSpeakerIp;

        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("JukeboxPlayed", $"{winner.Id} queued next on {targetSpeakerIp} by {user}");

        return new JukeboxOperationResult(true, "Winning suggestion queued as next track.", CloneSuggestion(winner));
    }

    public async Task<JukeboxOperationResult> RemoveSuggestionAsync(
        string suggestionId,
        string? performedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            return new JukeboxOperationResult(false, "Suggestion id is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var user = NormalizeUser(performedBy);
        var settings = await LoadSettingsAsync(cancellationToken);
        Cleanup(settings);

        var suggestion = settings.JukeboxSuggestions.FirstOrDefault(s =>
            string.Equals(s.Id, suggestionId, StringComparison.OrdinalIgnoreCase));
        if (suggestion is null)
        {
            return new JukeboxOperationResult(false, "Suggestion not found.");
        }

        settings.JukeboxSuggestions.Remove(suggestion);
        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("JukeboxSuggestionRemoved", $"{suggestion.Id} removed by {user}");

        return new JukeboxOperationResult(true, "Suggestion removed.", CloneSuggestion(suggestion));
    }

    public async Task<JukeboxOperationResult> UpdateSettingsAsync(
        JukeboxSettings updatedSettings,
        string? performedBy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = NormalizeUser(performedBy);
        var settings = await LoadSettingsAsync(cancellationToken);

        settings.Jukebox.Enabled = updatedSettings.Enabled;
        settings.Jukebox.MaxSuggestionsPerUserPerHour = Math.Clamp(updatedSettings.MaxSuggestionsPerUserPerHour, 1, MaxSuggestionsPerHourUpperBound);
        settings.Jukebox.MaxVotesPerUserPerHour = Math.Clamp(updatedSettings.MaxVotesPerUserPerHour, 1, MaxVotesPerHourUpperBound);
        settings.Jukebox.PlayedSuggestionRetentionDays = Math.Clamp(updatedSettings.PlayedSuggestionRetentionDays, 1, MaxRetentionDays);

        Cleanup(settings);
        await _uow.ISettingsRepo.WriteSettings(settings);
        await _actionLogger.LogAsync("JukeboxSettingsUpdated", $"Updated by {user}");

        return new JukeboxOperationResult(true, "Jukebox settings updated.");
    }

    private async Task<SonosSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = await _uow.ISettingsRepo.GetSettings() ?? new SonosSettings();
        settings.Jukebox ??= new();
        settings.JukeboxSuggestions ??= new();
        settings.Speakers ??= new();

        foreach (var suggestion in settings.JukeboxSuggestions)
        {
            suggestion.Votes ??= new();
        }

        return settings;
    }

    private static bool Cleanup(SonosSettings settings)
    {
        settings.Jukebox ??= new();
        settings.JukeboxSuggestions ??= new();

        var changed = false;
        var retentionDays = Math.Clamp(settings.Jukebox.PlayedSuggestionRetentionDays, 1, MaxRetentionDays);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        var beforeCount = settings.JukeboxSuggestions.Count;
        settings.JukeboxSuggestions.RemoveAll(s =>
            s.IsPlayed
            && s.PlayedUtc.HasValue
            && s.PlayedUtc.Value < cutoff);
        if (settings.JukeboxSuggestions.Count != beforeCount)
        {
            changed = true;
        }

        foreach (var suggestion in settings.JukeboxSuggestions)
        {
            suggestion.Votes ??= new();
        }

        if (TrimSuggestionHistory(settings.JukeboxSuggestions))
        {
            changed = true;
        }

        return changed;
    }

    private static bool TrimSuggestionHistory(List<JukeboxSuggestion> suggestions)
    {
        if (suggestions.Count <= MaxSuggestionHistory)
        {
            return false;
        }

        var kept = suggestions
            .OrderBy(s => s.IsPlayed)
            .ThenByDescending(s => s.SuggestedUtc)
            .Take(MaxSuggestionHistory)
            .ToList();

        suggestions.Clear();
        suggestions.AddRange(kept);
        return true;
    }

    private static string? ResolveTargetSpeakerIp(string? requestedSpeakerIp, SonosSettings settings)
    {
        settings.Speakers ??= new();
        var configuredIps = settings.Speakers
            .Select(s => s.IpAddress)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedSpeakerIp)
            && configuredIps.Contains(requestedSpeakerIp, StringComparer.OrdinalIgnoreCase))
        {
            return requestedSpeakerIp.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.IP_Adress)
            && configuredIps.Contains(settings.IP_Adress, StringComparer.OrdinalIgnoreCase))
        {
            return settings.IP_Adress;
        }

        return configuredIps.FirstOrDefault();
    }

    private static IEnumerable<JukeboxSuggestion> GetPendingOrdered(IEnumerable<JukeboxSuggestion> suggestions)
    {
        return suggestions
            .Where(s => !s.IsPlayed)
            .OrderByDescending(s => s.Votes?.Count ?? 0)
            .ThenBy(s => s.SuggestedUtc)
            .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static JukeboxState BuildState(SonosSettings settings)
    {
        var ordered = settings.JukeboxSuggestions
            .OrderBy(s => s.IsPlayed)
            .ThenByDescending(s => s.Votes?.Count ?? 0)
            .ThenBy(s => s.SuggestedUtc)
            .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Select(CloneSuggestion)
            .ToList();

        return new JukeboxState(CloneSettings(settings.Jukebox), ordered);
    }

    private static JukeboxSettings CloneSettings(JukeboxSettings source)
    {
        return new JukeboxSettings
        {
            Enabled = source.Enabled,
            MaxSuggestionsPerUserPerHour = source.MaxSuggestionsPerUserPerHour,
            MaxVotesPerUserPerHour = source.MaxVotesPerUserPerHour,
            PlayedSuggestionRetentionDays = source.PlayedSuggestionRetentionDays
        };
    }

    private static JukeboxSuggestion CloneSuggestion(JukeboxSuggestion source)
    {
        return new JukeboxSuggestion
        {
            Id = source.Id,
            ResourceUri = source.ResourceUri,
            Title = source.Title,
            Artist = source.Artist,
            SuggestedBy = source.SuggestedBy,
            SuggestedUtc = source.SuggestedUtc,
            Votes = (source.Votes ?? new())
                .Select(v => new JukeboxVote
                {
                    UserName = v.UserName,
                    VotedUtc = v.VotedUtc
                })
                .ToList(),
            IsPlayed = source.IsPlayed,
            PlayedUtc = source.PlayedUtc,
            PlayedOnSpeakerIp = source.PlayedOnSpeakerIp
        };
    }

    private static string NormalizeUser(string? performedBy)
        => string.IsNullOrWhiteSpace(performedBy) ? "Unknown" : performedBy.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
