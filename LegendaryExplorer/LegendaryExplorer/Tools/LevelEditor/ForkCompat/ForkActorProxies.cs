using System.Linq;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Tools.LevelEditor;

// The fork's actor proxy types, as subclasses of upstream's proxies. Upstream draws and edits every light with LightActorProxy /
// StaticLightCollectionComponentProxy (an EditableLight), and every effect/gameplay marker with EditorActorProxy; these only give the fork's
// tools (static lighting baker, BioStage, camera presets, actor preview, navigation) the types they test for and construct. The factories in
// ActorProxy.Create/CreateCollection make them, so "actor is DirectionalLightActorProxy" is true for actors loaded by upstream's code too.

/// <summary>Picks the fork's subtype for upstream's light and helper proxies</summary>
internal static class ForkActorProxies
{
    public static LightActorProxy CreateLight(IActorEditorContext context, ExportEntry actorExport) =>
        (LightActorProxy.GetLightComponent(actorExport) is { } component ? SceneLight.GetLightType(component) : null) switch
        {
            SceneLightType.Spot => new SpotLightActorProxy(context, actorExport),
            SceneLightType.Point => new PointLightActorProxy(context, actorExport),
            SceneLightType.Directional => new DirectionalLightActorProxy(context, actorExport),
            SceneLightType.Sky => new SkyLightActorProxy(context, actorExport),
            _ => new LightActorProxy(context, actorExport)
        };

    public static StaticLightCollectionComponentProxy CreateCollectionLight(IActorEditorContext context, ExportEntry lightComponent,
        StaticLightCollectionActor collection, int index, SceneLightType type) => type switch
        {
            SceneLightType.Spot => new SpotLightComponentActorProxy(context, lightComponent, collection, index),
            SceneLightType.Point => new PointLightComponentActorProxy(context, lightComponent, collection, index),
            SceneLightType.Directional => new DirectionalLightComponentActorProxy(context, lightComponent, collection, index),
            _ => new StaticLightCollectionComponentProxy(context, lightComponent, collection, index, type)
        };

    /// <summary>
    /// A new standalone light's view (a draft: placed, or a copy of <paramref name="cloneSource"/>), of the fork's subtype its committed view will be, so
    /// that it's drawn, shown and treated (Show flags, the bake, the inspector) as that view is. Its light is <paramref name="componentTemplate"/>'s
    /// </summary>
    internal static LightActorProxy CreateDraftLight(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ExportEntry componentTemplate,
        ExportEntry cloneSource = null, ExportEntry cloneCollection = null) => SceneLight.GetLightType(componentTemplate) switch
        {
            SceneLightType.Spot => new SpotLightActorProxy(context, draft, properties, componentTemplate) { CloneSource = cloneSource, CloneCollection = cloneCollection },
            SceneLightType.Point => new PointLightActorProxy(context, draft, properties, componentTemplate) { CloneSource = cloneSource, CloneCollection = cloneCollection },
            SceneLightType.Directional => new DirectionalLightActorProxy(context, draft, properties, componentTemplate) { CloneSource = cloneSource, CloneCollection = cloneCollection },
            SceneLightType.Sky => new SkyLightActorProxy(context, draft, properties, componentTemplate) { CloneSource = cloneSource, CloneCollection = cloneCollection },
            _ => new LightActorProxy(context, draft, properties, componentTemplate) { CloneSource = cloneSource, CloneCollection = cloneCollection }
        };

    /// <summary>
    /// A draft copy of <paramref name="saved"/>, a helper actor (an effect or gameplay marker), of the fork's subtype its committed view will be
    /// (<see cref="CreateHelper"/>), drawing what that view draws: an Emitter's particle system and a LensFlareSource's flare, from <paramref name="saved"/>'s
    /// components' values, as its commit's copies of them have them
    /// </summary>
    internal static EditorActorProxy CreateDraftHelper(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ActorClassInfo info, ExportEntry saved)
    {
        if (saved.IsA("Emitter")) return new EmitterActorProxy(context, draft, properties, info, saved) { CloneSource = saved };
        if (saved.IsA("LensFlareSource")) return new LensFlareSourceProxy(context, draft, properties, info, saved) { CloneSource = saved };
        if (saved.IsA("SFXPointOfInterest")) return new SFXPointOfInterestProxy(context, draft, properties, info) { CloneSource = saved };
        return new EditorActorProxy(context, draft, properties, info) { CloneSource = saved };
    }

