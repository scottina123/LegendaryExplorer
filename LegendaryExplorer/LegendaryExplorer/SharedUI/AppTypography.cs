using System;
using System.Drawing;
using System.Linq;
using System.Windows.Media;

namespace LegendaryExplorer.SharedUI;

/// <summary>
/// Cross-framework typography contract for WPF, WinForms, and GDI-rendered tool surfaces.
/// Keep these values aligned with Themes/AudemusTypography.xaml.
/// </summary>
public static class AppTypography
{
    public const string InterfaceFontFamilyName = "Segoe UI";
    public const float InterfaceFontSize = 12f;
    public const string DataFontFamilyName = "Lucida Console";
    public const float DataFontSize = 12f;
    public const string GraphFontFamilyName = "Segoe UI Variable Text";
    public const float GraphFontSize = 14f;

    public static readonly Typeface GraphTypeface = new(
        new System.Windows.Media.FontFamily($"{GraphFontFamilyName}, {InterfaceFontFamilyName}"),
        System.Windows.FontStyles.Normal,
        System.Windows.FontWeights.Normal,
        System.Windows.FontStretches.Normal);

    public static readonly Font InterfaceDrawingFont = CreateDrawingFont(
        InterfaceFontFamilyName,
        InterfaceFontFamilyName,
        InterfaceFontSize,
        System.Drawing.FontStyle.Regular,
        GraphicsUnit.Pixel);

    public static readonly Font DataDrawingFont = CreateDrawingFont(
        DataFontFamilyName,
        InterfaceFontFamilyName,
        DataFontSize,
        System.Drawing.FontStyle.Regular,
        GraphicsUnit.Pixel);

    public static readonly Font DataDrawingFontBold = CreateDrawingFont(
        DataFontFamilyName,
        InterfaceFontFamilyName,
        DataFontSize,
        System.Drawing.FontStyle.Bold,
        GraphicsUnit.Pixel);

    public static Font CreateGraphDrawingFont(float size = GraphFontSize, GraphicsUnit unit = GraphicsUnit.Pixel) =>
        CreateDrawingFont(GraphFontFamilyName, InterfaceFontFamilyName, size, System.Drawing.FontStyle.Regular, unit);

    public static Font CreateDataDrawingFont(float size = DataFontSize, GraphicsUnit unit = GraphicsUnit.Pixel) =>
        CreateDrawingFont(DataFontFamilyName, InterfaceFontFamilyName, size, System.Drawing.FontStyle.Regular, unit);

    private static Font CreateDrawingFont(string preferredFamily, string fallbackFamily, float size,
        System.Drawing.FontStyle style, GraphicsUnit unit)
    {
        string family = System.Drawing.FontFamily.Families.Any(x =>
            string.Equals(x.Name, preferredFamily, StringComparison.OrdinalIgnoreCase))
            ? preferredFamily
            : fallbackFamily;
        return new Font(family, size, style, unit);
    }
}
