using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// A copy of an actor, until it's committed: drawn by stand-ins of its source's static mesh, skeletal mesh and brush components (exports that aren't in the
/// package, as a new actor's are), each with its source's values and placed as it is on its source. Its meshes' lighting is edited on its stand-ins, and
/// written by its commit onto its copies of its source's components; once committed, it's drawn as its class is, from its own export. In its source's
/// level it's drawn with its source's baked lighting, which its commit copies; in another, with none, as its commit drops it. Fork: it draws what its
/// source's view draws, as that view draws it (so as its commit's copy's view will), lit with its source's light environment's settings by a light
/// environment of its own (as its copy is by its copy of it), its head morphed and its skeletal meshes posed as its source's are
/// </summary>
internal sealed class ActorCloneProxy : ActorProxy
{
    private readonly string category;
    private readonly bool isVolume;
    private readonly int hitPriority;
    //the source's component each stand-in is drawn from
    private readonly Dictionary<PrimitiveComponentProxy, ExportEntry> drawnFrom = [];

    public override string Category => category;
    public override bool IsVolume => isVolume;
    public override int HitPriority => hitPriority;

    /// <summary>Fork: the saved actor it's a copy of (or the collection's member), whose configured animation it's posed with until its commit</summary>
    internal ExportEntry SavedSource { get; }

