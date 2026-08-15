using System;
using System.Collections.Generic;
using System.Linq;
using BinarySerialization;
using LegendaryExplorerCore.Packages;
using ME3Tweaks.Wwiser;
using ME3Tweaks.Wwiser.Formats;
using ME3Tweaks.Wwiser.Model.Hierarchy;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using ME3Tweaks.Wwiser.Model.ParameterNode.Positioning;
using ME3Tweaks.Wwiser.Model.RTPC;
using ME3Tweaks.Wwiser.Model.State;
using HircAction = ME3Tweaks.Wwiser.Model.Hierarchy.Action;
using HierarchyState = ME3Tweaks.Wwiser.Model.Hierarchy.State;
using StateEntry = ME3Tweaks.Wwiser.Model.State.State;

namespace LegendaryExplorer.UnrealExtensions;

internal readonly record struct WwiseBankEffect(uint Id, uint PluginId, string SerializedHirc);

internal static class WwiseBankEffectPresets
{
    internal const uint BankVersion = 134;
    internal const uint FactoryRadioEffectId = 2952825346;
    internal const uint HelmetFilterEffectId = 125287176;
    internal const uint BioWareRadioFutzBoxEffectId = HelmetFilterEffectId;
    internal const uint BioWareRadioEqEffectId = 1177780410;
    internal const uint QecFutzBoxEffectId = 1827713496;
    internal const uint QecFlangerEffectId = 1487904704;
    // The source bank stores this uint as the little-endian bytes 3F 75 2B AA.
    internal const uint HelmetRtpcId = 0xAA2B753F;
    internal const uint Le2HelmetEqEffectId = 0xB6871666;
    internal const uint Le2HelmetFilterEffectId = 0x8371E703;
    internal const uint Le2RadioEqEffectId = 0x85ABA16D;
    internal const uint Le2RadioFilterEffectId = 0xF4D30DBA;
    internal const uint Le2HologramEqEffectId = 0x5A281C31;
    internal const uint Le2HologramFilterEffectId = 0x7128A668;
    // Wwise ShortID for the plain-text LE2 game parameter name "Helmet".
    internal const uint Le2HelmetRtpcId = 0x9D4305AE;
    internal const uint MusicDuckingStateGroupId = 0x7BC046C4;
    internal const uint MusicDuckingStateId = 0x61030AE6;
    internal const uint MusicDuckingStateInstanceId = 0x25716DBE;
    internal const float MusicDuckingVolumeDb = -3f;
    internal const uint Le2MusicDuckActionId = 0x015FDFA5;
    internal const uint Le2MusicDuckEventId = 0x16BCA20A;
    internal const uint Le2MusicResetActionId = 0x1423EC41;
    internal const uint Le2MusicResetEventId = 0xCD2B1173;
    internal const float Le2MusicDuckingVolumeDb = -12f;
    internal const uint StandardAttenuationSourceId = 0x13ED5249;
    internal const float StandardAttenuationOriginalMaxDistance = 70f;
    // Exact version-134 State HIRC from wwise_cithub_streaming in BioSnd_CitHub.
    private const string MusicDuckingStateHirc = "AQwAAAC+bXElAQAAAAAAQMA=";
    // Exact paired Set Voice Volume/Reset Voice Volume actions and Events from
    // wwise_omghub_streaming in BioS_OmgHub. The Action targets are replaced with the
    // generated root ActorMixer ID while the fixed Event and Action IDs remain unchanged.
    private const string Le2MusicDuckActionHirc =
        "AyEAAACl318BAgoF9RYNAAEQoA8AAAAEAgAAQMEAAAAAAAAAAAA=";
    private const string Le2MusicDuckEventHirc = "BAkAAAAKorwWAaXfXwE=";
    private const string Le2MusicResetActionHirc =
        "AyEAAABB7CMUAgsF9RYNAAEQ6AMAAAAEAQAAAAAAAAAAAAAAAAA=";
    private const string Le2MusicResetEventHirc = "BAkAAABzESvNAUHsIxQ=";
    // Exact version-134 Attenuation HIRC used by the localized KroGar dialogue banks.
    private const string StandardAttenuationHirc =
        "DpsAAABJUu0TAQAAtEIAAHVDAABAwAAA8EEAAAAAAAEC//8D/wQCAgAAAAAAAAAAAAAAAAAAAIxCkud3vwQAAAACAgAAAAAAIYiVvgQAAAAAAIxCY2T/vgQAAAACAgAAAAAAIYiVvgQAAAAAAIxCY2T/vgQAAAAAAwAAAAAAAADIQQQAAAAAAOBAAAAAAAkAAAAAAIxCAAAAAAQAAAAAAA==";
    // Exact version-134 five-curve Attenuation HIRC from BioD_JnkKgA_100Landing_LOC_INT.
    private const string Le2StandardAttenuationHirc =
        "DqIAAABJUu0TAAABAgP/BP8FAgIAAAAAAAAAAAAAAAAAAACMQv/+f78EAAAAAgIAAAAAACnzvL4EAAAAAACMQv/+f78EAAAAAgIAAAAAACnzvL4EAAAAAACMQv/+f78EAAAAAAIAAAAAAAAAAAABAAAAAACMQgAAcEIEAAAAAAMAAAAAAAAAyEEEAAAAAABSQQAAAAAJAAAAAACMQgAAAAAEAAAAAAA=";

