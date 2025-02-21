using System;

namespace SonosControl.Web.Services
{
    public class ThemeService
    {
        public string CurrentTheme { get; private set; } = "standard-base"; // Default theme

        public event Action OnChange;

        public void ToggleTheme()
        {
            CurrentTheme = CurrentTheme == "standard-base" ? "dark" : "standard-base";
            NotifyStateChanged();
        }

        private void NotifyStateChanged() => OnChange?.Invoke();
    }
}