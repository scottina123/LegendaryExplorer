using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Unreal;

/// <summary>Geometry shared by the BSP/brush preview and the convex Model drawing tool.</summary>
public static partial class ModelGeometry
{
    public sealed record Face(Vector3[] Vertices, Vector3 Normal, int Material);

    /// <summary>Brush Models often have no ModelComponents or render vertex buffer.</summary>
    public static List<Face> GetFaces(Model model, IEnumerable<int> nodeIndices = null, bool includeCookedBrushes = true)
    {
        var faces = new List<Face>();
        foreach (int index in nodeIndices ?? Enumerable.Range(0, model.Nodes.Length))
        {
            if ((uint)index >= model.Nodes.Length) continue;
            BspNode node = model.Nodes[index];
            if (node.NumVertices < 3) continue;
            var vertices = new Vector3[node.NumVertices];
            bool valid = true;
            for (int i = 0; i < vertices.Length; i++)
            {
                int poolIndex = node.iVertPool + i;
                if ((uint)poolIndex >= model.Verts.Length || (uint)model.Verts[poolIndex].pVertex >= model.Points.Length)
                {
                    valid = false;
                    break;
                }
                vertices[i] = model.Points[model.Verts[poolIndex].pVertex];
            }
            if (!valid) continue;
            int material = (uint)node.iSurf < model.Surfs.Length ? model.Surfs[node.iSurf].Material : 0;
            faces.Add(new Face(vertices, node.Plane.Normal, material));
        }

        // Editor brush geometry can survive in Polys even when the BSP arrays are empty.
        if (faces.Count == 0 && nodeIndices == null && model.Export?.FileRef.TryGetUExport(model.Polys, out var polysExport) == true
            && polysExport.ClassName == "Polys")
        {
            foreach (Poly poly in ObjectBinary.From<Polys>(polysExport).Elements)
                if (poly.Vertices.Length >= 3)
                    faces.Add(new Face(poly.Vertices, poly.Normal, poly.Material));
        }
        if (faces.Count == 0 && nodeIndices == null && includeCookedBrushes && model.Export != null)
            faces.AddRange(GetCookedBrushFaces(model.Export));
        return faces;
    }

    private static ExportEntry Resolve(IEntry entry, PackageCache cache) => entry switch {
        ExportEntry export => export,
        ImportEntry import => EntryImporter.ResolveImport(import, cache),
        _ => null
    };

    private static IEnumerable<IEntry> GetActorSources(ExportEntry actor, PropertyCollection props)
    {
        foreach (var reference in props.OfType<ObjectProperty>())
            if (actor.FileRef.GetEntry(reference.Value) is { } entry && IsModelSource(entry)) yield return entry;
        foreach (var array in props.OfType<ArrayProperty<ObjectProperty>>())
            foreach (var reference in array)
                if (actor.FileRef.GetEntry(reference.Value) is { } entry && IsModelSource(entry)) yield return entry;
        foreach (var child in actor.FileRef.Exports.Where(e => e.Parent == actor && !e.IsDefaultObject && IsModelSource(e)))
            yield return child;
    }

    private static bool IsModelSource(IEntry entry) => entry.ClassName is "Model" or "ModelComponent" || entry.IsA("BrushComponent");

    public static bool HasActorGeometry(ExportEntry actor) => !actor.IsDefaultObject && actor.IsA("Actor")
        && GetActorSources(actor, actor.GetCondensedProperties()).Any();

    /// <summary>Returns only this actor's geometry in actor-local space. In particular,
    /// never expands a shared cooked Model into the other volumes that reference it.</summary>
    public static List<Face> GetActorFaces(ExportEntry actor, PackageCache cache = null)
    {
        var faces = new List<Face>();
        var sources = GetActorSources(actor, actor.GetCondensedProperties(cache)).Distinct()
            .Select(e => Resolve(e, cache)).Where(e => e != null).ToArray();
        foreach (ExportEntry source in sources.Where(e => e.ClassName != "Model"))
        {
            var props = source.GetCondensedProperties(cache);
            var localFaces = GetAggregateFaces(props.GetProp<StructProperty>("BrushAggGeom"));
            if (localFaces.Count == 0)
            {
                int modelIndex = source.ClassName == "ModelComponent" ? ObjectBinary.From<ModelComponent>(source).Model
                    : props.GetProp<ObjectProperty>("Brush")?.Value ?? 0;
                if (Resolve(source.FileRef.GetEntry(modelIndex), cache) is { ClassName: "Model" } modelExport)
                {
                    Model model = ObjectBinary.From<Model>(modelExport);
                    localFaces = source.ClassName == "ModelComponent"
                        ? GetFaces(model, ObjectBinary.From<ModelComponent>(source).Elements.SelectMany(e => e.Nodes).Select(i => (int)i), false)
                        : GetFaces(model, includeCookedBrushes: false);
                }
            }
            Matrix4x4 transform = ComponentTransform(props);
            faces.AddRange(TransformFaces(localFaces, transform));
        }
        // Brush/volume actors usually reference both their Model and BrushComponent.
        // Prefer the component's cooked geometry; adding the Model would duplicate it.
        if (faces.Count == 0)
            foreach (ExportEntry model in sources.Where(e => e.ClassName == "Model"))
                faces.AddRange(GetFaces(ObjectBinary.From<Model>(model), includeCookedBrushes: false));
        return faces;
    }

