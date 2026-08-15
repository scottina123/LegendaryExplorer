using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using BinarySerialization;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Audio;
using LegendaryExplorerCore.Packages;
using ME3Tweaks.Wwiser;
using HircAction = ME3Tweaks.Wwiser.Model.Hierarchy.Action;
using HircEvent = ME3Tweaks.Wwiser.Model.Hierarchy.Event;
using ME3Tweaks.Wwiser.Model.Hierarchy;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using ME3Tweaks.Wwiser.Model.ParameterNode.Positioning;
using ME3Tweaks.Wwiser.Model.RTPC;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class BulkAudioImportDialogTests
{
    private const string EventsWorkUnitId = "{11111111-1111-1111-1111-111111111111}";
    private const string ActorMixerWorkUnitId = "{22222222-2222-2222-2222-222222222222}";
    private const uint QecFutzBoxEffectId = 1827713496;
    private const uint QecFlangerEffectId = 1487904704;
    private const uint BioWareRadioFutzBoxEffectId = 125287176;
    private const uint BioWareRadioEqEffectId = 1177780410;
    private const uint FactoryRadioEffectId = 2952825346;
    private const uint HelmetFilterEffectId = BioWareRadioFutzBoxEffectId;
    private const uint HelmetRtpcId = 0xAA2B753F;
    private const uint Le2HelmetEqEffectId = 0xB6871666;
    private const uint Le2HelmetFilterEffectId = 0x8371E703;
    private const uint Le2RadioEqEffectId = 0x85ABA16D;
    private const uint Le2RadioFilterEffectId = 0xF4D30DBA;
    private const uint Le2HologramEqEffectId = 0x5A281C31;
    private const uint Le2HologramFilterEffectId = 0x7128A668;
    private const uint Le2HelmetRtpcId = 0x9D4305AE;
    private const uint MusicDuckingStateGroupId = 0x7BC046C4;
    private const uint MusicDuckingStateId = 0x61030AE6;
    private const uint MusicDuckingStateInstanceId = 0x25716DBE;
    private const uint Le2MusicDuckActionId = 0x015FDFA5;
    private const uint Le2MusicDuckEventId = 0x16BCA20A;
    private const uint Le2MusicResetActionId = 0x1423EC41;
    private const uint Le2MusicResetEventId = 0xCD2B1173;
    private const uint StandardAttenuationSourceId = 0x13ED5249;
    private const string MusicDuckingStateHirc = "AQwAAAC+bXElAQAAAAAAQMA=";
    private const string Le2MusicDuckActionHirc =
        "AyEAAACl318BAgoF9RYNAAEQoA8AAAAEAgAAQMEAAAAAAAAAAAA=";
    private const string Le2MusicDuckEventHirc = "BAkAAAAKorwWAaXfXwE=";
    private const string Le2MusicResetActionHirc =
        "AyEAAABB7CMUAgsF9RYNAAEQ6AMAAAAEAQAAAAAAAAAAAAAAAAA=";
    private const string Le2MusicResetEventHirc = "BAkAAABzESvNAUHsIxQ=";
    private const string StandardAttenuationHirc =
        "DpsAAABJUu0TAQAAtEIAAHVDAABAwAAA8EEAAAAAAAEC//8D/wQCAgAAAAAAAAAAAAAAAAAAAIxCkud3vwQAAAACAgAAAAAAIYiVvgQAAAAAAIxCY2T/vgQAAAACAgAAAAAAIYiVvgQAAAAAAIxCY2T/vgQAAAAAAwAAAAAAAADIQQQAAAAAAOBAAAAAAAkAAAAAAIxCAAAAAAQAAAAAAA==";
    private const string Le2StandardAttenuationHirc =
        "DqIAAABJUu0TAAABAgP/BP8FAgIAAAAAAAAAAAAAAAAAAACMQv/+f78EAAAAAgIAAAAAACnzvL4EAAAAAACMQv/+f78EAAAAAgIAAAAAACnzvL4EAAAAAACMQv/+f78EAAAAAAIAAAAAAAAAAAABAAAAAACMQgAAcEIEAAAAAAMAAAAAAAAAyEEEAAAAAABSQQAAAAAJAAAAAACMQgAAAAAEAAAAAAA=";
    private const string Le2HelmetEqHirc =
        "EEsAAABmFoe2AwBpADgAAAABAAAAAADAwQCACUQAAAA/AQYAAAAAAMDBAMBiRAAAAD8AAAAAAAAAAAAAQJxFAACAPwEAAIBAAQAAAAAAAAA=";
    private const string Le2HelmetFilterHirc =
        "ECkAAAAD53GDAwBsABYAAAAAAEjCAAAAQArXIzzNzEw9AACAQQEBAAAAAAAAAA==";
    private const string Le2RadioEqHirc =
        "EEsAAABtoauFAwBpADgAAAABAAAAAAAAAAAAL0QAAIA/AQIAAAAAAAAAAACWRAAAQEABAAAAAAAAAAAAcNBFAACAPwAAAEBBAQAAAAAAAAA=";
    private const string Le2RadioFilterHirc =
        "ECkAAAC6DdP0AwBuABYAAAAAABDCAAAgQW8SgzqPwvU8AADAQQEBAAAAAAAAAA==";
    private const string Le2HologramEqHirc =
        "EEsAAAAxHChaAwBpADgAAAABAAAAAACwwQAAyEIAAIA/AAMAAAAAAAC/AAD6RAAAAEAABQAAAAAAwEAAAHpFAACAPwEAAAAAAQAAAAAAAAA=";
    private const string Le2HologramFilterHirc =
        "ECkAAABopihxAwBsABYAAAAzM9fBAABAQG8SgzpvEoM6AADAQAEBAAAAAAAAAA==";
    private const string BioWareRadioFutzBoxHirc =
        "EJ4AAAAIu3cHAxBuAIsAAAAAAAAAAADgEkYAAAAAAQAAAAAAlkMAAAAAAQQAAAAAAPBBAAAAAAAAAAAAAQAAAAAAekQAAAAAAAAAAAAAAPjBAGAuRQAAekQAAAAAAAAgwgAAIEEBEgAAAAAAyEIAAACgwQAAoMLNzMw9AAAgQQAAIEEACAAAAAQAAAAAAAAAAAAAAAAAoEAAAMhCAAAAAAAAAA==";
    private const string BioWareRadioEqHirc =
        "EL0AAAC6gDNGAwBpADgAAAAEAAAAAAAAAACA/EMAAIA/AQYAAAAAAAAAAMDNRAAAQEAABQAAAAAAwMEAQJxGAACAPwEAAAAAAQADAKhWFSkAAQE2GTELAgIAAAAAAGNk/74EAAAAAEAcRvXYb78EAAAAqFYVKQABDNVnOioAAgAAAAAAAGBqRgAAAAAAQBxGAIC7RAQAAACoVhUpAAECWsrmPgACAAAAAAAAAKBBBAAAAABAHEYAAPpEBAAAAAAAAAA=";

    [ClassInitialize]
    public static void Initialize(TestContext _) => LegendaryExplorerCoreLib.InitLib(TaskScheduler.Default);

    [TestMethod]
    public void ConversationOutputBusDefaultsToHelmetEffectForLe2AndLe3()
    {
        var method = typeof(BulkAudioImportDialog).GetMethod(
            "DefaultsToHelmetEffect", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE3, "Env-VO-Conversation"]));
        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE2, "Conversation"]));
        Assert.IsFalse((bool)method.Invoke(null, [MEGame.LE2, "Env-VO-Conversation"]));
        Assert.IsFalse((bool)method.Invoke(null, [MEGame.LE2, "Master Audio Bus"]));
        Assert.IsFalse((bool)method.Invoke(null, [MEGame.LE3, "Master Audio Bus"]));
    }

    [TestMethod]
    public void MusicDuckingIsAvailableForLe2AndLe3MusicBuses()
    {
        var method = typeof(BulkAudioImportDialog).GetMethod(
            "SupportsMusicDucking", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE3, "Env-Music"]));
        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE3, "NonSlowdown-Music"]));
        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE3, "Mus-1-Moderate Ducking"]));
        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE2, "Music"]));
        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE2, "Music-Diegetic"]));
        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE2, "UnDucked Music"]));
        Assert.IsFalse((bool)method.Invoke(null, [MEGame.LE2, "Conversation"]));
        Assert.IsFalse((bool)method.Invoke(null, [MEGame.LE3, "Env-VO-Conversation"]));
    }

    [TestMethod]
    public void StandardAttenuationIsAvailableForLe2AndLe3()
    {
        var method = typeof(BulkAudioImportDialog).GetMethod(
            "SupportsStandardAttenuation", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE3]));
        Assert.IsTrue((bool)method.Invoke(null, [MEGame.LE2]));
        Assert.IsFalse((bool)method.Invoke(null, [MEGame.LE1]));
    }

    [TestMethod]
    public void AppliesExactHackettQecEffectChainToLe3Bank()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_QEC_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyQecEffectToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath]);
            method.Invoke(null, [testBankPath]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            var qecShareSets = bank.HIRC.Items
                .Select(item => item.Item)
                .OfType<FxShareSet>()
                .Where(item => item.Id is QecFutzBoxEffectId or QecFlangerEffectId)
                .ToList();
            Assert.AreEqual(2, qecShareSets.Count);
            Assert.AreEqual(0x006E1003u, qecShareSets.Single(item => item.Id == QecFutzBoxEffectId).Plugin.PluginId);
            Assert.AreEqual(0x007D0003u, qecShareSets.Single(item => item.Id == QecFlangerEffectId).Plugin.PluginId);

            var actorMixers = bank.HIRC.Items
                .Select(item => item.Item)
                .OfType<ActorMixer>()
                .ToList();
            var actorMixerIds = actorMixers.Select(item => item.Id).ToHashSet();
            var rootActorMixers = actorMixers
                .Where(item => item.NodeBaseParameters.DirectParentId == 0 ||
                               !actorMixerIds.Contains(item.NodeBaseParameters.DirectParentId))
                .ToList();
            Assert.IsNotEmpty(rootActorMixers);

            foreach (var rootActorMixer in rootActorMixers)
            {
                CollectionAssert.AreEqual(
                    new[] { QecFutzBoxEffectId, QecFlangerEffectId },
                    rootActorMixer.NodeBaseParameters.FxParams.FxChunks.Select(item => item.Id).ToArray());
                Assert.IsTrue(rootActorMixer.NodeBaseParameters.FxParams.IsOverrideParentFx);
            }
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesExactBioWareRadioEffectToLe3Bank()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_Radio_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyBioWareRadioEffectToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, MEGame.LE3]);
            method.Invoke(null, [testBankPath, MEGame.LE3]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            var radioHircItems = bank.HIRC.Items
                .Where(item => item.Item.Id is BioWareRadioFutzBoxEffectId or BioWareRadioEqEffectId)
                .ToList();
            Assert.HasCount(2, radioHircItems);
            AssertExactShareSet(radioHircItems.Single(item => item.Item.Id == BioWareRadioFutzBoxEffectId),
                0x006E1003u, BioWareRadioFutzBoxHirc, bank);
            AssertExactShareSet(radioHircItems.Single(item => item.Item.Id == BioWareRadioEqEffectId),
                0x00690003u, BioWareRadioEqHirc, bank);
            Assert.IsFalse(bank.HIRC.Items.Any(item => item.Item.Id == FactoryRadioEffectId));

            var actorMixers = bank.HIRC.Items
                .Select(item => item.Item)
                .OfType<ActorMixer>()
                .ToList();
            var actorMixerIds = actorMixers.Select(item => item.Id).ToHashSet();
            var rootActorMixers = actorMixers
                .Where(item => item.NodeBaseParameters.DirectParentId == 0 ||
                               !actorMixerIds.Contains(item.NodeBaseParameters.DirectParentId))
                .ToList();
            Assert.IsNotEmpty(rootActorMixers);

            foreach (var rootActorMixer in rootActorMixers)
            {
                CollectionAssert.AreEqual(
                    new[] { BioWareRadioFutzBoxEffectId, BioWareRadioEqEffectId },
                    rootActorMixer.NodeBaseParameters.FxParams.FxChunks.Select(item => item.Id).ToArray());
                Assert.IsTrue(rootActorMixer.NodeBaseParameters.FxParams.IsOverrideParentFx);
            }
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesExactOmgHubMusicDuckingEventsToRootActorMixerForLe2()
    {
        var sourceBankPath = FindLegendaryExplorerTestData(
            "LEX Test LE2", "GeneratedSoundBanks", "Windows", "Test_Bank.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_LE2_MusicDucking_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyMusicDuckingToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, MEGame.LE2]);
            method.Invoke(null, [testBankPath, MEGame.LE2]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            var actorMixers = bank.HIRC.Items.Select(container => container.Item).OfType<ActorMixer>().ToList();
            var actorMixerIds = actorMixers.Select(actorMixer => actorMixer.Id).ToHashSet();
            var rootActorMixer = actorMixers.Single(actorMixer =>
                actorMixer.NodeBaseParameters.DirectParentId == 0 ||
                !actorMixerIds.Contains(actorMixer.NodeBaseParameters.DirectParentId));

            AssertExactRetargetedAction(bank, Le2MusicDuckActionId, Le2MusicDuckActionHirc,
                rootActorMixer.Id);
            AssertExactRetargetedAction(bank, Le2MusicResetActionId, Le2MusicResetActionHirc,
                rootActorMixer.Id);
            AssertExactEvent(bank, Le2MusicDuckEventId, Le2MusicDuckActionId, Le2MusicDuckEventHirc);
            AssertExactEvent(bank, Le2MusicResetEventId, Le2MusicResetActionId, Le2MusicResetEventHirc);
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesExactShippedRadioEffectToLe2Bank()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_LE2_Radio_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyBioWareRadioEffectToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, MEGame.LE2]);
            method.Invoke(null, [testBankPath, MEGame.LE2]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            var radioHircItems = bank.HIRC.Items
                .Where(item => item.Item.Id is Le2RadioEqEffectId or Le2RadioFilterEffectId)
                .ToList();
            Assert.HasCount(2, radioHircItems);
            AssertExactShareSet(radioHircItems.Single(item => item.Item.Id == Le2RadioEqEffectId),
                0x00690003u, Le2RadioEqHirc, bank);
            AssertExactShareSet(radioHircItems.Single(item => item.Item.Id == Le2RadioFilterEffectId),
                0x006E0003u, Le2RadioFilterHirc, bank);

            var rootActorMixers = GetRootActorMixers(bank);
            Assert.IsNotEmpty(rootActorMixers);
            foreach (var rootActorMixer in rootActorMixers)
            {
                CollectionAssert.AreEqual(
                    new[] { Le2RadioEqEffectId, Le2RadioFilterEffectId },
                    rootActorMixer.NodeBaseParameters.FxParams.FxChunks.Select(item => item.Id).ToArray());
                Assert.IsTrue(rootActorMixer.NodeBaseParameters.FxParams.IsOverrideParentFx);
            }
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesExactMe2IllusiveManHologramEffectToLe2Bank()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_LE2_Hologram_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyHologramEffectToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath]);
            method.Invoke(null, [testBankPath]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            AssertExactShareSet(bank.HIRC.Items.Single(item => item.Item.Id == Le2HologramEqEffectId),
                0x00690003u, Le2HologramEqHirc, bank);
            AssertExactShareSet(bank.HIRC.Items.Single(item => item.Item.Id == Le2HologramFilterEffectId),
                0x006C0003u, Le2HologramFilterHirc, bank);

            var rootActorMixers = GetRootActorMixers(bank);
            Assert.IsNotEmpty(rootActorMixers);
            foreach (var rootActorMixer in rootActorMixers)
            {
                var effects = rootActorMixer.NodeBaseParameters.FxParams;
                CollectionAssert.AreEqual(
                    new[] { Le2HologramEqEffectId, Le2HologramFilterEffectId },
                    effects.FxChunks.Select(item => item.Id).ToArray());
                CollectionAssert.AreEqual(new byte[] { 0, 1 },
                    effects.FxChunks.Select(item => item.FxIndex).ToArray());
                Assert.IsTrue(effects.FxChunks.All(item => item.IsShareSet));
                Assert.IsTrue(effects.IsOverrideParentFx);
            }
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesHelmetFilterControlledByShippedLe3Rtpc()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_Helmet_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyHelmetEffectToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, MEGame.LE3]);
            method.Invoke(null, [testBankPath, MEGame.LE3]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            var helmetEffect = bank.HIRC.Items.Single(item => item.Item.Id == HelmetFilterEffectId);
            AssertExactShareSet(helmetEffect, 0x006E1003u, BioWareRadioFutzBoxHirc, bank);

            var actorMixers = bank.HIRC.Items
                .Select(item => item.Item)
                .OfType<ActorMixer>()
                .ToList();
            var actorMixerIds = actorMixers.Select(item => item.Id).ToHashSet();
            var rootActorMixers = actorMixers
                .Where(item => item.NodeBaseParameters.DirectParentId == 0 ||
                               !actorMixerIds.Contains(item.NodeBaseParameters.DirectParentId))
                .ToList();
            Assert.IsNotEmpty(rootActorMixers);

            foreach (var rootActorMixer in rootActorMixers)
            {
                var effects = rootActorMixer.NodeBaseParameters.FxParams;
                Assert.HasCount(1, effects.FxChunks);
                Assert.AreEqual(HelmetFilterEffectId, effects.FxChunks[0].Id);
                Assert.AreEqual((byte)0, effects.FxChunks[0].FxIndex);
                Assert.IsTrue(effects.FxChunks[0].IsShareSet);
                Assert.IsTrue(effects.IsOverrideParentFx);

                var rtpcParameters = rootActorMixer.NodeBaseParameters.Rtpc;
                Assert.AreEqual(rtpcParameters.Rtpcs.Count, rtpcParameters.RTPCCount.Value);
                var helmetRtpcs = rtpcParameters.Rtpcs.Where(rtpc => rtpc.RtpcId == HelmetRtpcId).ToList();
                Assert.HasCount(1, helmetRtpcs);
                var helmetRtpc = helmetRtpcs[0];
                Assert.AreEqual(RtpcType.RtpcTypeInner.GameParameter, helmetRtpc.RtpcType.Value);
                Assert.AreEqual(AccumType.AccumTypeInner.Boolean, helmetRtpc.RtpcAccum.Value);
                Assert.AreEqual(ParameterId.RtpcParameterId.BypassFX0, helmetRtpc.ParamId.ParamId);
                Assert.AreEqual(CurveScaling.CurveScalingInner.None, helmetRtpc.RtpcConversionTable.Scaling.Value);
                Assert.AreEqual((ushort)2, helmetRtpc.RtpcConversionTable.GraphPointCount.Value);
                CollectionAssert.AreEqual(new[] { 0f, 1f },
                    helmetRtpc.RtpcConversionTable.Graph.Select(point => point.From).ToArray());
                CollectionAssert.AreEqual(new[] { 1f, 0f },
                    helmetRtpc.RtpcConversionTable.Graph.Select(point => point.To).ToArray());
                Assert.IsTrue(helmetRtpc.RtpcConversionTable.Graph.All(
                    point => point.Interp == CurveInterpolation.Constant));
            }

            CollectionAssert.AreEqual(new byte[] { 0x3F, 0x75, 0x2B, 0xAA },
                BitConverter.GetBytes(HelmetRtpcId));
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesLe2HelmetFiltersControlledByPlainTextHelmetSignal()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_LE2_Helmet_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyHelmetEffectToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, MEGame.LE2]);
            method.Invoke(null, [testBankPath, MEGame.LE2]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            AssertExactShareSet(bank.HIRC.Items.Single(item => item.Item.Id == Le2HelmetEqEffectId),
                0x00690003u, Le2HelmetEqHirc, bank);
            AssertExactShareSet(bank.HIRC.Items.Single(item => item.Item.Id == Le2HelmetFilterEffectId),
                0x006C0003u, Le2HelmetFilterHirc, bank);

            var rootActorMixers = GetRootActorMixers(bank);
            Assert.IsNotEmpty(rootActorMixers);
            foreach (var rootActorMixer in rootActorMixers)
            {
                var effects = rootActorMixer.NodeBaseParameters.FxParams;
                CollectionAssert.AreEqual(
                    new[] { Le2HelmetEqEffectId, Le2HelmetFilterEffectId },
                    effects.FxChunks.Select(item => item.Id).ToArray());
                CollectionAssert.AreEqual(new byte[] { 0, 1 },
                    effects.FxChunks.Select(item => item.FxIndex).ToArray());
                Assert.IsTrue(effects.FxChunks.All(item => item.IsShareSet));
                Assert.IsTrue(effects.IsOverrideParentFx);

                var rtpcParameters = rootActorMixer.NodeBaseParameters.Rtpc;
                Assert.AreEqual(rtpcParameters.Rtpcs.Count, rtpcParameters.RTPCCount.Value);
                var helmetRtpcs = rtpcParameters.Rtpcs
                    .Where(rtpc => rtpc.RtpcId == Le2HelmetRtpcId)
                    .OrderBy(rtpc => rtpc.ParamId.ParamId)
                    .ToList();
                Assert.HasCount(2, helmetRtpcs);
                CollectionAssert.AreEqual(
                    new[] { ParameterId.RtpcParameterId.BypassFX0, ParameterId.RtpcParameterId.BypassFX1 },
                    helmetRtpcs.Select(rtpc => rtpc.ParamId.ParamId).ToArray());
                foreach (var helmetRtpc in helmetRtpcs)
                {
                    Assert.AreEqual(RtpcType.RtpcTypeInner.GameParameter, helmetRtpc.RtpcType.Value);
                    Assert.AreEqual(AccumType.AccumTypeInner.Boolean, helmetRtpc.RtpcAccum.Value);
                    Assert.AreEqual(CurveScaling.CurveScalingInner.None,
                        helmetRtpc.RtpcConversionTable.Scaling.Value);
                    CollectionAssert.AreEqual(new[] { 0f, 1f },
                        helmetRtpc.RtpcConversionTable.Graph.Select(point => point.From).ToArray());
                    CollectionAssert.AreEqual(new[] { 1f, 0f },
                        helmetRtpc.RtpcConversionTable.Graph.Select(point => point.To).ToArray());
                    Assert.IsTrue(helmetRtpc.RtpcConversionTable.Graph.All(
                        point => point.Interp == CurveInterpolation.Constant));
                }
            }

            CollectionAssert.AreEqual(new byte[] { 0xAE, 0x05, 0x43, 0x9D },
                BitConverter.GetBytes(Le2HelmetRtpcId));
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesExactCitHubMusicDuckingStateToRootActorMixers()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_MusicDucking_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyMusicDuckingToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, MEGame.LE3]);
            method.Invoke(null, [testBankPath, MEGame.LE3]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            var duckingStateContainer = bank.HIRC.Items
                .Single(item => item.Item.Id == MusicDuckingStateInstanceId);
            var duckingState = duckingStateContainer.Item as ME3Tweaks.Wwiser.Model.Hierarchy.State;
            Assert.IsNotNull(duckingState);
            Assert.HasCount(1, duckingState.Prop.PropIds);
            Assert.AreEqual(ParameterId.RtpcParameterId.Volume, duckingState.Prop.PropIds[0].ParamId);
            CollectionAssert.AreEqual(new[] { -3f }, duckingState.Prop.PropValues.ToArray());
            using (var serializedState = new MemoryStream())
            {
                new BinarySerializer().Serialize(serializedState, duckingStateContainer,
                    BankSerializationContext.FromBank(bank));
                CollectionAssert.AreEqual(Convert.FromBase64String(MusicDuckingStateHirc),
                    serializedState.ToArray());
            }

            var actorMixers = bank.HIRC.Items
                .Select(item => item.Item)
                .OfType<ActorMixer>()
                .ToList();
            var actorMixerIds = actorMixers.Select(item => item.Id).ToHashSet();
            var rootActorMixers = actorMixers
                .Where(item => item.NodeBaseParameters.DirectParentId == 0 ||
                               !actorMixerIds.Contains(item.NodeBaseParameters.DirectParentId))
                .ToList();
            Assert.IsNotEmpty(rootActorMixers);

            foreach (var rootActorMixer in rootActorMixers)
            {
                var stateChunk = rootActorMixer.NodeBaseParameters.StateChunk;
                var duckingGroup = stateChunk.GroupChunks
                    .Single(group => group.Id == MusicDuckingStateGroupId);
                Assert.AreEqual(ME3Tweaks.Wwiser.Model.State.SyncType.SyncTypeInner.Immediate,
                    duckingGroup.StateGroup.StateSyncType.Value);
                Assert.HasCount(1, duckingGroup.StateGroup.States);
                Assert.AreEqual(MusicDuckingStateId, duckingGroup.StateGroup.States[0].Id);
                Assert.AreEqual(MusicDuckingStateInstanceId,
                    duckingGroup.StateGroup.States[0].StateInstanceId);
                Assert.AreEqual((uint)duckingGroup.StateGroup.States.Count,
                    duckingGroup.StateGroup.StateCount.Value);
                Assert.AreEqual((uint)stateChunk.GroupChunks.Count, stateChunk.StateGroupsCount.Value);

                CollectionAssert.IsSubsetOf(new uint[] { 4, 3, 7, 2, 0 },
                    stateChunk.PropertyInfo.Select(property => property.PropertyId.Value).ToArray());
                Assert.AreEqual((uint)stateChunk.PropertyInfo.Count, stateChunk.StatePropsCount.Value);
            }
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesExactStandardKroGarAttenuationToRootActorMixers()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_Attenuation_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyStandardAttenuationToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, 1d, MEGame.LE3]);
            method.Invoke(null, [testBankPath, 1d, MEGame.LE3]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.AreEqual(134u, bank.BKHD.BankGeneratorVersion);
            Assert.IsNotNull(bank.HIRC);

            var actorMixers = bank.HIRC.Items.Select(item => item.Item).OfType<ActorMixer>().ToList();
            var actorMixerIds = actorMixers.Select(item => item.Id).ToHashSet();
            var rootActorMixers = actorMixers
                .Where(item => item.NodeBaseParameters.DirectParentId == 0 ||
                               !actorMixerIds.Contains(item.NodeBaseParameters.DirectParentId))
                .ToList();
            Assert.IsNotEmpty(rootActorMixers);

            uint? attenuationId = null;
            foreach (var rootActorMixer in rootActorMixers)
            {
                var initialParams = rootActorMixer.NodeBaseParameters.InitialParams62;
                var attenuationReferences = initialParams.ParameterIds
                    .Select((parameter, index) => (parameter, index))
                    .Where(pair => pair.parameter.PropValue == PropId.AttenuationID)
                    .ToList();
                Assert.HasCount(1, attenuationReferences);
                var value = initialParams.ParameterValues[attenuationReferences[0].index];
                uint rootAttenuationId = value.StoredAsFloat
                    ? BitConverter.SingleToUInt32Bits(value.Float)
                    : value.Integer;
                attenuationId ??= rootAttenuationId;
                Assert.AreEqual(attenuationId.Value, rootAttenuationId);

                var positioning = rootActorMixer.NodeBaseParameters.PositioningChunk;
                Assert.IsTrue(positioning.HasPositioning);
                Assert.IsTrue(positioning.Has3DPositioning);
                Assert.AreEqual(PositioningChunk.SpeakerPanningType.DirectSpeakerAssignment,
                    positioning.PanningType);
                Assert.AreEqual(PositioningChunk.PositionType3D.Emitter, positioning.PositionType);
                Assert.IsTrue(positioning.Mode.HasFlag(SpatializationMode.PositionAndOrientation));
                Assert.IsTrue(positioning.Mode.HasFlag(SpatializationMode.EnableAttenuation));
                Assert.IsFalse(positioning.Mode.HasFlag(SpatializationMode.PositionOnly));
            }

            Assert.IsTrue(attenuationId.HasValue);
            var attenuationContainers = bank.HIRC.Items
                .Where(item => item.Item.Id == attenuationId.Value)
                .ToList();
            Assert.HasCount(1, attenuationContainers);
            var attenuation = attenuationContainers[0].Item as Attenuation;
            Assert.IsNotNull(attenuation);

            attenuation.Id = StandardAttenuationSourceId;
            using var serializedAttenuation = new MemoryStream();
            new BinarySerializer().Serialize(serializedAttenuation, attenuationContainers[0],
                BankSerializationContext.FromBank(bank));
            CollectionAssert.AreEqual(Convert.FromBase64String(StandardAttenuationHirc),
                serializedAttenuation.ToArray());
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void AppliesExactJnkKgALe2AttenuationToRootActorMixers()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_LE2_Attenuation_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyStandardAttenuationToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, 1d, MEGame.LE2]);
            method.Invoke(null, [testBankPath, 1d, MEGame.LE2]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.IsNotNull(bank.HIRC);

            var rootActorMixers = GetRootActorMixers(bank);
            Assert.IsNotEmpty(rootActorMixers);
            uint? attenuationId = null;
            foreach (var rootActorMixer in rootActorMixers)
            {
                var initialParams = rootActorMixer.NodeBaseParameters.InitialParams62;
                var attenuationReferences = initialParams.ParameterIds
                    .Select((parameter, index) => (parameter, index))
                    .Where(pair => pair.parameter.PropValue == PropId.AttenuationID)
                    .ToList();
                Assert.HasCount(1, attenuationReferences);
                var value = initialParams.ParameterValues[attenuationReferences[0].index];
                uint rootAttenuationId = value.StoredAsFloat
                    ? BitConverter.SingleToUInt32Bits(value.Float)
                    : value.Integer;
                attenuationId ??= rootAttenuationId;
                Assert.AreEqual(attenuationId.Value, rootAttenuationId);

                var positioning = rootActorMixer.NodeBaseParameters.PositioningChunk;
                Assert.IsTrue(positioning.Mode.HasFlag(SpatializationMode.PositionAndOrientation));
                Assert.IsTrue(positioning.Mode.HasFlag(SpatializationMode.EnableAttenuation));
                Assert.IsTrue(positioning.Mode.HasFlag(SpatializationMode.EnableDiffraction));
            }

            Assert.IsTrue(attenuationId.HasValue);
            var attenuationContainer = bank.HIRC.Items.Single(item => item.Item.Id == attenuationId.Value);
            var attenuation = attenuationContainer.Item as Attenuation;
            Assert.IsNotNull(attenuation);
            Assert.HasCount(5, attenuation.Curves);

            attenuation.Id = StandardAttenuationSourceId;
            using var serializedAttenuation = new MemoryStream();
            new BinarySerializer().Serialize(serializedAttenuation, attenuationContainer,
                BankSerializationContext.FromBank(bank));
            CollectionAssert.AreEqual(Convert.FromBase64String(Le2StandardAttenuationHirc),
                serializedAttenuation.ToArray());
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    [TestMethod]
    public void ScalesEveryStandardAttenuationDistanceCurve()
    {
        var sourceBankPath = FindWwiserTestBank("LE3_v134_1.bnk");
        var testBankPath = Path.Combine(Path.GetTempPath(), $"LEX_AttenuationScale_Test_{Guid.NewGuid():N}.bnk");
        File.Copy(sourceBankPath, testBankPath);

        try
        {
            var method = typeof(BulkAudioImportDialog).GetMethod(
                "ApplyStandardAttenuationToBank", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            method.Invoke(null, [testBankPath, 2d, MEGame.LE3]);
            method.Invoke(null, [testBankPath, 2d, MEGame.LE3]);

            using var stream = File.OpenRead(testBankPath);
            var bank = WwiseBankParser.Deserialize(stream);
            Assert.IsNotNull(bank.HIRC);

            var attenuationIds = bank.HIRC.Items.Select(item => item.Item).OfType<ActorMixer>()
                .SelectMany(actorMixer => actorMixer.NodeBaseParameters.InitialParams62.ParameterIds
                    .Select((parameter, index) => (parameter, index, actorMixer)))
                .Where(pair => pair.parameter.PropValue == PropId.AttenuationID)
                .Select(pair =>
                {
                    var value = pair.actorMixer.NodeBaseParameters.InitialParams62.ParameterValues[pair.index];
                    return value.StoredAsFloat ? BitConverter.SingleToUInt32Bits(value.Float) : value.Integer;
                })
                .Distinct()
                .ToList();
            Assert.HasCount(1, attenuationIds);

            var attenuationContainers = bank.HIRC.Items
                .Where(item => item.Item.Id == attenuationIds[0])
                .ToList();
            Assert.HasCount(1, attenuationContainers);
            var attenuation = attenuationContainers[0].Item as Attenuation;
            Assert.IsNotNull(attenuation);
            Assert.HasCount(4, attenuation.Curves);
            CollectionAssert.AreEqual(new[] { 0f, 140f },
                attenuation.Curves[0].Graph.Select(point => point.From).ToArray());
            CollectionAssert.AreEqual(new[] { 0f, 140f },
                attenuation.Curves[1].Graph.Select(point => point.From).ToArray());
            CollectionAssert.AreEqual(new[] { 0f, 140f },
                attenuation.Curves[2].Graph.Select(point => point.From).ToArray());
            CollectionAssert.AreEqual(new[] { 0f, 14f, 140f },
                attenuation.Curves[3].Graph.Select(point => point.From).ToArray());
        }
        finally
        {
            File.Delete(testBankPath);
        }
    }

    private static void AssertExactShareSet(HircItemContainer hircItem, uint expectedPluginId,
        string expectedHirc, ME3Tweaks.Wwiser.WwiseBank bank)
    {
        var shareSet = hircItem.Item as FxShareSet;
        Assert.IsNotNull(shareSet);
        Assert.AreEqual(expectedPluginId, shareSet.Plugin.PluginId);

        using var serializedHirc = new MemoryStream();
        new BinarySerializer().Serialize(serializedHirc, hircItem, BankSerializationContext.FromBank(bank));
        CollectionAssert.AreEqual(Convert.FromBase64String(expectedHirc), serializedHirc.ToArray());
    }

    private static List<ActorMixer> GetRootActorMixers(ME3Tweaks.Wwiser.WwiseBank bank)
    {
        var actorMixers = bank.HIRC.Items.Select(item => item.Item).OfType<ActorMixer>().ToList();
        var actorMixerIds = actorMixers.Select(item => item.Id).ToHashSet();
        return actorMixers
            .Where(item => item.NodeBaseParameters.DirectParentId == 0 ||
                           !actorMixerIds.Contains(item.NodeBaseParameters.DirectParentId))
            .ToList();
    }

    [TestMethod]
    public void BuildsPerAudioAndSharedStopEvents()
    {
        var firstWav = @"C:\Audio\First.wav";
        var secondWav = @"C:\Audio\Second.wav";
        var document = BuildEventsXml(
            [firstWav, secondWav],
            generateGenderedEvents: false,
            createSharedStopEvent: true,
            perAudioStopEventFiles: new HashSet<string>([firstWav], StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "First_Play", "First_Stop", "Second_Play", "Stop" },
            document.Descendants("Event").Select(GetName).ToArray());

        AssertStopEvent(document, "First_Stop", "First");
        AssertStopEvent(document, "Stop", "First", "Second");
        Assert.IsNull(document.Descendants("Event").SingleOrDefault(element => GetName(element) == "Second_Stop"));

        var sharedActionShortIds = GetEvent(document, "Stop")
            .Descendants("Action")
            .Select(action => action.Attribute("ShortID")?.Value)
            .ToArray();
        Assert.AreEqual(sharedActionShortIds.Length, sharedActionShortIds.Distinct().Count());
    }

    [TestMethod]
    public void PerAudioStopEventTargetsBothGeneratedGenderVariants()
    {
        var wavPath = @"C:\Audio\Voice.wav";
        var document = BuildEventsXml(
            [wavPath],
            generateGenderedEvents: true,
            createSharedStopEvent: false,
            perAudioStopEventFiles: new HashSet<string>([wavPath], StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "Voice_m_Play", "Voice_f_Play", "Voice_Stop" },
            document.Descendants("Event").Select(GetName).ToArray());
        AssertStopEvent(document, "Voice_Stop", "Voice_m", "Voice_f");
        Assert.IsNull(document.Descendants("Event").SingleOrDefault(element => GetName(element) == "Stop"));
    }

    [TestMethod]
    public void DoesNotCreateStopEventsWhenOptionsAreDisabled()
    {
        var document = BuildEventsXml(
            [@"C:\Audio\Voice.wav"],
            generateGenderedEvents: false,
            createSharedStopEvent: false,
            perAudioStopEventFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "Voice_Play" },
            document.Descendants("Event").Select(GetName).ToArray());
        Assert.IsFalse(document.Descendants("Property")
            .Any(property => property.Attribute("Name")?.Value == "ActionType"));
    }

    [TestMethod]
    public void BuildsLe2GenderedPlayAndStopEventsOverOneStreamedSound()
    {
        var wavPath = @"C:\Audio\Voice.wav";
        var document = BuildEventsXml(
            [wavPath],
            generateGenderedEvents: true,
            createSharedStopEvent: true,
            perAudioStopEventFiles: new HashSet<string>([wavPath], StringComparer.OrdinalIgnoreCase),
            game: MEGame.LE2);

        CollectionAssert.AreEqual(
            new[] { "Voice_m_Play", "Voice_f_Play", "Stop_Voice", "Stop" },
            document.Descendants("Event").Select(GetName).ToArray());

        CollectionAssert.AreEqual(
            new[] { "Voice", "Voice" },
            document.Descendants("Event")
                .Where(element => GetName(element).EndsWith("_Play", StringComparison.Ordinal))
                .Select(element => element.Descendants("ObjectRef").Single().Attribute("Name")?.Value)
                .ToArray());
        AssertStopEvent(document, "Stop_Voice", "Voice");
        AssertStopEvent(document, "Stop", "Voice");
    }

    [TestMethod]
    public void NormalizesLe2EventStyleWavNameBeforeCreatingGenderedEvents()
    {
        var wavPath = @"C:\Audio\VO_1850000_m_Play.wav";
        var document = BuildEventsXml(
            [wavPath],
            generateGenderedEvents: true,
            createSharedStopEvent: false,
            perAudioStopEventFiles: new HashSet<string>([wavPath], StringComparer.OrdinalIgnoreCase),
            game: MEGame.LE2);

        CollectionAssert.AreEqual(
            new[] { "VO_1850000_m_Play", "VO_1850000_f_Play", "Stop_VO_1850000_m" },
            document.Descendants("Event").Select(GetName).ToArray());

        CollectionAssert.AreEqual(
            new[] { "VO_1850000_m_Play", "VO_1850000_m_Play" },
            document.Descendants("Event")
                .Where(element => GetName(element) is "VO_1850000_m_Play" or "VO_1850000_f_Play")
                .Select(element => element.Descendants("ObjectRef").Single().Attribute("Name")?.Value)
                .ToArray());
    }

    [TestMethod]
    public void BuildsLe2FaceFxImportNamesForBothGenderVariants()
    {
        var method = typeof(FaceFXAnimSetEditorControl).GetMethod(
            "BuildImportedGenderedEventNameSet", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        var wavFiles = new[] { @"C:\Audio\VO_1850000_m_Play.mp3" };
        var bothGenders = method.Invoke(null, [wavFiles, MEGame.LE2]) as HashSet<string>;

        CollectionAssert.AreEquivalent(
            new[] { "VO_1850000_m_Play", "VO_1850000_f_Play" },
            bothGenders?.ToArray());
    }

    [TestMethod]
    public void CreatesWwiseEventAlongsideSameNamedWwiseStream()
    {
        using IMEPackage package = MEPackageHandler.CreateMemoryEmptyPackage("SameNameAudio.pcc", MEGame.LE2);
        var parent = ExportCreator.CreatePackageExport(package, "audio");
        var stream = ExportCreator.CreateExport(package, "VO_1850000_m_Play", "WwiseStream", parent,
            indexed: false);

        var method = typeof(WwiseBankImport).GetMethod("FindOrCreateEventExport",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        var wwiseEvent = method.Invoke(null, [package, parent, "VO_1850000_m_Play"]) as ExportEntry;

        Assert.IsNotNull(wwiseEvent);
        Assert.AreEqual("WwiseEvent", wwiseEvent.ClassName);
        Assert.AreEqual(stream.InstancedFullPath, wwiseEvent.InstancedFullPath);
        Assert.AreSame(stream, package.FindExport(stream.InstancedFullPath, "WwiseStream"));
        Assert.AreSame(wwiseEvent, package.FindExport(wwiseEvent.InstancedFullPath, "WwiseEvent"));
    }

    [TestMethod]
    public void BuildsLe2StreamedLoopingSoundWithAuxSendsAndSelectedOutputBus()
    {
        var document = BuildActorMixerXml(MEGame.LE2, [@"C:\Audio\Voice.wav"],
            generateGenderedEvents: true, loopAudio: true);
        var sounds = document.Descendants("Sound").ToList();
        Assert.HasCount(1, sounds);
        Assert.AreEqual("Voice", GetName(sounds[0]));
        Assert.IsTrue(document.Descendants("Property")
            .Any(property => property.Attribute("Name")?.Value == "IsStreamingEnabled" &&
                             property.Descendants("Value").Any(value => value.Value == "True")));
        Assert.IsTrue(document.Descendants("Property")
            .Any(property => property.Attribute("Name")?.Value == "IsLoopingEnabled"));
        Assert.IsFalse(document.Descendants("Property")
            .Any(property => property.Attribute("Name")?.Value == "IsLoopingInfinite"));
        Assert.IsTrue(document.Descendants("Property")
            .Any(property => property.Attribute("Name")?.Value == "UseGameAuxSends" &&
                             property.Attribute("Value")?.Value == "True"));

        var actorMixer = document.Descendants("ActorMixer").Single();
        Assert.AreEqual("Conversation", actorMixer.Element("ReferenceList")
            ?.Elements("Reference")
            .Single(reference => reference.Attribute("Name")?.Value == "OutputBus")
            .Element("ObjectRef")?.Attribute("Name")?.Value);
        Assert.IsTrue(document.Descendants("Reference")
            .Where(reference => reference.Attribute("Name")?.Value == "Conversion")
            .All(reference => reference.Element("ObjectRef")?.Attribute("Name")?.Value ==
                              "Default Conversion Settings"));
    }

    [TestMethod]
    public void RecognizesGenderedPlayEventNames()
    {
        Assert.AreEqual("VO_123_m_Play", WwiseEventNaming.GetPlayEventName(MEGame.LE2, "VO_123_m"));
        Assert.AreEqual("VO_123_m_Play", WwiseEventNaming.GetPlayEventName(MEGame.LE2, "VO_123_m_Play"));
        Assert.AreEqual("VO_123_m_Play", WwiseEventNaming.GetPlayEventName(MEGame.LE3, "VO_123_m"));
        Assert.AreEqual("Stop_VO_123", WwiseEventNaming.GetPerAudioStopEventName(MEGame.LE2, "VO_123"));
        Assert.AreEqual("VO_123_Stop", WwiseEventNaming.GetPerAudioStopEventName(MEGame.LE3, "VO_123"));
        Assert.IsTrue(WwiseEventNaming.IsPlayEventForGender("VO_123_f_Play", true, MEGame.LE2));
        Assert.IsFalse(WwiseEventNaming.IsPlayEventForGender("Play_VO_123_f", true, MEGame.LE2));
        Assert.IsTrue(WwiseEventNaming.IsPlayEventForGender("VO_123_m_Play", false, MEGame.LE3));
        Assert.IsFalse(WwiseEventNaming.IsPlayEventForGender("Play_VO_123_m", false, MEGame.LE3));
    }

    [TestMethod]
    public void ResolvesDialogueSoundPackageSibling()
    {
        Assert.IsTrue(DialogueAudioPackageNaming.TryGetSoundPackageName(
            "norvx_relationship_00_h_D", out var soundPackageName));
        Assert.AreEqual("norvx_relationship_00_h_S", soundPackageName);
        Assert.IsTrue(DialogueAudioPackageNaming.TryGetSoundPackageName(
            "BioD_Test_d", out soundPackageName));
        Assert.AreEqual("BioD_Test_S", soundPackageName);
        Assert.IsFalse(DialogueAudioPackageNaming.TryGetSoundPackageName(
            "norvx_relationship_00_h_S", out soundPackageName));
        Assert.IsNull(soundPackageName);
    }

    private static XDocument BuildEventsXml(List<string> wavFiles, bool generateGenderedEvents,
        bool createSharedStopEvent, HashSet<string> perAudioStopEventFiles, MEGame game = MEGame.LE3)
    {
        var method = typeof(BulkAudioImportDialog).GetMethod(
            "BuildEventsXml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        var xml = method.Invoke(null,
            [game, EventsWorkUnitId, ActorMixerWorkUnitId, wavFiles, generateGenderedEvents, createSharedStopEvent, perAudioStopEventFiles]) as string;
        Assert.IsNotNull(xml);
        return XDocument.Parse(xml);
    }

    private static XDocument BuildActorMixerXml(MEGame game, List<string> wavFiles,
        bool generateGenderedEvents, bool loopAudio = false)
    {
        var method = typeof(BulkAudioImportDialog).GetMethod(
            "BuildActorMixerXml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        var conversion = (Name: "Default Conversion Settings",
            Id: "{33333333-3333-3333-3333-333333333333}",
            WorkUnitId: "{44444444-4444-4444-4444-444444444444}");
        var xml = method.Invoke(null,
        [
            game,
            ActorMixerWorkUnitId,
            "TestBank",
            "{55555555-5555-5555-5555-555555555555}",
            wavFiles,
            0d,
            "Conversation",
            "{66666666-6666-6666-6666-666666666666}",
            "{77777777-7777-7777-7777-777777777777}",
            conversion,
            generateGenderedEvents,
            loopAudio,
        ]) as string;
        Assert.IsNotNull(xml);
        return XDocument.Parse(xml);
    }

    private static void AssertStopEvent(XDocument document, string eventName, params string[] expectedTargets)
    {
        var stopEvent = GetEvent(document, eventName);
        var actions = stopEvent.Descendants("Action").ToList();
        CollectionAssert.AreEqual(
            expectedTargets,
            actions.Select(action => action.Descendants("ObjectRef").Single().Attribute("Name")?.Value).ToArray());

        foreach (var action in actions)
        {
            var actionType = action.Descendants("Property")
                .Single(property => property.Attribute("Name")?.Value == "ActionType");
            Assert.AreEqual("int16", actionType.Attribute("Type")?.Value);
            Assert.AreEqual("2", actionType.Attribute("Value")?.Value);
            Assert.AreEqual(ActorMixerWorkUnitId, action.Descendants("ObjectRef").Single().Attribute("WorkUnitID")?.Value);
        }
    }

    private static XElement GetEvent(XDocument document, string eventName) =>
        document.Descendants("Event").Single(element => GetName(element) == eventName);

    private static string GetName(XElement element) => element.Attribute("Name")?.Value;

    private static void AssertExactRetargetedAction(ME3Tweaks.Wwiser.WwiseBank bank, uint actionId,
        string serializedSourceAction, uint targetId)
    {
        var actionContainer = bank.HIRC.Items.Single(container => container.Item.Id == actionId);
        var action = actionContainer.Item as HircAction;
        Assert.IsNotNull(action);
        Assert.AreEqual(targetId, action.TargetId);

        var serializer = new BinarySerializer();
        var context = BankSerializationContext.FromBank(bank);
        var expectedContainer = serializer.Deserialize<HircItemContainer>(
            Convert.FromBase64String(serializedSourceAction), context);
        ((HircAction)expectedContainer.Item).TargetId = targetId;

        using var actualBytes = new MemoryStream();
        using var expectedBytes = new MemoryStream();
        serializer.Serialize(actualBytes, actionContainer, context);
        serializer.Serialize(expectedBytes, expectedContainer, context);
        CollectionAssert.AreEqual(expectedBytes.ToArray(), actualBytes.ToArray());
    }

    private static void AssertExactEvent(ME3Tweaks.Wwiser.WwiseBank bank, uint eventId,
        uint actionId, string serializedSourceEvent)
    {
        var eventContainer = bank.HIRC.Items.Single(container => container.Item.Id == eventId);
        var hircEvent = eventContainer.Item as HircEvent;
        Assert.IsNotNull(hircEvent);
        CollectionAssert.AreEqual(new[] { actionId }, hircEvent.ActionIds.ToArray());
        Assert.AreEqual((uint)hircEvent.ActionIds.Count, hircEvent.ActionCount.Value);

        using var actualBytes = new MemoryStream();
        new BinarySerializer().Serialize(actualBytes, eventContainer, BankSerializationContext.FromBank(bank));
        CollectionAssert.AreEqual(Convert.FromBase64String(serializedSourceEvent), actualBytes.ToArray());
    }

    private static string FindWwiserTestBank(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "submodules", "Wwiser.NET",
                "ME3Tweaks.Wwiser.Tests", "TestData", "WholeBanks", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail($"Could not locate Wwiser test bank '{fileName}'.");
        return null;
    }

    private static string FindLegendaryExplorerTestData(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                [directory.FullName, "LegendaryExplorer", "WwiseTestData", .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail($"Could not locate Legendary Explorer test data '{Path.Combine(relativePath)}'.");
        return null;
    }
}
