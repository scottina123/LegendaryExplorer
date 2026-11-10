using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.CutsceneCorpus
{
    /// <summary>
    /// Pass 1: extracts conversations, node timelines, stage contexts, stages and animation lengths from one package.
    /// </summary>
    internal static class CorpusFileExtractor
    {
        internal static readonly HashSet<string> RelevantClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "BioConversation", "BioStage", "BioSeqAct_StartConversation", "SFXSeqAct_StartConversation", "SFXSeqAct_StartAmbientConv"
        };

        public static void Extract(IMEPackage pcc, CorpusFileResult result, Func<int, string> tlk)
        {
            ExtractConversations(pcc, result, tlk);
            ExtractStages(pcc, result);
            ExtractLevelCameras(pcc, result);
            ExtractAnimations(pcc, result);
        }

        private static void ExtractLevelCameras(IMEPackage pcc, CorpusFileResult result)
        {
            foreach (ExportEntry export in pcc.Exports.Where(export => !export.IsDefaultObject
                                                                       && export.ClassName.EndsWith("CameraActor", StringComparison.Ordinal)
                                                                       && export.InstancedFullPath.Contains("PersistentLevel", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    PropertyCollection properties = export.GetProperties();
                    StructProperty location = properties.GetProp<StructProperty>("location") ?? properties.GetProp<StructProperty>("Location");
                    StructProperty rotation = properties.GetProp<StructProperty>("Rotation");
                    result.LevelCameras.Add(new LevelCameraRecord
                    {
                        File = result.FileName,
                        Path = export.InstancedFullPath,
                        Class = export.ClassName,
                        Tag = properties.GetProp<NameProperty>("Tag")?.Value.Instanced ?? export.ObjectName.Instanced,
                        Origin = ToOrigin(new CameraOrigin(
                            location is null ? Vector3.Zero : CommonStructs.GetVector3(location),
                            rotation is null ? Vector3.Zero : CommonStructs.GetRotator(rotation).GetDegreesVector())),
                        Fov = properties.GetProp<FloatProperty>("FOVAngle")?.Value is { } fov ? CorpusPropertyJson.Round(fov, 2) : null
                    });
                }
                catch (Exception exception)
                {
                    result.Errors.Add($"{result.FileName} {export.InstancedFullPath}: camera actor failed: {exception.Message}");
                }
            }
        }

        #region Conversations

        private static void ExtractConversations(IMEPackage pcc, CorpusFileResult result, Func<int, string> tlk)
        {
            foreach (ExportEntry export in pcc.Exports.Where(export => export.ClassName == "BioConversation" && !export.IsDefaultObject))
            {
                try
                {
                    result.Conversations.Add(ExtractConversation(export, result, tlk));
                }
                catch (Exception exception)
                {
                    result.Errors.Add($"{result.FileName} {export.InstancedFullPath}: conversation failed: {exception.Message}");
                }
            }
        }

        private static ConversationRecord ExtractConversation(ExportEntry export, CorpusFileResult result, Func<int, string> tlk)
        {
            var conversation = new ConversationExtended(export);
            try
            {
                conversation.LoadConversation();
            }
            catch (Exception exception)
            {
                result.Errors.Add($"{result.FileName} {export.InstancedFullPath}: LoadConversation: {exception.Message}");
            }

            PropertyCollection properties = conversation.BioConvo;
            string fileStem = Path.GetFileNameWithoutExtension(result.FileName);
            var record = new ConversationRecord
            {
                Id = $"{fileStem}:{export.ObjectName.Instanced}",
                Name = export.ObjectName.Instanced,
                File = result.FileName,
                Dlc = result.Dlc,
                IsMod = result.IsMod,
                UIndex = export.UIndex,
                Path = export.InstancedFullPath,
                MainPackage = Path.GetFileName(result.FileName).StripUnrealLocalization(),
                MatineeSequence = conversation.Sequence?.InstancedFullPath
            };

            record.Speakers.Add(new SpeakerRecord { Index = -2, Tag = "Player" });
            record.Speakers.Add(new SpeakerRecord { Index = -1, Tag = "Owner" });
            var speakerTags = new Dictionary<int, string> { [-2] = "Player", [-1] = "Owner" };
            if (properties.GetProp<ArrayProperty<NameProperty>>("m_aSpeakerList") is { } speakerList)
            {
                for (int i = 0; i < speakerList.Count; i++)
                {
                    speakerTags[i] = speakerList[i].Value.Instanced;
                    record.Speakers.Add(new SpeakerRecord { Index = i, Tag = speakerList[i].Value.Instanced });
                }
            }
            else if (properties.GetProp<ArrayProperty<StructProperty>>("m_SpeakerList") is { } legacySpeakers)
            {
                for (int i = 0; i < legacySpeakers.Count; i++)
                {
                    string tag = legacySpeakers[i].GetProp<NameProperty>("sSpeakerTag")?.Value.Instanced;
                    speakerTags[i] = tag;
                    record.Speakers.Add(new SpeakerRecord { Index = i, Tag = tag });
                }
            }

            var startingEntries = new HashSet<int>();
            if (properties.GetProp<ArrayProperty<IntProperty>>("m_StartingList") is { } startingList)
            {
                foreach (IntProperty start in startingList)
                {
                    startingEntries.Add(start.Value);
                    record.StartNodes.Add($"E{start.Value}");
                }
            }

            if (properties.GetProp<ArrayProperty<NameProperty>>("m_aScriptList") is { } scripts)
            {
                record.Scripts.AddRange(scripts.Select(script => script.Value.Instanced));
            }

            var directionsByStrRef = new Dictionary<int, string>();
            if (properties.GetProp<ArrayProperty<StructProperty>>("m_aStageDirections") is { } directions)
            {
                foreach (StructProperty direction in directions)
                {
                    int strRef = direction.GetProp<StringRefProperty>("srStrRef")?.Value ?? 0;
                    string text = direction.GetProp<StrProperty>("sText")?.Value;
                    record.StageDirections.Add(new StageDirectionRecord
                    {
                        StrRef = strRef,
                        LineText = strRef > 0 ? tlk?.Invoke(strRef) : null,
                        Direction = text
                    });
                    if (strRef > 0 && !string.IsNullOrWhiteSpace(text))
                    {
                        directionsByStrRef[strRef] = directionsByStrRef.TryGetValue(strRef, out string existing)
                            ? existing + " / " + text
                            : text;
                    }
                }
            }

            Dictionary<int, ExportEntry> convStarts = BuildConvStarts(conversation);
            ArrayProperty<StructProperty> entries = properties.GetProp<ArrayProperty<StructProperty>>("m_EntryList");
            ArrayProperty<StructProperty> replies = properties.GetProp<ArrayProperty<StructProperty>>("m_ReplyList");
            record.EntryCount = entries?.Count ?? 0;
            record.ReplyCount = replies?.Count ?? 0;
            bool allAmbient = true;

            for (int i = 0; i < record.EntryCount; i++)
            {
                NodeRecord node = ExtractNode(entries[i], i, false, record, speakerTags, directionsByStrRef,
                    conversation, convStarts, result, tlk);
                node.IsStart = startingEntries.Contains(i);
                allAmbient &= node.Flags?.ContainsKey("ambient") == true;
                record.Nodes.Add(node);
            }
            for (int i = 0; i < record.ReplyCount; i++)
            {
                NodeRecord node = ExtractNode(replies[i], i, true, record, speakerTags, directionsByStrRef,
                    conversation, convStarts, result, tlk);
                allAmbient &= node.Flags?.ContainsKey("ambient") == true;
                record.Nodes.Add(node);
            }
            record.IsAmbient = record.Nodes.Count > 0 && allAmbient;
            return record;
        }

        private static Dictionary<int, ExportEntry> BuildConvStarts(ConversationExtended conversation)
        {
            var convStarts = new Dictionary<int, ExportEntry>();
            if (conversation.Sequence is not ExportEntry sequence)
            {
                return convStarts;
            }

            foreach (ObjectProperty objectProperty in sequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects") ?? Enumerable.Empty<ObjectProperty>())
            {
                if (objectProperty.ResolveToEntry(sequence.FileRef) is ExportEntry sequenceObject
                    && sequenceObject.ClassName == "BioSeqEvt_ConvNode")
                {
                    int key = sequenceObject.GetProperty<IntProperty>("m_nNodeID")?.Value ?? -1;
                    convStarts.TryAdd(key, sequenceObject);
                }
            }
            return convStarts;
        }

        private static NodeRecord ExtractNode(StructProperty nodeProperty, int index, bool isReply,
            ConversationRecord conversationRecord, IReadOnlyDictionary<int, string> speakerTags,
            IReadOnlyDictionary<int, string> directionsByStrRef, ConversationExtended conversation,
            Dictionary<int, ExportEntry> convStarts, CorpusFileResult result, Func<int, string> tlk)
        {
            PropertyCollection p = nodeProperty.Properties;
            int strRef = p.GetProp<StringRefProperty>("srText")?.Value ?? 0;
            int speakerIndex = isReply ? -2 : p.GetProp<IntProperty>("nSpeakerIndex")?.Value ?? -1;
            int listenerIndex = p.GetProp<IntProperty>("nListenerIndex")?.Value ?? -3;
            var node = new NodeRecord
            {
                ConversationId = conversationRecord.Id,
                Id = (isReply ? "R" : "E") + index,
                Kind = isReply ? "reply" : "entry",
                Index = index,
                StrRef = strRef,
                Text = strRef > 0 ? tlk?.Invoke(strRef) : null,
                StageDirection = directionsByStrRef.GetValueOrDefault(strRef),
                SpeakerIndex = speakerIndex,
                Speaker = speakerTags.GetValueOrDefault(speakerIndex),
                ListenerIndex = listenerIndex,
                // -3 is the default listener: the owner for player replies, the player for NPC lines.
                Listener = listenerIndex == -3
                    ? (isReply ? "Owner" : "Player")
                    : speakerTags.GetValueOrDefault(listenerIndex),
                ReplyType = isReply ? p.GetProp<EnumProperty>("ReplyType")?.Value.Instanced : null,
                GuiStyle = p.GetProp<EnumProperty>("eGUIStyle")?.Value.Instanced,
                CameraIntimacy = p.GetProp<IntProperty>("nCameraIntimacy")?.Value ?? 0,
                ConditionalFunc = p.GetProp<IntProperty>("nConditionalFunc")?.Value ?? -1,
                ConditionalParam = p.GetProp<IntProperty>("nConditionalParam")?.Value ?? -1,
                FireConditional = p.GetProp<BoolProperty>("bFireConditional")?.Value ?? false,
                StateTransition = p.GetProp<IntProperty>("nStateTransition")?.Value ?? -1,
                StateTransitionParam = p.GetProp<IntProperty>("nStateTransitionParam")?.Value ?? -1,
                ScriptIndex = p.GetProp<IntProperty>("nScriptIndex")?.Value ?? -1,
                ExportId = p.GetProp<IntProperty>("nExportID")?.Value ?? 0
            };

            var flags = new Dictionary<string, bool>();
            void Flag(string propertyName, string flagName)
            {
                if (p.GetProp<BoolProperty>(propertyName)?.Value == true)
                {
                    flags[flagName] = true;
                }
            }
            Flag("bSkippable", "skippable");
            Flag("bUnskippable", "unskippable");
            Flag("bAmbient", "ambient");
            Flag("bNonTextLine", "nonTextLine");
            Flag("bIgnoreBodyGestures", "ignoreBodyGestures");
            Flag("bAlwaysHideSubtitle", "alwaysHideSubtitle");
            Flag("bIsDefaultAction", "defaultAction");
            Flag("bIsMajorDecision", "majorDecision");
            node.Flags = flags.Count > 0 ? flags : null;

            if (isReply)
            {
                node.Next = p.GetProp<ArrayProperty<IntProperty>>("EntryList")?.Select(entry => $"E{entry.Value}").ToList();
            }
            else if (p.GetProp<ArrayProperty<StructProperty>>("ReplyListNew") is { } replyLinks)
            {
                node.Replies = replyLinks.Select(link =>
                {
                    int paraphraseStrRef = link.GetProp<StringRefProperty>("srParaphrase")?.Value ?? 0;
                    string paraphrase = link.GetProp<StrProperty>("sParaphrase")?.Value;
                    if (string.IsNullOrEmpty(paraphrase) && paraphraseStrRef > 0)
                    {
                        paraphrase = tlk?.Invoke(paraphraseStrRef);
                    }
                    return new ReplyLinkRecord
                    {
                        Target = $"R{link.GetProp<IntProperty>("nIndex")?.Value ?? -1}",
                        ParaphraseStrRef = paraphraseStrRef,
                        Paraphrase = string.IsNullOrEmpty(paraphrase) ? null : paraphrase,
                        Category = link.GetProp<EnumProperty>("Category")?.Value.Instanced
                    };
                }).ToList();
            }

            try
            {
                var dialogueNode = new DialogueNodeExtended(nodeProperty, isReply, index, speakerIndex, strRef, node.Text,
                    node.FireConditional, node.ConditionalFunc, node.StateTransition,
                    isReply && Enum.TryParse(node.ReplyType, out EReplyTypes replyType) ? replyType : EReplyTypes.REPLY_STANDARD);
                ExportEntry interpData = convStarts.Count > 0 ? conversation.ParseSingleNodeInterpData(dialogueNode, convStarts) : null;
                if (interpData is not null)
                {
                    node.InterpData = interpData.InstancedFullPath;
                    node.Timeline = ExtractTimeline(interpData, tlk);
                }
            }
            catch (Exception exception)
            {
                result.Errors.Add($"{result.FileName} {conversationRecord.Name} {node.Id}: timeline: {exception.Message}");
            }
            return node;
        }

        #endregion

        #region Timelines

        private static readonly HashSet<string> SkippedTrackSettings = new(StringComparer.OrdinalIgnoreCase)
        {
            "TrackTitle", "m_aTrackKeys", "PosTrack", "EulerTrack", "LookupTrack", "FloatTrack", "VectorTrack", "CutTrack",
            "SubTrackGroups", "SubTracks", "ActiveCondition", "bIsAnimControlTrack", "m_nmFindActor", "m_nmSFXFindActor"
        };

        internal static TimelineRecord ExtractTimeline(ExportEntry interpData, Func<int, string> tlk)
        {
            IMEPackage pcc = interpData.FileRef;
            var timeline = new TimelineRecord
            {
                Length = CorpusPropertyJson.Round(interpData.GetProperty<FloatProperty>("InterpLength")?.Value ?? 0f)
            };

            var groupNamesWithMove = new List<string>();
            foreach (ObjectProperty groupReference in interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups") ?? Enumerable.Empty<ObjectProperty>())
            {
                if (groupReference.ResolveToEntry(pcc) is not ExportEntry group)
                {
                    continue;
                }

                PropertyCollection groupProperties = group.GetProperties();
                string groupName = groupProperties.GetProp<NameProperty>("GroupName")?.Value.Instanced
                                   ?? (group.ClassName == "InterpGroupDirector" ? "Director" : group.ObjectName.Instanced);
                string groupFindActor = NameOrNull(groupProperties.GetProp<NameProperty>("m_nmSFXFindActor"));
                foreach (ObjectProperty trackReference in groupProperties.GetProp<ArrayProperty<ObjectProperty>>("InterpTracks") ?? Enumerable.Empty<ObjectProperty>())
                {
                    if (trackReference.ResolveToEntry(pcc) is not ExportEntry trackExport)
                    {
                        continue;
                    }
                    TrackRecord track = ExtractTrack(trackExport, groupName, groupFindActor, tlk);
                    timeline.Tracks.Add(track);
                    if (track.Class == "InterpTrackMove")
                    {
                        groupNamesWithMove.Add(groupName);
                    }
                }
            }

            foreach (TrackRecord track in timeline.Tracks)
            {
                switch (track.Kind)
                {
                    case "director":
                        foreach (JsonObject key in track.Keys)
                        {
                            timeline.Cuts.Add(new CutRecord
                            {
                                T = GetFloat(key, "t"),
                                Target = key["TargetCamGroup"]?.GetValue<string>(),
                                TransitionTime = GetFloat(key, "TransitionTime")
                            });
                        }
                        break;
                    case "stage_camera":
                        foreach (JsonObject key in track.Keys)
                        {
                            timeline.StageCameraSwitches.Add(new CutRecord
                            {
                                T = GetFloat(key, "t"),
                                Target = key["nmStageSpecificCam"]?.GetValue<string>(),
                                ForceCrossingLineOfAction = key["bForceCrossingLineOfAction"]?.GetValue<bool>() == true
                            });
                        }
                        break;
                    case "vo":
                        if (track.Settings?["m_nStrRefID"] is JsonValue strRefValue && strRefValue.TryGetValue(out int voStrRef) && voStrRef > 0)
                        {
                            timeline.VoStrRefs.Add(voStrRef);
                        }
                        break;
                }
            }
            timeline.Cuts.Sort((a, b) => a.T.CompareTo(b.T));
            timeline.StageCameraSwitches.Sort((a, b) => a.T.CompareTo(b.T));

            var cameraGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CutRecord cut in timeline.Cuts.Where(cut => !string.IsNullOrEmpty(cut.Target)))
            {
                cameraGroups.Add(cut.Target);
            }
            foreach (string groupName in groupNamesWithMove.Where(name => name.StartsWith("cam", StringComparison.OrdinalIgnoreCase)))
            {
                cameraGroups.Add(groupName);
            }
            timeline.CameraGroups = cameraGroups.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (TrackRecord track in timeline.Tracks.Where(track => track.Kind == "move"))
            {
                track.Kind = cameraGroups.Contains(track.Group) ? "camera_move" : "actor_move";
            }
            foreach (TrackRecord track in timeline.Tracks.Where(track => track.Kind == "fov" && !cameraGroups.Contains(track.Group)))
            {
                track.Kind = "float_prop";
            }
            return timeline;
        }

        private static TrackRecord ExtractTrack(ExportEntry trackExport, string groupName, string groupFindActor,
            Func<int, string> tlk)
        {
            IMEPackage pcc = trackExport.FileRef;
            PropertyCollection properties = trackExport.GetProperties();
            string findActor = NameOrNull(properties.GetProp<NameProperty>("m_nmFindActor"))
                               ?? NameOrNull(properties.GetProp<NameProperty>("m_nmSFXFindActor"));
            var track = new TrackRecord
            {
                Group = groupName,
                GroupFindActor = groupFindActor,
                Class = trackExport.ClassName,
                Title = properties.GetProp<StrProperty>("TrackTitle")?.Value,
                Actor = findActor ?? groupFindActor ?? groupName,
                Kind = ClassifyTrack(trackExport.ClassName, properties)
            };

            var consumed = new HashSet<string>(SkippedTrackSettings, StringComparer.OrdinalIgnoreCase);
            if (properties.GetProp<ArrayProperty<StructProperty>>("m_aTrackKeys") is { } trackKeys)
            {
                ArrayProperty<StructProperty> data = properties
                    .OfType<ArrayProperty<StructProperty>>()
                    .FirstOrDefault(array => array.Name.Name.StartsWith("m_a", StringComparison.Ordinal)
                                             && array.Name.Name != "m_aTrackKeys");
                if (data is not null)
                {
                    consumed.Add(data.Name.Name);
                }
                int count = Math.Max(trackKeys.Count, data?.Count ?? 0);
                for (int i = 0; i < count; i++)
                {
                    float time = i < trackKeys.Count
                        ? trackKeys[i].GetProp<FloatProperty>("fTime")?.Value ?? 0f
                        : data?[i].GetProp<FloatProperty>("fTime")?.Value ?? 0f;
                    var key = new JsonObject { ["t"] = CorpusPropertyJson.Round(time) };
                    if (i < trackKeys.Count && trackKeys[i].GetProp<NameProperty>("KeyName") is { } keyName && NameOrNull(keyName) is { } keyNameText)
                    {
                        key["KeyName"] = keyNameText;
                    }
                    if (data is not null && i < data.Count)
                    {
                        CopyStruct(key, data[i].Properties, pcc, tlk, "fTime", "KeyName");
                    }
                    track.Keys.Add(key);
                }
            }
            else if (trackExport.ClassName == "InterpTrackMove")
            {
                ExtractMoveKeys(properties, track);
            }
            else if (trackExport.ClassName == "InterpTrackDirector" && properties.GetProp<ArrayProperty<StructProperty>>("CutTrack") is { } cuts)
            {
                foreach (StructProperty cut in cuts)
                {
                    var key = new JsonObject { ["t"] = CorpusPropertyJson.Round(cut.GetProp<FloatProperty>("Time")?.Value ?? 0f) };
                    CopyStruct(key, cut.Properties, pcc, tlk, "Time");
                    track.Keys.Add(key);
                }
            }
            else
            {
                StructProperty curve = properties.OfType<StructProperty>()
                    .FirstOrDefault(structProperty => structProperty.Properties.GetProp<ArrayProperty<StructProperty>>("Points") is not null);
                if (curve is not null)
                {
                    consumed.Add(curve.Name.Name);
                    foreach (StructProperty point in curve.Properties.GetProp<ArrayProperty<StructProperty>>("Points"))
                    {
                        var key = new JsonObject { ["t"] = CorpusPropertyJson.Round(point.GetProp<FloatProperty>("InVal")?.Value ?? 0f) };
                        CopyStruct(key, point.Properties, pcc, tlk, "InVal");
                        track.Keys.Add(key);
                    }
                }
                else
                {
                    ArrayProperty<StructProperty> timed = properties.OfType<ArrayProperty<StructProperty>>()
                        .FirstOrDefault(array => array.Count > 0 && CorpusPropertyJson.HasAny(array[0].Properties, "Time", "StartTime", "fTime"));
                    if (timed is not null)
                    {
                        consumed.Add(timed.Name.Name);
                        foreach (StructProperty element in timed)
                        {
                            float time = element.Properties.GetProp<FloatProperty>("Time")?.Value
                                         ?? element.Properties.GetProp<FloatProperty>("StartTime")?.Value
                                         ?? element.Properties.GetProp<FloatProperty>("fTime")?.Value ?? 0f;
                            var key = new JsonObject { ["t"] = CorpusPropertyJson.Round(time) };
                            CopyStruct(key, element.Properties, pcc, tlk, "Time", "fTime");
                            track.Keys.Add(key);
                        }
                    }
                }
            }

            JsonObject settings = CorpusPropertyJson.ToObject(properties, pcc, tlk, property => consumed.Contains(property.Name.Name));
            track.Settings = settings.Count > 0 ? settings : null;
            return track;
        }

        private static void ExtractMoveKeys(PropertyCollection properties, TrackRecord track)
        {
            ArrayProperty<StructProperty> positions = properties.GetProp<StructProperty>("PosTrack")?.GetProp<ArrayProperty<StructProperty>>("Points");
            ArrayProperty<StructProperty> rotations = properties.GetProp<StructProperty>("EulerTrack")?.GetProp<ArrayProperty<StructProperty>>("Points");
            int count = Math.Max(positions?.Count ?? 0, rotations?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                StructProperty position = positions is not null && i < positions.Count ? positions[i] : null;
                StructProperty rotation = rotations is not null && i < rotations.Count ? rotations[i] : null;
                float time = position?.GetProp<FloatProperty>("InVal")?.Value ?? rotation?.GetProp<FloatProperty>("InVal")?.Value ?? 0f;
                var key = new JsonObject { ["t"] = CorpusPropertyJson.Round(time) };
                if (position is not null)
                {
                    key["pos"] = VectorNode(position, "OutVal");
                    key["posArrive"] = VectorNode(position, "ArriveTangent");
                    key["posLeave"] = VectorNode(position, "LeaveTangent");
                    key["interp"] = position.GetProp<EnumProperty>("InterpMode")?.Value.Instanced;
                }
                if (rotation is not null)
                {
                    // EulerTrack stores (X = roll, Y = pitch, Z = yaw) in degrees.
                    key["rot"] = VectorNode(rotation, "OutVal");
                    key["rotArrive"] = VectorNode(rotation, "ArriveTangent");
                    key["rotLeave"] = VectorNode(rotation, "LeaveTangent");
                    key["rotInterp"] = rotation.GetProp<EnumProperty>("InterpMode")?.Value.Instanced;
                }
                track.Keys.Add(key);
            }
        }

        private static JsonArray VectorNode(StructProperty point, string name)
        {
            StructProperty vector = point.GetProp<StructProperty>(name);
            Vector3 value = vector is null ? Vector3.Zero : CommonStructs.GetVector3(vector);
            return new JsonArray(CorpusPropertyJson.Round(value.X, 2), CorpusPropertyJson.Round(value.Y, 2), CorpusPropertyJson.Round(value.Z, 2));
        }

        private static void CopyStruct(JsonObject target, PropertyCollection properties, IMEPackage pcc, Func<int, string> tlk,
            params string[] skip)
        {
            foreach (Property property in properties)
            {
                if (property is NoneProperty || skip.Contains(property.Name.Name))
                {
                    continue;
                }
                CorpusPropertyJson.AddProperty(target, property, pcc, tlk);
            }
        }

        private static string ClassifyTrack(string className, PropertyCollection properties) => className switch
        {
            "BioEvtSysTrackGesture" => "gesture",
            "InterpTrackMove" => "move",
            "InterpTrackFloatProp" when properties.GetProp<NameProperty>("PropertyName")?.Value.Name == "FOVAngle" => "fov",
            "InterpTrackFloatProp" or "InterpTrackVectorProp" or "InterpTrackFloatMaterialParam" or "InterpTrackColorScale" => "float_prop",
            "InterpTrackDirector" => "director",
            "BioEvtSysTrackSwitchCamera" => "stage_camera",
            "BioEvtSysTrackLookAt" => "lookat",
            "BioEvtSysTrackSetFacing" => "facing",
            "BioEvtSysTrackDOF" => "dof",
            "BioEvtSysTrackLighting" => "lighting",
            "BioEvtSysTrackVOElements" => "vo",
            "InterpTrackFaceFX" => "facefx",
            "SFXInterpTrackPlayFaceOnlyVO" => "faceonly_vo",
            "BioEvtSysTrackProp" => "prop",
            "BioEvtSysTrackInterrupt" => "interrupt",
            "InterpTrackAnimControl" => "anim",
            "InterpTrackEvent" => "event",
            "InterpTrackFade" or "SFXInterpTrackBlackScreen" => "fade",
            "InterpTrackVisibility" or "InterpTrackToggle" => "visibility",
            _ when className.Contains("Wwise", StringComparison.OrdinalIgnoreCase)
                   || className.Contains("Sound", StringComparison.OrdinalIgnoreCase)
                   || className.Contains("Foley", StringComparison.OrdinalIgnoreCase) => "sound",
            _ => "other"
        };

        private static float GetFloat(JsonObject obj, string name) =>
            obj[name] is JsonValue value && value.TryGetValue(out float result) ? result : 0f;

        private static string NameOrNull(NameProperty property) =>
            property is null || property.Value.Name is null || property.Value.Name.Equals("None", StringComparison.OrdinalIgnoreCase)
                ? null
                : property.Value.Instanced;

        #endregion

        #region Stages

        private static void ExtractStages(IMEPackage pcc, CorpusFileResult result)
        {
            bool hasStarts = pcc.Exports.Any(export => StageBoneOriginResolver.StartConversationClassNames.Contains(export.ClassName));
            bool hasStages = pcc.Exports.Any(export => export.ClassName == "BioStage");
            if (!hasStarts && !hasStages)
            {
                return;
            }

            using var cache = new PackageCache();
            var stageIds = new HashSet<string>();
            if (hasStarts)
            {
                List<StageBoneOriginResolver.HeadlessStageConversationContext> contexts =
                    StageBoneOriginResolver.ResolveAllStageContextsHeadless(pcc, cache,
                        error => result.Errors.Add($"{result.FileName} stage context: {error}"));
                foreach (var context in contexts)
                {
                    StageRecord stage = BuildStageRecord(context.Geometry, result);
                    if (stageIds.Add(stage.Id))
                    {
                        result.Stages.Add(stage);
                    }

                    var record = new ConversationStageRecord
                    {
                        Id = $"{result.FileName}#{context.StartConversation.UIndex}@{context.Geometry.Stage.UIndex}",
                        ConversationName = context.ConversationName,
                        ConversationPath = context.ConversationPath,
                        File = result.FileName,
                        StartConversation = context.StartConversation.InstancedFullPath,
                        StartConversationClass = context.StartConversation.ClassName,
                        StageId = stage.Id,
                        StageTag = stage.Tag
                    };
                    foreach ((string link, string subtitle) in context.VariableLinkSubtitles)
                    {
                        record.Links[link] = subtitle;
                    }
                    foreach ((string tag, CameraOrigin origin) in context.ActorOrigins)
                    {
                        record.Actors[tag] = ToOrigin(origin);
                    }
                    // Keep raw slot transforms for every link that names a stage bone (Node0, Node1, ...),
                    // so actors without a resolvable tag can still be placed by slot.
                    foreach (string link in context.VariableLinkSubtitles.Keys)
                    {
                        if (context.Geometry.BoneOrigins.TryGetValue(link, out CameraOrigin slot) && !record.Actors.ContainsKey(link))
                        {
                            record.Actors[link] = ToOrigin(slot);
                        }
                    }
                    result.ConversationStages.Add(record);
                }
            }

            // Stage library: placed BioStages that no StartConversation in this file references.
            foreach (ExportEntry stage in pcc.Exports.Where(export => export.ClassName == "BioStage" && !export.IsDefaultObject
                                                                     && export.InstancedFullPath.Contains("PersistentLevel", StringComparison.OrdinalIgnoreCase)))
            {
                string id = $"{result.FileName}#{stage.UIndex}";
                if (stageIds.Contains(id))
                {
                    continue;
                }
                try
                {
                    StageRecord record = BuildStageRecord(StageBoneOriginResolver.ResolveStageGeometryHeadless(stage, cache), result);
                    stageIds.Add(record.Id);
                    result.Stages.Add(record);
                }
                catch (Exception exception)
                {
                    result.Errors.Add($"{result.FileName} {stage.InstancedFullPath}: stage failed: {exception.Message}");
                }
            }
        }

        private static StageRecord BuildStageRecord(StageBoneOriginResolver.HeadlessStageGeometry geometry, CorpusFileResult result)
        {
            ExportEntry stage = geometry.Stage;
            IMEPackage pcc = stage.FileRef;
            var record = new StageRecord
            {
                Id = $"{result.FileName}#{stage.UIndex}",
                File = result.FileName,
                Dlc = result.Dlc,
                IsMod = result.IsMod,
                Path = stage.InstancedFullPath,
                Tag = stage.GetProperty<NameProperty>("Tag")?.Value.Instanced,
                Archetype = stage.Archetype?.InstancedFullPath,
                Mesh = geometry.Bones.Select(bone => bone.MeshPath).FirstOrDefault(),
                Origin = ToOrigin(geometry.StageOrigin)
            };

            foreach ((string name, int index, int parentIndex, string _) in geometry.Bones)
            {
                if (!geometry.BoneOrigins.TryGetValue(name, out CameraOrigin world))
                {
                    continue;
                }
                record.Bones.Add(new StageBoneRecord
                {
                    Name = name,
                    Index = index,
                    Parent = parentIndex,
                    Kind = ClassifyBone(name, index, geometry.Cameras.ContainsKey(name)),
                    World = ToOrigin(world),
                    Local = ToLocal(world, geometry.StageOrigin)
                });
            }

            foreach ((string tag, StageCameraDefinition camera) in geometry.Cameras)
            {
                JsonObject settings = null;
                if (geometry.CameraPropertyLayers.TryGetValue(tag, out List<PropertyCollection> layers))
                {
                    settings = new JsonObject();
                    foreach (PropertyCollection layer in layers)
                    {
                        foreach (Property property in layer)
                        {
                            if (property is NoneProperty || property.Name.Name == "nmCameraTag")
                            {
                                continue;
                            }
                            settings.Remove(property.Name.Instanced);
                            CorpusPropertyJson.AddProperty(settings, property, pcc, null);
                        }
                    }
                }
                record.Cameras.Add(new StageCameraRecord
                {
                    Tag = tag,
                    World = ToOrigin(camera.Origin),
                    Local = ToLocal(camera.Origin, geometry.StageOrigin),
                    Fov = camera.FovDegrees is { } fov ? CorpusPropertyJson.Round(fov, 2) : null,
                    NearPlane = CorpusPropertyJson.Round(camera.NearPlane, 2),
                    DisableHeightAdjustment = camera.DisableHeightAdjustment,
                    Settings = settings is { Count: > 0 } ? settings : null
                });
            }
            return record;
        }

        private static string ClassifyBone(string name, int index, bool isCamera)
        {
            if (isCamera || name.StartsWith("cam", StringComparison.OrdinalIgnoreCase)) return "camera";
            if (index == 0) return "root";
            if (name.StartsWith("node", StringComparison.OrdinalIgnoreCase)) return "node";
            if (name.StartsWith("pup", StringComparison.OrdinalIgnoreCase)) return "puppet";
            if (name.StartsWith("dummy", StringComparison.OrdinalIgnoreCase)) return "dummy";
            return "other";
        }

        internal static OriginRecord ToOrigin(CameraOrigin origin) => new()
        {
            Position = CorpusPropertyJson.Vec(origin.Location),
            Rotation = CorpusPropertyJson.Vec(origin.Rotation)
        };

        internal static OriginRecord ToLocal(CameraOrigin world, CameraOrigin reference)
        {
            CameraPresetGenerator.BuildBasis(reference.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
            Vector3 delta = world.Location - reference.Location;
            return new OriginRecord
            {
                Position = CorpusPropertyJson.Vec(new Vector3(Vector3.Dot(delta, forward), Vector3.Dot(delta, right), Vector3.Dot(delta, up))),
                Rotation = CorpusPropertyJson.Vec(CameraPresetGenerator.WorldRotationToLocal(world.Rotation, reference.Rotation))
            };
        }

        #endregion

        #region Animations

        private static void ExtractAnimations(IMEPackage pcc, CorpusFileResult result)
        {
            foreach (ExportEntry export in pcc.Exports.Where(export => export.ClassName == "AnimSequence" && !export.IsDefaultObject))
            {
                try
                {
                    PropertyCollection properties = export.GetProperties();
                    result.Animations.Add(new AnimationRecord
                    {
                        Set = export.Parent?.ObjectName.Instanced,
                        Sequence = properties.GetProp<NameProperty>("SequenceName")?.Value.Instanced ?? export.ObjectName.Instanced,
                        Length = CorpusPropertyJson.Round(properties.GetProp<FloatProperty>("SequenceLength")?.Value ?? 0f),
                        Frames = properties.GetProp<IntProperty>("NumFrames")?.Value ?? 0,
                        RateScale = CorpusPropertyJson.Round(properties.GetProp<FloatProperty>("RateScale")?.Value ?? 1f),
                        Path = export.InstancedFullPath,
                        File = result.FileName
                    });
                }
                catch (Exception exception)
                {
                    result.Errors.Add($"{result.FileName} {export.InstancedFullPath}: animation failed: {exception.Message}");
                }
            }
        }

        #endregion
    }
}
