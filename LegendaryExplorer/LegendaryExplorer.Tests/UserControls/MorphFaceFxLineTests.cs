using System.Collections.Generic;
using System.Threading.Tasks;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class MorphFaceFxLineTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow("12345", "line", 12345)]
    [DataRow("VO_0012345_F", "line", 12345)]
    [DataRow("Package.VO_12345_m", "line", 12345)]
    [DataRow("", "conversation_12345_M", 12345)]
    [DataRow(null, "12345_f", 12345)]
    [DataRow("invalid", "line", 0)]
    [DataRow("999999999999999", "12345_M", 12345)]
    public void TlkIdsSupportFaceFxNamingConventions(string id, string name, int expected)
    {
        Assert.AreEqual(expected, MorphFaceFxLine.GetTlkId(new FaceFXLine { ID = id, NameAsString = name }));
    }

    [TestMethod]
    public void ReadsEveryAssetAndAnimSetAndRetainsDuplicateTlkIds()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MorphLines.pcc", MEGame.LE3);
        var setExport = package.CreateExport("Set", "FaceFXAnimSet", indexed: false);
        var set = FaceFXAnimSet.Create(MEGame.LE3);
        set.Names = ["12345_M", "67890_F"];
        set.Lines = [CreateLine("12345", 0), CreateLine("67890", 1)];
        setExport.WriteBinary(set);
        var assetExport = package.CreateExport("Asset", "FaceFXAsset", indexed: false);
        var asset = FaceFXAsset.Create(MEGame.LE3);
        asset.Names = ["12345_F"];
        asset.Lines = [CreateLine("12345", 0)];
        assetExport.WriteBinary(asset);
        package.CreateExport("Unrelated", "Object", indexed: false);
        byte[] originalSetData = setExport.Data;
        byte[] originalAssetData = assetExport.Data;
        var errors = new List<string>();
        var lines = MorphFaceFxLine.ReadPackage(package, [], errors);
        Assert.HasCount(0, errors, string.Join("\n", errors));
        Assert.HasCount(3, lines);
        Assert.AreSame(setExport, lines[0].SourceExport);
        Assert.AreSame(assetExport, lines[2].SourceExport);
        Assert.AreEqual(12345, lines[0].TLKID);
        Assert.AreEqual(12345, lines[2].TLKID);
        Assert.IsTrue(lines[0].IsMale);
        Assert.IsFalse(lines[2].IsMale);
        lines[0].TLKString = "We should leave the Citadel.";
        Assert.IsTrue(lines[0].Matches(" 12345 "));
        Assert.IsTrue(lines[0].Matches("CITADEL"));
        Assert.IsFalse(lines[0].Matches("67890"));
        Assert.IsTrue(lines[0].Matches(" "));
        CollectionAssert.AreEqual(originalSetData, setExport.Data);
        CollectionAssert.AreEqual(originalAssetData, assetExport.Data);
    }

    [TestMethod]
    public void BrokenAssetDoesNotPreventLoadingOtherExports()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("MorphLines.pcc", MEGame.LE3);
        package.CreateExport("Broken", "FaceFXAsset", indexed: false);
        var export = package.CreateExport("Valid", "FaceFXAnimSet", indexed: false);
        var set = FaceFXAnimSet.Create(MEGame.LE3);
        set.Names = ["12345_M"];
        set.Lines = [CreateLine("12345", 0)];
        export.WriteBinary(set);
        var errors = new List<string>();
        var lines = MorphFaceFxLine.ReadPackage(package, [], errors);
        Assert.HasCount(1, errors);
        Assert.HasCount(1, lines);
    }

    private static FaceFXLine CreateLine(string id, int nameIndex) => new()
    {
        ID = id, Path = "", NameIndex = nameIndex, AnimationNames = [], NumKeys = [], Points = []
    };
}
