using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BinaryPack;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase
{
    internal static class ConversationOwnerFriendlyNameResolver
    {
        private static readonly object OwnerFriendlyNameCacheLock = new();
        private static readonly Dictionary<MEGame, Dictionary<string, string>> OwnerFriendlyNameCache = [];

        public static string GetConversationOwnerFriendlyName(MEGame game, string conversationName)
        {
            if (!Settings.Global_UseOwnerFriendlyNames)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(conversationName))
            {
                return null;
            }

            Dictionary<string, string> ownerFriendlyNames;
            lock (OwnerFriendlyNameCacheLock)
            {
                if (!OwnerFriendlyNameCache.TryGetValue(game, out ownerFriendlyNames))
                {
                    ownerFriendlyNames = LoadOwnerFriendlyNames(game);
                    OwnerFriendlyNameCache[game] = ownerFriendlyNames;
                }
            }

            return ownerFriendlyNames.TryGetValue(conversationName, out var ownerFriendlyName)
                ? ownerFriendlyName
                : null;
        }

        public static string GetConversationOwnerDisplayName(MEGame game, string conversationName, string ownerName = "owner")
        {
            var ownerFriendlyName = GetConversationOwnerFriendlyName(game, conversationName);
            return string.IsNullOrWhiteSpace(ownerFriendlyName)
                ? ownerName
                : $"{ownerName} ({ownerFriendlyName})";
        }

        public static string GetConversationOwnerDisplayName(ExportEntry conversationExport, string ownerName = "owner")
        {
            if (conversationExport?.ClassName != "BioConversation")
            {
                return ownerName;
            }

            return GetConversationOwnerDisplayName(conversationExport.Game, conversationExport.ObjectName.Instanced, ownerName);
        }

        public static string GetConversationSpeakerDisplay(ExportEntry conversationExport, int speakerIndex)
        {
            if (conversationExport?.ClassName != "BioConversation")
            {
                return null;
            }

            return speakerIndex switch
            {
                -3 => "None",
                -2 => "player",
                -1 => GetConversationOwnerDisplayName(conversationExport),
                >= 0 => GetSpeakerName(conversationExport, speakerIndex),
                _ => null
            };
        }

        public static string GetEntryDisplayText(IEntry entry)
        {
            if (entry is ExportEntry { ClassName: "BioConversation" } conversationExport)
            {
                var ownerFriendlyName = GetConversationOwnerFriendlyName(conversationExport.Game, conversationExport.ObjectName.Instanced);
                if (!string.IsNullOrWhiteSpace(ownerFriendlyName))
                {
                    return $"{conversationExport.InstancedFullPath} [owner ({ownerFriendlyName})]";
                }
            }

            return entry?.InstancedFullPath;
        }

        private static string GetSpeakerName(ExportEntry conversationExport, int speakerIndex)
        {
            if (speakerIndex < 0)
            {
                return null;
            }

            if (!conversationExport.FileRef.Game.IsGame3())
            {
                var speakers = conversationExport.GetProperty<ArrayProperty<StructProperty>>("m_SpeakerList");
                if (speakers != null && speakerIndex < speakers.Count)
                {
                    return speakers[speakerIndex].GetProp<NameProperty>("sSpeakerTag")?.Value.Instanced;
                }
            }
            else
            {
                var speakers = conversationExport.GetProperty<ArrayProperty<NameProperty>>("m_aSpeakerList");
                if (speakers != null && speakerIndex < speakers.Count)
                {
                    return speakers[speakerIndex].Value.Instanced;
                }
            }

            return null;
        }

        private static Dictionary<string, string> LoadOwnerFriendlyNames(MEGame game)
        {
            var ownerFriendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var databasePath = AssetDatabaseWindow.GetDBPath(game);
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            {
                return ownerFriendlyNames;
            }

            string build = AssetDatabaseWindow.dbCurrentBuild.Trim(' ', '*', '.');
            string entryName = $"MasterDB.{game}_{build}.bin";

            try
            {
                using ZipArchive archive = new(new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (archive.GetEntry(entryName) is not ZipArchiveEntry entry)
                {
                    return ownerFriendlyNames;
                }

                using var entryStream = entry.Open();
                using var memoryStream = new MemoryStream((int)entry.Length);
                entryStream.CopyTo(memoryStream);
                var database = BinaryConverter.Deserialize<AssetDB>(memoryStream.GetBuffer().AsSpan(0, (int)memoryStream.Length));
                if (database?.Conversations == null)
                {
                    return ownerFriendlyNames;
                }

                foreach (var conversation in database.Conversations)
                {
                    if (!string.IsNullOrWhiteSpace(conversation?.ConvName)
                        && !string.IsNullOrWhiteSpace(conversation.OwnerFriendlyName)
                        && !ownerFriendlyNames.ContainsKey(conversation.ConvName))
                    {
                        ownerFriendlyNames[conversation.ConvName] = conversation.OwnerFriendlyName;
                    }
                }
            }
            catch
            {
                return ownerFriendlyNames;
            }

            return ownerFriendlyNames;
        }
    }
}
