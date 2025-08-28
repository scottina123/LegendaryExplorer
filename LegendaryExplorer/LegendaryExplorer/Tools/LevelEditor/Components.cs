using LegendaryExplorer.Misc;
using LegendaryExplorer.UserControls.SharedToolControls.Scene3D;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

public class PrimitiveComponentProxy : NotifyPropertyChangedBase, IDisposable
{
    public Matrix4x4 LocalToWorld;

    public PropertyCollection Properties;

    protected ExportEntry Export;

    public ActorProxy Actor;

    private Rotator rotation;
    private Vector3 translation;
    private Vector3 scale3D;
    private float scale;
    private bool absoluteTranslation;
    private bool absoluteRotation;
    private bool absoluteScale;
    public Rotator Rotation
    {
        get => rotation;
        set { if (SetProperty(ref rotation, value)) UpdateLocalToWorld(); }
    }
    public Vector3 Translation
    {
        get => translation;
        set { if (SetProperty(ref translation, value)) UpdateLocalToWorld(); }
    }
    public Vector3 Scale3D
    {
        get => scale3D;
        set { if (SetProperty(ref scale3D, value)) UpdateLocalToWorld(); }
    }
    public float Scale
    {
        get => scale;
        set { if (SetProperty(ref scale, value)) UpdateLocalToWorld(); }
    }
    public bool AbsoluteTranslation
    {
        get => absoluteTranslation;
        set { if (SetProperty(ref absoluteTranslation, value)) UpdateLocalToWorld(); }
    }
    public bool AbsoluteRotation
    {
        get => absoluteRotation;
        set { if (SetProperty(ref absoluteRotation, value)) UpdateLocalToWorld(); }
    }
    public bool AbsoluteScale
    {
        get => absoluteScale;
        set { if (SetProperty(ref absoluteScale, value)) UpdateLocalToWorld(); }
    }

    protected PrimitiveComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent)
    {
        Actor = parent;
        Export = componentExport;
        Properties = componentExport.GetCondensedProperties();

        var rotationProp = Properties.GetProp<StructProperty>("Rotation");
        var translationProp = Properties.GetProp<StructProperty>("Translation");
        var scale3DProp = Properties.GetProp<StructProperty>("Scale3D");

        scale = Properties.GetProp<FloatProperty>("Scale")?.Value ?? 1;
        translation = translationProp != null ? CommonStructs.GetVector3(translationProp) : Vector3.Zero;
        scale3D = scale3DProp != null ? CommonStructs.GetVector3(scale3DProp) : Vector3.One;
        rotation = rotationProp != null ? CommonStructs.GetRotator(rotationProp) : new Rotator(0, 0, 0);

        absoluteTranslation = Properties.GetProp<BoolProperty>("AbsoluteTranslation")?.Value ?? false;
        absoluteRotation = Properties.GetProp<BoolProperty>("AbsoluteRotation")?.Value ?? false;
        absoluteScale = Properties.GetProp<BoolProperty>("AbsoluteScale")?.Value ?? false;

        UpdateSelfLocalToWorld();
    }

    public static PrimitiveComponentProxy Create(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent)
    {
        string className = componentExport.ClassName;
        if (GlobalUnrealObjectInfo.IsA(className, "StaticMeshComponent", componentExport.Game))
        {
            return new StaticMeshComponentProxy(context, componentExport, parent);
        }

        return new PrimitiveComponentProxy(context, componentExport, parent);
    }
    public virtual void LoadMeshData(MeshRenderContext context)
    {

    }

    public virtual void Render(MeshRenderContext context, RenderPass pass)
    {

    }

    private void UpdateSelfLocalToWorld()
    {
        var parentMatrix = Actor.LocalToWorld;
        if (absoluteTranslation)
        {
            parentMatrix.Translation = Vector3.Zero;
        }
        if (absoluteRotation || absoluteScale)
        {
            Vector3 x = parentMatrix.GetAxis(0);
            Vector3 y = parentMatrix.GetAxis(1);
            Vector3 z = parentMatrix.GetAxis(2);

            if (absoluteScale)
            {
                x = x.Normal();
                y = y.Normal();
                z = z.Normal();
            }
            if (absoluteRotation)
            {
                x = new Vector3(x.Length(), 0, 0);
                y = new Vector3(0, y.Length(), 0);
                z = new Vector3(0, 0, z.Length());
            }
            parentMatrix[0, 0] = x.X; parentMatrix[0, 1] = x.Y; parentMatrix[0, 2] = x.Z;
            parentMatrix[1, 0] = y.X; parentMatrix[1, 1] = y.Y; parentMatrix[1, 2] = y.Z;
            parentMatrix[2, 0] = z.X; parentMatrix[2, 1] = z.Y; parentMatrix[2, 2] = z.Z;
        }

        LocalToWorld = ActorUtils.ComposeLocalToWorld(translation, rotation, scale * scale3D) * parentMatrix;
    }

    public virtual void UpdateLocalToWorld()
    {
        UpdateSelfLocalToWorld();
    }

    public virtual BoxSphereBounds GetBounds()
    {
        return new BoxSphereBounds
        {
            Origin = LocalToWorld.Translation
        };
    }

    #region IDisposeable
    private bool isDisposed;
    protected virtual void Dispose(bool disposing)
    {
        if (!isDisposed)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            isDisposed = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~PrimitiveComponentProxy()
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

public abstract class MeshComponentProxy : PrimitiveComponentProxy
{
    public int LOD;
    public List<IEntry> MaterialOverrides = [];

    protected MeshComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        if (Properties.GetProp<ArrayProperty<ObjectProperty>>("Materials") is { } mats)
        {
            MaterialOverrides.AddRange(mats.Select(x => x.Value != 0 ? x.ResolveToEntry(Export.FileRef) : null));
        }
    }
}

