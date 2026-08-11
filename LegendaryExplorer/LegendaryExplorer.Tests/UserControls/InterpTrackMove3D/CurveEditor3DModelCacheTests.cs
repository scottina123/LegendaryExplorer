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
}
