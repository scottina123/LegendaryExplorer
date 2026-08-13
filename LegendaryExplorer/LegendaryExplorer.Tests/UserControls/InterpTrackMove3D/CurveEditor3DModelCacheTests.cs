using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace LegendaryExplorer.Tests.UserControls.InterpTrackMove3D;

[TestClass]
public class CurveEditor3DModelCacheTests
{
    [TestMethod]
    public void StageSlotInvertsTheBoundActorsBodyComponentPivot()
    {
        var slot = new CameraOrigin(new Vector3(20610.672f, -7097.9004f, 1614.0001f),
            new Vector3(0, 0, 51.119385f));
        Matrix4x4 bodyLocal = Matrix4x4.CreateTranslation(0, 0,
            StageBoneOriginResolver.StuntActorBodyMeshRelativeZ);

        CameraOrigin actor = StageBoneOriginResolver.ResolveActorOriginFromStageSlot(slot, bodyLocal);

        Assert.AreEqual(slot.Location.X, actor.Location.X, 0.001f);
        Assert.AreEqual(slot.Location.Y, actor.Location.Y, 0.001f);
        Assert.AreEqual(slot.Location.Z + 88f, actor.Location.Z, 0.001f);
        Matrix4x4 actorTransform = ActorUtils.ComposeLocalToWorld(actor.Location,
            Rotator.FromDegreesVector(actor.Rotation), Vector3.One);
        Assert.AreEqual(slot.Location.Z, (bodyLocal * actorTransform).Translation.Z, 0.001f);
    }

    [TestMethod]
    public void StageSlotDoesNotMoveAnActorWhoseBodyComponentIsIdentity()
    {
        var slot = new CameraOrigin(new Vector3(10, 20, 30), new Vector3(0, 0, 45));

        CameraOrigin actor = StageBoneOriginResolver.ResolveActorOriginFromStageSlot(slot, Matrix4x4.Identity);

        Assert.AreEqual(slot.Location.X, actor.Location.X, 0.001f);
        Assert.AreEqual(slot.Location.Y, actor.Location.Y, 0.001f);
        Assert.AreEqual(slot.Location.Z, actor.Location.Z, 0.001f);
        Assert.AreEqual(slot.Rotation.Z, actor.Rotation.Z, 0.01f);
    }

