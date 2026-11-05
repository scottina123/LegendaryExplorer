using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.Meshplorer;

[TestClass]
public class MeshplorerModelTests
{
    private static readonly Vector2[] Rectangle = [new(-100, -50), new(100, -50), new(100, 50), new(-100, 50)];

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void DrawnModelRoundTripsAsClosedSolidWithOwnedPolys(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("DrawModel.pcc", game);
        var (model, polys) = ModelGeometry.CreatePrism(Rectangle, -30, 200, new Vector2(123, 456));
        ExportEntry export = MeshplorerWindow.AddDrawnModel(package, "DrawnModel", null, model, polys);
        // Naming a second drawing the same way must never overwrite the first one.
        var second = ModelGeometry.CreatePrism(Rectangle.Reverse().ToArray(), -30, 200);
        var other = MeshplorerWindow.AddDrawnModel(package, "DrawnModel", null, second.Model, second.Polys);
        Assert.AreNotEqual(export.ObjectName, other.ObjectName);
        using var stream = package.SaveToStream(false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream, "DrawModel.pcc");
        Model saved = ObjectBinary.From<Model>(reopened.GetUExport(export.UIndex));
        Polys savedPolys = ObjectBinary.From<Polys>(reopened.GetUExport(saved.Polys));
        Assert.AreEqual(export.UIndex, saved.Self);
        Assert.AreEqual(export.UIndex, savedPolys.Owner);
        Assert.AreEqual(export.UIndex, reopened.GetUExport(saved.Polys).idxLink);
        Assert.HasCount(6, saved.Nodes);
        Assert.HasCount(6, savedPolys.Elements);
        Assert.AreEqual(new Vector3(123, 456, 70), saved.Bounds.Origin);
        Assert.AreEqual(new Vector3(100, 50, 100), saved.Bounds.BoxExtent);
        Assert.IsTrue(saved.RootOutside);
        Assert.IsTrue(Contains(saved, saved.Bounds.Origin));
        Assert.IsFalse(Contains(saved, saved.Bounds.Origin + new Vector3(101, 0, 0)));
        Assert.IsFalse(Contains(saved, saved.Bounds.Origin - new Vector3(0, 51, 0)));
        Assert.IsFalse(Contains(saved, saved.Bounds.Origin + new Vector3(0, 0, 101)));
        Assert.IsTrue(MeshRenderer.CanParseStatic(reopened.GetUExport(export.UIndex)));
        List<ModelGeometry.Face> faces = ModelGeometry.GetFaces(saved);
        Assert.HasCount(6, faces);
        var edges = new Dictionary<(Vector3, Vector3), int>();
        foreach (var face in faces)
        {
            Assert.IsTrue(Vector3.Dot(face.Normal, face.Vertices[0] - saved.Bounds.Origin) > 0);
            for (int i = 0; i < face.Vertices.Length; i++)
            {
                var edge = (face.Vertices[i], face.Vertices[(i + 1) % face.Vertices.Length]);
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        }
        foreach (var edge in edges.Keys)
            Assert.AreEqual(edges[edge], edges.GetValueOrDefault((edge.Item2, edge.Item1)), "Every edge must have an opposite edge: watertight, consistently wound solid.");
        // No render data or ModelComponents are required by the preview.
        saved.VertexBuffer = [];
        Assert.HasCount(6, ModelGeometry.GetFaces(saved));
        // An editor-only brush can contain just the polygon data.
        saved.Nodes = [];
        Assert.HasCount(6, ModelGeometry.GetFaces(saved));
    }

    private static bool Contains(Model model, Vector3 point)
    {
        int index = 0;
        while (index >= 0)
        {
            BspNode node = model.Nodes[index];
            bool front = Vector3.Dot(node.Plane.Normal, point) > node.Plane.D;
            if (front && node.iFront < 0) return false;
            if (!front && node.iBack < 0) return true;
            index = front ? node.iFront : node.iBack;
        }
        return false;
    }

    [TestMethod]
    public void RejectsOpenDegenerateCrossedAndNonFiniteDrawings()
    {
        Vector2[][] invalid = [
            [], [new(0,0), new(1,1)],
            [new(0,0), new(1,0), new(2,0)],
            [new(0,0), new(2,2), new(0,2), new(2,0)],
            [new(0,0), new(1,0), new(1,0), new(0,1)],
            [new(float.NaN,0), new(1,0), new(0,1)]
        ];
        foreach (var drawing in invalid)
            Assert.ThrowsExactly<ArgumentException>(() => ModelGeometry.CreatePrism(drawing, 0, 100));
        Assert.ThrowsExactly<ArgumentException>(() => ModelGeometry.CreatePrism(Rectangle, 0, 0));
        Assert.ThrowsExactly<ArgumentException>(() => ModelGeometry.CreatePrism(Rectangle, 0, -1));
        Assert.ThrowsExactly<ArgumentException>(() => ModelGeometry.CreatePrism(Rectangle, 0, float.PositiveInfinity));
    }

    [STATestMethod]
    public void DrawDialogBuildsPreviewAndModelsFilterIncludesDirectModels()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, typeof(MeshplorerWindow).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("DrawDialog.pcc", MEGame.LE3);
        var window = new DrawModelWindow(package, null);
        typeof(DrawModelWindow).GetMethod("Rectangle_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, new RoutedEventArgs()]);
        var create = (Button)window.FindName("CreateButton");
        Assert.IsTrue(create.IsEnabled);
        var preview = (System.Windows.Controls.Viewport3D)window.FindName("Preview");
        Assert.HasCount(1, preview.Children);
        Assert.IsNull(window.FindName("ParentPicker"), "The drawer infers its parent from the launch context.");
        var camera = (System.Windows.Media.Media3D.PerspectiveCamera)preview.Camera;
        var originalCamera = camera.Position;
        window.OrbitPreview(75, 25);
        Assert.AreNotEqual(originalCamera, camera.Position);
        var rotatedCamera = camera.Position;
        ((TextBox)window.FindName("ExtrusionHeight")).Text = "-1";
        Assert.IsFalse(create.IsEnabled);
        ((TextBox)window.FindName("ExtrusionHeight")).Text = "200";
        Assert.IsTrue(create.IsEnabled);
        Assert.AreEqual(rotatedCamera, camera.Position, "Editing the drawing must preserve the orbit.");
        var embeddedRenderer = new MeshRenderer { ShowMeshTools = true };
        Assert.IsNotNull(embeddedRenderer.FindName("DrawModelButton"), "The Package Editor Mesh tab must expose Draw Model.");
        embeddedRenderer.Dispose();
        var model = package.CreateExport("Model", "Model");
        var component = package.CreateExport("ModelComponent", "ModelComponent");
        var meshplorer = new MeshplorerWindow(enableRecents: false);
        try
        {
            meshplorer.MeshExports.Add(model);
            meshplorer.MeshExports.Add(component);
            Assert.AreEqual(2, meshplorer.MeshesView.Cast<object>().Count());
            meshplorer.ShowModels = false;
            Assert.AreEqual(0, meshplorer.MeshesView.Cast<object>().Count());
            meshplorer.ShowModels = true;
            meshplorer.MeshSearchText = "ModelComponent";
            Assert.AreEqual(1, meshplorer.MeshesView.Cast<object>().Count());
        }
        finally { meshplorer.Mesh3DViewer.Dispose(); }
    }

    [TestMethod]
    public void CookedPlaceholderUsesOnlyItsBrushesWithActorAndComponentTransforms()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CookedBrush.pcc", MEGame.LE3);
        var model = package.CreateExport("Model", "Model");
        model.WriteBinary(Model.Create());
        var actor = package.CreateExport("Volume", "BlockingVolume");
        actor.WriteProperty(CommonStructs.Vector3Prop(new Vector3(100, 200, 300), "location"));
        actor.WriteProperty(new FloatProperty(2, "DrawScale"));
        var brush = package.CreateExport("BrushComponent", "BrushComponent", actor);
        var vertices = new ArrayProperty<StructProperty>("VertexData") {
            CommonStructs.Vector3Prop(Vector3.Zero), CommonStructs.Vector3Prop(Vector3.UnitX), CommonStructs.Vector3Prop(Vector3.UnitY)
        };
        var triangles = new ArrayProperty<IntProperty>("FaceTriData") { new(0), new(1), new(2), new(-1), new(1), new(2) };
        brush.WriteProperty(new ObjectProperty(model.UIndex, "Brush"));
        brush.WriteProperty(CommonStructs.Vector3Prop(new Vector3(10, 0, 0), "Translation"));
        brush.WriteProperty(new StructProperty("KAggregateGeom", new PropertyCollection {
            new ArrayProperty<StructProperty>("ConvexElems") { new("KConvexElem", new PropertyCollection { vertices, triangles }) }
        }, "BrushAggGeom"));
        var faces = ModelGeometry.GetFaces(ObjectBinary.From<Model>(model));
        Assert.HasCount(1, faces, "Malformed triangle indices must be skipped.");
        Assert.AreEqual(new Vector3(120, 200, 300), faces[0].Vertices[0]);
        Assert.AreEqual(new Vector3(122, 200, 300), faces[0].Vertices[1]);
        Assert.AreEqual(new Vector3(120, 202, 300), faces[0].Vertices[2]);
        var unrelated = package.CreateExport("OtherModel", "Model");
        unrelated.WriteBinary(Model.Create());
        Assert.IsEmpty(ModelGeometry.GetFaces(ObjectBinary.From<Model>(unrelated)));
    }

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void ConcaveOutlinesKeepTheirNotchesInPreviewAndSavedBsp(MEGame game)
    {
        Vector2[][] outlines = [
            [new(0,0), new(4,0), new(4,1), new(1,1), new(1,4), new(0,4)],
            [new(0,0), new(4,0), new(4,4), new(3,4), new(3,1), new(1,1), new(1,4), new(0,4)],
            [new(0,0), new(4,0), new(2,2), new(4,4), new(0,4)]
        ];
        foreach (var outline in outlines.Concat(outlines.Select(p => p.Reverse().ToArray())))
        {
            using var package = MEPackageHandler.CreateMemoryEmptyPackage("Concave.pcc", game);
            var (model, polys) = ModelGeometry.CreatePrism(outline, -1, 2);
            var export = MeshplorerWindow.AddDrawnModel(package, "Concave", null, model, polys);
            using var stream = package.SaveToStream(false);
            stream.Position = 0;
            using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream, "Concave.pcc");
            Model saved = ObjectBinary.From<Model>(reopened.GetUExport(export.UIndex));
            for (float x = -0.25f; x <= 4.25f; x += 0.5f)
                for (float y = -0.15f; y <= 4.15f; y += 0.5f)
                {
                    bool inside = false;
                    for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
                        if ((outline[i].Y > y) != (outline[j].Y > y)
                            && x < (outline[j].X - outline[i].X) * (y - outline[i].Y) / (outline[j].Y - outline[i].Y) + outline[i].X)
                            inside = !inside;
                    Assert.AreEqual(inside, Contains(saved, new Vector3(x, y, 0)), $"{game}: ({x}, {y}) must retain the drawn notch.");
                    Assert.IsFalse(Contains(saved, new Vector3(x, y, 2)));
                }
            double signedArea = 0;
            for (int i = 0; i < outline.Length; i++) signedArea += Vector3.Cross(new Vector3(outline[i], 0), new Vector3(outline[(i+1)%outline.Length], 0)).Z / 2;
            double topArea = ModelGeometry.GetFaces(saved).Where(f => f.Normal.Z > 0.9f).Sum(face => {
                int[] triangles = ModelGeometry.Triangulate(face.Vertices);
                double area = 0;
                for (int i = 0; i < triangles.Length; i += 3)
                    area += Vector3.Cross(face.Vertices[triangles[i+1]] - face.Vertices[triangles[i]], face.Vertices[triangles[i+2]] - face.Vertices[triangles[i]]).Length() / 2;
                return area;
            });
            Assert.AreEqual(Math.Abs(signedArea), topArea, 0.0001, "Preview triangles must cover the exact footprint without filling the notches.");
        }
    }

    [TestMethod]
    public void ScreenshotEngineeringModelsHavePreviewFacesWithoutComponents()
    {
        const string path = @"C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_Mordin\CookedPCConsole\BioA_Nor_405Engineering.pcc";
        if (!File.Exists(path)) { Assert.Inconclusive("Optional installed-game regression fixture is unavailable."); return; }
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        foreach (int index in new[] { 852, 853 })
        {
            var export = package.GetUExport(index);
            Assert.AreEqual("Model", export.ClassName);
            var model = ObjectBinary.From<Model>(export);
            var faces = ModelGeometry.GetFaces(model);
            Console.WriteLine($"#{index}: {faces.Count} faces; {model.Nodes.Length} BSP nodes; {model.VertexBuffer.Length} render vertices; Polys={model.Polys}; {faces.Sum(f => f.Vertices.Length - 2)} preview triangles");
            if (index == 853) Assert.IsNotEmpty(faces, $"#{index} {export.ObjectName} must be visible.");
        }
    }
}
