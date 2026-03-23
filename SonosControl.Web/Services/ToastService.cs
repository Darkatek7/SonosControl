namespace SonosControl.Web.Services;

public record ToastItem(
    string Id = Guid.NewGuid().ToString("N"),
    string Message = "",
    ToastSeverity Severity = ToastSeverity.Info,
    int DurationMs = 4000
);

public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Danger
}

public class ToastService : INotifyPropertyChanged
{
    private readonly List<ToastItem> _toasts = new();
    private const int MaxVisibleToasts = 3;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ToastsChanged;

    public IReadOnlyList<ToastItem> VisibleToasts => _toasts.AsReadOnly();

    public void Show(string message, ToastSeverity severity = ToastSeverity.Info, int durationMs = 4000)
    {
        var toast = new ToastItem(Message: message, Severity: severity, DurationMs: durationMs);
        _toasts.Add(toast);

        if (_toasts.Count > MaxVisibleToasts)
        {
            _toasts.RemoveAt(0);
        }

        OnToastsChanged();
        _ = AutoDismissAsync(toast);
    }

    public void Dismiss(string id)
    {
        var toast = _toasts.FirstOrDefault(t => t.Id == id);
        if (toast != null)
        {
            _toasts.Remove(toast);
            OnToastsChanged();
        }
    }

    private async Task AutoDismissAsync(ToastItem toast)
    {
        await Task.Delay(toast.DurationMs);
        Dismiss(toast.Id);
    }

    private void OnToastsChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleToasts)));
        ToastsChanged?.Invoke();
    }
}