public class StaticMeshComponentProxy : MeshComponentProxy
{
    private ModelPreview StaticMesh;
    private MeshElement CollisionMesh;

    public StaticMeshComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        LoadMeshData(context);
    }

    public override void LoadMeshData(MeshRenderContext context)
    {
        if (Properties.GetProp<ObjectProperty>("StaticMesh")?.ResolveToEntry(Export.FileRef) is ExportEntry meshExport)
        {
            StaticMesh stm = meshExport.GetBinaryData<StaticMesh>();
            if (stm.LODModels.Length > 0)
            {
                StaticMesh = new ModelPreview(context, stm, 0);
            }
            CollisionMesh = context.GetMeshFromAggGeom(stm.GetCollisionMeshProperty(Export.FileRef));
            UpdateSelfLocalToWorld();
        }
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        if (pass is RenderPass.Collision)
        {
            if (CollisionMesh is not null)
            {
                context.RenderMeshAsWireframe(CollisionMesh);
            }
            return;
        }
        StaticMesh?.RenderFallback(pass, context, LOD);
    }

    public override void UpdateLocalToWorld()
    {
        base.UpdateLocalToWorld();
        UpdateSelfLocalToWorld();
    }

    private void UpdateSelfLocalToWorld()
    {
        if (CollisionMesh is not null)
        {
            CollisionMesh.LocalToWorld = LocalToWorld;
        }
        if (StaticMesh is not null)
        {
            StaticMesh.UpdateLocalToWorld(LocalToWorld);
        }
    }

    public override BoxSphereBounds GetBounds()
    {
        if (StaticMesh is null or { LODs.Count: 0 })
        {
            return base.GetBounds();
        }
        return StaticMesh.LODs[0].Mesh.TransformedBounds;
    }

    protected override void Dispose(bool disposing)
    {
        StaticMesh?.Dispose();
        CollisionMesh?.Dispose();
        base.Dispose(disposing);
    }
}