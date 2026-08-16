using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Converters;

public sealed class PlanetGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PlanetVisualKind kind
            ? kind switch
            {
                PlanetVisualKind.Planet => "●",
                PlanetVisualKind.AsteroidBelt => "▲",
                PlanetVisualKind.Anomaly => "◆",
                PlanetVisualKind.RingedPlanet => "◉",
                PlanetVisualKind.Relay => "⇄",
                PlanetVisualKind.FuelDepot => "▣",
                PlanetVisualKind.Sun => "☀",
                _ => "◇"
            }
            : "●";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PlanetBrushConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<PlanetVisualKind, SolidColorBrush> BrushesByKind =
        new Dictionary<PlanetVisualKind, SolidColorBrush>
        {
            [PlanetVisualKind.Planet] = CreateBrush(103, 196, 225),
            [PlanetVisualKind.AsteroidBelt] = CreateBrush(204, 183, 145),
            [PlanetVisualKind.Anomaly] = CreateBrush(230, 172, 83),
            [PlanetVisualKind.RingedPlanet] = CreateBrush(177, 151, 231),
            [PlanetVisualKind.Relay] = CreateBrush(238, 70, 83),
            [PlanetVisualKind.FuelDepot] = CreateBrush(104, 210, 151),
            [PlanetVisualKind.Sun] = CreateBrush(255, 215, 91),
            [PlanetVisualKind.Object] = CreateBrush(180, 190, 201)
        };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PlanetVisualKind kind
            ? BrushesByKind.GetValueOrDefault(kind, BrushesByKind[PlanetVisualKind.Object])
            : Brushes.LightBlue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
