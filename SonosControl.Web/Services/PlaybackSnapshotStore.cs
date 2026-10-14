using System.Collections.Concurrent;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

/// <summary>A last-known playback observation for one configured Sonos room.</summary>
public sealed record PlaybackSnapshot(
    string SpeakerIp,
    string SpeakerName,
    bool IsPlaying,
    int? Volume,
    string? CurrentStationUrl,
    SonosTrackInfo? TrackInfo,
    bool IsStale,
    DateTimeOffset LastCheckedUtc,
    DateTimeOffset? LastSuccessfulRefreshUtc,
    string? Error,
    long Generation = 0)
{
    public PlaybackSnapshot Copy() => this with
    {
        TrackInfo = TrackInfo is null ? null : new SonosTrackInfo
        {
            Title = TrackInfo.Title,
            Artist = TrackInfo.Artist,
            Album = TrackInfo.Album,
            AlbumArtUri = TrackInfo.AlbumArtUri,
            StreamContent = TrackInfo.StreamContent
        }
    };
}

/// <summary>Thread-safe, process-local snapshots shared by playback UI circuits.</summary>
public sealed class PlaybackSnapshotStore
{
    private readonly ConcurrentDictionary<string, PlaybackSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _snapshotSync = new();
    private readonly object _settingsSync = new();
    private string? _settingsFingerprint;
    private readonly HashSet<string> _configuredIps = new(StringComparer.OrdinalIgnoreCase);
    private long _configurationEpoch;
    private long _configurationReadSequence;
    private long _latestConfigurationRead;
    private long _settingsMutationVersion;
    private bool _hasConfiguration;

    public event Action? Changed;
    public event Action? SettingsChanged;
    public long ConfigurationEpoch { get { lock (_snapshotSync) return _configurationEpoch; } }

    public (long Sequence, long MutationVersion) BeginConfigurationRead()
    {
        lock (_snapshotSync) return (++_configurationReadSequence, _settingsMutationVersion);
    }

