using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;

namespace SonosControl.Web.Services;

public sealed class MetricsCollector : IMetricsCollector
{
    private static readonly Meter Meter = new("SonosControl.Web", "1.0.0");

    private readonly Counter<long> _dashboardRefreshCounter = Meter.CreateCounter<long>("dashboard_refresh_total");
    private readonly Counter<long> _dashboardRefreshFailuresCounter = Meter.CreateCounter<long>("dashboard_refresh_failures_total");
    private readonly Histogram<double> _dashboardRefreshDurationMs = Meter.CreateHistogram<double>("dashboard_refresh_duration_ms");

    private readonly Counter<long> _sonosCommandErrorsCounter = Meter.CreateCounter<long>("sonos_command_errors_total");

    private readonly Counter<long> _playbackMonitorCycleCounter = Meter.CreateCounter<long>("playback_monitor_cycles_total");
    private readonly Histogram<double> _playbackMonitorCycleDurationMs = Meter.CreateHistogram<double>("playback_monitor_cycle_duration_ms");
    private readonly Counter<long> _playbackSessionWritesCounter = Meter.CreateCounter<long>("playback_session_writes_total");
    private readonly Counter<long> _playbackSessionWriteSkipsCounter = Meter.CreateCounter<long>("playback_session_write_skips_total");

    private long _dashboardRefreshSuccesses;
    private long _dashboardRefreshFailures;
    private long _dashboardSlowLaneRuns;
    private long _dashboardRefreshDurationMsTotal;
    private long _dashboardRefreshDurationMsMax;

    private long _sonosCommandErrors;

    private long _playbackMonitorCycles;
    private long _playbackMonitorSpeakersProcessedTotal;
    private long _playbackMonitorCycleDurationMsTotal;
    private long _playbackMonitorCycleDurationMsMax;
    private long _playbackSessionWrites;
    private long _playbackSessionWriteSkips;

    private readonly ConcurrentDictionary<string, long> _sonosCommandErrorsByCommand = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _dashboardDurationBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _playbackCycleDurationBuckets = new(StringComparer.OrdinalIgnoreCase);

    public void RecordDashboardRefreshResult(bool success, bool slowLane, TimeSpan duration)
    {
        var durationMs = Math.Max(0, (long)duration.TotalMilliseconds);

        if (success)
        {
            Interlocked.Increment(ref _dashboardRefreshSuccesses);
        }
        else
        {
            Interlocked.Increment(ref _dashboardRefreshFailures);
            _dashboardRefreshFailuresCounter.Add(1);
        }

        if (slowLane)
        {
            Interlocked.Increment(ref _dashboardSlowLaneRuns);
        }

        Interlocked.Add(ref _dashboardRefreshDurationMsTotal, durationMs);
        UpdateMax(ref _dashboardRefreshDurationMsMax, durationMs);
        IncrementBucket(_dashboardDurationBuckets, GetDurationBucket(durationMs));

        _dashboardRefreshCounter.Add(1, new KeyValuePair<string, object?>("lane", slowLane ? "slow" : "fast"));
        _dashboardRefreshDurationMs.Record(durationMs, new KeyValuePair<string, object?>("lane", slowLane ? "slow" : "fast"));
    }

    public void IncrementSonosCommandError(string commandName)
    {
        Interlocked.Increment(ref _sonosCommandErrors);
        _sonosCommandErrorsCounter.Add(1, new KeyValuePair<string, object?>("command", commandName));
        _sonosCommandErrorsByCommand.AddOrUpdate(commandName, 1, (_, current) => current + 1);
    }

    public void RecordPlaybackMonitorCycle(TimeSpan duration, int speakersProcessed, int sessionWrites)
    {
        var durationMs = Math.Max(0, (long)duration.TotalMilliseconds);

        Interlocked.Increment(ref _playbackMonitorCycles);
        Interlocked.Add(ref _playbackMonitorSpeakersProcessedTotal, Math.Max(0, speakersProcessed));
        Interlocked.Add(ref _playbackMonitorCycleDurationMsTotal, durationMs);
        UpdateMax(ref _playbackMonitorCycleDurationMsMax, durationMs);
        IncrementBucket(_playbackCycleDurationBuckets, GetDurationBucket(durationMs));

        _playbackMonitorCycleCounter.Add(1, new KeyValuePair<string, object?>("session_writes", sessionWrites));
        _playbackMonitorCycleDurationMs.Record(durationMs);
    }

