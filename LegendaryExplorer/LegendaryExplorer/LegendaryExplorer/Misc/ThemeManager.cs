using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Be.Windows.Forms;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.Tools.SequenceObjects;
using LegendaryExplorer.Tools.WwiseEditor;
using Color = System.Drawing.Color;

namespace LegendaryExplorer.Misc
{
    public enum AppTheme
    {
        Light,
        Dark,
        ModernDark
    }

    /// <summary>
    /// Manages application theme switching without coupling custom renderers to WPF resources.
    /// </summary>
    public static class ThemeManager
    {
        private const string DarkThemeUri = "/LegendaryExplorer;component/DarkTheme.xaml";
        private const string ModernDarkThemeUri = "/LegendaryExplorer;component/ModernDarkTheme.xaml";
        private const string LightThemeUri = "/LegendaryExplorer;component/LightTheme.xaml";
        private static readonly Dictionary<AppTheme, ResourceDictionary> ThemeDictionaries = new();
        private static bool _isApplyingTheme;
        
        // Track registered HexBox controls for theme updates
        private static readonly List<WeakReference<HexBox>> _registeredHexBoxes = new();

        /// <summary>
        /// Event that fires when the theme changes. Subscribe to this to update custom themed controls.
        /// </summary>
        public static event EventHandler<bool> ThemeChanged;

        public static AppTheme CurrentTheme => ParseThemeName(Settings.Global_Theme);
        public static bool IsDarkTheme => CurrentTheme != AppTheme.Light;
        public static bool IsModernDark => CurrentTheme == AppTheme.ModernDark;
        public static Color DarkCanvasDrawingColor => IsModernDark
            ? Color.FromArgb(5, 8, 13)
            : Color.FromArgb(30, 30, 30);
        public static System.Windows.Media.Color DarkCanvasMediaColor => IsModernDark
            ? System.Windows.Media.Color.FromRgb(5, 8, 13)
            : System.Windows.Media.Color.FromRgb(30, 30, 30);
        public static bool IsDarkCanvasColor(System.Windows.Media.Color color) =>
            color == System.Windows.Media.Color.FromRgb(5, 8, 13)
            || color == System.Windows.Media.Color.FromRgb(30, 30, 30);

        public static bool IsDarkThemeName(string themeName) => ParseThemeName(themeName) != AppTheme.Light;

        public static AppTheme ParseThemeName(string themeName) =>
            Enum.TryParse(themeName, true, out AppTheme theme) && Enum.IsDefined(theme)
                ? theme
                : AppTheme.Light;

        /// <summary>
        /// Applies the current theme based on settings.
        /// </summary>
        public static void ApplyTheme()
        {
            ApplyTheme(CurrentTheme);
        }

        /// <summary>
        /// Applies the specified theme.
        /// </summary>
        /// <param name="isDarkMode">True for dark mode, false for light mode.</param>
        public static void ApplyTheme(bool isDarkMode)
        {
            ApplyTheme(isDarkMode ? AppTheme.Dark : AppTheme.Light);
        }

        /// <summary>
        /// Applies the specified application theme.
        /// </summary>
        public static void ApplyTheme(AppTheme theme)
        {
            if (Application.Current == null || _isApplyingTheme)
                return;

            _isApplyingTheme = true;
            try
            {
                var mergedDictionaries = Application.Current.Resources.MergedDictionaries;
                string targetThemeUri = GetThemeUri(theme);
                string targetThemeFile = GetThemeFileName(targetThemeUri);
                ResourceDictionary targetDictionary = mergedDictionaries.FirstOrDefault(rd =>
                    IsThemeDictionary(rd, targetThemeFile));

                if (targetDictionary == null)
                {
                    if (!ThemeDictionaries.TryGetValue(theme, out targetDictionary))
                    {
                        targetDictionary = new ResourceDictionary
                        {
                            Source = new Uri(targetThemeUri, UriKind.Relative)
                        };
                        ThemeDictionaries[theme] = targetDictionary;
                    }

                    // Add before removing the previous theme. An intermediate resource gap
                    // can make live WPF controls re-template against incomplete resources.
                    mergedDictionaries.Add(targetDictionary);
                }

                foreach (ResourceDictionary oldTheme in mergedDictionaries
                             .Where(rd => rd != targetDictionary && IsThemeDictionary(rd))
                             .ToList())
                {
                    mergedDictionaries.Remove(oldTheme);
                }

                Color hexBackground = theme switch
                {
                    AppTheme.ModernDark => Color.FromArgb(0x08, 0x0D, 0x13),
                    AppTheme.Dark => Color.FromArgb(0x1E, 0x1E, 0x1E),
                    _ => Color.White
                };
                Color hexForeground = theme switch
                {
                    AppTheme.ModernDark => Color.FromArgb(0xE8, 0xF0, 0xF5),
                    AppTheme.Dark => Color.FromArgb(0xE0, 0xE0, 0xE0),
                    _ => Color.Black
                };
                HexBox.SetColors(hexBackground, hexForeground);

                ApplyGraphEditorTheme(theme);
                UpdateAllHexBoxThemes(theme);
                ThemeChanged?.Invoke(null, theme != AppTheme.Light);
            }
            finally
            {
                _isApplyingTheme = false;
            }
        }

