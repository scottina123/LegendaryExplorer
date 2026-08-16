using System.Globalization;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

public sealed record PlanetAppearanceRandomizationResult(
    PlanetAppearance Appearance,
    string DonorName,
    int DonorRowId,
    IReadOnlyList<string> CustomTexturePaths,
    int Seed);

/// <summary>
/// Produces coherent variations of deduplicated, conservative BASEGAME planet
/// appearances. Values which interact in the material remain inherited as a
/// block; colour and scalar mutations preserve the derived energy relationships.
/// </summary>
public static class PlanetAppearanceRandomizer
{
    private static readonly string[] SurfaceColors =
    [
        "Beach_Color", "Continent_Color", "Continent_Color_Alt",
        "Ocean_Color", "Ocean_Color_Alt", "Silt_Color"
    ];

    private static readonly string[] AtmosphereColors =
    [
        "Atmosphere_Color", "Horizon_Atmosphere_Color", "Corona_Color"
    ];

    private const double MaximumSurfaceLuminance = 1.1;
    private const double MaximumAtmosphereEnergy = 45;
    private const double MaximumHorizonEnergy = 7.1;
    private const double MaximumCoronaEnergy = 3.25;
    private const double MaximumCityEnergy = 3.25;
    private const double MaximumLight1Energy = 3.2;
    private const double MaximumLight2Energy = 106;
    private const double OverallVariation = 0.35;
    private const double TopologyVariation = 0.2;
    private const double CustomTextureChance = 0.35;

    private static readonly IReadOnlyList<(string Column, PlanetTextureCategory Category)> TextureSlots =
    [
        ("ContinentMask01", PlanetTextureCategory.Continent),
        ("ContinentMask02", PlanetTextureCategory.Continent),
        ("Continent_Texture", PlanetTextureCategory.Continent),
        ("Normal_Map", PlanetTextureCategory.Normals),
        ("Ocean_Texture", PlanetTextureCategory.Ocean),
        ("City_Emissive", PlanetTextureCategory.CityEmissive),
        ("AtmosphereMaster", PlanetTextureCategory.Atmosphere)
    ];

    public static PlanetAppearanceRandomizationResult Generate(
        PlanetAppearance current,
        IEnumerable<Planet> baseGamePlanets,
        int? seed = null,
        IEnumerable<PlanetTextureLink>? customTextures = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseGamePlanets);

