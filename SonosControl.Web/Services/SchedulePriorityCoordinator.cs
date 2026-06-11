namespace SonosControl.Web.Services;

public interface ISchedulePriorityCoordinator
{
    bool IsSonosConfigOwningPlayback { get; }
    void ObserveActiveWindow(string? activeWindowId);
    void NotifySonosConfigStart(string? suppressedWindowId);
    void NotifySonosConfigStop(string? suppressedWindowId);
    bool CanApplyWindow(string? activeWindowId);
}

public sealed class SchedulePriorityCoordinator : ISchedulePriorityCoordinator
{
    private readonly object _sync = new();
    private bool _sonosConfigOwnsPlayback;
    private string? _blockedWindowId;

    public bool IsSonosConfigOwningPlayback
    {
        get
        {
            lock (_sync)
            {
                return _sonosConfigOwnsPlayback;
            }
        }
    }

    public void ObserveActiveWindow(string? activeWindowId)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_blockedWindowId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(activeWindowId)
                || !string.Equals(_blockedWindowId, activeWindowId, StringComparison.OrdinalIgnoreCase))
            {
                _blockedWindowId = null;
            }
        }
    }

    public void NotifySonosConfigStart(string? suppressedWindowId)
    {
        lock (_sync)
        {
            _sonosConfigOwnsPlayback = true;
            _blockedWindowId = NormalizeWindowId(suppressedWindowId);
        }
    }

    public void NotifySonosConfigStop(string? suppressedWindowId)
    {
        lock (_sync)
        {
            _sonosConfigOwnsPlayback = false;
            _blockedWindowId = NormalizeWindowId(suppressedWindowId);
        }
    }

    public bool CanApplyWindow(string? activeWindowId)
    {
        lock (_sync)
        {
            if (_sonosConfigOwnsPlayback)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(activeWindowId))
            {
                return false;
            }

            return !string.Equals(_blockedWindowId, activeWindowId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? NormalizeWindowId(string? windowId)
        => string.IsNullOrWhiteSpace(windowId) ? null : windowId.Trim();
}