    /// <param name="source">What it's a copy of: <paramref name="saved"/>'s view, or a copy of it</param>
    /// <param name="saved">The saved actor it's a copy of, or the collection's member</param>
    /// <param name="isMember">Whether <paramref name="saved"/> is a collection's member, which is its component alone</param>
    internal ActorCloneProxy(IActorEditorContext context, ActorRecord draft, PropertyCollection properties, ActorProxy source, ExportEntry saved, bool isMember)
        : base(context, draft, properties)
    {
        SavedSource = saved;
        category = source.Category;
        isVolume = source.IsVolume;
        hitPriority = source.HitPriority;
        var cache = context.PackageCache;
        var drawn = new Dictionary<ExportEntry, PrimitiveComponentProxy>();
        // Fork: what its source's view draws, by the saved component each is drawn from (a copy's source's stand-ins, by theirs). It's drawn as the view of
        // its class draws it, which its commit's copy is drawn by: those components alone (a SkeletalMeshActor's view draws its SkeletalMeshComponent, not
        // every mesh its Components list), hidden and placed as that view has them (a BioArtPlaceable's destroyed mesh is hidden while it has its placeable
        // one; a stunt actor's body is lowered to its class default's Translation)
        var shownBy = new Dictionary<ExportEntry, PrimitiveComponentProxy>();
        foreach (var component in source.Components)
        {
            if ((component.CopiedFrom ?? component.Export) is { } shown) shownBy.TryAdd(shown, component);
        }
        //(the stand-ins drawn so far are disposed with it if it can't be drawn)
        try
        {
            // A member is drawn from its component, as its collection's view draws it
            if (isMember) Draw(saved, null);
            else
            {
                // The components under it that its own properties refer to (not those only its archetype's do), each named by a property that refers to it:
                // one of its own (StaticMeshComponent) before an entry in an array (Components[1]), which is serialized first
                var references = saved.GetProperties().SelectMany(p => p switch
                {
                    ObjectProperty o => [(Name: p.Name.Instanced, o.Value, InArray: false)],
                    ArrayProperty<ObjectProperty> a => a.Select((o, i) => (Name: $"{p.Name.Instanced}[{i}]", o.Value, InArray: true)),
                    _ => []
                }).OrderBy(r => r.InArray);
                foreach (var (name, component, _) in references)
                {
                    if (saved.FileRef.TryGetUExport(component, out var export) && export.Parent == saved && shownBy.ContainsKey(export)) Draw(export, name);
                }
            }
            IsVolumetricMesh = Components.OfType<StaticMeshComponentProxy>().Any(mesh => mesh.IsVolumetric);
            // A character's head is morphed (and fork: its hair follows it) as its source's view morphs them: BioPawnProxy, SFXSkeletalMeshActorProxy,
            // SFXStuntActorProxy. The morph is its saved actor's MorphHead, which its commit's copy has (in another level, as its copy of it)
            if (source.MorphedHead is { } sourceHead && drawn.GetValueOrDefault(sourceHead.CopiedFrom ?? sourceHead.Export) is SkeletalMeshComponentProxy headMesh)
            {
                var hairMesh = source.MorphedHair is { } sourceHair ? drawn.GetValueOrDefault(sourceHair.CopiedFrom ?? sourceHair.Export) as SkeletalMeshComponentProxy : null;
                ApplyMorphFace(headMesh, hairMesh);
            }
            //a draft is wholly uncommitted
            UpdateDirty();
        }
        catch
        {
            Dispose();
            throw;
        }

        PrimitiveComponentProxy Draw(ExportEntry component, string propertyName)
        {
            if (drawn.TryGetValue(component, out var done))
            {
                //drawn first as another's animation parent
                if (done is not null && propertyName is not null) done.ActorPropertyName ??= propertyName;
                return done;
            }
            //(and so, a component that's its own animation parent isn't drawn twice)
            drawn[component] = null;
            var game = component.Game;
            bool Is(string baseClass) => GlobalUnrealObjectInfo.IsA(component.ClassName, baseClass, game);
            // Only meshes and brushes are drawn. A spline mesh is a spline point's connection, which isn't cloned this way
            bool isStaticMesh = Is("StaticMeshComponent") && !Is("SplineMeshComponent"), isSkeletalMesh = Is("SkeletalMeshComponent");
            if (!isStaticMesh && !isSkeletalMesh && component.ClassName != "BrushComponent") return null;
            // Its values are its own (references stripped, as they'd refer to its source's objects); its mesh and materials are its source's
            var values = EffectiveLightingProperties.ReadValues(component, cache);
            //fork: placed as its source's view has it (its class's view may move it from where its values put it)
            var shownAs = shownBy.GetValueOrDefault(component);
            if (shownAs is not null) MatchTransform(values, shownAs);
            PreviewAssets Assets(string meshProperty)
            {
                var (mesh, meshPackage) = EffectiveLightingProperties.Reference(component, meshProperty, cache);
                var (materials, materialPackage) = EffectiveLightingProperties.Inherited<ArrayProperty<ObjectProperty>>(component, "Materials", cache);
                return new PreviewAssets(AssetReference.Of(mesh?.ResolveToEntry(meshPackage), Pcc, cache),
                    [.. (materials ?? Enumerable.Empty<ObjectProperty>()).Select(m => AssetReference.Of(m.ResolveToEntry(materialPackage), Pcc, cache))]);
            }
            // None already exists in every package. Pending properties bypass serialization, so even the package's name table stays untouched
            ExportEntry StandIn(ObjectBinary binary = null) => new(Pcc, null, "None", new byte[8], binary: binary);
            PrimitiveComponentProxy proxy;
            if (isStaticMesh)
            {
                // In its source's level it keeps its source's baked lighting, as its commit does; another level's lights didn't make it (ForAnotherLevel).
                // Fork: lit with its source's light environment's settings, by one of its own (in another level too, as its copy has its own copy of it)
                proxy = new StaticMeshComponentProxy(context.RenderContext, StandIn(StaticMeshComponent.Create()), this, values,
                    bakedLighting: component.FileRef == Pcc ? component : null, previewAssets: Assets("StaticMesh"), previewLightingSource: component);
            }
            else if (isSkeletalMesh)
            {
                // Drawn after the component it's transformed from, which is drawn first if it hasn't been (fork: if its source's view draws it, as the
                // copy's view will, which takes only a drawn component's transform)
                var parent = EffectiveLightingProperties.Reference(component, "ParentAnimComponent", cache);
                var animParent = parent.Property?.ResolveToEntry(parent.Package) is ExportEntry parentExport && parentExport.Parent == saved
                    && (isMember || shownBy.ContainsKey(parentExport)) ? Draw(parentExport, null) : null;
                proxy = new SkeletalMeshComponentProxy(context.RenderContext, StandIn(), this, values, Assets("SkeletalMesh"), animParent,
                    previewLightingSource: component);
            }
            else proxy = new BrushComponentProxy(context.RenderContext, StandIn(BrushComponent.Create()), this, values);
            //(as its source's view names it, which its copy's view will: a member's copy has no index in its collection yet, so none)
            proxy.ActorPropertyName = isMember ? propertyName : shownAs?.ActorPropertyName ?? propertyName;
            //fork: hidden as its source's view hides it
            if (shownAs is not null) proxy.IsVisible = shownAs.IsVisible;
            drawn[component] = proxy;
            drawnFrom[proxy] = component;
            Components.Add(proxy);
            return proxy;
        }
    }

