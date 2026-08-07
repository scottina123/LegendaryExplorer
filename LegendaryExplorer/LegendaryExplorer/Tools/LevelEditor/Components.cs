using LegendaryExplorer.Misc;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Animation;
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

    public ExportEntry Export { get; protected set; }

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

    public bool IsVisible { get; set; } = true;

    public uint LightingChannelMask;

    private static readonly string[] LightingChannelNames =
    [
        "bInitialized", "BSP", "Static", "Dynamic", "CompositeDynamic", "Skybox",
        "Unnamed_1", "Unnamed_2", "Unnamed_3", "Unnamed_4", "Unnamed_5", "Unnamed_6",
        "Cinematic_1", "Cinematic_2", "Cinematic_3", "Cinematic_4", "Cinematic_5", "Cinematic_6",
        "Cinematic_7", "Cinematic_8", "Cinematic_9", "Cinematic_10",
        "Gameplay_1", "Gameplay_2", "Gameplay_3", "Gameplay_4", "Crowd"
    ];

    public static uint ReadLightingChannelMask(PropertyCollection properties)
    {
        var lightingChannels = properties.GetProp<StructProperty>("LightingChannels");
        if (lightingChannels is null)
            return 0;

        uint mask = 0;
        if (lightingChannels.Properties.GetProp<BoolProperty>("bInitialized")?.Value == true
            || lightingChannels.Properties.GetProp<BoolProperty>("bIsInitialized")?.Value == true)
        {
            mask |= 1u;
        }

        for (int i = 1; i < LightingChannelNames.Length; i++)
        {
            if (lightingChannels.Properties.GetProp<BoolProperty>(NameReference.FromInstancedString(LightingChannelNames[i]))?.Value == true)
            {
                mask |= (1u << i);
            }
        }

        return mask;
    }

    protected PrimitiveComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent)
    {
        Actor = parent;
        Export = componentExport;
        Properties = componentExport.GetCondensedProperties();

        LoadFromProperties();
        UpdateSelfLocalToWorld();
    }

    protected virtual void LoadFromProperties()
    {
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

        LightingChannelMask = ReadLightingChannelMask(Properties);
    }

    public static PrimitiveComponentProxy Create(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent)
    {
        string className = componentExport.ClassName;
        switch (className)
        {
            case "BrushComponent":
                return new BrushComponentProxy(context, componentExport, parent);
        }
        if (GlobalUnrealObjectInfo.IsA(className, "SpotLightComponent", componentExport.Game))
        {
            return new SpotLightComponentProxy(context, componentExport, parent);
        }
        if (GlobalUnrealObjectInfo.IsA(className, "DirectionalLightComponent", componentExport.Game))
        {
            return new DirectionalLightComponentProxy(context, componentExport, parent);
        }
        if (GlobalUnrealObjectInfo.IsA(className, "PointLightComponent", componentExport.Game))
        {
            return new PointLightComponentProxy(context, componentExport, parent);
        }
        if (GlobalUnrealObjectInfo.IsA(className, "StaticMeshComponent", componentExport.Game))
        {
            return new StaticMeshComponentProxy(context, componentExport, parent);
        }
        if (GlobalUnrealObjectInfo.IsA(className, "SkeletalMeshComponent", componentExport.Game))
        {
            return new SkeletalMeshComponentProxy(context, componentExport, parent);
        }
        if (GlobalUnrealObjectInfo.IsA(className, "ParticleSystemComponent", componentExport.Game)
            && context is LevelEditorRenderContext levelContext)
        {
            return new ParticleSystemComponentProxy(levelContext, componentExport, parent);
        }

        return new PrimitiveComponentProxy(context, componentExport, parent);
    }

    public virtual void Render(MeshRenderContext context, RenderPass pass) { }

    public virtual void UpdateScene(MeshRenderContext context, float deltaTime) { }

    public virtual void RefreshFromExport()
    {
        Properties = Export.GetCondensedProperties();
        LoadFromProperties();
        UpdateLocalToWorld();
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

    /// <summary>
    /// Shifts the already-computed world transform. Unlike <see cref="UpdateLocalToWorld"/> this does not recompute
    /// the transform, so transforms that were inherited from another component (skeletal meshes attached to an anim
    /// parent, for example) are preserved.
    /// </summary>
    public virtual void ApplyWorldOffset(Vector3 offset)
    {
        LocalToWorld.Translation -= offset;
    }

    public virtual BoxSphereBounds GetBounds()
    {
        return new BoxSphereBounds
        {
            Origin = LocalToWorld.Translation
        };
    }
    public virtual bool TestUIndexes(HashSet<int> uIndexes)
    {
        if (uIndexes.Contains(Export.UIndex))
        {
            return true;
        }
        return false;
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

public interface ILevelRenderResource
{
    bool RenderResourcesInitialized { get; }
    void PrepareRenderResources();
}

public sealed class ParticleSystemComponentProxy : PrimitiveComponentProxy, ILevelRenderResource
{
    private readonly LevelEditorRenderContext renderContext;
    private readonly IEntry particleSystemEntry;
    private volatile bool renderResourcesInitialized;
    private LevelVfxRenderer.Instance instance;

    internal bool RenderResourcesInitialized => renderResourcesInitialized;
    internal bool HasRenderableVfx => RenderResourcesInitialized && instance is not null;
    bool ILevelRenderResource.RenderResourcesInitialized => RenderResourcesInitialized;

    public ParticleSystemComponentProxy(LevelEditorRenderContext context, ExportEntry componentExport,
        ActorProxy parent) : base(context, componentExport, parent)
    {
        renderContext = context;
        particleSystemEntry = Properties.GetProp<ObjectProperty>("Template")?.ResolveToEntry(Export.FileRef);
    }

    internal void PrepareRenderResources()
    {
        if (RenderResourcesInitialized)
        {
            return;
        }

        instance = renderContext.VfxRenderer.CreateInstance(particleSystemEntry);
        renderResourcesInitialized = true;
    }

    void ILevelRenderResource.PrepareRenderResources() => PrepareRenderResources();

    public override void UpdateScene(MeshRenderContext context, float deltaTime)
    {
        if (renderContext.ShowEmitterVfx && RenderResourcesInitialized && instance is not null)
        {
            instance.Simulation.Tick(deltaTime);
        }
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        if (!IsVisible || !renderContext.ShowEmitterVfx || pass is not RenderPass.Hair
            || !RenderResourcesInitialized || instance is null)
        {
            return;
        }
        renderContext.VfxRenderer.Render(instance, LocalToWorld);
    }

    public override BoxSphereBounds GetBounds()
    {
        if (RenderResourcesInitialized && instance?.System.Definition.FixedLocalBounds is { IsValid: true } localBounds)
        {
            VfxBounds worldBounds = VfxBoundsMath.Transform(localBounds, LocalToWorld);
            Vector3 extent = (worldBounds.Maximum - worldBounds.Minimum) * 0.5f;
            return new BoxSphereBounds
            {
                Origin = (worldBounds.Minimum + worldBounds.Maximum) * 0.5f,
                BoxExtent = extent,
                SphereRadius = extent.Length()
            };
        }

        return new BoxSphereBounds
        {
            Origin = LocalToWorld.Translation,
            BoxExtent = new Vector3(256f),
            SphereRadius = 443.405f
        };
    }

    protected override void Dispose(bool disposing)
    {
        instance = null;
        base.Dispose(disposing);
    }
}

public class PointLightComponentProxy : PrimitiveComponentProxy
{
    public float Radius { get; set; }
    public float Brightness { get; set; }
    public System.Drawing.Color LightColor { get; set; }
    public System.Drawing.Color LightEnv_BouncedModulationColor { get; set; }
    public bool ApplyBouncedModulationColor { get; set; }

    public Vector3 EffectiveLightColor
    {
        get
        {
            System.Drawing.Color color = ApplyBouncedModulationColor ? LightEnv_BouncedModulationColor : LightColor;
            return new Vector3(color.R / 255f, color.G / 255f, color.B / 255f);
        }
    }

    public PointLightComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        Radius = Properties.GetProp<FloatProperty>("Radius")?.Value ?? 1024f;
        Brightness = Properties.GetProp<FloatProperty>("Brightness")?.Value ?? 1f;
        if (Properties.GetProp<StructProperty>("LightColor") is { } lightColorProp)
        {
            LightColor = CommonStructs.GetColor(lightColorProp);
        }
        else
        {
            LightColor = System.Drawing.Color.White;
        }

        if (Properties.GetProp<StructProperty>("LightEnv_BouncedModulationColor") is { } bouncedColorProp)
        {
            LightEnv_BouncedModulationColor = CommonStructs.GetColor(bouncedColorProp);
        }
        else
        {
            LightEnv_BouncedModulationColor = LightColor;
        }
    }

    public virtual void CommitChanges()
    {
        Properties.AddOrReplaceProp(new FloatProperty(Radius, "Radius"));
        Properties.AddOrReplaceProp(new FloatProperty(Brightness, "Brightness"));
        Properties.AddOrReplaceProp(CommonStructs.ColorProp(LightColor, "LightColor"));
        Properties.AddOrReplaceProp(CommonStructs.ColorProp(LightEnv_BouncedModulationColor, "LightEnv_BouncedModulationColor"));
        Export.WriteProperties(Properties);
    }

    public override void RefreshFromExport()
    {
        base.RefreshFromExport();
        Radius = Properties.GetProp<FloatProperty>("Radius")?.Value ?? 1024f;
        Brightness = Properties.GetProp<FloatProperty>("Brightness")?.Value ?? 1f;
        if (Properties.GetProp<StructProperty>("LightColor") is { } lightColorProp)
        {
            LightColor = CommonStructs.GetColor(lightColorProp);
        }
        else
        {
            LightColor = System.Drawing.Color.White;
        }

        if (Properties.GetProp<StructProperty>("LightEnv_BouncedModulationColor") is { } bouncedColorProp)
        {
            LightEnv_BouncedModulationColor = CommonStructs.GetColor(bouncedColorProp);
        }
        else
        {
            LightEnv_BouncedModulationColor = LightColor;
        }
    }
}

public class SpotLightComponentProxy : PointLightComponentProxy
{
    public float InnerConeAngle { get; set; }
    public float OuterConeAngle { get; set; }

    public SpotLightComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        InnerConeAngle = Properties.GetProp<FloatProperty>("InnerConeAngle")?.Value ?? 0f;
        OuterConeAngle = Properties.GetProp<FloatProperty>("OuterConeAngle")?.Value ?? 44f;
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
    }

    public override void CommitChanges()
    {
        Properties.AddOrReplaceProp(new FloatProperty(InnerConeAngle, "InnerConeAngle"));
        Properties.AddOrReplaceProp(new FloatProperty(OuterConeAngle, "OuterConeAngle"));
        base.CommitChanges();
    }

    public override void RefreshFromExport()
    {
        base.RefreshFromExport();
        InnerConeAngle = Properties.GetProp<FloatProperty>("InnerConeAngle")?.Value ?? 0f;
        OuterConeAngle = Properties.GetProp<FloatProperty>("OuterConeAngle")?.Value ?? 44f;
    }

}

