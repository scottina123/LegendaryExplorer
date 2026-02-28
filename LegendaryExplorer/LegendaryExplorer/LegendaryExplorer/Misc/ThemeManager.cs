using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Be.Windows.Forms;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.Tools.SequenceObjects;
using LegendaryExplorer.Tools.WwiseEditor;
using Color = System.Drawing.Color;

namespace LegendaryExplorer.Misc
{
    /// <summary>
    /// Manages application theme switching between light and dark modes.
    /// </summary>
    public static class ThemeManager
    {
        private const string DarkThemeUri = "/LegendaryExplorer;component/DarkTheme.xaml";
        private const string LightThemeUri = "/LegendaryExplorer;component/LightTheme.xaml";
        private static ResourceDictionary _darkThemeDictionary;
        private static bool _isApplyingTheme;
        
        // Track registered HexBox controls for theme updates
        private static readonly List<WeakReference<HexBox>> _registeredHexBoxes = new();

        /// <summary>
        /// Event that fires when the theme changes. Subscribe to this to update custom themed controls.
        /// </summary>
        public static event EventHandler<bool> ThemeChanged;

        /// <summary>
        /// Applies the current theme based on settings.
        /// </summary>
        public static void ApplyTheme()
        {
            ApplyTheme(Settings.Global_DarkMode_Enabled);
        }

        /// <summary>
        /// Applies the specified theme.
        /// </summary>
        /// <param name="isDarkMode">True for dark mode, false for light mode.</param>
        public static void ApplyTheme(bool isDarkMode)
        {
            if (Application.Current == null || _isApplyingTheme)
                return;

            _isApplyingTheme = true;
            try
            {
                var mergedDictionaries = Application.Current.Resources.MergedDictionaries;

                if (isDarkMode)
                {
                    // Load dark theme dictionary if not already loaded
                    if (_darkThemeDictionary == null)
                    {
                        _darkThemeDictionary = new ResourceDictionary
                        {
                            Source = new Uri(DarkThemeUri, UriKind.Relative)
                        };
                    }

                    // Check if already applied by reference or by source URI
                    bool darkThemeAlreadyApplied = mergedDictionaries.Contains(_darkThemeDictionary) ||
                        mergedDictionaries.Any(rd => rd.Source?.OriginalString?.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) == true);

                    if (!darkThemeAlreadyApplied)
                    {
                        // Add dark theme BEFORE removing light theme to avoid an intermediate
                        // "no theme" state. Removing first causes all controls to re-template
                        // to defaults, and the subsequent Add re-templates again on controls
                        // in an inconsistent state, triggering an internal WPF NullReferenceException.
                        if (!mergedDictionaries.Contains(_darkThemeDictionary))
                        {
                            mergedDictionaries.Add(_darkThemeDictionary);
                        }

                        // Now safely remove light theme — controls already have dark styles
                        var lightTheme = mergedDictionaries.FirstOrDefault(rd => 
                            rd.Source?.OriginalString?.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase) == true);
                        if (lightTheme != null)
                        {
                            mergedDictionaries.Remove(lightTheme);
                        }
                    }
                    
                    // Also set the static HexBox colors so new instances get dark colors
                    HexBox.SetColors(Color.FromArgb(0x1E, 0x1E, 0x1E), Color.FromArgb(0xE0, 0xE0, 0xE0));
                }
                else
                {
                    // Add light theme BEFORE removing dark theme to avoid an intermediate
                    // "no theme" state that triggers internal WPF NullReferenceException.
                    bool lightThemePresent = mergedDictionaries.Any(rd => 
                        rd.Source?.OriginalString?.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase) == true);
                    if (!lightThemePresent)
                    {
                        mergedDictionaries.Insert(0, new ResourceDictionary
                        {
                            Source = new Uri(LightThemeUri, UriKind.Relative)
                        });
                    }

                    // Now safely remove dark theme — controls already have light styles
                    var darkTheme = mergedDictionaries.FirstOrDefault(rd => 
                        rd.Source?.OriginalString?.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) == true);
                    if (darkTheme != null)
                    {
                        mergedDictionaries.Remove(darkTheme);
                        _darkThemeDictionary = null; // Clear cache so a fresh instance is created next time
                    }

