using System;
using System.Linq;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorerCore.UnrealScript.Language.ByteCode;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class CurrentMapSequenceObjectTests
{
    private const string ClassName = "SFXSeqCond_CheckCurrentMap";

    [TestMethod]
    public void LE3ToolboxRegistersEmbeddedCurrentMapCondition()
    {
        GlobalTest.Init();
        var info = SequenceObjectCreator.GetSequenceConditions(MEGame.LE3)
            .Single(c => c.ClassName == ClassName);
        Assert.AreEqual("SequenceCondition", info.baseClass);
        using var stream = LegendaryExplorerCoreUtilities.GetCustomAppResourceStream(MEGame.LE3);
        using var resource = MEPackageHandler.OpenMEPackageFromStream(stream);
        var cls = resource.GetUExport(info.exportIndex);
        Assert.IsTrue(cls.IsClass);
        Assert.AreEqual(ClassName, cls.ObjectName.Name);
        var defaults = resource.GetUExport(ObjectBinary.From<UClass>(cls).Defaults);
        Assert.AreEqual("Default__" + ClassName, defaults.ObjectName.Name);
        Assert.AreEqual("None", defaults.GetProperty<NameProperty>("MapName").Value.Name);
    }

    [TestMethod]
    public void LE3CurrentMapConditionCanBeInsertedWiredAndSaved()
    {
        GlobalTest.Init();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CurrentMapToolboxTest.pcc", MEGame.LE3);
        using var cache = new PackageCache();
        var sequence = SequenceObjectCreator.CreateSequenceObject(package, "Sequence", cache);
        var node = SequenceObjectCreator.CreateSequenceObject(package, ClassName, cache,
            options => Assert.HasCount(0, options.RelinkReport, string.Join("; ", options.RelinkReport)));
        KismetHelper.AddObjectToSequence(node, sequence);
        Assert.AreEqual("None", node.GetProperty<NameProperty>("MapName").Value.Name);
        node.WriteProperty(new NameProperty("BioP_CitHot", "MapName"));
        var other = SequenceObjectCreator.CreateSequenceObject(sequence, ClassName, cache);
        other.WriteProperty(new NameProperty("BioP_Nor", "MapName"));
        Assert.AreSame(node.Class, other.Class);
        KismetHelper.CreateOutputLink(node, "False", other);
        using var saved = package.SaveToStream(false);
        saved.Position = 0;
        using var reloaded = MEPackageHandler.OpenMEPackageFromStream(saved);
        var restored = reloaded.GetUExport(node.UIndex);
        Assert.AreEqual("BioP_CitHot", restored.GetProperty<NameProperty>("MapName").Value.Name);
        Assert.AreEqual("BioP_Nor", reloaded.GetUExport(other.UIndex).GetProperty<NameProperty>("MapName").Value.Name);
        Assert.IsInstanceOfType<ExportEntry>(restored.Class);
        var inputs = restored.GetProperty<ArrayProperty<StructProperty>>("InputLinks");
        Assert.AreEqual("In", inputs.Single().GetProp<StrProperty>("LinkDesc").Value);
        var outputs = restored.GetProperty<ArrayProperty<StructProperty>>("OutputLinks");
        CollectionAssert.AreEqual(new[] { "True", "False" }, outputs.Select(o => o.GetProp<StrProperty>("LinkDesc").Value).ToArray());
        Assert.AreEqual(other.UIndex, outputs[1].GetProp<ArrayProperty<StructProperty>>("Links").Single().GetProp<ObjectProperty>("LinkedOp").Value);
        Assert.HasCount(0, outputs[0].GetProp<ArrayProperty<StructProperty>>("Links"));
    }

    [TestMethod]
    public void LE3CurrentMapBytecodeReadsInstanceMapAndSelectsBothOutputs()
    {
        GlobalTest.Init();
        using var stream = LegendaryExplorerCoreUtilities.GetCustomAppResourceStream(MEGame.LE3);
        using var package = MEPackageHandler.OpenMEPackageFromStream(stream);
        var cls = package.Exports.Single(e => e.IsClass && e.ObjectName.Name == ClassName);
        var mapProperty = package.Exports.Single(e => e.idxLink == cls.UIndex && e.ObjectName.Name == "MapName");
        var activated = package.Exports.Single(e => e.idxLink == cls.UIndex && e.ObjectName.Name == "Activated");
        var script = ObjectBinary.From<UFunction>(activated).ScriptBytes;
        var (statements, tokens) = Bytecode.ParseBytecode(script, activated);
        var reads = tokens.Where(t => t.OpCode is (int)OpCodes.LocalVariable or (int)OpCodes.InstanceVariable)
            .Where(t => BitConverter.ToInt32(script, t.StartPos - 0x20 + 1) == mapProperty.UIndex).ToList();
        Assert.HasCount(2, reads);
        Assert.IsTrue(reads.All(t => t.OpCode == (int)OpCodes.InstanceVariable));
        var text = string.Join("\n", statements.Select(t => t.text));
        StringAssert.Contains(text, "GetWorldInfo()");
        StringAssert.Contains(text, "CurrentWorld.GetMapName(null) ~= NameToString(MapName)");
        StringAssert.Contains(text, "MapName != 'None'");
        StringAssert.Contains(text, "CurrentWorld != None");
        StringAssert.Contains(text, "OutputLinks[0].bHasImpulse = bMatches");
        StringAssert.Contains(text, "OutputLinks[1].bHasImpulse = !bMatches");
    }
}
