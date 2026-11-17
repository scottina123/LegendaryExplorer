using Color = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace LegendaryExplorer.Misc
{
    /// <summary>
    /// Single code-side source for the two dark themes' colours.
    /// <para>
    /// WPF controls read colours from the XAML token dictionaries
    /// (<c>Themes/LegendaryDark.Tokens.xaml</c> for Dark, <c>Themes/AudemusDark.Tokens.xaml</c> for Modern Dark).
    /// Renderers that cannot consume WPF resources (Piccolo graph editors, WinForms HexBox, DWM window chrome,
    /// Direct3D viewports, the script editor) read from this class instead of keeping their own copies.
    /// Keep values here in sync with the matching token where one exists.
    /// </para>
    /// </summary>
    public sealed class DarkThemePalette
    {
        /// <summary>Traditional Legendary Explorer grey palette (<c>LegendaryDark.Tokens.xaml</c>).</summary>
        public static DarkThemePalette Dark { get; } = new()
        {
            AppBackground = Color.FromArgb(0x1E, 0x1E, 0x1E),        // AppBackgroundColor
            Canvas = Color.FromArgb(0x1E, 0x1E, 0x1E),               // CanvasBackgroundColor
            Text = Color.FromArgb(0xE0, 0xE0, 0xE0),                 // TextColor
            MutedText = Color.FromArgb(0xB0, 0xB0, 0xB0),            // MutedTextColor
            Panel = Color.FromArgb(0x2D, 0x2D, 0x30),                // PanelColor
            StructuralBorder = Color.FromArgb(0x3F, 0x3F, 0x46),     // StructuralBorderColor
            Accent = Color.FromArgb(0x00, 0x7A, 0xCC),               // AccentColor
            Selection = Color.FromArgb(0x26, 0x4F, 0x78),            // SelectedBackgroundColor

            // Native caption keeps the Windows dark-mode default colours.
            CaptionColor = null,
            CaptionTextColor = null,
            WindowBorderColor = null,

            HexBackground = Color.FromArgb(0x1E, 0x1E, 0x1E),        // FieldBackgroundColor
            HexText = Color.FromArgb(0xE0, 0xE0, 0xE0),
            HexDisabledBackground = Color.FromArgb(0x2D, 0x2D, 0x30),
            HexScrollBarWidth = null,                                // system width
            ScrollTrack = Color.FromArgb(0x3E, 0x3E, 0x42),
            ScrollThumb = Color.FromArgb(0x68, 0x68, 0x6B),
            ScrollThumbHover = Color.FromArgb(0x9E, 0x9E, 0x9E),
            ScrollThumbDragging = Color.FromArgb(0x9E, 0x9E, 0x9E),
            ScrollArrow = Color.FromArgb(0x99, 0x99, 0x99),
            ScrollBorder = Color.FromArgb(0x3F, 0x3F, 0x46),

            GraphNode = Color.FromArgb(45, 45, 48),
            GraphTitleBox = Color.FromArgb(37, 37, 38),
            GraphComment = Color.FromArgb(87, 166, 74),
            GraphText = Color.FromArgb(220, 220, 220),

            // Tool-specific canvases chosen for the grey theme.
            MeshplorerBackground = Color.FromArgb(32, 32, 32),
            PathfindingBackground = Color.FromArgb(28, 27, 27),
        };

        /// <summary>Blue-slate Modern Dark palette (<c>AudemusDark.Tokens.xaml</c>).</summary>
        public static DarkThemePalette ModernDark { get; } = new()
        {
            AppBackground = Color.FromArgb(0x0A, 0x10, 0x18),        // AppBackgroundColor
            Canvas = Color.FromArgb(0x05, 0x08, 0x0D),               // CanvasBackgroundColor
            Text = Color.FromArgb(0xE8, 0xF0, 0xF5),                 // TextColor
            MutedText = Color.FromArgb(0xB0, 0xB0, 0xB0),
            Panel = Color.FromArgb(0x10, 0x1A, 0x25),                // PanelColor
            StructuralBorder = Color.FromArgb(0x2A, 0x3A, 0x49),     // StructuralBorderColor
            Accent = Color.FromArgb(0x47, 0xB4, 0xD5),               // AccentColor
            Selection = Color.FromArgb(0x26, 0x4F, 0x78),

            CaptionColor = Color.FromArgb(0x0D, 0x17, 0x21),         // ToolbarBackgroundColor
            CaptionTextColor = Color.FromArgb(0xE8, 0xF0, 0xF5),     // TextColor
            WindowBorderColor = Color.FromArgb(0x2A, 0x3A, 0x49),    // StructuralBorderColor

            HexBackground = Color.FromArgb(0x08, 0x0D, 0x13),        // RecessedBackgroundColor
            HexText = Color.FromArgb(0xE8, 0xF0, 0xF5),
            HexDisabledBackground = Color.FromArgb(0x10, 0x1A, 0x25),
            HexScrollBarWidth = 12,
            ScrollTrack = Color.FromArgb(0x08, 0x0D, 0x13),
            ScrollThumb = Color.FromArgb(0x2A, 0x3A, 0x49),
            ScrollThumbHover = Color.FromArgb(0x49, 0x64, 0x77),
            ScrollThumbDragging = Color.FromArgb(0x47, 0xB4, 0xD5),
            ScrollArrow = Color.FromArgb(0x8F, 0xA2, 0xB2),
            ScrollBorder = Color.FromArgb(0x2A, 0x3A, 0x49),

            GraphNode = Color.FromArgb(22, 36, 51),
            GraphTitleBox = Color.FromArgb(16, 26, 37),
            GraphComment = Color.FromArgb(71, 180, 213),              // AccentColor
            GraphText = Color.FromArgb(232, 240, 245),

            MeshplorerBackground = Color.FromArgb(0x05, 0x08, 0x0D),
            PathfindingBackground = Color.FromArgb(0x05, 0x08, 0x0D),
        };

        /// <summary>The palette for a dark theme. Light has no dark palette and returns <c>null</c>.</summary>
        public static DarkThemePalette For(AppTheme theme) => theme switch
        {
            AppTheme.Dark => Dark,
            AppTheme.ModernDark => ModernDark,
            _ => null
        };

        /// <summary>The palette of the active theme, or <c>null</c> in Light.</summary>
        public static DarkThemePalette Current => For(ThemeManager.CurrentTheme);

        // ---- Application surfaces ----
        public Color AppBackground { get; private init; }
        /// <summary>Default background of graph and 3D viewports.</summary>
        public Color Canvas { get; private init; }
        public Color Text { get; private init; }
        public Color MutedText { get; private init; }
        public Color Panel { get; private init; }
        public Color StructuralBorder { get; private init; }
        public Color Accent { get; private init; }
        public Color Selection { get; private init; }

        // ---- Native window chrome (null = Windows default) ----
        public Color? CaptionColor { get; private init; }
        public Color? CaptionTextColor { get; private init; }
        public Color? WindowBorderColor { get; private init; }

        // ---- WinForms HexBox ----
        public Color HexBackground { get; private init; }
        public Color HexText { get; private init; }
        public Color HexDisabledBackground { get; private init; }
        /// <summary>Custom scrollbar width, or <c>null</c> for the system width.</summary>
        public int? HexScrollBarWidth { get; private init; }
        public Color ScrollTrack { get; private init; }
        public Color ScrollThumb { get; private init; }
        public Color ScrollThumbHover { get; private init; }
        public Color ScrollThumbDragging { get; private init; }
        public Color ScrollArrow { get; private init; }
        public Color ScrollBorder { get; private init; }

        // ---- Graph editors (Sequence, Wwise, Dialogue) ----
        public Color GraphNode { get; private init; }
        public Color GraphTitleBox { get; private init; }
        public Color GraphComment { get; private init; }
        public Color GraphText { get; private init; }
        public Color GraphConnection => Color.White;

        // ---- Tool canvases ----
        public Color MeshplorerBackground { get; private init; }
        public Color PathfindingBackground { get; private init; }

        // Dialogue Editor node colours are shared by both dark themes.
        public static Color DialogueLine => Color.FromArgb(130, 180, 255);
        public static Color DialogueLinkText => Color.White;
        public static Color DialogueParaint => Color.FromArgb(100, 149, 237);
        public static Color DialogueRenint => Color.FromArgb(255, 99, 71);
        public static Color DialogueAgree => Color.FromArgb(135, 206, 250);
        public static Color DialogueDisagree => Color.FromArgb(255, 160, 122);
        public static Color DialogueFriendly => Color.FromArgb(100, 149, 237);
        public static Color DialogueHostile => Color.FromArgb(205, 92, 92);
        public static Color DialogueEntry => Color.FromArgb(75, 0, 130);
        public static Color DialogueEntryPen => Color.FromArgb(80, 80, 80);
        public static Color DialogueReply => Color.FromArgb(85, 107, 47);
        public static Color DialogueReplyPen => Color.FromArgb(80, 80, 80);

        /// <summary>Every historic default dark canvas colour, so saved defaults follow theme switches.</summary>
        public static bool IsDarkCanvasColor(MediaColor color) =>
            color == ToMedia(Dark.Canvas) || color == ToMedia(ModernDark.Canvas);

        public static MediaColor ToMedia(Color color) => MediaColor.FromArgb(color.A, color.R, color.G, color.B);

        /// <summary>COLORREF (0x00BBGGRR) for Win32/DWM calls.</summary>
        public static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

        private DarkThemePalette() { }
    }
}
