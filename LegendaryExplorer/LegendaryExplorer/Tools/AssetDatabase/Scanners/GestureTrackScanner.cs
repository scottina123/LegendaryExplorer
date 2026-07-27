using System.Linq;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners
{
    internal sealed class GestureTrackScanner : AssetScanner
    {
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
                GetName(e.Properties, "nmStartingPoseSet"),
                GetName(e.Properties, "nmStartingPoseAnim"),
                gestures,
                e.IsMod);
            record.Usages.Add(new GestureTrackUsage(e.FileKey, e.Export.UIndex, e.IsMod));
            db.GeneratedGestureTracks.TryAdd($"{e.FileKey}:{e.Export.UIndex}", record);
        }

        private static string GetName(PropertyCollection properties, string propertyName)
        {
            return properties.GetProp<NameProperty>(propertyName)?.Value.Instanced ?? "None";
        }
    }
}
