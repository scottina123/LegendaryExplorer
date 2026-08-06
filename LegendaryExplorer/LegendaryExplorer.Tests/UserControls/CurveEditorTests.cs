using System.Threading.Tasks;
using System.Windows;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class CurveEditorTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [STATestMethod]
    public void UnloadExportDoesNotRewriteUnchangedCurves()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("CurveEditorTest.pcc", MEGame.LE3);
        var curve = new InterpCurve<float>();
        curve.Points.Add(new InterpCurvePoint<float>(0, 0, 12, 12, EInterpCurveMode.CIM_CurveAuto));
        curve.Points.Add(new InterpCurvePoint<float>(1, 1, 34, 34, EInterpCurveMode.CIM_CurveAuto));
        curve.Points.Add(new InterpCurvePoint<float>(2, 4, 56, 56, EInterpCurveMode.CIM_CurveAuto));

        ExportEntry export = package.CreateExport("TestCurve", "InterpTrackFloatBase", indexed: false);
        export.WriteProperties(new PropertyCollection
        {
            curve.ToStructProperty(package.Game, "FloatTrack")
        });
        byte[] dataBeforeUnload = export.Data;

        var editor = new CurveEditor();
        try
        {
            editor.LoadExport(export);
            editor.RaiseEvent(new RoutedEventArgs(UIElement.GotFocusEvent));
            editor.UnloadExport();

            CollectionAssert.AreEqual(dataBeforeUnload, export.Data);
        }
        finally
        {
            editor.Dispose();
        }
    }
}
