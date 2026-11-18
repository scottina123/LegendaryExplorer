using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Animation;

namespace LegendaryExplorer.Tools.LevelEditor;

// Fork component API on upstream's component proxies

public partial class PrimitiveComponentProxy
{
    private static readonly string[] ForkLightingChannelNames =
    [
        "bInitialized", "BSP", "Static", "Dynamic", "CompositeDynamic", "Skybox",
        "Unnamed_1", "Unnamed_2", "Unnamed_3", "Unnamed_4", "Unnamed_5", "Unnamed_6",
        "Cinematic_1", "Cinematic_2", "Cinematic_3", "Cinematic_4", "Cinematic_5", "Cinematic_6",
        "Cinematic_7", "Cinematic_8", "Cinematic_9", "Cinematic_10",
        "Gameplay_1", "Gameplay_2", "Gameplay_3", "Gameplay_4", "Crowd"
    ];

    /// <summary>
    /// Fork's packed LightingChannels (see <see cref="SceneLight.LightingChannelMask"/>): bit 0 is bInitialized, then the channels in order.
    /// Read from the component's current (effective) properties
    /// </summary>
    public uint LightingChannelMask => ReadLightingChannelMask(Properties);

    public static uint ReadLightingChannelMask(PropertyCollection properties)
    {
        if (properties?.GetProp<StructProperty>("LightingChannels") is not { } lightingChannels) return 0;

        uint mask = 0;
        if (lightingChannels.Properties.GetProp<BoolProperty>("bInitialized")?.Value == true
            || lightingChannels.Properties.GetProp<BoolProperty>("bIsInitialized")?.Value == true)
        {
            mask |= 1u;
        }
        for (int i = 1; i < ForkLightingChannelNames.Length; i++)
        {
            if (lightingChannels.Properties.GetProp<BoolProperty>(NameReference.FromInstancedString(ForkLightingChannelNames[i]))?.Value == true)
            {
                mask |= 1u << i;
            }
        }
        return mask;
    }
}

public abstract partial class MeshComponentProxy
{
    /// <summary>
    /// Fork (live material editor): each game-shader material the mesh draws, with the material it was read from and the sections (of the drawn LOD)
    /// that use it
    /// </summary>
    public IEnumerable<(IEntry SourceEntry, MaterialRenderProxy RenderProxy, IReadOnlyList<int> SlotIndexes)> GetLiveMaterialBindings()
    {
        if (Mesh is null || LOD < 0 || LOD >= Mesh.LODs.Count) yield break;
        List<ModelPreviewSection> sections = Mesh.LODs[LOD].Sections;
        foreach (IGrouping<string, int> group in Enumerable.Range(0, sections.Count)
                     .Where(i => sections[i].MaterialName is not null)
                     .GroupBy(i => sections[i].MaterialName, StringComparer.OrdinalIgnoreCase))
        {
            if (Mesh.Materials.TryGetValue(group.Key, out ModelPreviewMaterial<LEVertex> previewMaterial)
                && previewMaterial is LEShaderPreviewMaterial shaderMaterial)
            {
                yield return (shaderMaterial.MaterialExport, shaderMaterial.RenderProxy, group.ToArray());
            }
        }
    }