    public static EditorActorProxy CreateHelper(IActorEditorContext context, ExportEntry actorExport, ActorClassInfo info)
    {
        //IsA on the export also follows a class defined in the package (a custom subclass) to its super class
        if (actorExport.IsA("Emitter"))
            return new EmitterActorProxy(context, actorExport, info);
        if (actorExport.IsA("LensFlareSource"))
            return new LensFlareSourceProxy(context, actorExport, info);
        if (actorExport.IsA("SFXPointOfInterest"))
            return new SFXPointOfInterestProxy(context, actorExport, info);
        return new EditorActorProxy(context, actorExport, info);
    }
}

#region Standalone lights

/// <summary>Fork type: a PointLight actor (and its movable/toggleable variants). Upstream's <see cref="LightActorProxy"/></summary>
public class PointLightActorProxy : LightActorProxy
{
    public PointLightActorProxy(IActorEditorContext context, ExportEntry actorExport) : base(context, actorExport) { }

    /// <summary>A new one (a draft): see <see cref="ForkActorProxies.CreateDraftLight"/></summary>
    internal PointLightActorProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ExportEntry componentTemplate)
        : base(context, draft, properties, componentTemplate) { }
}

/// <summary>Fork type: a SpotLight actor. Upstream's <see cref="LightActorProxy"/></summary>
public class SpotLightActorProxy : PointLightActorProxy
{
    public SpotLightActorProxy(IActorEditorContext context, ExportEntry actorExport) : base(context, actorExport) { }

    /// <summary>A new one (a draft): see <see cref="ForkActorProxies.CreateDraftLight"/></summary>
    internal SpotLightActorProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ExportEntry componentTemplate)
        : base(context, draft, properties, componentTemplate) { }
}

/// <summary>Fork type: a DirectionalLight actor. Upstream's <see cref="LightActorProxy"/></summary>
public class DirectionalLightActorProxy : LightActorProxy
{
    public DirectionalLightActorProxy(IActorEditorContext context, ExportEntry actorExport) : base(context, actorExport) { }

    /// <summary>A new one (a draft): see <see cref="ForkActorProxies.CreateDraftLight"/></summary>
    internal DirectionalLightActorProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ExportEntry componentTemplate)
        : base(context, draft, properties, componentTemplate) { }
}

/// <summary>Fork type: a SkyLight actor. Upstream's <see cref="LightActorProxy"/></summary>
public class SkyLightActorProxy : LightActorProxy
{
    public SkyLightActorProxy(IActorEditorContext context, ExportEntry actorExport) : base(context, actorExport) { }

    /// <summary>A copy (a draft): see <see cref="ForkActorProxies.CreateDraftLight"/></summary>
    internal SkyLightActorProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ExportEntry componentTemplate)
        : base(context, draft, properties, componentTemplate) { }
}

#endregion

#region StaticLightCollectionActor lights

/// <summary>Fork type: a point light in a StaticLightCollectionActor. Upstream's <see cref="StaticLightCollectionComponentProxy"/></summary>
public class PointLightComponentActorProxy : StaticLightCollectionComponentProxy
{
    public PointLightComponentActorProxy(IActorEditorContext context, ExportEntry lightComponent, StaticLightCollectionActor collection, int index)
        : this(context, lightComponent, collection, index, SceneLightType.Point) { }

    protected PointLightComponentActorProxy(IActorEditorContext context, ExportEntry lightComponent, StaticLightCollectionActor collection, int index,
        SceneLightType type) : base(context, lightComponent, collection, index, type) { }
}

/// <summary>Fork type: a spot light in a StaticLightCollectionActor. Upstream's <see cref="StaticLightCollectionComponentProxy"/></summary>
public class SpotLightComponentActorProxy : PointLightComponentActorProxy
{
    public SpotLightComponentActorProxy(IActorEditorContext context, ExportEntry lightComponent, StaticLightCollectionActor collection, int index)
        : base(context, lightComponent, collection, index, SceneLightType.Spot) { }
}

