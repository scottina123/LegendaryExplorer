namespace LE1GalaxyMapEditor.Services;

public enum PlanetAppearanceEditorKind
{
    Shader,
    Scalar,
    Texture,
    ColorVector,
    MixerVector,
    PackedColor
}

public sealed record PlanetAppearancePropertyDefinition(
    string Id,
    string DisplayName,
    string Group,
    PlanetAppearanceEditorKind Editor,
    string Description,
    double? Minimum = null,
    double? Maximum = null,
    double? Step = null,
    IReadOnlyList<string>? TextureOptions = null,
    IReadOnlyList<string>? ComponentLabels = null)
{
    public IReadOnlyList<string> Columns { get; } = Editor is PlanetAppearanceEditorKind.ColorVector or PlanetAppearanceEditorKind.MixerVector
        ? [Id + "R", Id + "G", Id + "B", Id + "A"]
        : [Id];
}

public sealed record PlanetAppearanceGroupDefinition(
    string Name,
    string Description,
    bool ExpandedByDefault);

public static class PlanetAppearanceSchema
{
    public static IReadOnlyList<PlanetAppearanceGroupDefinition> Groups { get; } =
    [
        new("Identity", "Give this planet row its own unique material-instance name.", true),
        new("Continent / Landmass", "Build the land surface from two packed masks, a detail texture, colour layers, and their channel weights.", true),
        new("Normals", "Control small-scale surface relief and the tiling of the normal map.", true),
        new("Ocean", "Choose the ocean detail texture, its colour layers, and the packed channels used to blend them.", true),
        new("Beach / Silt", "Set the coastline and shallow-water transition colours.", true),
        new("City Emissive", "Configure the night-side city mask, colour, tiling, channel weights, and animated twinkle.", true),
        new("Atmosphere / Horizon", "Shape the moving atmosphere layer and the glow around the planet silhouette.", true),
        new("Corona", "Control the outer corona colour, bloom, and opacity.", true),
        new("Lights", "Set the packed colours and brightness values used by the planet's two runtime lights.", true)
    ];

