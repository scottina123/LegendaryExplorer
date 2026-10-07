using SharpDX.Direct3D11;

namespace LegendaryExplorer.Tools.AssetDatabase.VFXPreview;

/// <summary>
/// Converts the fallback shaders' straight texture color to the input expected by each material blend mode.
/// Cooked shaders already perform this conversion and must not be premultiplied a second time.
/// </summary>
internal static class VfxMaterialBlending
{
    internal const int BlendModeShift = 25;

    internal static RenderTargetBlendDescription CreateBlendDescription(VfxBlendMode mode)
    {
        (BlendOption source, BlendOption destination) = mode switch
        {
            VfxBlendMode.Opaque or VfxBlendMode.Masked => (BlendOption.One, BlendOption.Zero),
            VfxBlendMode.Additive => (BlendOption.One, BlendOption.One),
            VfxBlendMode.Modulate => (BlendOption.DestinationColor, BlendOption.Zero),
            VfxBlendMode.ModulateAndAdd => (BlendOption.DestinationColor, BlendOption.One),
            VfxBlendMode.AlphaComposite => (BlendOption.One, BlendOption.InverseSourceAlpha),
            _ => (BlendOption.SourceAlpha, BlendOption.InverseSourceAlpha)
        };
        bool preservesAlpha = mode is VfxBlendMode.Additive or VfxBlendMode.Modulate or VfxBlendMode.ModulateAndAdd;
        bool opaque = mode is VfxBlendMode.Opaque or VfxBlendMode.Masked;
        return new RenderTargetBlendDescription
        {
            IsBlendEnabled = !opaque,
            SourceBlend = source,
            DestinationBlend = destination,
            BlendOperation = BlendOperation.Add,
            SourceAlphaBlend = preservesAlpha ? BlendOption.Zero : BlendOption.One,
            DestinationAlphaBlend = preservesAlpha ? BlendOption.One : opaque ? BlendOption.Zero : BlendOption.InverseSourceAlpha,
            AlphaBlendOperation = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteMaskFlags.All
        };
    }

    // VfxBlendMode and VfxOpacitySource values are supplied in the shader flags by both fallback renderers.
    internal const string ShaderFunctions = """
float4 ApplyVfxMaterialBlend(float3 color, float alpha, float particleAlpha, int blendMode, int opacitySource)
{
    if (blendMode == 0 || blendMode == 1) return float4(color, 1);
    if (blendMode == 3)
    {
        // Synthesized luminance coverage is already present in an additive atlas's RGB. Applying it again
        // would square that coverage; the particle's lifetime fade still needs to attenuate the RGB.
        float fade = opacitySource == 1 ? saturate(particleAlpha) : alpha;
        return float4(color * fade, alpha);
    }
    if (blendMode == 4) return float4(lerp(float3(1, 1, 1), color, alpha), alpha);
    if (blendMode == 5 || blendMode == 7) return float4(color * alpha, alpha);
    return float4(color, alpha);
}
""";
}
