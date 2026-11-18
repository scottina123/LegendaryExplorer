using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MediaColor = System.Windows.Media.Color;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork actor API on upstream's <see cref="ActorProxy"/>. The fork's virtual light API maps onto upstream's <see cref="ILightProxy"/>/<see cref="EditableLight"/>:
/// any actor that's an <see cref="ILightProxy"/> (standalone and collection lights) answers with its light's settings, and setting them edits it (with
/// upstream's dirty tracking and preview refresh)
/// </summary>
public partial class ActorProxy
{
    private EditableLight ForkLight => (this as ILightProxy)?.Light;

    public virtual bool HasLightSettings => ForkLight is not null;
    public virtual bool HasLightRadius => ForkLight?.HasRadius == true;
    public virtual bool HasConeAngles => ForkLight?.IsSpot == true;

    public virtual float LightRadius
    {
        get => ForkLight?.Radius ?? 0f;
        set { if (ForkLight is { IsReadOnly: false } light) light.Radius = value; }
    }

    public virtual float Brightness
    {
        get => ForkLight?.Brightness ?? 0f;
        set { if (ForkLight is { IsReadOnly: false } light) light.Brightness = value; }
    }

    public virtual float InnerConeAngle
    {
        get => ForkLight?.InnerConeAngle ?? 0f;
        set { if (ForkLight is { IsReadOnly: false } light) light.InnerConeAngle = value; }
    }

    public virtual float OuterConeAngle
    {
        get => ForkLight?.OuterConeAngle ?? 0f;
        set { if (ForkLight is { IsReadOnly: false } light) light.OuterConeAngle = value; }
    }

    public virtual MediaColor LightColor
    {
        get => ForkLight?.Color ?? default;
        set { if (ForkLight is { IsReadOnly: false } light) light.Color = value; }
    }

    /// <summary>
    /// Fork: the light this actor adds to the scene, with its uncommitted edits. Found in the render context's level lights (upstream rebuilds them on
    /// every edit, see <see cref="IActorEditorContext.OnLightEdited"/>), or read from the component if the scene hasn't got it
    /// </summary>
    public virtual bool TryGetSceneLight(out SceneLight light)
    {
        light = null;
        if (this is not ILightProxy lightProxy || lightProxy.Light is not { } editable) return false;
        if (Editor?.RenderContext is { } context)
        {
            foreach (SceneLight candidate in context.GetLights(out _))
            {
                if (candidate.Export == editable.Component)
                {
                    light = candidate;
                    return true;
                }
            }
        }
        if (Editor is null || Pcc.GetEntry(editable.Component.UIndex) != editable.Component) return false;
        string ownerClass = this is CollectionActorComponentProxy collection ? collection.CollectionActorExport.ClassName : Export.ClassName;
        LightPreviewOverride preview = editable.GetPreviewOverride(lightProxy.EditedOwnerTransform);
        light = SceneLight.Create(editable.Component, ownerClass, Editor.PackageCache, preview?.OwnerTransform ?? LocalToWorld,
            preview?.Properties is { Count: > 0 } changed ? changed : null);
        return light is not null;
    }

    /// <summary>
    /// Fork (actor previews): morphs the actor's morphable head (the component <see cref="ApplyMorphFace"/> morphs, else its first skeletal mesh)
    /// with <paramref name="morphHead"/>, without writing it to the actor. Its hair, if it has one, gets the morph's final skeleton too
    /// (bone positions only), as the fork's previews did
    /// </summary>
    public virtual bool ApplyMorphHead(ExportEntry morphHead)
    {
        SkeletalMeshComponentProxy head = MorphedComponent ?? Components.OfType<SkeletalMeshComponentProxy>().FirstOrDefault();
        if (head is null || morphHead is null) return false;
        (LegendaryExplorerCore.Unreal.Classes.BonePosition[] bonePositions, Vector3[][] vertexOffsets) =
            LegendaryExplorerCore.Unreal.Classes.BioMorphFace.GetBoneAndVertexPositions(morphHead);
        head.ApplyMorph(bonePositions, vertexOffsets);
        if (MorphedHairComponent is { } hair && hair != head) hair.ApplyMorph(bonePositions, null);
        return true;
    }

    /// <summary>Fork: the actor and component exports the property inspector offers</summary>
    public virtual IEnumerable<ExportEntry> GetPropertyExports() =>
        new[] { Export }.Concat(Components.Select(component => component.Export)).Where(export => export is not null).Distinct();

    /// <summary>
    /// Fork: the actor's saved properties with its uncommitted transform written in, for the interpreter view and volume geometry edits
    /// </summary>
    public virtual PropertyCollection GetPropertiesForInterpreter()
    {
        var props = (Export?.GetProperties(includeNoneProperties: true) ?? Properties).DeepClone();

        // Only actors keep their transform in properties. A collection member's export is its component (its transform is in the collection's
        // binary), and a component has no Location/Rotation/DrawScale properties, so writing them there is wrong
        if (this is CollectionActorComponentProxy || Export is null || !Export.IsA("Actor")) return props;

        // Added properties must go before the terminating None: anything after it is read as the start of the export's binary, which corrupts it
        // when the interpreter writes these properties back
        var terminators = props.OfType<NoneProperty>().ToList();
        foreach (NoneProperty none in terminators) props.Remove(none);
        try
        {
            AddTransformProperties(props);
        }
        finally
        {
            foreach (NoneProperty none in terminators) props.Add(none);
        }
        return props;
    }

    private void AddTransformProperties(PropertyCollection props)
    {
        string locationPropName = LocationPropertyName(Pcc.Game);
        if (props.ContainsNamedProp(locationPropName) || Location != Vector3.Zero)
            props.AddOrReplaceProp(CommonStructs.Vector3Prop(Location, locationPropName));
        if (props.ContainsNamedProp("DrawScale") || DrawScale != 1f)
            props.AddOrReplaceProp(new FloatProperty(DrawScale, "DrawScale"));
        if (props.ContainsNamedProp("DrawScale3D") || DrawScale3D != Vector3.One)
            props.AddOrReplaceProp(CommonStructs.Vector3Prop(DrawScale3D, "DrawScale3D"));
        if (props.ContainsNamedProp("Rotation") || !Rotation.IsZero)
            props.AddOrReplaceProp(CommonStructs.RotatorProp(Rotation, "Rotation"));
        if (props.ContainsNamedProp("PrePivot") || PrePivot != Vector3.Zero)
            props.AddOrReplaceProp(CommonStructs.Vector3Prop(PrePivot, "PrePivot"));
    }
}

public partial class ActorProxy
{
    /// <summary>
    /// Fork: uncommitted changes the proxy tracks beyond its transform (the galaxy map's objects, the fork's light settings). Counted as uncommitted
    /// by <see cref="HasUncommittedChanges"/>; cleared by the subclass when it commits or reloads
    /// </summary>
    protected bool hasAuxiliaryChanges;

    /// <summary>Fork: records an auxiliary change and updates <see cref="IsDirty"/></summary>
    protected void MarkAuxiliaryChanged()
    {
        hasAuxiliaryChanges = true;
        UpdateDirty();
    }
}
