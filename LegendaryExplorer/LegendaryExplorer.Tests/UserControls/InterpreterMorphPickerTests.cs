using System.Threading.Tasks;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class InterpreterMorphPickerTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [DataTestMethod]
    [DataRow("SFXStuntActor")]
    [DataRow("SFXSkeletalMeshActor")]
    [DataRow("SFXSeqAct_SetMorphHead")]
    public void MorphHeadReferencesExposeVisualMorphPicker(string ownerClass)
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("MorphPickerTest.pcc", MEGame.LE3);
        ExportEntry owner = package.CreateExport("MorphOwner", ownerClass, indexed: false);
        var parent = new UPropertyTreeViewEntry { AttachedExport = owner };

        UPropertyTreeViewEntry morphNode = InterpreterExportLoader.GenerateUPropertyTreeViewEntry(
            new ObjectProperty(0, "MorphHead"),
            parent,
            owner);

        Assert.AreEqual("BioMorphFace", morphNode.AssetReferenceClass);
        Assert.IsTrue(morphNode.ShowAssetPicker);
        Assert.AreEqual("Choose morph...", morphNode.AssetPickerButtonText);
    }

    [STATestMethod]
    public void MorphDialogListsEveryLocalBioMorphFace()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("LocalMorphPickerTest.pcc", MEGame.LE3);
        package.CreateExport("MorphA", "BioMorphFace", indexed: false);
        package.CreateExport("MorphB", "BioMorphFace", indexed: false);
        package.CreateExport("NotAMorph", "SkeletalMesh", indexed: false);

        var dialog = new MorphPickerDialog(MEGame.LE3, package);
        try
        {
            Assert.AreEqual(2, dialog.AllLocalMorphs.Count);
            Assert.AreEqual(2, dialog.FilteredLocalMorphs.Count);
        }
        finally
        {
            dialog.Close();
        }
    }
}