public class DirectionalLightComponentProxy : PrimitiveComponentProxy
{
    public float Brightness { get; set; }
    public System.Drawing.Color LightColor { get; set; }

    public Vector3 EffectiveLightColor => new(LightColor.R / 255f, LightColor.G / 255f, LightColor.B / 255f);

    public DirectionalLightComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        Brightness = Properties.GetProp<FloatProperty>("Brightness")?.Value ?? 1f;
        if (Properties.GetProp<StructProperty>("LightColor") is { } lightColorProp)
        {
            LightColor = CommonStructs.GetColor(lightColorProp);
        }
        else
        {
            LightColor = System.Drawing.Color.White;
        }
    }

    public void CommitChanges()
    {
        Properties.AddOrReplaceProp(new FloatProperty(Brightness, "Brightness"));
        Properties.AddOrReplaceProp(CommonStructs.ColorProp(LightColor, "LightColor"));
        Export.WriteProperties(Properties);
    }

    public override void RefreshFromExport()
    {
        base.RefreshFromExport();
        Brightness = Properties.GetProp<FloatProperty>("Brightness")?.Value ?? 1f;
        if (Properties.GetProp<StructProperty>("LightColor") is { } lightColorProp)
        {
            LightColor = CommonStructs.GetColor(lightColorProp);
        }
        else
        {
            LightColor = System.Drawing.Color.White;
        }
    }
}