    /// <summary>
    /// Fork (live material editor picking): the game-shader materials a ray meets on the drawn LOD, nearest first, with the distance to each
    /// </summary>
    public IEnumerable<(MaterialRenderProxy RenderProxy, float Distance)> GetLiveMaterialHits(Vector3 rayOrigin, Vector3 rayDirection)
    {
        if (!IsVisible || Mesh is null || LOD < 0 || LOD >= Mesh.LODs.Count) yield break;

        ModelPreviewLOD<LEVertex> lod = Mesh.LODs[LOD];
        Mesh<LEVertex> mesh = lod.Mesh;
        var nearestByMaterial = new Dictionary<MaterialRenderProxy, float>();
        foreach (ModelPreviewSection section in lod.Sections)
        {
            if (section.MaterialName is null
                || !Mesh.Materials.TryGetValue(section.MaterialName, out ModelPreviewMaterial<LEVertex> previewMaterial)
                || previewMaterial is not LEShaderPreviewMaterial shaderMaterial)
            {
                continue;
            }

            int firstTriangle = (int)(section.StartIndex / 3);
            int endTriangle = Math.Min(mesh.Triangles.Count, firstTriangle + (int)section.TriangleCount);
            for (int triangleIndex = firstTriangle; triangleIndex < endTriangle; triangleIndex++)
            {
                Triangle triangle = mesh.Triangles[triangleIndex];
                if (triangle.Vertex1 >= (uint)mesh.Vertices.Count || triangle.Vertex2 >= (uint)mesh.Vertices.Count || triangle.Vertex3 >= (uint)mesh.Vertices.Count)
                {
                    continue;
                }
                Vector3 vertex0 = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex1].Position, mesh.LocalToWorld);
                Vector3 vertex1 = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex2].Position, mesh.LocalToWorld);
                Vector3 vertex2 = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex3].Position, mesh.LocalToWorld);
                if (RayIntersectsTriangle(rayOrigin, rayDirection, vertex0, vertex1, vertex2, out float distance)
                    && (!nearestByMaterial.TryGetValue(shaderMaterial.RenderProxy, out float nearestDistance) || distance < nearestDistance))
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

    /// <summary>
    /// Fork (volume drawing): where a ray first meets the drawn mesh, and the surface's normal there (facing the ray)
    /// </summary>
    internal bool TryGetDrawingSurfaceHit(Vector3 rayOrigin, Vector3 rayDirection, out Vector3 point, out Vector3 normal, out float distance)
    {
        point = normal = default;
        distance = float.PositiveInfinity;
        if (!IsVisible || Mesh is null || Mesh.LODs.Count == 0) return false;
        BoxSphereBounds bounds = GetBounds();
        float along = MathF.Max(Vector3.Dot(bounds.Origin - rayOrigin, rayDirection), 0);
        if (Vector3.DistanceSquared(rayOrigin + rayDirection * along, bounds.Origin) > bounds.SphereRadius * bounds.SphereRadius) return false;

        Mesh<LEVertex> mesh = Mesh.LODs[Math.Clamp(LOD, 0, Mesh.LODs.Count - 1)].Mesh;
        float nearest = float.PositiveInfinity;
        Vector3 hitNormal = default;
        foreach (Triangle triangle in mesh.Triangles)
        {
            if (triangle.Vertex1 >= (uint)mesh.Vertices.Count || triangle.Vertex2 >= (uint)mesh.Vertices.Count || triangle.Vertex3 >= (uint)mesh.Vertices.Count)
            {
                continue;
            }
            Vector3 a = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex1].Position, mesh.LocalToWorld);
            Vector3 b = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex2].Position, mesh.LocalToWorld);
            Vector3 c = Vector3.Transform(mesh.Vertices[(int)triangle.Vertex3].Position, mesh.LocalToWorld);
            if (RayIntersectsTriangle(rayOrigin, rayDirection, a, b, c, out float t) && t < nearest)
            {
                nearest = t;
                hitNormal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            }
        }
        if (!float.IsFinite(nearest)) return false;
        distance = nearest;
        point = rayOrigin + rayDirection * nearest;
        normal = Vector3.Dot(hitNormal, rayDirection) > 0 ? -hitNormal : hitNormal;
        return true;
    }

    private static bool RayIntersectsTriangle(Vector3 rayOrigin, Vector3 rayDirection, Vector3 vertex0, Vector3 vertex1, Vector3 vertex2, out float distance)
    {
        const float epsilon = 0.000001f;
        distance = 0;
        Vector3 edge1 = vertex1 - vertex0;
        Vector3 edge2 = vertex2 - vertex0;
        Vector3 cross = Vector3.Cross(rayDirection, edge2);
        float determinant = Vector3.Dot(edge1, cross);
        if (MathF.Abs(determinant) < epsilon) return false;

        float inverseDeterminant = 1f / determinant;
        Vector3 originToVertex = rayOrigin - vertex0;
        float u = Vector3.Dot(originToVertex, cross) * inverseDeterminant;
        if (u < 0 || u > 1) return false;

        Vector3 secondCross = Vector3.Cross(originToVertex, edge1);
        float v = Vector3.Dot(rayDirection, secondCross) * inverseDeterminant;
        if (v < 0 || u + v > 1) return false;

        distance = Vector3.Dot(edge2, secondCross) * inverseDeterminant;
        return distance > epsilon;
    }
}

