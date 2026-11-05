using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorerCore.Unreal.PhysX;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>A detached edit buffer. Existing exports are changed only by Commit.</summary>
internal sealed class VolumeGeometryEdit
{
    public ExportEntry Actor { get; }
    public ExportEntry SourceComponent { get; }
    public List<Vector3> Vertices { get; } = [];
    public List<KConvexElem> Hulls { get; private set; } = [];
    public Matrix4x4 ComponentToWorld { get; }
    private readonly Matrix4x4 _worldToComponent;
    private readonly byte[] _actorData, _componentData, _modelData;
    private readonly ExportEntry _sourceModel;
    private readonly PropertyCollection _actorProperties;
    private readonly List<int[]> _pieces = [];
    private readonly Stack<Vector3[]> _history = new();

    public static ExportEntry FindComponent(ExportEntry actor)
    {
        if (actor == null || !actor.Game.IsLEGame()) return null;
        var props = actor.GetProperties();
        foreach (var reference in props.OfType<ObjectProperty>().Concat(props.OfType<ArrayProperty<ObjectProperty>>().SelectMany(a => a)))
            if (actor.FileRef.GetEntry(reference.Value) is ExportEntry component && component.IsA("BrushComponent")) return component;
        return null;
    }

    public VolumeGeometryEdit(ExportEntry actor, Matrix4x4 componentToWorld, PropertyCollection actorProperties)
    {
        Actor = actor;
        SourceComponent = FindComponent(actor) ?? throw new ArgumentException("This actor has no editable BrushComponent.");
        if (!Matrix4x4.Invert(componentToWorld, out _worldToComponent)) throw new ArgumentException("A zero-scale volume cannot be edited.");
        ComponentToWorld = componentToWorld;
        _actorData = actor.Data.ToArray();
        _componentData = SourceComponent.Data.ToArray();
        _actorProperties = actorProperties.DeepClone();
        var props = SourceComponent.GetCondensedProperties();
        int modelIndex = props.GetProp<ObjectProperty>("Brush")?.Value ?? actor.GetProperty<ObjectProperty>("Brush")?.Value ?? 0;
        _sourceModel = actor.FileRef.GetEntry(modelIndex) as ExportEntry;
        _modelData = _sourceModel?.Data.ToArray();
        var convex = props.GetProp<StructProperty>("BrushAggGeom")?.GetProp<ArrayProperty<StructProperty>>("ConvexElems");
        var lookup = new Dictionary<(long, long, long), int>();
        void AddPiece(IEnumerable<Vector3> points)
        {
            var indices = new List<int>();
            foreach (Vector3 local in points)
            {
                Vector3 world = Vector3.Transform(local, componentToWorld);
                var key = ((long)Math.Round(world.X * 1000), (long)Math.Round(world.Y * 1000), (long)Math.Round(world.Z * 1000));
                if (!lookup.TryGetValue(key, out int index)) { index = Vertices.Count; lookup.Add(key, index); Vertices.Add(world); }
                indices.Add(index);
            }
            if (indices.Distinct().Count() >= 4) _pieces.Add(indices.Distinct().ToArray());
        }
        if (convex != null)
            foreach (var piece in convex)
                if (piece.GetProp<ArrayProperty<StructProperty>>("VertexData") is { } points) AddPiece(points.Select(CommonStructs.GetVector3));
        if (_pieces.Count == 0 && _sourceModel?.ClassName == "Model")
        {
            var faces = ModelGeometry.GetFaces(ObjectBinary.From<Model>(_sourceModel), includeCookedBrushes: false);
            var points = faces.SelectMany(f => f.Vertices).Distinct().ToArray();
            if (points.Length >= 4)
            {
                Vector3 center = points.Aggregate(Vector3.Zero, (sum, p) => sum + p) / points.Length;
                bool convexModel = faces.All(f =>
                {
                    Vector3 normal = Vector3.Normalize(Vector3.Cross(f.Vertices[1] - f.Vertices[0], f.Vertices[2] - f.Vertices[0]));
                    if (Vector3.Dot(normal, center - f.Vertices[0]) > 0) normal = -normal;
                    return points.All(p => Vector3.Dot(normal, p - f.Vertices[0]) < 0.01f);
                });
                if (!convexModel) throw new ArgumentException("This concave Model has no convex collision pieces to edit.");
                AddPiece(points);
            }
        }
        if (_pieces.Count == 0) throw new ArgumentException("This volume contains no editable Model or convex collision vertices.");
        Rebuild();
    }

