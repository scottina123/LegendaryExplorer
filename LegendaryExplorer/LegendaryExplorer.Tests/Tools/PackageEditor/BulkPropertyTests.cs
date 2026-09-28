using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.PackageEditor;

[TestClass]
public class BulkPropertyTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void PropertyPickerIncludesPropertiesMissingFromFirstExportAndKeepsStaticSlotsSeparate()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BulkProperties.pcc", MEGame.LE3);
        var first = package.CreateExport("First", "Object", indexed: false);
        var second = package.CreateExport("Second", "Object", indexed: false);
        first.WriteProperties(new PropertyCollection { new IntProperty(1, "Shared") });
        second.WriteProperties(new PropertyCollection
        {
            new IntProperty(2, "Shared"),
            new IntProperty(3, "Shared") { StaticArrayIndex = 1 },
            new StrProperty("Only on second", "Optional"),
            new StructProperty("CustomStruct", new PropertyCollection { new IntProperty(4, "Nested") }, "Struct"),
            new NoneProperty()
        });
        var failures = new List<string>();

        var targets = ((IEnumerable)Invoke("GetBulkPropertyTargets", new[] { first, second }, failures)).Cast<object>().ToList();

        Assert.IsEmpty(failures);
        Assert.HasCount(4, targets, "Only root properties should be offered, excluding the None terminator.");
        var shared = targets.Single(target => Get<Property>(target, "Property").Name == "Shared"
            && Get<Property>(target, "Property").StaticArrayIndex == 0);
        CollectionAssert.AreEqual(new[] { first, second }, Get<List<ExportEntry>>(shared, "Exports"));
        var slot = targets.Single(target => Get<Property>(target, "Property").StaticArrayIndex == 1);
        CollectionAssert.AreEqual(new[] { second }, Get<List<ExportEntry>>(slot, "Exports"));
        StringAssert.Contains(Get<string>(slot, "DisplayName"), "Shared[1]");
        Assert.IsTrue(targets.Any(target => Get<Property>(target, "Property").Name == "Optional"));
    }

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.LE3)]
    public void DeleteRemovesOnlySelectedStaticSlotAndPreservesBinaryAndAbsentExports(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BulkDelete.pcc", game);
        var first = package.CreateExport("First", "Object", indexed: false);
        var second = package.CreateExport("Second", "Object", indexed: false);
        var absent = package.CreateExport("Absent", "Object", indexed: false);
        foreach (var export in new[] { first, second })
        {
            export.WriteProperties(new PropertyCollection
            {
                new IntProperty(10, "Value"),
                new IntProperty(20, "Value") { StaticArrayIndex = 1 },
                new IntProperty(30, new NameReference("Value", 2)) { StaticArrayIndex = 1 },
                new StrProperty(export.ObjectName.Name, "Unrelated")
            });
            export.WriteBinary(new byte[] { 1, 3, 5, 7 });
        }
        absent.WriteProperties(new PropertyCollection { new IntProperty(99, "Value") });
        byte[] absentData = absent.Data.ToArray();

        var result = Update(new[] { first, absent, second }, "Value", 1, null);

        Assert.AreEqual(2, result.ModifiedCount);
        Assert.IsEmpty(result.Failures);
        CollectionAssert.AreEqual(absentData, absent.Data);
        foreach (var export in new[] { first, second })
        {
            var props = export.GetProperties();
            Assert.IsNull(props.GetProp<Property>("Value", 1));
            Assert.AreEqual(10, props.GetProp<IntProperty>("Value").Value);
            Assert.AreEqual(30, props.GetProp<IntProperty>(new NameReference("Value", 2), 1).Value);
            Assert.AreEqual(export.ObjectName.Name, props.GetProp<StrProperty>("Unrelated").Value);
            CollectionAssert.AreEqual(new byte[] { 1, 3, 5, 7 }, export.GetBinaryData());
        }
    }

    [TestMethod]
    [DataRow(MEGame.ME2)]
    [DataRow(MEGame.LE3)]
    public void EditChangesExistingValuesAndContinuesPastTypeMismatchesWithoutAddingMissingProperties(MEGame game)
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BulkEdit.pcc", game);
        var first = package.CreateExport("First", "Object", indexed: false);
        var mismatch = package.CreateExport("Mismatch", "Object", indexed: false);
        var absent = package.CreateExport("Absent", "Object", indexed: false);
        var second = package.CreateExport("Second", "Object", indexed: false);
        foreach (var export in new[] { first, second })
        {
            export.WriteProperties(new PropertyCollection
            {
                new IntProperty(10, "Value"),
                new IntProperty(20, "Value") { StaticArrayIndex = 1 },
                new StrProperty(export.ObjectName.Name, "Unrelated")
            });
            export.WriteBinary(new byte[] { 2, 4, 6, 8 });
        }
        mismatch.WriteProperties(new PropertyCollection { new StrProperty("Keep me", "Value") { StaticArrayIndex = 1 } });
        absent.WriteProperties(new PropertyCollection { new IntProperty(99, "Value") });
        byte[] absentData = absent.Data.ToArray();
        byte[] mismatchData = mismatch.Data.ToArray();
        var replacement = new IntProperty(42, "Value") { StaticArrayIndex = 1 };

        var result = Update(new[] { first, mismatch, absent, second }, "Value", 1, replacement);

        Assert.AreEqual(2, result.ModifiedCount);
        Assert.HasCount(1, result.Failures);
        StringAssert.Contains(result.Failures[0], $"#{mismatch.UIndex}");
        CollectionAssert.AreEqual(absentData, absent.Data);
        CollectionAssert.AreEqual(mismatchData, mismatch.Data);
        foreach (var export in new[] { first, second })
        {
            var props = export.GetProperties();
            Assert.AreEqual(42, props.GetProp<IntProperty>("Value", 1).Value);
            Assert.AreEqual(10, props.GetProp<IntProperty>("Value").Value);
            Assert.AreEqual(export.ObjectName.Name, props.GetProp<StrProperty>("Unrelated").Value);
            CollectionAssert.AreEqual(new byte[] { 2, 4, 6, 8 }, export.GetBinaryData());
        }
        Assert.AreEqual(42, replacement.Value);
        Assert.AreEqual(1, replacement.StaticArrayIndex);
    }

    [TestMethod]
    public void AddWorkflowCanStillFillMissingPropertiesAndEditAllValues()
    {
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("BulkAdd.pcc", MEGame.LE3);
        var first = package.CreateExport("First", "Object", indexed: false);
        var second = package.CreateExport("Second", "Object", indexed: false);
        first.WriteProperties(new PropertyCollection { new IntProperty(10, "Value") });
        second.WriteProperties(new PropertyCollection { new StrProperty("Keep me", "Unrelated") });

        var result = Update(new[] { first, second }, "Value", 0, new IntProperty(42, "Value"), addIfMissing: true);

        Assert.AreEqual(2, result.ModifiedCount);
        Assert.IsEmpty(result.Failures);
        Assert.AreEqual(42, first.GetProperty<IntProperty>("Value").Value);
        Assert.AreEqual(42, second.GetProperty<IntProperty>("Value").Value);
        Assert.AreEqual("Keep me", second.GetProperty<StrProperty>("Unrelated").Value);
    }

    private static (int ModifiedCount, List<string> Failures) Update(IEnumerable<ExportEntry> exports,
        NameReference name, int index, Property replacement, bool addIfMissing = false) =>
        ((int, List<string>))Invoke("UpdateBulkProperty", exports, name, index, replacement, addIfMissing);

    private static object Invoke(string method, params object[] args) =>
        typeof(PackageEditorExperimentsScottina).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    private static T Get<T>(object target, string property) => (T)target.GetType().GetProperty(property)!.GetValue(target);
}