public partial class StaticMeshComponentProxy
{
    /// <summary>Fork (navigation generation): adds the mesh's collision triangles</summary>
    internal void AppendNavigationCollision(List<LevelCollisionTriangle> output)
    {
        if (StaticMeshExport is not { } meshExport) return;
        StaticMesh staticMesh = RenderContext.GetStaticMesh(meshExport);
        LevelCollisionFlags flags = NavigationCollisionGeometry.GetFlags(this);
        NavigationCollisionGeometry.AppendStaticMesh(output, staticMesh, meshExport, LocalToWorld, flags, Export);
    }
}

public partial class BrushComponentProxy
{
    /// <summary>Fork (navigation generation): adds the brush's collision hulls</summary>
    internal void AppendNavigationCollision(List<LevelCollisionTriangle> output)
    {
        LevelCollisionFlags flags = NavigationCollisionGeometry.GetFlags(this);
        NavigationCollisionGeometry.AppendAggregateGeometry(output, Properties.GetProp<StructProperty>("BrushAggGeom"), LocalToWorld, flags, Export);
    }
}

public partial class SkeletalMeshComponentProxy
{
    private SkeletalMesh previewSkeletalMesh;

    /// <summary>Fork (actor preview): whether the component can be posed by a preview animation</summary>
    internal bool CanPreviewAnimation => animPlayer is not null && skinnedMeshRenderer is not null && previewSkeletalMesh?.RefSkeleton is { Length: > 0 };

    /// <summary>
    /// Builds the skinning and animation state <see cref="LoadMesh"/> builds from the component's mesh export, from <paramref name="mesh"/> directly,
    /// without its GPU geometry. For posing a skeleton that has no export to load from (and tests)
    /// </summary>
    internal void AttachPreviewSkeleton(SkeletalMesh mesh, MEGame game)
    {
        if (mesh?.LODModels is not { Length: > 0 } lods) return;
        skinnedMeshRenderer = new SkinnedMeshRenderer();
        skinnedMeshRenderer.BuildFromSkeletalMesh(game, lods[Math.Min(LOD, lods.Length - 1)]);
        animPlayer = new AnimSequencePlayer(mesh);
        previewSkeletalMesh = mesh;
    }

    /// <summary>
    /// Fork (actor preview): plays a chosen animation on this component, looping. Returns its player, or null if the animation has none of the
    /// component's bones (it's then cleared). Null clears it
    /// </summary>
    internal AnimSequencePlayer SetPreviewAnimation(AnimSequence animation)
    {
        if (animPlayer is null || skinnedMeshRenderer is null) return null;
        animPlayer.SetAnimation(animation, RenderContext.PackageCache, animationDataIsPrepared: animation?.RawAnimationData is not null);
        if (animation is not null && previewSkeletalMesh?.RefSkeleton is { } skeleton
            && !skeleton.Any(bone => animation.Bones.Contains(bone.Name.Instanced, StringComparer.OrdinalIgnoreCase)))
        {
            animPlayer.SetAnimation(null);
            animation = null;
        }
        animPlayer.IsLooping = true;
        skinnedMeshRenderer.NeedsUpdate = true;
        return animation is null ? null : animPlayer;
    }

    internal void SetPreviewAnimationTime(float time)
    {
        animPlayer?.SetCurrentTime(time);
        if (skinnedMeshRenderer is not null) skinnedMeshRenderer.NeedsUpdate = true;
    }
}