    public bool TryApplyConfiguration(long sequence, long mutationVersion, IEnumerable<string> speakerIps, out long epoch)
    {
        var next = speakerIps.Where(ip => !string.IsNullOrWhiteSpace(ip)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        lock (_snapshotSync)
        {
            epoch = _configurationEpoch;
            if (sequence < _latestConfigurationRead || mutationVersion != _settingsMutationVersion) return false;
            _latestConfigurationRead = sequence;
            if (!_hasConfiguration || !_configuredIps.SetEquals(next))
            {
                foreach (var removed in _generations.Keys.Where(ip => !next.Contains(ip)).ToArray())
                {
                    _generations.AddOrUpdate(removed, 1, (_, current) => current + 1);
                    changed |= _snapshots.TryRemove(removed, out _);
                }
                _configuredIps.Clear();
                _configuredIps.UnionWith(next);
                _configurationEpoch++;
                _hasConfiguration = true;
                changed = true;
            }
            epoch = _configurationEpoch;
        }
        if (changed) Changed?.Invoke();
        return true;
    }

    public bool IsConfigured(string speakerIp, long epoch)
    {
        lock (_snapshotSync) return epoch == _configurationEpoch && (!_hasConfiguration || _configuredIps.Contains(speakerIp));
    }

    public long BeginProbe(string speakerIp, long expectedEpoch)
    {
        lock (_snapshotSync)
        {
            if (expectedEpoch != _configurationEpoch || (_hasConfiguration && !_configuredIps.Contains(speakerIp))) return 0;
            return _generations.AddOrUpdate(speakerIp, 1, (_, current) => current + 1);
        }
    }

    public IReadOnlyList<PlaybackSnapshot> GetAll()
        => _snapshots.Values.Select(snapshot => snapshot.Copy()).OrderBy(snapshot => snapshot.SpeakerName).ToArray();

    public PlaybackSnapshot? Get(string speakerIp)
        => _snapshots.TryGetValue(speakerIp, out var snapshot) ? snapshot.Copy() : null;

    public long BeginProbe(string speakerIp)
    {
        lock (_snapshotSync)
        {
            if (_hasConfiguration && !_configuredIps.Contains(speakerIp)) return 0;
            return _generations.AddOrUpdate(speakerIp, 1, (_, current) => current + 1);
        }
    }

    public bool IsCurrentProbe(string speakerIp, long generation)
    {
        lock (_snapshotSync) return _generations.TryGetValue(speakerIp, out var current) && current == generation;
    }

    public bool TrySet(PlaybackSnapshot snapshot, long generation)
    {
        lock (_snapshotSync)
        {
            if ((_hasConfiguration && !_configuredIps.Contains(snapshot.SpeakerIp))
                || !_generations.TryGetValue(snapshot.SpeakerIp, out var current) || current != generation) return false;
            _snapshots[snapshot.SpeakerIp] = (snapshot with { Generation = generation }).Copy();
        }
        Changed?.Invoke();
        return true;
    }

    public bool TrySet(PlaybackSnapshot snapshot, long generation, long expectedEpoch)
    {
        lock (_snapshotSync)
        {
            if (expectedEpoch != _configurationEpoch || (_hasConfiguration && !_configuredIps.Contains(snapshot.SpeakerIp))
                || !_generations.TryGetValue(snapshot.SpeakerIp, out var current) || current != generation) return false;
            _snapshots[snapshot.SpeakerIp] = (snapshot with { Generation = generation }).Copy();
        }
        Changed?.Invoke();
        return true;
    }

    public void Set(PlaybackSnapshot snapshot)
    {
        lock (_snapshotSync)
        {
            var generation = _generations.AddOrUpdate(snapshot.SpeakerIp, 1, (_, current) => current + 1);
            _snapshots[snapshot.SpeakerIp] = (snapshot with { Generation = generation }).Copy();
        }
        Changed?.Invoke();
    }

    public void MarkExpiredStale(DateTimeOffset now, TimeSpan staleAfter)
    {
        var changed = false;
        lock (_snapshotSync)
        {
            foreach (var pair in _snapshots)
            {
                var snapshot = pair.Value;
                if (!snapshot.IsStale && now - snapshot.LastSuccessfulRefreshUtc.GetValueOrDefault(DateTimeOffset.MinValue) >= staleAfter)
                {
                    _snapshots[pair.Key] = snapshot with { IsStale = true, LastCheckedUtc = now, Error = "Playback status is delayed" };
                    changed = true;
                }
            }
        }
        if (changed) Changed?.Invoke();
    }

    public DateTimeOffset? GetNextStaleDeadline(TimeSpan staleAfter)
    {
        lock (_snapshotSync)
            return _snapshots.Values.Where(snapshot => !snapshot.IsStale && snapshot.LastSuccessfulRefreshUtc.HasValue)
                .Select(snapshot => snapshot.LastSuccessfulRefreshUtc!.Value + staleAfter)
                .OrderBy(deadline => deadline).Cast<DateTimeOffset?>().FirstOrDefault();
    }

    public void RemoveExcept(IEnumerable<string> speakerIps)
    {
        var configured = speakerIps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        lock (_snapshotSync)
        {
            foreach (var ip in _generations.Keys.Where(ip => !configured.Contains(ip)).ToArray())
            {
                _generations.AddOrUpdate(ip, 1, (_, current) => current + 1);
                changed |= _snapshots.TryRemove(ip, out _);
            }
            foreach (var ip in _snapshots.Keys.Where(ip => !configured.Contains(ip)))
                changed |= _snapshots.TryRemove(ip, out _);
            if (!_hasConfiguration || !_configuredIps.SetEquals(configured))
            {
                _configuredIps.Clear();
                _configuredIps.UnionWith(configured);
                _configurationEpoch++;
                _hasConfiguration = true;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    public void NotifySettingsChanged(string fingerprint)
    {
        var changed = false;
        lock (_settingsSync)
        {
            if (!string.Equals(_settingsFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _settingsFingerprint = fingerprint;
                changed = true;
            }
        }
        if (changed) SettingsChanged?.Invoke();
    }

    /// <summary>Publishes an explicit settings change, useful to writers that already know an update occurred.</summary>
    public void PublishSettingsChanged()
    {
        lock (_snapshotSync) _settingsMutationVersion++;
        SettingsChanged?.Invoke();
    }
}