                    // Reset static HexBox colors to light theme
                    HexBox.SetColors(Color.White, Color.Black);
                }

                // Update graph editor static colors (SObj)
                ApplyGraphEditorTheme(isDarkMode);

                // Update all registered HexBox controls
                UpdateAllHexBoxThemes(isDarkMode);
                
                // Fire the ThemeChanged event to notify subscribers
                ThemeChanged?.Invoke(null, isDarkMode);
            }
            finally
            {
                _isApplyingTheme = false;
            }
        }

        /// <summary>
        /// Applies theme colors to the static graph editor properties used by SObj and other graph objects.
        /// This ensures correct colors even when graph editors aren't open.
        /// </summary>
        /// <param name="isDarkMode">Whether dark mode is enabled.</param>
        private static void ApplyGraphEditorTheme(bool isDarkMode)
        {
            if (isDarkMode)
            {
                // Dark theme - Visual Studio dark mode inspired colors
                SObj.NodeBrushColor = Color.FromArgb(45, 45, 48);
                SObj.TitleBoxBrushColor = Color.FromArgb(37, 37, 38);
                SObj.CommentTextColor = Color.FromArgb(87, 166, 74);
                SObj.BoxTextColor = Color.FromArgb(220, 220, 220);

                // Wwise Graph Editor dark theme
                WwiseHircObjNode.NodeBrushColor = Color.FromArgb(45, 45, 48);
                WwiseHircObjNode.TitleBoxBrushColor = Color.FromArgb(37, 37, 38);
                WwiseHircObjNode.CommentTextColor = Color.FromArgb(87, 166, 74);
                WwiseHircObjNode.BoxTextColor = Color.FromArgb(220, 220, 220);
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
            ApplyHexBoxTheme(hexBox, Settings.Global_DarkMode_Enabled);
            
            // Hook into HandleCreated to reapply after control is fully initialized
            hexBox.HandleCreated += (s, e) => ApplyHexBoxTheme(hexBox, Settings.Global_DarkMode_Enabled);
            
            // Hook into VisibleChanged for when the control becomes visible
            hexBox.VisibleChanged += (s, e) => 
            { 
                if (hexBox.Visible) 
                    ApplyHexBoxTheme(hexBox, Settings.Global_DarkMode_Enabled); 
            };
            
            // Schedule another apply after delays to ensure rendering is complete
            Task.Delay(50).ContinueWith(_ =>
            {
                try
                {
                    if (!hexBox.IsDisposed)
                        ApplyHexBoxTheme(hexBox, Settings.Global_DarkMode_Enabled);
                }
                catch { }
            });
            
            Task.Delay(200).ContinueWith(_ =>
            {
                try
                {
                    if (!hexBox.IsDisposed)
                        ApplyHexBoxTheme(hexBox, Settings.Global_DarkMode_Enabled);
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
            if (hexBox == null || hexBox.IsDisposed) return;
            
            // Apply dark mode to the scrollbar
            hexBox.ScrollBarDarkMode = isDarkMode;

            // Apply dark mode to the context menu
            hexBox.ContextMenuDarkMode = isDarkMode;

            if (isDarkMode)
            {
                // Dark theme colors - set all color properties explicitly
                hexBox.BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E);           // DarkBackground #FF1E1E1E
                hexBox.ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0);           // DarkText #FFE0E0E0
                hexBox.InfoForeColor = Color.FromArgb(0xB0, 0xB0, 0xB0);       // DarkTextSecondary #FFB0B0B0
                hexBox.SelectionBackColor = Color.FromArgb(0x00, 0x7A, 0xCC); // DarkHighlight #FF007ACC
                hexBox.SelectionForeColor = Color.White;                       // DarkHighlightText
                hexBox.HighlightBackColor = Color.FromArgb(0x26, 0x4F, 0x78); // DarkSelection #FF264F78
                hexBox.HighlightForeColor = Color.FromArgb(0xFF, 0xFF, 0xE0); // Light yellow for visibility
                hexBox.BackColorDisabled = Color.FromArgb(0x2D, 0x2D, 0x30);  // DarkControl #FF2D2D30
            }
            else
            {
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
        private static void UpdateAllHexBoxThemes(bool isDarkMode)
        {
            // Clean up dead references as we iterate
            _registeredHexBoxes.RemoveAll(wr => !wr.TryGetTarget(out _));
            
            foreach (var weakRef in _registeredHexBoxes)
            {
                if (weakRef.TryGetTarget(out var hexBox))
                {
                    ApplyHexBoxTheme(hexBox, isDarkMode);
                }
            }
        }
    }
}