    public static List<Face> GetAggregateFaces(StructProperty aggregate)
    {
        var faces = new List<Face>();
        if (aggregate?.GetProp<ArrayProperty<StructProperty>>("ConvexElems") is not { } elements) return faces;
        foreach (StructProperty convex in elements)
        {
            var vertexData = convex.GetProp<ArrayProperty<StructProperty>>("VertexData");
            var indices = convex.GetProp<ArrayProperty<IntProperty>>("FaceTriData");
            if (vertexData == null || indices == null) continue;
            Vector3[] vertices = vertexData.Select(CommonStructs.GetVector3).ToArray();
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int a = indices[i].Value, b = indices[i + 1].Value, c = indices[i + 2].Value;
                if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length) continue;
                Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                if (normal.LengthSquared() < 0.000001f) continue;
                faces.Add(new Face([vertices[a], vertices[b], vertices[c]], Vector3.Normalize(normal), 0));
            }
        }
        return faces;
    }

    private static Matrix4x4 ComponentTransform(PropertyCollection props)
    {
        Vector3 scale = (props.GetProp<FloatProperty>("Scale")?.Value ?? 1) *
            (props.GetProp<StructProperty>("Scale3D") is { } s ? CommonStructs.GetVector3(s) : Vector3.One);
        Vector3 translation = props.GetProp<StructProperty>("Translation") is { } t ? CommonStructs.GetVector3(t) : Vector3.Zero;
        Rotator rotation = props.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : default;
        return ActorUtils.ComposeLocalToWorld(translation, rotation, scale);
    }

    private static IEnumerable<Face> TransformFaces(IEnumerable<Face> faces, Matrix4x4 transform)
    {
        foreach (Face face in faces)
        {
            Vector3[] vertices = face.Vertices.Select(v => Vector3.Transform(v, transform)).ToArray();
            Vector3 normal = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]);
            if (normal.LengthSquared() > 0.000001f)
                yield return new Face(vertices, Vector3.Normalize(normal), face.Material);
        }
    }

    /// <summary>Cooked volume Models can be empty shared placeholders. Their surviving
    /// geometry is stored on each referring BrushComponent, in that volume's local space.</summary>
    private static IEnumerable<Face> GetCookedBrushFaces(ExportEntry modelExport)
    {
        foreach (ExportEntry component in modelExport.FileRef.Exports.Where(e => e.ClassName == "BrushComponent" && !e.IsDefaultObject))
        {
            var props = component.GetProperties();
            if (props.GetProp<ObjectProperty>("Brush")?.Value != modelExport.UIndex) continue;
            var convexElements = props.GetProp<StructProperty>("BrushAggGeom")?.GetProp<ArrayProperty<StructProperty>>("ConvexElems");
            if (convexElements == null) continue;
            Vector3 scale = (props.GetProp<FloatProperty>("Scale")?.Value ?? 1) *
                (props.GetProp<StructProperty>("Scale3D") is { } scaleProp ? CommonStructs.GetVector3(scaleProp) : Vector3.One);
            Vector3 translation = props.GetProp<StructProperty>("Translation") is { } translationProp ? CommonStructs.GetVector3(translationProp) : Vector3.Zero;
            Rotator rotation = props.GetProp<StructProperty>("Rotation") is { } rotationProp ? CommonStructs.GetRotator(rotationProp) : default;
            Matrix4x4 transform = ActorUtils.ComposeLocalToWorld(translation, rotation, scale);
            if (component.Parent is ExportEntry actor && actor.IsA("Actor"))
                transform *= ActorUtils.GetLocalToWorld(actor);
            foreach (StructProperty convex in convexElements)
            {
                var vertexData = convex.GetProp<ArrayProperty<StructProperty>>("VertexData");
                var indices = convex.GetProp<ArrayProperty<IntProperty>>("FaceTriData");
                if (vertexData == null || indices == null) continue;
                Vector3[] vertices = vertexData.Select(p => Vector3.Transform(CommonStructs.GetVector3(p), transform)).ToArray();
                for (int i = 0; i + 2 < indices.Count; i += 3)
                {
                    int a = indices[i].Value, b = indices[i + 1].Value, c = indices[i + 2].Value;
                    if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length) continue;
                    Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                    if (normal.LengthSquared() < 0.000001f) continue;
                    yield return new Face([vertices[a], vertices[b], vertices[c]], Vector3.Normalize(normal), 0);
                }
            }
        }
    }

    /// <summary>Builds a closed prism from a simple polygon, with outward faces and a solid BSP.</summary>
    public static (Model Model, Polys Polys) CreatePrism(IReadOnlyList<Vector2> footprint, float bottom, float height, Vector2 origin = default)
    {
        if (footprint == null || footprint.Count < 3 || footprint.Count > 64)
            throw new ArgumentException("Draw between 3 and 64 corners.");
        if (!float.IsFinite(bottom) || !float.IsFinite(height) || height <= 0 || !float.IsFinite(bottom + height)
            || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y))
            throw new ArgumentException("Origin, base Z and height must be finite; height must be positive.");
        Vector2[] points = footprint.Select(p => p + origin).ToArray();
        if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))
            throw new ArgumentException("Corner coordinates must be finite.");

        ValidateOutline(points);
        if (SignedArea(points) < 0) Array.Reverse(points);
        int[] capTriangles = Triangulate2D(points);
        bool convex = Enumerable.Range(0, points.Length).All(i => Cross(points[i], points[(i + 1) % points.Length], points[(i + 2) % points.Length]) > 0);
        var faces = new List<Vector3[]>();
        if (convex)
        {
            faces.Add(points.Reverse().Select(p => new Vector3(p, bottom)).ToArray());
            faces.Add(points.Select(p => new Vector3(p, bottom + height)).ToArray());
        }
        else
        {
            for (int i = 0; i < capTriangles.Length; i += 3)
            {
                Vector2 a = points[capTriangles[i]], b = points[capTriangles[i + 1]], c = points[capTriangles[i + 2]];
                faces.Add([new(c, bottom), new(b, bottom), new(a, bottom)]);
                faces.Add([new(a, bottom + height), new(b, bottom + height), new(c, bottom + height)]);
            }
        }
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 a = points[i], b = points[(i + 1) % points.Length];
            faces.Add([new(a, bottom), new(b, bottom), new(b, bottom + height), new(a, bottom + height)]);
        }
        Model model = Model.Create();
        Polys polys = Polys.Create();
        var vertices = new List<Vector3>();
        var vectors = new List<Vector3>();
        var verts = new List<Vert>();
        var renderVertices = new List<ModelVertex>();
        var nodes = new List<BspNode>();
        var surfaces = new List<BspSurf>();
        var polygons = new List<Poly>();
        var lightmass = new LightmassPrimitiveSettings {
            FullyOccludedSamplesFraction = 1, EmissiveLightFalloffExponent = 2,
            EmissiveBoost = 1, DiffuseBoost = 1, SpecularBoost = 1
        };
        for (int f = 0; f < faces.Count; f++)
        {
            Vector3[] face = faces[f];
            Vector3 normal = Vector3.Normalize(Vector3.Cross(face[1] - face[0], face[2] - face[0]));
            if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z))
                throw new ArgumentException("The drawing is too large or too thin. Use smaller coordinates or a larger height.");
            Vector3 u = Vector3.Normalize(face[1] - face[0]), v = Vector3.Cross(normal, u);
            int first = vertices.Count, firstVector = vectors.Count;
            // Unreal planes store dot(N, P) in W, rather than System.Numerics' -dot.
            var plane = new Plane(normal, Vector3.Dot(normal, face[0]));
            vectors.AddRange([normal, u, v]);
            surfaces.Add(new BspSurf {
                pBase = first, vNormal = firstVector, vTextureU = firstVector + 1, vTextureV = firstVector + 2,
                iBrushPoly = f, Plane = plane, ShadowMapScale = 32, LightingChannels = 1, iLightmassIndex = 0
            });
            vertices.Add(face[0]);
            polygons.Add(new Poly {
                Base = face[0], Normal = normal, TextureU = u, TextureV = v, Vertices = face,
                ItemName = "None", RulesetVariation = "None", iLink = -1, iBrushPoly = f,
                ShadowMapScale = 32, LightingChannels = 1, LightmassSettings = lightmass
            });
        }
        BuildBsp(faces, surfaces, vertices, verts, renderVertices, nodes);
        Vector3 min = vertices.Aggregate(Vector3.Min), max = vertices.Aggregate(Vector3.Max);
        model.Bounds = new BoxSphereBounds { Origin = (min + max) / 2, BoxExtent = (max - min) / 2, SphereRadius = ((max - min) / 2).Length() };
        if (!float.IsFinite(model.Bounds.SphereRadius)) throw new ArgumentException("The drawing is too large.");
        model.Points = vertices.ToArray();
        model.Vectors = vectors.ToArray();
        model.Verts = verts.ToArray();
        model.Nodes = nodes.ToArray();
        model.Surfs = surfaces.ToArray();
        model.VertexBuffer = renderVertices.ToArray();
        model.NumVertices = (uint)renderVertices.Count;
        model.RootOutside = true;
        model.Linked = true;
        model.NumSharedSides = 4;
        model.LightingGuid = Guid.NewGuid();
        model.LightmassSettings = [lightmass];
        polys.Elements = polygons.ToArray();
        polys.PolyMax = polys.PolyCount;
        return (model, polys);
    }
}
