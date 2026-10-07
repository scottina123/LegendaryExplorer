using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using LegendaryExplorer.Misc;
using Microsoft.AppCenter;

namespace LegendaryExplorer.Misc.AppSettings
{
    /// <summary>
    /// Class that contains static-bindable settings.
    /// </summary>
    public static partial class Settings
    {
        // This file contains the main interaction code and is NOT pregenerated

        /// <summary>
        /// Compatibility view retained for renderers and older integrations that only
        /// need to distinguish light from dark. Setting it selects traditional Dark.
        /// </summary>
        public static bool Global_DarkMode_Enabled
        {
            get => ThemeManager.IsDarkThemeName(Global_Theme);
            set => Global_Theme = value ? AppTheme.Dark.ToString() : AppTheme.Light.ToString();
        }

        private static string GetThemeSetting(Dictionary<string, object> settings)
        {
            if (settings.ContainsKey("global_theme"))
            {
                return ThemeManager.ParseThemeName(
                    TryGetSetting(settings, "global_theme", "Dark")).ToString();
            }

            // Preserve the original dark-mode preference, and default new installations to Dark.
            return TryGetSetting(settings, "global_darkmode_enabled", true) ? "Dark" : "Light";
        }

        #region Static Property Changed

        private static bool Loaded = false;
        private static bool _deferSaving;
        public static event PropertyChangedEventHandler StaticPropertyChanged;

        internal static void ApplyPresetChanges(Action apply)
        {
            bool previouslyDeferred = _deferSaving;
            _deferSaving = true;
            try
            {
                apply();
            }
            finally
            {
                _deferSaving = previouslyDeferred;
            }
            if (!previouslyDeferred) Save(throwOnError: true);
        }

        /// <summary>
        /// Sets given property and notifies listeners of its change. IGNORES setting the property to same value.
        /// Should be called in property setters.
        /// </summary>
        /// <typeparam name="T">Type of given property.</typeparam>
        /// <param name="field">Backing field to update.</param>
        /// <param name="value">New value of property.</param>
        /// <param name="propertyName">Name of property.</param>
        /// <returns>True if success, false if backing field and new value aren't compatible.</returns>
        private static bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(propertyName));
            if (Loaded)
            {
                LogSettingChanging(propertyName, value);
                Save();

                if (propertyName == nameof(Global_Analytics_Enabled))
                {
                    // Will not re-enable for this session. However, will enable on the next session
                    AppCenter.SetEnabledAsync(Global_Analytics_Enabled);
                }
                
                if (propertyName == nameof(Global_Theme))
                {
                    // Apply theme immediately when setting changes
                    ThemeManager.ApplyTheme();
                    // Persist backgrounds even when these tools are closed. Startup
                    // loading leaves saved custom colors intact because Loaded is false.
                    Meshplorer_BackgroundColor = ThemeManager.MeshplorerBackgroundMediaColor.ToString();
                    PathfindingEditor_BackgroundColor = ThemeManager.PathfindingBackgroundDrawingColor.ToArgb();
                    StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Global_DarkMode_Enabled)));
                }
            }
            return true;
        }

        private static void LogSettingChanging(string propertyName, object value)
        {
            if (Loaded)
                Debug.WriteLine($@"Setting changing: {propertyName} -> {value}");
        }

        #endregion

        // Do not add settings to this class. Add them to the SettingsBuilder.tt file
    }
}
