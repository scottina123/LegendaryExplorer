using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.WwiseEditor;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using ME3Tweaks.Wwiser;
using ME3Tweaks.Wwiser.Model.Action;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using ME3Tweaks.Wwiser.Model.RTPC;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WwiserAction = ME3Tweaks.Wwiser.Model.Hierarchy.Action;
using WwiserEvent = ME3Tweaks.Wwiser.Model.Hierarchy.Event;
using WwiserIHasNode = ME3Tweaks.Wwiser.Model.Hierarchy.IHasNode;
using WwiserSound = ME3Tweaks.Wwiser.Model.Hierarchy.Sound;
using CoreWwiseEvent = LegendaryExplorerCore.Unreal.BinaryConverters.WwiseEvent;

namespace LegendaryExplorer.Tests.Tools.WwiseEditor;

[TestClass]
public class WwiseEditorWindowTests
{
    private const uint StopAllEventId = 788884573;
    private const uint FactoryRadioEffectId = 2952825346;
    private const uint HelmetFilterEffectId = 125287176;
    private const uint HelmetRtpcId = 0xAA2B753F;
    private static readonly uint[] BioWareRadioEffectIds = [125287176, 1177780410];

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void EffectScopesIncludeNestedOverrideParentFxNodes()
    {
        var bank = LoadTestBank();
        var nodes = GetParameterNodes(bank);
        Assert.IsGreaterThanOrEqualTo(2, nodes.Count);

        var root = nodes[0];
        root.Node.NodeBaseParameters.DirectParentId = 0;
        var nestedOverride = nodes[1];
        nestedOverride.Node.NodeBaseParameters.DirectParentId = root.Id;
        nestedOverride.Node.NodeBaseParameters.FxParams.IsOverrideParentFx = true;

        var effectScopes = InvokePrivate<List<WwiserIHasNode>>("GetEffectScopeNodes", nodes);

        CollectionAssert.Contains(effectScopes, root.Node);
        CollectionAssert.Contains(effectScopes, nestedOverride.Node);
    }

