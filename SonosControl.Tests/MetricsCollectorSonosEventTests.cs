using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class MetricsCollectorSonosEventTests
{
    [Fact]
    public void SnapshotReportsSonosEventCountersAndTheLatestSpeakerSplit()
    {
        var metrics = new MetricsCollector();
        metrics.RecordSonosEventReceived(queued: true);
        metrics.RecordSonosEventReceived(queued: true);
        metrics.RecordSonosEventReceived(queued: false);
        metrics.RecordSonosEventProbe();
        metrics.RecordSonosSubscriptionFailure();
        metrics.SetSonosEventSpeakers(4, 2, 1);
        metrics.SetSonosEventSpeakers(2, 1, 2);

        var events = metrics.GetSnapshot().SonosEvents;

        Assert.Equal(new SonosEventMetrics(EventsReceived: 2, EventsDropped: 1, ProbesRequested: 1, SubscriptionFailures: 1,
            ActiveSubscriptions: 2, EventedSpeakers: 1, PolledSpeakers: 2), events);
    }
}