/// <summary>Fork type: a directional light in a StaticLightCollectionActor. Upstream's <see cref="StaticLightCollectionComponentProxy"/></summary>
public class DirectionalLightComponentActorProxy : StaticLightCollectionComponentProxy
{
    public DirectionalLightComponentActorProxy(IActorEditorContext context, ExportEntry lightComponent, StaticLightCollectionActor collection, int index)
        : base(context, lightComponent, collection, index, SceneLightType.Directional) { }
}

#endregion

#region Effect and gameplay helpers

/// <summary>
/// Fork type: an Emitter actor. Its marker is upstream's "Effects helpers" marker (<see cref="EditorActorProxy"/>, drawn while
/// <see cref="LevelEditorRenderContext.ShowEmitterIcons"/> is set, or when selected); its particle system is its
/// <see cref="ParticleSystemComponentProxy"/>, simulated and drawn with the game's shaders while <see cref="LevelEditorRenderContext.ShowEmitterVfx"/> is set
/// </summary>
public class EmitterActorProxy : EditorActorProxy
{
    private readonly IActorEditorContext effectContext;

    /// <summary>Its ParticleSystemComponent (null if it has none, or isn't in a Level Editor view)</summary>
    public ParticleSystemComponentProxy ParticleSystemComponent { get; private set; }

    internal EmitterActorProxy(IActorEditorContext context, ExportEntry actorExport, ActorClassInfo info) : base(context, actorExport, info)
    {
        effectContext = context;
        AttachParticleSystemComponent();
    }