    //fork: puts in a stand-in's values the transform its source's view gives the component it's drawn from, where they'd give it another: a view of its
    //class may set one its values don't have (SFXStuntActorProxy's body Translation, from its class default's), which its commit's copy's view sets too
    private static void MatchTransform(PropertyCollection values, PrimitiveComponentProxy shown)
    {
        var translation = values.GetProp<StructProperty>("Translation") is { } t ? CommonStructs.GetVector3(t) : Vector3.Zero;
        if (translation != shown.Translation) values.AddOrReplaceProp(CommonStructs.Vector3Prop(shown.Translation, "Translation"));
        var rotation = values.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : new Rotator(0, 0, 0);
        if (!rotation.Equals(shown.Rotation)) values.AddOrReplaceProp(CommonStructs.RotatorProp(shown.Rotation, "Rotation"));
        var scale3D = values.GetProp<StructProperty>("Scale3D") is { } s ? CommonStructs.GetVector3(s) : Vector3.One;
        if (scale3D != shown.Scale3D) values.AddOrReplaceProp(CommonStructs.Vector3Prop(shown.Scale3D, "Scale3D"));
        if ((values.GetProp<FloatProperty>("Scale")?.Value ?? 1) != shown.Scale) values.AddOrReplaceProp(new FloatProperty(shown.Scale, "Scale"));
    }

    /// <summary>
    /// Fork: its head's morph is its saved actor's MorphHead, as its source's view and its commit's copy's read it. Its own values are its saved actor's, or
    /// in another level, without their references, which would be to another package's objects
    /// </summary>
    private protected override ExportEntry ResolveMorphHead() =>
        SavedSource.GetCondensedProperties().GetProp<ObjectProperty>("MorphHead")?.ResolveToExport(SavedSource.FileRef, Editor?.PackageCache);

    /// <summary>
    /// Fork: posed as its source's class poses it (body, head, hair and headgear: SkeletalMeshActorProxy, SFXStuntActorProxy, BioPawnProxy): every skeletal mesh
    /// stand-in, which are its source's skeletal meshes
    /// </summary>
    public override void SetAnimation(AnimSequence animSequence, float pos)
    {
        foreach (SkeletalMeshComponentProxy mesh in Components.OfType<SkeletalMeshComponentProxy>())
        {
            mesh.SetAnimation(animSequence, pos);
        }
    }

    /// <summary>The saved actor's component <paramref name="drawn"/>, one of its stand-ins, is drawn from</summary>
    internal ExportEntry DrawnFrom(PrimitiveComponentProxy drawn) => drawnFrom.GetValueOrDefault(drawn);

    /// <summary>
    /// For its commit, once its export is made (<see cref="ActorProxy.Materialize"/>): its copy of the component <paramref name="drawn"/>, one of its
    /// stand-ins, is drawn from. Null before, and once the commit is abandoned
    /// </summary>
    internal ExportEntry CommittedComponent(PrimitiveComponentProxy drawn) =>
        Materialized && DrawnFrom(drawn) is { } component ? CloneCopies.GetValueOrDefault(component) : null;

    /// <summary>Whether one of its stand-ins is drawn from <paramref name="component"/>, one of its saved actor's</summary>
    internal bool Draws(ExportEntry component) => drawnFrom.ContainsValue(component);

    internal override MeshComponentProxy MeshDrawing(ExportEntry component) =>
        drawnFrom.FirstOrDefault(p => p.Value == component).Key as MeshComponentProxy;

    /// <summary>
    /// Writes its meshes' lighting edits, chosen meshes and chosen materials onto its copies of the components they're drawn from. A volume whose scale isn't the one it was copied (or last
    /// committed) with has its collision cooked again, if it has cooked collision, as a rescaled volume's commit does (<see cref="BrushProxy.CommitChanges"/>)
    /// </summary>
    private protected override void CommitCloneEdits(IReadOnlyDictionary<ExportEntry, ExportEntry> copies)
    {
        ExportEntry CopyOf(ExportEntry component) => copies.GetValueOrDefault(component)
            ?? throw new InvalidOperationException($"{ActorName} has no copy of {component.ObjectName.Instanced}.");
        var copiedIndexes = copies.ToDictionary(p => p.Key.UIndex, p => p.Value.UIndex);
        foreach (var (proxy, component) in drawnFrom)
        {
            if (proxy is MeshComponentProxy { ExistingLighting: { HasChanges: true } lighting }) lighting.Write(CopyOf(component), copiedIndexes);
            if (proxy is StaticMeshComponentProxy { ChosenMesh: { } mesh }) StaticMeshChange.Write(CopyOf(component), mesh);
            if (proxy is StaticMeshComponentProxy { ChosenMaterials: { } materials }) StaticMeshChange.WriteMaterials(CopyOf(component), materials);
        }
        if (!IsScaleDirty) return;
        foreach (var (proxy, component) in drawnFrom)
        {
            if (proxy is BrushComponentProxy && CopyOf(component) is var brush && BrushProxy.HasCookedCollision(brush))
                BrushProxy.CookCollision(brush, Editor.RenderContext.PackageCache);
        }
    }
}
