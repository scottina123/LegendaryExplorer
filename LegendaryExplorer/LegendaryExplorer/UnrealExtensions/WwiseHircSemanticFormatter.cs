using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LegendaryExplorerCore.Packages;
using ME3Tweaks.Wwiser.Model.Hierarchy;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using WwiserAction = ME3Tweaks.Wwiser.Model.Hierarchy.Action;

namespace LegendaryExplorer.UnrealExtensions;

public readonly record struct WwiseHircSemanticInfo(string TypeName, string Description = null);

/// <summary>
/// Produces user-facing HIRC labels from Wwiser's version-aware object types. The older
/// <see cref="LegendaryExplorerCore.Unreal.BinaryConverters.HIRCType"/> values cannot be used for this on
/// newer banks because Wwise removed the two motion/feedback types and reused their serialized values.
/// </summary>
internal static class WwiseHircSemanticFormatter
{
    internal static IReadOnlyDictionary<uint, WwiseHircSemanticInfo> BuildInfoById(
        IEnumerable<HircItemContainer> containers, MEGame? game = null) =>
        containers
            .GroupBy(container => container.Item.Id)
            .ToDictionary(group => group.Key, group => GetInfo(group.First(), game));

    internal static WwiseHircSemanticInfo GetInfo(HircItemContainer container, MEGame? game = null) =>
        GetInfo(container.Type.Value, container.Item, game);

    internal static WwiseHircSemanticInfo GetInfo(HircItem item, MEGame? game = null) =>
        GetInfo(item.HircType, item, game);

    internal static string GetTypeName(HircType type) => type switch
    {
        HircType.State => "State",
        HircType.Sound => "Sound SFX/Sound Voice",
        HircType.Action => "Event Action",
        HircType.Event => "Event",
        HircType.RandomSequenceContainer => "Random/Sequence Container",
        HircType.SwitchContainer => "Switch Container",
        HircType.ActorMixer => "Actor-Mixer",
        HircType.Bus => "Audio Bus",
        HircType.LayerContainer => "Blend Container",
        HircType.MusicSegment => "Music Segment",
        HircType.MusicTrack => "Music Track",
        HircType.MusicSwitch => "Music Switch Container",
        HircType.MusicRandomSequence => "Music Playlist Container",
        HircType.Attenuation => "Attenuation",
        HircType.DialogueEvent => "Dialogue Event",
        HircType.FeedbackBus => "Motion Bus / Feedback Bus",
        HircType.FeedbackNode => "Motion FX / Feedback Node",
        HircType.FxShareSet => "Effect ShareSet",
        HircType.FxCustom => "Custom Effect",
        HircType.AuxiliaryBus => "Auxiliary Bus",
        HircType.LFO => "LFO Modulator",
        HircType.Envelope => "Envelope Modulator",
        HircType.AudioDevice => "Audio Device",
        HircType.TimeMod => "Time Modulator",
        _ => $"Unknown HIRC Type (0x{(uint)type:X2})"
    };

    internal static string GetKnownEffectName(uint id) => id switch
    {
        WwiseBankEffectPresets.FactoryRadioEffectId => "Dual Filters Radio Comm",
        WwiseBankEffectPresets.HelmetFilterEffectId => "BioWare FutzBox / Helmet Filter",
        WwiseBankEffectPresets.BioWareRadioEqEffectId => "BioWare Radio EQ",
        WwiseBankEffectPresets.QecFutzBoxEffectId => "Hackett QEC FutzBox",
        WwiseBankEffectPresets.QecFlangerEffectId => "Hackett QEC Flanger",
        WwiseBankEffectPresets.Le2HelmetEqEffectId => "LE2 Helmet EQ",
        WwiseBankEffectPresets.Le2HelmetFilterEffectId => "LE2 Helmet Filter",
        WwiseBankEffectPresets.Le2RadioEqEffectId => "LE2 Radio EQ",
        WwiseBankEffectPresets.Le2RadioFilterEffectId => "LE2 Radio Filter",
        WwiseBankEffectPresets.Le2HologramEqEffectId => "Illusive Man Hologram EQ",
        WwiseBankEffectPresets.Le2HologramFilterEffectId => "Illusive Man Hologram Filter",
        _ => null
    };