    /// <summary>
    /// A copy of <paramref name="saved"/> (a draft), until its commit clones it: its particle system plays as its source's does, from a stand-in with its
    /// source's ParticleSystemComponent's values and Template (as its commit's copy of the component has them)
    /// </summary>
    internal EmitterActorProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ActorClassInfo info, ExportEntry saved)
        : base(context, draft, properties, info)
    {
        effectContext = context;
        if (ParticleSystemComponentOf(saved) is { } component)
        {
            // As the committed view reads it (ParticleSystemComponentProxy): its Template in its own package
            var cache = context.PackageCache;
            IEntry template = EffectiveLightingProperties.ReadPrimitive(component, cache).GetProp<ObjectProperty>("Template")?.ResolveToEntry(component.FileRef);
            AttachDraftParticleSystem(template, EffectiveLightingProperties.ReadPrimitiveValues(component, cache));
        }
    }

    /// <summary>A new Emitter's view (a draft, <see cref="EmitterDraftProxy"/>): its particle system is attached by it</summary>
    private protected EmitterActorProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ActorClassInfo info)
        : base(context, draft, properties, info)
    {
        effectContext = context;
    }

    //its ParticleSystemComponent property's, or else a child ParticleSystemComponent, as the fork found it
    private static ExportEntry ParticleSystemComponentOf(ExportEntry actor) =>
        actor.GetCondensedProperties().GetProp<ObjectProperty>("ParticleSystemComponent")?.ResolveToEntry(actor.FileRef) is ExportEntry { } referenced
        && referenced.FileRef == actor.FileRef && referenced.IsA("ParticleSystemComponent")
            ? referenced
            : actor.GetChildren<ExportEntry>().FirstOrDefault(child => child.IsA("ParticleSystemComponent"));

    /// <summary>
    /// For a draft, until its commit makes its component: plays <paramref name="particleSystem"/> from a stand-in (an export that isn't in the package) with
    /// <paramref name="componentValues"/>, at the actor's transform, in place of the one it played. Nothing is added to the package
    /// </summary>
    private protected void AttachDraftParticleSystem(IEntry particleSystem, PropertyCollection componentValues)
    {
        if (ParticleSystemComponent is { } previous)
        {
            Components.Remove(previous);
            previous.Dispose();
            ParticleSystemComponent = null;
        }
        if (effectContext?.RenderContext is not LevelEditorRenderContext levelContext || particleSystem is null) return;
        try
        {
            //(None already exists in every package, so nothing is added to it: as a clone's stand-ins, ActorCloneProxy)
            var standIn = new ExportEntry(Pcc, null, "None", new byte[8]);
            var component = new ParticleSystemComponentProxy(levelContext, standIn, this, particleSystem, componentValues) { ActorPropertyName = "ParticleSystemComponent" };
            Components.Add(component);
            ParticleSystemComponent = component;
        }
        catch (System.Exception e) when (e is not System.OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"A new Emitter's particle system can't be previewed: {e.Message}");
        }
    }

    /// <summary>A draft's outliner line names its particle system, as its committed view's does</summary>
    protected override string ResolveDisplaySubtitle() => IsDraft ? ParticleSystemComponent?.ParticleSystem?.ObjectName.Instanced : base.ResolveDisplaySubtitle();

    /// <summary>
    /// Adds the ParticleSystemComponent's view to <see cref="ActorProxy.Components"/> (so it follows the actor's transform and is updated each frame).
    /// Its ParticleSystemComponent property, or else a child ParticleSystemComponent (as the fork found it)
    /// </summary>
    private void AttachParticleSystemComponent()
    {
        ParticleSystemComponent = null;
        if (effectContext?.RenderContext is not LevelEditorRenderContext levelContext || !HasSpatialRepresentation || IsDraft) return;
        ExportEntry componentExport = Properties?.GetProp<ObjectProperty>("ParticleSystemComponent")?.ResolveToEntry(Pcc) is ExportEntry { } referenced
            && referenced.FileRef == Pcc && referenced.IsA("ParticleSystemComponent")
                ? referenced
                : Export.GetChildren<ExportEntry>().FirstOrDefault(child => child.IsA("ParticleSystemComponent"));
        if (componentExport is null) return;
        try
        {
            var component = new ParticleSystemComponentProxy(levelContext, componentExport, this) { ActorPropertyName = "ParticleSystemComponent" };
            Components.Add(component);
            ParticleSystemComponent = component;
        }
        catch (System.Exception e) when (e is not System.OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Emitter {Export.InstancedFullPath}: its particle system can't be previewed: {e.Message}");
        }
    }

    public override ActorCoverage Coverage => base.Coverage is { Representation: ActorRepresentation.EditorMarker } coverage && ParticleSystemComponent is not null
        ? coverage with { Reason = "Particle system previewed with the game's shaders (Show emitter VFX); its simulation approximates the game's. Transform editing only." }
        : base.Coverage;

    public override void Render(LevelEditorRenderContext context, RenderPass pass)
    {
        if (context.ShowEmitterIcons || context.IsActorSelected(this))
        {
            base.Render(context, pass);
        }
        if (ParticleSystemComponent is { } component && context.IsComponentVisible(component))
        {
            component.Render(context, pass);
        }
    }

    public override bool TryRefreshPreview(IMEPackage package, System.Collections.Generic.HashSet<int> updatedExports, bool force = false)
    {
        ParticleSystemComponent?.OnPackageUpdated(package, updatedExports, force);
        bool refreshed = base.TryRefreshPreview(package, updatedExports, force);
        if (refreshed)
        {
            //re-reading the actor dropped its components
            AttachParticleSystemComponent();
        }
        return refreshed;
    }
}

/// <summary>
/// Fork type: a LensFlareSource actor. Its marker is upstream's "Effects helpers" marker (<see cref="EditorActorProxy"/>, drawn while
/// <see cref="LevelEditorRenderContext.ShowLensFlareIcons"/> is set, or when selected); its flare is its <see cref="LensFlareComponentProxy"/>,
/// drawn with the game's shaders
/// </summary>
public class LensFlareSourceProxy : EditorActorProxy
{
    private readonly IActorEditorContext effectContext;

    /// <summary>Its LensFlareComponent (null if it has none, or isn't in a Level Editor view)</summary>
    public LensFlareComponentProxy LensFlareComponent { get; private set; }

    internal LensFlareSourceProxy(IActorEditorContext context, ExportEntry actorExport, ActorClassInfo info) : base(context, actorExport, info)
    {
        effectContext = context;
        AttachLensFlareComponent();
    }

