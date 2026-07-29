using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners
{
    internal sealed class GestureTrackScanner : AssetScanner
    {
        private static readonly ConditionalWeakTable<IMEPackage, Dictionary<int, int>> NodeStrRefsByInterpData = new();

        public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
        {
            if (e.IsDefault || e.ClassName != "BioEvtSysTrackGesture")
            {
                return;
            }

            var gestures = e.Properties.GetProp<ArrayProperty<StructProperty>>("m_aGestures")?
                .Select(gesture => new GestureDataRecord(
                    GetName(gesture.Properties, "nmPoseSet"),
                    GetName(gesture.Properties, "nmPoseAnim"),
                    GetName(gesture.Properties, "nmGestureSet"),
                    GetName(gesture.Properties, "nmGestureAnim"),
                    GetName(gesture.Properties, "nmTransitionSet"),
                    GetName(gesture.Properties, "nmTransitionAnim")))
                .ToList() ?? [];

            var record = new GestureTrackRecord(
                e.ObjectNameInstanced,
                GetNodeStrRef(e.Export),
                GetName(e.Properties, "nmStartingPoseSet"),
                GetName(e.Properties, "nmStartingPoseAnim"),
                gestures,
                e.IsMod);
            record.Usages.Add(new GestureTrackUsage(e.FileKey, e.Export.UIndex, e.IsMod));
            db.GeneratedGestureTracks.TryAdd($"{e.FileKey}:{e.Export.UIndex}", record);
        }

        private static int GetNodeStrRef(ExportEntry gestureTrack)
        {
            var interpData = GetInterpData(gestureTrack);
            if (interpData == null)
            {
                return 0;
            }

            var nodeStrRefs = NodeStrRefsByInterpData.GetValue(gestureTrack.FileRef, BuildNodeStrRefLookup);
            return nodeStrRefs.GetValueOrDefault(interpData.UIndex);
        }

        private static ExportEntry GetInterpData(ExportEntry gestureTrack)
        {
            IEntry current = gestureTrack.Parent;
            while (current is ExportEntry export)
            {
                if (export.ClassName == "InterpData")
                {
                    return export;
                }

                current = export.Parent;
            }

            return null;
        }

        private static Dictionary<int, int> BuildNodeStrRefLookup(IMEPackage package)
        {
            Dictionary<int, int> nodeStrRefs = [];
            foreach (var conversationExport in package.Exports.Where(export => export.ClassName == "BioConversation"))
            {
                try
                {
                    var conversation = new ConversationExtended(conversationExport);
                    conversation.LoadConversation();
                    foreach (var node in conversation.EntryList.Concat(conversation.ReplyList))
                    {
                        try
                        {
                            var interpData = conversation.ParseSingleNodeInterpData(node);
                            if (interpData != null && node.LineStrRef > 0)
                            {
                                nodeStrRefs.TryAdd(interpData.UIndex, node.LineStrRef);
                            }
                        }
                        catch
                        {
                            // Malformed sequence links should not prevent the rest of the package from being indexed.
                        }
                    }
                }
                catch
                {
                    // Malformed conversations should not prevent the rest of the package from being indexed.
                }
            }

            return nodeStrRefs;
        }

        private static string GetName(PropertyCollection properties, string propertyName)
        {
            return properties.GetProp<NameProperty>(propertyName)?.Value.Instanced ?? "None";
        }
    }
}
