using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls.InterpTrackMove3D;

[TestClass]
public class InterpTrackMoveTransformTests
{
    [TestMethod]
    public void DialogueOwnerUsesLinkedStageActorTagAsAnAlias()
    {
        var ownerOrigin = new CameraOrigin(new Vector3(-16000, 7800, -170250), new Vector3(0, 0, 180));
        var origins = new Dictionary<string, CameraOrigin>(StringComparer.OrdinalIgnoreCase)
        {
            ["Owner"] = ownerOrigin,
            ["global_miranda"] = ownerOrigin,
            ["Player"] = new CameraOrigin(ownerOrigin.Location with { Z = ownerOrigin.Location.Z + 88 },
                ownerOrigin.Rotation),
        };

        Dictionary<string, HashSet<string>> aliases = CurveEditor3D.BuildActorTagAliases(
            ["owner", "player"], origins);

        Assert.IsTrue(CurveEditor3D.ActorTagMatchesAlias("global_miranda", "owner", aliases));
        Assert.IsTrue(CurveEditor3D.ActorTagMatchesAlias("Owner", "owner", aliases));
        Assert.IsFalse(CurveEditor3D.ActorTagMatchesAlias("global_miranda", "player", aliases));
    }

    [TestMethod]
    public void DialoguePreviewAlwaysUsesAuthoredTrackZ()
    {
        Assert.IsTrue(CurveEditor3D.ShouldUseActorTrackZ(isDialoguePreview: true,
            manualTrackZEnabled: false));
        Assert.IsTrue(CurveEditor3D.ShouldUseActorTrackZ(isDialoguePreview: true,
            manualTrackZEnabled: true));
        Assert.IsFalse(CurveEditor3D.ShouldUseActorTrackZ(isDialoguePreview: false,
            manualTrackZEnabled: false));
        Assert.IsTrue(CurveEditor3D.ShouldUseActorTrackZ(isDialoguePreview: false,
            manualTrackZEnabled: true));
    }

    [TestMethod]
    public void LookAtChangesYawWithoutChangingActorLocation()
    {
        var actor = new CameraOrigin(new Vector3(-15842.27f, 8299.669f, -170309f),
            new Vector3(3f, -4f, 15f));
        var stageNode = new Vector3(-15323.47f, 8973.74f, -170244.2f);

        CameraOrigin faced = CurveEditor3D.ApplyActorDirectionRotation(actor, stageNode,
            includePitch: false, orientationOffset: 0);

        Assert.AreEqual(actor.Location, faced.Location);
        Assert.AreEqual(actor.Rotation.X, faced.Rotation.X, 0.0001f);
        Assert.AreEqual(actor.Rotation.Y, faced.Rotation.Y, 0.0001f);
        Assert.AreNotEqual(actor.Rotation.Z, faced.Rotation.Z);
    }

    [TestMethod]
    public void SetFacingWithoutTrackMoveSnapsActorToPreviewAdjustedStageNode()
    {
        var actor = new CameraOrigin(new Vector3(-15842.27f, 8299.669f, -170309f),
            new Vector3(3f, -4f, 15f));
        var stageNode = new CameraOrigin(new Vector3(-15323.47f, 8973.74f, -170244.2f),
            new Vector3(0, 0, 125f));

        CameraOrigin faced = CurveEditor3D.ApplySetFacingStageNode(actor, stageNode, hasMovementTrack: false,
            orientationOffset: 20f);

        Assert.AreEqual(stageNode.Location.X, faced.Location.X, 0.0001f);
        Assert.AreEqual(stageNode.Location.Y, faced.Location.Y, 0.0001f);
        Assert.AreEqual(stageNode.Location.Z + 88f, faced.Location.Z, 0.0001f);
        Assert.AreEqual(actor.Rotation.X, faced.Rotation.X, 0.0001f);
        Assert.AreEqual(actor.Rotation.Y, faced.Rotation.Y, 0.0001f);
        Assert.AreEqual(145f, faced.Rotation.Z, 0.0001f);
    }

    [TestMethod]
    public void SetFacingWithTrackMoveKeepsEvaluatedActorLocation()
    {
        var actor = new CameraOrigin(new Vector3(-15842.27f, 8299.669f, -170309f),
            new Vector3(3f, -4f, 15f));
        var stageNode = new CameraOrigin(new Vector3(-15323.47f, 8973.74f, -170244.2f),
            new Vector3(0, 0, 125f));

        CameraOrigin faced = CurveEditor3D.ApplySetFacingStageNode(actor, stageNode, hasMovementTrack: true,
            orientationOffset: 20f);

        Assert.AreEqual(actor.Location, faced.Location);
        Assert.AreEqual(145f, faced.Rotation.Z, 0.0001f);
    }

