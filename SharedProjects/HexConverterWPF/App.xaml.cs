using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace HexConverter
{
    public partial class App : Application
    {
        private const int DwmUseImmersiveDarkModeAttribute = 20;
        private const int DwmUseImmersiveDarkModeAttributeBefore20H1 = 19;
        private static bool _isDarkTheme = true;

        protected override void OnStartup(StartupEventArgs e)
        {
            string theme = ResolveTheme(e.Args);
            _isDarkTheme = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
            Resources.MergedDictionaries[0] = new ResourceDictionary
            {
                Source = new Uri($"/HexConverter;component/Themes/HexConverter.{theme}.xaml", UriKind.Relative)
            };

            base.OnStartup(e);
        }

        private static string ResolveTheme(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--theme", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeTheme(args[i + 1]);
                }
            }

            try
            {
                string settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LegendaryExplorer", "appsettings.json");
                if (File.Exists(settingsPath))
                {
                    using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
                    if (settings.RootElement.TryGetProperty("global_theme", out JsonElement value))
                    {
                        return NormalizeTheme(value.GetString());
                    }
                }
            }
            catch
            {
                // Parent settings are optional; malformed or inaccessible JSON must not block launch.
            }

            return "Dark";
        }

        private static string NormalizeTheme(string theme) => theme?.ToLowerInvariant() switch
        {
            "light" => "Light",
            "moderndark" => "ModernDark",
            _ => "Dark"
        };

        public static void ApplyNativeTheme(Window window)
        {
            if (window == null)
            {
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int useDarkMode = _isDarkTheme ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeAttribute, ref useDarkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeAttributeBefore20H1, ref useDarkMode, sizeof(int));
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    }
}
