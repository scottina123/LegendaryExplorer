using System;
using System.Collections.Generic;
using System.Drawing;
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
using ME3Tweaks.Wwiser.Formats;
using ME3Tweaks.Wwiser.Model.Action;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using ME3Tweaks.Wwiser.Model.RTPC;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Piccolo;
using WwiserAction = ME3Tweaks.Wwiser.Model.Hierarchy.Action;
using WwiserEvent = ME3Tweaks.Wwiser.Model.Hierarchy.Event;
using WwiserHircItemContainer = ME3Tweaks.Wwiser.Model.Hierarchy.HircItemContainer;
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

    [TestMethod]
    public void Le2WwiseEventIdComesFromBinaryData()
    {
        const uint eventId = 0xF1234567;
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Le2WwiseEventIdTest.pcc", MEGame.LE2);
        ExportEntry wwiseEvent = package.CreateExport("Test_Play", "WwiseEvent", indexed: false);
        wwiseEvent.WritePropertiesAndBinary(new PropertyCollection(), new CoreWwiseEvent
        {
            WwiseEventID = eventId,
            Links = []
        });

        Assert.AreEqual(eventId, WExport.GetExportId(wwiseEvent));
    }

    [TestMethod]
    public void Le3WwiseEventFindsReferencedBankForPackageEditorSettings()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Le3WwiseEventBankTest.pcc", MEGame.LE3);
        ExportEntry parent = package.CreatePackageExport("Audio", forcedExport: false);
        ExportEntry bank = package.CreateExport("TestBank", "WwiseBank", parent, indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("Test_Play", "WwiseEvent", parent, indexed: false);
        wwiseEvent.WriteProperty(new StructProperty("WwiseRelationships", false,
            new ObjectProperty(bank, "Bank")) { Name = "Relationships" });

        Assert.AreSame(bank, InvokePrivate<ExportEntry>("FindReferencedBank", wwiseEvent));
    }

    [TestMethod]
    public void Le2WwiseEventFindsReferencedBankForPackageEditorSettings()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Le2WwiseEventBankTest.pcc", MEGame.LE2);
        ExportEntry parent = package.CreatePackageExport("Audio", forcedExport: false);
        ExportEntry bank = package.CreateExport("TestBank", "WwiseBank", parent, indexed: false);
        ExportEntry wwiseEvent = package.CreateExport("Test_Play", "WwiseEvent", parent, indexed: false);
        var references = new ArrayProperty<StructProperty>("References");
        references.Add(new StructProperty("WwisePlatformRelationships", new PropertyCollection
        {
            new StructProperty("WwiseRelationships", new PropertyCollection
            {
                new ObjectProperty(bank, "Bank")
            }, "Relationships"),
            new IntProperty(1, "Platform")
        }));
        wwiseEvent.WriteProperty(references);

        Assert.AreSame(bank, InvokePrivate<ExportEntry>("FindReferencedBank", wwiseEvent));
    }

    [TestMethod]
    public void EventTargetsResolvePlayActionsThroughHierarchyToSoundNodes()
    {
        var bank = LoadTestBank();
        var sound = bank.HIRC.Items.Select(item => item.Item).OfType<WwiserSound>().First();
        uint playTargetId = sound.NodeBaseParameters.DirectParentId;
        Assert.AreNotEqual(0u, playTargetId);
        const uint actionId = 0xF0000001;
        const uint eventId = 0xF0000002;
        var playAction = new WwiserAction
        {
            Id = actionId,
            Type = new ActionType { Value = ActionTypeValue.Play },
            TargetId = playTargetId,
            ActionParams = new Active
            {
                SpecificParams = new ME3Tweaks.Wwiser.Model.Action.Specific.Action()
            }
        };
        var playEvent = new WwiserEvent
        {
            Id = eventId,
            ActionCount = new VarCount { Value = 1 },
            ActionIds = [actionId]
        };
        bank.HIRC.Items.Add(new WwiserHircItemContainer
        {
            Type = new HircSmartType { Value = HircType.Action },
            Item = playAction
        });
        bank.HIRC.Items.Add(new WwiserHircItemContainer
        {
            Type = new HircSmartType { Value = HircType.Event },
            Item = playEvent
        });

        var targets = InvokePrivate<List<WwiserSound>>("GetEventTargetSounds", bank, eventId,
            GetParameterNodes(bank));

        CollectionAssert.Contains(targets, sound);
    }

    [TestMethod]
    public void Le2StopEventExportStoresIdAndRelationshipsInLe2Layout()
    {
        var sourceBank = LoadTestBank();
        var sounds = sourceBank.HIRC.Items.Select(item => item.Item).OfType<WwiserSound>()
            .GroupBy(sound => sound.BankSourceData.MediaInformation.SourceId)
            .Select(group => group.First())
            .Take(2)
            .ToList();
        Assert.HasCount(2, sounds);

        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("Le2WwiseSettingsTest.pcc", MEGame.LE2);
        ExportEntry parent = package.CreatePackageExport("Audio", forcedExport: false);
        ExportEntry bankExport = package.CreateExport("TestBank", "WwiseBank", parent, indexed: false);
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
        Assert.AreEqual(StopAllEventId, WExport.GetExportId(stopExport));
        Assert.AreEqual(StopAllEventId, stopExport.GetBinaryData<CoreWwiseEvent>().WwiseEventID);
        var reference = stopExport.GetProperty<ArrayProperty<StructProperty>>("References")?.Single();
        Assert.IsNotNull(reference);
        var relationships = reference.Properties.GetProp<StructProperty>("Relationships");
        Assert.IsNotNull(relationships);
        Assert.AreEqual(bankExport.UIndex,
            relationships.Properties.GetProp<ObjectProperty>("Bank")?.Value);
        CollectionAssert.AreEquivalent(expectedStreamIndexes,
            relationships.Properties.GetProp<ArrayProperty<ObjectProperty>>("Streams")
                ?.Select(stream => stream.Value).ToList());
    }

    [TestMethod]
    public void WwiseEventGraphLabelIncludesResolvedTlkText()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseEventGraphLabelTest.pcc", MEGame.LE3);
        ExportEntry wwiseEvent = package.CreateExport("VO_788349_m_Play", "WwiseEvent", indexed: false);

        string label = WExport.BuildDisplayValue(wwiseEvent, (tlkId, lookupPackage) =>
        {
            Assert.AreEqual(788349, tlkId);
            Assert.AreSame(package, lookupPackage);
            return "The resolved subtitle";
        });

        Assert.AreEqual($"#{wwiseEvent.UIndex} VO_788349_m_Play\nTLK 788349: The resolved subtitle", label);
    }

    [TestMethod]
    public void WwiseEventGraphLabelKeepsExportNameWhenTlkCannotBeResolved()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseEventGraphLabelTest.pcc", MEGame.LE3);
        ExportEntry wwiseEvent = package.CreateExport("VO_788349_m_Play", "WwiseEvent", indexed: false);

        string label = WExport.BuildDisplayValue(wwiseEvent, (_, _) => "No Data");

        Assert.AreEqual($"#{wwiseEvent.UIndex} VO_788349_m_Play", label);
    }

    [TestMethod]
    public void WwiseGraphSelectionMapsHircNodesByIdInsteadOfGraphOrder()
    {
        uint[] soundPanelOrder = [0xAAAA0001, 0xBBBB0002, 0xCCCC0003];

        Assert.AreEqual(1, WwiseEditorWindow.FindHircListIndexById(soundPanelOrder, 0xBBBB0002));
        Assert.AreEqual(-1, WwiseEditorWindow.FindHircListIndexById(soundPanelOrder, 0xDDDD0004));
    }

    [TestMethod]
    public void WwiseAutoLayoutPlacesEventActionVoiceChainsOnStackedRows()
    {
        var firstEvent = new PNode { Bounds = new RectangleF(-20, 0, 140, 80) };
        var firstEventExport = new PNode { Bounds = new RectangleF(-60, 0, 220, 50) };
        var firstAction = new PNode { Bounds = new RectangleF(0, 0, 180, 70) };
        var firstVoice = new PNode { Bounds = new RectangleF(-30, 0, 200, 90) };
        var firstStream = new PNode { Bounds = new RectangleF(-90, 0, 320, 50) };
        var secondEvent = new PNode { Bounds = new RectangleF(0, 0, 160, 80) };
        var secondAction = new PNode { Bounds = new RectangleF(0, 0, 180, 70) };
        var secondVoice = new PNode { Bounds = new RectangleF(0, 0, 200, 90) };
        var rows = new[]
        {
            new[]
            {
                (0, (IReadOnlyList<PNode>)new[] { firstEvent, firstEventExport }),
                (1, (IReadOnlyList<PNode>)new[] { firstAction }),
                (2, (IReadOnlyList<PNode>)new[] { firstVoice, firstStream })
            },
            new[]
            {
                (0, (IReadOnlyList<PNode>)new[] { secondEvent }),
                (1, (IReadOnlyList<PNode>)new[] { secondAction }),
                (2, (IReadOnlyList<PNode>)new[] { secondVoice })
            }
        };

        WwiseEditorWindow.ArrangeNodeRows(rows, 80, 100, 20);

        Assert.AreEqual(firstEvent.GlobalFullBounds.Top, firstAction.GlobalFullBounds.Top);
        Assert.AreEqual(firstAction.GlobalFullBounds.Top, firstVoice.GlobalFullBounds.Top);
        Assert.IsGreaterThan(firstEvent.GlobalFullBounds.Right + 80, firstAction.GlobalFullBounds.Left - 0.01f);
        Assert.IsGreaterThan(firstAction.GlobalFullBounds.Right + 80, firstVoice.GlobalFullBounds.Left - 0.01f);
        Assert.IsGreaterThanOrEqualTo(firstEventExport.GlobalFullBounds.Top,
            firstEvent.GlobalFullBounds.Bottom + 20);
        Assert.IsGreaterThanOrEqualTo(firstStream.GlobalFullBounds.Top,
            firstVoice.GlobalFullBounds.Bottom + 20);
        Assert.IsGreaterThanOrEqualTo(secondEvent.GlobalFullBounds.Top,
            firstStream.GlobalFullBounds.Bottom + 100);
        Assert.AreEqual(secondEvent.GlobalFullBounds.Top, secondAction.GlobalFullBounds.Top);
        Assert.AreEqual(secondAction.GlobalFullBounds.Top, secondVoice.GlobalFullBounds.Top);

        PNode[] allNodes =
        [
            firstEvent, firstEventExport, firstAction, firstVoice, firstStream,
            secondEvent, secondAction, secondVoice
        ];
        for (int first = 0; first < allNodes.Length; first++)
        {
            for (int second = first + 1; second < allNodes.Length; second++)
            {
                AssertBoundsHaveSpacing(allNodes[first].GlobalFullBounds, allNodes[second].GlobalFullBounds, 20);
            }
        }
    }

    [TestMethod]
    public void WwiseAutoLayoutHandlesDenseBanksWithoutQuadraticCollisionPass()
    {
        const int nodeCount = 500;
        const float nodeHeight = 60;
        const float rowSpacing = 100;
        var nodes = Enumerable.Range(0, nodeCount)
            .Select(_ => new PNode { Bounds = new RectangleF(0, 0, 60, nodeHeight) })
            .ToList();
        var rows = nodes.Select(node => new[]
        {
            (0, (IReadOnlyList<PNode>)new[] { node })
        });

        WwiseEditorWindow.ArrangeNodeRows(rows, 80, rowSpacing, 20);

        Assert.AreEqual((nodeCount - 1) * (nodeHeight + rowSpacing), nodes[^1].GlobalFullBounds.Top);
    }

    [TestMethod]
    public void WwiseDragTargetsOwningNodeWhenChildVisualIsPicked()
    {
        var owner = new TestWwiseNode();
        var child = new PNode();
        var grandchild = new PNode();
        owner.AddChild(child);
        child.AddChild(grandchild);

        Assert.AreSame(owner, WwiseGraphEditor.NodeDragHandler.FindOwningNode(grandchild));
        Assert.IsNull(WwiseGraphEditor.NodeDragHandler.FindOwningNode(new PNode()));
    }

    [TestMethod]
    public void WwiseExportLabelOutsideCircleIsDraggable()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseEventHitTest.pcc", MEGame.LE3);
        ExportEntry wwiseEvent = package.CreateExport(
            "This_Is_A_Very_Long_Wwise_Event_Name_That_Extends_Beyond_The_Circle", "WwiseEvent", indexed: false);
        using var node = new WExport(wwiseEvent, 0, 0, null);
        RectangleF renderedBounds = node.GlobalFullBounds;
        var labelHit = new RectangleF(renderedBounds.Left + 1, WExport.RADIUS - 1, 2, 2);

        Assert.IsLessThan(0, renderedBounds.Left);
        Assert.IsTrue(node.Intersects(labelHit));
    }

    private static void AssertBoundsHaveSpacing(RectangleF first, RectangleF second, float spacing)
    {
        AssertBoundsHaveSpacing(first, second, spacing, spacing);
    }

    private static void AssertBoundsHaveSpacing(RectangleF first, RectangleF second,
        float horizontalSpacing, float verticalSpacing)
    {
        bool separated = first.Right + horizontalSpacing <= second.Left
                         || second.Right + horizontalSpacing <= first.Left
                         || first.Bottom + verticalSpacing <= second.Top
                         || second.Bottom + verticalSpacing <= first.Top;
        Assert.IsTrue(separated, $"Node bounds {first} and {second} are too close.");
    }

    private sealed class TestWwiseNode : WwiseHircObjNode
    {
        public TestWwiseNode() : base(null, null)
        {
        }
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
            active.CurveInterpolation == CurveInterpolation.Linear));
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