    [TestMethod]
    public void SetFacingBetweenStageKeysComposesAnimationRootMotionFromActiveFacingKey()
    {
        var actor = new CameraOrigin(new Vector3(-15842.27f, 8299.669f, -170309f),
            new Vector3(3f, -4f, 15f));
        var stageNode = new CameraOrigin(new Vector3(-15323.47f, 8973.74f, -170244.2f),
            new Vector3(0, 0, 90f));

        CameraOrigin faced = CurveEditor3D.ApplySetFacingStageNode(actor, stageNode, hasMovementTrack: false,
            orientationOffset: 0, rootMotionSinceFacingKey: new Vector3(100, 0, 0),
            hasFollowingFacingKey: true);

        Assert.AreEqual(stageNode.Location.X, faced.Location.X, 0.0001f);
        Assert.AreEqual(stageNode.Location.Y + 100f, faced.Location.Y, 0.0001f);
        Assert.AreEqual(stageNode.Location.Z + 88f, faced.Location.Z, 0.0001f);
    }

    [TestMethod]
    public void FinalSetFacingKeyDoesNotContinueAnimationRootMotionPastStageNode()
    {
        var actor = new CameraOrigin(new Vector3(-15842.27f, 8299.669f, -170309f),
            new Vector3(3f, -4f, 15f));
        var stageNode = new CameraOrigin(new Vector3(-15323.47f, 8973.74f, -170244.2f),
            new Vector3(0, 0, 90f));

        CameraOrigin faced = CurveEditor3D.ApplySetFacingStageNode(actor, stageNode, hasMovementTrack: false,
            orientationOffset: 0, rootMotionSinceFacingKey: new Vector3(100, 0, 0),
            hasFollowingFacingKey: false);

        Assert.AreEqual(stageNode.Location.X, faced.Location.X, 0.0001f);
        Assert.AreEqual(stageNode.Location.Y, faced.Location.Y, 0.0001f);
        Assert.AreEqual(stageNode.Location.Z + 88f, faced.Location.Z, 0.0001f);
    }

    [TestMethod]
    public void SwitchCameraUsesLastAuthoredStageBoneKeyAtPlaybackTime()
    {
        float[] keyTimes = [0, 3.125f, 3.2083335f];

        Assert.AreEqual(0, CurveEditor3D.GetActiveSwitchCameraKeyIndex(keyTimes, 0));
        Assert.AreEqual(1, CurveEditor3D.GetActiveSwitchCameraKeyIndex(keyTimes, 3.125f));
        Assert.AreEqual(2, CurveEditor3D.GetActiveSwitchCameraKeyIndex(keyTimes, 4.5f));
    }

    [TestMethod]
    public void SwitchCameraUsesNativeConversationFov()
    {
        Assert.AreEqual(52.9f, CurveEditor3D.ConversationSwitchCameraFovDegrees, 0.0001f);
    }

    [TestMethod]
    public void SwitchCameraSeparatesImmediateAndQueuedKeys()
    {
        float[] keyTimes = [0, 0.06f, 4f];
        bool[] queued = [false, true, false];

        Assert.AreEqual(0, CurveEditor3D.GetSwitchCameraKeyIndex(keyTimes, queued, 1f, queued: false));
        Assert.AreEqual(1, CurveEditor3D.GetSwitchCameraKeyIndex(keyTimes, queued, 1f, queued: true));
        Assert.AreEqual(-1, CurveEditor3D.GetSwitchCameraKeyIndex(keyTimes, queued, 0.05f, queued: true));
        Assert.AreEqual(2, CurveEditor3D.GetSwitchCameraKeyIndex(keyTimes, queued, 5f, queued: false));
    }

    [TestMethod]
    public void StageCameraBinaryOffsetsAreAppliedAfterBoneWorldTransform()
    {
        var bone = new CameraOrigin(new Vector3(-13811.018f, 9488.872f, -170329.03f),
            new Vector3(0, -3.1585693f, -168.60168f));

        CameraOrigin camera = StageBoneOriginResolver.ApplyStageCameraOffsets(bone,
            heightDelta: 12f, pitchDelta: 0.8f, yawDelta: -2.5f);

        Assert.AreEqual(bone.Location.X, camera.Location.X, 0.0001f);
        Assert.AreEqual(bone.Location.Y, camera.Location.Y, 0.0001f);
        Assert.AreEqual(bone.Location.Z + 12f, camera.Location.Z, 0.0001f);
        Assert.AreEqual(bone.Rotation.Y + 0.8f, camera.Rotation.Y, 0.0001f);
        Assert.AreEqual(bone.Rotation.Z - 2.5f, camera.Rotation.Z, 0.0001f);
    }

    [TestMethod]
    public void StageCameraBinaryOverridesInheritArchetypeDefaults()
    {
        var archetypeDefaults = new PropertyCollection
        {
            new FloatProperty(35f, "fFov"),
            new FloatProperty(0f, "fPitchDelta"),
            new FloatProperty(0f, "fYawDelta"),
            new BoolProperty(false, "bDisableHeightAdjustment")
        };
        var placedStageOverrides = new PropertyCollection
        {
            new FloatProperty(0.8f, "fPitchDelta")
        };

        StageCameraSettings settings = StageBoneOriginResolver.ResolveStageCameraSettings(
            [archetypeDefaults, placedStageOverrides]);

        Assert.AreEqual(35f, settings.FovDegrees.GetValueOrDefault(), 0.0001f);
        Assert.AreEqual(0.8f, settings.PitchDelta, 0.0001f);
        Assert.AreEqual(0f, settings.YawDelta, 0.0001f);
        Assert.IsFalse(settings.DisableHeightAdjustment);
    }

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
