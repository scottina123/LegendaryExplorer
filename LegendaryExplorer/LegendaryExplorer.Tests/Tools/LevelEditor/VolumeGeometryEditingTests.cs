using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.PhysX;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class VolumeGeometryEditingTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    private static Vector3[] Box(Vector3 min, Vector3 max) =>
        (from x in new[] { min.X, max.X } from y in new[] { min.Y, max.Y } from z in new[] { min.Z, max.Z } select new Vector3(x, y, z)).ToArray();

    [TestMethod]
    public void EditedConvexHullIsClosedOutwardAndKeepsDeformedCorners()
    {
        var vertices = Box(Vector3.Zero, new Vector3(100));
        vertices[7] += new Vector3(25, 15, 40);
        var hull = ConvexVolumeGeometry.BuildHull(vertices.Concat([new Vector3(50)]));
        Assert.HasCount(8, hull.VertexData);
        Assert.Contains(vertices[7], hull.VertexData);
        var edges = new Dictionary<(int, int), int>();
        for (int i = 0; i < hull.FaceTriData.Count; i += 3)
        {
            int a = hull.FaceTriData[i], b = hull.FaceTriData[i + 1], c = hull.FaceTriData[i + 2];
            foreach (var edge in new[] { (a, b), (b, c), (c, a) }) edges[edge] = edges.GetValueOrDefault(edge) + 1;
        }
        Assert.IsTrue(edges.All(e => e.Value == 1 && edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) == 1));
        Assert.IsTrue(hull.FacePlaneData.All(p => vertices.All(v => Vector3.Dot(p.Normal, v) - p.D < 0.001)));
        Assert.ThrowsExactly<ArgumentException>(() => ConvexVolumeGeometry.BuildHull([Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.One with { Z = 0 }]));
        Assert.ThrowsExactly<ArgumentException>(() => ConvexVolumeGeometry.BuildHull([Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new(float.NaN, 0, 1)]));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConcaveUnionRetainsNotchAndRemovesInternalFaces(bool overlap)
    {
        var hulls = new[] {
            ConvexVolumeGeometry.BuildHull(Box(Vector3.Zero,new Vector3(200,50,100))),
            ConvexVolumeGeometry.BuildHull(Box(new Vector3(0,overlap?25:50,0),new Vector3(50,200,100)))
        };
        var exterior = ConvexVolumeGeometry.ExteriorFaces(hulls);
        var (model, _) = ModelGeometry.CreateFromFaces(exterior);
        for (float x = -9; x < 210; x += 20)
            for (float y = -7; y < 210; y += 20)
                foreach (float z in new[] { -5f, 45f, 105f })
                {
                    var p = new Vector3(x, y, z);
                    bool inside = hulls.Any(h => h.FacePlaneData.All(f => Vector3.Dot(f.Normal, p) < f.D));
                    Assert.AreEqual(inside, Contains(model, p), $"Point {p}, overlap={overlap}");
                }
        foreach (var face in exterior)
        {
            var normal = Vector3.Normalize(Vector3.Cross(face[1] - face[0], face[2] - face[0]));
            var outside = face.Aggregate(Vector3.Zero, (sum, v) => sum + v) / face.Length + normal * 0.01f;
            Assert.IsFalse(hulls.Any(h => h.FacePlaneData.All(f => Vector3.Dot(f.Normal, outside) < f.D - 0.001f)), "Internal triangle survived union clipping.");
        }
    }

    [TestMethod]
    [DataRow(MEGame.LE1, "BlockingVolume")]
    [DataRow(MEGame.LE2, "TriggerVolume")]
    [DataRow(MEGame.LE3, "BlockingVolume")]
    public void EditingIsDetachedAndCommitRedirectsOnlySelectedActor(MEGame game, string actorClass)
    {
        LevelVolumeDrawingTests.ConfigurePhysXSearchPath();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("VolumeEdit.pcc", game);
        var level = package.CreateExport("PersistentLevel", "Level"); level.WriteBinary(Level.Create(game));
        var actor = DrawnVolumeBuilder.Create(level, "Original", actorClass, ModelDrawingPlane.Create(new Vector3(500, 700, 900)),
            [new(0, 0), new(200, 0), new(200, 50), new(50, 50), new(50, 200), new(0, 200)], 100);
        var component = package.GetUExport(actor.GetProperty<ObjectProperty>("BrushComponent").Value);
        var model = package.GetUExport(actor.GetProperty<ObjectProperty>("Brush").Value);
        component.WriteProperty(new BoolProperty(true, "BlockRigidBody"));
        var child = package.CreateExport("Settings", "Object", component);
        child.WriteProperties([]);
        component.WriteProperty(new ObjectProperty(child, "Settings"));
        actor.WriteProperty(new NameProperty("KeepThisTag", "Tag"));
        var other = package.CreateExport("SharingActor", actorClass, level, createWithStack: true);
        other.WriteProperties(actor.GetProperties().DeepClone());
        var original = package.Exports.ToDictionary(e => e.UIndex, e => e.Data.ToArray());
        int count = package.ExportCount;
        var transform = Matrix4x4.CreateTranslation(500, 700, 900);
        var edit = new VolumeGeometryEdit(actor, transform, actor.GetProperties());
        Vector3 corner = edit.Vertices[0];
        edit.SaveUndo(); edit.Vertices[0] += new Vector3(-25, -10, 20); edit.Rebuild(); edit.Undo(); edit.Rebuild();
        Assert.AreEqual(corner, edit.Vertices[0]);
        edit.Vertices[0] += new Vector3(-25, -10, 20); edit.Rebuild();
        Assert.AreEqual(count, package.ExportCount);
        foreach (var pair in original) CollectionAssert.AreEqual(pair.Value, package.GetUExport(pair.Key).Data, "Preview must not write exports.");
        var replacement = edit.Commit();
        var replacementComponent = package.GetUExport(actor.GetProperty<ObjectProperty>("BrushComponent").Value);
        Assert.AreNotEqual(component.UIndex, replacementComponent.UIndex);
        Assert.AreNotEqual(model.UIndex, replacement.UIndex);
        Assert.AreEqual("Model", replacement.ClassName);
        Assert.AreEqual(replacement.UIndex, actor.GetProperty<ObjectProperty>("Brush").Value);
        Assert.AreEqual(replacement.UIndex, replacementComponent.GetProperty<ObjectProperty>("Brush").Value);
        Assert.AreEqual(replacementComponent.UIndex, actor.GetProperty<ObjectProperty>("CollisionComponent").Value);
        Assert.AreEqual(replacementComponent.UIndex, actor.GetProperty<ArrayProperty<ObjectProperty>>("Components")[0].Value);
        Assert.AreEqual(actor.UIndex, replacementComponent.idxLink);
        Assert.AreEqual(actorClass, actor.ClassName);
        Assert.AreEqual("KeepThisTag", actor.GetProperty<NameProperty>("Tag").Value.Name);
        Assert.IsTrue(replacementComponent.GetProperty<BoolProperty>("BlockRigidBody").Value);
        var newChild = package.GetUExport(replacementComponent.GetProperty<ObjectProperty>("Settings").Value);
        Assert.AreNotEqual(child.UIndex, newChild.UIndex);
        Assert.AreEqual(replacementComponent.UIndex, newChild.idxLink);
        foreach (var pair in original.Where(p => p.Key != actor.UIndex)) CollectionAssert.AreEqual(pair.Value, package.GetUExport(pair.Key).Data, "Other actors and original geometry must stay byte-for-byte intact.");
        Assert.IsFalse(package.Exports.Any(e => e.ClassName == "Class"));
        Assert.IsTrue(ObjectBinary.From<BrushComponent>(replacementComponent).CachedPhysBrushData.CachedConvexElements.All(e => e.ConvexElementData.Length > 32));
        Assert.IsTrue(ObjectBinary.From<Model>(replacement).Points.Any(p => Vector3.Distance(p, edit.Vertices[0] - transform.Translation) < 0.01));
        using var stream = package.SaveToStream(false); stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream, "VolumeEdit.pcc");
        Assert.AreEqual(replacement.UIndex, reopened.GetUExport(actor.UIndex).GetProperty<ObjectProperty>("Brush").Value);
        Assert.AreEqual(model.UIndex, reopened.GetUExport(other.UIndex).GetProperty<ObjectProperty>("Brush").Value);
        Assert.IsTrue(ObjectBinary.From<Level>(reopened.GetUExport(level.UIndex)).Actors.Contains(actor.UIndex));
    }

    [TestMethod]
    public void StaleSourceIsRejectedBeforeAddingReplacementExports()
    {
        LevelVolumeDrawingTests.ConfigurePhysXSearchPath();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("Stale.pcc", MEGame.LE3);
        var level = package.CreateExport("PersistentLevel", "Level"); level.WriteBinary(Level.Create(package.Game));
        var actor = DrawnVolumeBuilder.Create(level, "Original", "BlockingVolume", ModelDrawingPlane.Create(Vector3.Zero), [new(0, 0), new(100, 0), new(0, 100)], 100);
        var edit = new VolumeGeometryEdit(actor, Matrix4x4.Identity, actor.GetProperties());
        edit.SourceComponent.WriteProperty(new BoolProperty(true, "BlockRigidBody"));
        int count = package.ExportCount;
        Assert.ThrowsExactly<InvalidOperationException>(() => edit.Commit());
        Assert.AreEqual(count, package.ExportCount);
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(11)]
    [DataRow(15)]
    public void InstalledCookedVolumesCanBeEditedWithoutChangingSharedSourceGeometry(int index)
    {
        const string path = @"C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_Mordin\CookedPCConsole\BioA_Nor_405Engineering.pcc";
        if (!File.Exists(path)) { Assert.Inconclusive("Optional installed-game fixture is unavailable."); return; }
        LevelVolumeDrawingTests.ConfigurePhysXSearchPath();
        // Work on the in-memory package only; never save to the installed fixture.
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        var actor = package.GetUExport(index);
        var component = VolumeGeometryEdit.FindComponent(actor);
        Assert.IsNotNull(component);
        var props = component.GetProperties();
        Vector3 translation = props.GetProp<StructProperty>("Translation") is { } t ? CommonStructs.GetVector3(t) : Vector3.Zero;
        Vector3 scale = (props.GetProp<FloatProperty>("Scale")?.Value ?? 1) * (props.GetProp<StructProperty>("Scale3D") is { } s ? CommonStructs.GetVector3(s) : Vector3.One);
        Rotator rotation = props.GetProp<StructProperty>("Rotation") is { } r ? CommonStructs.GetRotator(r) : default;
        var matrix = ActorUtils.ComposeLocalToWorld(translation, rotation, scale) * ActorUtils.GetLocalToWorld(actor);
        var original = package.Exports.ToDictionary(e => e.UIndex, e => e.Data.ToArray());
        var edit = new VolumeGeometryEdit(actor, matrix, actor.GetProperties());
        Assert.IsNotEmpty(edit.Vertices);
        edit.Vertices[0] += new Vector3(2, 3, 4); edit.Rebuild();
        var model = edit.Commit();
        var binary = ObjectBinary.From<Model>(model);
        Assert.IsNotEmpty(binary.Nodes);
        var random = new Random(index);
        Vector3 min = edit.Hulls.SelectMany(h => h.VertexData).Aggregate(Vector3.Min) - Vector3.One;
        Vector3 max = edit.Hulls.SelectMany(h => h.VertexData).Aggregate(Vector3.Max) + Vector3.One;
        for (int i = 0; i < 1000; i++)
        {
            var point = min + (max - min) * new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
            if (edit.Hulls.Any(h => h.FacePlaneData.Any(p => MathF.Abs(Vector3.Dot(p.Normal, point) - p.D) < 0.01))) continue;
            bool inside = edit.Hulls.Any(h => h.FacePlaneData.All(p => Vector3.Dot(p.Normal, point) < p.D));
            Assert.AreEqual(inside, Contains(binary, point), $"Cooked actor #{index} must preserve its edited collision union at {point}.");
        }
        foreach (var pair in original.Where(p => p.Key != index)) CollectionAssert.AreEqual(pair.Value, package.GetUExport(pair.Key).Data, $"Original export #{pair.Key} changed.");
    }

    [TestMethod]
    [DataRow("Model", false)]
    [DataRow("ModelComponent", false)]
    [DataRow("BrushComponent", true)]
    [DataRow("StaticMeshComponent", true)]
    [DataRow("SkeletalMeshComponent", false)]
    public void ViewFilterClassifiesModelReferencesAndRefreshes(string componentClass, bool array)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("Visibility.pcc", MEGame.LE3);
        var actor = package.CreateExport("Actor", "Actor");
        var component = package.CreateExport("Geometry", componentClass, actor);
        actor.WriteProperties(array ? [new ArrayProperty<ObjectProperty>([new ObjectProperty(component)], "Components")] : [new ObjectProperty(component, "Geometry")]);
        using var proxy = new DefaultActor(actor);
        var context = new LevelEditorRenderContext();
        bool isVolume = componentClass is "Model" or "ModelComponent" or "BrushComponent";
        Assert.AreEqual(isVolume, proxy.IsVolume);
        Assert.AreEqual(!isVolume, context.IsActorVisible(proxy));
        context.ShowVolumes = true; Assert.IsTrue(context.IsActorVisible(proxy));
        actor.WriteProperties([]); proxy.RefreshFromExport(); Assert.IsFalse(proxy.IsVolume);
    }

    private sealed class DefaultActor(ExportEntry export) : ActorProxy(null, export);
    private static bool Contains(Model model, Vector3 point)
    {
        int index = 0;
        while (index >= 0)
        {
            var node = model.Nodes[index];
            bool front = Vector3.Dot(node.Plane.Normal, point) - node.Plane.D >= 0;
            int next = front ? node.iFront : node.iBack;
            if (next < 0) return !front;
            index = next;
        }
        return false;
    }
}
