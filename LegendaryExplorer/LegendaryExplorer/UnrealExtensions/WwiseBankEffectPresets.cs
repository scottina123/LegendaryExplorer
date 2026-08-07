using System;
using System.Collections.Generic;
using System.Linq;
using BinarySerialization;
using ME3Tweaks.Wwiser;
using ME3Tweaks.Wwiser.Formats;
using ME3Tweaks.Wwiser.Model.Hierarchy;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.RTPC;

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

    internal static IReadOnlyList<WwiseBankEffect> FactoryRadio { get; } =
    [
        new(FactoryRadioEffectId, 0x00690003u,
            "EEsAAAACigCwAwBpADgAAAABAAAAAAAAAAAASEQAAIA/AQYAAAAAAJBBAIA7RQAAgD8BAAAAAAAAAAAAoJFFAACAPwEAAEDBAQAAAAAAAAA=")
    ];

    private static WwiseBankEffect BioWareFutzBox { get; } =
        new(HelmetFilterEffectId, 0x006E1003u,
            "EJ4AAAAIu3cHAxBuAIsAAAAAAAAAAADgEkYAAAAAAQAAAAAAlkMAAAAAAQQAAAAAAPBBAAAAAAAAAAAAAQAAAAAAekQAAAAAAAAAAAAAAPjBAGAuRQAAekQAAAAAAAAgwgAAIEEBEgAAAAAAyEIAAACgwQAAoMLNzMw9AAAgQQAAIEEACAAAAAQAAAAAAAAAAAAAAAAAoEAAAMhCAAAAAAAAAA==");

    internal static IReadOnlyList<WwiseBankEffect> HelmetFilter { get; } = [BioWareFutzBox];

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

    internal static void SetHelmetRtpcOnScopes(IEnumerable<IHasNode> scopes, bool enabled)
    {
        foreach (var scope in scopes.Distinct())
        {
            var rtpcParameters = scope.NodeBaseParameters.Rtpc;
            rtpcParameters.Rtpcs.RemoveAll(rtpc => rtpc.RtpcId == HelmetRtpcId);
            if (enabled)
            {
                if (scope is not HircItem hircItem)
                {
                    throw new InvalidOperationException("The helmet RTPC can only be applied to Wwise HIRC audio nodes.");
                }

                rtpcParameters.Rtpcs.Add(new Rtpc
                {
                    RtpcId = HelmetRtpcId,
                    RtpcType = new RtpcType(RtpcType.RtpcTypeInner.GameParameter),
                    RtpcAccum = new AccumType(AccumType.AccumTypeInner.Boolean),
                    ParamId = new ParameterId { ParamId = ParameterId.RtpcParameterId.BypassFX0 },
                    RtpcCurveId = GenerateShortId($"{hircItem.Id:X8}_helmet_bypass_fx0"),
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