    internal static IReadOnlyList<WwiseBankEffect> FactoryRadio { get; } =
    [
        new(FactoryRadioEffectId, 0x00690003u,
            "EEsAAAACigCwAwBpADgAAAABAAAAAAAAAAAASEQAAIA/AQYAAAAAAJBBAIA7RQAAgD8BAAAAAAAAAAAAoJFFAACAPwEAAEDBAQAAAAAAAAA=")
    ];

    private static WwiseBankEffect BioWareFutzBox { get; } =
        new(HelmetFilterEffectId, 0x006E1003u,
            "EJ4AAAAIu3cHAxBuAIsAAAAAAAAAAADgEkYAAAAAAQAAAAAAlkMAAAAAAQQAAAAAAPBBAAAAAAAAAAAAAQAAAAAAekQAAAAAAAAAAAAAAPjBAGAuRQAAekQAAAAAAAAgwgAAIEEBEgAAAAAAyEIAAACgwQAAoMLNzMw9AAAgQQAAIEEACAAAAAQAAAAAAAAAAAAAAAAAoEAAAMhCAAAAAAAAAA==");

    internal static IReadOnlyList<WwiseBankEffect> HelmetFilter { get; } = [BioWareFutzBox];

    internal static IReadOnlyList<WwiseBankEffect> Le2HelmetFilter { get; } =
    [
        new(Le2HelmetEqEffectId, 0x00690003u,
            "EEsAAABmFoe2AwBpADgAAAABAAAAAADAwQCACUQAAAA/AQYAAAAAAMDBAMBiRAAAAD8AAAAAAAAAAAAAQJxFAACAPwEAAIBAAQAAAAAAAAA="),
        new(Le2HelmetFilterEffectId, 0x006C0003u,
            "ECkAAAAD53GDAwBsABYAAAAAAEjCAAAAQArXIzzNzEw9AACAQQEBAAAAAAAAAA==")
    ];

    internal static IReadOnlyList<WwiseBankEffect> Le2Radio { get; } =
    [
        new(Le2RadioEqEffectId, 0x00690003u,
            "EEsAAABtoauFAwBpADgAAAABAAAAAAAAAAAAL0QAAIA/AQIAAAAAAAAAAACWRAAAQEABAAAAAAAAAAAAcNBFAACAPwAAAEBBAQAAAAAAAAA="),
        new(Le2RadioFilterEffectId, 0x006E0003u,
            "ECkAAAC6DdP0AwBuABYAAAAAABDCAAAgQW8SgzqPwvU8AADAQQEBAAAAAAAAAA==")
    ];

