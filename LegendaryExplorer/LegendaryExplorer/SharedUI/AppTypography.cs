using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LegendaryExplorer.SharedUI;

/// <summary>
/// Cross-framework typography contract for WPF, WinForms, and GDI-rendered tool surfaces.
/// Keep these values aligned with Themes/AudemusTypography.xaml.
/// </summary>
public static class AppTypography
{
    public const string InterfaceFontFamilyName = "Lexend Deca";
    public const float InterfaceFontSize = 12f;
    public const string DataFontFamilyName = InterfaceFontFamilyName;
    public const float DataFontSize = 12f;
    public const string HexFontFamilyName = InterfaceFontFamilyName;
    public const string HexBoldFontFamilyName = InterfaceFontFamilyName;
    public const float HexFontSize = 10f;
    public const string GraphFontFamilyName = InterfaceFontFamilyName;
    public const float GraphFontSize = 14f;

    public static readonly System.Windows.Media.FontFamily InterfaceWpfFont = new(
        new Uri("pack://application:,,,/LegendaryExplorer;component/"),
        "./Resources/Fonts/LexendDeca-Regular.ttf#Lexend Deca");

    // Retain the collection, buffer and GDI registration for the process lifetime.
    // GDI+ graph drawing and GDI TextRenderer both need the bundled face registered.
    private static readonly PrivateFontCollection DrawingFonts = LoadDrawingFonts();
    private static IntPtr drawingFontMemory;
    private static IntPtr drawingFontRegistration;

    public static readonly Typeface GraphTypeface = new(
        InterfaceWpfFont,
        System.Windows.FontStyles.Normal,
        System.Windows.FontWeights.Normal,
        System.Windows.FontStretches.Normal);

    public static readonly Font InterfaceDrawingFont = CreateDrawingFont(InterfaceFontSize, GraphicsUnit.Pixel);

    public static readonly Font DataDrawingFont = CreateDrawingFont(DataFontSize, GraphicsUnit.Pixel);

    public static readonly Font DataDrawingFontBold = CreateDrawingFont(DataFontSize, GraphicsUnit.Pixel);

    // HexBox historically uses point-sized WinForms fonts. Keeping these metrics
    // separate avoids treating WPF device-independent pixels as GDI points.
    public static readonly Font HexDrawingFont = CreateDrawingFont(HexFontSize, GraphicsUnit.Point);

    public static readonly Font HexDrawingFontBold = CreateDrawingFont(HexFontSize, GraphicsUnit.Point);

    public static Font CreateGraphDrawingFont(float size = GraphFontSize, GraphicsUnit unit = GraphicsUnit.Pixel) =>
        CreateDrawingFont(size, unit);

    public static Font CreateDataDrawingFont(float size = DataFontSize, GraphicsUnit unit = GraphicsUnit.Pixel) =>
        CreateDrawingFont(size, unit);

    private static Font CreateDrawingFont(float size, GraphicsUnit unit)
    {
        return new Font(DrawingFonts.Families[0], size, System.Drawing.FontStyle.Regular, unit);
    }

    private static unsafe PrivateFontCollection LoadDrawingFonts()
    {
        using var stream = typeof(AppTypography).Assembly.GetManifestResourceStream("LegendaryExplorer.LexendDeca-Regular.ttf")
            ?? throw new InvalidOperationException("The bundled Lexend Deca font is missing.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        drawingFontMemory = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, drawingFontMemory, bytes.Length);
        uint count = 0;
        drawingFontRegistration = AddFontMemResourceEx(drawingFontMemory, (uint)bytes.Length, IntPtr.Zero, &count);
        if (drawingFontRegistration == IntPtr.Zero || count == 0)
            throw new InvalidOperationException("Could not register the bundled Lexend Deca font.");
        var fonts = new PrivateFontCollection();
        fonts.AddMemoryFont(drawingFontMemory, bytes.Length);
        return fonts;
    }

    [DllImport("gdi32.dll")]
    private static extern unsafe IntPtr AddFontMemResourceEx(IntPtr font, uint size, IntPtr reserved, uint* count);

    public static void Initialize()
    {
        // Cover ordinary WPF windows and documents as well as themed tool windows.
        Control.FontFamilyProperty.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(InterfaceWpfFont));
        Control.FontFamilyProperty.OverrideMetadata(typeof(ContextMenu), new FrameworkPropertyMetadata(InterfaceWpfFont));
        Control.FontFamilyProperty.OverrideMetadata(typeof(ToolTip), new FrameworkPropertyMetadata(InterfaceWpfFont));
        System.Windows.Forms.Application.SetDefaultFont(InterfaceDrawingFont);
    }
}
