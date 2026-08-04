using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using SharpDX.DXGI;
using System;

namespace LegendaryExplorer.Misc;

/// <summary>
/// Color-space helpers for the Direct3D preview renderers. Unreal color textures are sampled in
/// sRGB space, lighting is evaluated in linear space, and the preview backbuffers store sRGB values.
/// </summary>
internal static class PreviewColorSpace
{
    public static bool UsesSrgbSampling(ExportEntry textureExport)
        => textureExport?.GetProperty<BoolProperty>("SRGB")?.Value ?? true;

    public static Format ToSrgbFormat(Format format) => format switch
    {
        Format.R8G8B8A8_UNorm => Format.R8G8B8A8_UNorm_SRgb,
        Format.B8G8R8A8_UNorm => Format.B8G8R8A8_UNorm_SRgb,
        Format.B8G8R8X8_UNorm => Format.B8G8R8X8_UNorm_SRgb,
        Format.BC1_UNorm => Format.BC1_UNorm_SRgb,
        Format.BC2_UNorm => Format.BC2_UNorm_SRgb,
        Format.BC3_UNorm => Format.BC3_UNorm_SRgb,
        Format.BC7_UNorm => Format.BC7_UNorm_SRgb,
        _ => format
    };

    public static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f
            ? value * 12.92f
            : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    public static float SrgbToLinear(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>
    /// UE3 base-pass shaders normally feed a later post-process/gamma pass. Preview renderers draw
    /// them directly into an UNorm backbuffer, so insert that final linear-to-sRGB conversion before
    /// the decompiled shader's return statement.
    /// </summary>
    public static string EncodePixelShaderOutput(string hlsl)
    {
        if (!hlsl.Contains("out float4 o0", StringComparison.Ordinal))
        {
            return hlsl;
        }

        int closingBrace = hlsl.LastIndexOf('}');
        if (closingBrace < 0)
        {
            return hlsl;
        }

        int insertionPoint = hlsl.LastIndexOf("return;", closingBrace, StringComparison.Ordinal);
        if (insertionPoint < 0)
        {
            insertionPoint = closingBrace;
        }

        const string conversion = """
  float3 lexPreviewLinearOutput = max(o0.xyz, float3(0, 0, 0));
  float3 lexPreviewSrgbLow = lexPreviewLinearOutput * 12.92;
  float3 lexPreviewSrgbHigh = 1.055 * pow(lexPreviewLinearOutput, float3(1.0 / 2.4, 1.0 / 2.4, 1.0 / 2.4)) - 0.055;
  float3 lexPreviewUseHigh = step(float3(0.0031308, 0.0031308, 0.0031308), lexPreviewLinearOutput);
  o0.xyz = lerp(lexPreviewSrgbLow, lexPreviewSrgbHigh, lexPreviewUseHigh);
""";
        return hlsl.Insert(insertionPoint, conversion);
    }
}
