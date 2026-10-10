using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
[DoNotParallelize]
public class PackageSaveDuplicateWarningTests
{
    private Func<IMEPackage, string, ReferenceCheckPackage, bool> originalReferenceCallback;
    private Func<IMEPackage, string, IReadOnlyList<EntryStringPair>, bool> originalDuplicateCallback;
    private string temporaryDirectory;

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestInitialize]
    public void SetUp()
    {
        originalReferenceCallback = PackageSaver.PackageSaveReferenceWarningCallback;
        originalDuplicateCallback = PackageSaver.PackageSaveDuplicateWarningCallback;
        PackageSaver.PackageSaveReferenceWarningCallback = null;
        PackageSaver.PackageSaveDuplicateWarningCallback = null;
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"LEX_SaveDuplicateWarning_{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TestCleanup]
    public void TearDown()
    {
        PackageSaver.PackageSaveReferenceWarningCallback = originalReferenceCallback;
        PackageSaver.PackageSaveDuplicateWarningCallback = originalDuplicateCallback;
        foreach (string file in Directory.EnumerateFiles(temporaryDirectory))
            File.Delete(file);
        Directory.Delete(temporaryDirectory);
    }

    [TestMethod]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.UDK)]
    public void ConfirmingWarningSavesDuplicateIndexesWithoutRepairingThem(MEGame game)
    {
        string path = Destination(game == MEGame.UDK ? "Duplicates.upk" : "Duplicates.pcc");
        using var package = CreateDuplicates(path, game);
        var duplicate = package.Exports[1];
        byte[] originalData = duplicate.Data;
        int warningCount = 0;
        PackageSaver.PackageSaveDuplicateWarningCallback = (warnedPackage, destination, issues) =>
        {
            warningCount++;
            Assert.AreSame(package, warnedPackage);
            Assert.AreEqual(path, destination);
            Assert.AreEqual(1, issues.Count);
            Assert.AreSame(duplicate, issues[0].Entry);
            Assert.IsFalse(File.Exists(path), "The warning must precede any destination write.");
            return true;
        };

        Assert.IsTrue(package.TrySave(path, compress: false));

        Assert.AreEqual(1, warningCount);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(package.IsModified);
        Assert.AreEqual(0, duplicate.indexValue);
        CollectionAssert.AreEqual(originalData, duplicate.Data);
        using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        Assert.AreEqual(1, EntryChecker.CheckForDuplicateIndices(reopened).Count);
        CollectionAssert.AreEqual(originalData, reopened.GetUExport(duplicate.UIndex).Data);
    }

    [TestMethod]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.UDK)]
    public void CancellingLeavesTheExistingFileAndUnsavedEditsIntact(MEGame game)
    {
        string path = Destination(game == MEGame.UDK ? "Existing.upk" : "Existing.pcc");
        using (var original = MEPackageHandler.CreateMemoryEmptyPackage(path, game))
        {
            original.CreateExport("First", "Object", indexed: false);
            original.CreateExport("Second", "Object", indexed: false);
            Assert.IsTrue(original.TrySave(path, compress: false));
        }
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        byte[] diskBefore = File.ReadAllBytes(path);
        var duplicate = package.Exports[1];
        duplicate.ObjectName = package.Exports[0].ObjectName;
        duplicate.WriteProperty(new IntProperty(123, "UnsavedValue"));
        byte[] unsavedData = duplicate.Data;
        int warningCount = 0;
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, destination, _) =>
        {
            warningCount++;
            Assert.AreEqual(path, destination);
            CollectionAssert.AreEqual(diskBefore, File.ReadAllBytes(path));
            return false;
        };

        Assert.IsFalse(package.TrySave(compress: false));

        Assert.AreEqual(1, warningCount);
        CollectionAssert.AreEqual(diskBefore, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(unsavedData, duplicate.Data);
        Assert.AreEqual("First", duplicate.ObjectName.Name);
        Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellingSaveAsDoesNotCreateOrOverwriteTheDestination(bool existingDestination)
    {
        string sourcePath = Destination("Source.pcc");
        string destinationPath = Destination("Copy.pcc");
        byte[] diskBefore = [1, 3, 5, 7];
        if (existingDestination)
            File.WriteAllBytes(destinationPath, diskBefore);
        using var package = CreateDuplicates(sourcePath);
        byte[] unsavedData = package.Exports[1].Data;
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, destination, _) =>
        {
            Assert.AreEqual(destinationPath, destination);
            return false;
        };

        Assert.IsFalse(package.TrySave(destinationPath, compress: false));

        Assert.AreEqual(existingDestination, File.Exists(destinationPath));
        if (existingDestination)
            CollectionAssert.AreEqual(diskBefore, File.ReadAllBytes(destinationPath));
        Assert.IsFalse(File.Exists(sourcePath));
        Assert.AreEqual(sourcePath, package.FilePath);
        Assert.IsTrue(package.IsModified);
        CollectionAssert.AreEqual(unsavedData, package.Exports[1].Data);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ReferenceWarningPrecedesDuplicateWarningAndEitherCancellationStopsSaving(bool acceptReferences, bool acceptDuplicates)
    {
        string path = Destination("BothIssues.pcc");
        using var package = CreateDuplicates(path);
        package.Exports[0].WriteProperty(new ObjectProperty(900000, "BadReference"));
        var order = new List<string>();
        PackageSaver.PackageSaveReferenceWarningCallback = (_, _, issues) =>
        {
            Assert.IsTrue(issues.GetSignificantIssues().Count > 0);
            Assert.IsFalse(File.Exists(path));
            order.Add("References");
            return acceptReferences;
        };
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, _, issues) =>
        {
            Assert.AreEqual(1, issues.Count);
            Assert.IsFalse(File.Exists(path));
            order.Add("Duplicates");
            return acceptDuplicates;
        };

        Assert.AreEqual(acceptReferences && acceptDuplicates, package.TrySave(path, compress: false));

        CollectionAssert.AreEqual(acceptReferences ? new[] { "References", "Duplicates" } : new[] { "References" }, order);
        Assert.AreEqual(acceptReferences && acceptDuplicates, File.Exists(path));
        Assert.AreEqual(900000, package.Exports[0].GetProperty<ObjectProperty>("BadReference").Value);
        Assert.AreEqual(1, EntryChecker.CheckForDuplicateIndices(package).Count);
        if (!acceptReferences || !acceptDuplicates)
            Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    public void CleanReferencesStillProceedToDuplicateConfirmation()
    {
        string path = Destination("DuplicateOnly.pcc");
        using var package = CreateDuplicates(path);
        PackageSaver.PackageSaveReferenceWarningCallback = (_, _, _) =>
            throw new AssertFailedException("Clean references should not prompt.");
        int warningCount = 0;
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, _, _) =>
        {
            warningCount++;
            return false;
        };

        Assert.IsFalse(package.TrySave(path, compress: false));

        Assert.AreEqual(1, warningCount);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void WarningIncludesImportDuplicatesAndExportImportCollisions()
    {
        string path = Destination("ImportDuplicates.pcc");
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE3);
        package.CreateExport("Shared", "Object", indexed: false);
        var outer = package.CreateImport("Package", "External");
        var firstImport = AddImport(package, "Imported", outer);
        var duplicateImport = AddImport(package, "Imported", outer);
        var exportCollision = AddImport(package, "Shared", null);
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, _, issues) =>
        {
            CollectionAssert.AreEqual(new IEntry[] { duplicateImport, exportCollision }, issues.Select(issue => issue.Entry).ToArray());
            return true;
        };

        Assert.IsTrue(package.TrySave(path, compress: false));

        Assert.AreEqual(0, firstImport.indexValue);
        Assert.AreEqual(0, duplicateImport.indexValue);
        Assert.AreEqual(0, exportCollision.indexValue);
        using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        Assert.AreEqual(2, EntryChecker.CheckForDuplicateIndices(reopened).Count);
    }

    [TestMethod]
    public void CleanPackageDoesNotPromptAndUnconfiguredCallbackAllowsHeadlessDuplicates()
    {
        string cleanPath = Destination("Clean.pcc");
        using (var clean = MEPackageHandler.CreateMemoryEmptyPackage(cleanPath, MEGame.LE3))
        {
            clean.CreateExport("Only", "Object", indexed: false);
            PackageSaver.PackageSaveDuplicateWarningCallback = (_, _, _) =>
                throw new AssertFailedException("A package without duplicate indexes must not prompt.");
            Assert.IsTrue(clean.TrySave(cleanPath, compress: false));
            Assert.IsTrue(File.Exists(cleanPath));
        }
        PackageSaver.PackageSaveDuplicateWarningCallback = null;
        string duplicatePath = Destination("Headless.pcc");
        using var duplicates = CreateDuplicates(duplicatePath);

        Assert.IsTrue(duplicates.TrySave(duplicatePath, compress: false));

        Assert.IsTrue(File.Exists(duplicatePath));
        Assert.AreEqual(1, EntryChecker.CheckForDuplicateIndices(duplicates).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacySaveEntryPointsAlsoHonorDuplicateCancellation(bool asynchronous)
    {
        string path = Destination("Legacy.pcc");
        using var package = CreateDuplicates(path);
        int warningCount = 0;
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, _, _) =>
        {
            warningCount++;
            return false;
        };

        if (asynchronous)
            await package.SaveAsync(path, compress: false);
        else
            package.Save(path, compress: false);

        Assert.AreEqual(1, warningCount);
        Assert.IsFalse(File.Exists(path));
        Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AsyncSaveRestoresTheSavingStateAfterEitherDuplicateChoice(bool accept)
    {
        string path = Destination("Async.pcc");
        using var package = CreateDuplicates(path);
        var user = new RecordingPackageUser();
        package.RegisterTool(user);
        int warningCount = 0;
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, _, _) =>
        {
            warningCount++;
            Assert.IsFalse(File.Exists(path));
            return accept;
        };
        try
        {
            Assert.AreEqual(accept, await package.TrySaveAsync(path, compress: false));

            Assert.AreEqual(1, warningCount);
            Assert.AreEqual(accept, File.Exists(path));
            Assert.IsFalse(user.IsSaving);
            Assert.AreEqual(user.SaveStates.Count(state => state), user.SaveStates.Count(state => !state));
            Assert.IsTrue(user.SaveStates.Count > 0);
            if (!accept)
                Assert.IsTrue(package.IsModified);
            Assert.AreEqual(1, EntryChecker.CheckForDuplicateIndices(package).Count);
        }
        finally
        {
            package.Release(user);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IncompleteDuplicateCheckWarnsAndStillAllowsSaving(bool accept)
    {
        string path = Destination("CorruptName.pcc");
        using var package = CreateDuplicates(path);
        var export = package.Exports[0];
        int invalidNameIndex = package.Names.Count + 10;
        byte[] header = export.Header;
        BitConverter.GetBytes(invalidNameIndex).CopyTo(header, ExportEntry.OFFSET_idxObjectName);
        export.Header = header;
        int warningCount = 0;
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, _, issues) =>
        {
            warningCount++;
            Assert.IsTrue(issues.Any(issue => issue.Message.Contains("could not finish")));
            Assert.IsFalse(File.Exists(path));
            return accept;
        };

        Assert.AreEqual(accept, package.TrySave(path, compress: false));

        Assert.AreEqual(1, warningCount);
        Assert.AreEqual(accept, File.Exists(path));
        Assert.AreEqual(invalidNameIndex, BitConverter.ToInt32(export.Header, ExportEntry.OFFSET_idxObjectName));
        if (!accept)
            Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    public void CircularOuterProducesAnIncompleteCheckWarningInsteadOfHanging()
    {
        string path = Destination("CircularOuter.pcc");
        using var package = CreateDuplicates(path);
        var first = package.Exports[0];
        var second = package.Exports[1];
        first.Parent = second;
        second.Parent = first;
        int warningCount = 0;
        PackageSaver.PackageSaveDuplicateWarningCallback = (_, _, issues) =>
        {
            warningCount++;
            Assert.IsTrue(issues.Any(issue => issue.Message.Contains("cycle")));
            return false;
        };

        Assert.IsFalse(package.TrySave(path, compress: false));

        Assert.AreEqual(1, warningCount);
        Assert.IsFalse(File.Exists(path));
        Assert.IsTrue(package.IsModified);
    }

    private string Destination(string name) => Path.Combine(temporaryDirectory, name);

    private static IMEPackage CreateDuplicates(string path, MEGame game = MEGame.LE3)
    {
        var package = MEPackageHandler.CreateMemoryEmptyPackage(path, game);
        package.CreateExport("Duplicate", "Object", indexed: false);
        package.CreateExport("Duplicate", "Object", indexed: false).WriteProperty(new IntProperty(42, "Value"));
        return package;
    }

    private static ImportEntry AddImport(IMEPackage package, NameReference name, IEntry parent)
    {
        var import = new ImportEntry(package, parent, name) { ClassName = "Object", PackageFile = "Core" };
        package.AddImport(import);
        return import;
    }

    private sealed class RecordingPackageUser : IPackageUser
    {
        public List<bool> SaveStates { get; } = [];
        public bool IsSaving { get; private set; }

        public void HandleSaveStateChange(bool isSaving)
        {
            IsSaving = isSaving;
            SaveStates.Add(isSaving);
        }

        public void HandleUpdate(List<PackageUpdate> updates) { }
        public void RegisterClosed(Action handler) { }
        public void ReleaseUse() { }
    }
}
