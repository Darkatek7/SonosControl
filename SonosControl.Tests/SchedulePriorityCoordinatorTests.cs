using SonosControl.Web.Services;
using Xunit;

namespace SonosControl.Tests;

public class SchedulePriorityCoordinatorTests
{
    [Fact]
    public void NotifySonosConfigStart_BlocksSameWindowUntilWindowChanges()
    {
        var coordinator = new SchedulePriorityCoordinator();

        coordinator.NotifySonosConfigStart("window-a");

        Assert.True(coordinator.IsSonosConfigOwningPlayback);
        Assert.False(coordinator.CanApplyWindow("window-a"));

        coordinator.NotifySonosConfigStop("window-a");

        Assert.False(coordinator.IsSonosConfigOwningPlayback);
        Assert.False(coordinator.CanApplyWindow("window-a"));

        coordinator.ObserveActiveWindow("window-b");

        Assert.True(coordinator.CanApplyWindow("window-b"));
        Assert.True(coordinator.CanApplyWindow("window-a"));
    }

    [Fact]
    public void ObserveActiveWindow_ClearsBlockedWindowAfterNoActiveWindow()
    {
        var coordinator = new SchedulePriorityCoordinator();

        coordinator.NotifySonosConfigStop("window-a");
        Assert.False(coordinator.CanApplyWindow("window-a"));

        coordinator.ObserveActiveWindow(null);

        Assert.True(coordinator.CanApplyWindow("window-a"));
    }
}