public abstract class MeshComponentProxy : PrimitiveComponentProxy, ILevelRenderResource
{
    protected readonly MeshRenderContext RenderContext;
    public bool IsVolumetric;
    public string MeshIFP { get; protected set; }
    protected ModelPreview<WorldVertex> Mesh;
    protected ModelPreview<LEVertex> GameShaderMesh;
    public int LOD;
    public List<IEntry> MaterialOverrides = [];
    protected BoxSphereBounds? SerializedMeshBounds;
    private volatile bool renderResourcesInitialized;
    protected readonly object RenderResourceLock = new();
    internal bool RenderResourcesInitialized
    {
        get => renderResourcesInitialized;
        private protected set => renderResourcesInitialized = value;
    }

    protected MeshComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        RenderContext = context;
        if (Properties.GetProp<ArrayProperty<ObjectProperty>>("Materials") is { } mats)
        {
            MaterialOverrides.AddRange(mats.Select(x => x.Value != 0 ? x.ResolveToEntry(Export.FileRef) : null));
        }
    }

    public override BoxSphereBounds GetBounds()
    {
        if (RenderResourcesInitialized && GameShaderMesh is { } gameShaderMesh && gameShaderMesh.LODs.Count > LOD)
        {
            return gameShaderMesh.LODs[LOD].Mesh.TransformedBounds;
        }
        if (Mesh is null || Mesh.LODs.Count <= LOD)
        {
            return SerializedMeshBounds is { } bounds ? bounds.TransformBy(LocalToWorld) : base.GetBounds();
        }
        return Mesh.LODs[LOD].Mesh.TransformedBounds;
    }

    protected bool EnsureRenderResources()
    {
        lock (RenderResourceLock)
        {
            return EnsureRenderResourcesCore();
        }
    }

    protected abstract bool EnsureRenderResourcesCore();

    internal void PrepareRenderResources() => EnsureRenderResources();
    bool ILevelRenderResource.RenderResourcesInitialized => RenderResourcesInitialized;
    void ILevelRenderResource.PrepareRenderResources() => PrepareRenderResources();

    protected bool UseGameShaderPreview(MeshRenderContext context) =>
        context is LevelEditorRenderContext { UseGameShaderMeshPreviews: true };

    protected virtual int LiveMaterialLOD => LOD;

    protected void RenderGameShaderMesh(MeshRenderContext context, RenderPass pass, int lod = -1)
    {
        if (lod < 0) lod = LOD;
        if (GameShaderMesh is null || lod < 0 || lod >= GameShaderMesh.LODs.Count)
        {
            return;
        }
        if (pass is RenderPass.HitTest)
        {
            context.RenderNativeMeshHitTest(GameShaderMesh.LODs[lod].Mesh);
            return;
        }

        bool previousCameraRelative = context.UseCameraRelativeNativeRendering;
        context.UseCameraRelativeNativeRendering = true;
        try
        {
            GameShaderMesh.Render(pass, context, lod);
        }
        finally
        {
            context.UseCameraRelativeNativeRendering = previousCameraRelative;
        }
    }

    public IEnumerable<(IEntry SourceEntry, MaterialRenderProxy RenderProxy, IReadOnlyList<int> SlotIndexes)> GetLiveMaterialBindings()
    {
        if (!EnsureRenderResources() || GameShaderMesh is null)
        {
            yield break;
        }

        foreach (IGrouping<string, (IEntry Entry, int Slot)> group in GameShaderMesh.MaterialSlots
                     .Select((entry, slot) => (Entry: entry, Slot: slot))
                     .Where(item => item.Entry is not null)
                     .GroupBy(item => item.Entry.InstancedFullPath, StringComparer.OrdinalIgnoreCase))
        {
            IEntry sourceEntry = group.First().Entry;
            if (GameShaderMesh.Materials.TryGetValue(sourceEntry.InstancedFullPath, out ModelPreviewMaterial<LEVertex> previewMaterial)
                && previewMaterial is LEShaderPreviewMaterial shaderMaterial)
            {
                yield return (sourceEntry, shaderMaterial.RenderProxy, group.Select(item => item.Slot).ToArray());
            }
        }
    }

    public IEnumerable<(MaterialRenderProxy RenderProxy, float Distance)> GetLiveMaterialHits(
        Vector3 rayOrigin, Vector3 rayDirection)
    {
        if (!EnsureRenderResources())
        {
            yield break;
        }
        int liveMaterialLod = LiveMaterialLOD;
        if (!IsVisible || GameShaderMesh is null || liveMaterialLod < 0 || liveMaterialLod >= GameShaderMesh.LODs.Count)
        {
            yield break;
        }

        ModelPreviewLOD<LEVertex> lod = GameShaderMesh.LODs[liveMaterialLod];
        Mesh<LEVertex> mesh = lod.Mesh;
        var nearestByMaterial = new Dictionary<MaterialRenderProxy, float>();
        foreach (ModelPreviewSection section in lod.Sections)
        {
            if (section.MaterialName is null
                || !GameShaderMesh.Materials.TryGetValue(section.MaterialName, out ModelPreviewMaterial<LEVertex> previewMaterial)
                || previewMaterial is not LEShaderPreviewMaterial shaderMaterial)
            {
                continue;
            }

            int firstTriangle = (int)(section.StartIndex / 3);
            int endTriangle = Math.Min(mesh.Triangles.Count, firstTriangle + (int)section.TriangleCount);
            for (int triangleIndex = firstTriangle; triangleIndex < endTriangle; triangleIndex++)
            {
                Triangle triangle = mesh.Triangles[triangleIndex];
                if (triangle.Vertex1 >= (uint)mesh.Vertices.Count
                    || triangle.Vertex2 >= (uint)mesh.Vertices.Count
                    || triangle.Vertex3 >= (uint)mesh.Vertices.Count)
                {
                    continue;
                }

                Vector3 vertex0 = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex1].Position, mesh.LocalToWorld);
                Vector3 vertex1 = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex2].Position, mesh.LocalToWorld);
                Vector3 vertex2 = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex3].Position, mesh.LocalToWorld);
                if (RayIntersectsTriangle(rayOrigin, rayDirection, vertex0, vertex1, vertex2, out float distance)
                    && (!nearestByMaterial.TryGetValue(shaderMaterial.RenderProxy, out float nearestDistance)
                        || distance < nearestDistance))
                {
                    nearestByMaterial[shaderMaterial.RenderProxy] = distance;
                }
            }
        }

        foreach ((MaterialRenderProxy renderProxy, float distance) in nearestByMaterial.OrderBy(pair => pair.Value))
        {
            yield return (renderProxy, distance);
        }
    }

    private static bool RayIntersectsTriangle(Vector3 rayOrigin, Vector3 rayDirection,
        Vector3 vertex0, Vector3 vertex1, Vector3 vertex2, out float distance)
    {
        const float epsilon = 0.000001f;
        Vector3 edge1 = vertex1 - vertex0;
        Vector3 edge2 = vertex2 - vertex0;
        Vector3 cross = Vector3.Cross(rayDirection, edge2);
        float determinant = Vector3.Dot(edge1, cross);
        if (Math.Abs(determinant) < epsilon)
        {
            distance = 0;
            return false;
        }

        float inverseDeterminant = 1f / determinant;
        Vector3 originToVertex = rayOrigin - vertex0;
        float u = Vector3.Dot(originToVertex, cross) * inverseDeterminant;
        if (u < 0 || u > 1)
        {
            distance = 0;
            return false;
        }

        Vector3 secondCross = Vector3.Cross(originToVertex, edge1);
        float v = Vector3.Dot(rayDirection, secondCross) * inverseDeterminant;
        if (v < 0 || u + v > 1)
        {
            distance = 0;
            return false;
        }

        distance = Vector3.Dot(edge2, secondCross) * inverseDeterminant;
        return distance > epsilon;
    }

    protected override void Dispose(bool disposing)
    {
        lock (RenderResourceLock)
        {
            Mesh?.Dispose();
            Mesh = null;
            GameShaderMesh?.Dispose();
            GameShaderMesh = null;
        }
        base.Dispose(disposing);
    }
}

