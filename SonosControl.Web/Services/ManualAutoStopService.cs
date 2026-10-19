using Microsoft.EntityFrameworkCore;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;
using SonosControl.Web.Data;
using SonosControl.Web.Models;

namespace SonosControl.Web.Services;

public readonly record struct ManualAutoStopStatus(DateTimeOffset StopAtUtc, string Reason, string? RuleKey = null);

/// <summary>Stores successful manual-playback deadlines and enforces them across app restarts.</summary>
public sealed class ManualAutoStopService(
    IServiceScopeFactory scopes,
    PlaybackCommandCoordinator commands,
    TimeProvider time,
    IConfiguration configuration,
    ILogger<ManualAutoStopService> logger) : BackgroundService, IManualAutoStopStatusProvider
{
    private const string ReleasedReason = "Daily rule released";
    private const string LiveSessionReason = "App playback session";
    private readonly TimeZoneInfo _timeZone = new ConfiguredTimeZoneService(
        configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfiguredTimeZoneService>.Instance).TimeZone;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Generation, DateTimeOffset Until)> _scheduleRetryUntil
        = new(StringComparer.OrdinalIgnoreCase);

    public async Task RegisterSuccessfulStartAsync(IEnumerable<string> speakerIps, CancellationToken cancellationToken = default)
    {
        var speakers = Normalize(speakerIps);
        if (speakers.Length == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await scope.ServiceProvider.GetRequiredService<ISettingsRepo>().GetSettings();
            if (settings is null) return;
            var now = time.GetUtcNow();
            foreach (var ip in speakers)
            {
                var prior = await db.ManualPlaybackDeadlines.FindAsync([ip], cancellationToken);
                var releasedKey = GetReleasedRuleKey(prior);
                var stop = ResolveDeadline(settings, ip, now, releasedKey);
                if (!stop.HasValue)
                {
                    var release = ReleaseForRule(settings.Speakers.FirstOrDefault(speaker => string.Equals(speaker.IpAddress, ip, StringComparison.OrdinalIgnoreCase)), releasedKey);
                    if (prior is not null && releasedKey is not null && release > now)
                    {
                        prior.StartedAtUtc = now.UtcDateTime;
                        prior.StopAtUtc = release.Value.UtcDateTime;
                        prior.Reason = LiveSessionReason;
                        prior.RuleKey = releasedKey;
                        prior.Generation++;
                    }
                    else if (prior is null)
                    {
                        // A successful app initiated start is a known session even when
                        // automation currently has no enabled stop rule.
                        db.ManualPlaybackDeadlines.Add(new ManualPlaybackDeadline
                        {
                            SpeakerIp = ip,
                            StartedAtUtc = now.UtcDateTime,
                            StopAtUtc = now.AddDays(1).UtcDateTime,
                            Reason = LiveSessionReason,
                            Generation = 1
                        });
                    }
                    else
                    {
                        // A successful manual restart replaces any prior schedule ownership.
                        // Keep the app's known session durable when no stop rule applies.
                        prior.StartedAtUtc = now.UtcDateTime;
                        prior.StopAtUtc = now.AddDays(1).UtcDateTime;
                        prior.Reason = LiveSessionReason;
                        prior.RuleKey = null;
                        prior.Generation++;
                    }
                    continue;
                }
                if (prior is null)
                    db.ManualPlaybackDeadlines.Add(new ManualPlaybackDeadline { SpeakerIp = ip, StartedAtUtc = now.UtcDateTime, StopAtUtc = stop.Value.StopAtUtc.UtcDateTime, Reason = stop.Value.Reason, RuleKey = AddReleasedRuleKey(stop.Value.RuleKey, releasedKey), Generation = 1 });
                else
                {
                    prior.StartedAtUtc = now.UtcDateTime;
                    prior.StopAtUtc = stop.Value.StopAtUtc.UtcDateTime;
                    prior.Reason = stop.Value.Reason;
                    prior.RuleKey = AddReleasedRuleKey(stop.Value.RuleKey, releasedKey);
                    prior.Generation++;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, long>> RegisterScheduleOccurrenceAsync(
        IEnumerable<string> speakerIps, ScheduleWindow window, CancellationToken cancellationToken = default,
        bool includeRecoveryTargets = false)
    {
        var speakers = Normalize(speakerIps);
        if (speakers.Length == 0 || !window.HasTimeSchedule || !window.IsEnabled) return new Dictionary<string, long>();
        var now = time.GetUtcNow();
        if (!TryGetWindowEnd(window, now, out var stopAt)) return new Dictionary<string, long>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await scope.ServiceProvider.GetRequiredService<ISettingsRepo>().GetSettings();
            if (settings is null) return new Dictionary<string, long>();
            var generations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var ip in speakers)
            {
                var configured = settings.Speakers.Any(speaker => string.Equals(speaker.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
                if (!configured || (!includeRecoveryTargets && !WindowTargetsSpeaker(window, settings, ip))) continue;
                var occurrenceStart = GetOccurrenceStart(window, settings, ip, now);
                var prior = await db.ManualPlaybackDeadlines.FindAsync([ip], cancellationToken);
                // A successful manual start may have happened while this scheduled execution
                // was running. Its later session owns the speaker, even when its active-window
                // deadline has the same Schedule reason.
                if (prior is not null && prior.StartedAtUtc > occurrenceStart.UtcDateTime) continue;
                var releasedKey = GetReleasedRuleKey(prior);
                releasedKey ??= DetermineReleasedRuleKey(settings, ip, occurrenceStart);
                var key = ComposeScheduleKey(window.Id, releasedKey);
                if (prior is null)
                {
                    db.ManualPlaybackDeadlines.Add(new ManualPlaybackDeadline
                    {
                        SpeakerIp = ip,
                        StartedAtUtc = now.UtcDateTime,
                        StopAtUtc = stopAt.UtcDateTime,
                        Reason = $"Schedule: {window.Name}",
                        RuleKey = key,
                        Generation = 1
                    });
                }
                else
                {
                    prior.StopAtUtc = stopAt.UtcDateTime;
                    prior.Reason = $"Schedule: {window.Name}";
                    prior.RuleKey = key;
                    prior.Generation++;
                }
                generations[ip] = prior?.Generation ?? 1;
            }
            await db.SaveChangesAsync(cancellationToken);
            return generations;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Refreshes the active schedule generations immediately before the scheduler waits for its stop lease.</summary>
    public async Task<IReadOnlyDictionary<string, long>> GetScheduleStopGenerationsAsync(
        IEnumerable<string> speakerIps, string windowId, DateTimeOffset stopAtUtc,
        CancellationToken cancellationToken = default)
    {
        var speakers = Normalize(speakerIps);
        var generations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (speakers.Length == 0 || string.IsNullOrWhiteSpace(windowId)) return generations;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var ip in speakers)
        {
            var row = await db.ManualPlaybackDeadlines.AsNoTracking()
                .FirstOrDefaultAsync(item => item.SpeakerIp == ip, cancellationToken);
            if (row is null || !row.Reason.StartsWith("Schedule:", StringComparison.Ordinal)
                || row.StopAtUtc != stopAtUtc.UtcDateTime
                || !string.Equals(RemoveReleasedRuleKey(row.RuleKey), $"schedule:{windowId}", StringComparison.Ordinal)) continue;
            generations[ip] = row.Generation;
        }
        return generations;
    }

    /// <summary>Validates due occurrence ownership under the room lease without consuming its retryable deadline.</summary>
    public async Task<IReadOnlySet<string>> GetValidDueScheduleStopGenerationsAsync(
        IEnumerable<string> speakerIps, string windowId, DateTimeOffset stopAtUtc,
        IReadOnlyDictionary<string, long> expectedGenerations,
        CancellationToken cancellationToken = default, bool requireDue = true)
    {
        var speakers = Normalize(speakerIps);
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (speakers.Length == 0 || string.IsNullOrWhiteSpace(windowId) || expectedGenerations is null) return valid;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = time.GetUtcNow();
            foreach (var ip in speakers)
            {
                if (!expectedGenerations.TryGetValue(ip, out var generation)) continue;
                var row = await db.ManualPlaybackDeadlines.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.SpeakerIp == ip, cancellationToken);
                if (row is not null && row.Generation == generation && row.StopAtUtc == stopAtUtc.UtcDateTime
                    && (!requireDue || row.StopAtUtc <= now.UtcDateTime) && row.Reason.StartsWith("Schedule:", StringComparison.Ordinal)
                    && string.Equals(RemoveReleasedRuleKey(row.RuleKey), $"schedule:{windowId}", StringComparison.Ordinal))
                    valid.Add(ip);
            }
            return valid;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Consumes exactly the due schedule generations captured by the scheduler before lease acquisition.</summary>
    public async Task<IReadOnlySet<string>> TryAcknowledgeScheduleStopAsync(
        IEnumerable<string> speakerIps, string windowId, DateTimeOffset stopAtUtc,
        IReadOnlyDictionary<string, long> expectedGenerations,
        CancellationToken cancellationToken = default, bool requireDue = true)
    {
        var speakers = Normalize(speakerIps);
        var acknowledged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (speakers.Length == 0 || string.IsNullOrWhiteSpace(windowId) || expectedGenerations is null) return acknowledged;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await scope.ServiceProvider.GetRequiredService<ISettingsRepo>().GetSettings();
            if (settings is null) return acknowledged;
            var now = time.GetUtcNow();
            foreach (var ip in speakers)
            {
                if (!expectedGenerations.TryGetValue(ip, out var generation)) continue;
                var row = await db.ManualPlaybackDeadlines.FirstOrDefaultAsync(item => item.SpeakerIp == ip, cancellationToken);
                if (row is null || row.Generation != generation || row.StopAtUtc != stopAtUtc.UtcDateTime
                    || (requireDue && row.StopAtUtc > now.UtcDateTime) || !row.Reason.StartsWith("Schedule:", StringComparison.Ordinal)
                    || !string.Equals(RemoveReleasedRuleKey(row.RuleKey), $"schedule:{windowId}", StringComparison.Ordinal)) continue;

                var releasedRuleKey = GetReleasedRuleKey(row);
                var release = ReleaseForRule(
                    settings.Speakers.FirstOrDefault(speaker => string.Equals(speaker.IpAddress, ip, StringComparison.OrdinalIgnoreCase)), releasedRuleKey);
                if (releasedRuleKey is not null && release > now && IsRuleConfigured(settings, ip, releasedRuleKey))
                {
                    row.Reason = ReleasedReason;
                    row.RuleKey = releasedRuleKey;
                    row.StopAtUtc = release.Value.UtcDateTime;
                    row.Generation++;
                }
                else db.Remove(row);
                acknowledged.Add(ip);
            }
            await db.SaveChangesAsync(cancellationToken);
            return acknowledged;
        }
        finally { _gate.Release(); }
    }

    public async Task<ManualAutoStopStatus?> GetNextStopAsync(string speakerIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(speakerIp)) return null;
        await using var scope = scopes.CreateAsyncScope();
        var settings = await scope.ServiceProvider.GetRequiredService<ISettingsRepo>().GetSettings();
        if (settings is null) return null;
        var now = time.GetUtcNow();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ip = speakerIp.Trim();
        var armed = await db.ManualPlaybackDeadlines.AsNoTracking().FirstOrDefaultAsync(item => item.SpeakerIp == ip, cancellationToken);
        if (armed is not null && armed.Reason is not (ReleasedReason or LiveSessionReason))
            return new(new DateTimeOffset(DateTime.SpecifyKind(armed.StopAtUtc, DateTimeKind.Utc)), armed.Reason, armed.RuleKey);

        // No armed row. Mirror what would actually stop playback instead of inventing a stop:
        // an active schedule always ends for its rooms, and a known app session that is past its
        // daily release (or has no row armed yet) will meet the next daily cutoff. Untracked
        // external playback and idle rooms with neither remain without a stop.
        var activeWindow = GetEffectiveWindow(settings, ip, now);
        if (activeWindow is not null && TryGetWindowEnd(activeWindow, now, out var scheduleEnd))
            return new(scheduleEnd, $"Schedule: {activeWindow.Name}", ComposeScheduleKey(activeWindow.Id, null));
        if (armed?.Reason == LiveSessionReason)
        {
            var speaker = settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
            var upcoming = speaker is null ? null : GetNextDailyCutoff(speaker, TimeZoneInfo.ConvertTime(now, _timeZone), now);
            if (upcoming.HasValue)
            {
                var cutoff = upcoming.Value.StopAtUtc;
                var nextSchedule = GetNextWindowStart(settings, ip, now);
                if (nextSchedule.HasValue && nextSchedule.Value < cutoff) cutoff = nextSchedule.Value;
                return new(cutoff, "Daily auto-stop", upcoming.Value.RuleKey);
            }
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Manual auto-stop processing failed; due stops will be retried."); }
            await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
        }
    }

    public async Task ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connector = scope.ServiceProvider.GetRequiredService<ISonosConnectorRepo>();
        await ReconcileReleasedOccurrencesAsync(db, scope.ServiceProvider.GetRequiredService<ISettingsRepo>(), cancellationToken);
        var due = await db.ManualPlaybackDeadlines.AsNoTracking().Where(item => item.StopAtUtc <= time.GetUtcNow().UtcDateTime)
            .OrderBy(item => item.StopAtUtc).Select(item => new { item.SpeakerIp, item.Generation, item.Reason }).ToListAsync(cancellationToken);
        var dueIps = due.Select(item => item.SpeakerIp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _scheduleRetryUntil.Keys.Where(ip => !dueIps.Contains(ip)).ToArray())
            _scheduleRetryUntil.TryRemove(stale, out _);
        foreach (var item in due)
        {
            if (item.Reason.StartsWith("Schedule:", StringComparison.Ordinal)
                && _scheduleRetryUntil.TryGetValue(item.SpeakerIp, out var backoff)
                && backoff.Generation == item.Generation && backoff.Until > time.GetUtcNow())
                continue;
            var initialMembers = await connector.GetAllSpeakersInGroup(item.SpeakerIp, cancellationToken);
            if (initialMembers is null || !initialMembers.Contains(item.SpeakerIp, StringComparer.OrdinalIgnoreCase))
            {
                await RetryLaterAsync(db, item.SpeakerIp, item.Generation, cancellationToken);
                continue;
            }
            var leaseTargets = Normalize(initialMembers.Append(item.SpeakerIp));
            await using var lease = await commands.EnterMandatoryStopAsync(leaseTargets, cancellationToken);
            await using var currentScope = scopes.CreateAsyncScope();
            var currentConnector = currentScope.ServiceProvider.GetRequiredService<ISonosConnectorRepo>();
            SonosSettings? stopSettings = null;

            // Phase 1 (serialized): validate ownership and handle every non-stopping outcome.
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // The reconciliation query above can leave rows tracked in its context. Read the
                // current generation from a new context after acquiring ownership so a new
                // playback start cannot be hidden by EF's identity map.
                var currentDb = currentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var currentSettingsRepo = currentScope.ServiceProvider.GetRequiredService<ISettingsRepo>();
                var deadline = await currentDb.ManualPlaybackDeadlines.FirstOrDefaultAsync(row => row.SpeakerIp == item.SpeakerIp, cancellationToken);
                if (deadline is null || deadline.Generation != item.Generation || deadline.StopAtUtc > time.GetUtcNow().UtcDateTime) continue;
                try
                {
                    var settings = await currentSettingsRepo.GetSettings();
                    if (settings is null) throw new InvalidOperationException("Settings could not be read before enforcing the stop.");
                    var currentMembers = await currentConnector.GetAllSpeakersInGroup(item.SpeakerIp, cancellationToken);
                    if (currentMembers is null || !leaseTargets.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Normalize(currentMembers.Append(item.SpeakerIp))))
                        throw new InvalidOperationException("Speaker group changed while the stop was being acquired.");
                    var now = time.GetUtcNow();
                    var activeWindow = GetEffectiveWindow(settings, item.SpeakerIp, now);
                    if (deadline.Reason == ReleasedReason)
                    {
                        currentDb.Remove(deadline);
                        await currentDb.SaveChangesAsync(cancellationToken);
                        continue;
                    }
                    if (deadline.Reason == LiveSessionReason)
                    {
                        ArmNextDailyCutoff(deadline, settings, now);
                        await currentDb.SaveChangesAsync(cancellationToken);
                        continue;
                    }
                    if (deadline.Reason == "Daily auto-stop" && deadline.RuleKey is not null
                        && !IsRuleConfigured(settings, item.SpeakerIp, deadline.RuleKey))
                    {
                        ArmNextDailyCutoff(deadline, settings, now);
                        await currentDb.SaveChangesAsync(cancellationToken);
                        continue;
                    }
                    if (deadline.Reason == "Daily auto-stop" && deadline.RuleKey is not null
                        && HasEffectiveScheduleStartedAfterRuleCutoff(settings, item.SpeakerIp, deadline.RuleKey, deadline.StartedAtUtc, now))
                    {
                        MarkOccurrenceReleased(deadline, settings, item.SpeakerIp, now, activeWindow);
                        await currentDb.SaveChangesAsync(cancellationToken);
                        continue;
                    }
                    if (deadline.Reason == "Daily auto-stop" && deadline.RuleKey is not null
                        && IsAtOrAfterRelease(settings, item.SpeakerIp, deadline.RuleKey, now))
                    {
                        ArmNextDailyCutoff(deadline, settings, now);
                        await currentDb.SaveChangesAsync(cancellationToken);
                        continue;
                    }
                    if (deadline.Reason.StartsWith("Schedule:", StringComparison.Ordinal) && activeWindow is not null
                        && TryGetWindowEnd(activeWindow, now, out var nextStop))
                    {
                        deadline.StopAtUtc = nextStop.UtcDateTime;
                        deadline.Reason = $"Schedule: {activeWindow.Name}";
                        deadline.RuleKey = ComposeScheduleKey(activeWindow.Id, GetReleasedRuleKey(deadline));
                        deadline.Generation++;
                        await currentDb.SaveChangesAsync(cancellationToken);
                        continue;
                    }
                    stopSettings = settings;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not enforce auto-stop for {SpeakerIp}; retrying shortly.", deadline.SpeakerIp);
                    BackOffRetry(deadline);
                    await currentDb.SaveChangesAsync(cancellationToken);
                }
            }
            finally { _gate.Release(); }
            if (stopSettings is null) continue;

            // Phase 2 (not serialized): device I/O. The mandatory-stop lease keeps other playback
            // commands off these rooms, and the generation is re-checked before acknowledging, so
            // a slow speaker does not block registrations for other rooms.
            Exception? stopFailure = null;
            try { await StopSpeakerSafelyAsync(currentConnector, item.SpeakerIp, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { stopFailure = ex; }

            // Phase 3 (serialized): acknowledge only if no newer session replaced the deadline.
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await using var finalScope = scopes.CreateAsyncScope();
                var finalDb = finalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var current = await finalDb.ManualPlaybackDeadlines.FirstOrDefaultAsync(row => row.SpeakerIp == item.SpeakerIp, cancellationToken);
                if (current is null || current.Generation != item.Generation) continue;
                var now = time.GetUtcNow();
                if (stopFailure is not null)
                {
                    logger.LogWarning(stopFailure, "Could not enforce auto-stop for {SpeakerIp}; retrying shortly.", current.SpeakerIp);
                    BackOffRetry(current);
                    await finalDb.SaveChangesAsync(cancellationToken);
                    continue;
                }
                var releasedRuleKey = GetReleasedRuleKey(current);
                var release = ReleaseForRule(stopSettings.Speakers.FirstOrDefault(speaker => string.Equals(speaker.IpAddress, item.SpeakerIp, StringComparison.OrdinalIgnoreCase)), releasedRuleKey);
                if (releasedRuleKey is not null && release > now)
                {
                    current.Reason = ReleasedReason;
                    current.RuleKey = releasedRuleKey;
                    current.StopAtUtc = release.Value.UtcDateTime;
                    current.Generation++;
                }
                else finalDb.Remove(current);
                await finalDb.SaveChangesAsync(cancellationToken);
                _scheduleRetryUntil.TryRemove(item.SpeakerIp, out _);
                logger.LogInformation("Stopped {SpeakerIp} at its manual auto-stop deadline ({Reason}).", current.SpeakerIp, current.Reason);
            }
            finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// Manual rows move their deadline back by the retry delay. Schedule rows keep deadline and
    /// generation (they are the scheduler's occurrence identity), so their retry is delayed in memory.
    /// </summary>
    private void BackOffRetry(ManualPlaybackDeadline deadline)
    {
        var retryAt = time.GetUtcNow().Add(RetryDelay);
        if (deadline.Reason.StartsWith("Schedule:", StringComparison.Ordinal))
        {
            _scheduleRetryUntil[deadline.SpeakerIp] = (deadline.Generation, retryAt);
            return;
        }
        deadline.StopAtUtc = retryAt.UtcDateTime;
        deadline.Generation++;
    }

    private async Task StopSpeakerSafelyAsync(ISonosConnectorRepo connector, string ip, CancellationToken cancellationToken)
    {
        var initial = await connector.GetAllSpeakersInGroup(ip, cancellationToken)
            ?? throw new InvalidOperationException("Speaker group could not be verified.");
        var members = initial.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (members.Count == 0 || !members.Contains(ip)) throw new InvalidOperationException("Speaker group could not be verified.");
        if (members.Count > 1)
        {
            await connector.UngroupSpeaker(ip, cancellationToken);
            var current = await connector.GetAllSpeakersInGroup(ip, cancellationToken)
                ?? throw new InvalidOperationException("Speaker isolation could not be verified.");
            var isolated = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (isolated.Count != 1 || !isolated.Contains(ip)) throw new InvalidOperationException("Speaker isolation could not be verified.");
        }
        await connector.PausePlaying(ip, cancellationToken);
    }

    private ManualAutoStopStatus? ResolveDeadline(SonosSettings settings, string ip, DateTimeOffset now, string? releasedRuleKey = null)
    {
        var candidates = new List<ManualAutoStopStatus>();
        var activeWindow = GetEffectiveWindow(settings, ip, now);
        if (activeWindow is not null && TryGetWindowEnd(activeWindow, now, out var scheduleEnd))
        {
            var occurrenceStart = GetOccurrenceStart(activeWindow, settings, ip, now);
            releasedRuleKey ??= DetermineReleasedRuleKey(settings, ip, occurrenceStart);
            return new(scheduleEnd, $"Schedule: {activeWindow.Name}", ComposeScheduleKey(activeWindow.Id, releasedRuleKey));
        }
        var speaker = settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
        if (speaker?.ManualAutoStopMinutes is not > 0) return null;
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        if (TryGetActiveDailyRule(speaker, local, out var ruleDay, out var ruleDate))
        {
            var key = DailyRuleKey(ruleDay, ruleDate);
            var stop = now.AddMinutes(speaker.ManualAutoStopMinutes.Value);
            var releaseUtc = ResolveLocalTime(GetReleaseDate(ruleDay, ruleDate).ToDateTime(ruleDay.ReleaseTime));
            if (releaseUtc <= now) return Earliest(candidates);
            if (releaseUtc < stop) stop = releaseUtc;
            var nextSchedule = GetNextWindowStart(settings, ip, now);
            if (nextSchedule.HasValue && nextSchedule.Value < stop) stop = nextSchedule.Value;
            if (!string.Equals(releasedRuleKey, key, StringComparison.Ordinal))
                candidates.Add(new(stop, "Daily auto-stop", key));
        }
        else
        {
            var upcoming = GetNextDailyCutoff(speaker, local, now);
            if (upcoming.HasValue)
            {
                var cutoff = upcoming.Value.StopAtUtc;
                var nextSchedule = GetNextWindowStart(settings, ip, now);
                if (nextSchedule.HasValue && nextSchedule.Value < cutoff) cutoff = nextSchedule.Value;
                if (!string.Equals(releasedRuleKey, upcoming.Value.RuleKey, StringComparison.Ordinal))
                    candidates.Add(new(cutoff, "Daily auto-stop", upcoming.Value.RuleKey));
            }
        }
        return Earliest(candidates);
    }

    private ScheduleWindow? GetEffectiveWindow(SonosSettings settings, string speakerIp, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        return ScheduleWindowEvaluator.SelectActiveWindow(settings.ScheduleWindows?.Where(window => window.HasTimeSchedule && WindowTargetsSpeaker(window, settings, speakerIp)), local, settings);
    }

    private static bool WindowTargetsSpeaker(ScheduleWindow window, SonosSettings settings, string ip)
    {
        var scene = window.Playback;
        return scene?.Enabled == true && SceneTargetResolver.Resolve(scene, settings).Contains(ip, StringComparer.OrdinalIgnoreCase);
    }

    private bool TryGetWindowEnd(ScheduleWindow window, DateTimeOffset now, out DateTimeOffset end)
    {
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        if (window.StopTime <= window.StartTime && TimeOnly.FromDateTime(local.DateTime) >= window.StartTime) date = date.AddDays(1);
        var localEnd = date.ToDateTime(window.StopTime);
        end = ResolveLocalTime(localEnd);
        return end > now;
    }

    private DateTimeOffset GetOccurrenceStart(ScheduleWindow window, SonosSettings settings, string ip, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        var today = DateOnly.FromDateTime(local.DateTime);
        DateTimeOffset? latestStart = null;
        for (var offset = -1; offset <= 0; offset++)
        {
            var start = ResolveLocalTime(today.AddDays(offset).ToDateTime(window.StartTime));
            if (start > now || (latestStart.HasValue && start <= latestStart.Value)) continue;
            var candidateEnd = GetWindowEndFromStart(window, start);
            if (candidateEnd > now && string.Equals(GetEffectiveWindow(settings, ip, start)?.Id, window.Id, StringComparison.Ordinal))
                latestStart = start;
        }
        return latestStart ?? now;
    }

    private DateTimeOffset GetWindowEndFromStart(ScheduleWindow window, DateTimeOffset start)
    {
        var localStart = TimeZoneInfo.ConvertTime(start, _timeZone);
        var startDate = DateOnly.FromDateTime(localStart.DateTime);
        var endDate = window.StopTime <= window.StartTime ? startDate.AddDays(1) : startDate;
        return ResolveLocalTime(endDate.ToDateTime(window.StopTime));
    }

    private DateTimeOffset? GetNextWindowStart(SonosSettings settings, string ip, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        for (var offset = 0; offset < 8; offset++)
        {
            var date = DateOnly.FromDateTime(local.DateTime).AddDays(offset);
            DateTimeOffset? earliest = null;
            foreach (var window in settings.ScheduleWindows.Where(item => item.IsEnabled && item.HasTimeSchedule && WindowTargetsSpeaker(item, settings, ip)))
            {
                var candidate = ResolveLocalTime(date.ToDateTime(window.StartTime));
                if (candidate > now && (!earliest.HasValue || candidate < earliest.Value)
                    && string.Equals(GetEffectiveWindow(settings, ip, candidate)?.Id, window.Id, StringComparison.Ordinal)) earliest = candidate;
            }
            if (earliest.HasValue) return earliest;
        }
        return null;
    }

    private DateTimeOffset ResolveLocalTime(DateTime local)
    {
        while (_timeZone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return _timeZone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, _timeZone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _timeZone));
    }

    private (DateTimeOffset StopAtUtc, string RuleKey)? GetNextDailyCutoff(SonosSpeaker speaker, DateTimeOffset local, DateTimeOffset now)
    {
        var currentDate = DateOnly.FromDateTime(local.DateTime);
        for (var offset = 0; offset < 8; offset++)
        {
            var date = currentDate.AddDays(offset);
            if (speaker.ManualAutoStopDays?.TryGetValue(date.DayOfWeek, out var rule) != true || rule is null || !rule.Enabled) continue;
            var cutoff = ResolveLocalTime(date.ToDateTime(rule.StopTime));
            if (cutoff <= now) continue;
            return (cutoff, DailyRuleKey(rule, date));
        }
        return null;
    }

    private bool TryGetActiveDailyRule(SonosSpeaker speaker, DateTimeOffset local, out ManualAutoStopDay rule, out DateOnly ruleDate)
    {
        var date = DateOnly.FromDateTime(local.DateTime);
        var timeOfDay = TimeOnly.FromDateTime(local.DateTime);
        if (speaker.ManualAutoStopDays?.TryGetValue(date.DayOfWeek, out rule!) == true && rule.Enabled
            && (rule.StopTime >= rule.ReleaseTime
                ? timeOfDay >= rule.StopTime
                : timeOfDay >= rule.StopTime && timeOfDay < rule.ReleaseTime))
        {
            ruleDate = date;
            return true;
        }
        var previous = date.AddDays(-1);
        if (speaker.ManualAutoStopDays?.TryGetValue(previous.DayOfWeek, out rule!) == true && rule.Enabled
            && rule.StopTime >= rule.ReleaseTime && timeOfDay < rule.ReleaseTime)
        {
            ruleDate = previous;
            return true;
        }
        rule = null!;
        ruleDate = default;
        return false;
    }

    private static DateOnly GetReleaseDate(ManualAutoStopDay rule, DateOnly ruleDate)
        => rule.StopTime >= rule.ReleaseTime ? ruleDate.AddDays(1) : ruleDate;

    private static string DailyRuleKey(ManualAutoStopDay rule, DateOnly date)
        => $"daily:{date:yyyy-MM-dd}:{rule.StopTime:HH:mm}-{rule.ReleaseTime:HH:mm}";

    private static DateOnly? ParseRuleDate(string ruleKey)
    {
        var dailyKey = ExtractDailyRuleKey(ruleKey);
        var parts = dailyKey?.Split(':');
        return parts is { Length: >= 2 } && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", out var date) ? date : null;
    }

    private void MarkOccurrenceReleased(ManualPlaybackDeadline deadline, SonosSettings settings, string ip, DateTimeOffset now, ScheduleWindow? activeWindow)
    {
        var rule = settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
        var dailyKey = ExtractDailyRuleKey(deadline.RuleKey);
        var ruleReleased = dailyKey is not null
            && HasEffectiveScheduleStartedAfterRuleCutoff(settings, ip, dailyKey, deadline.StartedAtUtc, now);
        var release = ReleaseForRule(rule, dailyKey);
        if (activeWindow is not null && TryGetWindowEnd(activeWindow, now, out var scheduleEnd))
        {
            deadline.Reason = $"Schedule: {activeWindow.Name}";
            deadline.RuleKey = ComposeScheduleKey(activeWindow.Id, ruleReleased ? dailyKey : null);
            deadline.StopAtUtc = scheduleEnd.UtcDateTime;
        }
        else if (ruleReleased)
        {
            deadline.Reason = LiveSessionReason;
            deadline.RuleKey = dailyKey;
            deadline.StopAtUtc = release is not null && release > now ? release.Value.UtcDateTime : now.UtcDateTime;
        }
        else return;
        deadline.Generation++;
    }

    private DateTimeOffset? ReleaseForRule(SonosSpeaker? speaker, string? ruleKey)
    {
        if (speaker is null || ruleKey is null) return null;
        var date = ParseRuleDate(ruleKey);
        if (!date.HasValue || !speaker.ManualAutoStopDays.TryGetValue(date.Value.DayOfWeek, out var rule) || !rule.Enabled) return null;
        return ResolveLocalTime(GetReleaseDate(rule, date.Value).ToDateTime(rule.ReleaseTime));
    }

    private async Task ReconcileReleasedOccurrencesAsync(ApplicationDbContext db, ISettingsRepo settingsRepo, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var rows = await db.ManualPlaybackDeadlines.ToListAsync(cancellationToken);
            if (rows.Count == 0) return;
            var settings = await settingsRepo.GetSettings();
            if (settings is null) return;
            foreach (var row in rows)
            {
                var dailyKey = row.Reason is ReleasedReason or LiveSessionReason ? row.RuleKey : ExtractDailyRuleKey(row.RuleKey);
                if (dailyKey is null)
                {
                    if (row.Reason != LiveSessionReason) continue;
                    var speaker = settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, row.SpeakerIp, StringComparison.OrdinalIgnoreCase));
                    var nextRule = speaker is null ? null : GetNextDailyCutoff(speaker, TimeZoneInfo.ConvertTime(now, _timeZone), now);
                    if (nextRule.HasValue)
                    {
                        row.Reason = "Daily auto-stop";
                        row.RuleKey = nextRule.Value.RuleKey;
                        row.StopAtUtc = nextRule.Value.StopAtUtc.UtcDateTime;
                        row.Generation++;
                    }
                    else if (row.StopAtUtc <= now.UtcDateTime)
                    {
                        row.StopAtUtc = now.AddDays(1).UtcDateTime;
                        row.Generation++;
                    }
                    continue;
                }
                var release = ReleaseForRule(settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, row.SpeakerIp, StringComparison.OrdinalIgnoreCase)), dailyKey);
                if (release.HasValue && release.Value <= now)
                {
                    if (row.Reason == "Daily auto-stop")
                        ArmNextDailyCutoff(row, settings, now);
                    else if (row.Reason == ReleasedReason) db.Remove(row);
                    else if (row.Reason == LiveSessionReason) ArmNextDailyCutoff(row, settings, now);
                    else
                    {
                        row.RuleKey = RemoveReleasedRuleKey(row.RuleKey);
                        row.Generation++;
                    }
                    continue;
                }
                if (row.Reason == LiveSessionReason)
                {
                    if (!IsRuleConfigured(settings, row.SpeakerIp, dailyKey)) ArmNextDailyCutoff(row, settings, now);
                    continue;
                }
                if (row.Reason != "Daily auto-stop") continue;
                if (!IsRuleConfigured(settings, row.SpeakerIp, dailyKey))
                {
                    ArmNextDailyCutoff(row, settings, now);
                    continue;
                }
                var active = GetEffectiveWindow(settings, row.SpeakerIp, now);
                if (!HasEffectiveScheduleStartedAfterRuleCutoff(settings, row.SpeakerIp, dailyKey, row.StartedAtUtc, now)) continue;
                MarkOccurrenceReleased(row, settings, row.SpeakerIp, now, active);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private void ArmNextDailyCutoff(ManualPlaybackDeadline deadline, SonosSettings settings, DateTimeOffset now)
    {
        var speaker = settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, deadline.SpeakerIp, StringComparison.OrdinalIgnoreCase));
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        var upcoming = speaker is null ? null : GetNextDailyCutoff(speaker, local, now);
        if (upcoming is null)
        {
            // The current app session still exists after release. Retain a row so a later
            // configured cutoff can be armed after settings change/restart reconciliation.
            deadline.StopAtUtc = now.AddDays(1).UtcDateTime;
            deadline.Reason = LiveSessionReason;
            deadline.RuleKey = null;
        }
        else
        {
            deadline.StopAtUtc = upcoming.Value.StopAtUtc.UtcDateTime;
            deadline.Reason = "Daily auto-stop";
            deadline.RuleKey = upcoming.Value.RuleKey;
        }
        deadline.Generation++;
    }

    private async Task RetryLaterAsync(ApplicationDbContext db, string ip, long generation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var row = await db.ManualPlaybackDeadlines.FindAsync([ip], cancellationToken);
            if (row is null || row.Generation != generation) return;
            // Scheduler leases use the original end time and generation as occurrence identity.
            // Keep both unchanged until the pause has succeeded and been acknowledged.
            BackOffRetry(row);
            if (row.Reason.StartsWith("Schedule:", StringComparison.Ordinal)) return;
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private bool HasEffectiveScheduleStartedAfterRuleCutoff(SonosSettings settings, string ip, string ruleKey, DateTime startedAtUtc, DateTimeOffset now)
    {
        var ruleDate = ParseRuleDate(ruleKey);
        var speaker = settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
        if (!ruleDate.HasValue || speaker?.ManualAutoStopDays?.TryGetValue(ruleDate.Value.DayOfWeek, out var rule) != true || rule is null || !rule.Enabled)
            return false;
        var cutoff = ResolveLocalTime(ruleDate.Value.ToDateTime(rule.StopTime));
        var started = new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc));
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started, _timeZone).DateTime).AddDays(-1);
        var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _timeZone).DateTime);
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            foreach (var window in settings.ScheduleWindows.Where(item => item.IsEnabled && item.HasTimeSchedule && WindowTargetsSpeaker(item, settings, ip)))
            {
                var start = ResolveLocalTime(date.ToDateTime(window.StartTime));
                if (start <= started || start > now || start < cutoff) continue;
                if (string.Equals(GetEffectiveWindow(settings, ip, start)?.Id, window.Id, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    private static bool IsRuleConfigured(SonosSettings settings, string ip, string ruleKey)
    {
        var dailyRuleKey = ExtractDailyRuleKey(ruleKey);
        var date = dailyRuleKey is null ? null : ParseRuleDate(dailyRuleKey);
        var speaker = settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
        if (!date.HasValue || speaker?.ManualAutoStopDays?.TryGetValue(date.Value.DayOfWeek, out var rule) != true || rule is null) return false;
        return rule.Enabled && string.Equals(DailyRuleKey(rule, date.Value), dailyRuleKey, StringComparison.Ordinal);
    }

    private bool IsAtOrAfterRelease(SonosSettings settings, string ip, string ruleKey, DateTimeOffset now)
    {
        var release = ReleaseForRule(settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, ip, StringComparison.OrdinalIgnoreCase)), ruleKey);
        return release.HasValue && now >= release.Value;
    }

    private static string? GetReleasedRuleKey(ManualPlaybackDeadline? deadline)
        => deadline?.Reason is ReleasedReason or LiveSessionReason ? deadline.RuleKey : ExtractReleasedRuleKey(deadline?.RuleKey);

    private static string? ExtractReleasedRuleKey(string? key)
    {
        var marker = key?.IndexOf("|released:", StringComparison.Ordinal);
        return marker is >= 0 ? key![(marker.Value + 10)..] : null;
    }

    private static string? ExtractDailyRuleKey(string? key)
    {
        if (key is null) return null;
        if (key.StartsWith("daily:", StringComparison.Ordinal)) return RemoveReleasedRuleKey(key);
        var marker = key.IndexOf("|released:daily:", StringComparison.Ordinal);
        return marker >= 0 ? key[(marker + "|released:".Length)..] : null;
    }

    private static string ComposeScheduleKey(string scheduleId, string? releasedRuleKey)
        => string.IsNullOrWhiteSpace(releasedRuleKey) ? $"schedule:{scheduleId}" : $"schedule:{scheduleId}|released:{releasedRuleKey}";

    private static string? AddReleasedRuleKey(string? key, string? releasedRuleKey)
    {
        if (key is null || string.IsNullOrWhiteSpace(releasedRuleKey)) return key;
        var baseKey = key.Split("|released:", StringSplitOptions.None)[0];
        return $"{baseKey}|released:{releasedRuleKey}";
    }

    private static string? RemoveReleasedRuleKey(string? key)
        => key?.Split("|released:", StringSplitOptions.None)[0];

    private static ManualAutoStopStatus? Earliest(List<ManualAutoStopStatus> candidates)
        => candidates.Count == 0 ? null : candidates.MinBy(item => item.StopAtUtc);

    private string? DetermineReleasedRuleKey(SonosSettings settings, string ip, DateTimeOffset now)
    {
        var speaker = settings.Speakers.FirstOrDefault(item => string.Equals(item.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
        if (speaker is null) return null;
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        if (TryGetActiveDailyRule(speaker, local, out var active, out var activeDate))
            return DailyRuleKey(active, activeDate);
        return null;
    }

    private static string[] Normalize(IEnumerable<string> ips) => ips.Where(ip => !string.IsNullOrWhiteSpace(ip))
        .Select(ip => ip.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
