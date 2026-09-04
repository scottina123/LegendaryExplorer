using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BinarySerialization;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.WwiseEditor;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using ME3Tweaks.Wwiser;
using ME3Tweaks.Wwiser.Formats;
using ME3Tweaks.Wwiser.Model.Action;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using ME3Tweaks.Wwiser.Model.ParameterNode.Positioning;
using ME3Tweaks.Wwiser.Model.RTPC;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Piccolo;
using WwiserAction = ME3Tweaks.Wwiser.Model.Hierarchy.Action;
using WwiserActorMixer = ME3Tweaks.Wwiser.Model.Hierarchy.ActorMixer;
using WwiserEvent = ME3Tweaks.Wwiser.Model.Hierarchy.Event;
using WwiserEmptyHircItem = ME3Tweaks.Wwiser.Model.Hierarchy.EmptyHircItem;
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
    private static readonly uint[] HackettQecEffectIds = [1827713496, 1487904704];
    private static readonly uint[] Le2RadioEffectIds = [0x85ABA16D, 0xF4D30DBA];

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void BankEffectScopesUseUniqueLeavesAndOverrideEveryBioWareParent()
    {
        const uint rootId = 0xF0000001;
        const uint firstBranchId = 0xF0000002;
        const uint secondBranchId = 0xF0000003;
        const uint nestedOverrideId = 0xF0000004;
        const uint firstSoundId = 0xF0000005;
        const uint secondSoundId = 0xF0000006;
        var root = new WwiserActorMixer { Id = rootId };
        var firstBranch = new WwiserActorMixer
        {
            Id = firstBranchId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = rootId }
        };
        var secondBranch = new WwiserActorMixer
        {
            Id = secondBranchId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = rootId }
        };
        var nestedOverride = new WwiserActorMixer
        {
            Id = nestedOverrideId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = firstBranchId }
        };
        nestedOverride.NodeBaseParameters.FxParams.IsOverrideParentFx = true;
        var firstSound = new WwiserSound
        {
            Id = firstSoundId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = nestedOverrideId }
        };
        var secondSound = new WwiserSound
        {
            Id = secondSoundId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = secondBranchId }
        };
        var nodes = new List<(uint Id, WwiserIHasNode Node)>
        {
            (rootId, root), (firstBranchId, firstBranch), (secondBranchId, secondBranch),
            (nestedOverrideId, nestedOverride), (firstSoundId, firstSound), (secondSoundId, secondSound)
        };

        var effectScopes = InvokePrivate<List<WwiserIHasNode>>("GetRuntimeOverrideNodes", nodes);

        CollectionAssert.AreEqual(new List<WwiserIHasNode> { firstSound, secondSound }, effectScopes);

        root.NodeBaseParameters.FxParams.FxChunks.AddRange(HackettQecEffectIds.Select((id, index) =>
            new FxChunk { FxIndex = checked((byte)index), Id = id, IsShareSet = true }));
        root.NodeBaseParameters.FxParams.NumFx = checked((byte)HackettQecEffectIds.Length);
        InvokePrivate("ApplyEffectPresetToScopes", effectScopes, WwiseEditorEffectPreset.Qec, MEGame.LE3,
            nodes.Select(item => item.Node).ToList());

        foreach (var parent in new WwiserIHasNode[] { root, firstBranch, secondBranch, nestedOverride })
        {
            Assert.IsEmpty(parent.NodeBaseParameters.FxParams.FxChunks);
        }
        foreach (var scope in effectScopes)
        {
            Assert.IsTrue(scope.NodeBaseParameters.FxParams.IsOverrideParentFx);
            CollectionAssert.AreEqual(HackettQecEffectIds,
                scope.NodeBaseParameters.FxParams.FxChunks.OrderBy(chunk => chunk.FxIndex)
                    .Select(chunk => chunk.Id).ToArray());
        }
    }

    [TestMethod]
    public void BankEffectScopesUseSoundLeavesForFlatImportedHierarchy()
    {
        const uint rootId = 0xF0000011;
        const uint firstSoundId = 0xF0000012;
        const uint secondSoundId = 0xF0000013;
        var root = new WwiserActorMixer { Id = rootId };
        var firstSound = new WwiserSound
        {
            Id = firstSoundId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = rootId }
        };
        var secondSound = new WwiserSound
        {
            Id = secondSoundId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = rootId }
        };
        var nodes = new List<(uint Id, WwiserIHasNode Node)>
        {
            (rootId, root), (firstSoundId, firstSound), (secondSoundId, secondSound)
        };

        var effectScopes = InvokePrivate<List<WwiserIHasNode>>("GetRuntimeOverrideNodes", nodes);

        CollectionAssert.AreEqual(new List<WwiserIHasNode> { firstSound, secondSound }, effectScopes);
    }

    [TestMethod]
    public void InheritedVanillaEffectsAreShownAndCanBeSuppressedPerEvent()
    {
        foreach ((MEGame game, WwiseEditorEffectPreset expectedPreset, uint[] effectIds) in new[]
                 {
                     (MEGame.LE3, WwiseEditorEffectPreset.BioWareRadio, BioWareRadioEffectIds),
                     (MEGame.LE2, WwiseEditorEffectPreset.Le2Radio, Le2RadioEffectIds)
                 })
        {
            const uint mixerId = 0xF0000040;
            const uint soundId = 0xF0000041;
            var mixer = new WwiserActorMixer { Id = mixerId };
            mixer.NodeBaseParameters.FxParams.FxChunks.AddRange(effectIds.Select((id, index) =>
                new FxChunk { FxIndex = checked((byte)index), Id = id, IsShareSet = true }));
            mixer.NodeBaseParameters.FxParams.NumFx = checked((byte)effectIds.Length);
            mixer.NodeBaseParameters.FxParams.IsOverrideParentFx = true;
            var sound = new WwiserSound
            {
                Id = soundId,
                NodeBaseParameters = new NodeBaseParameters { DirectParentId = mixerId }
            };
            var nodes = new List<(uint Id, WwiserIHasNode Node)>
            {
                (mixerId, mixer), (soundId, sound)
            };
            var targets = new List<WwiserIHasNode> { sound };

            Assert.AreEqual(expectedPreset, InvokePrivate<WwiseEditorEffectPreset>(
                "GetCurrentEffectPreset", targets, nodes, game, false));
            string summary = InvokePrivate<string>("GetCurrentEffectSummary", targets, nodes, game);
            StringAssert.Contains(summary, "inherited");
            foreach (uint effectId in effectIds)
            {
                StringAssert.Contains(summary, $"0x{effectId:X8}");
            }

            InvokePrivate("ApplyEffectPresetToScopes", targets, WwiseEditorEffectPreset.None,
                game, null);
            Assert.IsTrue(sound.NodeBaseParameters.FxParams.IsOverrideParentFx);
            Assert.IsEmpty(sound.NodeBaseParameters.FxParams.FxChunks);
            CollectionAssert.AreEqual(effectIds,
                mixer.NodeBaseParameters.FxParams.FxChunks.Select(chunk => chunk.Id).ToArray());
            Assert.AreEqual(WwiseEditorEffectPreset.None, InvokePrivate<WwiseEditorEffectPreset>(
                "GetCurrentEffectPreset", targets, nodes, game, false));

            InvokePrivate("ApplyEffectPresetToScopes", targets, WwiseEditorEffectPreset.Inherit,
                game, null);
            Assert.IsFalse(sound.NodeBaseParameters.FxParams.IsOverrideParentFx);
            Assert.AreEqual(expectedPreset, InvokePrivate<WwiseEditorEffectPreset>(
                "GetCurrentEffectPreset", targets, nodes, game, false));
        }
    }

    [TestMethod]
    public void RemovingVanillaBankEffectsClearsEveryNodeAndKeepsLeafOverridesEmpty()
    {
        const uint mixerId = 0xF0000050;
        const uint soundId = 0xF0000051;
        const uint customEffectId = 0x12345678;
        var mixer = new WwiserActorMixer { Id = mixerId };
        mixer.NodeBaseParameters.FxParams.FxChunks.Add(new FxChunk
        {
            FxIndex = 0,
            Id = customEffectId,
            IsShareSet = true
        });
        mixer.NodeBaseParameters.FxParams.NumFx = 1;
        mixer.NodeBaseParameters.FxParams.IsOverrideParentFx = true;
        var sound = new WwiserSound
        {
            Id = soundId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = mixerId }
        };
        var nodes = new List<(uint Id, WwiserIHasNode Node)>
        {
            (mixerId, mixer), (soundId, sound)
        };
        var leafScopes = new List<WwiserIHasNode> { sound };

        string summary = InvokePrivate<string>("GetCurrentEffectSummary", leafScopes, nodes, MEGame.LE3);
        StringAssert.Contains(summary, $"0x{customEffectId:X8}");
        InvokePrivate("ApplyEffectPresetToScopes", leafScopes, WwiseEditorEffectPreset.None,
            MEGame.LE3, nodes.Select(item => item.Node).ToList());

        Assert.IsEmpty(mixer.NodeBaseParameters.FxParams.FxChunks);
        Assert.IsFalse(mixer.NodeBaseParameters.FxParams.IsOverrideParentFx);
        Assert.IsEmpty(sound.NodeBaseParameters.FxParams.FxChunks);
        Assert.IsTrue(sound.NodeBaseParameters.FxParams.IsOverrideParentFx);
        Assert.AreEqual(WwiseEditorEffectPreset.None, InvokePrivate<WwiseEditorEffectPreset>(
            "GetCurrentEffectPreset", leafScopes, nodes, MEGame.LE3, true));
    }

    [TestMethod]
    public void EmptyEventEffectOverrideRoundTripsWithoutRemovingParentEffect()
    {
        var bank = LoadTestBank();
        var parameterNodes = GetParameterNodes(bank);
        var sound = bank.HIRC.Items.Select(item => item.Item).OfType<WwiserSound>()
            .First(item => item.NodeBaseParameters.DirectParentId != 0);
        uint soundId = sound.Id;
        uint parentId = sound.NodeBaseParameters.DirectParentId;
        var parent = parameterNodes.Single(item => item.Id == parentId).Node;
        parent.NodeBaseParameters.FxParams.FxChunks.Clear();
        parent.NodeBaseParameters.FxParams.FxChunks.AddRange(BioWareRadioEffectIds.Select((id, index) =>
            new FxChunk { FxIndex = checked((byte)index), Id = id, IsShareSet = true }));
        parent.NodeBaseParameters.FxParams.NumFx = checked((byte)BioWareRadioEffectIds.Length);
        parent.NodeBaseParameters.FxParams.IsOverrideParentFx = true;
        sound.NodeBaseParameters.FxParams.FxChunks.Clear();
        sound.NodeBaseParameters.FxParams.NumFx = 0;
        sound.NodeBaseParameters.FxParams.IsOverrideParentFx = false;

        InvokePrivate("ApplyEffectPresetToScopes", new List<WwiserIHasNode> { sound },
            WwiseEditorEffectPreset.None, MEGame.LE3, null);

        var reparsed = RoundTrip(bank);
        var reparsedNodes = GetParameterNodes(reparsed).ToDictionary(item => item.Id, item => item.Node);
        var reparsedSound = reparsedNodes[soundId];
        var reparsedParent = reparsedNodes[parentId];
        Assert.IsTrue(reparsedSound.NodeBaseParameters.FxParams.IsOverrideParentFx);
        Assert.IsEmpty(reparsedSound.NodeBaseParameters.FxParams.FxChunks);
        Assert.IsTrue(reparsedParent.NodeBaseParameters.FxParams.IsOverrideParentFx);
        CollectionAssert.AreEqual(BioWareRadioEffectIds,
            reparsedParent.NodeBaseParameters.FxParams.FxChunks.OrderBy(chunk => chunk.FxIndex)
                .Select(chunk => chunk.Id).ToArray());
    }

    [TestMethod]
    public void BioWareRadioEffectAppliesToEveryInheritanceScopeAndPreservesOtherEffects()
    {
        var bank = LoadTestBank();
        var nodes = GetParameterNodes(bank);
        Assert.IsGreaterThanOrEqualTo(2, nodes.Count);

        var scopes = new List<WwiserIHasNode> { nodes[0].Node, nodes[1].Node };
        scopes[0].NodeBaseParameters.DirectParentId = 0;
        scopes[1].NodeBaseParameters.DirectParentId = ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)scopes[0]).Id;
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
        Assert.IsTrue(scopes[0].NodeBaseParameters.FxParams.IsOverrideParentFx);
        Assert.IsTrue(scopes[1].NodeBaseParameters.FxParams.IsOverrideParentFx);

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
    public void RightColumnEventLookupUsesClickedHircIdAndCurrentBank()
    {
        const uint eventId = 0xF00000A1;
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("RightColumnEventTest.pcc", MEGame.LE3);
        ExportEntry parent = package.CreatePackageExport("Audio", forcedExport: false);
        ExportEntry firstBank = package.CreateExport("FirstBank", "WwiseBank", parent, indexed: false);
        ExportEntry secondBank = package.CreateExport("SecondBank", "WwiseBank", parent, indexed: false);
        ExportEntry firstEvent = package.CreateExport("FirstEvent", "WwiseEvent", parent, indexed: false);
        ExportEntry secondEvent = package.CreateExport("SecondEvent", "WwiseEvent", parent, indexed: false);
        foreach ((ExportEntry eventExport, ExportEntry bankExport) in new[]
                 {
                     (firstEvent, firstBank), (secondEvent, secondBank)
                 })
        {
            eventExport.WriteProperty(new IntProperty(unchecked((int)eventId), "Id"));
            eventExport.WriteProperty(new StructProperty("WwiseRelationships", false,
                new ObjectProperty(bankExport, "Bank")) { Name = "Relationships" });
        }

        ExportEntry resolved = InvokePrivate<ExportEntry>("FindEventExportForBank",
            package.Exports, eventId, secondBank);

        Assert.AreSame(secondEvent, resolved);
    }

    [TestMethod]
    public void EventLeafScopesRoundTripEveryEditableAudioSetting()
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

        var targets = InvokePrivate<List<WwiserIHasNode>>("GetEventTargetAudioNodes", bank, eventId,
            GetParameterNodes(bank));

        CollectionAssert.Contains(targets, sound);
        Assert.IsTrue(targets.All(target => target is WwiserSound));
        var targetSounds = targets.Cast<WwiserSound>().ToList();
        var effectScopes = InvokePrivate<List<WwiserIHasNode>>("GetEventEffectScopeNodes", targets);
        CollectionAssert.AreEquivalent(targets.ToList(), effectScopes);

        const float volume = -7.5f;
        const uint outputBusId = 0x12345678;
        foreach (var target in targets)
        {
            InvokePrivate("SetInitialParameter", target.NodeBaseParameters.InitialParams62,
                PropId.Volume, volume, true);
            target.NodeBaseParameters.OverrideBusId = outputBusId;
        }
        foreach (var targetSound in targetSounds)
        {
            InvokePrivate("SetLoopAudio", targetSound, true);
        }

        object hackettQec = GetPreset("HackettQec");
        Assert.IsTrue(InvokePresetMethod("EnsureEffectData", bank, hackettQec));
        InvokePrivate("SetEffectPresetOnScopes", effectScopes, hackettQec);
        Assert.IsTrue(WwiseBankEffectPresets.EnsureMusicDuckingData(bank));
        WwiseBankEffectPresets.SetMusicDuckingOnScopes(targets, true);

        var attenuationIds = new Dictionary<uint, uint>();
        foreach (var target in targets)
        {
            uint targetId = ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)target).Id;
            Assert.IsTrue(WwiseBankEffectPresets.EnsureStandardAttenuationDataForScope(
                bank, MEGame.LE3, 1.25f, targetId, out uint attenuationId));
            attenuationIds[targetId] = attenuationId;
            WwiseBankEffectPresets.SetStandardAttenuationOnScopes([target], attenuationId, true);
        }
        InvokePrivate("EnsureStopEventInBank", bank, StopAllEventId, "Stop", targetSounds);

        var reparsed = RoundTrip(bank);
        var reparsedTargets = reparsed.HIRC.Items.Select(item => item.Item).OfType<WwiserIHasNode>()
            .Where(target => attenuationIds.ContainsKey(
                ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)target).Id))
            .ToList();
        Assert.HasCount(targets.Count, reparsedTargets);
        foreach (var target in reparsedTargets)
        {
            uint targetId = ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)target).Id;
            Assert.AreEqual(volume, InvokePrivate<float>("GetNodeVolume", target));
            Assert.AreEqual(outputBusId, target.NodeBaseParameters.OverrideBusId);
            Assert.IsTrue(target.NodeBaseParameters.FxParams.IsOverrideParentFx);
            CollectionAssert.AreEqual(HackettQecEffectIds,
                target.NodeBaseParameters.FxParams.FxChunks.OrderBy(chunk => chunk.FxIndex)
                    .Select(chunk => chunk.Id).ToArray());
            Assert.IsTrue(WwiseBankEffectPresets.HasMusicDuckingOnAllScopes([target]));
            Assert.IsTrue(WwiseBankEffectPresets.HasStandardAttenuationOnAllScopes(
                [target], attenuationIds[targetId]));
            Assert.IsTrue(InvokePrivate<bool>("IsLooping", (WwiserSound)target));
        }
        AssertStopEventCoversSounds(reparsed, targetSounds);
    }

    [TestMethod]
    public void Le2BankLeafScopesRoundTripEveryEditableAudioSetting()
    {
        var bank = LoadTestBank();
        var parameterNodes = GetParameterNodes(bank);
        var scopes = InvokePrivate<List<WwiserIHasNode>>("GetRuntimeOverrideNodes", parameterNodes);
        var sounds = bank.HIRC.Items.Select(item => item.Item).OfType<WwiserSound>().ToList();
        var parentIds = parameterNodes.Select(item => item.Node.NodeBaseParameters.DirectParentId).ToHashSet();

        Assert.IsNotEmpty(scopes);
        Assert.IsTrue(scopes.All(scope => scope is WwiserSound ||
            !parentIds.Contains(((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)scope).Id)));

        const float volume = -9.5f;
        const uint outputBusId = 0x23456789;
        foreach (var scope in scopes)
        {
            InvokePrivate("SetInitialParameter", scope.NodeBaseParameters.InitialParams62,
                PropId.Volume, volume, true);
            scope.NodeBaseParameters.OverrideBusId = outputBusId;
        }
        foreach (var sound in sounds)
        {
            InvokePrivate("SetLoopAudio", sound, true);
        }

        object le2Radio = GetPreset("Le2Radio");
        Assert.IsTrue(InvokePresetMethod("EnsureEffectData", bank, le2Radio));
        InvokePrivate("ApplyEffectPresetToScopes", scopes, WwiseEditorEffectPreset.Le2Radio,
            MEGame.LE2, parameterNodes.Select(item => item.Node).ToList());

        var scopeIds = scopes.Select(scope =>
            ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)scope).Id).ToList();
        Assert.IsTrue(WwiseBankEffectPresets.SetLe2MusicDuckingOnTargets(bank, scopeIds, true));
        Assert.IsTrue(WwiseBankEffectPresets.EnsureStandardAttenuationData(
            bank, MEGame.LE2, 1.25f, out uint attenuationId));
        WwiseBankEffectPresets.SetStandardAttenuationOnScopes(scopes, attenuationId, true,
            enableDiffraction: true);
        InvokePrivate("EnsureStopEventInBank", bank, StopAllEventId, "Stop", sounds);

        var reparsed = RoundTrip(bank);
        var reparsedScopes = reparsed.HIRC.Items.Select(item => item.Item).OfType<WwiserIHasNode>()
            .Where(scope => scopeIds.Contains(
                ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)scope).Id))
            .ToList();
        Assert.HasCount(scopes.Count, reparsedScopes);
        foreach (var scope in reparsedScopes)
        {
            Assert.AreEqual(volume, InvokePrivate<float>("GetNodeVolume", scope));
            Assert.AreEqual(outputBusId, scope.NodeBaseParameters.OverrideBusId);
            Assert.IsTrue(scope.NodeBaseParameters.FxParams.IsOverrideParentFx);
            CollectionAssert.AreEqual(Le2RadioEffectIds,
                scope.NodeBaseParameters.FxParams.FxChunks.OrderBy(chunk => chunk.FxIndex)
                    .Select(chunk => chunk.Id).ToArray());
            Assert.IsTrue(WwiseBankEffectPresets.HasStandardAttenuationOnAllScopes(
                [scope], attenuationId));
            Assert.IsTrue(scope.NodeBaseParameters.PositioningChunk.Flags
                .HasFlag(PositioningChunk.PositioningFlags.PositioningInfoOverrideParent));
            Assert.IsTrue(scope.NodeBaseParameters.PositioningChunk.Mode
                .HasFlag(SpatializationMode.EnableDiffraction));
            if (scope is WwiserSound sound)
            {
                Assert.IsTrue(InvokePrivate<bool>("IsLooping", sound));
            }
        }

        Assert.IsTrue(WwiseBankEffectPresets.HasLe2MusicDuckingOnAllTargets(reparsed, scopeIds));
        AssertStopEventCoversSounds(reparsed, sounds);
    }

    [TestMethod]
    public void EventEffectScopesUseUniqueSoundLeafToOverrideBioWareHierarchy()
    {
        const uint rootId = 0xF0000020;
        const uint branchId = 0xF0000021;
        const uint groupId = 0xF0000022;
        const uint soundId = 0xF0000023;
        var root = new WwiserActorMixer { Id = rootId };
        var branch = new WwiserActorMixer
        {
            Id = branchId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = rootId }
        };
        var group = new WwiserActorMixer
        {
            Id = groupId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = branchId }
        };
        var sound = new WwiserSound
        {
            Id = soundId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = groupId }
        };
        var nodes = new List<(uint Id, WwiserIHasNode Node)>
        {
            (rootId, root), (branchId, branch), (groupId, group), (soundId, sound)
        };

        var scopes = InvokePrivate<List<WwiserIHasNode>>("GetEventEffectScopeNodes",
            new List<WwiserIHasNode> { sound });

        CollectionAssert.AreEqual(new List<WwiserIHasNode> { sound }, scopes);
        InvokePrivate("SetEffectPresetOnScopes", scopes, GetPreset("HackettQec"));
        Assert.IsTrue(sound.NodeBaseParameters.FxParams.IsOverrideParentFx);
        CollectionAssert.AreEqual(HackettQecEffectIds,
            sound.NodeBaseParameters.FxParams.FxChunks.OrderBy(chunk => chunk.FxIndex)
                .Select(chunk => chunk.Id).ToArray());
        Assert.IsTrue(new WwiserIHasNode[] { root, branch, group }
            .All(parent => parent.NodeBaseParameters.FxParams.FxChunks.Count == 0));
    }

    [TestMethod]
    public void EventEffectScopesKeepSoundForFlatImportedHierarchy()
    {
        const uint rootId = 0xF0000030;
        const uint soundId = 0xF0000031;
        var root = new WwiserActorMixer { Id = rootId };
        var sound = new WwiserSound
        {
            Id = soundId,
            NodeBaseParameters = new NodeBaseParameters { DirectParentId = rootId }
        };
        var nodes = new List<(uint Id, WwiserIHasNode Node)>
        {
            (rootId, root), (soundId, sound)
        };

        var scopes = InvokePrivate<List<WwiserIHasNode>>("GetEventEffectScopeNodes",
            new List<WwiserIHasNode> { sound });

        CollectionAssert.AreEqual(new List<WwiserIHasNode> { sound }, scopes);
    }

    [TestMethod]
    public void EventSettingsApplyHackettQecToOpaqueMusicHierarchy()
    {
        var bank = LoadTestBank();
        const uint playlistId = 0xF0000010;
        const uint segmentId = 0xF0000011;
        const uint trackId = 0xF0000012;
        const uint actionId = 0xF0000013;
        const uint eventId = 0xF0000014;
        var playlist = CreateOpaqueMusicNode(playlistId, 0);
        var segment = CreateOpaqueMusicNode(segmentId, playlistId);
        var track = CreateOpaqueMusicTrackNode(trackId, segmentId);
        var playAction = new WwiserAction
        {
            Id = actionId,
            Type = new ActionType { Value = ActionTypeValue.Play },
            TargetId = playlistId,
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
            Type = new HircSmartType { Value = HircType.MusicRandomSequence },
            Item = playlist
        });
        bank.HIRC.Items.Add(new WwiserHircItemContainer
        {
            Type = new HircSmartType { Value = HircType.MusicSegment },
            Item = segment
        });
        bank.HIRC.Items.Add(new WwiserHircItemContainer
        {
            Type = new HircSmartType { Value = HircType.MusicTrack },
            Item = track
        });
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

        var parameterNodes = InvokePrivate<List<(uint Id, WwiserIHasNode Node)>>(
            "GetEditableParameterNodes", bank);
        var targets = InvokePrivate<List<WwiserIHasNode>>("GetEventTargetAudioNodes", bank, eventId,
            parameterNodes);
        var effectScopes = InvokePrivate<List<WwiserIHasNode>>("GetEventEffectScopeNodes", targets);

        Assert.HasCount(1, targets);
        Assert.AreEqual(trackId,
            ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)targets[0]).Id);
        Assert.HasCount(1, effectScopes);
        Assert.AreEqual(trackId,
            ((ME3Tweaks.Wwiser.Model.Hierarchy.HircItem)effectScopes[0]).Id);
        targets[0].NodeBaseParameters.OverrideBusId = 0x12345678;
        InvokePrivate("SetInitialParameter", targets[0].NodeBaseParameters.InitialParams62,
            PropId.Volume, -6f, true);
        object hackettQec = GetPreset("HackettQec");
        Assert.IsTrue(InvokePresetMethod("EnsureEffectData", bank, hackettQec));
        InvokePrivate("SetEffectPresetOnScopes", effectScopes, hackettQec);
        InvokePrivate("CommitOpaqueMusicNodes", parameterNodes, bank.BKHD.BankGeneratorVersion);

        using var nodeData = new MemoryStream(track.Data, false);
        nodeData.Position = 13;
        var committedNode = new BinarySerializer().Deserialize<NodeBaseParameters>(nodeData,
            new BankSerializationContext(bank.BKHD.BankGeneratorVersion));
        Assert.AreEqual(0x12345678u, committedNode.OverrideBusId);
        int volumeIndex = committedNode.InitialParams62.ParameterIds.FindIndex(
            parameter => parameter.PropValue == PropId.Volume);
        Assert.IsGreaterThanOrEqualTo(volumeIndex, 0);
        Assert.AreEqual(-6f, committedNode.InitialParams62.ParameterValues[volumeIndex].Float);
        Assert.IsTrue(committedNode.FxParams.IsOverrideParentFx);
        CollectionAssert.AreEqual(HackettQecEffectIds,
            committedNode.FxParams.FxChunks.OrderBy(chunk => chunk.FxIndex)
                .Select(chunk => chunk.Id).ToArray());
        CollectionAssert.AreEqual(new byte[] { 0xAB, 0xCD }, track.Data[^2..]);

        using var effectData = new MemoryStream(segment.Data, false);
        effectData.Position = 1;
        var committedEffectNode = new BinarySerializer().Deserialize<NodeBaseParameters>(effectData,
            new BankSerializationContext(bank.BKHD.BankGeneratorVersion));
        Assert.IsEmpty(committedEffectNode.FxParams.FxChunks);
        CollectionAssert.AreEqual(new byte[] { 0xAB, 0xCD }, segment.Data[^2..]);

        var reparsed = RoundTrip(bank);
        Assert.IsInstanceOfType<WwiserEmptyHircItem>(reparsed.HIRC.Items
            .Single(item => item.Item.Id == trackId).Item);
        Assert.IsTrue(HackettQecEffectIds.All(id => reparsed.HIRC.Items.Any(item =>
            item.Type.Value == HircType.FxShareSet && item.Item.Id == id)));
        var reparsedNodes = InvokePrivate<List<(uint Id, WwiserIHasNode Node)>>(
            "GetEditableParameterNodes", reparsed);
        var reparsedTrack = reparsedNodes
            .Single(item => item.Id == trackId).Node;
        Assert.AreEqual(0x12345678u, reparsedTrack.NodeBaseParameters.OverrideBusId);
        CollectionAssert.AreEqual(HackettQecEffectIds,
            reparsedTrack.NodeBaseParameters.FxParams.FxChunks.OrderBy(chunk => chunk.FxIndex)
                .Select(chunk => chunk.Id).ToArray());
        var reparsedSegment = reparsedNodes
            .Single(item => item.Id == segmentId).Node;
        Assert.IsEmpty(reparsedSegment.NodeBaseParameters.FxParams.FxChunks);
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
    public void HircEventPreviewIncludesEveryMatchingWwiseEventAndTlkText()
    {
        const uint eventId = 0x18F06680;
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("WwiseEventPreviewTest.pcc", MEGame.LE3);
        ExportEntry femaleEvent = package.CreateExport("VO_17251537_f_Play", "WwiseEvent", indexed: false);
        femaleEvent.WriteProperty(new IntProperty(unchecked((int)eventId), "Id"));
        ExportEntry maleEvent = package.CreateExport("VO_17251537_m_Play", "WwiseEvent", indexed: false);
        maleEvent.WriteProperty(new IntProperty(unchecked((int)eventId), "Id"));

        IReadOnlyDictionary<uint, string> previews = WwiseEditorWindow.BuildHircEventPreviews(
            package.Exports, (tlkId, lookupPackage) =>
            {
                Assert.AreEqual(17251537, tlkId);
                Assert.AreSame(package, lookupPackage);
                return "Hold up.";
            });

        string preview = previews[eventId];
        StringAssert.Contains(preview, $"#{femaleEvent.UIndex} VO_17251537_f_Play\nTLK 17251537: Hold up.");
        StringAssert.Contains(preview, $"#{maleEvent.UIndex} VO_17251537_m_Play\nTLK 17251537: Hold up.");
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

    private static WwiserEmptyHircItem CreateOpaqueMusicNode(uint id, uint parentId)
    {
        var node = new NodeBaseParameters { DirectParentId = parentId };
        using var nodeData = new MemoryStream();
        new BinarySerializer().Serialize(nodeData, node, new BankSerializationContext(134));
        return new WwiserEmptyHircItem
        {
            Id = id,
            Data = new byte[] { 0 }.Concat(nodeData.ToArray()).Concat(new byte[] { 0xAB, 0xCD }).ToArray()
        };
    }

    private static WwiserEmptyHircItem CreateOpaqueMusicTrackNode(uint id, uint parentId)
    {
        var node = new NodeBaseParameters { DirectParentId = parentId };
        using var musicData = new MemoryStream();
        using (var writer = new BinaryWriter(musicData, System.Text.Encoding.UTF8, true))
        {
            writer.Write((byte)0); // MIDI behavior
            writer.Write(0u); // source count
            writer.Write(0u); // time parameter count
            writer.Write(0u); // curve count
        }
        new BinarySerializer().Serialize(musicData, node, new BankSerializationContext(134));
        musicData.Write([0xAB, 0xCD]);
        return new WwiserEmptyHircItem { Id = id, Data = musicData.ToArray() };
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
