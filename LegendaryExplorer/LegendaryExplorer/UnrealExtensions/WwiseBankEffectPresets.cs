using System;
using System.Collections.Generic;
using System.Linq;
using BinarySerialization;
using ME3Tweaks.Wwiser;
using ME3Tweaks.Wwiser.Model.Hierarchy;

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
}
