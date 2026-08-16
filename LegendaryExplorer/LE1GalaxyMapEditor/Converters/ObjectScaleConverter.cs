using System.Globalization;
using System.Windows.Data;

namespace LE1GalaxyMapEditor.Converters;

public sealed class ObjectScaleConverter : IValueConverter, IMultiValueConverter
{
    public const double MinimumSourceScale = 0.5d;
    public const double MaximumSourceScale = 8d;
    public const double ReferenceViewportExtent = 760d;
    public const double MaximumDisplayScale = 3.6d;

    public static double Calculate(double sourceScale)
    {
        var clamped = Math.Clamp(
            double.IsFinite(sourceScale) ? sourceScale : 1d,
            MinimumSourceScale,
            MaximumSourceScale);
        var logarithmicScale = Math.Log2(clamped);
        var baseline = 1d + (0.3541666666666667d * logarithmicScale) +
                       (0.10416666666666667d * logarithmicScale * logarithmicScale);

        // Preserve the complete lower half of the curve, then progressively
        // widen the contrast near the upper boundary. The cubic ramp keeps
        // scale 2 almost unchanged while taking scale 8 from 3.0 to 3.6.
        var upperProgress = Math.Clamp(
            logarithmicScale / Math.Log2(MaximumSourceScale),
            0d,
            1d);
        return baseline + ((MaximumDisplayScale - 3d) * upperProgress * upperProgress * upperProgress);
    }

    public static double Calculate(double sourceScale, double viewportExtent)
    {
        var viewportFactor = double.IsFinite(viewportExtent) && viewportExtent > 0
            ? Math.Clamp(viewportExtent / ReferenceViewportExtent, 0.5d, 1d)
            : 1d;
        return Calculate(sourceScale) * viewportFactor;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double scale ? Calculate(scale) : 1d;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values is [double scale, double viewportExtent, ..]
            ? Calculate(scale, viewportExtent)
            : 1d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
