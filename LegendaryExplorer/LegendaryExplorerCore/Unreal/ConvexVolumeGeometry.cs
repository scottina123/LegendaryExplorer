using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Unreal.PhysX;

namespace LegendaryExplorerCore.Unreal;

/// <summary>Rebuilds edited convex collision pieces and their union's exterior boundary.</summary>
public static class ConvexVolumeGeometry
{
    private sealed record TriangleFace(int A, int B, int C, Plane Plane);

    public static KConvexElem BuildHull(IEnumerable<Vector3> input)
    {
        Vector3[] points = input.Distinct().ToArray();
        if (points.Length < 4 || points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            throw new ArgumentException("Each collision piece needs at least four finite, non-coplanar vertices.");
        if (points.Any(p => MathF.Abs(p.X) > 100_000_000 || MathF.Abs(p.Y) > 100_000_000 || MathF.Abs(p.Z) > 100_000_000))
            throw new ArgumentException("A collision vertex is outside the supported coordinate range.");
        float scale = (points.Aggregate(Vector3.Max) - points.Aggregate(Vector3.Min)).Length();
        float epsilon = MathF.Max(scale * 0.000001f, 0.00001f);
        int a = 0, b = Enumerable.Range(1, points.Length - 1).MaxBy(i => Vector3.DistanceSquared(points[a], points[i]));
        Vector3 ab = points[b] - points[a];
        int c = Enumerable.Range(0, points.Length).MaxBy(i => Vector3.Cross(ab, points[i] - points[a]).LengthSquared());
        Vector3 normal = Vector3.Cross(ab, points[c] - points[a]);
        if (normal.LengthSquared() < epsilon * epsilon) throw new ArgumentException("A collision piece has collapsed to a line.");
        normal = Vector3.Normalize(normal);
        int d = Enumerable.Range(0, points.Length).MaxBy(i => MathF.Abs(Vector3.Dot(normal, points[i] - points[a])));
        if (MathF.Abs(Vector3.Dot(normal, points[d] - points[a])) < epsilon)
            throw new ArgumentException("A collision piece has collapsed to a flat plane.");
        Vector3 interior = (points[a] + points[b] + points[c] + points[d]) / 4;
        var faces = new List<TriangleFace>();
        void AddFace(int i, int j, int k)
        {
            Vector3 n = Vector3.Cross(points[j] - points[i], points[k] - points[i]);
            if (n.LengthSquared() < epsilon * epsilon) return;
            n = Vector3.Normalize(n);
            if (Vector3.Dot(n, interior - points[i]) > 0) { (j, k) = (k, j); n = -n; }
            faces.Add(new TriangleFace(i, j, k, new Plane(n, Vector3.Dot(n, points[i]))));
        }
        AddFace(a, b, c); AddFace(a, d, b); AddFace(a, c, d); AddFace(b, d, c);
        var seeds = new HashSet<int> { a, b, c, d };
        for (int p = 0; p < points.Length; p++)
        {
            if (seeds.Contains(p)) continue;
            var visible = faces.Where(f => Vector3.Dot(f.Plane.Normal, points[p]) - f.Plane.D > epsilon).ToArray();
            if (visible.Length == 0) continue;
            var edges = new Dictionary<(int, int), (int A, int B, int Count)>();
            foreach (var face in visible)
            {
                foreach (var edge in new[] { (face.A, face.B), (face.B, face.C), (face.C, face.A) })
                {
                    var key = (Math.Min(edge.Item1, edge.Item2), Math.Max(edge.Item1, edge.Item2));
                    edges[key] = edges.TryGetValue(key, out var old) ? (old.A, old.B, old.Count + 1) : (edge.Item1, edge.Item2, 1);
                }
                faces.Remove(face);
            }
            foreach (var edge in edges.Values.Where(e => e.Count == 1)) AddFace(edge.A, edge.B, p);
        }
        int[] used = faces.SelectMany(f => new[] { f.A, f.B, f.C }).Distinct().Order().ToArray();
        if (used.Length > 256) throw new ArgumentException("A collision piece exceeds the engine's 256-vertex limit.");
        var remap = used.Select((index, i) => (index, i)).ToDictionary(p => p.index, p => p.i);
        var hull = new KConvexElem();
        hull.VertexData.AddRange(used.Select(i => points[i]));
        foreach (var point in hull.VertexData) hull.ElemBox.Add(point);
        foreach (var face in faces)
        {
            hull.FaceTriData.AddRange([remap[face.A], remap[face.C], remap[face.B]]); // UE clockwise winding.
            AddDirection(hull.FaceNormalDirections, face.Plane.Normal);
            if (!hull.FacePlaneData.Any(p => Vector3.Dot(p.Normal, face.Plane.Normal) > 0.99999f && MathF.Abs(p.D - face.Plane.D) < epsilon)) hull.FacePlaneData.Add(face.Plane);
            AddDirection(hull.EdgeDirections, Vector3.Normalize(points[face.B] - points[face.A]));
            AddDirection(hull.EdgeDirections, Vector3.Normalize(points[face.C] - points[face.B]));
            AddDirection(hull.EdgeDirections, Vector3.Normalize(points[face.A] - points[face.C]));
        }
        for (int i = 0; i < hull.VertexData.Count; i += 4)
        {
            Vector3 v0 = hull.VertexData[i], v1 = i + 1 < hull.VertexData.Count ? hull.VertexData[i + 1] : v0;
            Vector3 v2 = i + 2 < hull.VertexData.Count ? hull.VertexData[i + 2] : v0, v3 = i + 3 < hull.VertexData.Count ? hull.VertexData[i + 3] : v0;
            hull.PermutedVertexData.Add(new Plane(v0.X, v1.X, v2.X, v3.X));
            hull.PermutedVertexData.Add(new Plane(v0.Y, v1.Y, v2.Y, v3.Y));
            hull.PermutedVertexData.Add(new Plane(v0.Z, v1.Z, v2.Z, v3.Z));
        }
        return hull;
    }

    private static void AddDirection(List<Vector3> list, Vector3 direction)
    {
        if (!list.Any(d => MathF.Abs(Vector3.Dot(d, direction)) > 0.99999f)) list.Add(direction);
    }

    public static List<Vector3[]> ExteriorFaces(IReadOnlyList<KConvexElem> hulls)
    {
        var result = new List<Vector3[]>();
        for (int h = 0; h < hulls.Count; h++)
        {
            var hull = hulls[h];
            for (int i = 0; i < hull.FaceTriData.Count; i += 3)
            {
                Vector3[] face = [hull.VertexData[hull.FaceTriData[i]], hull.VertexData[hull.FaceTriData[i + 2]], hull.VertexData[hull.FaceTriData[i + 1]]];
                Vector3 normal = Vector3.Normalize(Vector3.Cross(face[1] - face[0], face[2] - face[0]));
                var fragments = new List<Vector3[]> { face };
                for (int other = 0; other < hulls.Count && fragments.Count > 0; other++)
                {
                    if (other == h) continue;
                    var exterior = new List<Vector3[]>();
                    foreach (var fragment in fragments)
                    {
                        var inside = new List<Vector3[]> { fragment };
                        foreach (var plane in hulls[other].FacePlaneData)
                        {
                            var remaining = new List<Vector3[]>();
                            foreach (var polygon in inside)
                            {
                                // Sample just outside this face: touching interior faces disappear,
                                // while coincident exterior faces retain their boundary.
                                float offset = Vector3.Dot(normal, plane.Normal) * 0.0001f;
                                Split(polygon, plane, offset, out var front, out var back);
                                if (front.Length >= 3) exterior.Add(front);
                                if (back.Length >= 3) remaining.Add(back);
                            }
                            inside = remaining;
                            if (inside.Count == 0) break;
                        }
                    }
                    fragments = exterior;
                }
                result.AddRange(fragments);
            }
        }
        return result;
    }

    private static void Split(Vector3[] polygon, Plane plane, float offset, out Vector3[] front, out Vector3[] back)
    {
        var f = new List<Vector3>(); var b = new List<Vector3>();
        float[] distances = polygon.Select(p => Vector3.Dot(plane.Normal, p) - plane.D).ToArray();
        for (int i = 0; i < polygon.Length; i++)
        {
            int next = (i + 1) % polygon.Length;
            float da = distances[i], db = distances[next];
            bool outside = da + offset > 0.00001f, nextOutside = db + offset > 0.00001f;
            (outside ? f : b).Add(polygon[i]);
            if (outside != nextOutside && MathF.Abs(da - db) > 0.0000001f)
            {
                var point = Vector3.Lerp(polygon[i], polygon[next], Math.Clamp(da / (da - db), 0, 1));
                f.Add(point); b.Add(point);
            }
        }
        front = Clean(f); back = Clean(b);
    }

    private static Vector3[] Clean(List<Vector3> points)
    {
        for (int i = points.Count - 1; i >= 0 && points.Count > 1; i--)
            if (Vector3.DistanceSquared(points[i], points[(i + 1) % points.Count]) < 0.00000001f) points.RemoveAt(i);
        bool changed = true;
        while (changed && points.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < points.Count; i++)
                if (Vector3.Cross(points[i] - points[(i + points.Count - 1) % points.Count], points[(i + 1) % points.Count] - points[i]).LengthSquared() < 0.00000001f)
                { points.RemoveAt(i); changed = true; break; }
        }
        return points.Count >= 3 ? points.ToArray() : [];
    }
}