public class StaticMeshComponentProxy : MeshComponentProxy
{
    private Mesh<WorldVertex> CollisionMesh;
    private readonly StaticMesh staticMesh;
    private readonly ExportEntry meshExport;
    private readonly bool useGameShader;
    private bool hasRenderableMesh;
    private StructProperty collisionGeometry;
    private bool collisionGeometryLoaded;

    internal void AppendNavigationCollision(List<LevelCollisionTriangle> output)
    {
        LevelCollisionFlags flags = NavigationCollisionGeometry.GetFlags(this);
        NavigationCollisionGeometry.AppendStaticMesh(output, staticMesh, meshExport, LocalToWorld, flags, Export);
    }

    public StaticMeshComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        if (Properties.GetProp<ObjectProperty>("StaticMesh") is { Value: not 0 } meshProperty
            && context.ResolveExportCached(Export.FileRef, meshProperty.Value) is { } resolvedMeshExport)
        {
            meshExport = resolvedMeshExport;
            staticMesh = context.GetCachedStaticMesh(meshExport);
            SerializedMeshBounds = staticMesh.Bounds;
            if (staticMesh.LODModels.Length > LOD)
            {
                hasRenderableMesh = true;
                useGameShader = UseGameShaderPreview(context) && meshExport.Game.IsMEGame();
                MeshIFP = meshExport.InstancedFullPath;
                if (MeshIFP.Contains("Volumetric", StringComparison.OrdinalIgnoreCase)
                    || GetEffectiveMaterialEntries().Any(entry => entry?.InstancedFullPath.Contains(
                        "VolumeLight", StringComparison.OrdinalIgnoreCase) is true))
                {
                    IsVolumetric = true;
                }
            }
        }
    }

    private IEnumerable<IEntry> GetEffectiveMaterialEntries()
    {
        StaticMeshElement[] elements = staticMesh?.LODModels.Length > LOD
            ? staticMesh.LODModels[LOD].Elements
            : [];
        for (int slot = 0; slot < elements.Length; slot++)
        {
            if (slot < MaterialOverrides.Count && MaterialOverrides[slot] is { } materialOverride)
            {
                yield return materialOverride;
            }
            else if (elements[slot].Material != 0
                     && meshExport.FileRef.IsEntry(elements[slot].Material))
            {
                yield return meshExport.FileRef.GetEntry(elements[slot].Material);
            }
        }
    }

    protected override bool EnsureRenderResourcesCore()
    {
        if (RenderResourcesInitialized)
        {
            return GameShaderMesh is not null || Mesh is not null;
        }
        if (!hasRenderableMesh)
        {
            RenderResourcesInitialized = true;
            return false;
        }

        if (useGameShader)
        {
            GameShaderMesh = new ModelPreview<LEVertex>(RenderContext, staticMesh, LOD, MaterialOverrides);
            GameShaderMesh.PrepareGraphicsResources(RenderContext);
        }
        else
        {
            Mesh = new ModelPreview<WorldVertex>(RenderContext, staticMesh, LOD, MaterialOverrides);
        }
        MaterialOverrides.Clear();
        UpdateSelfLocalToWorld(force: true);
        RenderResourcesInitialized = true;
        return true;
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        if (!IsVisible) return;
        if (pass is RenderPass.Collision)
        {
            if (!collisionGeometryLoaded)
            {
                collisionGeometry = staticMesh?.GetCollisionMeshProperty(meshExport?.FileRef ?? Export.FileRef);
                collisionGeometryLoaded = true;
            }
            CollisionMesh ??= context.GetMeshFromAggGeom(collisionGeometry);
            if (CollisionMesh is not null)
            {
                CollisionMesh.LocalToWorld = LocalToWorld;
                context.RenderMeshAsWireframe(CollisionMesh);
            }
            return;
        }
        if (!RenderResourcesInitialized) return;
        if (GameShaderMesh is not null)
        {
            RenderGameShaderMesh(context, pass);
        }
        else
        {
            Mesh?.Render(pass, context, LOD);
        }
    }

    public override void UpdateLocalToWorld()
    {
        base.UpdateLocalToWorld();
        UpdateSelfLocalToWorld();
    }

    public override void ApplyWorldOffset(Vector3 offset)
    {
        base.ApplyWorldOffset(offset);
        UpdateSelfLocalToWorld();
    }

    private void UpdateSelfLocalToWorld(bool force = false)
    {
        if (CollisionMesh is not null)
        {
            CollisionMesh.LocalToWorld = LocalToWorld;
        }
        if ((force || RenderResourcesInitialized) && Mesh is not null)
        {
            Mesh.UpdateLocalToWorld(LocalToWorld);
        }
        if ((force || RenderResourcesInitialized) && GameShaderMesh is not null)
        {
            GameShaderMesh.UpdateLocalToWorld(LocalToWorld);
        }
    }

    protected override void Dispose(bool disposing)
    {
        CollisionMesh?.Dispose();
        base.Dispose(disposing);
    }
}

