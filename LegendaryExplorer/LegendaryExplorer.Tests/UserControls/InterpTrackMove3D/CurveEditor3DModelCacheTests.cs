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
    public void GestureRootTranslationRemainsEnabledForSplineDrivenActors()
    {
        Assert.IsFalse(CurveEditor3D.ShouldLockDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 2));
    }

    [TestMethod]
    public void GestureRootTranslationRemainsEnabledForSingleKeyActorAnchor()
    {
        Assert.IsFalse(CurveEditor3D.ShouldLockDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 1));
    }

    [TestMethod]
    public void GestureRootTranslationIsLockedWhenTheNodeHasNoActorTrackMove()
    {
        Assert.IsTrue(CurveEditor3D.ShouldLockDialogueGestureRootTranslation(
            isConversationPreview: true, movementKeyCount: 0));
        Assert.IsFalse(CurveEditor3D.ShouldLockDialogueGestureRootTranslation(
            isConversationPreview: false, movementKeyCount: 0));
    }

    [TestMethod]
    public void LookAtDoesNotRotateThePawnTransform()
    {
        Assert.IsFalse(CurveEditor3D.DirectionTrackControlsActorTransform(isLookAt: true));
        Assert.IsTrue(CurveEditor3D.DirectionTrackControlsActorTransform(isLookAt: false));
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
        Assert.IsFalse(CurveEditor3D.IsEligibleActorTrackGroup(cameraGroup, "player"));
        Assert.IsFalse(CurveEditor3D.IsEligibleActorTrackGroup(ownerGroup, "player"));
        Assert.IsTrue(CurveEditor3D.IsEligibleActorTrackGroup(ownerGroup, "owner"));
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
}
