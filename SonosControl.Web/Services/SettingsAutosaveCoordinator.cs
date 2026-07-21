using System.Text.Json;
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
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISettingsRepo _settingsRepo;
    private readonly ILogger<SettingsAutosaveCoordinator> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private SonosSettings? _pendingSettings;
    private SonosSettings? _lastFailedSettings;
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
            _pendingSettings = Clone(settings);
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
        SonosSettings? retry;
        lock (_sync)
        {
            retry = _lastFailedSettings is null ? null : Clone(_lastFailedSettings);
        }

        return retry is null ? Task.CompletedTask : QueueSaveAsync(retry, immediate: true);
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
                SonosSettings? snapshot;
                lock (_sync)
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    snapshot = _pendingSettings is null ? null : Clone(_pendingSettings);
                }

                if (snapshot is null)
                {
                    return;
                }

                await _settingsRepo.WriteSettings(snapshot);
                lock (_sync)
                {
                    if (generation == _generation)
                    {
                        _lastFailedSettings = null;
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
                _lastFailedSettings = _pendingSettings is null ? null : Clone(_pendingSettings);
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

    private static SonosSettings Clone(SonosSettings source)
    {
        var json = JsonSerializer.Serialize(source, CloneOptions);
        return JsonSerializer.Deserialize<SonosSettings>(json, CloneOptions)
               ?? throw new InvalidOperationException("Settings could not be prepared for saving.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        _writeLock.Dispose();
    }
}
