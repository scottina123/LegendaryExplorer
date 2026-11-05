using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Unreal;

/// <summary>A right-handed work plane in Unreal world coordinates.</summary>
public readonly record struct ModelDrawingPlane(Vector3 Origin, Vector3 U, Vector3 V)
{
    public Vector3 Normal => Vector3.Normalize(Vector3.Cross(U, V));
    public Vector3 Point(Vector2 corner, float depth = 0) => Origin + U * corner.X + V * corner.Y + Normal * depth;

    public static ModelDrawingPlane FromNormal(Vector3 origin, Vector3 normal)
    {
        if (!IsFinite(origin) || !IsFinite(normal) || normal.LengthSquared() < 0.00001f)
            throw new ArgumentException("The drawing plane needs a finite, nonzero normal.");
        normal = Vector3.Normalize(normal);
        Vector3 u = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY, normal));
        return new ModelDrawingPlane(origin, u, Vector3.Cross(normal, u));
    }

    public static ModelDrawingPlane Create(Vector3 origin, int axis = 0, float pitchDegrees = 0, float yawDegrees = 0, float rollDegrees = 0)
    {
        if (!IsFinite(origin) || !float.IsFinite(pitchDegrees) || !float.IsFinite(yawDegrees) || !float.IsFinite(rollDegrees))
            throw new ArgumentException("Plane position and angles must be finite.");
        Vector3 u = axis == 2 ? Vector3.UnitY : Vector3.UnitX;
        Vector3 v = axis == 0 ? Vector3.UnitY : Vector3.UnitZ;
        const float radians = MathF.PI / 180;
        Matrix4x4 rotation = Matrix4x4.CreateRotationX(rollDegrees * radians)
                             * Matrix4x4.CreateRotationY(pitchDegrees * radians) * Matrix4x4.CreateRotationZ(yawDegrees * radians);
        return new ModelDrawingPlane(origin, Vector3.TransformNormal(u, rotation), Vector3.TransformNormal(v, rotation));
    }

    public bool IntersectRay(Vector3 rayOrigin, Vector3 direction, out Vector2 corner)
    {
        corner = default;
        float denominator = Vector3.Dot(direction, Normal);
        if (MathF.Abs(denominator) < 0.00001f) return false;
        float distance = Vector3.Dot(Origin - rayOrigin, Normal) / denominator;
        if (distance < 0 || !float.IsFinite(distance)) return false;
        Vector3 relative = rayOrigin + direction * distance - Origin;
        corner = new Vector2(Vector3.Dot(relative, U), Vector3.Dot(relative, V));
        return float.IsFinite(corner.X) && float.IsFinite(corner.Y);
    }

    public static Vector2 Snap(Vector2 point, float step) => step > 0 && float.IsFinite(step)
        ? new Vector2(MathF.Round(point.X / step) * step, MathF.Round(point.Y / step) * step) : point;

    public (Model Model, Polys Polys) Extrude(IReadOnlyList<Vector2> corners, float height)
    {
        var result = ModelGeometry.CreatePrism(corners, 0, height);
        Model model = result.Model;
        ModelDrawingPlane plane = this;
        Vector3 TransformPoint(Vector3 p) => plane.Point(new Vector2(p.X, p.Y), p.Z);
        Vector3 TransformVector(Vector3 p) => plane.U * p.X + plane.V * p.Y + plane.Normal * p.Z;
        model.Points = model.Points.Select(TransformPoint).ToArray();
        model.Vectors = model.Vectors.Select(TransformVector).ToArray();
        foreach (ModelVertex vertex in model.VertexBuffer) vertex.Position = TransformPoint(vertex.Position);
        foreach (BspSurf surface in model.Surfs)
        {
            Vector3 normal = TransformVector(surface.Plane.Normal);
            surface.Plane = new Plane(normal, Vector3.Dot(normal, model.Points[surface.pBase]));
        }
        foreach (BspNode node in model.Nodes) node.Plane = model.Surfs[node.iSurf].Plane;
        foreach (Poly poly in result.Polys.Elements)
        {
            poly.Base = TransformPoint(poly.Base);
            poly.Normal = TransformVector(poly.Normal);
            poly.TextureU = TransformVector(poly.TextureU);
            poly.TextureV = TransformVector(poly.TextureV);
            poly.Vertices = poly.Vertices.Select(TransformPoint).ToArray();
        }
        Vector3 min = model.Points.Aggregate(Vector3.Min), max = model.Points.Aggregate(Vector3.Max);
        if (!IsFinite(min) || !IsFinite(max)) throw new ArgumentException("The drawing extends beyond finite coordinates.");
        model.Bounds = new BoxSphereBounds { Origin = (min + max) / 2, BoxExtent = (max - min) / 2, SphereRadius = ((max - min) / 2).Length() };
        return result;
    }

    /// <summary>Triangular prisms retain concave notches when cooked as convex collision.</summary>
    public List<Vector3[]> CollisionHulls(IReadOnlyList<Vector2> corners, float height)
    {
        _ = ModelGeometry.CreatePrism(corners, 0, height); // Use exactly the model's outline validation.
        int[] triangles = ModelGeometry.Triangulate(corners.Select(p => new Vector3(p, 0)).ToArray());
        var hulls = new List<Vector3[]>();
        for (int i = 0; i < triangles.Length; i += 3)
            hulls.Add([
                Point(corners[triangles[i]]), Point(corners[triangles[i + 1]]), Point(corners[triangles[i + 2]]),
                Point(corners[triangles[i]], height), Point(corners[triangles[i + 1]], height), Point(corners[triangles[i + 2]], height)
            ]);
        return hulls;
    }

    private static bool IsFinite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