    public void SaveUndo() => _history.Push(Vertices.ToArray());
    public void Undo()
    {
        if (_history.TryPop(out var vertices)) { Vertices.Clear(); Vertices.AddRange(vertices); }
    }
    public void Rebuild() => Hulls = _pieces.Select(piece => ConvexVolumeGeometry.BuildHull(piece.Select(i => Vector3.Transform(Vertices[i], _worldToComponent)))).ToList();

    public ExportEntry Commit()
    {
        if (!Actor.Data.AsSpan().SequenceEqual(_actorData) || !SourceComponent.Data.AsSpan().SequenceEqual(_componentData)
            || _sourceModel != null && !_sourceModel.Data.AsSpan().SequenceEqual(_modelData))
            throw new InvalidOperationException("The actor or its source geometry changed during editing. Cancel and reopen the edit to use those changes.");
        Rebuild();
        var geometry = ModelGeometry.CreateFromFaces(ConvexVolumeGeometry.ExteriorFaces(Hulls));
        var componentProperties = SourceComponent.GetProperties().DeepClone();
        Vector3 scale = (componentProperties.GetProp<FloatProperty>("Scale")?.Value ?? 1)
            * (componentProperties.GetProp<StructProperty>("Scale3D") is { } scale3d ? CommonStructs.GetVector3(scale3d) : Vector3.One)
            * ActorUtils.GetActorTotalScale(_actorProperties);
        var physics = BrushComponent.Create();
        physics.CachedPhysBrushData = BrushComponent.CookConvexData(Hulls.Select(h => h.VertexData.ToArray()), scale);
        var aggregate = new StructProperty("KAggregateGeom", new PropertyCollection {
            new ArrayProperty<StructProperty>(Hulls.Select(h=>h.GetStructProperty()),"ConvexElems")
        }, "BrushAggGeom");
        var package = Actor.FileRef;
        ExportEntry model = null, component = null;
        try
        {
            model = package.CreateExport("Model", "Model", Actor);
            var polys = package.CreateExport("Polys", "Polys", model);
            component = EntryCloner.CloneTree(SourceComponent);
            component.Parent = Actor;
            // Keep references to cloned child objects (for example RB_BodySetup).
            componentProperties = component.GetProperties().DeepClone();
            geometry.Model.Self = model.UIndex;
            geometry.Model.Polys = polys.UIndex;
            geometry.Polys.Owner = model.UIndex;
            foreach (var surface in geometry.Model.Surfs) surface.Actor = Actor.UIndex;
            foreach (var polygon in geometry.Polys.Elements) polygon.Actor = Actor.UIndex;
            model.WriteBinary(geometry.Model);
            polys.WriteBinary(geometry.Polys);
            componentProperties.AddOrReplaceProp(new ObjectProperty(model, "Brush"));
            componentProperties.AddOrReplaceProp(aggregate);
            componentProperties.AddOrReplaceProp(new IntProperty(Actor.Game == MEGame.LE1 ? 33882375 : 34079762, "CachedPhysBrushDataVersion"));
            component.WritePropertiesAndBinary(componentProperties, physics);
            var properties = _actorProperties.DeepClone();
            int oldModel = _sourceModel?.UIndex ?? 0;
            void Rewire(PropertyCollection collection)
            {
                void Reference(ObjectProperty p)
                {
                    if (p.Value == SourceComponent.UIndex) p.Value = component.UIndex;
                    else if (oldModel != 0 && p.Value == oldModel) p.Value = model.UIndex;
                }
                foreach (var property in collection)
                    switch (property)
                    {
                        case ObjectProperty p: Reference(p); break;
                        case StructProperty s: Rewire(s.Properties); break;
                        case ArrayProperty<ObjectProperty> a: foreach (var p in a) Reference(p); break;
                        case ArrayProperty<StructProperty> a: foreach (var s in a) Rewire(s.Properties); break;
                    }
            }
            Rewire(properties);
            if (properties.ContainsNamedProp("Brush") || Actor.IsA("Brush")) properties.AddOrReplaceProp(new ObjectProperty(model, "Brush"));
            Actor.WriteProperties(properties); // The only write to an existing export; all replacement data is ready.
            return model;
        }
        catch
        {
            if (!Actor.Data.AsSpan().SequenceEqual(_actorData)) Actor.Data = _actorData.ToArray();
            if (component != null) EntryPruner.TrashEntryAndDescendants(component);
            if (model != null) EntryPruner.TrashEntryAndDescendants(model);
            throw;
        }
    }
}