    /// <summary>
    /// Exact version-134 ShareSets used on the hologram dialogue actor mixers in
    /// profre_illusive_d. Their parameter payloads match ME2's inline effects byte-for-byte.
    /// </summary>
    internal static IReadOnlyList<WwiseBankEffect> Le2Hologram { get; } =
    [
        new(Le2HologramEqEffectId, 0x00690003u,
            "EEsAAAAxHChaAwBpADgAAAABAAAAAACwwQAAyEIAAIA/AAMAAAAAAAC/AAD6RAAAAEAABQAAAAAAwEAAAHpFAACAPwEAAAAAAQAAAAAAAAA="),
        new(Le2HologramFilterEffectId, 0x006C0003u,
            "ECkAAABopihxAwBsABYAAAAzM9fBAABAQG8SgzpvEoM6AADAQAEBAAAAAAAAAA==")
    ];

    internal static IReadOnlyList<WwiseBankEffect> BioWareRadio { get; } =
    [
        BioWareFutzBox,
        new(BioWareRadioEqEffectId, 0x00690003u,
            "EL0AAAC6gDNGAwBpADgAAAAEAAAAAAAAAACA/EMAAIA/AQYAAAAAAAAAAMDNRAAAQEAABQAAAAAAwMEAQJxGAACAPwEAAAAAAQADAKhWFSkAAQE2GTELAgIAAAAAAGNk/74EAAAAAEAcRvXYb78EAAAAqFYVKQABDNVnOioAAgAAAAAAAGBqRgAAAAAAQBxGAIC7RAQAAACoVhUpAAECWsrmPgACAAAAAAAAAKBBBAAAAABAHEYAAPpEBAAAAAAAAAA=")
    ];

    internal static IReadOnlyList<WwiseBankEffect> HackettQec { get; } =
    [
        new(QecFutzBoxEffectId, 0x006E1003u,
            "EJ4AAADYsfBsAxBuAIsAAAAAAAAAAACgjEYAAAAAAAAAAAAAIEIAAAAAAQQAAAAAAPBBAAAAAAAAAAAAAQAAAAAAekQAAAAAAAAAAAAAAMDCAKCMRgAAIEIAAAAAAACgwQAAoEEBAwAAAAAAyEIAAAAgwgAAAAAAAIA/AAAgQQAAyEIAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAMhCAAAAAAAAAA=="),
        new(QecFlangerEffectId, 0x007D0003u,
            "EE4AAADAn69YAwB9ADsAAAAAACBBAACAPwAAgD8AAAAAAABIQs3MzD0AAAAAAAAAAAAASEIAALRCAAAAAAAAAAAAAAAAAABIQgEBAAAAAAAAAAA=")
    ];