public class SkeletalMeshComponentProxy : MeshComponentProxy
{
    SkinnedMeshRenderer skinnedMeshRenderer;
    AnimSequencePlayer animPlayer;
    private readonly MeshRenderContext renderContext;
    private SkeletalMesh skeletalMesh;
    private MEGame skeletalMeshGame;
    private bool useGameShader;
    private bool hasRenderableMesh;
    private ExportEntry pendingMorphExport;
    private bool pendingMorphUsesStoredLods;
    private ActorPreviewAnimation pendingPreviewAnimation;
    // Resource preparation can run on Level Editor's loader thread. CPU skinning is safe there,
    // but D3D11's immediate context is owned by the render thread, so publish the prepared vertices
    // and defer only their dynamic-buffer upload until the component is actually rendered.
    private volatile bool preparedSkinningUploadPending;

    public SkeletalMeshComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        renderContext = context;
        bool bTransformFromAnimParent = Properties.GetProp<BoolProperty>("bTransformFromAnimParent")?.Value ?? true;
        if (bTransformFromAnimParent
            && Properties.GetProp<ObjectProperty>("ParentAnimComponent")?.ResolveToEntry(Export.FileRef) is ExportEntry parentAnimExport
            && parent.Components.FirstOrDefault(cmp => cmp.Export == parentAnimExport) is { } parentAnimComponent)
        {
            LocalToWorld = parentAnimComponent.LocalToWorld;
        }
        if (Properties.GetProp<ObjectProperty>("SkeletalMesh") is { Value: not 0 } meshProperty
            && context.ResolveExportCached(Export.FileRef, meshProperty.Value) is { } meshExport)
        {
            SkeletalMesh skm = context.GetCachedSkeletalMesh(meshExport);
            skeletalMesh = skm;
            skeletalMeshGame = meshExport.FileRef.Game;
            SerializedMeshBounds = skm.Bounds;
            if (skm.LODModels.Length > LOD)
            {
                hasRenderableMesh = true;
                useGameShader = (UseGameShaderPreview(context) || parent.UseInGameSkeletalMeshRendering)
                                && meshExport.Game.IsMEGame();
                MeshIFP = meshExport.InstancedFullPath;
            }
        }
    }

    protected override bool EnsureRenderResourcesCore()
    {
        if (RenderResourcesInitialized)
        {
            return GameShaderMesh is not null || Mesh is not null;
        }
        if (!hasRenderableMesh)
        {
            RenderResourcesInitialized = true;
            return false;
        }

        if (useGameShader)
        {
            GameShaderMesh = new ModelPreview<LEVertex>(RenderContext, skeletalMesh, MaterialOverrides,
                loadOnlyFirstLod: true);
            GameShaderMesh.PrepareGraphicsResources(RenderContext);
        }
        else
        {
            Mesh = new ModelPreview<WorldVertex>(RenderContext, skeletalMesh, MaterialOverrides,
                loadOnlyFirstLod: true);
        }
        MaterialOverrides.Clear();
        UpdateSelfLocalToWorld(force: true);

        if (pendingMorphExport is not null)
        {
            ApplyPendingMorph();
        }
        ApplyPendingPreviewAnimation();
        PrepareSkinningForUpload();
        RenderResourcesInitialized = true;
        return true;
    }

    public override void UpdateScene(MeshRenderContext context, float deltaTime)
    {
        UploadPreparedSkinning(context);
        if (skinnedMeshRenderer?.NeedsUpdate is true)
        {
            if (GameShaderMesh is not null)
            {
                skinnedMeshRenderer.UpdateSkinning(context.ImmediateContext, GameShaderMesh.LODs[LOD].Mesh, animPlayer);
            }
            else if (Mesh is not null)
            {
                skinnedMeshRenderer.UpdateSkinning(context.ImmediateContext, Mesh.LODs[LOD].Mesh, animPlayer);
            }
        }
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        if (!IsVisible) return;
        if (!RenderResourcesInitialized) return;
        UploadPreparedSkinning(context);
        int renderLod = LOD;
        if (GameShaderMesh is not null)
        {
            RenderGameShaderMesh(context, pass, renderLod);
        }
        else
        {
            Mesh?.Render(pass, context, renderLod);
        }
    }

    private void PrepareSkinningForUpload()
    {
        if (skinnedMeshRenderer?.NeedsUpdate is not true)
        {
            return;
        }

        preparedSkinningUploadPending = GameShaderMesh is not null
            ? skinnedMeshRenderer.PrepareSkinning(GameShaderMesh.LODs[LOD].Mesh, animPlayer)
            : Mesh is not null && skinnedMeshRenderer.PrepareSkinning(Mesh.LODs[LOD].Mesh, animPlayer);
    }

    private void UploadPreparedSkinning(MeshRenderContext context)
    {
        if (!preparedSkinningUploadPending)
        {
            return;
        }

        if (GameShaderMesh is not null)
        {
            GameShaderMesh.LODs[LOD].Mesh.UpdateVertices(context.ImmediateContext);
        }
        else
        {
            Mesh?.LODs[LOD].Mesh.UpdateVertices(context.ImmediateContext);
        }
        preparedSkinningUploadPending = false;
    }

    private void EnsureSkinningRenderer()
    {
        if (skinnedMeshRenderer is not null || skeletalMesh is null || skeletalMesh.LODModels.Length <= LOD)
        {
            return;
        }
        skinnedMeshRenderer = new SkinnedMeshRenderer();
        skinnedMeshRenderer.BuildFromSkeletalMesh(skeletalMeshGame, skeletalMesh.LODModels[LOD]);
        animPlayer = new AnimSequencePlayer(skeletalMesh);
    }

    public void SetAnimation(AnimSequence animSequence, float pos)
    {
        if (animSequence is null)
        {
            if (animPlayer?.HasAnimation is true)
            {
                //cancel animation, reset to ref pose
                animPlayer.SetAnimation(null);
                skinnedMeshRenderer.NeedsUpdate = true;
            }
            return;
        }
        if (!EnsureRenderResources()) return;
        EnsureSkinningRenderer();
        if (animPlayer is null) return;
        if (animSequence.Name != animPlayer.AnimName)
        {
            animPlayer.SetAnimation(animSequence, renderContext.PackageCache);
        }
        animPlayer.SetCurrentTime(pos);
        skinnedMeshRenderer.NeedsUpdate = true;
    }

    internal void ConfigurePreviewAnimation(ActorPreviewAnimation animation)
    {
        lock (RenderResourceLock)
        {
            pendingPreviewAnimation = animation;
            if (!RenderResourcesInitialized)
            {
                return;
            }

            if (animation is null)
            {
                SetAnimation(null, 0f);
            }
            else
            {
                ApplyPendingPreviewAnimation();
            }
            UpdateScene(RenderContext, 0f);
        }
    }

    private void ApplyPendingPreviewAnimation()
    {
        AnimSequence animation = pendingPreviewAnimation?.Resolve(renderContext);
        if (animation is null)
        {
            return;
        }

        EnsureSkinningRenderer();
        if (animPlayer is null)
        {
            return;
        }

        if (animation.Name != animPlayer.AnimName)
        {
            animPlayer.SetAnimation(animation, renderContext.PackageCache, animationDataIsPrepared: true);
        }
        animPlayer.SetCurrentTime(animation.SequenceLength * ActorPreviewAnimation.RepresentativePoseFraction);
        skinnedMeshRenderer.NeedsUpdate = true;
    }

    public void ApplyMorph(ExportEntry morphExport, bool useStoredMorphLods)
    {
        if (skeletalMesh is null || morphExport is null)
        {
            return;
        }
        lock (RenderResourceLock)
        {
            pendingMorphExport = morphExport;
            pendingMorphUsesStoredLods = useStoredMorphLods;
            if (RenderResourcesInitialized)
            {
                ApplyPendingMorph();
            }
        }
    }

    private void ApplyPendingMorph()
    {
        ExportEntry morphExport = pendingMorphExport;
        if (morphExport is null || (Mesh is null && GameShaderMesh is null)) return;
        EnsureSkinningRenderer();
        if (skinnedMeshRenderer is null) return;

        (LegendaryExplorerCore.Unreal.Classes.BonePosition[] bonePositions, Vector3[][] morphLods) =
            LegendaryExplorerCore.Unreal.Classes.BioMorphFace.GetBoneAndVertexPositions(morphExport);
        Vector3[] morphPositions = pendingMorphUsesStoredLods && morphLods?.Length > LOD ? morphLods[LOD] : null;
        skinnedMeshRenderer.ApplyMorph(skeletalMesh.RefSkeleton, bonePositions, morphPositions);
        ApplyMorphMaterialOverrides(morphExport);
    }

    private void ApplyMorphMaterialOverrides(ExportEntry morphExport)
    {
        if (GameShaderMesh is null
            || morphExport.GetProperty<ObjectProperty>("m_oMaterialOverrides")
                is not { Value: not 0 } materialOverrideProperty
            || renderContext.ResolveExportCached(morphExport.FileRef, materialOverrideProperty.Value)
                is not { } materialOverride)
        {
            return;
        }

        List<MaterialRenderProxy> materials = GameShaderMesh.Materials.Values
            .OfType<LEShaderPreviewMaterial>()
            .Select(value => value.RenderProxy)
            .Distinct()
            .ToList();
        PropertyCollection overrideProperties = materialOverride.GetProperties(packageCache: renderContext.PackageCache);
        foreach (MaterialRenderProxy material in materials)
        {
            material.ResetPreviewParameterOverrides();
            if (overrideProperties.GetProp<ArrayProperty<StructProperty>>("m_aScalarOverrides") is { } scalarOverrides)
            {
                foreach (StructProperty scalar in scalarOverrides)
                {
                    string name = scalar.GetProp<NameProperty>("nName")?.Value.Instanced;
                    if (!string.IsNullOrEmpty(name))
                    {
                        material.SetScalarParameter(name, scalar.GetProp<FloatProperty>("sValue")?.Value ?? 0f);
                    }
                }
            }
            if (overrideProperties.GetProp<ArrayProperty<StructProperty>>("m_aColorOverrides") is { } colorOverrides)
            {
                foreach (StructProperty color in colorOverrides)
                {
                    string name = color.GetProp<NameProperty>("nName")?.Value.Instanced;
                    if (!string.IsNullOrEmpty(name))
                    {
                        LinearColor value = color.GetProp<StructProperty>("cValue") is { } linearColor
                            ? CommonStructs.GetLinearColor(linearColor)
                            : LinearColor.White;
                        material.SetVectorParameter(name, value);
                    }
                }
            }
        }

        if (overrideProperties.GetProp<ArrayProperty<StructProperty>>("m_aTextureOverrides") is not { } textureOverrides)
        {
            return;
        }
        foreach (StructProperty texture in textureOverrides)
        {
            string name = texture.GetProp<NameProperty>("nName")?.Value.Instanced;
            IEntry textureEntry = texture.GetProp<ObjectProperty>("m_pTexture")
                ?.ResolveToEntry(materialOverride.FileRef);
            ExportEntry textureExport = textureEntry switch
            {
                ExportEntry export when export.IsTexture() => export,
                ImportEntry import => renderContext.ResolveExportCached(import),
                _ => null
            };
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            if (textureExport is not null && !textureExport.IsTexture())
            {
                textureExport = null;
            }
            PreviewTextureCache.TextureEntry cachedTexture = textureExport is not null
                ? renderContext.TextureCache.LoadTexture(textureExport, renderContext.PackageCache)
                : null;
            foreach (MaterialRenderProxy material in materials)
            {
                material.SetTextureParameter(name, textureExport?.InstancedFullPath, cachedTexture);
            }
        }
    }

    public override void UpdateLocalToWorld()
    {
        base.UpdateLocalToWorld();
        UpdateSelfLocalToWorld();
    }

    public override void ApplyWorldOffset(Vector3 offset)
    {
        base.ApplyWorldOffset(offset);
        UpdateSelfLocalToWorld();
    }

    private void UpdateSelfLocalToWorld(bool force = false)
    {
        if (force || RenderResourcesInitialized)
        {
            Mesh?.UpdateLocalToWorld(LocalToWorld);
            GameShaderMesh?.UpdateLocalToWorld(LocalToWorld);
        }
    }
}