    [TestMethod]
    public void GestureRootTranslationIsExtractedForSplineDrivenActors()
    {
        Assert.IsTrue(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 2));
    }

    [TestMethod]
    public void VisibleSplinePlaybackKeepsAuthoredLocomotionOnTheSkeletalRoot()
    {
        Assert.IsFalse(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 2, isCacheEvaluation: false));
    }

    [TestMethod]
    public void GestureRootTranslationIsExtractedForOffStageSingleKeyActorAnchor()
    {
        Assert.IsTrue(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 1, oneKeyTrackOwnsLocomotion: true));
        Assert.IsTrue(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 1, isCacheEvaluation: false,
            oneKeyTrackOwnsLocomotion: true));
        Assert.IsFalse(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 1, oneKeyTrackOwnsLocomotion: false));
    }

    [TestMethod]
    public void GestureRootTranslationIsNotExtractedWithoutAnActorTrackMove()
    {
        Assert.IsFalse(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 0));
        Assert.IsFalse(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: false, movementKeyCount: 0));
    }

    [TestMethod]
    public void LookAtDoesNotRotateThePawnTransform()
    {
        Assert.IsFalse(CurveEditor3D.DirectionTrackControlsActorTransform(isLookAt: true));
        Assert.IsTrue(CurveEditor3D.DirectionTrackControlsActorTransform(isLookAt: false));
    }

    [TestMethod]
    public void LookAtTargetIsInheritedUntilAnAuthoredKeyOverridesOrClearsIt()
    {
        Assert.AreEqual("lookat_leaning", CurveEditor3D.ResolveInheritedLookAtTarget(
            "lookat_leaning", [], time: 2));
        Assert.AreEqual("player", CurveEditor3D.ResolveInheritedLookAtTarget(
            "lookat_leaning", [(1f, true, "player")], time: 2));
        Assert.IsNull(CurveEditor3D.ResolveInheritedLookAtTarget(
            "lookat_leaning", [(1f, false, null)], time: 2));
    }

    [TestMethod]
    public void FutureLookAtKeyDoesNotOverrideInheritedTargetEarly()
    {
        Assert.AreEqual("lookat_leaning", CurveEditor3D.ResolveInheritedLookAtTarget(
            "lookat_leaning", [(3f, true, "player")], time: 2));
        Assert.AreEqual("player", CurveEditor3D.ResolveInheritedLookAtTarget(
            "lookat_leaning", [(3f, true, "player")], time: 3));
    }

    [TestMethod]
    public void LookAtBoneRotationPreservesTheHeadPosition()
    {
        Matrix4x4 headTransform = Matrix4x4.CreateTranslation(10, 20, 30);

        Matrix4x4 rotated = CurveEditor3D.ApplyLookAtBoneRotation(headTransform,
            new Vector3(100, 120, 80));

        Assert.AreEqual(headTransform.Translation, rotated.Translation);
        Assert.AreNotEqual(headTransform, rotated);
        Assert.AreEqual(headTransform, CurveEditor3D.ApplyLookAtBoneRotation(headTransform,
            headTransform.Translation));
    }

    [TestMethod]
    public void ExtractedGestureMotionContinuesFromInheritedActorOrigin()
    {
        var inherited = new CameraOrigin(new Vector3(100, 200, 300), new Vector3(0, 0, 90));

        CameraOrigin moved = CurveEditor3D.ApplyDialogueGestureRootMotion(inherited,
            new Vector3(25, 0, 0));

        Assert.AreEqual(100, moved.Location.X, 0.001f);
        Assert.AreEqual(225, moved.Location.Y, 0.001f);
        Assert.AreEqual(300, moved.Location.Z, 0.001f);
        Assert.AreEqual(inherited.Rotation, moved.Rotation);
    }

    [TestMethod]
    public void StageAttachedPlayerRootMotionAlignsItsAuthoredAxesToItsSlot()
    {
        var track = new CameraOrigin(new Vector3(21671.81f, -6342.2065f, 1696),
            new Vector3(0, 0, -161.47705f));
        var playerSlot = new CameraOrigin(new Vector3(20982.588f, -6657.2593f, 1702.0001f),
            new Vector3(0, 0, -133.15979f));
        var authoredRootDelta = new Vector3(-203.01079f, -237.88031f, -9.859883f);

        Assert.IsTrue(CurveEditor3D.IsStageAttachedPlayerTrackDisplaced(track, playerSlot));

        float yawOffset = CurveEditor3D.CalculateStageAttachedRootMotionYawOffset(track, playerSlot,
            authoredRootDelta);
        Vector3 alignedRootDelta = CurveEditor3D.RotateDialogueGestureRootMotion(authoredRootDelta, yawOffset);
        CameraOrigin moved = CurveEditor3D.ApplyDialogueGestureRootMotion(track, alignedRootDelta);
        var targetDirection = new Vector2(playerSlot.Location.X - track.Location.X,
            playerSlot.Location.Y - track.Location.Y);
        var movementDirection = new Vector2(moved.Location.X - track.Location.X,
            moved.Location.Y - track.Location.Y);

        float normalizedCross = (targetDirection.X * movementDirection.Y
                                 - targetDirection.Y * movementDirection.X)
                                / (targetDirection.Length() * movementDirection.Length());
        Assert.AreEqual(0f, normalizedCross, 0.0001f);
        Assert.IsTrue(Vector2.Dot(targetDirection, movementDirection) > 0f);

        CameraOrigin faced = CurveEditor3D.ApplyDialogueGestureRootMotionFacing(moved, 180f);
        Assert.AreEqual(moved.Location, faced.Location);
        Assert.AreEqual(18.52295f, faced.Rotation.Z, 0.0001f);
    }

    [TestMethod]
    public void PlayerTrackAlreadyOnItsStageSlotDoesNotOwnGestureLocomotion()
    {
        var dockPlayerTrack = new CameraOrigin(new Vector3(-16058.323f, 7772.323f, -170311.8f),
            new Vector3(0, 0, -140.00977f));
        var dockPlayerSlot = new CameraOrigin(new Vector3(-16058.323f, 7772.323f, -170303f),
            new Vector3(0, 0, -139.99878f));

        Assert.IsFalse(CurveEditor3D.IsStageAttachedPlayerTrackDisplaced(dockPlayerTrack, dockPlayerSlot));
        Assert.IsFalse(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 1,
            oneKeyTrackOwnsLocomotion: CurveEditor3D.IsStageAttachedPlayerTrackDisplaced(
                dockPlayerTrack, dockPlayerSlot)));
    }

    [TestMethod]
    public void InitialOffStagePlayerTrackMayUseItsBoundStageSlotAsRootMotionDestination()
    {
        var track = new CameraOrigin(new Vector3(21671.81f, -6342.2065f, 1696),
            new Vector3(0, 0, -161.47705f));
        var playerSlot = new CameraOrigin(new Vector3(20982.588f, -6657.2593f, 1702.0001f),
            new Vector3(0, 0, -133.15979f));
        var inherited = new CameraOrigin(playerSlot.Location with { Z = playerSlot.Location.Z + 88f },
            playerSlot.Rotation);

        Assert.IsTrue(CurveEditor3D.ShouldAlignStageAttachedPlayerRootMotion(track, playerSlot, inherited));
    }

    [TestMethod]
    public void LaterDockPlayerAnchorDoesNotWalkBackTowardOriginalStageSlot()
    {
        var dockE9Track = new CameraOrigin(new Vector3(-14659.821f, 8792.842f, -170309f), Vector3.Zero);
        var initialPlayerSlot = new CameraOrigin(new Vector3(-16058.323f, 7772.323f, -170303f),
            new Vector3(0, 0, -139.99878f));
        var inheritedFromE8 = new CameraOrigin(new Vector3(-14994.821f, 8815.842f, -170309f), Vector3.Zero);

        Assert.IsFalse(CurveEditor3D.ShouldAlignStageAttachedPlayerRootMotion(
            dockE9Track, initialPlayerSlot, inheritedFromE8));
        Assert.IsFalse(CurveEditor3D.ShouldExtractDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 1,
            oneKeyTrackOwnsLocomotion: CurveEditor3D.ShouldAlignStageAttachedPlayerRootMotion(
                dockE9Track, initialPlayerSlot, inheritedFromE8)));
    }

    [TestMethod]
    public void StageAttachedPlayerRootMotionKeepsAnAlreadyAlignedForwardDelta()
    {
        var track = new CameraOrigin(Vector3.Zero, Vector3.Zero);
        var playerSlot = new CameraOrigin(new Vector3(500, 0, 0), Vector3.Zero);

        Vector3 rootDelta = new(100, 0, 0);
        float yawOffset = CurveEditor3D.CalculateStageAttachedRootMotionYawOffset(track, playerSlot, rootDelta);

        Assert.AreEqual(0f, yawOffset, 0.0001f);
        Assert.AreEqual(rootDelta, CurveEditor3D.RotateDialogueGestureRootMotion(rootDelta, yawOffset));
    }

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void CachedTrackMoveEditsWaitForExplicitCommit()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("CurveEditor3DCacheTest.pcc", MEGame.LE3);
        var position = new InterpCurve<Vector3>();
        position.Points.Add(new InterpCurvePoint<Vector3>(0, new Vector3(10, 20, 30)));
        var rotation = new InterpCurve<Vector3>();
        rotation.Points.Add(new InterpCurvePoint<Vector3>(0, Vector3.Zero));
        var lookupPoints = new ArrayProperty<StructProperty>("Points");
        lookupPoints.Add(new StructProperty("InterpLookupPoint", false,
            new NameProperty("None", "GroupName"), new FloatProperty(0, "Time")));
        var lookupTrack = new StructProperty("InterpLookupTrack", new PropertyCollection { lookupPoints },
            "LookupTrack");
        ExportEntry export = package.CreateExport("MoveTrack", "InterpTrackMove", indexed: false);
        export.WriteProperties(new PropertyCollection
        {
            position.ToStructProperty(package.Game, "PosTrack"),
            rotation.ToStructProperty(package.Game, "EulerTrack"),
            lookupTrack,
        });
        var model = new CurveEditor3DModel { AutoCommit = false };
        model.Load(export);

        model.Keyframes[0].X = 250;

        Assert.IsTrue(model.HasPendingChanges);
        InterpCurve<Vector3> beforeCommit = InterpCurve<Vector3>.FromStructProperty(
            export.GetProperty<StructProperty>("PosTrack"), package.Game);
        Assert.AreEqual(10, beforeCommit.Points[0].OutVal.X, 0.001f);

        model.CommitChanges();

        Assert.IsFalse(model.HasPendingChanges);
        InterpCurve<Vector3> afterCommit = InterpCurve<Vector3>.FromStructProperty(
            export.GetProperty<StructProperty>("PosTrack"), package.Game);
        Assert.AreEqual(250, afterCommit.Points[0].OutVal.X, 0.001f);
    }

    [TestMethod]
    public void MultiKeyTrackMoveChangesActorLocationBetweenKeyframes()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("MultiKeyTrackMoveTest.pcc", MEGame.LE3);
        var position = new InterpCurve<Vector3>();
        position.Points.Add(new InterpCurvePoint<Vector3>(0, new Vector3(100, 200, 300),
            Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Linear));
        position.Points.Add(new InterpCurvePoint<Vector3>(2, new Vector3(500, 600, 700),
            Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Linear));
        var rotation = new InterpCurve<Vector3>();
        rotation.Points.Add(new InterpCurvePoint<Vector3>(0, Vector3.Zero));
        rotation.Points.Add(new InterpCurvePoint<Vector3>(2, new Vector3(0, 0, 90)));
        var lookupPoints = new ArrayProperty<StructProperty>("Points");
        lookupPoints.Add(new StructProperty("InterpLookupPoint", false,
            new NameProperty("None", "GroupName"), new FloatProperty(0, "Time")));
        lookupPoints.Add(new StructProperty("InterpLookupPoint", false,
            new NameProperty("None", "GroupName"), new FloatProperty(2, "Time")));
        ExportEntry export = package.CreateExport("MoveTrack", "InterpTrackMove", indexed: false);
        export.WriteProperties(new PropertyCollection
        {
            position.ToStructProperty(package.Game, "PosTrack"),
            rotation.ToStructProperty(package.Game, "EulerTrack"),
            new StructProperty("InterpLookupTrack", new PropertyCollection { lookupPoints }, "LookupTrack"),
        });
        var model = new CurveEditor3DModel();
        model.Load(export);

        CameraOrigin start = CurveEditor3D.EvaluateTrackMove(model, 0);
        CameraOrigin midpoint = CurveEditor3D.EvaluateTrackMove(model, 1);
        CameraOrigin end = CurveEditor3D.EvaluateTrackMove(model, 2);

        Assert.AreEqual(new Vector3(100, 200, 300), start.Location);
        Assert.AreEqual(new Vector3(300, 400, 500), midpoint.Location);
        Assert.AreEqual(new Vector3(500, 600, 700), end.Location);
        Assert.AreNotEqual(start.Location, midpoint.Location);
        Assert.AreNotEqual(midpoint.Location, end.Location);
    }

    [TestMethod]
    public void CameraTrackMoveCannotBeAssignedToOwnerOrPlayer()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("ActorCameraAssignmentTest.pcc", MEGame.LE3);
        ExportEntry actorTrack = package.CreateExport("OwnerMove", "InterpTrackMove", indexed: false);
        ExportEntry cameraTrack = package.CreateExport("Cam1Move", "InterpTrackMove", indexed: false);

        Assert.IsTrue(CurveEditor3D.IsEligibleActorTrackMove(actorTrack, [cameraTrack]));
        Assert.IsFalse(CurveEditor3D.IsEligibleActorTrackMove(cameraTrack, [cameraTrack]));
    }

    [TestMethod]
    public void EmptyCameraStubGroupCannotParticipateInActorMatching()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("EmptyCameraGroupTest.pcc", MEGame.LE3);
        ExportEntry emptyCameraGroup = package.CreateExport("Cam2", "InterpGroup", indexed: false);
        emptyCameraGroup.WriteProperties(new PropertyCollection
        {
            new NameProperty("cam2", "GroupName"),
            new NameProperty("Cam_2", "m_nmSFXFindActor"),
            new ArrayProperty<ObjectProperty>("InterpTracks"),
        });
        ExportEntry actorGroup = package.CreateExport("Owner", "InterpGroup", indexed: false);
        ExportEntry actorTrack = package.CreateExport("OwnerMove", "InterpTrackMove", indexed: false);
        actorGroup.WriteProperties(new PropertyCollection
        {
            new NameProperty("Miranda", "GroupName"),
            new NameProperty("Owner", "m_nmSFXFindActor"),
            new ArrayProperty<ObjectProperty>("InterpTracks") { new(actorTrack) },
        });

        Assert.IsFalse(CurveEditor3D.IsActorMatchingInterpGroup(emptyCameraGroup));
        Assert.IsTrue(CurveEditor3D.IsActorMatchingInterpGroup(actorGroup));
    }

    [TestMethod]
    public void EmptyCameraStubStillParticipatesInDirectorPlayback()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("EmptyDirectorCameraGroupTest.pcc",
            MEGame.LE3);
        ExportEntry emptyCameraGroup = package.CreateExport("Cam1", "InterpGroup", indexed: false);
        emptyCameraGroup.WriteProperties(new PropertyCollection
        {
            new NameProperty("Cam1", "GroupName"),
            new NameProperty("Cam_2", "m_nmSFXFindActor"),
            new ArrayProperty<ObjectProperty>("InterpTracks"),
        });

        string actorTag = CurveEditor3D.GetCameraActorTag(emptyCameraGroup);

        Assert.AreEqual("Cam_2", actorTag);
        Assert.IsTrue(CurveEditor3D.ShouldRetainDirectorCameraCut(hasTrackMove: false,
            hasSwitchCamera: false, actorTag));
        Assert.IsFalse(CurveEditor3D.ShouldRetainDirectorCameraCut(hasTrackMove: false,
            hasSwitchCamera: false, cameraActorTag: null));
    }

    [TestMethod]
    public void StubCameraFallbackSurvivesCacheSerialization()
    {
        var source = new DialogueDirectorCutCache
        {
            Time = 1.25f,
            GroupName = "Cam1",
            CameraActorTag = "mircam1",
            CameraActor = new PackageExportReference
            {
                PackagePath = @"C:\BioD_Test.pcc",
                UIndex = 42,
                InstancedFullPath = "TheWorld.PersistentLevel.CameraActor_0",
                ClassName = "CameraActor",
            },
            FallbackOrigin = new DialogueOriginCache
            {
                Location = new Vector3(10, 20, 30),
                Rotation = new Vector3(0, -5, 170),
            },
            FallbackFovDegrees = 47f,
        };

        DialogueDirectorCutCache restored = JsonConvert.DeserializeObject<DialogueDirectorCutCache>(
            JsonConvert.SerializeObject(source));

        Assert.AreEqual(source.CameraActorTag, restored.CameraActorTag);
        Assert.AreEqual(source.CameraActor.UIndex, restored.CameraActor.UIndex);
        Assert.AreEqual(source.FallbackOrigin.Location, restored.FallbackOrigin.Location);
        Assert.AreEqual(source.FallbackOrigin.Rotation, restored.FallbackOrigin.Rotation);
        Assert.AreEqual(source.FallbackFovDegrees, restored.FallbackFovDegrees);
    }

    [TestMethod]
    public void CameraSeedPrefersLevelActorThenAuthoredTrackThenSavedFallback()
    {
        var placed = new CameraOrigin(new Vector3(1, 0, 0), Vector3.Zero);
        var authored = new CameraOrigin(new Vector3(2, 0, 0), Vector3.Zero);
        var cached = new CameraOrigin(new Vector3(3, 0, 0), Vector3.Zero);
        var viewport = new CameraOrigin(new Vector3(4, 0, 0), Vector3.Zero);

        Assert.AreEqual(placed, CurveEditor3D.ResolveDialogueCameraSeed(placed, authored, cached, viewport));
        Assert.AreEqual(authored, CurveEditor3D.ResolveDialogueCameraSeed(null, authored, cached, viewport));
        Assert.AreEqual(cached, CurveEditor3D.ResolveDialogueCameraSeed(null, null, cached, viewport));
        Assert.AreEqual(viewport, CurveEditor3D.ResolveDialogueCameraSeed(null, null, null, viewport));
        Assert.AreEqual(47f, CurveEditor3D.ResolveDialogueCameraFovSeed(null, 47f, 55f, 60f));
    }

    [TestMethod]
    public void SharedConversationDirectionTrackUsesItsOwnActorBinding()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("DirectionTrackActorTest.pcc", MEGame.LE3);
        ExportEntry playerFacing = package.CreateExport("PlayerFacing", "BioEvtSysTrackSetFacing", indexed: false);
        playerFacing.WriteProperties(new PropertyCollection
        {
            new NameProperty("Player", "m_nmFindActor"),
        });
        ExportEntry ownerFacing = package.CreateExport("OwnerFacing", "BioEvtSysTrackSetFacing", indexed: false);
        ownerFacing.WriteProperties(new PropertyCollection
        {
            new NameProperty("Owner", "m_nmFindActor"),
        });
        ExportEntry unbound = package.CreateExport("UnboundFacing", "BioEvtSysTrackSetFacing", indexed: false);

        Assert.AreEqual("Player", CurveEditor3D.GetDirectionTrackActorTag(playerFacing));
        Assert.AreEqual("Owner", CurveEditor3D.GetDirectionTrackActorTag(ownerFacing));
        Assert.IsNull(CurveEditor3D.GetDirectionTrackActorTag(unbound));
    }

    [TestMethod]
    public void MissingInterpLengthUsesLatestVoOrFaceOnlyVoTimePlusOneSecond()
    {
        Assert.AreEqual(6f, CurveEditor3D.ResolveDialogueNodeFallbackDuration(
            interpLength: 0, voStartTime: 0, voDuration: 5, lastFaceOnlyVoKeyTime: 3));
        Assert.AreEqual(9f, CurveEditor3D.ResolveDialogueNodeFallbackDuration(
            interpLength: 0, voStartTime: 0.5f, voDuration: 4, lastFaceOnlyVoKeyTime: 8));
        Assert.AreEqual(4f, CurveEditor3D.ResolveDialogueNodeFallbackDuration(
            interpLength: 4, voStartTime: 0, voDuration: 20, lastFaceOnlyVoKeyTime: 30));
        Assert.AreEqual(0.1f, CurveEditor3D.ResolveDialogueNodeFallbackDuration(
            interpLength: 0, voStartTime: 0, voDuration: 0, lastFaceOnlyVoKeyTime: 0));
    }

    [TestMethod]
    public void LastFaceOnlyVoKeyIsReadAcrossAllNodeInterps()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("FaceOnlyVoDurationTest.pcc", MEGame.LE3);
        ExportEntry firstInterp = CreateFaceOnlyVoInterp(package, "First", 1.5f, 4.25f);
        ExportEntry secondInterp = CreateFaceOnlyVoInterp(package, "Second", 3f, 7.5f);

        Assert.AreEqual(7.5f, CurveEditor3D.GetLastFaceOnlyVoKeyTime([firstInterp, secondInterp]));
    }

    [TestMethod]
    public void FovIdentifiesCameraTrackWhenGroupNameDoesNotStartWithCam()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("FovCameraGroupTest.pcc", MEGame.LE3);
        ExportEntry pcam = package.CreateExport("InterpGroup_0", "InterpGroup", indexed: false);
        pcam.WriteProperties(new PropertyCollection
        {
            new NameProperty("pcam", "GroupName"),
            new NameProperty("mircam1", "m_nmSFXFindActor"),
        });
        ExportEntry owner = package.CreateExport("InterpGroup_1", "InterpGroup", indexed: false);
        owner.WriteProperties(new PropertyCollection
        {
            new NameProperty("Miranda", "GroupName"),
            new NameProperty("Owner", "m_nmSFXFindActor"),
        });

        Assert.IsTrue(CurveEditor3D.IsCameraTrackGroup(pcam, hasFovTrack: true));
        Assert.IsFalse(CurveEditor3D.IsCameraTrackGroup(owner, hasFovTrack: false));
    }

    [TestMethod]
    public void PlayerOnlyAcceptsTrackMovesOwnedByPlayerGroup()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("StrictPlayerTrackGroupTest.pcc", MEGame.LE3);
        ExportEntry playerGroup = package.CreateExport("InterpGroup_0", "InterpGroup", indexed: false);
        playerGroup.WriteProperty(new NameProperty("Player", "GroupName"));
        ExportEntry duplicatePlayerGroup = package.CreateExport("InterpGroup_3", "InterpGroup", indexed: false);
        duplicatePlayerGroup.WriteProperties(new PropertyCollection
        {
            new NameProperty("Player0", "GroupName"),
            new NameProperty("Player", "m_nmSFXFindActor"),
        });
        ExportEntry cameraGroup = package.CreateExport("InterpGroup_1", "InterpGroup", indexed: false);
        cameraGroup.WriteProperties(new PropertyCollection
        {
            new NameProperty("pcam", "GroupName"),
            new NameProperty("Player", "m_nmSFXFindActor"),
        });
        ExportEntry ownerGroup = package.CreateExport("InterpGroup_2", "InterpGroup", indexed: false);
        ownerGroup.WriteProperties(new PropertyCollection
        {
            new NameProperty("Miranda", "GroupName"),
            new NameProperty("Owner", "m_nmSFXFindActor"),
        });

        Assert.IsTrue(CurveEditor3D.IsEligibleActorTrackGroup(playerGroup, "player"));
        Assert.IsTrue(CurveEditor3D.IsEligibleActorTrackGroup(duplicatePlayerGroup, "player"));
        Assert.IsFalse(CurveEditor3D.IsEligibleActorTrackGroup(cameraGroup, "player"));
        Assert.IsFalse(CurveEditor3D.IsEligibleActorTrackGroup(ownerGroup, "player"));
        Assert.IsTrue(CurveEditor3D.IsEligibleActorTrackGroup(ownerGroup, "owner"));
    }

    [TestMethod]
    public void SceneShopBranchRequiresExplicitSelectionAndRetainsSharedGroups()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SceneShopSelectionTest.pcc", MEGame.LE3);
        ExportEntry sequence = package.CreateExport("Node_Data_Sequence", "Sequence", indexed: false);
        ExportEntry variable = package.CreateExport("SeqVar_Bool_0", "SeqVar_Bool", sequence, indexed: false);
        variable.WriteProperty(new NameProperty("MirandaAlive", "VarName"));
        ExportEntry interp = package.CreateExport("InterpData_0", "InterpData", sequence, indexed: false);
        ExportEntry sharedGroup = package.CreateExport("InterpGroup_Shared", "InterpGroup", interp, indexed: false);
        ExportEntry aliveScene = package.CreateExport("SFXSceneGroup_0", "SFXSceneGroup", interp, indexed: false);
        ExportEntry alivePlayer = package.CreateExport("InterpGroup_0", "InterpGroup", interp, indexed: false);
        ExportEntry deadScene = package.CreateExport("SFXSceneGroup_1", "SFXSceneGroup", interp, indexed: false);
        ExportEntry deadPlayer = package.CreateExport("InterpGroup_1", "InterpGroup", interp, indexed: false);
        aliveScene.WriteProperty(new NameProperty("Miranda_Alive", "GroupName"));
        alivePlayer.WriteProperty(new NameProperty("Player", "GroupName"));
        deadScene.WriteProperty(new NameProperty("Miranda_Dead", "GroupName"));
        deadPlayer.WriteProperty(new NameProperty("Player0", "GroupName"));
        interp.WriteProperty(new ArrayProperty<ObjectProperty>("InterpGroups")
        {
            new(sharedGroup.UIndex), new(aliveScene.UIndex), new(alivePlayer.UIndex),
            new(deadScene.UIndex), new(deadPlayer.UIndex),
        });

        ExportEntry gameData = package.CreateExport("SFXSceneShopGameData_0", "SFXSceneShopGameData", interp,
            indexed: false);
        ExportEntry start = package.CreateExport("SFXSceneShopNodeStart_0", "SFXSceneShopNodeStart", gameData,
            indexed: false);
        ExportEntry check = package.CreateExport("SFXSceneShopNodeKisVarCheck_0", "SFXSceneShopNodeKisVarCheck",
            gameData, indexed: false);
        ExportEntry aliveNode = package.CreateExport("SFXSceneShopNodeScene_0", "SFXSceneShopNodeScene", gameData,
            indexed: false);
        ExportEntry deadNode = package.CreateExport("SFXSceneShopNodeScene_1", "SFXSceneShopNodeScene", gameData,
            indexed: false);
        start.WriteProperty(CreateSceneShopOutputPins(("Out", check)));
        check.WriteProperties(new PropertyCollection
        {
            new NameProperty("MirandaAlive", "m_nmKismetBoolVarName"),
            CreateSceneShopOutputPins(("True", aliveNode), ("False", deadNode)),
        });
        aliveNode.WriteProperty(new ObjectProperty(aliveScene.UIndex, "m_pLinkedScene"));
        deadNode.WriteProperty(new ObjectProperty(deadScene.UIndex, "m_pLinkedScene"));
        gameData.WriteProperty(new ArrayProperty<ObjectProperty>("m_aNodes")
        {
            new(start.UIndex), new(check.UIndex), new(aliveNode.UIndex), new(deadNode.UIndex),
        });

        CollectionAssert.AreEqual(new[] { sharedGroup },
            CurveEditor3D.GetActiveInterpGroups(interp, selectedSceneGroup: null).ToArray());
        CollectionAssert.AreEqual(new[] { sharedGroup, deadPlayer },
            CurveEditor3D.GetActiveInterpGroups(interp, deadScene).ToArray());
        variable.WriteProperty(new IntProperty(1, "bValue"));
        CollectionAssert.AreEqual(new[] { sharedGroup, deadPlayer },
            CurveEditor3D.GetActiveInterpGroups(interp, deadScene).ToArray(),
            "Changing the serialized Kismet default must not replace the editor's retained branch choice.");
        CollectionAssert.AreEqual(new[] { sharedGroup, alivePlayer },
            CurveEditor3D.GetActiveInterpGroups(interp, aliveScene).ToArray());
    }

    [TestMethod]
    public void SceneShopChoiceStopsTimelineAtTheFirstUnresolvedNode()
    {
        Assert.AreEqual(4.5f,
            CurveEditor3D.ResolveDialogueTimelineEndForSceneShop(24f, [11f, 4.5f, 18f]));
        Assert.AreEqual(24f,
            CurveEditor3D.ResolveDialogueTimelineEndForSceneShop(24f, []));
    }

    [TestMethod]
    public void SceneShopCacheBuildExpandsEveryAuthoredPathCombination()
    {
        IReadOnlyList<Dictionary<int, int>> variants =
            CurveEditor3D.ExpandDialogueSceneShopSelectionVariants([
                (120, (IReadOnlyList<int>)[201, 202]),
                (121, (IReadOnlyList<int>)[301, 302, 303]),
            ]);

        Assert.AreEqual(6, variants.Count);
        CollectionAssert.AreEquivalent(new[] { 201, 202 },
            variants.Select(variant => variant[120]).Distinct().ToArray());
        CollectionAssert.AreEquivalent(new[] { 301, 302, 303 },
            variants.Select(variant => variant[121]).Distinct().ToArray());
        Assert.AreEqual(6, variants.Select(variant => $"{variant[120]}:{variant[121]}")
            .Distinct().Count());
    }

    [TestMethod]
    public void DialogueCachePresetSerializesEverySceneShopRuntimeVariant()
    {
        var node = new DialogueCacheNodePreset
        {
            IsReply = false,
            NodeIndex = 2,
            SceneShopVariants =
            [
                new DialogueCacheNodePreset
                {
                    SceneShopSelections = [new PackageExportReference { UIndex = 201 }],
                },
                new DialogueCacheNodePreset
                {
                    SceneShopSelections = [new PackageExportReference { UIndex = 202 }],
                },
            ],
        };

        DialogueCacheNodePreset restored = JsonConvert.DeserializeObject<DialogueCacheNodePreset>(
            JsonConvert.SerializeObject(node));

        Assert.IsNotNull(restored);
        Assert.AreEqual(2, restored.SceneShopVariants.Count);
        CollectionAssert.AreEquivalent(new[] { 201, 202 }, restored.SceneShopVariants
            .Select(variant => variant.SceneShopSelections.Single().UIndex).ToArray());
    }

    [TestMethod]
    public void NoTrackNodeInheritsTheActorsLastLiveTransform()
    {
        var cachedStart = new CameraOrigin(new Vector3(-16053, 7767, -170309), new Vector3(0, 0, -140));
        var lastTrackKey = new CameraOrigin(new Vector3(-15842, 8299, -170309), new Vector3(0, 0, -309));
        var manualOverride = new CameraOrigin(new Vector3(10, 20, 30), new Vector3(0, 0, 45));

        Assert.AreEqual(lastTrackKey, CurveEditor3D.ResolveDialogueActorStartOrigin(cachedStart,
            actorOverride: null, liveInheritedOrigin: lastTrackKey, hasMovementTrack: false));
        Assert.AreEqual(cachedStart, CurveEditor3D.ResolveDialogueActorStartOrigin(cachedStart,
            actorOverride: null, liveInheritedOrigin: lastTrackKey, hasMovementTrack: true));
        Assert.AreEqual(manualOverride, CurveEditor3D.ResolveDialogueActorStartOrigin(cachedStart,
            actorOverride: manualOverride, liveInheritedOrigin: lastTrackKey, hasMovementTrack: false));
    }

    [TestMethod]
    public void TrackMoveEvaluationHonorsNegativeKeysTangentsAndConstantSegments()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("TrackMoveInterpolationTest.pcc", MEGame.LE3);
        var position = new InterpCurve<Vector3>();
        position.Points.Add(new InterpCurvePoint<Vector3>(-2, Vector3.Zero,
            Vector3.Zero, new Vector3(4, 0, 0), EInterpCurveMode.CIM_CurveUser));
        position.Points.Add(new InterpCurvePoint<Vector3>(0, new Vector3(10, 0, 0),
            new Vector3(2, 0, 0), Vector3.Zero, EInterpCurveMode.CIM_Constant));
        position.Points.Add(new InterpCurvePoint<Vector3>(2, new Vector3(30, 0, 0),
            Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_CurveAutoClamped));
        var rotation = new InterpCurve<Vector3>();
        rotation.Points.Add(new InterpCurvePoint<Vector3>(-2, Vector3.Zero));
        rotation.Points.Add(new InterpCurvePoint<Vector3>(0, Vector3.Zero));
        rotation.Points.Add(new InterpCurvePoint<Vector3>(2, Vector3.Zero));
        ExportEntry export = package.CreateExport("MoveTrack", "InterpTrackMove", indexed: false);
        export.WriteProperties(new PropertyCollection
        {
            position.ToStructProperty(package.Game, "PosTrack"),
            rotation.ToStructProperty(package.Game, "EulerTrack"),
            CreateLookupTrack(-2, 0, 2),
        });
        var model = new CurveEditor3DModel();
        model.Load(export);

        CameraOrigin tangentMidpoint = CurveEditor3D.EvaluateTrackMove(model, -1);
        CameraOrigin constantMidpoint = CurveEditor3D.EvaluateTrackMove(model, 1);
        CameraOrigin exactNextKey = CurveEditor3D.EvaluateTrackMove(model, 2);

        Assert.AreEqual(5.5f, tangentMidpoint.Location.X, 0.001f);
        Assert.AreEqual(10f, constantMidpoint.Location.X, 0.001f);
        Assert.AreEqual(30f, exactNextKey.Location.X, 0.001f);
    }

    [TestMethod]
    public void QuaternionTrackStillHonorsConstantRotationKeys()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("QuaternionConstantTrackMoveTest.pcc", MEGame.LE3);
        var position = new InterpCurve<Vector3>();
        position.Points.Add(new InterpCurvePoint<Vector3>(0, Vector3.Zero));
        position.Points.Add(new InterpCurvePoint<Vector3>(2, Vector3.Zero));
        var rotation = new InterpCurve<Vector3>();
        rotation.Points.Add(new InterpCurvePoint<Vector3>(0, new Vector3(0, 0, 10),
            Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Constant));
        rotation.Points.Add(new InterpCurvePoint<Vector3>(2, new Vector3(0, 0, 100),
            Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Linear));
        ExportEntry export = package.CreateExport("MoveTrack", "InterpTrackMove", indexed: false);
        export.WriteProperties(new PropertyCollection
        {
            position.ToStructProperty(package.Game, "PosTrack"),
            rotation.ToStructProperty(package.Game, "EulerTrack"),
            CreateLookupTrack(0, 2),
        });
        var model = new CurveEditor3DModel();
        model.Load(export);

        CameraOrigin held = CurveEditor3D.EvaluateTrackMove(model, 1, useQuaternionInterpolation: true);
        CameraOrigin nextKey = CurveEditor3D.EvaluateTrackMove(model, 2, useQuaternionInterpolation: true);

        Assert.AreEqual(10f, held.Rotation.Z, 0.001f);
        Assert.AreEqual(100f, nextKey.Rotation.Z, 0.001f);
    }

    [TestMethod]
    public void TrackMoveCurveTensionIsUsedAndPreservedByCacheSnapshots()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("TrackMoveTensionTest.pcc", MEGame.LE3);
        var position = new InterpCurve<Vector3>();
        var rotation = new InterpCurve<Vector3>();
        foreach ((float time, float value) in new[] { (0f, 0f), (1f, 10f), (2f, 20f) })
        {
            position.Points.Add(new InterpCurvePoint<Vector3>(time, new Vector3(value, 0, 0),
                Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_CurveAutoClamped));
            rotation.Points.Add(new InterpCurvePoint<Vector3>(time, new Vector3(0, 0, value * 2),
                Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_CurveAutoClamped));
        }
        ExportEntry export = package.CreateExport("MoveTrack", "InterpTrackMove", indexed: false);
        export.WriteProperties(new PropertyCollection
        {
            position.ToStructProperty(package.Game, "PosTrack"),
            rotation.ToStructProperty(package.Game, "EulerTrack"),
            CreateLookupTrack(0, 1, 2),
            new FloatProperty(0.5f, "LinCurveTension"),
            new FloatProperty(0.25f, "AngCurveTension"),
        });
        var source = new CurveEditor3DModel { AutoCommit = false };
        source.Load(export);

        source.SetAllPosTrackInterpModes(EInterpCurveMode.CIM_CurveAutoClamped);
        source.SetAllEulerTrackInterpModes(EInterpCurveMode.CIM_CurveAutoClamped);

        Assert.AreEqual(5f, source.PositionTrack.Points[1].LeaveTangent.X, 0.001f);
        Assert.AreEqual(15f, source.RotationTrack.Points[1].LeaveTangent.Z, 0.001f);
        CurveEditor3DModelSnapshot snapshot = JsonConvert.DeserializeObject<CurveEditor3DModelSnapshot>(
            JsonConvert.SerializeObject(source.CreateCacheSnapshot()));
        var restored = new CurveEditor3DModel { AutoCommit = false };
        restored.LoadCacheSnapshot(export, snapshot);
        Assert.AreEqual(0.5f, restored.PositionCurveTension, 0.001f);
        Assert.AreEqual(0.25f, restored.RotationCurveTension, 0.001f);
        Assert.AreEqual(5f, restored.PositionTrack.Points[1].LeaveTangent.X, 0.001f);
        Assert.AreEqual(15f, restored.RotationTrack.Points[1].LeaveTangent.Z, 0.001f);
    }

    [TestMethod]
    public void CacheSnapshotRestoresPendingCurvesAndStageLookupBones()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("TrackMovePresetTest.pcc", MEGame.LE3);
        var position = new InterpCurve<Vector3>();
        position.Points.Add(new InterpCurvePoint<Vector3>(0, new Vector3(1, 2, 3),
            new Vector3(4, 5, 6), new Vector3(7, 8, 9), EInterpCurveMode.CIM_CurveUser));
        var rotation = new InterpCurve<Vector3>();
        rotation.Points.Add(new InterpCurvePoint<Vector3>(0, new Vector3(10, 20, 30),
            Vector3.Zero, Vector3.Zero, EInterpCurveMode.CIM_Linear));
        var lookupPoints = new ArrayProperty<StructProperty>("Points")
        {
            new StructProperty("InterpLookupPoint", false,
                new NameProperty("DockP2_Player", "GroupName"), new FloatProperty(0, "Time")),
        };
        ExportEntry export = package.CreateExport("MoveTrack", "InterpTrackMove", indexed: false);
        export.WriteProperties(new PropertyCollection
        {
            position.ToStructProperty(package.Game, "PosTrack"),
            rotation.ToStructProperty(package.Game, "EulerTrack"),
            new StructProperty("InterpLookupTrack", new PropertyCollection { lookupPoints }, "LookupTrack"),
        });
        var source = new CurveEditor3DModel { AutoCommit = false };
        source.Load(export);
        source.Keyframes[0].X = 99;

        CurveEditor3DModelSnapshot snapshot = JsonConvert.DeserializeObject<CurveEditor3DModelSnapshot>(
            JsonConvert.SerializeObject(source.CreateCacheSnapshot()));
        var restored = new CurveEditor3DModel { AutoCommit = false };
        restored.LoadCacheSnapshot(export, snapshot);

        Assert.IsTrue(restored.HasPendingChanges);
        Assert.AreEqual(99, restored.Keyframes[0].X, 0.001f);
        Assert.AreEqual(EInterpCurveMode.CIM_CurveUser, restored.Keyframes[0].PosTrackInterpMode);
        Assert.AreEqual("DockP2_Player", restored.CreateCacheSnapshot().LookupPoints[0].GroupName);
        InterpCurve<Vector3> unchangedExport = InterpCurve<Vector3>.FromStructProperty(
            export.GetProperty<StructProperty>("PosTrack"), package.Game);
        Assert.AreEqual(1, unchangedExport.Points[0].OutVal.X, 0.001f);
    }

    private static StructProperty CreateLookupTrack(params float[] times)
    {
        var lookupPoints = new ArrayProperty<StructProperty>("Points");
        foreach (float time in times)
        {
            lookupPoints.Add(new StructProperty("InterpLookupPoint", false,
                new NameProperty("None", "GroupName"), new FloatProperty(time, "Time")));
        }
        return new StructProperty("InterpLookupTrack", new PropertyCollection { lookupPoints }, "LookupTrack");
    }

    private static ArrayProperty<StructProperty> CreateSceneShopOutputPins(
        params (string Name, ExportEntry Target)[] outputs)
    {
        var pins = new ArrayProperty<StructProperty>("m_aOutputPins");
        foreach ((string name, ExportEntry target) in outputs)
        {
            pins.Add(new StructProperty("SFXSceneShopPin", false,
                new StrProperty(name, "sLinkName"),
                new ArrayProperty<StructProperty>("aLinks")
                {
                    new StructProperty("SFXSceneShopLink", false,
                        new ObjectProperty(target.UIndex, "pLinkedNode"),
                        new IntProperty(0, "nLinkedIndex")),
                }));
        }
        return pins;
    }

    private static ExportEntry CreateFaceOnlyVoInterp(IMEPackage package, string name, params float[] keyTimes)
    {
        ExportEntry interp = package.CreateExport($"{name}Interp", "InterpData", indexed: false);
        ExportEntry group = package.CreateExport($"{name}Group", "InterpGroup", indexed: false);
        ExportEntry track = package.CreateExport($"{name}FaceOnlyVo", "SFXInterpTrackPlayFaceOnlyVO", indexed: false);
        var trackKeys = new ArrayProperty<StructProperty>("m_aTrackKeys");
        foreach (float keyTime in keyTimes)
        {
            trackKeys.Add(new StructProperty("SFXInterpTrackKey", false,
                new FloatProperty(keyTime, "fTime")));
        }
        track.WriteProperty(trackKeys);
        group.WriteProperty(new ArrayProperty<ObjectProperty>("InterpTracks")
        {
            new(track.UIndex),
        });
        interp.WriteProperty(new ArrayProperty<ObjectProperty>("InterpGroups")
        {
            new(group.UIndex),
        });
        return interp;
    }
}
