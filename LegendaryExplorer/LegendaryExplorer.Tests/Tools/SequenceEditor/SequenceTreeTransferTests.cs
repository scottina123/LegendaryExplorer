using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.SequenceEditor;

[TestClass]
public class SequenceTreeTransferTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void CopyPreservesNestedSequencesAndLinksWithoutOverwritingDestinationOrSource()
    {
        using var sourcePackage = MEPackageHandler.CreateMemoryEmptyPackage("Source.pcc", MEGame.LE3);
        using var destinationPackage = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", MEGame.LE3);
        var parent = CreateSequence(sourcePackage, "Main_Sequence");
        var source = CreateSequence(sourcePackage, "Miranda_StateCheck", parent);
        var nested = CreateSequence(sourcePackage, "Nested", source);
        var first = sourcePackage.CreateExport("First", "SeqAct_Log", indexed: false);
        var second = sourcePackage.CreateExport("Second", "SeqAct_Log", indexed: false);
        KismetHelper.AddObjectToSequence(first, nested);
        KismetHelper.AddObjectToSequence(second, nested);
        KismetHelper.CreateNewOutputLink(first, "Out", second, 2);
        // Tree children such as InterpData groups also need copying even without a property reference.
        var child = sourcePackage.CreateExport("OwnedChild", "Object", indexed: false);
        child.Parent = first;
        var destination = CreateSequence(destinationPackage, "Camps");
        var existing = CreateSequence(destinationPackage, "Existing", destination);
        destination.WriteProperty(new StrProperty("Destination contents", "ObjName"));
        var sourceData = sourcePackage.Exports.ToDictionary(export => export, export => export.Data.ToArray());
        var sourceName = source.ObjectName;
        var sourceChanged = source.EntryHasPendingChanges;
        var sourceHeaderChanged = source.HeaderChanged;

        using var cache = new PackageCache();
        var rop = new RelinkerOptionsPackage(cache);
        var copy = SequenceTreeTransfer.Copy(source, destination, rop);

        Assert.IsEmpty(rop.RelinkReport);
        AssertMembership(copy, destination);
        CollectionAssert.AreEqual(new[] { existing.UIndex, copy.UIndex }, SequenceObjects(destination));
        Assert.AreEqual("Destination contents", destination.GetProperty<StrProperty>("ObjName").Value);
        var nestedCopy = (ExportEntry)rop.CrossPackageMap[nested];
        var firstCopy = (ExportEntry)rop.CrossPackageMap[first];
        var secondCopy = (ExportEntry)rop.CrossPackageMap[second];
        AssertMembership(nestedCopy, copy);
        AssertMembership(firstCopy, nestedCopy);
        AssertMembership(secondCopy, nestedCopy);
        Assert.AreSame(firstCopy, rop.CrossPackageMap[child].Parent);
        var output = firstCopy.GetProperty<ArrayProperty<StructProperty>>("OutputLinks")[0]
            .GetProp<ArrayProperty<StructProperty>>("Links")[0];
        Assert.AreEqual(secondCopy.UIndex, output.GetProp<ObjectProperty>("LinkedOp").Value);
        Assert.AreEqual(2, output.GetProp<IntProperty>("InputLinkIdx").Value);
        Assert.IsFalse(destinationPackage.Exports.Any(export => export.ObjectName == parent.ObjectName));
        foreach (var (export, data) in sourceData)
        {
            CollectionAssert.AreEqual(data, export.Data);
        }
        Assert.AreEqual(sourceName, source.ObjectName);
        Assert.AreEqual(sourceChanged, source.EntryHasPendingChanges);
        Assert.AreEqual(sourceHeaderChanged, source.HeaderChanged);
    }

    [TestMethod]
    public void RepeatedDropsCreateIndependentCopiesEvenWhenTheSourcePathAlreadyExists()
    {
        using var sourcePackage = MEPackageHandler.CreateMemoryEmptyPackage("Source.pcc", MEGame.LE3);
        using var destinationPackage = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", MEGame.LE3);
        var sourceParent = CreateSequence(sourcePackage, "Main_Sequence");
        var source = CreateSequence(sourcePackage, "Child", sourceParent);
        var nested = CreateSequence(sourcePackage, "Nested", source);
        var existingParent = CreateSequence(destinationPackage, "Main_Sequence");
        var existing = CreateSequence(destinationPackage, "Child", existingParent);
        var destination = CreateSequence(destinationPackage, "Camps");
        var existingData = existing.Data.ToArray();

        using var cache = new PackageCache();
        var firstOptions = new RelinkerOptionsPackage(cache);
        var first = SequenceTreeTransfer.Copy(source, destination, firstOptions);
        var secondOptions = new RelinkerOptionsPackage(cache);
        var second = SequenceTreeTransfer.Copy(source, destination, secondOptions);

        Assert.IsEmpty(firstOptions.RelinkReport);
        Assert.IsEmpty(secondOptions.RelinkReport);
        Assert.AreNotSame(existing, first);
        Assert.AreNotSame(first, second);
        Assert.AreNotEqual(first.InstancedFullPath, second.InstancedFullPath);
        AssertMembership(first, destination);
        AssertMembership(second, destination);
        AssertMembership((ExportEntry)firstOptions.CrossPackageMap[nested], first);
        AssertMembership((ExportEntry)secondOptions.CrossPackageMap[nested], second);
        Assert.AreNotSame(firstOptions.CrossPackageMap[nested], secondOptions.CrossPackageMap[nested]);
        AssertMembership(existing, existingParent);
        CollectionAssert.AreEqual(existingData, existing.Data);
        CollectionAssert.AreEqual(new[] { first.UIndex, second.UIndex }, SequenceObjects(destination));
        Assert.AreEqual("Child", source.ObjectName.Instanced);
    }

    [TestMethod]
    public void RootSequenceCanBeCopiedWithoutAnExistingParentSequence()
    {
        using var sourcePackage = MEPackageHandler.CreateMemoryEmptyPackage("Source.pcc", MEGame.LE3);
        using var destinationPackage = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", MEGame.LE3);
        var source = CreateSequence(sourcePackage, "Main_Sequence");
        var nested = CreateSequence(sourcePackage, "Nested", source);
        var destination = CreateSequence(destinationPackage, "Main_Sequence");

        using var cache = new PackageCache();
        var rop = new RelinkerOptionsPackage(cache);
        var copy = SequenceTreeTransfer.Copy(source, destination, rop);

        Assert.IsEmpty(rop.RelinkReport);
        AssertMembership(copy, destination);
        AssertMembership((ExportEntry)rop.CrossPackageMap[nested], copy);
        Assert.IsNull(source.GetProperty<ObjectProperty>("ParentSequence"));
    }

    [TestMethod]
    public void DropsRequireSequencesInDifferentPackages()
    {
        using var sourcePackage = MEPackageHandler.CreateMemoryEmptyPackage("Source.pcc", MEGame.LE3);
        using var destinationPackage = MEPackageHandler.CreateMemoryEmptyPackage("Destination.pcc", MEGame.LE3);
        var source = CreateSequence(sourcePackage, "Main_Sequence");
        var destination = CreateSequence(destinationPackage, "Main_Sequence");
        var nonSequence = destinationPackage.CreateExport("InterpData", "InterpData", indexed: false);

        Assert.IsTrue(SequenceTreeTransfer.CanCopy(source, destination));
        Assert.IsFalse(SequenceTreeTransfer.CanCopy(source, source));
        Assert.IsFalse(SequenceTreeTransfer.CanCopy(source, nonSequence));
        Assert.IsFalse(SequenceTreeTransfer.CanCopy(nonSequence, source));
        Assert.IsFalse(SequenceTreeTransfer.CanCopy(null, destination));
        Assert.IsFalse(SequenceTreeTransfer.CanCopy(source, null));
    }

    private static ExportEntry CreateSequence(IMEPackage package, string name, ExportEntry parent = null)
    {
        var sequence = package.CreateExport(name, "Sequence", indexed: false);
        sequence.WriteProperty(new ArrayProperty<ObjectProperty>("SequenceObjects"));
        if (parent != null)
        {
            KismetHelper.AddObjectToSequence(sequence, parent);
        }
        return sequence;
    }

    private static int[] SequenceObjects(ExportEntry sequence) => sequence
        .GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects").Select(property => property.Value).ToArray();

    private static void AssertMembership(ExportEntry sequenceObject, ExportEntry parent)
    {
        Assert.AreSame(parent, sequenceObject.Parent);
        Assert.AreEqual(parent.UIndex, sequenceObject.GetProperty<ObjectProperty>("ParentSequence").Value);
        Assert.AreEqual(1, SequenceObjects(parent).Count(index => index == sequenceObject.UIndex));
    }
}
