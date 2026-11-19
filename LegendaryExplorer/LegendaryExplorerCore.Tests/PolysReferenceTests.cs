using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class PolysReferenceTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME1)]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.UDK)]
    public void ArrayOwnerAcceptsSelfAndContainingModelWithoutChangingData(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("PolysOwner.pcc", game);
        var model = package.CreateImport("Model", "ContainingModel");
        var export = package.CreateExport("Polys", "Polys", indexed: false);
        foreach (int owner in new[] { export.UIndex, model.UIndex, 0 })
        {
            var polys = Polys.Create();
            polys.Owner = owner;
            export.WriteBinary(polys);
            byte[] originalData = export.Data;

            Assert.IsEmpty(BinaryIssues(export), $"Valid array owner {owner} must not be reported.");
            CollectionAssert.AreEqual(originalData, export.Data);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ArrayOwnerStillReportsMissingAndTrashedReferences(bool trashed)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BadPolysOwner.pcc", MEGame.LE3);
        var export = package.CreateExport("Polys", "Polys", indexed: false);
        var polys = Polys.Create();
        polys.Owner = trashed
            ? package.CreateExport(UnrealPackageFile.TrashPackageName, "Package", indexed: false).UIndex
            : 900000;
        export.WriteBinary(polys);

        var issue = BinaryIssues(export).Single();
        Assert.AreEqual(polys.Owner, issue.ReferencedUIndex);
        StringAssert.Contains(issue.Message, "Owner");
    }

    [TestMethod]
    public void PolygonActorAndMaterialStillRejectWrongTypes()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("PolysElementTypes.pcc", MEGame.LE3);
        var export = package.CreateExport("Polys", "Polys", indexed: false);
        var wrongType = package.CreateImport("Texture2D", "WrongType");
        var polys = Polys.Create();
        polys.Owner = export.UIndex;
        polys.Elements = [new Poly
        {
            Vertices = [],
            Actor = wrongType.UIndex,
            Material = wrongType.UIndex,
            ItemName = "None",
            RulesetVariation = "None",
            LightmassSettings = new LightmassPrimitiveSettings()
        }];
        export.WriteBinary(polys);

        var issues = BinaryIssues(export);
        Assert.HasCount(2, issues);
        Assert.IsTrue(issues.Any(issue => issue.Message.Contains("Brush")));
        Assert.IsTrue(issues.Any(issue => issue.Message.Contains("MaterialInterface")));
    }

    private static ReferenceIssue[] BinaryIssues(ExportEntry export)
    {
        var results = new ReferenceCheckPackage();
        EntryChecker.CheckReferences(results, export.FileRef, LECLocalizationShim.NonLocalizedStringConverter);
        return results.GetSignificantIssues().OfType<ReferenceIssue>()
            .Where(issue => issue.Entry == export && issue.Location == ReferenceIssueLocation.Binary).ToArray();
    }
}
