using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class LevelVolumeDrawingTests
{
    private static readonly Vector2[] NotchedOutline = [new(0,0), new(200,0), new(200,50), new(50,50), new(50,200), new(0,200)];

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)]
    public void WorkPlaneRayDrawingAndTiltedExtrusionKeepAllCoordinatesConsistent(int axis)
    {
        var plane = ModelDrawingPlane.Create(new Vector3(1200, -3400, 5000), axis, 35, 20, -15);
        var corner = new Vector2(50, 175);
        Vector3 point = plane.Point(corner);
        Assert.IsTrue(plane.IntersectRay(point + plane.Normal * 100, -plane.Normal, out Vector2 hit));
        Assert.IsTrue(Vector2.Distance(corner, hit) < 0.001);
        Assert.IsFalse(plane.IntersectRay(point + plane.Normal * 100, plane.Normal, out _));
        Assert.IsFalse(plane.IntersectRay(point + plane.Normal * 100, plane.U, out _));
        Assert.AreEqual(new Vector2(50, 175), ModelDrawingPlane.Snap(new Vector2(53, 174), 25));
        var (model, polys) = plane.Extrude(NotchedOutline, 100);
        foreach (Vector3 vertex in model.Points)
        {
            float distance = Vector3.Dot(vertex - plane.Origin, plane.Normal);
            Assert.IsTrue(MathF.Abs(distance) < 0.005 || MathF.Abs(distance - 100) < 0.005);
        }
        foreach (Poly poly in polys.Elements)
            Assert.IsTrue(poly.Vertices.All(p => MathF.Abs(Vector3.Dot(poly.Normal, p - poly.Base)) < 0.01));
        foreach (BspNode node in model.Nodes)
            Assert.IsTrue(MathF.Abs(Vector3.Dot(node.Plane.Normal, model.Points[model.Verts[node.iVertPool].pVertex]) - node.Plane.D) < 0.01);
        Assert.AreEqual(NotchedOutline.Length - 2, plane.CollisionHulls(NotchedOutline, 100).Count);
        foreach (var hull in plane.CollisionHulls(NotchedOutline, 100)) Assert.HasCount(6, hull);
    }

    [TestMethod]
    [DataRow(MEGame.LE1, "BlockingVolume")]
    [DataRow(MEGame.LE2, "BlockingVolume")]
    [DataRow(MEGame.LE3, "BlockingVolume")]
    [DataRow(MEGame.LE3, "TriggerVolume")]
    public void CreatedVolumeRoundTripsWithModelComponentCookedCollisionAndLevelMembership(MEGame game, string actorClass)
    {
        ConfigurePhysXSearchPath();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("DrawnLevel.pcc", game);
        ExportEntry level = package.CreateExport("PersistentLevel", "Level");
        level.WriteBinary(Level.Create(game));
        var plane = ModelDrawingPlane.Create(new Vector3(1000, 2000, -3000), 1, 20, 30, 10);
        ExportEntry actor = DrawnVolumeBuilder.Create(level, "DrawnVolume", actorClass, plane, NotchedOutline, 150);
        using var stream = package.SaveToStream(false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream, "DrawnLevel.pcc");
        var savedActor = reopened.GetUExport(actor.UIndex);
        Assert.IsTrue(ObjectBinary.From<Level>(reopened.GetUExport(level.UIndex)).Actors.Contains(savedActor.UIndex));
        Assert.AreEqual(plane.Origin, CommonStructs.GetVector3(savedActor.GetProperty<StructProperty>("location")));
        ExportEntry model = reopened.GetUExport(savedActor.GetProperty<ObjectProperty>("Brush").Value);
        ExportEntry component = reopened.GetUExport(savedActor.GetProperty<ObjectProperty>("BrushComponent").Value);
        Assert.AreEqual("Model", model.ClassName, "Drawing must use the vanilla Model class.");
        Assert.AreEqual("BrushComponent", component.ClassName);
        Assert.IsFalse(reopened.Exports.Any(e => e.ClassName == "Class"), "Do not create custom Unreal classes.");
        Assert.AreEqual(savedActor.UIndex, model.idxLink);
        Assert.AreEqual(savedActor.UIndex, component.idxLink);
        Assert.AreEqual(model.UIndex, component.GetProperty<ObjectProperty>("Brush").Value);
        Assert.AreEqual(component.UIndex, savedActor.GetProperty<ObjectProperty>("CollisionComponent").Value);
        Assert.HasCount(NotchedOutline.Length - 2, ObjectBinary.From<BrushComponent>(component).CachedPhysBrushData.CachedConvexElements);
        Assert.IsTrue(ObjectBinary.From<BrushComponent>(component).CachedPhysBrushData.CachedConvexElements.All(e => e.ConvexElementData.Length > 32));
        var hulls = component.GetProperty<StructProperty>("BrushAggGeom").GetProp<ArrayProperty<StructProperty>>("ConvexElems");
        Assert.HasCount(NotchedOutline.Length - 2, hulls);
        Assert.IsTrue(hulls.All(h => h.GetProp<ArrayProperty<StructProperty>>("VertexData").Count >= 6));
        Assert.AreEqual(actorClass == "BlockingVolume", component.GetProperty<BoolProperty>("BlockActors").Value);
        Assert.IsTrue(ModelGeometry.GetActorFaces(savedActor).Count > 0);
        // Collision prisms must not fill the missing upper-right part of the L.
        foreach (var hull in hulls)
        {
            var points = hull.GetProp<ArrayProperty<StructProperty>>("VertexData").Select(CommonStructs.GetVector3).ToArray();
            Assert.IsTrue(points.All(p => Vector3.Dot(p, plane.U) < 55) || points.All(p => Vector3.Dot(p, plane.V) < 55));
        }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void PerspectiveDrawingStartsAtTheActualCameraEye(bool firstPerson)
    {
        var camera = new LegendaryExplorer.Tools.LevelEditor.Scene3D.SceneCamera {
            FirstPerson = firstPerson, Position = new Vector3(500, 1500, 400), FocusDepth = 1000,
            Yaw = 0.4f, Pitch = -0.3f, aspect = 2
        };
        var ray = LegendaryExplorer.Tools.LevelEditor.LevelEditor.GetVolumeDrawingRay(camera, 1000, 500, new System.Windows.Point(500, 250));
        Assert.AreEqual(camera.EyePosition, ray.Origin);
        Vector3 target = firstPerson ? camera.Position + camera.CameraForward * 1000 : camera.Position;
        var plane = ModelDrawingPlane.FromNormal(target, camera.CameraForward);
        Assert.IsTrue(plane.IntersectRay(ray.Origin, ray.Direction, out Vector2 corner));
        Assert.IsTrue(corner.Length() < 0.001f);
    }

    [TestMethod]
    public void OrthographicDrawingOffsetsTheRayInTheViewPlane()
    {
        var camera = new LegendaryExplorer.Tools.LevelEditor.Scene3D.SceneCamera {
            IsOrthographic = true, Position = new Vector3(0, 0, 1000), aspect = 2, OrthoWidth = 400
        };
        var center = LegendaryExplorer.Tools.LevelEditor.LevelEditor.GetVolumeDrawingRay(camera, 1000, 500, new System.Windows.Point(500, 250));
        var right = LegendaryExplorer.Tools.LevelEditor.LevelEditor.GetVolumeDrawingRay(camera, 1000, 500, new System.Windows.Point(750, 250));
        Assert.IsTrue(Vector3.Distance(right.Origin - center.Origin, camera.CameraRight * 100) < 0.001f);
        Assert.AreEqual(center.Direction, right.Direction);
    }

    [TestMethod]
    public void ConvexCollisionPrismsHaveClosedTopologyAndOutwardPlanes()
    {
        var plane = ModelDrawingPlane.Create(Vector3.Zero, 2, 35, -20, 15);
        foreach (Vector3[] vertices in plane.CollisionHulls(NotchedOutline, 100))
        {
            var hull = DrawnVolumeBuilder.BuildPrismHull(vertices);
            Assert.HasCount(5, hull.FacePlaneData);
            Assert.HasCount(6, hull.PermutedVertexData);
            foreach (var face in hull.FacePlaneData)
                Assert.IsTrue(vertices.All(v => Vector3.Dot(face.Normal, v) - face.D < 0.001f));
            Vector3 center = vertices.Aggregate(Vector3.Zero, (sum, p) => sum + p) / vertices.Length;
            var edges = new System.Collections.Generic.HashSet<(int,int)>();
            for (int i = 0; i < hull.FaceTriData.Count; i += 3)
            {
                int a = hull.FaceTriData[i], b = hull.FaceTriData[i+1], c = hull.FaceTriData[i+2];
                Vector3 outward = Vector3.Cross(vertices[c] - vertices[a], vertices[b] - vertices[a]);
                Assert.IsTrue(Vector3.Dot(outward, vertices[a] - center) > 0);
                edges.Add((a,b)); edges.Add((b,c)); edges.Add((c,a));
            }
            Assert.IsTrue(edges.All(e => edges.Contains((e.Item2,e.Item1))));
        }
    }

    [TestMethod]
    public void InvalidDraftDoesNotAddActorsOrExports()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("InvalidDrawing.pcc", MEGame.LE3);
        ExportEntry level = package.CreateExport("PersistentLevel", "Level");
        level.WriteBinary(Level.Create(package.Game));
        int exports = package.ExportCount;
        Assert.ThrowsExactly<ArgumentException>(() => DrawnVolumeBuilder.Create(level, "Invalid", "BlockingVolume",
            ModelDrawingPlane.Create(Vector3.Zero), [new(0,0), new(200,200), new(0,200), new(200,0)], 100));
        Assert.AreEqual(exports, package.ExportCount);
        Assert.IsEmpty(ObjectBinary.From<Level>(level).Actors);
    }

    internal static void ConfigurePhysXSearchPath()
    {
        const string binaries = @"C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\Binaries\Win64";
        if (!File.Exists(Path.Combine(binaries, "PhysXCore64.dll"))) { Assert.Inconclusive("Installed PhysX libraries are unavailable."); return; }
        string path = Path.Combine(AppContext.BaseDirectory, "volume-drawing-native");
        Directory.CreateDirectory(path);
        foreach (string name in new[] { "PhysXCore64.dll", "PhysXCooking64.dll", "PhysXLoader64.dll" })
        {
            string source = Path.Combine(binaries, name == "PhysXLoader64.dll" ? @"PhysXLocal\PhysXLoader64.dll" : name);
            string target = Path.Combine(path, name);
            if (!File.Exists(target)) File.Copy(source, target);
        }
        string existing = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;
        if (existing?.Contains(path, StringComparison.OrdinalIgnoreCase) != true)
            AppContext.SetData("NATIVE_DLL_SEARCH_DIRECTORIES", path + ";" + existing);
    }
}
