using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LegendaryExplorer.MainWindow;

/// <summary>Appearance used only by the launcher tiles; original icon resources stay intact.</summary>
internal static class ToolTileAppearance
{
    public static Brush CreateAccent(ImageSource icon)
    {
        if (icon is not BitmapSource source)
            return Brushes.SlateGray;

        // The original icons share a clear background gradient in their lower-right
        // corner, below the lettering. Sample that artwork rather than assigning a
        // new category palette that could drift from the existing tool colours.
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        Color Sample(double y, double brightness)
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth * 0.88),
                (int)(bitmap.PixelHeight * y), 1, 1), pixel, 4, 0);
            // A few developer tools have transparent artwork instead of a colour
            // gradient. Give those a visible neutral accent.
            if (pixel[3] < 32 || (pixel[0] == 0 && pixel[1] == 0 && pixel[2] == 0))
                return Color.FromRgb((byte)brightness, (byte)brightness, (byte)brightness);
            // Keep the artwork's hue while lifting its dark background colours
            // enough to read as a narrow accent on the dark tile.
            double scale = brightness / System.Math.Max(1, (int)System.Math.Max(pixel[2], System.Math.Max(pixel[1], pixel[0])));
            return Color.FromRgb((byte)(pixel[2] * scale), (byte)(pixel[1] * scale), (byte)(pixel[0] * scale));
        }

        var brush = new LinearGradientBrush(Sample(0.86, 180), Sample(0.96, 245), new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}
