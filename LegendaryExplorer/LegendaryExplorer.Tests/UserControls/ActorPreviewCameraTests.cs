using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ActorPreviewCameraTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-31744)]
    [DataRow(16384)]
    [DataRow(-16384)]
    [DataRow(25600)]
    [DataRow(-43008)] // SFXStuntActor_5 in BioD_CitHub_Dock.
    public void CameraFramesActorFromItsOwnFront(int yaw)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ActorPreview.pcc", MEGame.LE3);
        var actor = CreateActor(package, yaw);
        BoxSphereBounds bounds = new() { Origin = new Vector3(10, 20, 80), SphereRadius = 100 };
        Matrix4x4 originalTransform = actor.LocalToWorld;

        Vector3 position = ActorPreviewControl.GetPreviewCameraPosition(actor, bounds);
        AssertCameraInFront(position, bounds, actor.LocalToWorld);
        Assert.AreEqual(originalTransform, actor.LocalToWorld);
        Assert.IsFalse(actor.IsDirty);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CameraUsesDisplayedComponentRotationAndIgnoresScaleForDistance(bool absoluteRotation)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ActorPreview.pcc", MEGame.LE3);
        var actor = CreateActor(package, -43008);
        ExportEntry componentExport = package.CreateExport("BodySMC", "SkeletalMeshComponent", actor.Export, indexed: false);
        componentExport.WriteProperties(new PropertyCollection
        {
            CommonStructs.RotatorProp(new Rotator(4096, 8192, 2048), "Rotation"),
            new BoolProperty(absoluteRotation, "AbsoluteRotation"),
            new FloatProperty(3f, "Scale")
        });
        var component = new SkeletalMeshComponentProxy(null, componentExport, actor);
        actor.Components.Add(component);
        BoxSphereBounds bounds = new() { Origin = new Vector3(10, 20, 80), SphereRadius = 100 };

        Vector3 position = ActorPreviewControl.GetPreviewCameraPosition(actor, bounds);

        AssertCameraInFront(position, bounds, component.LocalToWorld);
        Assert.AreEqual(220f, Vector3.Distance(bounds.Origin, position), 0.001f);
    }

    [TestMethod]
    public void CameraRemainsUsableWithZeroScaleAndEmptyBounds()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ActorPreview.pcc", MEGame.LE3);
        var actor = CreateActor(package, 0);
        actor.LocalToWorld = Matrix4x4.CreateScale(0);
        BoxSphereBounds bounds = new() { Origin = new Vector3(10, 20, 80) };

        Vector3 position = ActorPreviewControl.GetPreviewCameraPosition(actor, bounds);

        Assert.AreEqual(bounds.Origin + new Vector3(100, 0, 0), position);
    }

    private static PreviewActor CreateActor(IMEPackage package, int yaw)
    {
        ExportEntry export = package.CreateExport("PreviewActor", "SFXStuntActor", indexed: false);
        export.WriteProperties(new PropertyCollection
        {
            CommonStructs.RotatorProp(new Rotator(0, yaw, 0), "Rotation")
        });
        return new PreviewActor(export);
    }

    private static void AssertCameraInFront(Vector3 position, BoxSphereBounds bounds, Matrix4x4 meshToWorld)
    {
        Assert.IsTrue(Matrix4x4.Invert(meshToWorld, out Matrix4x4 worldToMesh));
        Vector3 localOffset = Vector3.TransformNormal(position - bounds.Origin, worldToMesh);
        Assert.IsGreaterThan(0f, localOffset.X);
        Assert.AreEqual(0f, localOffset.Y, 0.001f);
        Assert.AreEqual(0f, localOffset.Z, 0.001f);

        var camera = new SceneCamera { FirstPerson = true, Position = position };
        camera.OrientTowards(bounds.Origin);
        Vector3 target = Vector3.Transform(bounds.Origin, camera.ViewMatrix);
        Assert.AreEqual(0f, target.X, 0.001f);
        Assert.AreEqual(0f, target.Y, 0.001f);
        Assert.IsGreaterThan(0f, target.Z);
    }

    private sealed class PreviewActor(ExportEntry export) : ActorProxy(null, export);
}