        var currentSignature = VisualSignature(current);
        var donors = baseGamePlanets
            .Where(PlanetAppearanceCodec.IsAppearanceCapable)
            .Select(planet => (Planet: planet, Appearance: PlanetAppearanceCodec.Decode(planet)))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Appearance.Shader))
            .Where(candidate => IsConservativeDonor(candidate.Appearance))
            .GroupBy(candidate => VisualSignature(candidate.Appearance), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (donors.Length == 0)
        {
            throw new InvalidOperationException("No suitable BASEGAME Planet appearances are available to randomise.");
        }

        var alternatives = donors
            .Where(candidate => !string.Equals(
                VisualSignature(candidate.Appearance),
                currentSignature,
                StringComparison.Ordinal))
            .ToArray();
        var pool = alternatives.Length > 0 ? alternatives : donors;
        var actualSeed = seed ?? Random.Shared.Next(0, int.MaxValue);
        var random = new Random(actualSeed);
        var donor = pool[random.Next(pool.Length)];

        var result = current.Clone();
        result.CopyVisualsFrom(donor.Appearance);
        var selectedCustomTextures = ApplyCustomTextures(result, customTextures, random);
        MutateCoherently(result, random);

        return new PlanetAppearanceRandomizationResult(
            result,
            donor.Planet.DisplayName,
            donor.Planet.RowId,
            selectedCustomTextures,
            actualSeed);
    }

    private static IReadOnlyList<string> ApplyCustomTextures(
        PlanetAppearance appearance,
        IEnumerable<PlanetTextureLink>? customTextures,
        Random random)
    {
        var links = (customTextures ?? [])
            .Where(link => !string.IsNullOrWhiteSpace(link.InMemoryPath) &&
                           link.Categories != PlanetTextureCategory.None)
            .GroupBy(link => link.InMemoryPath.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        if (links.Length == 0)
        {
            return [];
        }

        var selected = new List<string>();
        foreach (var (column, category) in TextureSlots)
        {
            var options = links
                .Where(link => (link.Categories & category) != 0)
                .ToArray();
            if (options.Length == 0 || random.NextDouble() >= CustomTextureChance)
            {
                continue;
            }

            var path = options[random.Next(options.Length)].InMemoryPath.Trim();
            appearance[column] = path;
            selected.Add(path);
        }

        return selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void MutateCoherently(PlanetAppearance appearance, Random random)
    {
        var atmosphereEnergy = AtmosphereEnergy(appearance);
        var horizonEnergy = HorizonEnergy(appearance);
        var coronaEnergy = CoronaEnergy(appearance);
        var cityEnergy = CityEnergy(appearance);
        var light1Energy = LightEnergy(appearance, 1);
        var light2Energy = LightEnergy(appearance, 2);

        var surfaceHue = RandomHueShift(random);
        var atmosphereHue = surfaceHue + Next(random, -15, 15);
        var surfaceSaturation = Next(random, 0.8, 1.2);
        var surfaceIntensity = VariationMultiplier(random);
        foreach (var color in SurfaceColors)
        {
            TransformHdrColor(appearance, color, surfaceHue, surfaceSaturation, surfaceIntensity);
        }
        LimitPaletteLuminance(appearance, SurfaceColors, MaximumSurfaceLuminance);

        foreach (var color in AtmosphereColors)
        {
            TransformHdrColor(
                appearance,
                color,
                atmosphereHue,
                Next(random, 0.8, 1.2),
                Next(random, 0.85, 1.15));
        }
        TransformHdrColor(
            appearance,
            "City_Emissive_Color",
            surfaceHue + Next(random, -10, 10),
            Next(random, 0.8, 1.2),
            1);
        TransformPackedColor(appearance, "SunColor1", atmosphereHue, random);
        TransformPackedColor(appearance, "SunColor2", atmosphereHue, random);

        RestoreColorMixerEnergy(
            appearance,
            "Atmosphere_Color",
            "Atmosphere_Mixer",
            Math.Min(MaximumAtmosphereEnergy, JitterEnergy(atmosphereEnergy, random)));
        RestoreScalarEnergy(
            appearance,
            "Horizon_Atmosphere_Color",
            "Horizon_Atmosphere_Intensity",
            Math.Min(MaximumHorizonEnergy, JitterEnergy(horizonEnergy, random)));
        RestoreScalarEnergy(
            appearance,
            "Corona_Color",
            "Opacity",
            Math.Min(MaximumCoronaEnergy, JitterEnergy(coronaEnergy, random)));
        RestoreColorMixerEnergy(
            appearance,
            "City_Emissive_Color",
            "City_Emissive_Mixer",
            Math.Min(MaximumCityEnergy, JitterEnergy(cityEnergy, random)));
        RestoreLightEnergy(
            appearance,
            1,
            Math.Min(MaximumLight1Energy, JitterEnergy(light1Energy, random)));
        RestoreLightEnergy(
            appearance,
            2,
            Math.Min(MaximumLight2Energy, JitterEnergy(light2Energy, random)));

        JitterLandTopology(appearance, random);
        JitterPositiveScalar(appearance, "Bump_Amount", random, 0, 10);
        JitterPositiveScalar(appearance, "Atmosphere_Min", random, 0, 2);
        JitterPositiveScalar(appearance, "Atmosphere_Tile_U", random, 0.02, 3);
        JitterPositiveScalar(appearance, "Atmosphere_Tile_V", random, 0.02, 2);
        JitterPanMultiplier(appearance, random);
        JitterPositiveScalar(appearance, "Emissive_Twinkle_Multiplier", random, 0, 0.03);
        JitterPositiveScalar(appearance, "Normal_Map_Tile", random, 0, 2);
        JitterPositiveScalar(appearance, "City_Emissive_Tile", random, 0, 3);
        JitterPositiveScalar(appearance, "Horizon_Atmosphere_Falloff", random, 0.5, 15);
    }

    private static bool IsConservativeDonor(PlanetAppearance appearance)
    {
        foreach (var property in PlanetAppearanceSchema.Properties)
        {
            if (property.Editor is PlanetAppearanceEditorKind.Scalar or
                PlanetAppearanceEditorKind.ColorVector or
                PlanetAppearanceEditorKind.MixerVector)
            {
                if (property.Columns.Any(column => !TryDouble(appearance[column], out _)))
                {
                    return false;
                }
            }
        }

        var maskValues = new[] { "Continent_Mask_Mixer", "Continent_Mask_Mixer02" }
            .SelectMany(prefix => "RGB".Select(component => Value(appearance, prefix + component)))
            .ToArray();
        if (maskValues.Any(value => value < 0))
        {
            return false;
        }

        var maskWeight = maskValues.Sum();
        if (maskWeight <= 0)
        {
            return false;
        }

        var thresholdRatio = Value(appearance, "Landmass_MixerG") / maskWeight;
        var beachRatio = Value(appearance, "Landmass_MixerR") / maskWeight;
        var siltRatio = Value(appearance, "Landmass_MixerB") / maskWeight;
        return thresholdRatio is >= 0 and <= 0.6 &&
               beachRatio is >= 0 and <= 0.85 &&
               siltRatio is >= 0 and <= 0.55 &&
               MaximumLuminance(appearance, SurfaceColors) <= MaximumSurfaceLuminance &&
               AtmosphereEnergy(appearance) <= MaximumAtmosphereEnergy &&
               HorizonEnergy(appearance) <= MaximumHorizonEnergy &&
               CoronaEnergy(appearance) <= MaximumCoronaEnergy &&
               CityEnergy(appearance) <= MaximumCityEnergy &&
               LightEnergy(appearance, 1) <= MaximumLight1Energy &&
               LightEnergy(appearance, 2) <= MaximumLight2Energy;
    }

    private static string VisualSignature(PlanetAppearance appearance) => string.Join(
        '|',
        PlanetAppearanceSchema.Columns
            .Where(column => !column.Equals("Shader", StringComparison.OrdinalIgnoreCase))
            .Select(column => $"{appearance[column].Length}:{appearance[column]}"));

    private static double RandomHueShift(Random random) => random.NextDouble() < 0.7
        ? Next(random, -45, 45)
        : Next(random, -180, 180);

    private static void TransformHdrColor(
        PlanetAppearance appearance,
        string prefix,
        double hueShift,
        double saturationScale,
        double intensityScale)
    {
        var red = Value(appearance, prefix + "R");
        var green = Value(appearance, prefix + "G");
        var blue = Value(appearance, prefix + "B");
        var maximum = Math.Max(red, Math.Max(green, blue));
        if (maximum <= 0)
        {
            return;
        }

        var (hue, saturation, value) = RgbToHsv(red / maximum, green / maximum, blue / maximum);
        var transformed = HsvToRgb(
            WrapHue(hue + hueShift),
            Math.Clamp(saturation * saturationScale, 0, 1),
            value);
        var scale = maximum * intensityScale;
        Set(appearance, prefix + "R", transformed.Red * scale);
        Set(appearance, prefix + "G", transformed.Green * scale);
        Set(appearance, prefix + "B", transformed.Blue * scale);
    }

    private static void TransformPackedColor(
        PlanetAppearance appearance,
        string column,
        double hueShift,
        Random random)
    {
        if (!long.TryParse(appearance[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
        {
            return;
        }

        var packed = unchecked((uint)signed);
        var red = (packed >> 16) & 0xff;
        var green = (packed >> 8) & 0xff;
        var blue = packed & 0xff;
        var (hue, saturation, value) = RgbToHsv(red / 255d, green / 255d, blue / 255d);
        var transformed = HsvToRgb(
            WrapHue(hue + hueShift),
            Math.Clamp(saturation * Next(random, 0.8, 1.2), 0, 1),
            value);
        var result = (packed & 0xff000000) |
                     ((uint)Math.Round(transformed.Red * 255) << 16) |
                     ((uint)Math.Round(transformed.Green * 255) << 8) |
                     (uint)Math.Round(transformed.Blue * 255);
        appearance[column] = unchecked((int)result).ToString(CultureInfo.InvariantCulture);
    }

    private static void LimitPaletteLuminance(
        PlanetAppearance appearance,
        IEnumerable<string> colors,
        double maximum)
    {
        var prefixes = colors.ToArray();
        var current = MaximumLuminance(appearance, prefixes);
        if (current <= maximum || current <= 0)
        {
            return;
        }

        var scale = maximum / current;
        foreach (var prefix in prefixes)
        {
            ScaleColor(appearance, prefix, scale);
        }
    }

    private static void RestoreColorMixerEnergy(
        PlanetAppearance appearance,
        string colorPrefix,
        string mixerPrefix,
        double targetEnergy)
    {
        var mixerSum = "RGB".Sum(component => Math.Max(0, Value(appearance, mixerPrefix + component)));
        var currentEnergy = Luminance(appearance, colorPrefix) * mixerSum;
        if (currentEnergy > 0)
        {
            ScaleColor(appearance, colorPrefix, targetEnergy / currentEnergy);
        }
    }

    private static void RestoreScalarEnergy(
        PlanetAppearance appearance,
        string colorPrefix,
        string scalarColumn,
        double targetEnergy)
    {
        var luminance = Luminance(appearance, colorPrefix);
        if (luminance > 0)
        {
            Set(appearance, scalarColumn, targetEnergy / luminance);
        }
    }

    private static void RestoreLightEnergy(PlanetAppearance appearance, int light, double targetEnergy)
    {
        var luminance = PackedLinearLuminance(appearance[$"SunColor{light}"]);
        if (luminance > 0)
        {
            Set(appearance, $"Brightness{light}", targetEnergy / luminance);
        }
    }

    private static void JitterLandTopology(PlanetAppearance appearance, Random random)
    {
        var maskColumns = new[] { "Continent_Mask_Mixer", "Continent_Mask_Mixer02" }
            .SelectMany(prefix => "RGB".Select(component => prefix + component))
            .ToArray();
        var originalWeight = maskColumns.Sum(column => Math.Max(0, Value(appearance, column)));
        if (originalWeight <= 0)
        {
            return;
        }

        foreach (var column in maskColumns)
        {
            var value = Value(appearance, column);
            if (value > 0)
            {
                Set(appearance, column, value * Next(
                    random,
                    1 - TopologyVariation,
                    1 + TopologyVariation));
            }
        }

        var variedWeight = maskColumns.Sum(column => Math.Max(0, Value(appearance, column)));
        var ratioLimits = new Dictionary<char, double>
        {
            ['R'] = 0.85,
            ['G'] = 0.6,
            ['B'] = 0.55
        };
        foreach (var component in "RGB")
        {
            var originalRatio = Value(appearance, "Landmass_Mixer" + component) / originalWeight;
            var variedRatio = originalRatio * Next(
                random,
                1 - TopologyVariation,
                1 + TopologyVariation);
            Set(
                appearance,
                "Landmass_Mixer" + component,
                variedWeight * Math.Clamp(variedRatio, 0, ratioLimits[component]));
        }
    }

    private static void JitterPositiveScalar(
        PlanetAppearance appearance,
        string column,
        Random random,
        double minimum,
        double maximum)
    {
        var value = Value(appearance, column);
        if (value <= 0)
        {
            return;
        }

        Set(appearance, column, Math.Clamp(value * VariationMultiplier(random), minimum, maximum));
    }

    private static void JitterPanMultiplier(PlanetAppearance appearance, Random random)
    {
        var value = Value(appearance, "Atmosphere_Pan_Multiplier");
        if (value == 1)
        {
            return;
        }

        var result = 1 + (value - 1) * VariationMultiplier(random);
        Set(appearance, "Atmosphere_Pan_Multiplier", Math.Clamp(result, 0, 8));
    }

    private static double JitterEnergy(double energy, Random random) => energy <= 0
        ? 0
        : energy * VariationMultiplier(random);

    private static double VariationMultiplier(Random random) => Next(
        random,
        1 - OverallVariation,
        1 + OverallVariation);

    private static double AtmosphereEnergy(PlanetAppearance appearance) =>
        Luminance(appearance, "Atmosphere_Color") *
        "RGB".Sum(component => Math.Max(0, Value(appearance, "Atmosphere_Mixer" + component)));

    private static double HorizonEnergy(PlanetAppearance appearance) =>
        Luminance(appearance, "Horizon_Atmosphere_Color") *
        Math.Max(0, Value(appearance, "Horizon_Atmosphere_Intensity"));

    private static double CoronaEnergy(PlanetAppearance appearance) =>
        Luminance(appearance, "Corona_Color") * Math.Max(0, Value(appearance, "Opacity"));

    private static double CityEnergy(PlanetAppearance appearance) =>
        Luminance(appearance, "City_Emissive_Color") *
        "RGB".Sum(component => Math.Max(0, Value(appearance, "City_Emissive_Mixer" + component)));

    private static double LightEnergy(PlanetAppearance appearance, int light) =>
        PackedLinearLuminance(appearance[$"SunColor{light}"]) *
        Math.Max(0, Value(appearance, $"Brightness{light}"));

    private static double MaximumLuminance(PlanetAppearance appearance, IEnumerable<string> prefixes) =>
        prefixes.Max(prefix => Luminance(appearance, prefix));

    private static double Luminance(PlanetAppearance appearance, string prefix) =>
        0.2126 * Value(appearance, prefix + "R") +
        0.7152 * Value(appearance, prefix + "G") +
        0.0722 * Value(appearance, prefix + "B");

    private static double PackedLinearLuminance(string token)
    {
        if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
        {
            return 0;
        }

        var packed = unchecked((uint)signed);
        return 0.2126 * SrgbToLinear((packed >> 16) & 0xff) +
               0.7152 * SrgbToLinear((packed >> 8) & 0xff) +
               0.0722 * SrgbToLinear(packed & 0xff);
    }

    private static double SrgbToLinear(uint component)
    {
        var value = component / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static void ScaleColor(PlanetAppearance appearance, string prefix, double scale)
    {
        foreach (var component in "RGB")
        {
            Set(appearance, prefix + component, Value(appearance, prefix + component) * scale);
        }
    }

    private static double Value(PlanetAppearance appearance, string column) =>
        TryDouble(appearance[column], out var value) ? value : 0;

    private static bool TryDouble(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    private static void Set(PlanetAppearance appearance, string column, double value) =>
        appearance[column] = ((float)value).ToString("R", CultureInfo.InvariantCulture);

    private static double Next(Random random, double minimum, double maximum) =>
        minimum + random.NextDouble() * (maximum - minimum);

    private static double WrapHue(double hue)
    {
        hue %= 360;
        return hue < 0 ? hue + 360 : hue;
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(
        double red,
        double green,
        double blue)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        var hue = delta <= 0
            ? 0
            : maximum == red
                ? 60 * (((green - blue) / delta) % 6)
                : maximum == green
                    ? 60 * ((blue - red) / delta + 2)
                    : 60 * ((red - green) / delta + 4);
        return (WrapHue(hue), maximum <= 0 ? 0 : delta / maximum, maximum);
    }

    private static (double Red, double Green, double Blue) HsvToRgb(
        double hue,
        double saturation,
        double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var match = value - chroma;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return (red + match, green + match, blue + match);
    }
}
