using System;
using System.Collections.Generic;

namespace SonosControl.Web.Services;

public interface IMetricsCollector
{
    void RecordDashboardRefreshResult(bool success, bool slowLane, TimeSpan duration);
    void IncrementSonosCommandError(string commandName);
    void RecordPlaybackMonitorCycle(TimeSpan duration, int speakersProcessed, int sessionWrites);
    void RecordPlaybackSessionWrite(bool skippedByThrottle);
    AppMetricsSnapshot GetSnapshot();
}

public sealed record AppMetricsSnapshot(
    DateTime GeneratedAtUtc,
    DashboardRefreshMetrics Dashboard,
    SonosCommandMetrics SonosCommands,
    PlaybackMonitorMetrics PlaybackMonitor);

public sealed record DashboardRefreshMetrics(
    long Successes,
    long Failures,
    long SlowLaneRuns,
    double AverageDurationMs,
    double MaxDurationMs,
    IReadOnlyDictionary<string, long> DurationBuckets);

public sealed record SonosCommandMetrics(
    long TotalErrors,
    IReadOnlyDictionary<string, long> ErrorsByCommand);

public sealed record PlaybackMonitorMetrics(
    long Cycles,
    long SessionWrites,
    long SessionWriteSkips,
    double AverageCycleDurationMs,
    double MaxCycleDurationMs,
    IReadOnlyDictionary<string, long> CycleDurationBuckets,
    double AverageSpeakersPerCycle);
