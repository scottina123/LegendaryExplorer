using System;
using System.Collections.Generic;
using System.Linq;
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
        public static Color DarkCanvasDrawingColor => (DarkThemePalette.Current ?? DarkThemePalette.Dark).Canvas;
        public static System.Windows.Media.Color DarkCanvasMediaColor => DarkThemePalette.ToMedia(DarkCanvasDrawingColor);
        public static System.Windows.Media.Color MeshplorerBackgroundMediaColor => DarkThemePalette.Current is { } palette
            ? DarkThemePalette.ToMedia(palette.MeshplorerBackground)
            : System.Windows.Media.Color.FromRgb(128, 128, 128);
        public static Color PathfindingBackgroundDrawingColor => DarkThemePalette.Current?.PathfindingBackground
            ?? Color.FromArgb(130, 130, 130);
        public static bool IsDarkCanvasColor(System.Windows.Media.Color color) => DarkThemePalette.IsDarkCanvasColor(color);

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

                DarkThemePalette palette = DarkThemePalette.For(theme);
                HexBox.SetColors(palette?.HexBackground ?? Color.White, palette?.HexText ?? Color.Black);

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
            if (DarkThemePalette.For(theme) is { } palette)
            {
                SObj.NodeBrushColor = palette.GraphNode;
                SObj.TitleBoxBrushColor = palette.GraphTitleBox;
                SObj.CommentTextColor = palette.GraphComment;
                SObj.BoxTextColor = palette.GraphText;

                WwiseHircObjNode.NodeBrushColor = palette.GraphNode;
                WwiseHircObjNode.TitleBoxBrushColor = palette.GraphTitleBox;
                WwiseHircObjNode.CommentTextColor = palette.GraphComment;
                WwiseHircObjNode.BoxTextColor = palette.GraphText;
                WwiseHircObjNode.ConnectionColor = palette.GraphConnection;

                DBox.lineColor = DarkThemePalette.DialogueLine;
                DObj.linkTextColor = DarkThemePalette.DialogueLinkText;
                DObj.paraintColor = DarkThemePalette.DialogueParaint;
                DObj.renintColor = DarkThemePalette.DialogueRenint;
                DObj.agreeColor = DarkThemePalette.DialogueAgree;
                DObj.disagreeColor = DarkThemePalette.DialogueDisagree;
                DObj.friendlyColor = DarkThemePalette.DialogueFriendly;
                DObj.hostileColor = DarkThemePalette.DialogueHostile;
                DObj.connectionColor = palette.GraphConnection;
                DObj.entryColor = DarkThemePalette.DialogueEntry;
                DObj.entryPenColor = DarkThemePalette.DialogueEntryPen;
                DObj.replyColor = DarkThemePalette.DialogueReply;
                DObj.replyPenColor = DarkThemePalette.DialogueReplyPen;
                DObj.graphBackgroundColor = palette.Canvas;
                DObj.boxColor = palette.GraphNode;
                DObj.boxTextColor = palette.GraphText;
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
            foreach (var weakRef in _registeredHexBoxes)
            {
                if (weakRef.TryGetTarget(out var existingHexBox) && existingHexBox == hexBox)
                    return;
            }

            _registeredHexBoxes.Add(new WeakReference<HexBox>(hexBox));

            ApplyHexBoxTheme(hexBox, CurrentTheme);

            // Host code sometimes assigns colours after registration or recreates the handle.
            // Re-apply on those events; ApplyHexBoxTheme only touches properties that differ,
            // so this is cheap when nothing changed. Handlers are static so they never stack.
            hexBox.HandleCreated -= OnHexBoxHandleCreated;
            hexBox.HandleCreated += OnHexBoxHandleCreated;
            hexBox.VisibleChanged -= OnHexBoxVisibleChanged;
            hexBox.VisibleChanged += OnHexBoxVisibleChanged;
            hexBox.Disposed -= OnHexBoxDisposed;
            hexBox.Disposed += OnHexBoxDisposed;

            // One deferred pass on the UI thread once the current layout settles. This replaces two
            // thread-pool continuations that mutated WinForms state off the UI thread.
            if (hexBox.IsHandleCreated)
                hexBox.BeginInvoke(() => ApplyHexBoxTheme(hexBox, CurrentTheme));
        }

        private static void OnHexBoxHandleCreated(object sender, EventArgs e)
        {
            if (sender is HexBox hexBox)
                hexBox.BeginInvoke(() => ApplyHexBoxTheme(hexBox, CurrentTheme));
        }

        private static void OnHexBoxVisibleChanged(object sender, EventArgs e)
        {
            if (sender is HexBox { Visible: true } hexBox)
                ApplyHexBoxTheme(hexBox, CurrentTheme);
        }

        private static void OnHexBoxDisposed(object sender, EventArgs e)
        {
            if (sender is not HexBox hexBox) return;
            hexBox.HandleCreated -= OnHexBoxHandleCreated;
            hexBox.VisibleChanged -= OnHexBoxVisibleChanged;
            hexBox.Disposed -= OnHexBoxDisposed;
            _registeredHexBoxes.RemoveAll(wr => !wr.TryGetTarget(out var target) || target == hexBox);
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
            if (hexBox.InvokeRequired)
            {
                try { hexBox.BeginInvoke(() => ApplyHexBoxTheme(hexBox, theme)); } catch { }
                return;
            }

            DarkThemePalette palette = DarkThemePalette.For(theme);
            bool isDarkMode = palette != null;
            bool changed = false;

            // HexBox is WinForms-hosted and cannot consume WPF font resources directly.
            // Preserve its established point-sized metrics independently of WPF typography.
            // HexBox's Font setter always re-lays out the control, so only assign real changes.
            if (!Equals(hexBox.Font, AppTypography.HexDrawingFont))
            {
                hexBox.Font = AppTypography.HexDrawingFont;
                changed = true;
            }
            if (!Equals(hexBox.BoldFont, AppTypography.HexDrawingFontBold))
            {
                hexBox.BoldFont = AppTypography.HexDrawingFontBold;
                changed = true;
            }

            if (hexBox.ScrollBarDarkMode != isDarkMode)
            {
                hexBox.ScrollBarDarkMode = isDarkMode;
                changed = true;
            }
            hexBox.ContextMenuDarkMode = isDarkMode;

            if (palette != null)
            {
                changed |= ApplyHexBoxScrollBarMetrics(hexBox, palette.HexScrollBarWidth
                    ?? System.Windows.Forms.SystemInformation.VerticalScrollBarWidth);
                var scrollBar = hexBox.VScrollBar;
                changed |= Set(scrollBar.TrackColor, palette.ScrollTrack, c => scrollBar.TrackColor = c);
                changed |= Set(scrollBar.ThumbColor, palette.ScrollThumb, c => scrollBar.ThumbColor = c);
                changed |= Set(scrollBar.ThumbHoverColor, palette.ScrollThumbHover, c => scrollBar.ThumbHoverColor = c);
                changed |= Set(scrollBar.ThumbDraggingColor, palette.ScrollThumbDragging, c => scrollBar.ThumbDraggingColor = c);
                changed |= Set(scrollBar.ArrowColor, palette.ScrollArrow, c => scrollBar.ArrowColor = c);
                changed |= Set(scrollBar.BorderColor, palette.ScrollBorder, c => scrollBar.BorderColor = c);

                changed |= Set(hexBox.BackColor, palette.HexBackground, c => hexBox.BackColor = c);
                changed |= Set(hexBox.ForeColor, palette.HexText, c => hexBox.ForeColor = c);
                changed |= Set(hexBox.InfoForeColor, palette.MutedText, c => hexBox.InfoForeColor = c);
                changed |= Set(hexBox.SelectionBackColor, Color.FromArgb(0x00, 0x7A, 0xCC), c => hexBox.SelectionBackColor = c);
                changed |= Set(hexBox.SelectionForeColor, Color.White, c => hexBox.SelectionForeColor = c);
                changed |= Set(hexBox.HighlightBackColor, palette.Selection, c => hexBox.HighlightBackColor = c);
                changed |= Set(hexBox.HighlightForeColor, Color.FromArgb(0xFF, 0xFF, 0xE0), c => hexBox.HighlightForeColor = c);
                changed |= Set(hexBox.BackColorDisabled, palette.HexDisabledBackground, c => hexBox.BackColorDisabled = c);
            }
            else
            {
                changed |= ApplyHexBoxScrollBarMetrics(hexBox, System.Windows.Forms.SystemInformation.VerticalScrollBarWidth);

                // Light theme colors (defaults)
                changed |= Set(hexBox.BackColor, Color.White, c => hexBox.BackColor = c);
                changed |= Set(hexBox.ForeColor, Color.Black, c => hexBox.ForeColor = c);
                changed |= Set(hexBox.InfoForeColor, Color.Gray, c => hexBox.InfoForeColor = c);
                changed |= Set(hexBox.SelectionBackColor, Color.Blue, c => hexBox.SelectionBackColor = c);
                changed |= Set(hexBox.SelectionForeColor, Color.White, c => hexBox.SelectionForeColor = c);
                changed |= Set(hexBox.HighlightBackColor, Color.Yellow, c => hexBox.HighlightBackColor = c);
                changed |= Set(hexBox.HighlightForeColor, Color.Black, c => hexBox.HighlightForeColor = c);
                changed |= Set(hexBox.BackColorDisabled, Color.FromName("WhiteSmoke"), c => hexBox.BackColorDisabled = c);
            }

            // Previously every call (including every visibility toggle) forced Invalidate+Update+Refresh
            // on both the HexBox and its parent: several synchronous repaints each time.
            if (changed)
                hexBox.Invalidate(true);
        }

        private static bool Set(Color current, Color target, Action<Color> assign)
        {
            if (current.ToArgb() == target.ToArgb()) return false;
            assign(target);
            return true;
        }

        private static bool ApplyHexBoxScrollBarMetrics(HexBox hexBox, int width)
        {
            if (hexBox.VScrollBar.Width == width) return false;

            bool wasVisible = hexBox.VScrollBarVisible;
            if (wasVisible) hexBox.VScrollBarVisible = false;
            hexBox.VScrollBar.Width = width;
            if (wasVisible) hexBox.VScrollBarVisible = true;
            return true;
        }

        /// <summary>
        /// Updates all registered HexBox controls with the current theme.
        /// </summary>
        private static void UpdateAllHexBoxThemes(AppTheme theme)
        {
            // Clean up dead references as we iterate
            _registeredHexBoxes.RemoveAll(wr => !wr.TryGetTarget(out _));

            foreach (var weakRef in _registeredHexBoxes.ToList())
            {
                if (weakRef.TryGetTarget(out var hexBox))
                {
                    ApplyHexBoxTheme(hexBox, theme);
                }
            }
        }
    }
}
