using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using LE1GalaxyMapEditor.Infrastructure;

namespace LE1GalaxyMapEditor.Theming;

/// <summary>The LEX themes understood by the LE1 Galaxy Map Editor.</summary>
public enum EditorTheme
{
    Light,
    Dark,
    ModernDark
}

/// <summary>
/// Applies the editor's existing visual resources to its own windows without
/// leaking implicit styles into the rest of the host application.
/// </summary>
public static class EditorThemeManager
{
    private static readonly Uri StylesUri = new(
        "/LE1GalaxyMapEditor;component/Themes/EditorStyles.xaml",
        UriKind.Relative);

    private static readonly List<WeakReference<Window>> Windows = [];

    public static event EventHandler? WindowCloseCancelled;

    public static EditorTheme CurrentTheme { get; private set; } = EditorTheme.ModernDark;

    /// <summary>Prepares a window before its generated XAML is loaded.</summary>
    public static void Initialize(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.Resources.MergedDictionaries.Add(CreateThemeResources(CurrentTheme));
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = StylesUri });
        Windows.Add(new WeakReference<Window>(window));
        window.Loaded += Window_OnLoaded;
        window.Closed += Window_OnClosed;
        DarkTitleBar.Apply(window, CurrentTheme);
    }

    /// <summary>Updates every open editor window to the selected LEX theme.</summary>
    public static void ApplyTheme(EditorTheme theme)
    {
        CurrentTheme = theme;
        Windows.RemoveAll(reference => !reference.TryGetTarget(out _));

        foreach (var reference in Windows)
        {
            if (!reference.TryGetTarget(out var window))
            {
                continue;
            }

            var dictionaries = window.Resources.MergedDictionaries;
            var oldTheme = dictionaries.OfType<EditorThemeResourceDictionary>().FirstOrDefault();
            var index = oldTheme is null ? 0 : dictionaries.IndexOf(oldTheme);
            if (oldTheme is not null)
            {
                dictionaries.Remove(oldTheme);
            }

            dictionaries.Insert(index, CreateThemeResources(theme));
            DarkTitleBar.Apply(window, theme);
        }
    }

    private static void Window_OnClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is not Window closedWindow)
        {
            return;
        }

        closedWindow.Closed -= Window_OnClosed;
        closedWindow.Loaded -= Window_OnLoaded;
        closedWindow.Closing -= Window_OnClosing;
        Windows.RemoveAll(reference =>
            !reference.TryGetTarget(out var window) || ReferenceEquals(window, closedWindow));
    }

    private static void Window_OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Loaded -= Window_OnLoaded;
        // Derived windows attach their data-loss guards in their constructors.
        // Register after Loaded so this observer sees their final Cancel value.
        window.Closing -= Window_OnClosing;
        window.Closing += Window_OnClosing;
    }

    private static void Window_OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (sender is not Window window)
        {
            return;
        }

        if (eventArgs.Cancel)
        {
            WindowCloseCancelled?.Invoke(window, EventArgs.Empty);
        }
    }

    private static EditorThemeResourceDictionary CreateThemeResources(EditorTheme theme)
    {
        var resources = new EditorThemeResourceDictionary();

        AddBrush(resources, "AppBackgroundBrush", ThemeColor(theme, "0A1018", "1E1E1E", "FFFFFF"));
        AddBrush(resources, "PanelBrush", ThemeColor(theme, "101A25", "252526", "F5F5F5"));
        AddBrush(resources, "PanelRaisedBrush", ThemeColor(theme, "162433", "2D2D30", "EDEDED"));
        AddBrush(resources, "BorderBrush", ThemeColor(theme, "2A3A49", "3F3F46", "C8C8C8"));
        AddBrush(resources, "TextBrush", ThemeColor(theme, "E8F0F5", "E0E0E0", "1E1E1E"));
        AddBrush(resources, "MutedTextBrush", ThemeColor(theme, "8FA2B2", "B0B0B0", "5F5F5F"));
        AddBrush(resources, "AccentBrush", ThemeColor(theme, "47B4D5", "007ACC", "0067C0"));
        AddBrush(resources, "AccentDimBrush", ThemeColor(theme, "243E4B", "264F78", "D6EBF7"));
        AddBrush(resources, "DangerBrush", ThemeColor(theme, "ED5665", "F06A73", "C42B1C"));
        AddBrush(resources, "WarningBrush", ThemeColor(theme, "E4AE56", "D7BA7D", "8A6500"));

        resources[SystemColors.HighlightBrushKey] = CreateBrush(ThemeColor(theme, "29485B", "264F78", "CCE8FF"));
        resources[SystemColors.InactiveSelectionHighlightBrushKey] = CreateBrush(ThemeColor(theme, "213B4B", "3F3F46", "E5E5E5"));
        resources[SystemColors.HighlightTextBrushKey] = CreateBrush(ThemeColor(theme, "FFFFFF", "FFFFFF", "1E1E1E"));
        resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = CreateBrush(ThemeColor(theme, "E8F0F5", "E0E0E0", "1E1E1E"));

        foreach (var hex in BrushColors)
        {
            AddBrush(resources, $"LE1GMEBrush{hex}", Transform(hex, theme));
        }

        foreach (var hex in ColorValues)
        {
            resources[$"LE1GMEColor{hex}"] = Transform(hex, theme);
        }

        // Galaxy, cluster and system canvases intentionally keep their authored
        // colors: only the surrounding application chrome follows the LEX theme.
        foreach (var hex in VisualBrushColors)
        {
            AddBrush(resources, $"LE1GMEVisualBrush{hex}", ParseColor(hex));
        }

        foreach (var hex in VisualColorValues)
        {
            resources[$"LE1GMEVisualColor{hex}"] = ParseColor(hex);
        }

        return resources;
    }

    private static Color ThemeColor(EditorTheme theme, string modernDark, string dark, string light) =>
        ParseColor(theme switch
        {
            EditorTheme.Light => light,
            EditorTheme.Dark => dark,
            _ => modernDark
        });

    private static void AddBrush(ResourceDictionary resources, object key, Color color) =>
        resources[key] = CreateBrush(color);

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Transform(string hex, EditorTheme theme)
    {
        var modern = ParseColor(hex);
        if (theme == EditorTheme.ModernDark)
        {
            return modern;
        }

        ToHsv(modern, out var hue, out var saturation, out var value);
        var semanticColor = saturation >= 0.42 &&
                            (hue < 175 || hue > 235 || value >= 0.58);

        Color transformed;
        if (theme == EditorTheme.Dark)
        {
            if (semanticColor)
            {
                transformed = value < 0.45
                    ? Blend(modern, Colors.White, 0.08)
                    : modern;
            }
            else
            {
                var level = value < 0.55
                    ? 24 + (int)Math.Round(value * 52)
                    : 174 + (int)Math.Round(value * 66);
                transformed = Gray(level);
            }
        }
        else if (semanticColor)
        {
            transformed = value < 0.45
                ? Blend(modern, Colors.White, 0.80)
                : Blend(modern, Colors.Black, value > 0.72 ? 0.30 : 0.12);
        }
        else if (value < 0.55)
        {
            transformed = Gray(255 - (int)Math.Round(value * 92));
        }
        else
        {
            transformed = Gray(28 + (int)Math.Round((1 - value) * 105));
        }

        transformed.A = modern.A;
        return transformed;
    }

    private static Color ParseColor(string hex) =>
        (Color)ColorConverter.ConvertFromString($"#{hex}");

    private static Color Gray(int level)
    {
        var channel = (byte)Math.Clamp(level, 0, 255);
        return Color.FromRgb(channel, channel, channel);
    }

    private static Color Blend(Color first, Color second, double secondWeight)
    {
        var firstWeight = 1 - secondWeight;
        return Color.FromArgb(
            first.A,
            (byte)Math.Round(first.R * firstWeight + second.R * secondWeight),
            (byte)Math.Round(first.G * firstWeight + second.G * secondWeight),
            (byte)Math.Round(first.B * firstWeight + second.B * secondWeight));
    }

    private static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;

        hue = delta == 0
            ? 0
            : max == red
                ? 60 * (((green - blue) / delta) % 6)
                : max == green
                    ? 60 * (((blue - red) / delta) + 2)
                    : 60 * (((red - green) / delta) + 4);
        if (hue < 0)
        {
            hue += 360;
        }

        saturation = max == 0 ? 0 : delta / max;
        value = max;
    }

    private static readonly string[] BrushColors =
    [
        "020407", "05080D", "080D13", "0A1018", "0A1118", "0B131B", "0B151F",
        "0C141D", "0C151E", "0D1721", "101820", "101C27", "111D28", "111E29",
        "132838", "142331", "142737", "142B3A", "17232E", "172532", "172735",
        "17384A", "183225", "1A2B37", "1C2630", "202E39", "203547", "203A4B",
        "233645", "243E4B", "244657", "29485B", "2A3A49", "2A4356", "315E43",
        "322919", "351A20", "355E75", "36171D", "496477", "607887", "62BEEA",
        "647784", "66502A", "68A5C0", "69CF8F", "71313D", "718594", "7890A2",
        "7A2E3A", "8499A8", "8AA0AE", "90D8A9", "91A4B2", "A9BBC7", "AA0A1018",
        "B9C4CC", "B9C9D4", "BDD0DD", "BFD1DC", "C6D5DF", "C8D7E1", "D8E0E7",
        "E3231C10", "E5EEF5", "F0141B23", "FF7D88", "FFFFFF"
    ];

    private static readonly string[] ColorValues = ["243E4B", "FFFFFF"];

    private static readonly string[] VisualBrushColors =
    [
        "05080D", "14070D12", "16FFB640", "24F2A33A", "324655", "35FFD267",
        "42F2A33A", "667C8B", "778C9C", "880A1018", "AA0A1018", "BD0A1018",
        "C4070B10", "E24A321B", "F2A33A", "FFD27A", "FFF3B34A", "FFFFD889"
    ];

    private static readonly string[] VisualColorValues =
    [
        "05070C", "070A10", "10131C", "101520", "15232E", "1B2638", "24313D",
        "24364B", "7A91A6", "FFFFB744"
    ];

    private sealed class EditorThemeResourceDictionary : ResourceDictionary
    {
    }
}