    public void RecordPlaybackSessionWrite(bool skippedByThrottle)
    {
        if (skippedByThrottle)
        {
            Interlocked.Increment(ref _playbackSessionWriteSkips);
            _playbackSessionWriteSkipsCounter.Add(1);
            return;
        }

        Interlocked.Increment(ref _playbackSessionWrites);
        _playbackSessionWritesCounter.Add(1);
    }

    public AppMetricsSnapshot GetSnapshot()
    {
        var dashboardSuccesses = Interlocked.Read(ref _dashboardRefreshSuccesses);
        var dashboardFailures = Interlocked.Read(ref _dashboardRefreshFailures);
        var dashboardTotalRuns = dashboardSuccesses + dashboardFailures;
        var dashboardDurationTotalMs = Interlocked.Read(ref _dashboardRefreshDurationMsTotal);
        var dashboardAverageMs = dashboardTotalRuns == 0 ? 0 : (double)dashboardDurationTotalMs / dashboardTotalRuns;

        var playbackCycles = Interlocked.Read(ref _playbackMonitorCycles);
        var playbackDurationTotalMs = Interlocked.Read(ref _playbackMonitorCycleDurationMsTotal);
        var playbackAverageDurationMs = playbackCycles == 0 ? 0 : (double)playbackDurationTotalMs / playbackCycles;
        var averageSpeakers = playbackCycles == 0
            ? 0
            : (double)Interlocked.Read(ref _playbackMonitorSpeakersProcessedTotal) / playbackCycles;

        return new AppMetricsSnapshot(
            GeneratedAtUtc: DateTime.UtcNow,
            Dashboard: new DashboardRefreshMetrics(
                Successes: dashboardSuccesses,
                Failures: dashboardFailures,
                SlowLaneRuns: Interlocked.Read(ref _dashboardSlowLaneRuns),
                AverageDurationMs: dashboardAverageMs,
                MaxDurationMs: Interlocked.Read(ref _dashboardRefreshDurationMsMax),
                DurationBuckets: SnapshotDictionary(_dashboardDurationBuckets)),
            SonosCommands: new SonosCommandMetrics(
                TotalErrors: Interlocked.Read(ref _sonosCommandErrors),
                ErrorsByCommand: SnapshotDictionary(_sonosCommandErrorsByCommand)),
            PlaybackMonitor: new PlaybackMonitorMetrics(
                Cycles: playbackCycles,
                SessionWrites: Interlocked.Read(ref _playbackSessionWrites),
                SessionWriteSkips: Interlocked.Read(ref _playbackSessionWriteSkips),
                AverageCycleDurationMs: playbackAverageDurationMs,
                MaxCycleDurationMs: Interlocked.Read(ref _playbackMonitorCycleDurationMsMax),
                CycleDurationBuckets: SnapshotDictionary(_playbackCycleDurationBuckets),
                AverageSpeakersPerCycle: averageSpeakers));
    }

    private static IReadOnlyDictionary<string, long> SnapshotDictionary(ConcurrentDictionary<string, long> source)
        => source
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

    private static void IncrementBucket(ConcurrentDictionary<string, long> buckets, string bucket)
        => buckets.AddOrUpdate(bucket, 1, (_, current) => current + 1);

    private static string GetDurationBucket(long durationMs)
    {
        if (durationMs <= 100)
        {
            return "<=100ms";
        }

        if (durationMs <= 300)
        {
            return "<=300ms";
        }

        if (durationMs <= 1000)
        {
            return "<=1s";
        }

        if (durationMs <= 3000)
        {
            return "<=3s";
        }

        return ">3s";
    }

    private static void UpdateMax(ref long target, long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}
