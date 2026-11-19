using System;
using System.Collections.Generic;
using System.Numerics;
using LegendaryExplorer.Lightmaps.Solver;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Lightmaps.Lightmass;

/// <summary>A vertex of a mapping's mesh in world space, as Lightmass's FStaticLightingVertex has it</summary>
internal struct LightmassGeoVertex
{
    public Vector3 Position, TangentX, TangentY, TangentZ;
    public Vector2 MaterialUV, LightmapUV;
}

/// <summary>An element of a mapping's mesh: its material and how its triangles shadow</summary>
internal sealed record LightmassGeoElement(LightmassMaterial Material, LightmassElementSettings Settings, bool CastShadow, bool TwoSidedShadow, bool SelfShadowOnly)
{
    /// <summary>Lit from both sides: the primitive's bUseTwoSidedLighting, or a two-sided material</summary>
    public bool TwoSidedLighting => Settings.TwoSidedLighting || Material.TwoSided;
}

/// <summary>
/// A mesh instance, BSP mapping or terrain mapping in world space, as Lightmass builds its static lighting mesh from what the exporter writes
/// (FStaticMeshStaticLightingMesh, FBSPSurfaceStaticLighting, FTerrainComponentStaticLighting)
/// </summary>
internal sealed class LightmassGeoMesh
{
    public LightmassGeoVertex[] Vertices = [];
    /// <summary>Three per triangle</summary>
    public int[] Indices = [];
    /// <summary>Each triangle's index into <see cref="Elements"/>, or -1 for a triangle in no element</summary>
    public int[] TriangleElements = [];
    public List<LightmassGeoElement> Elements = [];
    /// <summary>The solver instance its triangles and samples belong to</summary>
    public uint Instance;

    public int TriangleCount => Indices.Length / 3;

    public Vector3 FaceNormal(int t)
    {
        var a = Vertices[Indices[t * 3]].Position;
        var n = Vector3.Cross(Vertices[Indices[t * 3 + 1]].Position - a, Vertices[Indices[t * 3 + 2]].Position - a);
        return n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.Zero;
    }

    // ================= static meshes =================

