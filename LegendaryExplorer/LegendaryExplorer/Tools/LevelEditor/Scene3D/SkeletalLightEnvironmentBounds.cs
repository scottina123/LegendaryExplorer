using System;
using System.Collections.Generic;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.LevelEditor.Scene3D;

/// <summary>
/// Skeletal component bounds used by UE3's light environment. The rendered vertex bounds remain
/// useful for culling, but are not the component bounds from which the game samples lighting.
/// Physics shapes and their bone assignments are read once; only their posed transforms change.
/// </summary>
internal sealed class SkeletalLightEnvironmentBounds
{
    private enum ShapeType { Box, Sphere, Capsule, Convex }

    private sealed record Shape(int BoneIndex, ShapeType Type, Matrix4x4 Transform,
        Vector3 Extent, float Radius, float HalfLength, Vector3[] Vertices = null);

    private readonly BoxSphereBounds meshBounds;
    private readonly Matrix4x4[] referencePose;
    private readonly Shape[] shapes;
    private Matrix4x4[] cachedPose = [];
    private Matrix4x4 cachedLocalToWorld;
    private BoxSphereBounds cachedBounds;
    private bool hasCachedBounds;

    private SkeletalLightEnvironmentBounds(SkeletalMesh mesh, List<Shape> shapes)
    {
        meshBounds = mesh.Bounds;
        referencePose = BuildReferencePose(mesh.RefSkeleton);
        this.shapes = [.. shapes];
    }

    internal bool HasPhysicsBounds => shapes.Length > 0;

    internal static SkeletalLightEnvironmentBounds Create(MeshRenderContext context,
        ExportEntry component, PropertyCollection componentProperties, SkeletalMesh mesh)
    {
        List<Shape> shapes = [];
        try
        {
            if (componentProperties.GetProp<ObjectProperty>("PhysicsAsset") is { Value: not 0 } assetProperty
                && context.ResolveExportCached(component.FileRef, assetProperty.Value) is { } asset)
            {
                PropertyCollection assetProperties = asset.GetCondensedProperties(context.PackageCache,
                    resolveImports: true, mergeStructs: true);
                if (assetProperties.GetProp<ArrayProperty<ObjectProperty>>("BodySetup") is { } bodies)
                {
                    var selected = assetProperties.GetProp<ArrayProperty<IntProperty>>("BoundsBodies");
                    HashSet<int> selectedBodies = selected is null ? null : [];
                    if (selected is not null)
                        foreach (IntProperty index in selected) selectedBodies.Add(index.Value);
                    bool allBodies = componentProperties.GetProp<BoolProperty>("bConsiderAllBodiesForBounds")?.Value ?? false;
                    Dictionary<string, int> boneIndices = new(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < (mesh.RefSkeleton?.Length ?? 0); i++)
                        boneIndices.TryAdd(mesh.RefSkeleton[i].Name.Instanced, i);

                    for (int i = 0; i < bodies.Count; i++)
                    {
                        if (context.ResolveExportCached(asset.FileRef, bodies[i].Value) is not { } body) continue;
                        PropertyCollection properties = body.GetCondensedProperties(context.PackageCache,
                            resolveImports: true, mergeStructs: true);
                        // BoundsBodies is the cooked selection of bConsiderForBounds bodies. An explicit
                        // empty selection must not accidentally include every body's default value.
                        if (!allBodies && !(selectedBodies?.Contains(i)
                                            ?? properties.GetProp<BoolProperty>("bConsiderForBounds")?.Value ?? true)) continue;
                        if (properties.GetProp<NameProperty>("BoneName") is not { } boneName
                            || !boneIndices.TryGetValue(boneName.Value.Instanced, out int boneIndex)) continue;
                        ReadShapes(properties.GetProp<StructProperty>("AggGeom"), boneIndex, shapes);
                    }
                }
            }
        }
        catch (Exception e)
        {
            // An optional/unreadable physics asset must not prevent a skeletal component from
            // loading. A partial set of bodies would distort the origin, so use authored bounds.
            shapes.Clear();
            System.Diagnostics.Debug.WriteLine($"Could not read skeletal lighting bounds for {component.InstancedFullPath}: {e.Message}");
        }
        return new SkeletalLightEnvironmentBounds(mesh, shapes);
    }

    internal BoxSphereBounds GetBounds(ReadOnlySpan<Matrix4x4> componentPose, Matrix4x4 localToWorld)
    {
        // AnimPlayer mutates its pose array in place. Comparing its contents also covers callers
        // that change a pose without allocating a new array or uploading a new vertex buffer.
        if (hasCachedBounds && cachedLocalToWorld == localToWorld && componentPose.SequenceEqual(cachedPose))
            return cachedBounds;
        if (cachedPose.Length != componentPose.Length) cachedPose = new Matrix4x4[componentPose.Length];
        componentPose.CopyTo(cachedPose);
        cachedLocalToWorld = localToWorld;
        cachedBounds = ComputeBounds(componentPose, localToWorld);
        hasCachedBounds = true;
        return cachedBounds;
    }

