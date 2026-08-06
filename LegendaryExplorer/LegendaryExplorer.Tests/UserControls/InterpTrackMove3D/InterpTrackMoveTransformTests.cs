using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Unreal.BinaryConverters;
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

    [TestMethod]
    public void KeyframeDisplayValuesUseWorldSpaceAndEditsRemainLocalInTrack()
    {
        var stage = new CameraOrigin(new Vector3(-2545.6843f, -52040.52f, 1309f),
            new Vector3(0, 0, 90));
        var local = new CameraOrigin(new Vector3(-143.52734f, -16.49707f, 156f),
            new Vector3(0, -2.9882812f, 9.140625f));
        var positionPoint = new InterpCurvePoint<Vector3>(0, local.Location);
        var rotationPoint = new InterpCurvePoint<Vector3>(0, local.Rotation);
        ConstructorInfo constructor = typeof(CurveEditor3DKeyframe).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var keyframe = (CurveEditor3DKeyframe)constructor.Invoke(
            [positionPoint, rotationPoint, local.Rotation, new Action<CurveEditor3DKeyframe, float?>((_, _) => { })]);
        typeof(CurveEditor3DKeyframe).GetMethod("SetCoordinateBasis", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(keyframe, [stage]);

        CameraOrigin expectedWorld = InterpTrackMoveTransform.ToWorld(stage, local);
        Assert.AreEqual(expectedWorld.Location.X, keyframe.DisplayX, 0.001f);
        Assert.AreEqual(expectedWorld.Location.Y, keyframe.DisplayY, 0.001f);
        Assert.AreEqual(expectedWorld.Location.Z, keyframe.DisplayZ, 0.001f);
        Assert.AreEqual(expectedWorld.Rotation.Z, keyframe.DisplayYaw, 0.001f);

        keyframe.DisplayX += 25;
        keyframe.DisplayYaw += 15;

        CameraOrigin expectedLocal = InterpTrackMoveTransform.ToLocal(stage,
            new CameraOrigin(expectedWorld.Location with { X = expectedWorld.Location.X + 25 },
                expectedWorld.Rotation with { Z = expectedWorld.Rotation.Z + 15 }));
        Assert.AreEqual(expectedLocal.Location.X, keyframe.Location.X, 0.001f);
        Assert.AreEqual(expectedLocal.Location.Y, keyframe.Location.Y, 0.001f);
        Assert.AreEqual(expectedLocal.Location.Z, keyframe.Location.Z, 0.001f);
        Assert.AreEqual(expectedLocal.Rotation.Z, keyframe.Rotation.Z, 0.001f);
    }
}
