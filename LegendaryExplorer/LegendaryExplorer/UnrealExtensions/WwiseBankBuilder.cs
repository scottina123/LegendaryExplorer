using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using BinarySerialization;
using LegendaryExplorerCore.Packages;
using ME3Tweaks.Wwiser;
using ME3Tweaks.Wwiser.Model;
using ME3Tweaks.Wwiser.Model.Hierarchy;
using ME3Tweaks.Wwiser.Model.Hierarchy.Enums;
using ME3Tweaks.Wwiser.Model.ParameterNode;
using HircAction = ME3Tweaks.Wwiser.Model.Hierarchy.Action;
using HircEvent = ME3Tweaks.Wwiser.Model.Hierarchy.Event;
using WwiseBank = ME3Tweaks.Wwiser.WwiseBank;

namespace LegendaryExplorer.UnrealExtensions;

/// <summary>
/// Builds streamed LE soundbanks from the hierarchy descriptions used by the bulk importer.
/// The version-134 node defaults come from the generated Test_Bank.bnk in WwiseTestData/LEX Test LE2.
/// No authoring project or Wwise executable is needed at runtime.
/// </summary>
internal static class WwiseBankBuilder
{
    private const string SoundTemplate = "AiwAAAC5w3QnAQAEAACk+ZALqBYAAAAAAAAAAAAAdESiHgAAAAAAAAEAAAAAAAAAAA==";
    private const string ActorMixerTemplate = "BysAAAB0RKIeAAAA0CKROwAAAAAAAAADCAAAAQAAAAAAAAAAAgAAACN8CBK5w3Qn";
    private const string PlayActionTemplate = "AxIAAADiy7g3AwS5w3QnAAAABBb684k=";
    private const string StopActionTemplate = "AxAAAADjMU8WAwG5w3QnAAAABAYA";

    internal static uint GenerateId(string name)
    {
        uint hash = 2166136261;
        foreach (byte character in Encoding.UTF8.GetBytes(name))
        {
            // The SDK folds ASCII capitals on the UTF-8 byte sequence; Unicode bytes
            // remain unchanged, unlike applying ToLowerInvariant to UTF-16 characters.
            byte folded = character is >= (byte)'A' and <= (byte)'Z'
                ? (byte)(character + ('a' - 'A'))
                : character;
            hash = unchecked(hash * 16777619) ^ folded;
        }
        return hash;
    }

    // Event-style LE2 input names and the shared Stop event must retain their public event
    // IDs. Give the corresponding Sound a separate ID because HIRC IDs share one namespace.
    internal static uint GenerateSoundId(MEGame game, string name) =>
        name.Equals("Stop", StringComparison.OrdinalIgnoreCase) ||
        game == MEGame.LE2 && name.EndsWith("_Play", StringComparison.OrdinalIgnoreCase)
            ? GenerateId(name + "_Sound")
            : GenerateId(name);

