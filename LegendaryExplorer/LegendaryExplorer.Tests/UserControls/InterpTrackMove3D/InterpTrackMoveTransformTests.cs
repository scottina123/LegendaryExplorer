using System.Numerics;
using LegendaryExplorer.Tools.InterpEditor;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls.InterpTrackMove3D;

[TestClass]
public class InterpTrackMoveTransformTests
{
    [TestMethod]
    public void AnchorObjectTrackOriginIsComposedWithStageTransform()
    {
        var stage = new CameraOrigin(new Vector3(-2545.6843f, -52040.52f, 1309f),
            new Vector3(0, 0, 90));
        var track = new CameraOrigin(new Vector3(-143.52734f, -16.49707f, 156f),
            new Vector3(0, -2.9882812f, 9.140625f));

        CameraOrigin world = InterpTrackMoveTransform.ToWorld(stage, track);

        Assert.AreEqual(-2529.1873f, world.Location.X, 0.001f);
        Assert.AreEqual(-52184.047f, world.Location.Y, 0.001f);
        Assert.AreEqual(1465f, world.Location.Z, 0.001f);
        Assert.AreEqual(99.140625f, world.Rotation.Z, 0.001f);
    }

    [TestMethod]
    public void WorldOriginCanBeConvertedBackToAnchorLocalValues()
    {
        var stage = new CameraOrigin(new Vector3(1024, -2048, 512), new Vector3(5, -10, 135));
        var track = new CameraOrigin(new Vector3(-125, 80, 175), new Vector3(2, -4, 25));

        CameraOrigin roundTrip = InterpTrackMoveTransform.ToLocal(stage,
            InterpTrackMoveTransform.ToWorld(stage, track));

        Assert.AreEqual(track.Location.X, roundTrip.Location.X, 0.001f);
        Assert.AreEqual(track.Location.Y, roundTrip.Location.Y, 0.001f);
        Assert.AreEqual(track.Location.Z, roundTrip.Location.Z, 0.001f);
        Assert.AreEqual(track.Rotation.X, roundTrip.Rotation.X, 0.001f);
        Assert.AreEqual(track.Rotation.Y, roundTrip.Rotation.Y, 0.001f);
        Assert.AreEqual(track.Rotation.Z, roundTrip.Rotation.Z, 0.001f);
    }
}
