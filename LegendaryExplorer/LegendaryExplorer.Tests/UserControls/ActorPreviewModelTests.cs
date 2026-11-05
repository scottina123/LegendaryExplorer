using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ActorPreviewModelTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow("BlockingVolume")]
    [DataRow("TriggerVolume")]
    [DataRow("PhysicsVolume")]
    [DataRow("Actor")]
    public void ModelReferencesEnableActorPreviewForAllActorTypes(string className)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ActorModels.pcc", MEGame.LE3);
        var geometry = ModelGeometry.CreatePrism([new(-100,-50), new(100,-50), new(100,50), new(-100,50)], 0, 200);
        var model = MeshplorerWindow.AddDrawnModel(package, "Model", null, geometry.Model, geometry.Polys);
        var actor = package.CreateExport("Actor", className);
        actor.WriteProperty(new ObjectProperty(model.UIndex, "Brush"));
        actor.WriteProperty(CommonStructs.Vector3Prop(new Vector3(10000, -5000, 2000), "location"));
        actor.WriteProperty(new FloatProperty(3, "DrawScale"));
        byte[] original = actor.Data.ToArray();
        Assert.IsTrue(ActorPreviewControl.CanPreviewModelActor(actor));
        var faces = ModelGeometry.GetActorFaces(actor);
        Assert.HasCount(6, faces);
        Assert.AreEqual(-100, faces.SelectMany(f => f.Vertices).Min(p => p.X));
        Assert.AreEqual(200, faces.SelectMany(f => f.Vertices).Max(p => p.Z));
        CollectionAssert.AreEqual(original, actor.Data, "Preview must not modify the actor's geometry or transform.");
    }

    [TestMethod]
    public void ModelComponentReferenceUsesOnlyItsNodesAndAppliesItsLocalTransform()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ActorModelComponent.pcc", MEGame.LE3);
        var geometry = ModelGeometry.CreatePrism([new(0,0),new(100,0),new(100,100),new(0,100)], 0, 100);
        var model = MeshplorerWindow.AddDrawnModel(package, "Model", null, geometry.Model, geometry.Polys);
        var actor = package.CreateExport("Actor", "Actor");
        var component = package.CreateExport("Component", "ModelComponent", actor);
        var binary = ModelComponent.Create();
        binary.Model = model.UIndex;
        binary.Elements = [new ModelElement { Nodes = [0] }];
        binary.Elements[0].LightMap = new LightMap { LightMapType = 0 };
        binary.Elements[0].ShadowMaps = [];
        binary.Elements[0].IrrelevantLights = [];
        component.WritePropertiesAndBinary(new PropertyCollection { CommonStructs.Vector3Prop(new Vector3(20, 30, 40), "Translation") }, binary);
        actor.WriteProperty(new ArrayProperty<ObjectProperty>([new ObjectProperty(component.UIndex)], "Components"));
        Assert.IsTrue(ActorPreviewControl.CanPreviewModelActor(actor));
        var faces = ModelGeometry.GetActorFaces(actor);
        Assert.HasCount(1, faces);
        Assert.AreEqual(40, faces[0].Vertices.Min(p => p.Z));
        Assert.AreEqual(20, faces[0].Vertices.Min(p => p.X));
    }

    [TestMethod]
    public void SharedCookedModelDoesNotPullOtherVolumesIntoSelectedActorPreview()
    {
        const string path = @"C:\Steam\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\DLC\DLC_MOD_Mordin\CookedPCConsole\BioA_Nor_405Engineering.pcc";
        if (!File.Exists(path)) { Assert.Inconclusive("Optional installed-game fixture is unavailable."); return; }
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        var model = ObjectBinary.From<Model>(package.GetUExport(853));
        int allVolumes = ModelGeometry.GetFaces(model).Count;
        foreach (int index in new[] { 5, 6, 11, 15 })
        {
            var actor = package.GetUExport(index);
            byte[] original = actor.Data.ToArray();
            Assert.IsTrue(ActorPreviewControl.CanPreviewModelActor(actor));
            var faces = ModelGeometry.GetActorFaces(actor);
            Assert.IsTrue(faces.Count > 0 && faces.Count < allVolumes);
            CollectionAssert.AreEqual(original, actor.Data);
        }
    }
}
