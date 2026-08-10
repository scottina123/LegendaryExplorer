using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls.InterpTrackMove3D;

[TestClass]
public class CurveEditor3DFovModelTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void FovKeyEditsWriteBackToFloatTrack()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("CurveEditor3DFovTest.pcc", MEGame.LE3);
        var curve = new InterpCurve<float>();
        curve.Points.Add(new InterpCurvePoint<float>(0, 60, 0, 0, EInterpCurveMode.CIM_CurveAuto));
        curve.Points.Add(new InterpCurvePoint<float>(2, 75, 0, 0, EInterpCurveMode.CIM_CurveAuto));
        ExportEntry export = package.CreateExport("FOVAngle", "InterpTrackFloatProp", indexed: false);
        export.WriteProperty(curve.ToStructProperty(package.Game, "FloatTrack"));

        var model = new CurveEditor3DFovModel();
        model.Load(export);
        CurveEditor3DFovKeyframe edited = model.Keyframes[1];
        edited.Value = 90;
        edited.Time = 1.5f;
        edited.InterpMode = EInterpCurveMode.CIM_CurveUser;
        edited.ArriveTangent = 12.5f;
        edited.LeaveTangent = -3.25f;

        InterpCurve<float> saved = InterpCurve<float>.FromStructProperty(
            export.GetProperty<StructProperty>("FloatTrack"), package.Game);
        InterpCurvePoint<float> savedPoint = saved.Points.Single(point => point.InVal == 1.5f);
        Assert.AreEqual(90, savedPoint.OutVal, 0.001f);
        Assert.AreEqual(12.5f, savedPoint.ArriveTangent, 0.001f);
        Assert.AreEqual(-3.25f, savedPoint.LeaveTangent, 0.001f);
        Assert.AreEqual(EInterpCurveMode.CIM_CurveUser, savedPoint.InterpMode);
    }

    [TestMethod]
    public void FovKeysCanBeAddedAndDeletedIndependently()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("CurveEditor3DFovAddTest.pcc", MEGame.LE3);
        ExportEntry export = package.CreateExport("FOVAngle", "InterpTrackFloatProp", indexed: false);
        export.WriteProperty(new InterpCurve<float>().ToStructProperty(package.Game, "FloatTrack"));
        var model = new CurveEditor3DFovModel();
        model.Load(export);

        CurveEditor3DFovKeyframe keyframe = model.AddKeyframe(1.25f, 65f);

        Assert.IsNotNull(keyframe);
        Assert.AreEqual(1, model.Keyframes.Count);
        Assert.IsTrue(model.HasKeyframeAtTime(1.25f));
        Assert.IsNull(model.AddKeyframe(1.25f, 80f));

        model.DeleteKeyframe(keyframe);

        Assert.AreEqual(0, model.Keyframes.Count);
        InterpCurve<float> saved = InterpCurve<float>.FromStructProperty(
            export.GetProperty<StructProperty>("FloatTrack"), package.Game);
        Assert.AreEqual(0, saved.Points.Count);
    }
}
