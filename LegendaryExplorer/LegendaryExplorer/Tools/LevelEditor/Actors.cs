using DocumentFormat.OpenXml.Drawing;
using LegendaryExplorer.Misc;
using LegendaryExplorer.UserControls.SharedToolControls.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

public class ActorProxy : NotifyPropertyChangedBase, IDisposable
{
    public Matrix4x4 LocalToWorld;

    public List<PrimitiveComponentProxy> Components = [];

    public PropertyCollection Properties;

    public ExportEntry Export { get; }

    protected Rotator rotation;
    protected Vector3 location;
    protected Vector3 drawScale3D;
    protected float drawScale;
    protected Vector3 prePivot;
    public Rotator Rotation
    {
        get => rotation;
        set { if (SetProperty(ref rotation, value)) UpdateLocalToWorld(); }
    }
    public Vector3 Location
    {
        get => location;
        set { if (SetProperty(ref location, value)) UpdateLocalToWorld(); }
    }
    public Vector3 DrawScale3D
    {
        get => drawScale3D;
        set { if (SetProperty(ref drawScale3D, value)) UpdateLocalToWorld(); }
    }
    public float DrawScale
    {
        get => drawScale;
        set { if (SetProperty(ref drawScale, value)) UpdateLocalToWorld(); }
    }
    public Vector3 PrePivot
    {
        get => prePivot;
        set { if (SetProperty(ref prePivot, value)) UpdateLocalToWorld(); }
    }

    protected ActorProxy(MeshRenderContext context, ExportEntry actorExport)
    {
        Export = actorExport;
        Properties = actorExport.GetCondensedProperties();
        PropertyCollection props = Properties;

        var rotationProp = props.GetProp<StructProperty>("Rotation");
        var locationsProp = props.GetProp<StructProperty>("location");
        var drawScale3DProp = props.GetProp<StructProperty>("DrawScale3D");
        var prePivotProp = props.GetProp<StructProperty>("PrePivot");

        drawScale = props.GetProp<FloatProperty>("DrawScale")?.Value ?? 1;
        location = locationsProp != null ? CommonStructs.GetVector3(locationsProp) : Vector3.Zero;
        drawScale3D = drawScale3DProp != null ? CommonStructs.GetVector3(drawScale3DProp) : Vector3.One;
        prePivot = prePivotProp != null ? CommonStructs.GetVector3(prePivotProp) : Vector3.Zero;
        rotation = rotationProp != null ? CommonStructs.GetRotator(rotationProp) : new Rotator(0, 0, 0);
        UpdateLocalToWorld();
    }

    //only for use by the faux actors that are children of the CollectionActors
    protected ActorProxy(ExportEntry actorExport)
    {
        Export = actorExport;
        drawScale = 1;
        location =  Vector3.Zero;
        drawScale3D =  Vector3.One;
        prePivot =  Vector3.Zero;
        rotation = new Rotator(0, 0, 0);
    }

    protected virtual void UpdateLocalToWorld()
    {
        //LocalToWorld = Matrix4x4.CreateScale(drawScale * drawScale3D) * Matrix4x4.CreateTranslation(location) * Matrix4x4.CreateFromYawPitchRoll(rotation.Yaw, rotation.Pitch, rotation.Roll);
        //LocalToWorld = Matrix4x4.CreateScale(drawScale * drawScale3D) * Matrix4x4.CreateFromYawPitchRoll(rotation.Yaw, rotation.Pitch, rotation.Roll) * Matrix4x4.CreateTranslation(location);

        //LocalToWorld = Matrix4x4.CreateFromYawPitchRoll(rotation.Yaw, rotation.Pitch, rotation.Roll) * Matrix4x4.CreateScale(drawScale * drawScale3D) * Matrix4x4.CreateTranslation(location);
        //LocalToWorld = Matrix4x4.CreateFromYawPitchRoll(rotation.Yaw, rotation.Pitch, rotation.Roll) * Matrix4x4.CreateTranslation(location) * Matrix4x4.CreateScale(drawScale * drawScale3D);

        //LocalToWorld = Matrix4x4.CreateTranslation(location) * Matrix4x4.CreateFromYawPitchRoll(rotation.Yaw, rotation.Pitch, rotation.Roll) * Matrix4x4.CreateScale(drawScale * drawScale3D);
        //LocalToWorld = Matrix4x4.CreateTranslation(location) * Matrix4x4.CreateScale(drawScale * drawScale3D) * Matrix4x4.CreateFromYawPitchRoll(rotation.Yaw, rotation.Pitch, rotation.Roll);
        
        LocalToWorld = ActorUtils.ComposeLocalToWorld(location, rotation, drawScale * drawScale3D, prePivot);
        foreach (var cmp in Components)
        {
            cmp.UpdateLocalToWorld();
        }
    }

    public static ActorProxy Create(MeshRenderContext context, ExportEntry actorExport)
    {
        string className = actorExport.ClassName;
        switch (className)
        {
        }
        if (GlobalUnrealObjectInfo.IsA(className, "StaticMeshActor", actorExport.Game))
        {
            return new StaticMeshActorProxy(context, actorExport);
        }
        return null;
        //return new ActorProxy(context, actorExport);
    }

    protected void AddComponents(MeshRenderContext context, params Span<string> propNames)
    {
        foreach (var propName in propNames)
        {
            if (Properties.GetProp<ObjectProperty>(propName)?.ResolveToEntry(Export.FileRef) is ExportEntry componentExport)
            {
                if (PrimitiveComponentProxy.Create(context, componentExport, this) is { } cmpProxy)
                {
                    Components.Add(cmpProxy);
                }
            }
        }
    }

    public virtual void Render(MeshRenderContext context, RenderPass pass)
    {
        foreach (var component in Components)
        {
            component.Render(context, pass);
        }
    }

    public virtual BoxSphereBounds GetBounds()
    {
        if (Components.Count is 0)
        {
            return new BoxSphereBounds
            {
                Origin = LocalToWorld.Translation
            };
        }
        var bounds = Components[0].GetBounds();
        for (int i = 1; i < Components.Count; i++)
        {
            bounds = bounds.Union(Components[i].GetBounds());
        }
        return bounds;
    }

    #region IDisposable
    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                foreach (var cmp in Components)
                {
                    cmp.Dispose();
                }
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~ActorProxy()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion
}

file class StaticMeshActorProxy : ActorProxy
{
    public StaticMeshActorProxy(MeshRenderContext context, ExportEntry actorExport) : base(context, actorExport)
    {
        AddComponents(context, "StaticMeshComponent");
    }
}

public class StaticMeshComponentActorProxy : ActorProxy
{
    public StaticMeshComponentActorProxy(MeshRenderContext context, ExportEntry smcExport, StaticMeshCollectionActor smca, int smcaIndex) : base(smcExport)
    {
        LocalToWorld = smca.LocalToWorldTransforms[smcaIndex];
        (location, drawScale3D, rotation) = smca.GetDecomposedTransformationForIndex(smcaIndex);
        var staticMeshComponentProxy = PrimitiveComponentProxy.Create(context, smcExport, this);
        Components.Add(staticMeshComponentProxy);
    }
}