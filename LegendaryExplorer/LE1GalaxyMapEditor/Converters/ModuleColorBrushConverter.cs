using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Converters;

public static class ModuleColorPalette
{
    public static Color SelectionOrange { get; } = Color.FromRgb(0xF2, 0xA3, 0x3A);

    private static readonly IReadOnlyDictionary<ModuleColor, Color> Colors =
        new Dictionary<ModuleColor, Color>
        {
            [ModuleColor.BaseGameBlue] = Color.FromRgb(0x59, 0xC8, 0xF0),
            [ModuleColor.Red] = Color.FromRgb(0xFF, 0x5F, 0x6D),
            [ModuleColor.Pink] = Color.FromRgb(0xFF, 0x93, 0xC8),
            [ModuleColor.Purple] = Color.FromRgb(0xA8, 0x84, 0xF3),
            [ModuleColor.Cyan] = Color.FromRgb(0x43, 0xDD, 0xE5),
            [ModuleColor.Yellow] = Color.FromRgb(0xF4, 0xD3, 0x5E),
            [ModuleColor.Green] = Color.FromRgb(0x65, 0xD4, 0x87),
            [ModuleColor.White] = Color.FromRgb(0xF2, 0xF6, 0xFA),
            [ModuleColor.Magenta] = Color.FromRgb(0xEE, 0x4D, 0xDB)
        };

    private static readonly IReadOnlyDictionary<ModuleColor, SolidColorBrush> Brushes =
        Colors.ToDictionary(pair => pair.Key, pair => Freeze(new SolidColorBrush(pair.Value)));

    public static Color GetColor(ModuleColor color)
        => Colors.GetValueOrDefault(color, Colors[ModuleColor.BaseGameBlue]);

    public static SolidColorBrush GetBrush(ModuleColor color)
        => Brushes.GetValueOrDefault(color, Brushes[ModuleColor.BaseGameBlue]);

    public static SolidColorBrush CreateBrush(ModuleColor color, byte alpha)
    {
        var source = GetColor(color);
        return Freeze(new SolidColorBrush(Color.FromArgb(alpha, source.R, source.G, source.B)));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public sealed class ModuleColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value switch
        {
            ModuleColor moduleColor => moduleColor,
            GalaxyMapRowOrigin origin => origin.Color,
            GalaxyMapModule module => module.Color,
            _ => ModuleColor.BaseGameBlue
        };

        return ModuleColorPalette.GetBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