        private static string GetThemeUri(AppTheme theme) => theme switch
        {
            AppTheme.Dark => DarkThemeUri,
            AppTheme.ModernDark => ModernDarkThemeUri,
            _ => LightThemeUri
        };

        private static string GetThemeFileName(string uri) => uri[(uri.LastIndexOf('/') + 1)..];

        private static bool IsThemeDictionary(ResourceDictionary dictionary, string fileName = null)
        {
            string source = dictionary.Source?.OriginalString;
            if (string.IsNullOrEmpty(source)) return false;

            string sourceFile = GetThemeFileName(source);
            return fileName != null
                ? string.Equals(sourceFile, fileName, StringComparison.OrdinalIgnoreCase)
                : string.Equals(sourceFile, "LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(sourceFile, "DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(sourceFile, "ModernDarkTheme.xaml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies theme colors to the static graph editor properties used by SObj and other graph objects.
        /// This ensures correct colors even when graph editors aren't open.
        /// </summary>
        /// <param name="theme">The active application theme.</param>
        private static void ApplyGraphEditorTheme(AppTheme theme)
        {
            if (theme == AppTheme.ModernDark)
            {
                // Dark theme - Visual Studio dark mode inspired colors
                SObj.NodeBrushColor = Color.FromArgb(22, 36, 51);
                SObj.TitleBoxBrushColor = Color.FromArgb(16, 26, 37);
                SObj.CommentTextColor = Color.FromArgb(71, 180, 213);
                SObj.BoxTextColor = Color.FromArgb(232, 240, 245);

                // Wwise Graph Editor dark theme
                WwiseHircObjNode.NodeBrushColor = Color.FromArgb(22, 36, 51);
                WwiseHircObjNode.TitleBoxBrushColor = Color.FromArgb(16, 26, 37);
                WwiseHircObjNode.CommentTextColor = Color.FromArgb(71, 180, 213);
                WwiseHircObjNode.BoxTextColor = Color.FromArgb(232, 240, 245);
                WwiseHircObjNode.ConnectionColor = Color.White;

                // Dialogue Editor dark theme
                DBox.lineColor = Color.FromArgb(130, 180, 255);
                DObj.linkTextColor = Color.White;
                DObj.paraintColor = Color.FromArgb(100, 149, 237);
                DObj.renintColor = Color.FromArgb(255, 99, 71);
                DObj.agreeColor = Color.FromArgb(135, 206, 250);
                DObj.disagreeColor = Color.FromArgb(255, 160, 122);
                DObj.friendlyColor = Color.FromArgb(100, 149, 237);
                DObj.hostileColor = Color.FromArgb(205, 92, 92);
                DObj.connectionColor = Color.White;
                DObj.entryColor = Color.FromArgb(218, 165, 32);
                DObj.entryPenColor = Color.FromArgb(220, 220, 220);
                DObj.replyColor = Color.FromArgb(95, 158, 160);
                DObj.replyPenColor = Color.FromArgb(220, 220, 220);
                DObj.graphBackgroundColor = Color.FromArgb(5, 8, 13);
                DObj.boxColor = Color.FromArgb(22, 36, 51);
                DObj.boxTextColor = Color.FromArgb(232, 240, 245);
            }
            else if (theme == AppTheme.Dark)
            {
                // Traditional Legendary Explorer dark palette.
                SObj.NodeBrushColor = Color.FromArgb(45, 45, 48);
                SObj.TitleBoxBrushColor = Color.FromArgb(37, 37, 38);
                SObj.CommentTextColor = Color.FromArgb(87, 166, 74);
                SObj.BoxTextColor = Color.FromArgb(220, 220, 220);

                WwiseHircObjNode.NodeBrushColor = Color.FromArgb(45, 45, 48);
                WwiseHircObjNode.TitleBoxBrushColor = Color.FromArgb(37, 37, 38);
                WwiseHircObjNode.CommentTextColor = Color.FromArgb(87, 166, 74);
                WwiseHircObjNode.BoxTextColor = Color.FromArgb(220, 220, 220);
                WwiseHircObjNode.ConnectionColor = Color.White;

                DBox.lineColor = Color.FromArgb(130, 180, 255);
                DObj.linkTextColor = Color.White;
                DObj.paraintColor = Color.FromArgb(100, 149, 237);
                DObj.renintColor = Color.FromArgb(255, 99, 71);
                DObj.agreeColor = Color.FromArgb(135, 206, 250);
                DObj.disagreeColor = Color.FromArgb(255, 160, 122);
                DObj.friendlyColor = Color.FromArgb(100, 149, 237);
                DObj.hostileColor = Color.FromArgb(205, 92, 92);
                DObj.connectionColor = Color.White;
                DObj.entryColor = Color.FromArgb(218, 165, 32);
                DObj.entryPenColor = Color.FromArgb(220, 220, 220);
                DObj.replyColor = Color.FromArgb(95, 158, 160);
                DObj.replyPenColor = Color.FromArgb(220, 220, 220);
                DObj.graphBackgroundColor = Color.FromArgb(30, 30, 30);
                DObj.boxColor = Color.FromArgb(45, 45, 48);
                DObj.boxTextColor = Color.FromArgb(220, 220, 220);
            }
            else
            {
                // Light theme defaults
                SObj.NodeBrushColor = Color.FromArgb(140, 140, 140);
                SObj.TitleBoxBrushColor = Color.FromArgb(112, 112, 112);
                SObj.CommentTextColor = Color.FromArgb(25, 25, 112);
                SObj.BoxTextColor = Color.FromArgb(255, 255, 255);

                // Wwise Graph Editor light theme (original colors)
                WwiseHircObjNode.NodeBrushColor = Color.FromArgb(140, 140, 140);
                WwiseHircObjNode.TitleBoxBrushColor = Color.FromArgb(112, 112, 112);
                WwiseHircObjNode.CommentTextColor = Color.FromArgb(74, 63, 190);
                WwiseHircObjNode.BoxTextColor = Color.FromArgb(255, 255, 128);
                WwiseHircObjNode.ConnectionColor = Color.Black;

                // Dialogue Editor light theme
                DBox.lineColor = Color.White;
                DObj.linkTextColor = Color.Black;
                DObj.paraintColor = Color.Blue;
                DObj.renintColor = Color.Red;
                DObj.agreeColor = Color.DodgerBlue;
                DObj.disagreeColor = Color.Tomato;
                DObj.friendlyColor = Color.FromArgb(3, 3, 116);
                DObj.hostileColor = Color.FromArgb(116, 3, 3);
                DObj.connectionColor = Color.Black;
                DObj.entryColor = Color.FromArgb(218, 165, 32);
                DObj.entryPenColor = Color.Black;
                DObj.replyColor = Color.FromArgb(64, 224, 208);
                DObj.replyPenColor = Color.Black;
                DObj.graphBackgroundColor = Color.FromArgb(115, 115, 115);
                DObj.boxColor = Color.FromArgb(80, 80, 80);
                DObj.boxTextColor = Color.White;
            }
        }

        /// <summary>
        /// Registers a HexBox control for theme management and applies current theme.
        /// Call this when a HexBox is loaded.
        /// </summary>
        /// <param name="hexBox">The HexBox control to register.</param>
        public static void RegisterHexBox(HexBox hexBox)
        {
            if (hexBox == null) return;
            
            // Clean up dead references and check if already registered
            _registeredHexBoxes.RemoveAll(wr => !wr.TryGetTarget(out _));
            
            // Check if already registered
            foreach (var weakRef in _registeredHexBoxes)
            {
                if (weakRef.TryGetTarget(out var existingHexBox) && existingHexBox == hexBox)
                    return;
            }
            
            _registeredHexBoxes.Add(new WeakReference<HexBox>(hexBox));
            
            // Apply theme immediately
            ApplyHexBoxTheme(hexBox, CurrentTheme);
            
            // Hook into HandleCreated to reapply after control is fully initialized
            hexBox.HandleCreated += (s, e) => ApplyHexBoxTheme(hexBox, CurrentTheme);
            
            // Hook into VisibleChanged for when the control becomes visible
            hexBox.VisibleChanged += (s, e) => 
            { 
                if (hexBox.Visible) 
                    ApplyHexBoxTheme(hexBox, CurrentTheme);
            };
            
            // Schedule another apply after delays to ensure rendering is complete
            Task.Delay(50).ContinueWith(_ =>
            {
                try
                {
                    if (!hexBox.IsDisposed)
                        ApplyHexBoxTheme(hexBox, CurrentTheme);
                }
                catch { }
            });
            
            Task.Delay(200).ContinueWith(_ =>
            {
                try
                {
                    if (!hexBox.IsDisposed)
                        ApplyHexBoxTheme(hexBox, CurrentTheme);
                }
                catch { }
            });
        }
        
        /// <summary>
        /// Applies the current theme colors to a HexBox control.
        /// </summary>
        /// <param name="hexBox">The HexBox control to theme.</param>
        /// <param name="isDarkMode">Whether dark mode is enabled.</param>
        public static void ApplyHexBoxTheme(HexBox hexBox, bool isDarkMode)
        {
            ApplyHexBoxTheme(hexBox, isDarkMode ? AppTheme.Dark : AppTheme.Light);
        }

        public static void ApplyHexBoxTheme(HexBox hexBox, AppTheme theme)
        {
            if (hexBox == null || hexBox.IsDisposed) return;

            bool isDarkMode = theme != AppTheme.Light;

            // HexBox is WinForms-hosted and cannot consume WPF font resources directly.
            // Apply the shared cross-framework typography contract here instead.
            hexBox.Font = AppTypography.DataDrawingFont;
            hexBox.BoldFont = AppTypography.DataDrawingFontBold;
            
            // Apply dark mode to the scrollbar
            hexBox.ScrollBarDarkMode = isDarkMode;

            // Apply dark mode to the context menu
            hexBox.ContextMenuDarkMode = isDarkMode;

            if (theme == AppTheme.ModernDark)
            {
                ApplyHexBoxScrollBarMetrics(hexBox, 12);
                hexBox.VScrollBar.TrackColor = Color.FromArgb(0x08, 0x0D, 0x13);
                hexBox.VScrollBar.ThumbColor = Color.FromArgb(0x2A, 0x3A, 0x49);
                hexBox.VScrollBar.ThumbHoverColor = Color.FromArgb(0x49, 0x64, 0x77);
                hexBox.VScrollBar.ThumbDraggingColor = Color.FromArgb(0x47, 0xB4, 0xD5);
                hexBox.VScrollBar.ArrowColor = Color.FromArgb(0x8F, 0xA2, 0xB2);
                hexBox.VScrollBar.BorderColor = Color.FromArgb(0x2A, 0x3A, 0x49);

                // Dark theme colors - set all color properties explicitly
                hexBox.BackColor = Color.FromArgb(0x08, 0x0D, 0x13);           // RecessedBackground #080D13
                hexBox.ForeColor = Color.FromArgb(0xE8, 0xF0, 0xF5);           // Text #E8F0F5
                hexBox.InfoForeColor = Color.FromArgb(0xB0, 0xB0, 0xB0);       // DarkTextSecondary #FFB0B0B0
                hexBox.SelectionBackColor = Color.FromArgb(0x00, 0x7A, 0xCC); // DarkHighlight #FF007ACC
                hexBox.SelectionForeColor = Color.White;                       // DarkHighlightText
                hexBox.HighlightBackColor = Color.FromArgb(0x26, 0x4F, 0x78); // DarkSelection #FF264F78
                hexBox.HighlightForeColor = Color.FromArgb(0xFF, 0xFF, 0xE0); // Light yellow for visibility
                hexBox.BackColorDisabled = Color.FromArgb(0x10, 0x1A, 0x25);  // Panel #101A25
            }
            else if (theme == AppTheme.Dark)
            {
                ApplyHexBoxScrollBarMetrics(hexBox, System.Windows.Forms.SystemInformation.VerticalScrollBarWidth);
                hexBox.VScrollBar.TrackColor = Color.FromArgb(0x3E, 0x3E, 0x42);
                hexBox.VScrollBar.ThumbColor = Color.FromArgb(0x68, 0x68, 0x6B);
                hexBox.VScrollBar.ThumbHoverColor = Color.FromArgb(0x9E, 0x9E, 0x9E);
                hexBox.VScrollBar.ThumbDraggingColor = Color.FromArgb(0x9E, 0x9E, 0x9E);
                hexBox.VScrollBar.ArrowColor = Color.FromArgb(0x99, 0x99, 0x99);
                hexBox.VScrollBar.BorderColor = Color.FromArgb(0x3F, 0x3F, 0x46);

                hexBox.BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E);
                hexBox.ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0);
                hexBox.InfoForeColor = Color.FromArgb(0xB0, 0xB0, 0xB0);
                hexBox.SelectionBackColor = Color.FromArgb(0x00, 0x7A, 0xCC);
                hexBox.SelectionForeColor = Color.White;
                hexBox.HighlightBackColor = Color.FromArgb(0x26, 0x4F, 0x78);
                hexBox.HighlightForeColor = Color.FromArgb(0xFF, 0xFF, 0xE0);
                hexBox.BackColorDisabled = Color.FromArgb(0x2D, 0x2D, 0x30);
            }
            else
            {
                ApplyHexBoxScrollBarMetrics(hexBox, System.Windows.Forms.SystemInformation.VerticalScrollBarWidth);

                // Light theme colors (defaults)
                hexBox.BackColor = Color.White;
                hexBox.ForeColor = Color.Black;
                hexBox.InfoForeColor = Color.Gray;
                hexBox.SelectionBackColor = Color.Blue;
                hexBox.SelectionForeColor = Color.White;
                hexBox.HighlightBackColor = Color.Yellow;
                hexBox.HighlightForeColor = Color.Black;
                hexBox.BackColorDisabled = Color.FromName("WhiteSmoke");
            }
            
            // Force immediate synchronous refresh for WinForms control hosted in WPF
            try
            {
                if (hexBox.InvokeRequired)
                {
                    hexBox.Invoke(() => PerformHexBoxRefresh(hexBox));
                }
                else
                {
                    PerformHexBoxRefresh(hexBox);
                }
            }
            catch
            {
                // Control might be disposed
            }
        }

        private static void ApplyHexBoxScrollBarMetrics(HexBox hexBox, int width)
        {
            if (hexBox.VScrollBar.Width == width) return;

            bool wasVisible = hexBox.VScrollBarVisible;
            if (wasVisible) hexBox.VScrollBarVisible = false;
            hexBox.VScrollBar.Width = width;
            if (wasVisible) hexBox.VScrollBarVisible = true;
        }
        
        /// <summary>
        /// Performs the actual refresh operations on the HexBox control
        /// </summary>
        private static void PerformHexBoxRefresh(HexBox hexBox)
        {
            try
            {
                hexBox.Invalidate(true);
                hexBox.Update();
                hexBox.Refresh();
                
                // Also refresh parent
                if (hexBox.Parent != null)
                {
                    hexBox.Parent.Invalidate(true);
                    hexBox.Parent.Update();
                    try { hexBox.Parent.Refresh(); } catch { }
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Updates all registered HexBox controls with the current theme.
        /// </summary>
        private static void UpdateAllHexBoxThemes(AppTheme theme)
        {
            // Clean up dead references as we iterate
            _registeredHexBoxes.RemoveAll(wr => !wr.TryGetTarget(out _));
            
            foreach (var weakRef in _registeredHexBoxes)
            {
                if (weakRef.TryGetTarget(out var hexBox))
                {
                    ApplyHexBoxTheme(hexBox, theme);
                }
            }
        }
    }
}
