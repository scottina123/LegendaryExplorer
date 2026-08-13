using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners
{
    internal class ConversationScanner : AssetScanner
    {
        private static readonly HashSet<string> StartConversationClasses =
        [
            "BioSeqAct_StartConversation",
            "SFXSeqAct_StartConversation",
            "SFXSeqAct_StartAmbientConv"
        ];

        public ConversationScanner() : base()
        {
        }

        public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
        {
            if (e.IsDefault) return;

            if (StartConversationClasses.Contains(e.ClassName))
            {
                ScanConversationOwnerMetadata(e, db);
            }

            if (e.ClassName == "BioConversation")
            {
                bool IsAmbient = true;

                var speakers = GetSpeakers(e.Export, e.Properties);

                var entryprop = e.Properties.GetProp<ArrayProperty<StructProperty>>("m_EntryList");
                foreach (StructProperty Node in entryprop)
                {
                    int speakerindex = Node.GetProp<IntProperty>("nSpeakerIndex");
                    speakerindex = speakerindex + 2;
                    if (speakerindex < 0 || speakerindex >= speakers.Count)
                        continue;
                    int linestrref = 0;
                    var linestrrefprop = Node.GetProp<StringRefProperty>("srText");
                    if (linestrrefprop != null)
                    {
                        linestrref = linestrrefprop.Value;
                    }

                    var ambientLine = Node.GetProp<BoolProperty>("IsAmbient");
                    if (IsAmbient)
                        IsAmbient = ambientLine;

                    var newLine = new ConvoLine(linestrref, speakers[speakerindex], e.Export.ObjectName.Instanced);
                    if (HasTLKLine(newLine, e.Export.FileRef))
                    {
                        db.GeneratedLines.TryAdd(linestrref.ToString(), newLine);
                    }
                }

                var replyprop = e.Properties.GetProp<ArrayProperty<StructProperty>>("m_ReplyList");
                if (replyprop != null)
                {
                    foreach (StructProperty Node in replyprop)
                    {
                        int linestrref = 0;
                        var linestrrefprop = Node.GetProp<StringRefProperty>("srText");
                        if (linestrrefprop != null)
                        {
                            linestrref = linestrrefprop.Value;
                        }

                        var ambientLine = Node.GetProp<BoolProperty>("IsAmbient");
                        if (IsAmbient)
                            IsAmbient = ambientLine;

                        ConvoLine newLine = new(linestrref, "Shepard", e.Export.ObjectName.Instanced);
                        if (HasTLKLine(newLine, e.Export.FileRef))
                        {
                            db.GeneratedLines.TryAdd(linestrref.ToString(), newLine);
                        }
                    }
                }

                var stageDirections = GetStageDirections(e.Properties);
                var newConv = new Conversation(e.Export.ObjectName.Instanced,
                                               IsAmbient,
                                               new FileKeyExportPair(e.FileKey, e.Export.UIndex),
                                               stageDirections: stageDirections);
                db.GeneratedConvo.AddOrUpdate(GetConversationLookupKey(e.Export.ObjectName.Instanced),
                                             _ => newConv,
                                             (_, existing) =>
                                             {
                                                 existing.ConvName = newConv.ConvName;
                                                 existing.IsAmbient = newConv.IsAmbient;
                                                 existing.ConvFile = newConv.ConvFile;
                                                 existing.StageDirections = newConv.StageDirections;
                                                 return existing;
                                             });
            }
        }

        internal static List<ConversationStageDirection> GetStageDirections(PropertyCollection properties)
        {
            var stageDirections = new List<ConversationStageDirection>();
            var stageDirectionProperties = properties.GetProp<ArrayProperty<StructProperty>>("m_aStageDirections");
            if (stageDirectionProperties == null)
            {
                return stageDirections;
            }

            foreach (var stageDirection in stageDirectionProperties)
            {
                int strRef = stageDirection.GetProp<StringRefProperty>("srStrRef")?.Value ?? 0;
                string text = stageDirection.GetProp<StrProperty>("sText")?.Value;
                stageDirections.Add(new ConversationStageDirection(strRef, text));
            }

            return stageDirections;
        }

        private static void ScanConversationOwnerMetadata(ExportScanInfo e, ConcurrentAssetDB db)
        {
            var convProp = e.Properties.GetProp<ObjectProperty>("Conv");
            if (convProp == null || convProp.Value == 0)
            {
                return;
            }

            var convEntry = e.Export.FileRef.GetEntry(convProp.Value);
            if (convEntry == null)
            {
                return;
            }

            var ownerObjectRef = GetFirstLinkedVariableIndex(e.Properties, "Owner");
            var conversationName = convEntry.ObjectName.Instanced;
            var packageName = e.FileName;
            var exportIndex = e.Export.UIndex;
            var ownerFriendlyName = ResolveFriendlyOwnerName(e.Export.FileRef, ownerObjectRef);

            db.GeneratedConvo.AddOrUpdate(GetConversationLookupKey(conversationName),
                                         _ => new Conversation(conversationName,
                                                               IsAmbient: false,
                                                               ConvFile: new FileKeyExportPair(-1, 0),
                                                               packageName,
                                                               exportIndex,
                                                                ownerObjectRef,
                                                                ownerFriendlyName),
                                         (_, existing) =>
                                         {
                                             if (string.IsNullOrWhiteSpace(existing.PackageName))
                                             {
                                                 existing.PackageName = packageName;
                                             }

                                             if (existing.ConversationExportIndex <= 0)
                                             {
                                                 existing.ConversationExportIndex = exportIndex;
                                             }

                                             if (existing.OwnerObjectRef == 0)
                                             {
                                                 existing.OwnerObjectRef = ownerObjectRef;
                                             }

                                              if (string.IsNullOrWhiteSpace(existing.OwnerFriendlyName))
                                              {
                                                  existing.OwnerFriendlyName = ownerFriendlyName;
                                              }

                                             return existing;
                                         });
        }

        private static string ResolveFriendlyOwnerName(IMEPackage package, int ownerObjectRef)
        {
            if (package == null || ownerObjectRef <= 0 || !package.TryGetUExport(ownerObjectRef, out var ownerVar))
            {
                return null;
            }

            switch (ownerVar.ClassName)
            {
                case "SeqVar_Object":
                {
                    var objValue = ownerVar.GetProperty<ObjectProperty>("ObjValue");
                    if (objValue == null || objValue.Value <= 0 || !package.TryGetEntry(objValue.Value, out var actorEntry))
                    {
                        return null;
                    }

                    if (actorEntry is ExportEntry actorExport)
                    {
                        var actorTag = actorExport.GetProperty<NameProperty>("Tag")?.Value.Instanced;
                        if (!string.IsNullOrWhiteSpace(actorTag))
                        {
                            return actorTag;
                        }

                        if (actorExport.HasArchetype && actorExport.Archetype is ExportEntry archetype)
                        {
                            var archetypeTag = archetype.GetProperty<NameProperty>("Tag")?.Value.Instanced;
                            if (!string.IsNullOrWhiteSpace(archetypeTag))
                            {
                                return archetypeTag;
                            }
                        }
                    }

                    return actorEntry.ObjectName.Instanced;
                }
                case "BioSeqVar_ObjectFindByTag":
                {
                    var tagProperty = ownerVar.GetProperty<Property>("m_sObjectTagToFind");
                    var tagName = tagProperty switch
                    {
                        NameProperty nameProperty => nameProperty.Value.Instanced,
                        StrProperty strProperty => strProperty.Value,
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(tagName))
                    {
                        return tagName;
                    }

                    return null;
                }
                default:
                    return ownerVar.ObjectName.Instanced;
            }
        }

        private static int GetFirstLinkedVariableIndex(PropertyCollection props, params string[] linkDescriptions)
        {
            var links = props.GetProp<ArrayProperty<StructProperty>>("VariableLinks");
            if (links == null)
            {
                return 0;
            }

            foreach (var linkDescription in linkDescriptions)
            {
                foreach (var link in links)
                {
                    var desc = link.GetProp<StrProperty>("LinkDesc");
                    if (!string.Equals(desc?.Value, linkDescription, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var linkedVars = link.GetProp<ArrayProperty<ObjectProperty>>("LinkedVariables");
                    if (linkedVars is { Count: > 0 })
                    {
                        return linkedVars[0].Value;
                    }
                }
            }

            return 0;
        }

        private static string GetConversationLookupKey(string conversationName) => conversationName?.ToLowerInvariant() ?? string.Empty;

        private List<string> GetSpeakers(ExportEntry export, PropertyCollection props)
        {
            var speakers = new List<string> { "Shepard", "Owner" };
            if (!export.Game.IsGame3())
            {
                var s_speakers = props.GetProp<ArrayProperty<StructProperty>>("m_SpeakerList");
                if (s_speakers != null)
                {
                    speakers.AddRange(s_speakers.Select(t => t.GetProp<NameProperty>("sSpeakerTag").ToString()));
                }
            }
            else
            {
                var a_speakers = props.GetProp<ArrayProperty<NameProperty>>("m_aSpeakerList");
                if (a_speakers != null)
                {
                    foreach (NameProperty n in a_speakers)
                    {
                        speakers.Add(n.ToString());
                    }
                }
            }

            return speakers;
        }

        /// <summary>
        /// If game one, sets the ConvoLine line to resolved TLK string. Returns false if not possible
        /// </summary>
        /// <param name="line"></param>
        /// <param name="fileref"></param>
        /// <returns></returns>
        private static bool HasTLKLine(ConvoLine line, IMEPackage fileref)
        {
            if (fileref.Game == MEGame.ME1)
            {
                line.Line = ME1TalkFiles.FindDataById(line.StrRef, fileref);
                if (line.Line is "No Data" or "\"\"" or "\" \"" or " ")
                    return false;
            }
            else if (fileref.Game == MEGame.LE1)
            {
                line.Line = LE1TalkFiles.FindDataById(line.StrRef, fileref);
                if (line.Line is "No Data" or "\"\"" or "\" \"" or " ")
                    return false;
            }
            return true;
        }
    }
}
