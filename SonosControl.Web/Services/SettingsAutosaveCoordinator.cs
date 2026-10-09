using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services;

public enum SettingsSaveState
{
    Saved,
    Saving,
    Error
}

public sealed class SettingsAutosaveCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(350);
    private readonly ISettingsRepo _settingsRepo;
    private readonly ILogger<SettingsAutosaveCoordinator> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private SettingsChanges? _pendingChanges;
    private SonosSettings? _pendingSource;
    private SettingsChanges? _lastFailedChanges;
    private long _generation;

    public SettingsAutosaveCoordinator(ISettingsRepo settingsRepo, ILogger<SettingsAutosaveCoordinator> logger)
    {
        _settingsRepo = settingsRepo;
        _logger = logger;
    }

    public SettingsSaveState State { get; private set; } = SettingsSaveState.Saved;
    public string? ErrorMessage { get; private set; }
    public event Action? StateChanged;

    public Task QueueSaveAsync(SonosSettings settings, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        CancellationToken token;
        long generation;
        lock (_sync)
        {
            _pendingChanges = SettingsChanges.Capture(settings);
            _pendingSource = settings;
            generation = ++_generation;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
            SetState(SettingsSaveState.Saving, null);
        }

        return SaveGenerationAsync(generation, immediate ? TimeSpan.Zero : DefaultDebounce, token);
    }

    public Task RetryAsync()
    {
        lock (_sync)
        {
            if (_lastFailedChanges is null) return Task.CompletedTask;
            _pendingChanges = _lastFailedChanges;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            return SaveGenerationAsync(++_generation, TimeSpan.Zero, _debounceCts.Token);
        }
    }

    private async Task SaveGenerationAsync(long generation, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                SettingsChanges? changes;
                SonosSettings? source;
                lock (_sync)
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    changes = _pendingChanges;
                    source = _pendingSource;
                }

                if (changes is null)
                {
                    return;
                }

                await _settingsRepo.UpdateSettingsAsync(changes.Apply, cancellationToken);
                if (source is not null) SettingsChanges.Accept(source, changes);
                lock (_sync)
                {
                    if (generation == _generation)
                    {
                        _lastFailedChanges = null;
                        SetState(SettingsSaveState.Saved, null);
                    }
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer change superseded this save.
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (generation != _generation) return;
                _lastFailedChanges = _pendingChanges;
                SetState(SettingsSaveState.Error, ex.Message);
            }

            _logger.LogWarning(ex, "Settings autosave failed.");
        }
    }

    private void SetState(SettingsSaveState state, string? error)
    {
        State = state;
        ErrorMessage = error;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        // In-flight saves still release the gate after disposal.
    }
}
