using System;
using System.Collections.Generic;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners
{
    internal sealed class ActorScanner : AssetScanner
    {
        // Maps class name prefixes/names to ActorType.
        // Pawn and SkeletalMeshActor are matched by prefix to capture subclasses.
        private static readonly Dictionary<string, ActorType> ExactClassMap = new(StringComparer.Ordinal)
        {
            ["SFXStuntActor"]       = ActorType.StuntActor,
            ["SFXPointOfInterest"]  = ActorType.PointOfInterest,
        };

        public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
        {
            if (e.IsDefault)
            {
                return;
            }

            if (!TryGetActorType(e.ClassName, out var actorType))
            {
                return;
            }

            var tag = e.Properties.GetProp<NameProperty>("Tag")?.Value.Instanced;
            var gameNameStrRef = e.Properties.GetProp<IntProperty>("m_srGameName")?.Value ?? 0;

            var usage = new ActorUsage(e.FileKey, e.Export.UIndex, e.IsMod);
            var key = e.AssetKey;

            if (db.GeneratedActors.TryGetValue(key, out var existingRecord))
            {
                lock (existingRecord)
                {
                    existingRecord.Usages.Add(usage);
                }
                return;
            }

            var newRecord = new ActorRecord(e.ObjectNameInstanced, tag ?? string.Empty, gameNameStrRef, actorType, e.IsMod);
            newRecord.Usages.Add(usage);
            if (!db.GeneratedActors.TryAdd(key, newRecord))
            {
                existingRecord = db.GeneratedActors[key];
                lock (existingRecord)
                {
                    existingRecord.Usages.Add(usage);
                }
            }
        }

        private static bool TryGetActorType(string className, out ActorType actorType)
        {
            if (ExactClassMap.TryGetValue(className, out actorType))
            {
                return true;
            }

            if (className.StartsWith("SFXPawn", StringComparison.Ordinal))
            {
                actorType = ActorType.Pawn;
                return true;
            }

            if (className.StartsWith("SFXSkeletalMeshActor", StringComparison.Ordinal))
            {
                actorType = ActorType.SkeletalMeshActor;
                return true;
            }

            actorType = default;
            return false;
        }
    }
}