    private BoxSphereBounds ComputeBounds(ReadOnlySpan<Matrix4x4> componentPose, Matrix4x4 localToWorld)
    {
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        bool hasBounds = false;
        foreach (Shape shape in shapes)
        {
            Matrix4x4 boneTransform = shape.BoneIndex < componentPose.Length
                ? componentPose[shape.BoneIndex] : referencePose[shape.BoneIndex];
            if (!IsUsableTransform(boneTransform)) continue;
            Matrix4x4 transform = shape.Transform * boneTransform * localToWorld;
            if (!IsUsableTransform(transform)) continue;
            if (shape.Type == ShapeType.Box)
            {
                for (int i = 0; i < 8; i++)
                    AddPoint(Vector3.Transform(shape.Extent * new Vector3(
                        (i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1), transform));
            }
            else if (shape.Type == ShapeType.Convex)
            {
                foreach (Vector3 vertex in shape.Vertices) AddPoint(Vector3.Transform(vertex, transform));
            }
            else
            {
                // UE3's simple collision primitives use the largest scale for their circular radius.
                // A sphyl's Length is its cylinder segment, with a radius added at each end along Z.
                float scale = MathF.Max(Vector3.TransformNormal(Vector3.UnitX, transform).Length(),
                    MathF.Max(Vector3.TransformNormal(Vector3.UnitY, transform).Length(),
                        Vector3.TransformNormal(Vector3.UnitZ, transform).Length()));
                Vector3 radius = new(shape.Radius * scale);
                Vector3 axis = shape.Type == ShapeType.Capsule
                    ? Vector3.TransformNormal(Vector3.UnitZ * shape.HalfLength, transform) : Vector3.Zero;
                AddPoint(Vector3.Min(transform.Translation - axis, transform.Translation + axis) - radius);
                AddPoint(Vector3.Max(transform.Translation - axis, transform.Translation + axis) + radius);
            }
        }
        if (hasBounds)
        {
            Vector3 extent = (maximum - minimum) * 0.5f;
            return new BoxSphereBounds { Origin = minimum + extent, BoxExtent = extent, SphereRadius = extent.Length() };
        }

        // Without usable physics bodies UE3 uses the authored mesh bounds, displaced by the root
        // bone's motion relative to its reference position. Pose-dependent vertex extents do not
        // replace this authored volume or shift its center when limbs/clothing move asymmetrically.
        BoxSphereBounds fallback = meshBounds;
        if (componentPose.Length > 0 && referencePose.Length > 0 && IsUsableTransform(componentPose[0]))
            fallback.Origin += componentPose[0].Translation - referencePose[0].Translation;
        return fallback.TransformBy(localToWorld);

        void AddPoint(Vector3 point)
        {
            if (!IsFinite(point)) return;
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
            hasBounds = true;
        }
    }

    private static void ReadShapes(StructProperty aggregate, int boneIndex, List<Shape> destination)
    {
        if (aggregate is null) return;
        Read("BoxElems", ShapeType.Box);
        Read("SphereElems", ShapeType.Sphere);
        Read("SphylElems", ShapeType.Capsule);
        Read("ConvexElems", ShapeType.Convex);

        void Read(string name, ShapeType type)
        {
            if (aggregate.GetProp<ArrayProperty<StructProperty>>(name) is not { } elements) return;
            foreach (StructProperty element in elements)
            {
                Matrix4x4 transform = element.GetProp<StructProperty>("TM") is { } matrix
                    ? CommonStructs.GetMatrix(matrix) : Matrix4x4.Identity;
                if (!IsUsableTransform(transform)) continue;
                Vector3 extent = new(
                    element.GetProp<FloatProperty>("X")?.Value ?? 0,
                    element.GetProp<FloatProperty>("Y")?.Value ?? 0,
                    element.GetProp<FloatProperty>("Z")?.Value ?? 0);
                float radius = element.GetProp<FloatProperty>("Radius")?.Value ?? 0;
                float length = element.GetProp<FloatProperty>("Length")?.Value ?? 0;
                if (!IsFinite(extent) || !float.IsFinite(radius) || !float.IsFinite(length)
                    || radius < 0 || length < 0) continue;
                Vector3[] vertices = null;
                if (type == ShapeType.Convex)
                {
                    if (element.GetProp<ArrayProperty<StructProperty>>("VertexData") is not { Count: > 0 } data) continue;
                    List<Vector3> points = [];
                    foreach (StructProperty vector in data)
                    {
                        Vector3 point = CommonStructs.GetVector3(vector);
                        if (IsFinite(point)) points.Add(point);
                    }
                    if (points.Count == 0) continue;
                    vertices = [.. points];
                }
                destination.Add(new Shape(boneIndex, type, transform, Vector3.Abs(extent) * 0.5f,
                    radius, length * 0.5f, vertices));
            }
        }
    }

    private static Matrix4x4[] BuildReferencePose(MeshBone[] skeleton)
    {
        Matrix4x4[] pose = new Matrix4x4[skeleton?.Length ?? 0];
        for (int i = 0; i < pose.Length; i++)
        {
            MeshBone bone = skeleton[i];
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(bone.Orientation) * Matrix4x4.CreateTranslation(bone.Position);
            pose[i] = bone.ParentIndex >= 0 && bone.ParentIndex < i ? local * pose[bone.ParentIndex] : local;
        }
        return pose;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsUsableTransform(Matrix4x4 value) =>
        IsFinite(new Vector3(value.M11, value.M12, value.M13))
        && IsFinite(new Vector3(value.M21, value.M22, value.M23))
        && IsFinite(new Vector3(value.M31, value.M32, value.M33))
        && IsFinite(value.Translation) && float.IsFinite(value.M44) && value.M44 != 0;
}
