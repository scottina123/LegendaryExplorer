using System.Linq;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.SequenceEditor;

[TestClass]
public class SequenceTreeActionTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void CloneCreatesASiblingWithIndependentContentsAndInternalLinks()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CloneSequence.pcc", MEGame.LE3);
        var parent = CreateSequence(package, "Main_Sequence");
        var source = CreateSequence(package, "Source", parent);
        var nested = CreateSequence(package, "Nested", source);
        var first = CreateAction(nested, "First");
        var second = CreateAction(nested, "Second");
        KismetHelper.CreateNewOutputLink(first, "Out", second);
        KismetHelper.CreateNewOutputLink(source, "Out", CreateAction(parent, "External"));
        var sourceData = source.Data.ToArray();
        var nestedData = nested.Data.ToArray();
        var firstData = first.Data.ToArray();

        var clone = SequenceEditorWPF.CloneSequenceTreeExport(source);

        AssertMembership(clone, parent);
        Assert.AreNotEqual(source.InstancedFullPath, clone.InstancedFullPath);
        var clonedNested = KismetHelper.GetSequenceObjects(clone).OfType<ExportEntry>().Single();
        Assert.AreNotSame(nested, clonedNested);
        AssertMembership(clonedNested, clone);
        var clonedActions = KismetHelper.GetSequenceObjects(clonedNested).OfType<ExportEntry>().ToList();
        var clonedFirst = clonedActions.Single(export => export.ObjectName.Name == "First");
        var clonedSecond = clonedActions.Single(export => export.ObjectName.Name == "Second");
        AssertMembership(clonedFirst, clonedNested);
        AssertMembership(clonedSecond, clonedNested);
        Assert.AreSame(clonedSecond, KismetHelper.GetOutputLinksOfNode(clonedFirst)[0].Single().LinkedOp);
        Assert.IsEmpty(KismetHelper.GetOutputLinksOfNode(clone)[0]);
        CollectionAssert.AreEqual(sourceData, source.Data);
        CollectionAssert.AreEqual(nestedData, nested.Data);
        CollectionAssert.AreEqual(firstData, first.Data);
        Assert.AreSame(second, KismetHelper.GetOutputLinksOfNode(first)[0].Single().LinkedOp);
    }

    [TestMethod]
    public void TrashRemovesTheSubtreeAndIncomingLinksWhileKeepingSiblingSequences()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("TrashSequence.pcc", MEGame.LE3);
        var parent = CreateSequence(package, "Main_Sequence");
        var source = CreateSequence(package, "Source", parent);
        var nested = CreateSequence(package, "Nested", source);
        var child = CreateAction(nested, "Child");
        var sibling = CreateSequence(package, "Sibling", parent);
        var incoming = CreateAction(sibling, "Incoming");
        KismetHelper.CreateNewOutputLink(incoming, "Out", source);

        SequenceEditorWPF.TrashSequenceTreeExport(source);

        CollectionAssert.AreEqual(new[] { sibling.UIndex }, SequenceObjects(parent));
        AssertMembership(sibling, parent);
        Assert.IsEmpty(KismetHelper.GetOutputLinksOfNode(incoming)[0]);
        Assert.IsFalse(source.IsA("Sequence"));
        Assert.IsFalse(nested.IsA("Sequence"));
        Assert.IsFalse(child.IsA("SequenceObject"));
    }

    [TestMethod]
    public void RootCloneAndTrashMaintainLevelGameSequences()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("RootSequence.pcc", MEGame.LE3);
        var world = package.CreateExport("TheWorld", "Package", indexed: false);
        var levelExport = package.CreateExport("PersistentLevel", "Level", world, indexed: false);
        var worldInfo = package.CreateExport("WorldInfo", "BioWorldInfo", levelExport, indexed: false);
        var source = CreateSequence(package, "Main_Sequence");
        source.Parent = levelExport;
        CreateSequence(package, "Nested", source);
        var level = Level.Create(package.Game);
        level.Actors.Add(worldInfo.UIndex);
        level.GameSequences = [source.UIndex];
        levelExport.WriteBinary(level);

        var clone = SequenceEditorWPF.CloneSequenceTreeExport(source);

        Assert.AreSame(levelExport, clone.Parent);
        Assert.IsNull(clone.GetProperty<ObjectProperty>("ParentSequence"));
        CollectionAssert.AreEqual(new[] { source.UIndex, clone.UIndex }, ObjectBinary.From<Level>(levelExport).GameSequences);
        var clonedChild = KismetHelper.GetSequenceObjects(clone).OfType<ExportEntry>().Single();
        AssertMembership(clonedChild, clone);

        SequenceEditorWPF.TrashSequenceTreeExport(source);
        CollectionAssert.AreEqual(new[] { clone.UIndex }, ObjectBinary.From<Level>(levelExport).GameSequences);
        AssertMembership(clonedChild, clone);
        SequenceEditorWPF.TrashSequenceTreeExport(clone);
        Assert.IsEmpty(ObjectBinary.From<Level>(levelExport).GameSequences);
        Assert.IsFalse(package.Exports.Any(export => export.IsA("Sequence")));
    }

    [TestMethod]
    public void ReferencedSequenceActionsOperateOnTheViewportWrapper()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("ReferencedSequence.pcc", MEGame.LE3);
        var parent = CreateSequence(package, "Main_Sequence");
        var reference = package.CreateExport("Reference", "SequenceReference", indexed: false);
        KismetHelper.AddObjectToSequence(reference, parent);
        var sequence = CreateSequence(package, "Referenced", reference);
        reference.WriteProperty(new ObjectProperty(sequence, "oSequenceReference"));
        CreateAction(sequence, "Child");

        var clone = SequenceEditorWPF.CloneSequenceTreeExport(sequence);

        Assert.AreEqual("SequenceReference", clone.ClassName);
        AssertMembership(clone, parent);
        var clonedSequence = (ExportEntry)clone.GetProperty<ObjectProperty>("oSequenceReference").ResolveToEntry(package);
        Assert.AreNotSame(sequence, clonedSequence);
        Assert.AreSame(clone, clonedSequence.Parent);
        var clonedChild = KismetHelper.GetSequenceObjects(clonedSequence).OfType<ExportEntry>().Single();
        AssertMembership(clonedChild, clonedSequence);

        SequenceEditorWPF.TrashSequenceTreeExport(sequence);
        CollectionAssert.AreEqual(new[] { clone.UIndex }, SequenceObjects(parent));
        Assert.IsFalse(reference.IsA("SequenceObject"));
        Assert.IsFalse(sequence.IsA("Sequence"));
        AssertMembership(clonedChild, clonedSequence);
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

    private static ExportEntry CreateAction(ExportEntry parent, string name)
    {
        var action = parent.FileRef.CreateExport(name, "SeqAct_Log", indexed: false);
        KismetHelper.AddObjectToSequence(action, parent);
        return action;
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