    /// <summary>
    /// An instance's LOD in world space: positions by LocalToWorld (after the spline deformation for a spline mesh), tangents by LocalToWorld and
    /// normals by its inverse transpose, normalised; TangentY = cross(TangentZ, TangentX) · sign(TangentZ.W) as FStaticMeshVertex has it. Element
    /// triangles shadow when the instance casts shadows and the element has shadow casting on.
    /// </summary>
    public static LightmassGeoMesh FromInstance(LightmassInstance instance, uint instanceId)
    {
        var lod = instance.Mesh.Mesh.LODModels[instance.LodIndex];
        int vertexCount = LightmassExporter.VertexCount(lod);
        var l2w = instance.LocalToWorld;
        Matrix4x4.Invert(l2w, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        int uvCount = (int)lod.VertexBuffer.NumTexCoords;
        int lightmapChannel = instance.Mesh.LightMapCoordinateIndex;
        Vector2 Uv(StaticMeshVertexBuffer.StaticMeshFullVertex vtx, int channel)
        {
            if (channel < 0 || channel >= uvCount) return Vector2.Zero;
            if (lod.VertexBuffer.bUseFullPrecisionUVs) return vtx.FullPrecisionUVs != null && channel < vtx.FullPrecisionUVs.Length ? vtx.FullPrecisionUVs[channel] : Vector2.Zero;
            return vtx.HalfPrecisionUVs != null && channel < vtx.HalfPrecisionUVs.Length ? new Vector2(vtx.HalfPrecisionUVs[channel].X, vtx.HalfPrecisionUVs[channel].Y) : Vector2.Zero;
        }

        var mesh = new LightmassGeoMesh { Instance = instanceId, Vertices = new LightmassGeoVertex[vertexCount] };
        for (int v = 0; v < vertexCount; v++)
        {
            var vtx = lod.VertexBuffer.VertexData[v];
            var position = lod.PositionVertexBuffer.VertexData[v];
            var x = (Vector3)vtx.TangentX;
            var z4 = (Vector4)vtx.TangentZ;
            if (instance.Spline != null) (position, x, z4) = instance.Spline.Deform(position, x, z4);
            var z = new Vector3(z4.X, z4.Y, z4.Z);
            var y = Vector3.Cross(z, x) * (z4.W < 0 ? -1f : 1f);
            mesh.Vertices[v] = new LightmassGeoVertex
            {
                Position = Vector3.Transform(position, l2w),
                TangentX = SafeNormalize(Vector3.TransformNormal(x, l2w)),
                TangentY = SafeNormalize(Vector3.TransformNormal(y, l2w)),
                TangentZ = SafeNormalize(Vector3.TransformNormal(z, normalMatrix)),
                MaterialUV = Uv(vtx, 0),
                LightmapUV = Uv(vtx, lightmapChannel),
            };
        }

        int triangles = lod.IndexBuffer.Length / 3;
        var indices = new List<int>(triangles * 3);
        var elementOf = new List<int>(triangles);
        for (int e = 0; e < lod.Elements.Length; e++)
        {
            var element = lod.Elements[e];
            var material = e < instance.ElementMaterials.Count ? instance.ElementMaterials[e] : instance.ElementMaterials.Count > 0 ? instance.ElementMaterials[^1] : null;
            if (material == null) continue;
            mesh.Elements.Add(new LightmassGeoElement(material, instance.Settings, instance.CastShadow && element.bEnableShadowCasting,
                instance.CastShadowAsTwoSided || material.TwoSided, instance.SelfShadowOnly));
            int elementIndex = mesh.Elements.Count - 1;
            long first = element.FirstIndex;
            for (long t = 0; t < element.NumTriangles; t++)
            {
                long i = first + t * 3;
                if (i + 2 >= lod.IndexBuffer.Length) break;
                int a = lod.IndexBuffer[i], b = lod.IndexBuffer[i + 1], c = lod.IndexBuffer[i + 2];
                if (a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                indices.Add(a); indices.Add(b); indices.Add(c);
                elementOf.Add(elementIndex);
            }
        }
        mesh.Indices = indices.ToArray();
        mesh.TriangleElements = elementOf.ToArray();
        return mesh;
    }

    // ================= BSP =================

    /// <summary>A BSP mapping: its world-space vertices with the surfaces' tangent bases, and its lightmap UVs already in the allocation's space</summary>
    public static LightmassGeoMesh FromBsp(LightmassBspMapping mapping, uint instanceId)
    {
        var mesh = new LightmassGeoMesh { Instance = instanceId, Vertices = new LightmassGeoVertex[mapping.Vertices.Count] };
        for (int v = 0; v < mapping.Vertices.Count; v++)
        {
            var b = mapping.Vertices[v];
            mesh.Vertices[v] = new LightmassGeoVertex
            {
                Position = b.Position, TangentX = SafeNormalize(b.TangentX), TangentY = SafeNormalize(b.TangentY), TangentZ = SafeNormalize(b.TangentZ),
                MaterialUV = b.TextureUV, LightmapUV = b.LightmapUV,
            };
        }
        foreach (var element in mapping.Elements)
            mesh.Elements.Add(new LightmassGeoElement(element.Material, element.Settings, true, mapping.CastShadowAsTwoSided || element.Material.TwoSided, false));
        mesh.Indices = mapping.Indices.ToArray();
        mesh.TriangleElements = mapping.TriangleElements.ToArray();
        return mesh;
    }

    // ================= terrain =================

    /// <summary>
    /// A terrain mapping's visible quads (with the expanded border), each split along the diagonal its orientation flag gives. A vertex's normal is from
    /// the height field's central differences (tangents (1, 0, dZ/dX) and (0, 1, dZ/dY) in terrain space). Its lightmap UV puts the section-local vertex
    /// (x, y) at the centre of texel ((x + ExpandQuads) · StaticLightingResolution), so the (N·Resolution + 1)-texel lightmap's texels sit on the
    /// vertices from one edge of the expanded section to the other.
    /// </summary>
    public static LightmassGeoMesh FromTerrain(LightmassTerrainMapping mapping, uint instanceId)
    {
        var field = mapping.Terrain.HeightField;
        int res = mapping.Terrain.StaticLightingResolution;
        var l2w = mapping.LocalToWorld;
        Matrix4x4.Invert(l2w, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        var mesh = new LightmassGeoMesh { Instance = instanceId };
        mesh.Elements.Add(new LightmassGeoElement(mapping.Element.Material, mapping.Element.Settings, mapping.CastShadow, mapping.Element.Material.TwoSided,
            mapping.SelfShadowOnly));
        var vertices = new List<LightmassGeoVertex>();
        var index = new Dictionary<(int, int), int>();
        int Vertex(int x, int y)
        {
            if (index.TryGetValue((x, y), out int i)) return i;
            int gx = mapping.SectionBaseX + x, gy = mapping.SectionBaseY + y;
            var gradient = field.Gradient(gx, gy);
            var tx = new Vector3(1, 0, gradient.X);
            var ty = new Vector3(0, 1, gradient.Y);
            var tz = Vector3.Cross(tx, ty);
            vertices.Add(new LightmassGeoVertex
            {
                Position = LightmassSceneBuilder.TerrainVertex(mapping, x, y),
                TangentX = SafeNormalize(Vector3.TransformNormal(tx, l2w)),
                TangentY = SafeNormalize(Vector3.TransformNormal(ty, l2w)),
                TangentZ = SafeNormalize(Vector3.TransformNormal(tz, normalMatrix)),
                MaterialUV = new Vector2(gx, gy) / MathF.Max(1, MathF.Max(field.NumVerticesX, field.NumVerticesY)),
                LightmapUV = new Vector2(((x + mapping.ExpandQuadsX) * res + 0.5f) / mapping.SizeX, ((y + mapping.ExpandQuadsY) * res + 0.5f) / mapping.SizeY),
            });
            return index[(x, y)] = vertices.Count - 1;
        }
        var indices = new List<int>(mapping.Quads.Count * 6);
        foreach (var (x, y) in mapping.Quads)
        {
            int v00 = Vertex(x, y), v10 = Vertex(x + 1, y), v01 = Vertex(x, y + 1), v11 = Vertex(x + 1, y + 1);
            if (field.IsQuadFlipped(mapping.SectionBaseX + x, mapping.SectionBaseY + y)) indices.AddRange([v00, v01, v10, v10, v01, v11]);
            else indices.AddRange([v00, v01, v11, v00, v11, v10]);
        }
        mesh.Vertices = vertices.ToArray();
        mesh.Indices = indices.ToArray();
        mesh.TriangleElements = new int[mesh.Indices.Length / 3];
        return mesh;
    }

    // ================= solver scene =================

    /// <summary>
    /// Adds the mesh's triangles to the solver's scene. Each triangle is wound so that (V1 − V0) × (V2 − V0) points to the side its vertex normals are
    /// on (its front); a degenerate one is left out.
    /// </summary>
    public void AddTo(SolverScene scene, Func<LightmassGeoElement, uint> material)
    {
        uint baseVertex = (uint)scene.Vertices.Count;
        foreach (var v in Vertices)
            scene.Vertices.Add(new SolverVertex { Position = v.Position, Normal = v.TangentZ, MaterialUV = v.MaterialUV });
        var materials = new uint[Elements.Count];
        for (int e = 0; e < Elements.Count; e++) materials[e] = material(Elements[e]);
        for (int t = 0; t < TriangleCount; t++)
        {
            int e = TriangleElements[t];
            if (e < 0 || e >= Elements.Count) continue;
            int a = Indices[t * 3], b = Indices[t * 3 + 1], c = Indices[t * 3 + 2];
            var pa = Vertices[a].Position;
            var face = Vector3.Cross(Vertices[b].Position - pa, Vertices[c].Position - pa);
            if (!(face.LengthSquared() > 1e-20f)) continue;
            if (Vector3.Dot(face, Vertices[a].TangentZ + Vertices[b].TangentZ + Vertices[c].TangentZ) < 0) (b, c) = (c, b);
            var element = Elements[e];
            var flags = SolverTriangleFlags.None;
            if (element.CastShadow) flags |= SolverTriangleFlags.CastShadow;
            if (element.TwoSidedShadow) flags |= SolverTriangleFlags.TwoSidedShadow;
            if (element.SelfShadowOnly) flags |= SolverTriangleFlags.SelfShadowOnly;
            if (element.Settings.ShadowIndirectOnly) flags |= SolverTriangleFlags.ShadowIndirectOnly;
            scene.Triangles.Add(new SolverTriangle
            {
                V0 = baseVertex + (uint)a, V1 = baseVertex + (uint)b, V2 = baseVertex + (uint)c,
                Material = materials[e], Instance = Instance, Flags = flags,
            });
        }
    }

    internal static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-20f && float.IsFinite(v.X + v.Y + v.Z) ? Vector3.Normalize(v) : Vector3.Zero;
}
