using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using SonosControl.Web.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SonosControl.Web.Services;

public class ThemeService : IDisposable
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ThemeService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private ThemePreferenceMode _currentPreference = ThemePreferenceMode.System;
    private ApplicationUser? _currentUser;
    private bool _initialized;

    public ThemePreferenceMode CurrentPreference => _currentPreference;
    public string CurrentPreferenceIdentifier => _currentPreference.ToIdentifier();

    public event Action? OnPreferenceChanged;

    public ThemeService(AuthenticationStateProvider authenticationStateProvider,
                        UserManager<ApplicationUser> userManager,
                        ILogger<ThemeService> logger)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _userManager = userManager;
        _logger = logger;

        _authenticationStateProvider.AuthenticationStateChanged += HandleAuthenticationStateChanged;
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await LoadPreferenceAsync();
        _initialized = true;
    }

    public async Task SetPreferenceAsync(ThemePreferenceMode preference)
    {
        await InitializeAsync();

        if (_currentPreference == preference)
        {
            return;
        }

        _currentPreference = preference;

        if (_currentUser != null)
        {
            _currentUser.ThemePreference = preference.ToIdentifier();
            var result = await _userManager.UpdateAsync(_currentUser);

            if (!result.Succeeded)
            {
                _logger.LogWarning("Unable to persist theme preference updates for user {UserId}: {Errors}",
                    _currentUser.Id,
                    string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }

        NotifyPreferenceChanged();
    }

    private void HandleAuthenticationStateChanged(Task<AuthenticationState> authenticationStateTask)
    {
        _ = LoadPreferenceAsync();
    }

    private async Task LoadPreferenceAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            ApplicationUser? appUser = null;
            ThemePreferenceMode preference = ThemePreferenceMode.System;

            if (user.Identity?.IsAuthenticated ?? false)
            {
                appUser = await _userManager.GetUserAsync(user);
                if (appUser != null)
                {
                    preference = ThemePreferenceModeExtensions.FromIdentifier(appUser.ThemePreference);
                }
            }

            var changed = preference != _currentPreference;
            _currentUser = appUser;
            _currentPreference = preference;

            if (changed)
            {
                NotifyPreferenceChanged();
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private void NotifyPreferenceChanged()
    {
        OnPreferenceChanged?.Invoke();
    }

    public void Dispose()
    {
        _authenticationStateProvider.AuthenticationStateChanged -= HandleAuthenticationStateChanged;
        _loadLock.Dispose();
    }
}
