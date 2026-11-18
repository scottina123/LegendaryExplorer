using System;
using System.Numerics;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// A LensFlareSource's LensFlareComponent: its flare, drawn with the game's shaders (see <see cref="LevelLensFlareRenderer"/>). Flares are
/// translucent: in <see cref="RenderPass.Base"/> a visible, unculled flare queues itself on the context's sorted translucent queue
/// (<see cref="LevelEditorRenderContext.QueueTranslucentEffect"/>), which <see cref="RenderPass.Translucent"/> draws. Its template is prepared
/// the first time it's drawn
/// </summary>
public sealed class LensFlareComponentProxy : PrimitiveComponentProxy, ILevelRenderResource, ITranslucentEffect
{
    private readonly LevelEditorRenderContext renderContext;
    private readonly IEntry template;
    private LevelLensFlareRenderer.PreparedFlare flare;
    private bool renderResourcesInitialized;
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

    /// <summary>
    /// For a copy of a LensFlareSource (a draft), until its commit clones its component: a stand-in (an export that isn't in the package) drawing
    /// <paramref name="template"/> with <paramref name="componentValues"/>, its source's component's values, as the copy's component will have them
    /// </summary>
    internal LensFlareComponentProxy(LevelEditorRenderContext context, ExportEntry standIn, ActorProxy actor, IEntry template, PropertyCollection componentValues)
        : base(context, standIn, actor, componentValues)
    {
        renderContext = context;
        this.template = template;
    }

    /// <summary>The LensFlare it draws (its Template)</summary>
    internal IEntry Template => template;

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
        try
        {
            flare = renderContext.LensFlareRenderer.Prepare(template);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Lens flare {template?.InstancedFullPath}: {e.Message}");
            flare = null;
        }
        renderResourcesInitialized = true;
    }

    private bool IsShown => IsVisible && active && renderContext.CanDrawEffects && template is not null
        && (Actor is not LensFlareSourceProxy source || source.IsFlareActive);

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        switch (pass)
        {
            case RenderPass.Base:
                if (!IsShown) return;
                BoxSphereBounds bounds = GetBounds();
                if (context.CullingFrustum is { } frustum && !frustum.Intersects(bounds)) return;
                if (flare is { Disposed: true }) ForgetPreparedFlare();
                PrepareRenderResources();
                if (flare is null || flare.Materials.Count == 0 || flare.Disposed) return;
                renderContext.QueueTranslucentEffect(this, LocalToWorld.Translation);
                break;
            case RenderPass.Translucent:
                //the first effect drawn in the translucent pass draws every queued one, sorted
                renderContext.EndTranslucentPass();
                break;
        }
    }

    void ITranslucentEffect.DrawTranslucent(LevelEditorRenderContext context)
    {
        if (flare is { Disposed: false })
        {
            context.LensFlareRenderer.Render(flare, LocalToWorld, sourceColor);
        }
    }

    /// <summary>The flare's template was released (its package edited, or the level's effects cleared): prepared again when next drawn</summary>
    internal void ForgetPreparedFlare()
    {
        flare = null;
        renderResourcesInitialized = false;
    }

    /// <summary>
    /// A package was edited: if it changed the flare's template (or its elements), the flare is prepared again from the new data when next drawn.
    /// <paramref name="force"/> (the context released its preview resources) releases every prepared flare
    /// </summary>
    internal void OnPackageUpdated(IMEPackage package, System.Collections.Generic.HashSet<int> updatedExports, bool force)
    {
        if (force)
        {
            renderContext.LensFlareRenderer.Clear();
            ForgetPreparedFlare();
            return;
        }
        if (renderContext.ResolveExportCached(template) is { } templateExport && templateExport.FileRef == package
            && (updatedExports.Contains(templateExport.UIndex)
                || System.Linq.Enumerable.Any(templateExport.GetAllDescendants(), entry => updatedExports.Contains(entry.UIndex))))
        {
            ForgetPreparedFlare();
        }
    }

    public override BoxSphereBounds GetBounds()
    {
        // Reflections can extend well beyond the source; do not cull them as a point-sized marker.
        float radius = RenderResourcesInitialized ? MathF.Max(256, flare?.Definition.Radius ?? 0) : 256;
        return new BoxSphereBounds { Origin = LocalToWorld.Translation, BoxExtent = new Vector3(radius), SphereRadius = radius };
    }

    protected override void Dispose(bool disposing)
    {
        flare = null;
        base.Dispose(disposing);
    }
}
