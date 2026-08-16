using System.Globalization;
using System.Numerics;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Rendering;

namespace LE1GalaxyMapEditor.Services;

public static class PlanetAppearanceCodec
{
    private const string VanillaTexturePackagePrefix = "BIOA_GXM10_T.";

    public static PlanetAppearance Decode(Planet planet)
    {
        ArgumentNullException.ThrowIfNull(planet);
        return new PlanetAppearance(PlanetAppearanceSchema.Columns.Select(column =>
            new KeyValuePair<string, string>(column, planet.ExtraFields.GetValueOrDefault(column) ?? string.Empty)));
    }

    public static IReadOnlyList<string> ChangedColumns(
        PlanetAppearance original,
        PlanetAppearance edited)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        return PlanetAppearanceSchema.Columns
            .Where(column => !string.Equals(original[column], edited[column], StringComparison.Ordinal))
            .ToArray();
    }

    public static bool VisualsEqual(PlanetAppearance left, PlanetAppearance right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return PlanetAppearanceSchema.Columns
            .Where(column => !column.Equals("Shader", StringComparison.OrdinalIgnoreCase))
            .All(column => string.Equals(left[column], right[column], StringComparison.Ordinal));
    }

    public static bool IsAppearanceCapable(Planet planet) =>
        planet.PlanetLevelType == 1 &&
        !planet.IsAsteroidBelt &&
        !planet.NameText.Contains("Asteroid", StringComparison.OrdinalIgnoreCase);

    public static PlanetRenderMaterial ToRenderMaterial(PlanetAppearance appearance) => new()
    {
        HorizonAtmosphereIntensity = Float(appearance, "Horizon_Atmosphere_Intensity", 0.7f),
        HorizonAtmosphereFalloff = Float(appearance, "Horizon_Atmosphere_Falloff", 6),
        BumpAmount = Float(appearance, "Bump_Amount"),
        AtmosphereMin = Float(appearance, "Atmosphere_Min"),
        AtmosphereTileU = Float(appearance, "Atmosphere_Tile_U", 1),
        AtmosphereTileV = Float(appearance, "Atmosphere_Tile_V", 0.6f),
        AtmospherePanMultiplier = Float(appearance, "Atmosphere_Pan_Multiplier", 1.25f),
        EmissiveTwinkleMultiplier = Float(appearance, "Emissive_Twinkle_Multiplier", 0.03f),
        NormalMapTile = Float(appearance, "Normal_Map_Tile", 1),
        CityEmissiveTile = Float(appearance, "City_Emissive_Tile", 1),
        ContinentMask01 = Texture(appearance, "ContinentMask01", "GXM_ContinentMask01"),
        ContinentMask02 = Texture(appearance, "ContinentMask02", "GXM_DiffuseMask01"),
        ContinentTexture = Texture(appearance, "Continent_Texture", "GXM_DiffuseMask01"),
        OceanTexture = Texture(appearance, "Ocean_Texture", "GXM_DiffuseMask01"),
        AtmosphereMaster = Texture(appearance, "AtmosphereMaster", "GXM_Atmosphere03"),
        CityEmissive = Texture(appearance, "City_Emissive", "GXM_ContinentMask02"),
        NormalMap = Texture(appearance, "Normal_Map", "GXM_PlanetNormal01"),
        AtmosphereColor = Vector(appearance, "Atmosphere_Color"),
        AtmosphereMixer = Vector(appearance, "Atmosphere_Mixer"),
        BeachColor = Vector(appearance, "Beach_Color"),
        CityEmissiveColor = Vector(appearance, "City_Emissive_Color"),
        CityEmissiveMixer = Vector(appearance, "City_Emissive_Mixer"),
        ContinentColor = Vector(appearance, "Continent_Color"),
        ContinentColorAlt = Vector(appearance, "Continent_Color_Alt"),
        ContinentMaskMixer = Vector(appearance, "Continent_Mask_Mixer"),
        ContinentMaskMixer02 = Vector(appearance, "Continent_Mask_Mixer02"),
        ContinentTextureMixer = Vector(appearance, "Continent_Texture_Mixer"),
        HorizonAtmosphereColor = Vector(appearance, "Horizon_Atmosphere_Color"),
        LandmassMixer = Vector(appearance, "Landmass_Mixer"),
        OceanColor = Vector(appearance, "Ocean_Color"),
        OceanColorAlt = Vector(appearance, "Ocean_Color_Alt"),
        OceanTextureMixer = Vector(appearance, "Ocean_Texture_Mixer"),
        SiltColor = Vector(appearance, "Silt_Color"),
        SunColor0 = Packed(appearance, "SunColor0"),
        SunColor1 = Packed(appearance, "SunColor1"),
        SunColor2 = Packed(appearance, "SunColor2"),
        Brightness0 = Float(appearance, "Brightness0", 2),
        Brightness1 = Float(appearance, "Brightness1", 1.5f),
        Brightness2 = Float(appearance, "Brightness2", 3),
        FringeBloom = Float(appearance, "Fringe_Bloom"),
        Opacity = Float(appearance, "Opacity", 5),
        CoronaColor = Vector(appearance, "Corona_Color")
    };

    public static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static bool TryParseFloat(string token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    public static string TextureDisplayName(string reference)
    {
        var trimmed = (reference ?? string.Empty).Trim();
        while (trimmed.StartsWith(VanillaTexturePackagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[VanillaTexturePackagePrefix.Length..];
        }

        var objectSeparator = trimmed.LastIndexOf('.');
        if (objectSeparator >= 0 && objectSeparator < trimmed.Length - 1)
        {
            trimmed = trimmed[(objectSeparator + 1)..];
        }

        return trimmed;
    }

    private static float Float(PlanetAppearance appearance, string column, float fallback = 0) =>
        TryParseFloat(appearance[column], out var value) ? value : fallback;

    private static Vector4 Vector(PlanetAppearance appearance, string prefix) => new(
        Float(appearance, prefix + "R"),
        Float(appearance, prefix + "G"),
        Float(appearance, prefix + "B"),
        Float(appearance, prefix + "A"));

    private static uint Packed(PlanetAppearance appearance, string column)
    {
        var token = appearance[column];
        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
        {
            return unchecked((uint)signed);
        }

        return uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string Texture(PlanetAppearance appearance, string column, string fallback) =>
        string.IsNullOrWhiteSpace(appearance[column]) ? fallback : appearance[column].Trim();
}