    internal static bool CanEnsureEffectData(ME3Tweaks.Wwiser.WwiseBank bank,
        IReadOnlyList<WwiseBankEffect> effects)
    {
        if (bank.HIRC == null || bank.BKHD.BankGeneratorVersion != BankVersion)
        {
            return false;
        }

        foreach (var effect in effects)
        {
            var existingItem = bank.HIRC.Items.FirstOrDefault(item => item.Item.Id == effect.Id);
            if (existingItem != null &&
                (existingItem.Item is not FxShareSet shareSet || shareSet.Plugin.PluginId != effect.PluginId))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool EnsureEffectData(ME3Tweaks.Wwiser.WwiseBank bank,
        IReadOnlyList<WwiseBankEffect> effects)
    {
        if (!CanEnsureEffectData(bank, effects))
        {
            return false;
        }

        var serializer = new BinarySerializer();
        var context = BankSerializationContext.FromBank(bank);
        foreach (var effect in effects)
        {
            if (bank.HIRC.Items.Any(item => item.Item.Id == effect.Id))
            {
                continue;
            }

            bank.HIRC.Items.Add(serializer.Deserialize<HircItemContainer>(
                Convert.FromBase64String(effect.SerializedHirc), context));
        }

        bank.HIRC.ItemCount = checked((uint)bank.HIRC.Items.Count);
        return true;
    }

    internal static bool HasHelmetRtpcOnAllScopes(IReadOnlyCollection<IHasNode> scopes) =>
        scopes.Count > 0 && scopes.All(scope => scope.NodeBaseParameters.Rtpc.Rtpcs.Any(IsHelmetRtpc));

    internal static bool EnsureStandardAttenuationData(ME3Tweaks.Wwiser.WwiseBank bank,
        float distanceScale, out uint attenuationId) =>
        EnsureStandardAttenuationData(bank, MEGame.LE3, distanceScale, out attenuationId);

    internal static bool EnsureStandardAttenuationData(ME3Tweaks.Wwiser.WwiseBank bank,
        MEGame game, float distanceScale, out uint attenuationId)
    {
        attenuationId = 0;
        if (bank.HIRC == null || bank.BKHD.BankGeneratorVersion != BankVersion ||
            game is not (MEGame.LE2 or MEGame.LE3) ||
            float.IsNaN(distanceScale) || float.IsInfinity(distanceScale) || distanceScale <= 0)
        {
            return false;
        }

        uint generatedAttenuationId = GenerateShortId($"lex_standard_attenuation_{bank.BKHD.SoundBankId:X8}");
        attenuationId = generatedAttenuationId;
        var existingIndex = bank.HIRC.Items.FindIndex(item => item.Item.Id == generatedAttenuationId);
        if (existingIndex >= 0 && bank.HIRC.Items[existingIndex].Item is not Attenuation)
        {
            return false;
        }

        var serializer = new BinarySerializer();
        string serializedHirc = game == MEGame.LE2
            ? Le2StandardAttenuationHirc
            : StandardAttenuationHirc;
        var attenuationContainer = serializer.Deserialize<HircItemContainer>(
            Convert.FromBase64String(serializedHirc), BankSerializationContext.FromBank(bank));
        if (attenuationContainer.Item is not Attenuation attenuation ||
            attenuation.Id != StandardAttenuationSourceId)
        {
            return false;
        }

        attenuation.Id = generatedAttenuationId;
        foreach (var curve in attenuation.Curves)
        {
            foreach (var point in curve.Graph)
            {
                point.From *= distanceScale;
            }
        }

        if (existingIndex >= 0)
        {
            bank.HIRC.Items[existingIndex] = attenuationContainer;
        }
        else
        {
            bank.HIRC.Items.Add(attenuationContainer);
        }

        bank.HIRC.ItemCount = checked((uint)bank.HIRC.Items.Count);
        return true;
    }

    internal static bool HasStandardAttenuationOnAllScopes(IReadOnlyCollection<IHasNode> scopes,
        uint attenuationId) => scopes.Count > 0 && scopes.All(scope =>
    {
        var initialParams = scope.NodeBaseParameters.InitialParams62;
        var positioning = scope.NodeBaseParameters.PositioningChunk;
        return initialParams.ParameterIds.Zip(initialParams.ParameterValues)
                   .Any(parameter => parameter.First.PropValue == PropId.AttenuationID &&
                                     GetRawParameterValue(parameter.Second) == attenuationId) &&
               positioning.HasPositioning && positioning.Has3DPositioning &&
               positioning.Mode.HasFlag(SpatializationMode.PositionAndOrientation) &&
               positioning.Mode.HasFlag(SpatializationMode.EnableAttenuation);
    });

    internal static void SetStandardAttenuationOnScopes(IEnumerable<IHasNode> scopes,
        uint attenuationId, bool enabled, bool enableDiffraction = false)
    {
        foreach (var scope in scopes.Distinct())
        {
            var initialParams = scope.NodeBaseParameters.InitialParams62;
            for (int index = initialParams.ParameterIds.Count - 1; index >= 0; index--)
            {
                if (initialParams.ParameterIds[index].PropValue != PropId.AttenuationID)
                {
                    continue;
                }

                initialParams.ParameterIds.RemoveAt(index);
                initialParams.ParameterValues.RemoveAt(index);
            }

            var positioning = scope.NodeBaseParameters.PositioningChunk;
            if (enabled)
            {
                initialParams.AddParameter(PropId.AttenuationID, new InitialParamsV62.ParameterValue
                {
                    Integer = attenuationId,
                    StoredAsFloat = false
                });
                positioning.HasPositioning = true;
                positioning.Has3DPositioning = true;
                positioning.PanningType = PositioningChunk.SpeakerPanningType.DirectSpeakerAssignment;
                if (!positioning.HasAutomation)
                {
                    positioning.PositionType = PositioningChunk.PositionType3D.Emitter;
                }
                positioning.Mode &= ~SpatializationMode.PositionOnly;
                positioning.Mode |= SpatializationMode.PositionAndOrientation |
                                    SpatializationMode.EnableAttenuation;
                if (enableDiffraction)
                {
                    positioning.Mode |= SpatializationMode.EnableDiffraction;
                }
            }
            else
            {
                positioning.Mode &= ~SpatializationMode.EnableAttenuation;
            }

            initialParams.ParamLength = checked((byte)initialParams.ParameterIds.Count);
        }
    }

    internal static bool CanEnsureMusicDuckingData(ME3Tweaks.Wwiser.WwiseBank bank)
    {
        if (bank.HIRC == null || bank.BKHD.BankGeneratorVersion != BankVersion)
        {
            return false;
        }

        var existingItem = bank.HIRC.Items.FirstOrDefault(item => item.Item.Id == MusicDuckingStateInstanceId);
        return existingItem == null || existingItem.Item is HierarchyState state && IsMusicDuckingState(state);
    }

    internal static bool EnsureMusicDuckingData(ME3Tweaks.Wwiser.WwiseBank bank)
    {
        if (!CanEnsureMusicDuckingData(bank))
        {
            return false;
        }

        if (bank.HIRC.Items.All(item => item.Item.Id != MusicDuckingStateInstanceId))
        {
            var serializer = new BinarySerializer();
            bank.HIRC.Items.Add(serializer.Deserialize<HircItemContainer>(
                Convert.FromBase64String(MusicDuckingStateHirc), BankSerializationContext.FromBank(bank)));
        }

        bank.HIRC.ItemCount = checked((uint)bank.HIRC.Items.Count);
        return true;
    }

    internal static bool HasMusicDuckingOnAllScopes(IReadOnlyCollection<IHasNode> scopes) =>
        scopes.Count > 0 && scopes.All(scope =>
            scope.NodeBaseParameters.StateChunk.GroupChunks.Any(IsMusicDuckingGroup));

    internal static void SetMusicDuckingOnScopes(IEnumerable<IHasNode> scopes, bool enabled)
    {
        foreach (var scope in scopes.Distinct())
        {
            var stateChunk = scope.NodeBaseParameters.StateChunk;
            var duckingGroup = stateChunk.GroupChunks.FirstOrDefault(group => group.Id == MusicDuckingStateGroupId);

            if (!enabled)
            {
                if (duckingGroup != null)
                {
                    duckingGroup.StateGroup.States.RemoveAll(state => state.Id == MusicDuckingStateId);
                    duckingGroup.StateGroup.StateCount.Value =
                        checked((uint)duckingGroup.StateGroup.States.Count);
                    if (duckingGroup.StateGroup.States.Count == 0)
                    {
                        stateChunk.GroupChunks.Remove(duckingGroup);
                    }
                }

                stateChunk.StateGroupsCount.Value = checked((uint)stateChunk.GroupChunks.Count);
                continue;
            }

            EnsureMusicDuckingPropertyInfo(stateChunk);
            if (duckingGroup == null)
            {
                duckingGroup = new StateGroupChunk
                {
                    Id = MusicDuckingStateGroupId,
                    StateGroup = new StateGroup
                    {
                        StateSyncType = new SyncType { Value = SyncType.SyncTypeInner.Immediate },
                        StateCount = new StateCount()
                    }
                };
                stateChunk.GroupChunks.Add(duckingGroup);
            }

            duckingGroup.StateGroup.StateSyncType.Value = SyncType.SyncTypeInner.Immediate;
            duckingGroup.StateGroup.States.RemoveAll(state => state.Id == MusicDuckingStateId);
            duckingGroup.StateGroup.States.Add(new StateEntry
            {
                Id = MusicDuckingStateId,
                StateInstanceId = MusicDuckingStateInstanceId
            });
            duckingGroup.StateGroup.StateCount.Value = checked((uint)duckingGroup.StateGroup.States.Count);
            stateChunk.StateGroupsCount.Value = checked((uint)stateChunk.GroupChunks.Count);
        }
    }

    internal static void SetHelmetRtpcOnScopes(IEnumerable<IHasNode> scopes, bool enabled)
    {
        SetHelmetRtpcOnScopes(scopes, HelmetRtpcId,
            [ParameterId.RtpcParameterId.BypassFX0], enabled);
    }

    internal static bool EnsureLe2MusicDuckingData(ME3Tweaks.Wwiser.WwiseBank bank,
        uint targetActorMixerId)
    {
        if (bank.HIRC == null || bank.BKHD.BankGeneratorVersion != BankVersion || targetActorMixerId == 0)
        {
            return false;
        }

        var serializer = new BinarySerializer();
        var context = BankSerializationContext.FromBank(bank);
        HircItemContainer[] shippedContainers =
        [
            serializer.Deserialize<HircItemContainer>(Convert.FromBase64String(Le2MusicDuckActionHirc), context),
            serializer.Deserialize<HircItemContainer>(Convert.FromBase64String(Le2MusicResetActionHirc), context),
            serializer.Deserialize<HircItemContainer>(Convert.FromBase64String(Le2MusicDuckEventHirc), context),
            serializer.Deserialize<HircItemContainer>(Convert.FromBase64String(Le2MusicResetEventHirc), context)
        ];

        foreach (var action in shippedContainers.Select(container => container.Item).OfType<HircAction>())
        {
            action.TargetId = targetActorMixerId;
        }

        foreach (var shippedContainer in shippedContainers)
        {
            var existingContainer = bank.HIRC.Items
                .FirstOrDefault(container => container.Item.Id == shippedContainer.Item.Id);
            if (existingContainer != null &&
                !SerializeHircContainer(serializer, context, existingContainer)
                    .SequenceEqual(SerializeHircContainer(serializer, context, shippedContainer)))
            {
                return false;
            }
        }

        foreach (var shippedContainer in shippedContainers)
        {
            if (bank.HIRC.Items.All(container => container.Item.Id != shippedContainer.Item.Id))
            {
                bank.HIRC.Items.Add(shippedContainer);
            }
        }

        bank.HIRC.ItemCount = checked((uint)bank.HIRC.Items.Count);
        return true;
    }

    internal static void SetLe2HelmetRtpcOnScopes(IEnumerable<IHasNode> scopes, bool enabled)
    {
        SetHelmetRtpcOnScopes(scopes, Le2HelmetRtpcId,
            [ParameterId.RtpcParameterId.BypassFX0, ParameterId.RtpcParameterId.BypassFX1], enabled);
    }

    private static void SetHelmetRtpcOnScopes(IEnumerable<IHasNode> scopes, uint rtpcId,
        IReadOnlyList<ParameterId.RtpcParameterId> bypassParameters, bool enabled)
    {
        foreach (var scope in scopes.Distinct())
        {
            var rtpcParameters = scope.NodeBaseParameters.Rtpc;
            rtpcParameters.Rtpcs.RemoveAll(rtpc => rtpc.RtpcId == rtpcId);
            if (enabled)
            {
                if (scope is not HircItem hircItem)
                {
                    throw new InvalidOperationException("The helmet RTPC can only be applied to Wwise HIRC audio nodes.");
                }

                for (int effectIndex = 0; effectIndex < bypassParameters.Count; effectIndex++)
                {
                    string curveName = rtpcId == HelmetRtpcId
                        ? $"{hircItem.Id:X8}_helmet_bypass_fx{effectIndex}"
                        : $"{hircItem.Id:X8}_{rtpcId:X8}_helmet_bypass_fx{effectIndex}";
                    rtpcParameters.Rtpcs.Add(new Rtpc
                    {
                        RtpcId = rtpcId,
                        RtpcType = new RtpcType(RtpcType.RtpcTypeInner.GameParameter),
                        RtpcAccum = new AccumType(AccumType.AccumTypeInner.Boolean),
                        ParamId = new ParameterId { ParamId = bypassParameters[effectIndex] },
                        RtpcCurveId = GenerateShortId(curveName),
                        RtpcConversionTable = new RtpcConversionTable
                        {
                            Scaling = new CurveScaling { Value = CurveScaling.CurveScalingInner.None },
                            GraphPointCount = new V36ShortCount { Value = 2 },
                            Graph =
                            [
                                new RtpcGraphItem { From = 0, To = 1, Interp = CurveInterpolation.Constant },
                                new RtpcGraphItem { From = 1, To = 0, Interp = CurveInterpolation.Constant }
                            ]
                        }
                    });
                }
            }

            rtpcParameters.RTPCCount.Value = checked((ushort)rtpcParameters.Rtpcs.Count);
        }
    }

    private static bool IsHelmetRtpc(Rtpc rtpc)
    {
        var graph = rtpc.RtpcConversionTable.Graph;
        return rtpc.RtpcId == HelmetRtpcId &&
               rtpc.RtpcType.Value == RtpcType.RtpcTypeInner.GameParameter &&
               rtpc.RtpcAccum.Value == AccumType.AccumTypeInner.Boolean &&
               rtpc.ParamId.ParamId == ParameterId.RtpcParameterId.BypassFX0 &&
               rtpc.RtpcConversionTable.Scaling.Value == CurveScaling.CurveScalingInner.None &&
               graph.Count == 2 &&
               graph[0].From == 0 && graph[0].To == 1 && graph[0].Interp == CurveInterpolation.Constant &&
               graph[1].From == 1 && graph[1].To == 0 && graph[1].Interp == CurveInterpolation.Constant;
    }

    private static uint GetRawParameterValue(InitialParamsV62.ParameterValue value) =>
        value.StoredAsFloat ? BitConverter.SingleToUInt32Bits(value.Float) : value.Integer;

    private static bool IsMusicDuckingState(HierarchyState state) =>
        state.Prop.PropIds.Count == 1 && state.Prop.PropValues.Count == 1 &&
        state.Prop.PropIds[0].ParamId == ParameterId.RtpcParameterId.Volume &&
        state.Prop.PropValues[0] == MusicDuckingVolumeDb;

    private static bool IsMusicDuckingGroup(StateGroupChunk group) =>
        group.Id == MusicDuckingStateGroupId && group.StateGroup.States.Any(state =>
            state.Id == MusicDuckingStateId && state.StateInstanceId == MusicDuckingStateInstanceId);

    private static void EnsureMusicDuckingPropertyInfo(StateChunk stateChunk)
    {
        (uint Id, bool InDb)[] shippedPropertyInfo =
        [
            (4, false),
            (3, false),
            (7, true),
            (2, false),
            (0, true)
        ];

        foreach (var (id, inDb) in shippedPropertyInfo)
        {
            var propertyInfo = stateChunk.PropertyInfo.FirstOrDefault(property => property.PropertyId.Value == id);
            if (propertyInfo == null)
            {
                propertyInfo = new StateProp { PropertyId = new VarCount { Value = id } };
                stateChunk.PropertyInfo.Add(propertyInfo);
            }

            propertyInfo.AccumType = new AccumType(AccumType.AccumTypeInner.Additive);
            propertyInfo.InDb = inDb;
        }

        stateChunk.StatePropsCount.Value = checked((uint)stateChunk.PropertyInfo.Count);
    }

    private static byte[] SerializeHircContainer(BinarySerializer serializer,
        BankSerializationContext context, HircItemContainer container)
    {
        using var stream = new System.IO.MemoryStream();
        serializer.Serialize(stream, container, context);
        return stream.ToArray();
    }

    private static uint GenerateShortId(string name)
    {
        uint hash = 2166136261;
        foreach (char character in name.ToLowerInvariant())
        {
            hash *= 16777619;
            hash ^= character;
        }
        return hash;
    }
}
