using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
[DoNotParallelize]
public class PackageSaveReferenceWarningTests
{
    private Func<IMEPackage, string, ReferenceCheckPackage, bool> originalWarningCallback;
    private string temporaryDirectory;

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestInitialize]
    public void SetUp()
    {
        originalWarningCallback = PackageSaver.PackageSaveReferenceWarningCallback;
        PackageSaver.PackageSaveReferenceWarningCallback = null;
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"LEX_SaveReferenceWarning_{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TestCleanup]
    public void TearDown()
    {
        PackageSaver.PackageSaveReferenceWarningCallback = originalWarningCallback;
        foreach (string file in Directory.EnumerateFiles(temporaryDirectory))
        {
            File.Delete(file);
        }
        Directory.Delete(temporaryDirectory);
    }

    [TestMethod]
    [DataRow(MEGame.LE3)]
    [DataRow(MEGame.UDK)]
    public void ConfirmingWarningSavesTheInvalidReferenceWithoutRepairingIt(MEGame game)
    {
        string path = Destination(game == MEGame.UDK ? "Invalid.upk" : "Invalid.pcc");
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(path, game);
        var export = package.CreateExport("Source", "Object", indexed: false);
        export.WriteProperty(new ObjectProperty(900000, "ReferenceTarget"));
        byte[] originalData = export.Data;
        int warningCount = 0;
        PackageSaver.PackageSaveReferenceWarningCallback = (warnedPackage, destination, issues) =>
        {
            warningCount++;
            Assert.AreSame(package, warnedPackage);
            Assert.AreEqual(path, destination);
            Assert.IsTrue(issues.GetSignificantIssues().Any(issue => issue.Entry == export));
            Assert.IsFalse(File.Exists(path), "The warning must happen before writing the destination.");
            return true;
        };

        Assert.IsTrue(package.TrySave(path, compress: false));

        Assert.AreEqual(1, warningCount);
        Assert.IsTrue(File.Exists(path));
        CollectionAssert.AreEqual(originalData, export.Data);
        using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        Assert.AreEqual(900000, reopened.FindExport("Source").GetProperty<ObjectProperty>("ReferenceTarget").Value);
    }

    [TestMethod]
    public void DecliningWarningLeavesExistingFileAndUnsavedChangesIntact()
    {
        string path = Destination("Existing.pcc");
        using (var originalPackage = CreateObjectVariable(path, 0))
        {
            Assert.IsTrue(originalPackage.TrySave(path, compress: false));
        }
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        byte[] originalFile = File.ReadAllBytes(path);
        var export = package.FindExport("Source");
        export.WriteProperty(new ObjectProperty(-900000, "ObjValue"));
        byte[] unsavedData = export.Data;
        Assert.IsTrue(package.IsModified);
        int warningCount = 0;
        PackageSaver.PackageSaveReferenceWarningCallback = (_, destination, _) =>
        {
            warningCount++;
            Assert.AreEqual(path, destination);
            CollectionAssert.AreEqual(originalFile, File.ReadAllBytes(path));
            return false;
        };

        Assert.IsFalse(package.TrySave(compress: false));

        Assert.AreEqual(1, warningCount);
        CollectionAssert.AreEqual(originalFile, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(unsavedData, export.Data);
        Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    public void DecliningSaveAsDoesNotCreateTheDestination()
    {
        string originalPath = Destination("Source.pcc");
        string saveAsPath = Destination("CancelledCopy.pcc");
        using var package = CreateObjectVariable(originalPath, 900000);
        byte[] originalData = package.FindExport("Source").Data;
        PackageSaver.PackageSaveReferenceWarningCallback = (_, destination, _) =>
        {
            Assert.AreEqual(saveAsPath, destination);
            return false;
        };

        Assert.IsFalse(package.TrySave(saveAsPath, compress: false));

        Assert.IsFalse(File.Exists(saveAsPath));
        Assert.IsFalse(File.Exists(originalPath));
        Assert.AreEqual(originalPath, package.FilePath);
        Assert.IsTrue(package.IsModified);
        CollectionAssert.AreEqual(originalData, package.FindExport("Source").Data);
    }

    [TestMethod]
    public void CleanPackageSavesWithoutInvokingWarningCallback()
    {
        string path = Destination("Clean.pcc");
        using var package = CreateObjectVariable(path, 0);
        PackageSaver.PackageSaveReferenceWarningCallback = (_, _, _) =>
            throw new AssertFailedException("A clean package must not prompt the user.");

        Assert.IsTrue(package.TrySave(path, compress: false));

        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(package.IsModified);
    }

    [TestMethod]
    public void WarningIncludesWrongTypePropertyAndBinaryReferences()
    {
        string path = Destination("PropertyAndBinary.pcc");
        using var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE3);
        var wrongType = package.CreateImport("StaticMeshComponent", "MeshComponent");
        var cube = package.CreateExport("Cube", "TextureCube", indexed: false);
        cube.WriteProperty(new ObjectProperty(wrongType, "FacePosX"));
        var redirector = package.CreateExport("Redirector", "ObjectRedirector", indexed: false);
        redirector.WriteBinary(new ObjectRedirector { DestinationObject = -900000 });
        byte[] originalCube = cube.Data;
        byte[] originalRedirector = redirector.Data;
        int warningCount = 0;
        PackageSaver.PackageSaveReferenceWarningCallback = (_, _, issues) =>
        {
            warningCount++;
            var references = issues.GetSignificantIssues().OfType<ReferenceIssue>().ToArray();
            Assert.IsTrue(references.Any(issue => issue.Entry == cube && issue.Location == ReferenceIssueLocation.Property));
            Assert.IsTrue(references.Any(issue => issue.Entry == redirector && issue.Location == ReferenceIssueLocation.Binary));
            return true;
        };

        Assert.IsTrue(package.TrySave(path, compress: false));

        Assert.AreEqual(1, warningCount, "A single save should show one warning for all bad references.");
        CollectionAssert.AreEqual(originalCube, cube.Data);
        CollectionAssert.AreEqual(originalRedirector, redirector.Data);
    }

    [TestMethod]
    public void LegacySaveAlsoHonorsWarningCancellation()
    {
        string path = Destination("Legacy.pcc");
        using var package = CreateObjectVariable(path, 900000);
        int warningCount = 0;
        PackageSaver.PackageSaveReferenceWarningCallback = (_, _, _) =>
        {
            warningCount++;
            return false;
        };

        package.Save(path, compress: false);

        Assert.AreEqual(1, warningCount);
        Assert.IsFalse(File.Exists(path));
        Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    public void BlockingHeaderReferenceAlsoInvokesTheWarning()
    {
        string path = Destination("CircularHeader.pcc");
        using var package = CreateObjectVariable(path, 0);
        var export = package.FindExport("Source");
        byte[] header = export.Header;
        BitConverter.GetBytes(export.UIndex).CopyTo(header, ExportEntry.OFFSET_idxLink);
        export.Header = header;
        int warningCount = 0;
        PackageSaver.PackageSaveReferenceWarningCallback = (_, _, issues) =>
        {
            warningCount++;
            Assert.IsTrue(issues.GetBlockingErrors().Any(issue => issue.Entry == export));
            return false;
        };

        Assert.IsFalse(package.TrySave(path, compress: false));

        Assert.AreEqual(1, warningCount);
        Assert.IsFalse(File.Exists(path));
        Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    public async Task LegacyAsyncSaveAlsoHonorsWarningCancellation()
    {
        string path = Destination("LegacyAsync.pcc");
        using var package = CreateObjectVariable(path, 900000);
        int warningCount = 0;
        PackageSaver.PackageSaveReferenceWarningCallback = (_, _, _) =>
        {
            warningCount++;
            return false;
        };

        await package.SaveAsync(path, compress: false);

        Assert.AreEqual(1, warningCount);
        Assert.IsFalse(File.Exists(path));
        Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void IncompleteReferenceCheckWarnsButStillAllowsTheSave(bool confirm)
    {
        string path = Destination("CorruptName.pcc");
        using var package = CreateObjectVariable(path, 0);
        var export = package.FindExport("Source");
        byte[] originalData = export.Data;
        int invalidNameIndex = package.Names.Count + 10;
        byte[] header = export.Header;
        BitConverter.GetBytes(invalidNameIndex).CopyTo(header, ExportEntry.OFFSET_idxObjectName);
        export.Header = header;
        int warningCount = 0;
        PackageSaver.PackageSaveReferenceWarningCallback = (_, destination, issues) =>
        {
            warningCount++;
            Assert.AreEqual(path, destination);
            Assert.IsTrue(issues.GetSignificantIssues().Any(issue => issue.Message.Contains("could not finish")),
                "An incomplete reference check should warn instead of preventing the user from saving.");
            Assert.IsFalse(File.Exists(path));
            return confirm;
        };

        Assert.AreEqual(confirm, package.TrySave(path, compress: false));

        Assert.AreEqual(1, warningCount);
        Assert.AreEqual(confirm, File.Exists(path));
        CollectionAssert.AreEqual(originalData, export.Data);
        Assert.AreEqual(invalidNameIndex, BitConverter.ToInt32(export.Header, ExportEntry.OFFSET_idxObjectName));
        if (!confirm)
            Assert.IsTrue(package.IsModified);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AsyncSaveWarnsOnceAndRestoresUsersSavingState(bool confirm)
    {
        string path = Destination("Async.pcc");
        using var package = CreateObjectVariable(path, 900000);
        var user = new RecordingPackageUser();
        package.RegisterTool(user);
        int warningCount = 0;
        PackageSaver.PackageSaveReferenceWarningCallback = (_, destination, _) =>
        {
            warningCount++;
            Assert.AreEqual(path, destination);
            Assert.IsFalse(File.Exists(path));
            return confirm;
        };
        try
        {
            Assert.AreEqual(confirm, await package.TrySaveAsync(path, compress: false));

            Assert.AreEqual(1, warningCount);
            Assert.AreEqual(confirm, File.Exists(path));
            Assert.IsFalse(user.IsSaving, "Accepting or cancelling a warning must release the saving state.");
            Assert.AreEqual(user.SaveStates.Count(state => state), user.SaveStates.Count(state => !state));
            if (confirm)
            {
                CollectionAssert.AreEqual(new[] { true, false }, user.SaveStates);
            }
            else
            {
                Assert.IsTrue(package.IsModified);
            }
            Assert.AreEqual(900000, package.FindExport("Source").GetProperty<ObjectProperty>("ObjValue").Value);
        }
        finally
        {
            package.Release(user);
        }
    }

    [TestMethod]
    public void UnconfiguredWarningCallbackAllowsHeadlessSaves()
    {
        string path = Destination("Headless.pcc");
        using var package = CreateObjectVariable(path, 900000);
        Assert.IsNull(PackageSaver.PackageSaveReferenceWarningCallback);

        Assert.IsTrue(package.TrySave(path, compress: false));

        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(900000, package.FindExport("Source").GetProperty<ObjectProperty>("ObjValue").Value);
    }

    private string Destination(string name) => Path.Combine(temporaryDirectory, name);

    private static IMEPackage CreateObjectVariable(string path, int reference)
    {
        var package = MEPackageHandler.CreateMemoryEmptyPackage(path, MEGame.LE3);
        var export = package.CreateExport("Source", "SeqVar_Object", indexed: false);
        export.WriteProperty(new ObjectProperty(reference, "ObjValue"));
        return package;
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
