using System;
using System.Linq;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners
{
    internal sealed class PropActionScanner : AssetScanner
    {
        public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
        {
            if (e.IsDefault)
            {
                return;
            }

            if (e.ClassName == "Class" && e.Export.ObjectName.Name.StartsWith("SFXWeapon_", StringComparison.OrdinalIgnoreCase))
            {
                string propName = e.Export.ObjectName.Name["SFXWeapon_".Length..];
                AddRecord(db, new PropActionRecord(propName, NameReference.None.Name, e.FileKey, 0, -1, e.Export.UIndex, false, e.IsMod), "weapon");
                return;
            }

            if (e.ClassName == "BioGestureRuntimeData")
            {
                ScanRuntimeData(e, db);
                return;
            }

            if (e.ClassName == "BioEvtSysTrackProp")
            {
                ScanPropTrack(e, db);
            }
        }

        private static void ScanRuntimeData(ExportScanInfo e, ConcurrentAssetDB db)
        {
            BioGestureRuntimeData runtimeData = e.Export.GetBinaryData<BioGestureRuntimeData>();
            if (e.Export.Game.IsGame1() || runtimeData?.m_mapMeshProps is null)
            {
                return;
            }

            foreach ((NameReference propKey, BioGestureRuntimeData.BioMeshPropData propData) in runtimeData.m_mapMeshProps)
            {
                if (propData?.mapActions is null)
                {
                    continue;
                }

                string[] propNames = [propKey.Name, propData.nmPropName.Name];
                foreach (string propName in propNames
                             .Where(IsUsableName)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach ((NameReference actionKey, BioGestureRuntimeData.BioMeshPropActionData actionData) in propData.mapActions)
                    {
                        if (actionData is null)
                        {
                            continue;
                        }

                        string[] actionNames = [actionKey.Name, actionData.nmActionName.Name];
                        foreach (string actionName in actionNames
                                     .Where(IsUsableName)
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            var record = new PropActionRecord(
                                propName,
                                actionName,
                                e.FileKey,
                                0,
                                -1,
                                0,
                                !string.IsNullOrWhiteSpace(actionData.sClientEffect) || !string.IsNullOrWhiteSpace(actionData.sParticleSys),
                                e.IsMod);
                            AddRecord(db, record, $"runtime:{e.Export.UIndex}");
                        }
                    }
                }
            }
        }

        private static void ScanPropTrack(ExportScanInfo e, ConcurrentAssetDB db)
        {
            if (e.Properties.GetProp<ArrayProperty<StructProperty>>("m_aPropKeys") is not { } propKeys)
            {
                return;
            }

            for (int keyIndex = 0; keyIndex < propKeys.Count; keyIndex++)
            {
                StructProperty propKey = propKeys[keyIndex];
                NameProperty prop = propKey.Properties.GetProp<NameProperty>("nmProp");
                NameProperty action = propKey.Properties.GetProp<NameProperty>("nmAction");
                if (prop is null || action is null || !IsUsableName(prop.Value.Name) || !IsUsableName(action.Value.Name))
                {
                    continue;
                }

                int weaponUIndex = propKey.Properties.GetProp<ObjectProperty>("pWeaponClass")?.Value ?? 0;
                bool hasEffects = propKey.Properties.Any(property =>
                    property.Name.Name.Contains("Effect", StringComparison.OrdinalIgnoreCase)
                    && property is not NoneProperty);
                var record = new PropActionRecord(
                    prop.Value.Name,
                    action.Value.Name,
                    e.FileKey,
                    e.Export.UIndex,
                    keyIndex,
                    weaponUIndex,
                    hasEffects,
                    e.IsMod);
                AddRecord(db, record, $"track:{e.Export.UIndex}:{keyIndex}");
            }
        }

        private static bool IsUsableName(string name) =>
            !string.IsNullOrWhiteSpace(name) && !name.Equals(NameReference.None.Name, StringComparison.OrdinalIgnoreCase);

        private static void AddRecord(ConcurrentAssetDB db, PropActionRecord record, string sourceKey)
        {
            string key = $"{record.PropName}\0{record.ActionName}\0{record.SourceFileKey}\0{sourceKey}";
            db.GeneratedPropActions.TryAdd(key, record);
        }
    }
}