public class BrushComponentProxy : PrimitiveComponentProxy
{
    private readonly Mesh<WorldVertex> Brush;
    private readonly StructProperty brushAggregateGeometry;

    public BrushComponentProxy(MeshRenderContext context, ExportEntry componentExport, ActorProxy parent) : base(context, componentExport, parent)
    {
        brushAggregateGeometry = Properties.GetProp<StructProperty>("BrushAggGeom");
        Brush = context.GetMeshFromAggGeom(brushAggregateGeometry);
        UpdateSelfLocalToWorld();
    }

    internal void AppendNavigationCollision(List<LevelCollisionTriangle> output)
    {
        LevelCollisionFlags flags = NavigationCollisionGeometry.GetFlags(this);
        NavigationCollisionGeometry.AppendAggregateGeometry(output, brushAggregateGeometry, LocalToWorld, flags, Export);
    }

    public override void Render(MeshRenderContext context, RenderPass pass)
    {
        if (!IsVisible) return;
        if (Brush is not null)
        {
            context.RenderMeshAsWireframe(Brush);
        }
    }

    public override void UpdateLocalToWorld()
    {
        base.UpdateLocalToWorld();
        UpdateSelfLocalToWorld();
    }

    public override void ApplyWorldOffset(Vector3 offset)
    {
        base.ApplyWorldOffset(offset);
        UpdateSelfLocalToWorld();
    }

    private void UpdateSelfLocalToWorld()
    {
        if (Brush is not null)
        {
            Brush.LocalToWorld = LocalToWorld;
        }
    }

    protected override void Dispose(bool disposing)
    {
        Brush?.Dispose();
        base.Dispose(disposing);
    }
}