    public static IReadOnlyList<PlanetAppearancePropertyDefinition> Properties { get; } =
    [
        new("Shader", "Shader name", "Identity", PlanetAppearanceEditorKind.Shader,
            "Unique material-instance name written to this Planet row. It cannot match another effective Planet."),

        new("ContinentMask01", "Primary mask", "Continent / Landmass", PlanetAppearanceEditorKind.Texture,
            "Texture reference whose channels define the primary landmass masks.",
            TextureOptions: ["BIOA_GXM10_T.GXM_ContinentMask01", "BIOA_GXM10_T.GXM_Atmosphere02", "BIOA_GXM10_T.GXM_ContinentMask04"]),
        new("ContinentMask02", "Secondary mask", "Continent / Landmass", PlanetAppearanceEditorKind.Texture,
            "Texture reference supplying additional land and variation masks.",
            TextureOptions: ["BIOA_GXM10_T.GXM_DiffuseMask01", "BIOA_GXM10_T.GXM_ContinentMask02"]),
        new("Continent_Texture", "Surface texture", "Continent / Landmass", PlanetAppearanceEditorKind.Texture,
            "Detail texture mixed across the land surface.",
            TextureOptions: ["BIOA_GXM10_T.GXM_DiffuseMask01"]),
        new("Continent_Color", "Continent colour", "Continent / Landmass", PlanetAppearanceEditorKind.ColorVector,
            "Primary HDR colour applied to land."),
        new("Continent_Color_Alt", "Alternate colour", "Continent / Landmass", PlanetAppearanceEditorKind.ColorVector,
            "Secondary HDR land colour blended by the masks."),
        new("Landmass_Mixer", "Landmass blending", "Continent / Landmass", PlanetAppearanceEditorKind.MixerVector,
            "Controls the land cutoff and the transition bands used for beach and silt colouring.", 0, 10, 0.1,
            ComponentLabels: ["Beach transition", "Land threshold", "Silt transition"]),
        new("Continent_Mask_Mixer", "Primary mask channels", "Continent / Landmass", PlanetAppearanceEditorKind.MixerVector,
            "Weights the red, green and blue masks packed into the primary texture.", 0, 10, 0.1,
            ComponentLabels: ["Red channel", "Green channel", "Blue channel"]),
        new("Continent_Mask_Mixer02", "Secondary mask channels", "Continent / Landmass", PlanetAppearanceEditorKind.MixerVector,
            "Weights the red, green and blue masks packed into the secondary texture.", 0, 10, 0.1,
            ComponentLabels: ["Red channel", "Green channel", "Blue channel"]),
        new("Continent_Texture_Mixer", "Texture channels", "Continent / Landmass", PlanetAppearanceEditorKind.MixerVector,
            "Weights the red, green and blue masks packed into the land detail texture.", 0, 10, 0.1,
            ComponentLabels: ["Red channel", "Green channel", "Blue channel"]),

        new("Normal_Map", "Normal map", "Normals", PlanetAppearanceEditorKind.Texture,
            "Surface normal texture. Package-qualified references are preserved exactly.",
            TextureOptions: ["BIOA_GXM10_T.GXM_PlanetNormal01", "BIOA_GXM10_T.GXM_PlanetNormal02"]),
        new("Normal_Map_Tile", "Normal tiling", "Normals", PlanetAppearanceEditorKind.Scalar,
            "UV repeat applied to the normal map.", 0, 2, 0.1),
        new("Bump_Amount", "Bump amount", "Normals", PlanetAppearanceEditorKind.Scalar,
            "Strength of normal-map surface relief.", 0, 10, 0.1),

        new("Ocean_Texture", "Surface texture", "Ocean", PlanetAppearanceEditorKind.Texture,
            "Detail texture mixed across the ocean surface.",
            TextureOptions: ["BIOA_GXM10_T.GXM_DiffuseMask01"]),
        new("Ocean_Color", "Ocean colour", "Ocean", PlanetAppearanceEditorKind.ColorVector,
            "Primary HDR colour applied to oceans."),
        new("Ocean_Color_Alt", "Alternate colour", "Ocean", PlanetAppearanceEditorKind.ColorVector,
            "Secondary HDR ocean colour blended by the masks."),
        new("Ocean_Texture_Mixer", "Texture channels", "Ocean", PlanetAppearanceEditorKind.MixerVector,
            "Weights the red, green and blue masks packed into the ocean detail texture.", 0, 10, 0.1,
            ComponentLabels: ["Red channel", "Green channel", "Blue channel"]),

        new("Beach_Color", "Beach colour", "Beach / Silt", PlanetAppearanceEditorKind.ColorVector,
            "HDR colour used at the coastline transition."),
        new("Silt_Color", "Silt colour", "Beach / Silt", PlanetAppearanceEditorKind.ColorVector,
            "HDR shallow-water and coastal silt colour."),

        new("City_Emissive", "Emissive mask", "City Emissive", PlanetAppearanceEditorKind.Texture,
            "Night-side emissive mask used for city lights.",
            TextureOptions: ["BIOA_GXM10_T.GXM_ContinentMask02", "BIOA_GXM10_T.GXM_ContinentMask03"]),
        new("City_Emissive_Color", "Emissive colour", "City Emissive", PlanetAppearanceEditorKind.ColorVector,
            "HDR RGBA colour of night-side city lights."),
        new("City_Emissive_Mixer", "Mask channels", "City Emissive", PlanetAppearanceEditorKind.MixerVector,
            "Weights the red, green and blue masks packed into the emissive texture.", 0, 10, 0.1,
            ComponentLabels: ["Red channel", "Green channel", "Blue channel"]),
        new("City_Emissive_Tile", "Tiling", "City Emissive", PlanetAppearanceEditorKind.Scalar,
            "UV repeat applied to the city emissive mask.", 0, 3, 0.1),
        new("Emissive_Twinkle_Multiplier", "Twinkle", "City Emissive", PlanetAppearanceEditorKind.Scalar,
            "Animated variation applied to city emissive light.", 0, 0.1, 0.01),

        new("AtmosphereMaster", "Atmosphere mask", "Atmosphere / Horizon", PlanetAppearanceEditorKind.Texture,
            "Texture controlling the moving atmosphere layer.",
            TextureOptions: ["BIOA_GXM10_T.GXM_Atmosphere01", "BIOA_GXM10_T.GXM_Atmosphere02", "BIOA_GXM10_T.GXM_Atmosphere03", "BIOA_GXM10_T.GXM_ContinentMask04", "BIOA_GXM10_T.GXM_DiffuseMask01"]),
        new("Atmosphere_Color", "Atmosphere colour", "Atmosphere / Horizon", PlanetAppearanceEditorKind.ColorVector,
            "HDR RGBA multiplier for the atmosphere texture."),
        new("Atmosphere_Mixer", "Atmosphere channels", "Atmosphere / Horizon", PlanetAppearanceEditorKind.MixerVector,
            "Weights the red, green and blue masks packed into the atmosphere texture.", 0, 10, 0.1,
            ComponentLabels: ["Red channel", "Green channel", "Blue channel"]),
        new("Atmosphere_Min", "Minimum", "Atmosphere / Horizon", PlanetAppearanceEditorKind.Scalar,
            "Minimum contribution of the atmosphere layer.", 0, 2, 0.1),
        new("Atmosphere_Tile_U", "Tile U", "Atmosphere / Horizon", PlanetAppearanceEditorKind.Scalar,
            "Horizontal repeat of the atmosphere mask.", 0, 3, 0.1),
        new("Atmosphere_Tile_V", "Tile V", "Atmosphere / Horizon", PlanetAppearanceEditorKind.Scalar,
            "Vertical repeat of the atmosphere mask.", 0, 2, 0.1),
        new("Atmosphere_Pan_Multiplier", "Pan multiplier", "Atmosphere / Horizon", PlanetAppearanceEditorKind.Scalar,
            "Animation speed multiplier for the atmosphere mask.", 0, 8, 0.1),
        new("Horizon_Atmosphere_Color", "Horizon colour", "Atmosphere / Horizon", PlanetAppearanceEditorKind.ColorVector,
            "HDR RGBA colour of the silhouette glow."),
        new("Horizon_Atmosphere_Intensity", "Horizon intensity", "Atmosphere / Horizon", PlanetAppearanceEditorKind.Scalar,
            "Brightness of the atmosphere around the planet silhouette.", 0, 10, 0.1),
        new("Horizon_Atmosphere_Falloff", "Horizon falloff", "Atmosphere / Horizon", PlanetAppearanceEditorKind.Scalar,
            "Controls how tightly the horizon glow hugs the silhouette.", 0, 15, 0.1),

        new("Corona_Color", "Colour", "Corona", PlanetAppearanceEditorKind.ColorVector,
            "HDR RGBA colour applied to the planet corona."),
        new("Fringe_Bloom", "Fringe bloom", "Corona", PlanetAppearanceEditorKind.Scalar,
            "Additional bloom contribution at the corona fringe.", 0, 5, 0.1),
        new("Opacity", "Opacity", "Corona", PlanetAppearanceEditorKind.Scalar,
            "Overall intensity/opacity multiplier for the corona.", 0, 20, 0.1),

        new("SunColor1", "Light 1 colour", "Lights", PlanetAppearanceEditorKind.PackedColor,
            "Packed ARGB colour of the first preview/game light."),
        new("Brightness1", "Light 1 brightness", "Lights", PlanetAppearanceEditorKind.Scalar,
            "Intensity of the first light.", 0, 20, 0.1),
        new("SunColor2", "Light 2 colour", "Lights", PlanetAppearanceEditorKind.PackedColor,
            "Packed ARGB colour of the second preview/game light."),
        new("Brightness2", "Light 2 brightness", "Lights", PlanetAppearanceEditorKind.Scalar,
            "Intensity of the second light.", 0, 50, 0.1),
        new("SunColor0", "Unused colour", "Lights", PlanetAppearanceEditorKind.PackedColor,
            "Packed ARGB colour retained for CSV fidelity; vanilla appears not to use light 0."),
        new("Brightness0", "Unused brightness", "Lights", PlanetAppearanceEditorKind.Scalar,
            "Brightness retained for CSV fidelity; vanilla appears not to use light 0.", 0, 50, 0.1)
    ];

    public static IReadOnlyList<string> Columns { get; } = Properties
        .SelectMany(property => property.Columns)
        .ToArray();

    private static readonly HashSet<string> ColumnSet = new(Columns, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TextureObjectSet = new(
        Properties.SelectMany(property => property.TextureOptions ?? [])
            .Select(PlanetAppearanceCodec.TextureDisplayName),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsAppearanceColumn(string column) => ColumnSet.Contains(column);

    public static bool IsSupportedTextureObject(string objectName)
        => !string.IsNullOrWhiteSpace(objectName) && TextureObjectSet.Contains(objectName.Trim());

    public static bool IsSelectablePlanetTextureObject(
        string objectName,
        bool isCustomResourceTexture,
        bool isReferencedByPlanet)
        => !string.IsNullOrWhiteSpace(objectName) &&
           (IsSupportedTextureObject(objectName) || isCustomResourceTexture && isReferencedByPlanet);
}
