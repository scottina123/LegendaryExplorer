using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace LegendaryExplorer.Tools.LevelEditor;

internal static class NavigationCollisionGeometry
{
    public static LevelCollisionFlags GetFlags(PrimitiveComponentProxy component)
    {
        PropertyCollection actorProperties = component.Actor.Export.GetCondensedProperties();
        if (IsNoCollision(component.Properties) || IsNoCollision(actorProperties) ||
            IsFalse(component.Properties, "CollideActors", "bCollideActors") ||
            IsFalse(actorProperties, "CollideActors", "bCollideActors"))
        {
            return LevelCollisionFlags.None;
        }

        bool blocksActors = GetBool(component.Properties, "BlockActors", "bBlockActors") ??
                            GetBool(actorProperties, "BlockActors", "bBlockActors") ?? true;
        if (!blocksActors)
        {
            return LevelCollisionFlags.None;
        }

        LevelCollisionFlags flags = LevelCollisionFlags.None;
        if (GetBool(component.Properties, "BlockZeroExtent", "bBlockZeroExtent") ??
            GetBool(actorProperties, "BlockZeroExtent", "bBlockZeroExtent") ?? true)
        {
            flags |= LevelCollisionFlags.BlocksRay;
        }
        if (GetBool(component.Properties, "BlockNonZeroExtent", "bBlockNonZeroExtent") ??
            GetBool(actorProperties, "BlockNonZeroExtent", "bBlockNonZeroExtent") ?? true)
        {
            flags |= LevelCollisionFlags.BlocksShape;
        }
        return flags;
    }

    public static void AppendStaticMesh(List<LevelCollisionTriangle> output, StaticMesh mesh,
        Matrix4x4 localToWorld, LevelCollisionFlags flags, ExportEntry source)
    {
        if (mesh is null || flags == LevelCollisionFlags.None)
        {
            return;
        }

        int initialCount = output.Count;
        AppendAggregateGeometry(output, mesh.GetCollisionMeshProperty(source.FileRef), localToWorld, flags, source);
        if (output.Count > initialCount || mesh.LODModels is not { Length: > 0 })
        {
            return;
        }

        StaticMeshRenderData lod = mesh.LODModels[0];
        Vector3[] vertices = lod.PositionVertexBuffer?.VertexData;
        if (vertices is null || vertices.Length == 0)
        {
            return;
        }

        kDOPCollisionTriangle[] collisionTriangles = source.Game >= MEGame.ME3
            ? mesh.kDOPTreeME3UDKLE?.Triangles
            : mesh.kDOPTreeME1ME2?.Triangles;
        if (collisionTriangles is { Length: > 0 })
        {
            foreach (kDOPCollisionTriangle triangle in collisionTriangles)
            {
                AppendTriangle(output, vertices, triangle.Vertex1, triangle.Vertex2, triangle.Vertex3,
                    localToWorld, flags, source);
            }
            return;
        }

        ushort[] indices = lod.IndexBuffer;
        if (indices is null)
        {
            return;
        }
        foreach (StaticMeshElement element in lod.Elements ?? [])
        {
            if (!element.EnableCollision && !element.OldEnableCollision)
            {
                continue;
            }
            int end = Math.Min(indices.Length, (int)element.FirstIndex + (int)element.NumTriangles * 3);
            for (int index = (int)element.FirstIndex; index + 2 < end; index += 3)
            {
                AppendTriangle(output, vertices, indices[index], indices[index + 1], indices[index + 2],
                    localToWorld, flags, source);
            }
        }
    }

    public static void AppendAggregateGeometry(List<LevelCollisionTriangle> output, StructProperty aggregateGeometry,
        Matrix4x4 localToWorld, LevelCollisionFlags flags, ExportEntry source)
    {
        if (aggregateGeometry?.GetProp<ArrayProperty<StructProperty>>("ConvexElems") is not { } convexElements)
        {
            return;
        }

        foreach (StructProperty convexElement in convexElements)
        {
            ArrayProperty<IntProperty> indices = convexElement.GetProp<ArrayProperty<IntProperty>>("FaceTriData");
            ArrayProperty<StructProperty> vertexProperties = convexElement.GetProp<ArrayProperty<StructProperty>>("VertexData");
            if (indices is null || vertexProperties is null)
            {
                continue;
            }

            var vertices = new Vector3[vertexProperties.Count];
            for (int index = 0; index < vertexProperties.Count; index++)
            {
                vertices[index] = CommonStructs.GetVector3(vertexProperties[index]);
            }
            for (int index = 0; index + 2 < indices.Count; index += 3)
            {
                AppendTriangle(output, vertices, indices[index].Value, indices[index + 1].Value,
                    indices[index + 2].Value, localToWorld, flags, source);
            }
        }
    }

    private static void AppendTriangle(List<LevelCollisionTriangle> output, IReadOnlyList<Vector3> vertices,
        int first, int second, int third, Matrix4x4 localToWorld, LevelCollisionFlags flags, ExportEntry source)
    {
        if ((uint)first >= vertices.Count || (uint)second >= vertices.Count || (uint)third >= vertices.Count)
        {
            return;
        }
        Vector3 a = Vector3.Transform(vertices[first], localToWorld);
        Vector3 b = Vector3.Transform(vertices[second], localToWorld);
        Vector3 c = Vector3.Transform(vertices[third], localToWorld);
        if (LevelCollisionTriangle.TryCreate(a, b, c, flags, source, out LevelCollisionTriangle triangle))
        {
            output.Add(triangle);
        }
    }

    private static bool IsNoCollision(PropertyCollection properties) =>
        properties.GetProp<EnumProperty>("CollisionType")?.Value.Name.Equals(
            "COLLIDE_NoCollision", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsFalse(PropertyCollection properties, params string[] names) =>
        GetBool(properties, names) is false;

    private static bool? GetBool(PropertyCollection properties, params string[] names)
    {
        foreach (string name in names)
        {
            if (properties.GetProp<BoolProperty>(name) is { } property)
                return property.Value;
        }
        return null;
    }
}