    /// <summary>
    /// A copy of <paramref name="saved"/> (a draft), until its commit clones it: its flare is drawn as its source's is, from a stand-in with its source's
    /// LensFlareComponent's values and Template (as its commit's copy of the component has them)
    /// </summary>
    internal LensFlareSourceProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ActorClassInfo info, ExportEntry saved)
        : base(context, draft, properties, info)
    {
        effectContext = context;
        if (effectContext.RenderContext is not LevelEditorRenderContext levelContext || LensFlareComponentOf(saved) is not { } source) return;
        try
        {
            // As the committed view reads it (LensFlareComponentProxy): its Template in its own package
            var cache = context.PackageCache;
            IEntry template = EffectiveLightingProperties.ReadPrimitive(source, cache).GetProp<ObjectProperty>("Template")?.ResolveToEntry(source.FileRef);
            //(None already exists in every package, so nothing is added to it: as a clone's stand-ins, ActorCloneProxy)
            var standIn = new ExportEntry(Pcc, null, "None", new byte[8]);
            var component = new LensFlareComponentProxy(levelContext, standIn, this, template, EffectiveLightingProperties.ReadPrimitiveValues(source, cache))
            {
                ActorPropertyName = "LensFlareComp"
            };
            Components.Add(component);
            LensFlareComponent = component;
        }
        catch (System.Exception e) when (e is not System.OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"A copy of LensFlareSource {saved.InstancedFullPath}: its flare can't be previewed: {e.Message}");
        }
    }

    //its LensFlareComp, or else a child LensFlareComponent, as LensFlareComponentExport finds it
    private static ExportEntry LensFlareComponentOf(ExportEntry actor) =>
        actor.GetCondensedProperties().GetProp<ObjectProperty>("LensFlareComp")?.ResolveToEntry(actor.FileRef) is ExportEntry { } component && component.IsA("LensFlareComponent")
            ? component
            : actor.GetChildren<ExportEntry>().FirstOrDefault(child => child.IsA("LensFlareComponent"));

    /// <summary>A draft's outliner line names its flare, as its committed view's does</summary>
    protected override string ResolveDisplaySubtitle() => IsDraft ? LensFlareComponent?.Template?.ObjectName.Instanced : base.ResolveDisplaySubtitle();

    /// <summary>bCurrentlyActive (true if unset)</summary>
    internal bool IsFlareActive => Properties?.GetProp<BoolProperty>("bCurrentlyActive")?.Value ?? true;

    /// <summary>The LensFlareComponent export (its LensFlareComp, or a child component), or null. A draft has none until its commit</summary>
    internal ExportEntry LensFlareComponentExport => IsDraft ? null :
        Properties?.GetProp<ObjectProperty>("LensFlareComp")?.ResolveToEntry(Pcc) is ExportEntry { } component && component.IsA("LensFlareComponent")
            ? component
            : Export.GetChildren<ExportEntry>().FirstOrDefault(child => child.IsA("LensFlareComponent"));

    private void AttachLensFlareComponent()
    {
        LensFlareComponent = null;
        if (effectContext?.RenderContext is not LevelEditorRenderContext levelContext || !HasSpatialRepresentation
            || LensFlareComponentExport is not { } componentExport || componentExport.FileRef != Pcc) return;
        try
        {
            var component = new LensFlareComponentProxy(levelContext, componentExport, this) { ActorPropertyName = "LensFlareComp" };
            Components.Add(component);
            LensFlareComponent = component;
        }
        catch (System.Exception e) when (e is not System.OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"LensFlareSource {Export.InstancedFullPath}: its flare can't be previewed: {e.Message}");
        }
    }

    public override ActorCoverage Coverage => base.Coverage is { Representation: ActorRepresentation.EditorMarker } coverage && LensFlareComponent is not null
        ? coverage with { Reason = "Lens flare previewed with the game's shaders (occlusion is not simulated). Transform editing only." }
        : base.Coverage;

    public override void Render(LevelEditorRenderContext context, RenderPass pass)
    {
        if (context.ShowLensFlareIcons || context.IsActorSelected(this))
        {
            base.Render(context, pass);
        }
        if (LensFlareComponent is { } component && context.IsComponentVisible(component))
        {
            component.Render(context, pass);
        }
    }

    /// <summary>
    /// The actor, its flare component, every export nested under the actor (the component's children, sprites...) and the flare template
    /// when it's in the same file, each once: what the property inspector offers
    /// </summary>
    public override System.Collections.Generic.IEnumerable<ExportEntry> GetPropertyExports()
    {
        //(a draft has no exports until its commit)
        if (IsDraft) return [];
        System.Collections.Generic.IEnumerable<ExportEntry> exports = new[] { Export, LensFlareComponentExport }
            .Concat(base.GetPropertyExports())
            .Concat(Export.GetAllDescendants().OfType<ExportEntry>());
        if (LensFlareComponentExport?.GetProperty<ObjectProperty>("Template")?.ResolveToEntry(Pcc) is ExportEntry template && template.FileRef == Pcc)
        {
            exports = exports.Append(template).Concat(template.GetAllDescendants().OfType<ExportEntry>());
        }
        return exports.Where(export => export is not null).Distinct();
    }

    /// <summary>An edit of any export the inspector offers for it refreshes the actor</summary>
    public override bool TestUIndexes(System.Collections.Generic.HashSet<int> uIndexes) =>
        base.TestUIndexes(uIndexes) || GetPropertyExports().Any(export => uIndexes.Contains(export.UIndex));

    public override bool TryRefreshPreview(IMEPackage package, System.Collections.Generic.HashSet<int> updatedExports, bool force = false)
    {
        LensFlareComponent?.OnPackageUpdated(package, updatedExports, force);
        bool refreshed = base.TryRefreshPreview(package, updatedExports, force);
        if (refreshed)
        {
            //re-reading the actor dropped its components
            AttachLensFlareComponent();
        }
        return refreshed;
    }
}

