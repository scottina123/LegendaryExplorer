using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class DuplicateIndexRepairTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.UDK)]
    public void PreservesKeeperAndExistingNumbersAndPersistsWithoutChangingReferencesOrData(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("Duplicates.pcc", game);
        var first = package.CreateExport("Object", "Object", indexed: false);
        var reserved = package.CreateExport(new NameReference("Object", 1), "Object", indexed: false);
        var duplicate = package.CreateExport("Object", "Object", indexed: false);
        var secondDuplicate = package.CreateExport("Object", "Object", indexed: false);
        var source = package.CreateExport("Source", "Object", indexed: false);
        source.WriteProperties(new PropertyCollection
        {
            new ObjectProperty(duplicate, "Target"),
            new ArrayProperty<ObjectProperty>(new[] { new ObjectProperty(first), new ObjectProperty(secondDuplicate) }, "Targets")
        });
        var allEntries = package.Exports.Cast<IEntry>().Concat(package.Imports).ToArray();
        var originalReferences = allEntries.Select(entry => (entry.UIndex, entry.idxLink)).ToArray();
        var originalData = package.Exports.Select(entry => entry.Data).ToArray();
        var originalNames = package.Names.ToArray();

        Assert.AreEqual(2, DuplicateIndexRepairer.FixDuplicateIndices(package));

        Assert.AreEqual(0, first.indexValue);
        Assert.AreEqual(1, reserved.indexValue);
        Assert.AreEqual(2, duplicate.indexValue);
        Assert.AreEqual(3, secondDuplicate.indexValue);
        CollectionAssert.AreEqual(originalReferences, allEntries.Select(entry => (entry.UIndex, entry.idxLink)).ToArray());
        CollectionAssert.AreEqual(originalNames, package.Names.ToArray());
        for (int i = 0; i < package.ExportCount; i++)
            CollectionAssert.AreEqual(originalData[i], package.Exports[i].Data);
        AssertNoDuplicates(package);
        Assert.AreEqual(0, DuplicateIndexRepairer.FixDuplicateIndices(package));

        using var stream = package.SaveToStream(compress: false);
        stream.Position = 0;
        using var reopened = MEPackageHandler.OpenMEPackageFromStream(stream);
        AssertNoDuplicates(reopened);
        Assert.AreEqual(duplicate.UIndex, reopened.GetUExport(source.UIndex).GetProperty<ObjectProperty>("Target").Value);
        Assert.AreEqual(2, reopened.GetUExport(duplicate.UIndex).indexValue);
        Assert.AreEqual(3, reopened.GetUExport(secondDuplicate.UIndex).indexValue);
    }

    [TestMethod]
    public void RepairsImportsAndExportImportCollisionsInCheckerTableOrder()
    {
        using var package = CreatePackage();
        var earlierImport = AddImport(package, "Object", "Shared");
        var export = package.CreateExport("Shared", "Object", indexed: false);
        var secondImport = AddImport(package, "Object", "Shared");
        var importKeeper = AddImport(package, "Object", "Imported");
        var importDuplicate = AddImport(package, "Object", "Imported");

        Assert.AreEqual(3, DuplicateIndexRepairer.FixDuplicateIndices(package));

        Assert.AreEqual(0, export.indexValue, "Exports precede imports in the checker even when an import was created first.");
        Assert.AreEqual(1, earlierImport.indexValue);
        Assert.AreEqual(2, secondImport.indexValue);
        Assert.AreEqual(0, importKeeper.indexValue);
        Assert.AreEqual(1, importDuplicate.indexValue);
        AssertNoDuplicates(package);
    }

    [TestMethod]
    public void UsesRenderedPathToAvoidLiteralSuffixCollisions()
    {
        using var package = CreatePackage();
        var first = package.CreateExport("Object", "Object", indexed: false);
        var literal = package.CreateExport("Object_0", "Object", indexed: false);
        var duplicate = package.CreateExport("Object", "Object", indexed: false);
        var numberedAlias = package.CreateExport(new NameReference("Object", 1), "Object", indexed: false);

        Assert.AreEqual(2, DuplicateIndexRepairer.FixDuplicateIndices(package));

        Assert.AreEqual(0, first.indexValue);
        Assert.AreEqual(0, literal.indexValue);
        Assert.AreEqual(2, duplicate.indexValue);
        Assert.AreEqual(3, numberedAlias.indexValue);
        AssertNoDuplicates(package);
    }

    [TestMethod]
    public void RepairsParentsFirstWithoutRenumberingChildrenMadeUniqueByTheParent()
    {
        using var package = CreatePackage();
        // Children can occur earlier than their outer in the export table.
        var firstChild = package.CreateExport("Child", "Object", indexed: false);
        var secondChild = package.CreateExport("Child", "Object", indexed: false);
        var firstParent = package.CreateExport("Parent", "Package", indexed: false);
        var secondParent = package.CreateExport("Parent", "Package", indexed: false);
        firstChild.Parent = firstParent;
        secondChild.Parent = secondParent;
        Assert.AreEqual(2, EntryChecker.CheckForDuplicateIndices(package).Count);

        Assert.AreEqual(1, DuplicateIndexRepairer.FixDuplicateIndices(package));

        Assert.AreEqual(0, firstParent.indexValue);
        Assert.AreEqual(1, secondParent.indexValue);
        Assert.AreEqual(0, firstChild.indexValue);
        Assert.AreEqual(0, secondChild.indexValue);
        AssertNoDuplicates(package);
    }

    [TestMethod]
    public void RechecksDescendantCollisionsIntroducedByAParentRename()
    {
        using var package = CreatePackage();
        var firstParent = package.CreateExport("Parent", "Package", indexed: false);
        var duplicateParent = package.CreateExport("Parent", "Package", indexed: false);
        var differentClassParent = package.CreateExport(new NameReference("Parent", 1), "Object", indexed: false);
        var movedChild = package.CreateExport("Child", "Object", duplicateParent, indexed: false);
        var otherChild = package.CreateExport("Child", "Object", differentClassParent, indexed: false);
        Assert.AreEqual(1, EntryChecker.CheckForDuplicateIndices(package).Count);

        Assert.AreEqual(2, DuplicateIndexRepairer.FixDuplicateIndices(package));

        Assert.AreEqual(0, firstParent.indexValue);
        Assert.AreEqual(1, duplicateParent.indexValue, "Different classes may retain the same path under the checker rules.");
        Assert.AreEqual(0, movedChild.indexValue);
        Assert.AreEqual(1, otherChild.indexValue);
        AssertNoDuplicates(package);
    }

    [TestMethod]
    public void PreservesClassAndCaseDistinctionsAndIgnoredTrashPackages()
    {
        using var package = CreatePackage();
        var material = package.CreateExport("Asset", "Material", indexed: false);
        var texture = package.CreateExport("Asset", "Texture2D", indexed: false);
        var materialDuplicate = package.CreateExport("Asset", "Material", indexed: false);
        var lowerCase = package.CreateExport("LowerCasePlaceholder", "Material", indexed: false);
        var trash = package.CreateExport(UnrealPackageFile.TrashPackageName.ToLowerInvariant(), "Package", indexed: false);
        var trashDuplicate = package.CreateExport(UnrealPackageFile.TrashPackageName.ToLowerInvariant(), "Package", indexed: false);
        var trashChild = package.CreateExport("Asset", "Object", trash, indexed: false);
        var trashChildDuplicate = package.CreateExport("Asset", "Object", trash, indexed: false);
        // Disk packages may contain name entries that differ only by case, while normal name insertion
        // coalesces them. Rename a distinct table slot to construct that exact checker distinction.
        package.replaceName(package.findName("LowerCasePlaceholder"), "asset");
        Assert.AreEqual(2, EntryChecker.CheckForDuplicateIndices(package).Count);

        Assert.AreEqual(2, DuplicateIndexRepairer.FixDuplicateIndices(package));

        foreach (var untouched in new[] { material, texture, lowerCase, trash, trashDuplicate, trashChild })
            Assert.AreEqual(0, untouched.indexValue);
        Assert.AreEqual(1, materialDuplicate.indexValue);
        Assert.AreEqual(1, trashChildDuplicate.indexValue, "Only Package entries under the trash prefix are ignored by the checker.");
        AssertNoDuplicates(package);
    }

    [TestMethod]
    public void FindsAFreeNumberEvenWhenTheDuplicateNumberIsTheMaximumInteger()
    {
        using var package = CreatePackage();
        var first = package.CreateExport(new NameReference("Object", int.MaxValue), "Object", indexed: false);
        var duplicate = package.CreateExport(new NameReference("Object", int.MaxValue), "Object", indexed: false);

        Assert.AreEqual(1, DuplicateIndexRepairer.FixDuplicateIndices(package));

        Assert.AreEqual(int.MaxValue, first.indexValue);
        Assert.AreEqual(1, duplicate.indexValue);
        AssertNoDuplicates(package);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RejectsInvalidOuterChainsBeforeMakingAnyChanges(bool cycle)
    {
        using var package = CreatePackage();
        var first = package.CreateExport("Object", "Object", indexed: false);
        var duplicate = package.CreateExport("Object", "Object", indexed: false);
        var invalid = package.CreateExport("Invalid", "Object", indexed: false);
        if (cycle)
        {
            var other = package.CreateExport("Other", "Object", indexed: false);
            invalid.Parent = other;
            other.Parent = invalid;
        }
        else
        {
            invalid.idxLink = int.MaxValue;
        }
        byte[] originalHeader = duplicate.Header;

        Assert.ThrowsExactly<InvalidDataException>(() => DuplicateIndexRepairer.FixDuplicateIndices(package));

        Assert.AreEqual(0, first.indexValue);
        CollectionAssert.AreEqual(originalHeader, duplicate.Header);
    }

    private static IMEPackage CreatePackage() => MEPackageHandler.CreateMemoryEmptyPackage("Duplicates.pcc", MEGame.LE3);

    private static ImportEntry AddImport(IMEPackage package, string className, NameReference name)
    {
        var import = new ImportEntry(package, null, name) { ClassName = className, PackageFile = "Core" };
        package.AddImport(import);
        return import;
    }

    private static void AssertNoDuplicates(IMEPackage package)
        => Assert.AreEqual(0, EntryChecker.CheckForDuplicateIndices(package).Count);
}
