using System.Globalization;

namespace LE1GalaxyMapEditor.Services;

public static class GalaxyMapNumber
{
    public static string FormatDisplay(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Serialize(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    public static bool HasSupportedPrecision(double value)
        => double.IsFinite(value) &&
           Math.Abs(value - Math.Round(value, 2, MidpointRounding.AwayFromZero)) < 0.000000000001d;
}
