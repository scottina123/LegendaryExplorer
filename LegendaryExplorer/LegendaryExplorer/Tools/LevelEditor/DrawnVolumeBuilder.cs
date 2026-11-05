using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.PhysX;

namespace LegendaryExplorer.Tools.LevelEditor;

internal static class DrawnVolumeBuilder
{
    internal static ExportEntry Create(ExportEntry levelExport, string name, string actorClass,
        ModelDrawingPlane worldPlane, IReadOnlyList<Vector2> corners, float height)
    {
        if (levelExport.ClassName != "Level" || !levelExport.Game.IsLEGame())
            throw new ArgumentException("Volume collision creation requires a Legendary Edition level.");
        if (actorClass is not ("BlockingVolume" or "TriggerVolume"))
            throw new ArgumentException("Choose BlockingVolume or TriggerVolume.");
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Use a volume name containing letters, numbers and underscores.");

        var localPlane = worldPlane with { Origin = Vector3.Zero };
        var geometry = localPlane.Extrude(corners, height);
        // Prepare the hulls before adding any exports. A concave outline must never be
        // reduced to a single convex hull, which would fill all its notches.
        var convex = new ArrayProperty<StructProperty>("ConvexElems");
        foreach (Vector3[] hull in localPlane.CollisionHulls(corners, height))
            convex.Add(BuildPrismHull(hull).GetStructProperty());
        var aggregate = new StructProperty("KAggregateGeom", new PropertyCollection { convex }, "BrushAggGeom");
        var package = levelExport.FileRef;
        ExportEntry actor = null;
        try
        {
            actor = package.CreateExport(name, actorClass, levelExport, createWithStack: true);
            actor.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            var model = package.CreateExport("Model", "Model", actor);
            var polys = package.CreateExport("Polys", "Polys", model);
            var component = package.CreateExport("BrushComponent", "BrushComponent", actor, prePropBinary: new byte[8]);
            component.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            geometry.Model.Self = model.UIndex;
            geometry.Model.Polys = polys.UIndex;
            geometry.Polys.Owner = model.UIndex;
            foreach (var surface in geometry.Model.Surfs) surface.Actor = actor.UIndex;
            foreach (var polygon in geometry.Polys.Elements) polygon.Actor = actor.UIndex;
            model.WriteBinary(geometry.Model);
            polys.WriteBinary(geometry.Polys);
            bool blocking = actorClass == "BlockingVolume";
            actor.WriteProperties([
                new ObjectProperty(model, "Brush"), new ObjectProperty(component, "BrushComponent"),
                new ObjectProperty(component, "CollisionComponent"),
                new ArrayProperty<ObjectProperty>([new ObjectProperty(component)], "Components"),
                CommonStructs.Vector3Prop(worldPlane.Origin, "location"), new NameProperty(actor.ObjectName, "Tag"),
                new BoolProperty(true, "bCollideActors"), new BoolProperty(blocking, "bBlockActors"),
                new BoolProperty(false, "bHidden")
            ]);
            component.WritePropertiesAndBinary(new PropertyCollection {
                new ObjectProperty(model, "Brush"), aggregate, new ObjectProperty(0, "ReplacementPrimitive"),
                new BoolProperty(true, "CollideActors"), new BoolProperty(blocking, "BlockActors"),
                new BoolProperty(blocking, "BlockZeroExtent"), new BoolProperty(blocking, "BlockNonZeroExtent"),
                new BoolProperty(false, "HiddenGame"), new FloatProperty(1, "Scale"),
                CommonStructs.Vector3Prop(Vector3.One, "Scale3D")
            }, BrushComponent.Create());
            var physics = ObjectBinary.From<BrushComponent>(component);
            physics.RegenCachedPhysBrushData();
            component.WriteBinary(physics);
            Level level = ObjectBinary.From<Level>(levelExport);
            level.Actors.Add(actor.UIndex);
            levelExport.WriteBinary(level);
            return actor;
        }
        catch
        {
            if (actor != null) EntryPruner.TrashEntryAndDescendants(actor);
            throw;
        }
    }

    // These hulls are known triangular prisms, so their complete UE collision topology
    // can be built directly. PhysX cooks their point clouds separately for runtime use.
    internal static KConvexElem BuildPrismHull(Vector3[] vertices)
    {
        if (vertices.Length != 6) throw new ArgumentException("A triangular prism needs six vertices.");
        var hull = new KConvexElem();
        hull.VertexData.AddRange(vertices);
        foreach (Vector3 vertex in vertices) hull.ElemBox.Add(vertex);
        Vector3 center = vertices.Aggregate(Vector3.Zero, (sum, p) => sum + p) / vertices.Length;
        int[] indices = [0,2,1, 3,4,5, 0,1,4, 0,4,3, 1,2,5, 1,5,4, 2,0,3, 2,3,5];
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            Vector3 normal = Vector3.Normalize(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]));
            if (Vector3.Dot(normal, vertices[a] - center) < 0) { (b, c) = (c, b); normal = -normal; }
            // KConvexElem uses clockwise Unreal indices and outward face planes.
            hull.FaceTriData.AddRange([a, c, b]);
            AddDirection(hull.FaceNormalDirections, normal);
            var plane = new Plane(normal, Vector3.Dot(normal, vertices[a]));
            if (!hull.FacePlaneData.Any(p => Vector3.Dot(p.Normal, normal) > 0.9999f && MathF.Abs(p.D - plane.D) < 0.001f)) hull.FacePlaneData.Add(plane);
            AddDirection(hull.EdgeDirections, Vector3.Normalize(vertices[b] - vertices[a]));
            AddDirection(hull.EdgeDirections, Vector3.Normalize(vertices[c] - vertices[b]));
            AddDirection(hull.EdgeDirections, Vector3.Normalize(vertices[a] - vertices[c]));
        }
        for (int i = 0; i < vertices.Length; i += 4)
        {
            Vector3 a = vertices[i], b = vertices[Math.Min(i + 1, vertices.Length - 1)];
            Vector3 c = i + 2 < vertices.Length ? vertices[i + 2] : a;
            Vector3 d = i + 3 < vertices.Length ? vertices[i + 3] : a;
            hull.PermutedVertexData.Add(new Plane(a.X, b.X, c.X, d.X));
            hull.PermutedVertexData.Add(new Plane(a.Y, b.Y, c.Y, d.Y));
            hull.PermutedVertexData.Add(new Plane(a.Z, b.Z, c.Z, d.Z));
        }
        return hull;
        static void AddDirection(List<Vector3> directions, Vector3 direction)
        {
            if (!directions.Any(d => MathF.Abs(Vector3.Dot(d, direction)) > 0.9999f)) directions.Add(direction);
        }
    }
}
