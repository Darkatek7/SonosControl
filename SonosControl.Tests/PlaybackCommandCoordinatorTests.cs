using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public sealed class PlaybackCommandCoordinatorTests
{
    [Fact]
    public async Task VolumeWaitsForAutomationWithoutPreemptingItOrClaimingPlayback()
    {
        var coordinator = new PlaybackCommandCoordinator(TimeProvider.System);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var automation = await coordinator.EnterAutomationAsync(["room-a"], start);
        Assert.NotNull(automation);
        var volumeTask = coordinator.EnterVolumeAsync("room-a");
        Assert.False(volumeTask.IsCompleted);
        Assert.False(automation.WasPreempted);
        Assert.False(automation.CancellationToken.IsCancellationRequested);
        await automation.DisposeAsync();
        await using (var volume = await volumeTask.WaitAsync(TimeSpan.FromSeconds(2)))
            Assert.Equal(PlaybackCommandOrigin.Volume, volume.Origin);
        Assert.Empty(coordinator.ManualSpeakersSince(start));
        await using var nextStation = await coordinator.EnterAutomationAsync(["room-a"], start);
        Assert.NotNull(nextStation);
    }

    [Fact]
    public async Task ManualLeaseCancelsOverlappingAutomationBeforeAcquiringTarget()
    {
        var coordinator = new PlaybackCommandCoordinator(TimeProvider.System);
        var automation = await coordinator.EnterAutomationAsync(["room-a"], DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.NotNull(automation);
        var canceled = false;
        using var registration = automation.CancellationToken.Register(() => canceled = true);

        var manualTask = coordinator.EnterManualAsync(["ROOM-A"]);
        await Task.Delay(20);
        await automation.DisposeAsync();
        await using var manual = await manualTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(canceled);
        Assert.True(automation.WasPreempted);
        Assert.Contains("ROOM-A", coordinator.ManualSpeakersSince(DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task ManualLeaseDoesNotPreemptIndependentAutomationTargets()
    {
        var coordinator = new PlaybackCommandCoordinator(TimeProvider.System);
        await using var automation = await coordinator.EnterAutomationAsync(["room-a"], DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.NotNull(automation);

        await using var manual = await coordinator.EnterManualAsync(["room-b"]);

        Assert.False(automation.CancellationToken.IsCancellationRequested);
        Assert.False(automation.WasPreempted);
    }

    [Fact]
    public async Task MandatoryStopCanAcquireAfterManualOverrideWithoutExtendingManualClaim()
    {
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero));
        var coordinator = new PlaybackCommandCoordinator(clock);
        await using (await coordinator.EnterManualAsync(["room-a"])) { }
        var claimedAt = Assert.Single(coordinator.ManualSpeakersSince(clock.GetUtcNow().AddSeconds(-1)));

        await using var stop = await coordinator.EnterMandatoryStopAsync(["ROOM-A"]);

        Assert.Equal("room-a", claimedAt);
        Assert.Equal(PlaybackCommandOrigin.SleepTimer, stop.Origin);
        Assert.Contains("room-a", coordinator.ManualSpeakersSince(clock.GetUtcNow().AddSeconds(-1)));
    }

    [Fact]
    public async Task ManualClaimSuppressesOnlyOccurrencesThatAlreadyStarted()
    {
        var coordinator = new PlaybackCommandCoordinator(TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        await using (await coordinator.EnterManualAsync(["room-a"]))
            Assert.Null(await coordinator.EnterAutomationAsync(["room-a"], now.AddMinutes(-1)));
        await using var nextOccurrence = await coordinator.EnterAutomationAsync(["room-a"], now.AddMinutes(1));
        Assert.NotNull(nextOccurrence);
    }

    [Fact]
    public async Task ManualClaimUsesAcquisitionTimeWhenItWaitsAcrossAnOccurrenceBoundary()
    {
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero));
        var coordinator = new PlaybackCommandCoordinator(clock);
        var automation = await coordinator.EnterAutomationAsync(["room-a"], clock.GetUtcNow().AddMinutes(-1));
        Assert.NotNull(automation);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = automation.CancellationToken.Register(canceled.SetResult);

        var manualTask = coordinator.EnterManualAsync(["room-a"]);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var nextOccurrence = clock.GetUtcNow().AddMinutes(1);
        clock.SetUtcNow(nextOccurrence.AddSeconds(5));
        await automation.DisposeAsync();
        await using (await manualTask.WaitAsync(TimeSpan.FromSeconds(2))) { }

        Assert.Null(await coordinator.EnterAutomationAsync(["room-a"], nextOccurrence));
        await using var futureOccurrence = await coordinator.EnterAutomationAsync(["room-a"], nextOccurrence.AddMinutes(1));
        Assert.NotNull(futureOccurrence);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
