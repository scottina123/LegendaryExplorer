using System.Linq;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class CasualAppearanceSequenceObjectTests
{
    private const string ClassName = "SFXSeqAct_OverrideCasualAppearance";

    [TestMethod]
    public void LE3ToolboxRegistrationMatchesEmbeddedCasualAppearanceClass()
    {
        GlobalTest.Init();
        var info = GlobalUnrealObjectInfo.GetClassOrStructInfo(MEGame.LE3, ClassName);
        using var stream = LegendaryExplorerCoreUtilities.GetCustomAppResourceStream(MEGame.LE3);
        using var resource = MEPackageHandler.OpenMEPackageFromStream(stream);
        var cls = resource.GetUExport(info.exportIndex);
        Assert.IsTrue(cls.IsClass);
        Assert.AreEqual(ClassName, cls.ObjectName.Name);
        Assert.AreEqual("Default__" + ClassName,
            resource.GetUExport(ObjectBinary.From<UClass>(cls).Defaults).ObjectName.Name);
    }

    [TestMethod]
    public void LE3ToolboxCreatesReusableCasualAppearanceAction()
    {
        GlobalTest.Init();
        using var package = MEPackageHandler.CreateMemoryEmptyPackage("CasualAppearanceToolboxTest.pcc", MEGame.LE3);
        using var cache = new PackageCache();
        var node = SequenceObjectCreator.CreateSequenceObject(package, ClassName, cache,
            options => Assert.HasCount(0, options.RelinkReport, string.Join("; ", options.RelinkReport)));
        Assert.IsNotNull(node.Class, "The toolbox must attach the embedded implementation to the new action.");
        Assert.AreEqual(ClassName, node.ClassName);
        AssertLinks(node);

        var second = SequenceObjectCreator.CreateSequenceObject(package, ClassName, cache);
        Assert.AreSame(node.Class, second.Class, "Subsequent insertions must reuse the existing class.");
        node.WriteProperty(new IntProperty(7, "CasualAppearanceID"));
        using var saved = package.SaveToStream(false);
        saved.Position = 0;
        using var reloaded = MEPackageHandler.OpenMEPackageFromStream(saved);
        var restored = reloaded.GetUExport(node.UIndex);
        Assert.AreEqual(ClassName, restored.ClassName);
        Assert.AreEqual(7, restored.GetProperty<IntProperty>("CasualAppearanceID").Value);
        AssertLinks(restored);

        var cls = (ExportEntry)restored.Class;
        var defaults = reloaded.GetUExport(ObjectBinary.From<UClass>(cls).Defaults);
        Assert.IsFalse(defaults.GetProperty<BoolProperty>("bCallHandler").Value);
        Assert.AreEqual(-1, defaults.GetProperty<IntProperty>("CasualAppearanceID").Value);
        var activated = reloaded.Exports.Single(e => e.idxLink == cls.UIndex && e.ObjectName.Name == "Activated");
        var (tokens, _) = Bytecode.ParseBytecode(ObjectBinary.From<UFunction>(activated).ScriptBytes, activated);
        Assert.IsTrue(tokens.Any(t => t.text.Contains("Player.OverrideCasualID = (InputLinks[1].bHasImpulse) ? -1 : CasualAppearanceID")));
        Assert.IsTrue(tokens.Any(t => t.text.Contains("Player.UpdateAppearance()")));
        Assert.IsTrue(tokens.Any(t => t.text.Contains("OutputLinks[0].bHasImpulse = True")));
    }

    private static void AssertLinks(ExportEntry node)
    {
        var inputs = node.GetProperty<ArrayProperty<StructProperty>>("InputLinks");
        CollectionAssert.AreEqual(new[] { "Override", "Remove Override" },
            inputs.Select(p => p.GetProp<StrProperty>("LinkDesc").Value).ToArray());
        var variable = node.GetProperty<ArrayProperty<StructProperty>>("VariableLinks").Single();
        Assert.AreEqual("CasualAppearanceID", variable.GetProp<NameProperty>("PropertyName").Value.Name);
        Assert.AreEqual("SeqVar_Int", node.FileRef.GetEntry(variable.GetProp<ObjectProperty>("ExpectedType").Value).ObjectName.Name);
    }
}
