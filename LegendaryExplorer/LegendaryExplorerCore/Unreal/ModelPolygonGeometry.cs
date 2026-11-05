using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Unreal;

public static partial class ModelGeometry
{
    private static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
        ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);

    private static double SignedArea(IReadOnlyList<Vector2> points) => Enumerable.Range(0, points.Count)
        .Sum(i => (double)points[i].X * points[(i + 1) % points.Count].Y - (double)points[i].Y * points[(i + 1) % points.Count].X) / 2;

    private static bool OnSegment(Vector2 a, Vector2 b, Vector2 p) => Math.Abs(Cross(a, b, p)) < 0.000001
        && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);

    private static void ValidateOutline(IReadOnlyList<Vector2> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 a = points[i], b = points[(i + 1) % points.Count];
            if (Vector2.DistanceSquared(a, b) < 0.000001f)
                throw new ArgumentException("Two corners overlap. Move or undo the duplicate corner.");
            for (int j = i + 1; j < points.Count; j++)
            {
                if (j == i + 1 || i == 0 && j == points.Count - 1) continue;
                Vector2 c = points[j], d = points[(j + 1) % points.Count];
                if (Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0
                    || OnSegment(a, b, c) || OnSegment(a, b, d) || OnSegment(c, d, a) || OnSegment(c, d, b))
                    throw new ArgumentException($"Edges {i + 1} and {j + 1} cross or overlap. Draw corners around the perimeter.");
            }
        }
        if (Math.Abs(SignedArea(points)) < 0.000001) throw new ArgumentException("The outline has no area. Move the corners apart.");
    }

    /// <summary>Ear clipping preserves concave boundaries, unlike a triangle fan.</summary>
    public static int[] Triangulate(IReadOnlyList<Vector3> vertices)
    {
        if (vertices.Count < 3) return [];
        Vector3 normal = Vector3.Zero;
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector3 a = vertices[i], b = vertices[(i + 1) % vertices.Count];
            normal.X += (a.Y - b.Y) * (a.Z + b.Z);
            normal.Y += (a.Z - b.Z) * (a.X + b.X);
            normal.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        Vector3 n = Vector3.Abs(normal);
        Vector2[] projected = vertices.Select(p => n.Z >= n.X && n.Z >= n.Y ? new Vector2(p.X, p.Y)
            : n.Y >= n.X ? new Vector2(p.X, p.Z) : new Vector2(p.Y, p.Z)).ToArray();
        return Triangulate2D(projected);
    }

    private static int[] Triangulate2D(IReadOnlyList<Vector2> points)
    {
        double winding = Math.Sign(SignedArea(points));
        if (winding == 0) throw new ArgumentException("Cannot triangulate an outline with no area.");
        var remaining = Enumerable.Range(0, points.Count).ToList();
        var result = new List<int>();
        while (remaining.Count > 3)
        {
            bool clipped = false;
            for (int j = 0; j < remaining.Count; j++)
            {
                int a = remaining[(j + remaining.Count - 1) % remaining.Count], b = remaining[j], c = remaining[(j + 1) % remaining.Count];
                double turn = Cross(points[a], points[b], points[c]) * winding;
                if (Math.Abs(turn) < 0.000001) { remaining.RemoveAt(j); clipped = true; break; }
                if (turn < 0) continue;
                if (remaining.Any(p => p != a && p != b && p != c
                    && Cross(points[a], points[b], points[p]) * winding >= -0.000001
                    && Cross(points[b], points[c], points[p]) * winding >= -0.000001
                    && Cross(points[c], points[a], points[p]) * winding >= -0.000001)) continue;
                result.AddRange([a, b, c]);
                remaining.RemoveAt(j);
                clipped = true;
                break;
            }
            if (!clipped) throw new ArgumentException("Cannot triangulate this outline. Check for crossed edges or overlapping corners.");
        }
        if (remaining.Count == 3 && Math.Abs(Cross(points[remaining[0]], points[remaining[1]], points[remaining[2]])) > 0.000001)
            result.AddRange(remaining);
        return result.ToArray();
    }

    private sealed record Fragment(Vector3[] Vertices, int Surface);

    // A chain of convex half-spaces cannot represent a concave solid. Partition the
    // boundary polygons by each splitter, retaining both front and back subtrees.
    private static void BuildBsp(List<Vector3[]> faces, List<BspSurf> surfaces, List<Vector3> points,
        List<Vert> verts, List<ModelVertex> renderVertices, List<BspNode> nodes)
    {
        int AddNode(Fragment polygon)
        {
            int index = nodes.Count;
            if (index > 65535) throw new ArgumentException("This outline produces too many BSP fragments. Simplify it.");
            nodes.Add(new BspNode {
                Plane = surfaces[polygon.Surface].Plane, iVertPool = verts.Count, iSurf = polygon.Surface,
                iVertexIndex = renderVertices.Count, NumVertices = (byte)polygon.Vertices.Length,
                iFront = -1, iBack = -1, iPlane = -1, iCollisionBound = -1, iLeaf0 = -1, iLeaf1 = -1,
                ComponentIndex = ushort.MaxValue, ComponentElementIndex = -1
            });
            foreach (Vector3 position in polygon.Vertices)
            {
                verts.Add(new Vert { pVertex = points.Count, iSide = -1 });
                points.Add(position);
                renderVertices.Add(new ModelVertex { Position = position });
            }
            return index;
        }

        int Build(List<Fragment> polygons)
        {
            if (polygons.Count == 0) return -1;
            Plane plane = surfaces[polygons[0].Surface].Plane;
            var coplanar = new List<Fragment>();
            var front = new List<Fragment>();
            var back = new List<Fragment>();
            foreach (Fragment polygon in polygons)
            {
                if (polygon.Surface == polygons[0].Surface) { coplanar.Add(polygon); continue; }
                float[] distances = polygon.Vertices.Select(v => Vector3.Dot(plane.Normal, v) - plane.D).ToArray();
                bool hasFront = distances.Any(d => d > 0.001f), hasBack = distances.Any(d => d < -0.001f);
                if (!hasFront && !hasBack) coplanar.Add(polygon);
                else if (!hasBack) front.Add(polygon);
                else if (!hasFront) back.Add(polygon);
                else
                {
                    var f = new List<Vector3>(); var b = new List<Vector3>();
                    for (int i = 0; i < polygon.Vertices.Length; i++)
                    {
                        int next = (i + 1) % polygon.Vertices.Length;
                        Vector3 a = polygon.Vertices[i], end = polygon.Vertices[next];
                        float da = distances[i], db = distances[next];
                        if (da >= -0.001f) f.Add(a);
                        if (da <= 0.001f) b.Add(a);
                        if (da > 0.001f && db < -0.001f || da < -0.001f && db > 0.001f)
                        {
                            Vector3 split = Vector3.Lerp(a, end, da / (da - db));
                            f.Add(split); b.Add(split);
                        }
                    }
                    if (f.Count >= 3) front.Add(new Fragment(f.ToArray(), polygon.Surface));
                    if (b.Count >= 3) back.Add(new Fragment(b.ToArray(), polygon.Surface));
                }
            }
            int root = AddNode(coplanar[0]), last = root;
            foreach (var polygon in coplanar.Skip(1)) { int index = AddNode(polygon); nodes[last].iPlane = index; last = index; }
            nodes[root].iFront = Build(front);
            nodes[root].iBack = Build(back);
            return root;
        }
        Build(faces.Select((f, i) => new Fragment(f, i)).ToList());
    }
}