    private static WwiseHircSemanticInfo GetInfo(HircType type, HircItem item, MEGame? game)
    {
        string objectDescription = item switch
        {
            FxBase effect => GetKnownEffectName(effect.Id) is { } effectName
                ? effectName
                : $"Effect plug-in 0x{effect.Plugin.PluginId:X8}",
            WwiserAction action => HumanizeIdentifier(action.Type.Value.ToString()),
            ActorMixer actorMixer => FormatCount(actorMixer.Children.ChildrenValues.Count, "child", "children"),
            RandSeqContainer container =>
                $"{HumanizeIdentifier(container.Mode.ToString())}; " +
                FormatCount(container.Children.ChildrenValues.Count, "child", "children"),
            SwitchContainer container =>
                $"{HumanizeIdentifier(container.GroupType.Value.ToString())} group 0x{container.GroupId:X8}; " +
                FormatCount(container.Children.ChildrenValues.Count, "child", "children"),
            LayerContainer container =>
                $"{FormatCount(container.Children.ChildrenValues.Count, "child", "children")}; " +
                FormatCount(container.Layers.Count, "layer", "layers"),
            Attenuation attenuation =>
                $"{FormatCount(attenuation.Curves.Count, "curve", "curves")}; " +
                (attenuation.IsConeEnabled ? "directional cone enabled" : "no directional cone"),
            Event hircEvent => FormatCount(hircEvent.ActionIds.Count, "action", "actions"),
            _ => null
        };

        var descriptions = new List<string>();
        if (!string.IsNullOrWhiteSpace(objectDescription))
        {
            descriptions.Add(objectDescription);
        }

        if (item is IHasNode parameterNode)
        {
            NodeBaseParameters parameters = parameterNode.NodeBaseParameters;
            if (parameters.OverrideBusId != 0)
            {
                string busName = game.HasValue
                    ? WwiseOutputBusOptions.GetOutputBusName(game.Value, parameters.OverrideBusId)
                    : null;
                descriptions.Add(busName == null
                    ? $"Output bus: 0x{parameters.OverrideBusId:X8}"
                    : $"Output bus: {busName}");
            }

            if (parameters.FxParams.FxChunks.Count > 0)
            {
                string effects = string.Join(" + ", parameters.FxParams.FxChunks
                    .OrderBy(effect => effect.FxIndex)
                    .Select(effect => GetKnownEffectName(effect.Id) ?? $"0x{effect.Id:X8}"));
                descriptions.Add($"Effects: {effects}");
            }
            else if (parameters.FxParams.IsOverrideParentFx)
            {
                descriptions.Add("Effects: none (overrides parent)");
            }

            AddInitialParameterDescription(parameters, PropId.Volume, "Volume", " dB", descriptions);
            AddInitialParameterDescription(parameters, PropId.Pitch, "Pitch", " cents", descriptions);
            AddInitialParameterDescription(parameters, PropId.AttenuationID, "Attenuation", null,
                descriptions, formatAsId: true);
        }

        return new WwiseHircSemanticInfo(GetTypeName(type),
            descriptions.Count == 0 ? null : string.Join(Environment.NewLine, descriptions));
    }

    private static void AddInitialParameterDescription(NodeBaseParameters parameters, PropId property,
        string label, string suffix, ICollection<string> descriptions, bool formatAsId = false)
    {
        int index = parameters.InitialParams62.ParameterIds.FindIndex(id => id.PropValue == property);
        if (index < 0 || index >= parameters.InitialParams62.ParameterValues.Count)
        {
            return;
        }

        var value = parameters.InitialParams62.ParameterValues[index];
        if (formatAsId)
        {
            uint id = value.StoredAsFloat ? BitConverter.SingleToUInt32Bits(value.Float) : value.Integer;
            descriptions.Add($"{label}: 0x{id:X8}");
        }
        else
        {
            descriptions.Add($"{label}: {value.Value:0.##}{suffix}");
        }
    }

    private static string FormatCount(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    private static string HumanizeIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (i > 0 && (char.IsUpper(current) || char.IsDigit(current)) &&
                !char.IsWhiteSpace(value[i - 1]) && !char.IsDigit(value[i - 1]))
            {
                result.Append(' ');
            }
            result.Append(current);
        }
        return result.ToString()
            .Replace("R T P C", "RTPC", StringComparison.Ordinal)
            .Replace("L P F", "LPF", StringComparison.Ordinal)
            .Replace("H P F", "HPF", StringComparison.Ordinal)
            .Replace("L F E", "LFE", StringComparison.Ordinal)
            .Replace("F X", "FX", StringComparison.Ordinal);
    }
}
