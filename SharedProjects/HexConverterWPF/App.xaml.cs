using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HexConverter
{
    public partial class App : Application
    {
        private const int DwmUseImmersiveDarkModeAttribute = 20;
        private const int DwmUseImmersiveDarkModeAttributeBefore20H1 = 19;

        public static void ApplyNativeDarkMode(Window window)
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

            int useDarkMode = 1;
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeAttribute, ref useDarkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeAttributeBefore20H1, ref useDarkMode, sizeof(int));
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    }
}
