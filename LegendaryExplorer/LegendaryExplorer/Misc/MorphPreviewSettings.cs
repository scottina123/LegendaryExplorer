using Scene3D = LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegacyScene3D = LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;

namespace LegendaryExplorer.Misc;

/// <summary>
/// Shared display settings for Morph Editor and Actor Preview. Color textures are decoded to linear,
/// lit with a neutral skylight, and encoded to sRGB once by the preview pixel shader.
/// </summary>
internal static class MorphPreviewSettings
{
    private const float LightScale = 1.5f;
    // UE's optional shader gamma stays neutral because output encoding already handles it.
    private const float InvGamma = 1f;

    public static void Apply(Scene3D.MeshRenderContext context)
    {
        context.UseSrgbColorManagement = true;
        context.GameShaderLightScale = LightScale;
        context.GameShaderInvGamma = InvGamma;
    }

    public static void Apply(LegacyScene3D.MeshRenderContext context)
    {
        context.UseSrgbColorManagement = true;
        context.GameShaderLightScale = LightScale;
        context.GameShaderInvGamma = InvGamma;
    }
}
