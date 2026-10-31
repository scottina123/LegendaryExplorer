using System;
using System.Linq;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.UnrealScript.Language.ByteCode;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class StuntMeshSequenceObjectTests
{
    [TestMethod]
    [DataRow(MEGame.LE3, "SFXSeqAct_SetStuntMeshes")]
    [DataRow(MEGame.ME3, "SFXSeqAct_SetStuntMeshes")]
    [DataRow(MEGame.ME3, "SFXSeqAct_SetStuntBodyMesh")]
    [DataRow(MEGame.ME3, "SFXSeqAct_SetStuntGearMesh")]
    public void ToolboxStuntMeshesReadPreserveAnimationFromInstance(MEGame game, string className)
    {
        GlobalTest.Init();
        using var resourceStream = LegendaryExplorerCoreUtilities.GetCustomAppResourceStream(game);
        using var resource = MEPackageHandler.OpenMEPackageFromStream(resourceStream);
        AssertInstanceVariableRead(resource, className);

        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("StuntMeshToolboxTest.pcc", game);
        using var cache = new PackageCache();
        var node = SequenceObjectCreator.CreateSequenceObject(destination, className, cache,
            options => Assert.HasCount(0, options.RelinkReport, string.Join("; ", options.RelinkReport)));
        AssertInstanceVariableRead(destination, className);

        // Check the reference again after saving and reloading the inserted class.
        node.WriteProperty(new BoolProperty(true, "bPreserveAnimation"));
        using var saved = destination.SaveToStream(false);
        saved.Position = 0;
        using var reloaded = MEPackageHandler.OpenMEPackageFromStream(saved);
        Assert.IsTrue(reloaded.GetUExport(node.UIndex).GetProperty<BoolProperty>("bPreserveAnimation").Value);
        AssertInstanceVariableRead(reloaded, className);
    }

    private static void AssertInstanceVariableRead(IMEPackage package, string className)
    {
        var cls = package.Exports.Single(e => e.IsClass && e.ObjectName.Name == className);
        var property = package.Exports.Single(e => e.idxLink == cls.UIndex && e.ObjectName.Name == "bPreserveAnimation");
        Assert.AreEqual("BoolProperty", property.ClassName);
        var function = package.Exports.Single(e => e.idxLink == cls.UIndex && e.ObjectName.Name == "ResetSkeletalMesh");
        var script = ObjectBinary.From<UFunction>(function).ScriptBytes;
        var (_, tokens) = Bytecode.ParseBytecode(script, function);
        var reads = tokens.Where(t => t.OpCode is (int)OpCodes.LocalVariable or (int)OpCodes.InstanceVariable)
            .Where(t => BitConverter.ToInt32(script, t.StartPos - 0x20 + 1) == property.UIndex).ToList();

        Assert.HasCount(1, reads, $"Expected one bPreserveAnimation read in {function.InstancedFullPath}.");
        Assert.AreEqual((int)OpCodes.InstanceVariable, reads[0].OpCode,
            $"{package.Game} {className} must read the action's property, not a function local.");
    }
}