/// <summary>Fork type: an SFXPointOfInterest actor (Game 3). Upstream's gameplay marker (<see cref="EditorActorProxy"/>)</summary>
public class SFXPointOfInterestProxy : EditorActorProxy
{
    internal SFXPointOfInterestProxy(IActorEditorContext context, ExportEntry actorExport, ActorClassInfo info) : base(context, actorExport, info) { }

    /// <summary>A copy of one (a draft): see <see cref="ForkActorProxies.CreateDraftHelper"/></summary>
    internal SFXPointOfInterestProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ActorClassInfo info)
        : base(context, draft, properties, info) { }

    /// <summary>The actor, its SFXSimpleUseModule modules and its components, for the property inspector</summary>
    public override System.Collections.Generic.IEnumerable<ExportEntry> GetPropertyExports()
    {
        //(a draft has no exports until its commit)
        if (IsDraft) yield break;
        yield return Export;
        if (Export.GetProperty<ArrayProperty<ObjectProperty>>("Modules") is { } modules)
        {
            foreach (ObjectProperty moduleReference in modules)
            {
                if (Pcc.TryGetUExport(moduleReference.Value, out ExportEntry module) && module.ClassName == "SFXSimpleUseModule")
                {
                    yield return module;
                }
            }
        }
        foreach (ExportEntry component in base.GetPropertyExports().Where(export => export != Export))
        {
            yield return component;
        }
    }
}

#endregion

/// <summary>
/// Fork type (actor preview): a SkeletalMeshComponent previewed on its own, as if it were an actor
/// </summary>
public sealed class SkeletalMeshComponentPreviewActorProxy : ActorProxy
{
    public SkeletalMeshComponentProxy SkeletalMeshComponent { get; }

    public SkeletalMeshComponentPreviewActorProxy(IActorEditorContext context, ExportEntry componentExport) : base(context, componentExport)
    {
        SkeletalMeshComponent = PrimitiveComponentProxy.Create(context.RenderContext, componentExport, this) as SkeletalMeshComponentProxy;
        if (SkeletalMeshComponent is not null)
        {
            Components.Add(SkeletalMeshComponent);
            ApplyMorphFace(SkeletalMeshComponent);
        }
    }

    public override void SetAnimation(AnimSequence animSequence, float pos) => SkeletalMeshComponent?.SetAnimation(animSequence, pos);
}