    [TestMethod]
    public void BioWareRadioEffectAppliesToEveryInheritanceScopeAndPreservesOtherEffects()
    {
        var bank = LoadTestBank();
        var nodes = GetParameterNodes(bank);
        Assert.IsGreaterThanOrEqualTo(2, nodes.Count);

        var scopes = new List<WwiserIHasNode> { nodes[0].Node, nodes[1].Node };
        foreach (var scope in scopes)
        {
            scope.NodeBaseParameters.FxParams.FxChunks.Clear();
            scope.NodeBaseParameters.FxParams.NumFx = 0;
            scope.NodeBaseParameters.FxParams.IsOverrideParentFx = false;
        }

        const uint unrelatedEffectId = 0x12345678;
        scopes[0].NodeBaseParameters.FxParams.FxChunks.Add(new FxChunk
        {
            FxIndex = 0,
            Id = FactoryRadioEffectId,
            IsShareSet = true
        });
        scopes[0].NodeBaseParameters.FxParams.NumFx = 1;
        scopes[1].NodeBaseParameters.FxParams.FxChunks.Add(new FxChunk
        {
            FxIndex = 0,
            Id = unrelatedEffectId,
            IsShareSet = true
        });
        scopes[1].NodeBaseParameters.FxParams.FxChunks.Add(new FxChunk
        {
            FxIndex = 1,
            Id = FactoryRadioEffectId,
            IsShareSet = true
        });
        scopes[1].NodeBaseParameters.FxParams.NumFx = 2;

        object bioWareRadio = GetPreset("BioWareRadio");
        Assert.IsTrue(InvokePresetMethod("EnsureEffectData", bank, bioWareRadio));
        InvokePrivate("SetEffectPresetOnScopes", scopes, bioWareRadio);
        InvokePrivate("SetEffectPresetOnScopes", scopes, bioWareRadio);

        CollectionAssert.AreEqual(BioWareRadioEffectIds,
            scopes[0].NodeBaseParameters.FxParams.FxChunks.Select(chunk => chunk.Id).ToArray());
        CollectionAssert.AreEqual(new[] { unrelatedEffectId }.Concat(BioWareRadioEffectIds).ToArray(),
            scopes[1].NodeBaseParameters.FxParams.FxChunks.Select(chunk => chunk.Id).ToArray());
        Assert.IsTrue(scopes.All(scope => scope.NodeBaseParameters.FxParams.IsOverrideParentFx));

        var reparsed = RoundTrip(bank);
        foreach (var scopeId in scopes.Select(scope => ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)scope).Id))
        {
            var reparsedScope = reparsed.HIRC.Items.Select(item => item.Item)
                .OfType<WwiserIHasNode>()
                .Single(item => ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)item).Id == scopeId);
            Assert.IsTrue(BioWareRadioEffectIds.All(id =>
                reparsedScope.NodeBaseParameters.FxParams.FxChunks.Any(chunk => chunk.Id == id)));
        }
    }

    [TestMethod]
    public void HelmetEffectAppliesFilterAndRtpcToEveryInheritanceScope()
    {
        var bank = LoadTestBank();
        var nodes = GetParameterNodes(bank);
        Assert.IsGreaterThanOrEqualTo(2, nodes.Count);

        var scopes = new List<WwiserIHasNode> { nodes[0].Node, nodes[1].Node };
        foreach (var scope in scopes)
        {
            scope.NodeBaseParameters.FxParams.FxChunks.Clear();
            scope.NodeBaseParameters.FxParams.NumFx = 0;
            scope.NodeBaseParameters.Rtpc.Rtpcs.RemoveAll(rtpc => rtpc.RtpcId == HelmetRtpcId);
            scope.NodeBaseParameters.Rtpc.RTPCCount.Value =
                checked((ushort)scope.NodeBaseParameters.Rtpc.Rtpcs.Count);
        }

        object helmetFilter = GetPreset("HelmetFilter");
        Assert.IsTrue(InvokePresetMethod("EnsureEffectData", bank, helmetFilter));
        InvokePrivate("SetEffectPresetOnScopes", scopes, helmetFilter);
        InvokePrivate("SetEffectPresetOnScopes", scopes, helmetFilter);

        Assert.IsTrue(InvokePresetMethod("HasHelmetRtpcOnAllScopes", scopes));
        foreach (var scope in scopes)
        {
            CollectionAssert.AreEqual(new[] { HelmetFilterEffectId },
                scope.NodeBaseParameters.FxParams.FxChunks.Select(chunk => chunk.Id).ToArray());
            var helmetRtpcs = scope.NodeBaseParameters.Rtpc.Rtpcs
                .Where(rtpc => rtpc.RtpcId == HelmetRtpcId)
                .ToList();
            Assert.HasCount(1, helmetRtpcs);
            var helmetRtpc = helmetRtpcs[0];
            Assert.AreEqual(RtpcType.RtpcTypeInner.GameParameter, helmetRtpc.RtpcType.Value);
            Assert.AreEqual(AccumType.AccumTypeInner.Boolean, helmetRtpc.RtpcAccum.Value);
            Assert.AreEqual(ParameterId.RtpcParameterId.BypassFX0, helmetRtpc.ParamId.ParamId);
            CollectionAssert.AreEqual(new[] { 0f, 1f },
                helmetRtpc.RtpcConversionTable.Graph.Select(point => point.From).ToArray());
            CollectionAssert.AreEqual(new[] { 1f, 0f },
                helmetRtpc.RtpcConversionTable.Graph.Select(point => point.To).ToArray());
        }

        var reparsed = RoundTrip(bank);
        var reparsedScopes = reparsed.HIRC.Items.Select(item => item.Item)
            .OfType<WwiserIHasNode>()
            .Where(scope => scopes.Select(item => ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)item).Id)
                .Contains(((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)scope).Id))
            .ToList();
        Assert.HasCount(scopes.Count, reparsedScopes);
        Assert.IsTrue(InvokePresetMethod("HasHelmetRtpcOnAllScopes", reparsedScopes));

        object bioWareRadio = GetPreset("BioWareRadio");
        Assert.IsTrue(InvokePresetMethod("EnsureEffectData", bank, bioWareRadio));
        InvokePrivate("SetEffectPresetOnScopes", scopes, bioWareRadio);
        Assert.IsFalse(InvokePresetMethod("HasHelmetRtpcOnAllScopes", scopes));
    }

    [TestMethod]
    public void StopAllEventCreatesOneStopActionPerSoundAndRoundTrips()
    {
        var bank = LoadTestBank();
        var sounds = bank.HIRC.Items.Select(item => item.Item).OfType<WwiserSound>().ToList();
        Assert.IsNotEmpty(sounds);
        Assert.IsFalse(bank.HIRC.Items.Any(item => item.Item.Id == StopAllEventId));

        InvokePrivate("EnsureStopAllEventInBank", bank, sounds);
        int itemCountAfterFirstApply = bank.HIRC.Items.Count;
        InvokePrivate("EnsureStopAllEventInBank", bank, sounds);
        Assert.AreEqual(itemCountAfterFirstApply, bank.HIRC.Items.Count);

        AssertStopEventCoversSounds(bank, sounds);
        AssertStopEventCoversSounds(RoundTrip(bank), sounds);
    }

    [TestMethod]
    public void StopAllEventExportLinksBankAndEveryMatchingStream()
    {
        var sourceBank = LoadTestBank();
        var sounds = sourceBank.HIRC.Items.Select(item => item.Item).OfType<WwiserSound>()
            .GroupBy(sound => sound.BankSourceData.MediaInformation.SourceId)
            .Select(group => group.First())
            .Take(2)
            .ToList();
        Assert.HasCount(2, sounds);

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseSettingsTest.pcc", MEGame.LE3);
        ExportEntry parent = package.CreatePackageExport("Audio", forcedExport: false);
        ExportEntry bankExport = package.CreateExport("TestBank", "WwiseBank", parent, indexed: false);
        bankExport.WriteProperty(new BoolProperty(true, "IsLocalised"));

        var expectedStreamIndexes = new List<int>();
        foreach (var sound in sounds)
        {
            ExportEntry stream = package.CreateExport($"Stream_{sound.Id}", "WwiseStream", parent, indexed: false);
            stream.WriteProperty(new IntProperty(
                unchecked((int)sound.BankSourceData.MediaInformation.SourceId), "Id"));
            expectedStreamIndexes.Add(stream.UIndex);
        }

        InvokePrivate("EnsureStopAllEventExport", bankExport, sounds);
        InvokePrivate("EnsureStopAllEventExport", bankExport, sounds);

        var stopExports = package.Exports.Where(export => export.ClassName == "WwiseEvent" &&
            export.Parent == parent && export.ObjectNameString == "Stop").ToList();
        Assert.HasCount(1, stopExports);
        ExportEntry stopExport = stopExports[0];
        Assert.AreEqual(unchecked((int)StopAllEventId), stopExport.GetProperty<IntProperty>("Id")?.Value);
        Assert.AreEqual(bankExport.UIndex, stopExport.GetProperty<StructProperty>("Relationships")?.Properties
            .GetProp<ObjectProperty>("Bank")?.Value);
        Assert.IsTrue(stopExport.GetProperty<BoolProperty>("IsLocalised")?.Value);
        CollectionAssert.AreEquivalent(expectedStreamIndexes,
            stopExport.GetBinaryData<CoreWwiseEvent>().Links[0].WwiseStreams);
    }

    private static void AssertStopEventCoversSounds(ME3Tweaks.Wwiser.WwiseBank bank,
        IReadOnlyCollection<WwiserSound> expectedSounds)
    {
        var stopEvent = bank.HIRC.Items.Select(item => item.Item).OfType<WwiserEvent>()
            .Single(item => item.Id == StopAllEventId);
        var actions = bank.HIRC.Items.Select(item => item.Item).OfType<WwiserAction>()
            .Where(action => stopEvent.ActionIds.Contains(action.Id))
            .ToList();

        Assert.AreEqual(expectedSounds.Count, stopEvent.ActionIds.Count);
        Assert.AreEqual(expectedSounds.Count, actions.Count);
        CollectionAssert.AreEquivalent(expectedSounds.Select(sound => sound.Id).ToArray(),
            actions.Select(action => action.TargetId).ToArray());
        Assert.IsTrue(actions.All(action => action.Type.Value == ActionTypeValue.Stop));
        Assert.IsTrue(actions.All(action => action.ActionParams is Active active &&
            active.Params.CurveInterpolation == CurveInterpolation.Linear));
    }

    private static ME3Tweaks.Wwiser.WwiseBank LoadTestBank()
    {
        using var stream = File.OpenRead(FindWwiserTestBank("LE3_v134_1.bnk"));
        return WwiseBankParser.Deserialize(stream);
    }

    private static ME3Tweaks.Wwiser.WwiseBank RoundTrip(ME3Tweaks.Wwiser.WwiseBank bank)
    {
        using var stream = new MemoryStream();
        WwiseBankParser.Serialize(bank, stream);
        stream.Position = 0;
        return WwiseBankParser.Deserialize(stream);
    }

    private static List<(uint Id, WwiserIHasNode Node)> GetParameterNodes(ME3Tweaks.Wwiser.WwiseBank bank) =>
        bank.HIRC.Items
            .Where(item => item.Item is WwiserIHasNode)
            .Select(item => (item.Item.Id, (WwiserIHasNode)item.Item))
            .ToList();

    private static object GetPreset(string propertyName)
    {
        Type presets = typeof(WwiseEditorWindow).Assembly
            .GetType("LegendaryExplorer.UnrealExtensions.WwiseBankEffectPresets");
        Assert.IsNotNull(presets);
        object value = presets.GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        Assert.IsNotNull(value);
        return value;
    }

    private static bool InvokePresetMethod(string methodName, params object[] arguments)
    {
        Type presets = typeof(WwiseEditorWindow).Assembly
            .GetType("LegendaryExplorer.UnrealExtensions.WwiseBankEffectPresets");
        Assert.IsNotNull(presets);
        MethodInfo method = presets.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (bool)method.Invoke(null, arguments);
    }

    private static void InvokePrivate(string methodName, params object[] arguments)
    {
        MethodInfo method = typeof(WwiseEditorWindow).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        method.Invoke(null, arguments);
    }

    private static T InvokePrivate<T>(string methodName, params object[] arguments)
    {
        MethodInfo method = typeof(WwiseEditorWindow).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (T)method.Invoke(null, arguments);
    }

    private static string FindWwiserTestBank(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "submodules", "Wwiser.NET",
                "ME3Tweaks.Wwiser.Tests", "TestData", "WholeBanks", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail($"Could not locate Wwiser test bank '{fileName}'.");
        return null;
    }
}