    internal static WwiseBank Build(MEGame game, string bankName, XDocument actorMixerDocument,
        XDocument eventsDocument, IReadOnlyDictionary<string, uint> sourceIds)
    {
        if (game is not (MEGame.LE2 or MEGame.LE3))
        {
            throw new NotSupportedException("Soundbank creation supports LE2 and LE3.");
        }

        var bank = new WwiseBank
        {
            BKHD = new BankHeaderChunk
            {
                BankGeneratorVersion = WwiseBankEffectPresets.BankVersion,
                SoundBankId = GenerateId(bankName),
                LanguageIdStringHash = GenerateId("SFX")
            },
            HIRC = new HierarchyChunk { Items = [] }
        };
        var context = BankSerializationContext.FromBank(bank);
        var serializer = new BinarySerializer();
        HircItemContainer Clone(string template) => serializer.Deserialize<HircItemContainer>(
            Convert.FromBase64String(template), context);

        var actorElement = actorMixerDocument.Descendants("ActorMixer").Single();
        var actorContainer = Clone(ActorMixerTemplate);
        var actorMixer = (ActorMixer)actorContainer.Item;
        var eventIds = eventsDocument.Descendants("Event")
            .Select(element => GenerateId((string)element.Attribute("Name"))).ToHashSet();
        var soundIds = actorElement.Descendants("Sound")
            .Select(element => GenerateSoundId(game, (string)element.Attribute("Name"))).ToHashSet();
        actorMixer.Id = GenerateId((string)actorElement.Attribute("Name"));
        for (int suffix = 0; eventIds.Contains(actorMixer.Id) || soundIds.Contains(actorMixer.Id); suffix++)
        {
            actorMixer.Id = GenerateId($"{bankName}_{suffix}_ActorMixer");
        }
        actorMixer.NodeBaseParameters.DirectParentId = 0;
        var busName = actorElement.Element("ReferenceList")?.Elements("Reference")
            .Single(reference => (string)reference.Attribute("Name") == "OutputBus")
            .Element("ObjectRef")?.Attribute("Name")?.Value ?? "Master Audio Bus";
        actorMixer.NodeBaseParameters.OverrideBusId = GenerateId(busName);
        actorMixer.NodeBaseParameters.AuxParams.Unk1 = true;
        actorMixer.NodeBaseParameters.AuxParams.Unk2 = true;
        var volume = actorElement.Descendants("Property")
            .Single(property => (string)property.Attribute("Name") == "Volume")
            .Descendants("Value").Single().Value;
        actorMixer.NodeBaseParameters.InitialParams62 = new InitialParamsV62();
        actorMixer.NodeBaseParameters.InitialParams62.AddParameter(PropId.Volume,
            new InitialParamsV62.ParameterValue
            {
                Float = float.Parse(volume, CultureInfo.InvariantCulture), StoredAsFloat = true
            });
        actorMixer.NodeBaseParameters.InitialParams62.ParamLength = 1;
        actorMixer.Children.ChildrenValues = [];

        var usedIds = new HashSet<uint> { actorMixer.Id };
        var sounds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var soundElement in actorElement.Descendants("Sound"))
        {
            string name = (string)soundElement.Attribute("Name");
            uint soundId = GenerateSoundId(game, name);
            if (!usedIds.Add(soundId))
            {
                throw new InvalidOperationException($"Audio object '{name}' has a duplicate soundbank ID. Rename the input or bank.");
            }
            var soundContainer = Clone(SoundTemplate);
            var sound = (Sound)soundContainer.Item;
            sound.Id = soundId;
            sound.NodeBaseParameters.DirectParentId = actorMixer.Id;
            sound.NodeBaseParameters.OverrideBusId = 0;
            sound.BankSourceData.Plugin.PluginId = 0x00040001;
            sound.BankSourceData.StreamType.Value = StreamType.StreamTypeInner.Streaming;
            string sourceName = Path.GetFileNameWithoutExtension(soundElement.Descendants("AudioFile").Single().Value);
            sound.BankSourceData.MediaInformation.SourceId = sourceIds[sourceName];
            sound.BankSourceData.MediaInformation.InMemoryMediaSize = 0;
            sound.BankSourceData.MediaInformation.Flags = 0;
            if (soundElement.Descendants("Property").Any(property =>
                    (string)property.Attribute("Name") == "IsLoopingEnabled"))
            {
                sound.NodeBaseParameters.InitialParams62.AddParameter(PropId.Loop,
                    new InitialParamsV62.ParameterValue { Integer = 0, StoredAsFloat = false });
                sound.NodeBaseParameters.InitialParams62.ParamLength =
                    checked((byte)sound.NodeBaseParameters.InitialParams62.ParameterIds.Count);
            }
            sounds.Add(name, soundId);
            actorMixer.Children.ChildrenValues.Add(soundId);
            bank.HIRC.Items.Add(soundContainer);
        }
        actorMixer.Children.ChildrenCount = checked((uint)actorMixer.Children.ChildrenValues.Count);
        // The runtime resolves an ActorMixer's children while loading that HIRC item.
        // Its child Sounds must already exist; emitting the mixer first makes the
        // otherwise readable bank fail native LoadBank with AK_IDNotFound.
        bank.HIRC.Items.Add(actorContainer);

        foreach (var eventElement in eventsDocument.Descendants("Event"))
        {
            string name = (string)eventElement.Attribute("Name");
            uint eventId = GenerateId(name);
            if (!usedIds.Add(eventId))
            {
                throw new InvalidOperationException($"Event '{name}' has a duplicate soundbank ID. Rename the input or bank.");
            }
            var actionIds = new List<uint>();
            int actionIndex = 0;
            foreach (var actionElement in eventElement.Descendants("Action"))
            {
                bool stop = actionElement.Descendants("Property")
                    .Any(property => (string)property.Attribute("Name") == "ActionType" &&
                                     (string)property.Attribute("Value") == "2");
                uint actionId;
                do
                {
                    actionId = GenerateId($"{name}_{++actionIndex}_Action");
                } while (eventIds.Contains(actionId) || !usedIds.Add(actionId));
                var actionContainer = Clone(stop ? StopActionTemplate : PlayActionTemplate);
                var action = (HircAction)actionContainer.Item;
                action.Id = actionId;
                action.TargetId = sounds[(string)actionElement.Descendants("ObjectRef").Single().Attribute("Name")];
                if (!stop)
                {
                    action.BankData.BankId = bank.BKHD.SoundBankId;
                }
                bank.HIRC.Items.Add(actionContainer);
                actionIds.Add(actionId);
            }
            bank.HIRC.Items.Add(new HircItemContainer
            {
                Type = new HircSmartType { Value = HircType.Event },
                Item = new HircEvent { Id = eventId, ActionIds = actionIds }
            });
        }
        bank.HIRC.ItemCount = checked((uint)bank.HIRC.Items.Count);
        return bank;
    }
}
