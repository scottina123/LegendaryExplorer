using System.Numerics;

namespace LE1GalaxyMapEditor.Rendering;

/// <summary>
/// Renderer-facing material data. This deliberately uses framework types so
/// SharpDX remains an implementation detail of the rendering project.
/// </summary>
public sealed class PlanetRenderMaterial
{
    public float HorizonAtmosphereIntensity { get; set; } = 0.7f;
    public float HorizonAtmosphereFalloff { get; set; } = 6;
    public float BumpAmount { get; set; }
    public float AtmosphereMin { get; set; }
    public float AtmosphereTileU { get; set; } = 1;
    public float AtmosphereTileV { get; set; } = 0.6f;
    public float AtmospherePanMultiplier { get; set; } = 1.25f;
    public float EmissiveTwinkleMultiplier { get; set; } = 0.03f;
    public float NormalMapTile { get; set; } = 1;
    public float CityEmissiveTile { get; set; } = 1;

    public string ContinentMask01 { get; set; } = "GXM_ContinentMask01";
    public string ContinentMask02 { get; set; } = "GXM_DiffuseMask01";
    public string ContinentTexture { get; set; } = "GXM_DiffuseMask01";
    public string OceanTexture { get; set; } = "GXM_DiffuseMask01";
    public string AtmosphereMaster { get; set; } = "GXM_Atmosphere03";
    public string CityEmissive { get; set; } = "GXM_ContinentMask02";
    public string NormalMap { get; set; } = "GXM_PlanetNormal01";

    public Vector4 AtmosphereColor { get; set; } = new(5, 5, 5, 0);
    public Vector4 AtmosphereMixer { get; set; } = new(2, 0, 0.1f, 0);
    public Vector4 BeachColor { get; set; } = new(0.098689f, 0.394083f, 0.907547f, 0);
    public Vector4 CityEmissiveColor { get; set; } = new(0, 0, 0, 1);
    public Vector4 CityEmissiveMixer { get; set; } = new(1, 0, 0, 1);
    public Vector4 ContinentColor { get; set; } = new(0.034230f, 0.227137f, 0.024223f, 0);
    public Vector4 ContinentColorAlt { get; set; } = new(0.638780f, 0.511398f, 0.201096f, 0);
    public Vector4 ContinentMaskMixer { get; set; } = new(2, 1, 0, 0);
    public Vector4 ContinentMaskMixer02 { get; set; } = new(0, 0, 1, 0);
    public Vector4 ContinentTextureMixer { get; set; } = new(1, 0, 0, 0);
    public Vector4 HorizonAtmosphereColor { get; set; } = new(0, 0.061907f, 0.344026f, 0.891262f);
    public Vector4 LandmassMixer { get; set; } = new(0.2f, 1, 0.3f, 0);
    public Vector4 OceanColor { get; set; } = new(0.000262f, 0.024223f, 0.093876f, 0);
    public Vector4 OceanColorAlt { get; set; } = new(0.001433f, 0.130352f, 0.517401f, 0);
    public Vector4 OceanTextureMixer { get; set; } = new(0, 0, 5, 0);
    public Vector4 SiltColor { get; set; } = new(0.001963f, 0.166872f, 0.652370f, 0);

    public float FringeBloom { get; set; }
    public float Opacity { get; set; } = 5;
    public Vector4 CoronaColor { get; set; } = new(0.121986f, 0.442323f, 1, 0);
    public uint SunColor0 { get; set; } = 14217470;
    public uint SunColor1 { get; set; } = 14217470;
    public uint SunColor2 { get; set; } = 1259637;
    public float Brightness0 { get; set; } = 2;
    public float Brightness1 { get; set; } = 1.5f;
    public float Brightness2 { get; set; } = 3;

    public PlanetRenderMaterial Clone() => (PlanetRenderMaterial)MemberwiseClone();
}

public readonly record struct PlanetPreviewOptions(
    bool Lit = true,
    bool PointLights = true,
    bool PostProcessed = true,
    bool Corona = true,
    bool Stars = true);

public sealed record PlanetPreviewFrame(
    byte[] BgraPixels,
    int Width,
    int Height,
    TimeSpan RenderTime,
    IReadOnlyList<string> MissingTextures);

internal readonly record struct ValidationMode(
    bool Lit,
    bool PointLights,
    bool PostProcessed,
    bool Corona,
    bool Stars);
