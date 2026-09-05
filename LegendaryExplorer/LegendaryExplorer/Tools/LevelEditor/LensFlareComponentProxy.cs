using System;
using System.Numerics;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.LevelEditor;

public sealed class LensFlareComponentProxy : PrimitiveComponentProxy, ILevelRenderResource
{
    private readonly LevelEditorRenderContext renderContext;
    private readonly IEntry template;
    private LevelLensFlareRenderer.PreparedFlare flare;
    private volatile bool renderResourcesInitialized;
    private bool active;
    private Vector4 sourceColor;

    public bool RenderResourcesInitialized => renderResourcesInitialized;
    internal bool HasAnimatedFlare => active && RenderResourcesInitialized && flare?.HasAnimatedMaterials == true;

    public LensFlareComponentProxy(LevelEditorRenderContext context, ExportEntry export, ActorProxy actor)
        : base(context, export, actor)
    {
        renderContext = context;
        template = Properties.GetProp<ObjectProperty>("Template")?.ResolveToEntry(export.FileRef);
    }

    protected override void LoadFromProperties()
    {
        base.LoadFromProperties();
        active = (Properties.GetProp<BoolProperty>("bAutoActivate")?.Value ?? true)
                 && !(Properties.GetProp<BoolProperty>("HiddenGame")?.Value ?? false);
        StructProperty color = Properties.GetProp<StructProperty>("SourceColor");
        sourceColor = color is null ? Vector4.One : new Vector4(
            color.GetProp<FloatProperty>("R")?.Value ?? 1, color.GetProp<FloatProperty>("G")?.Value ?? 1,
            color.GetProp<FloatProperty>("B")?.Value ?? 1, color.GetProp<FloatProperty>("A")?.Value ?? 1);
    }

    public void PrepareRenderResources()
    {
        if (RenderResourcesInitialized) return;
        flare = renderContext.LensFlareRenderer.Prepare(template);
        renderResourcesInitialized = true;
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        if (IsVisible && active && RenderResourcesInitialized && pass == RenderPass.Hair
            && (Actor is not LensFlareSourceProxy source || source.IsFlareActive))
        {
            renderContext.LensFlareRenderer.Render(flare, LocalToWorld, sourceColor);
        }
    }

    public override BoxSphereBounds GetBounds()
    {
        // Reflections can extend well beyond the source; do not cull them as a point-sized marker.
        float radius = RenderResourcesInitialized ? MathF.Max(256, flare?.Definition.Radius ?? 0) : 256;
        return new BoxSphereBounds { Origin = LocalToWorld.Translation, BoxExtent = new Vector3(radius), SphereRadius = radius };
    }
}
