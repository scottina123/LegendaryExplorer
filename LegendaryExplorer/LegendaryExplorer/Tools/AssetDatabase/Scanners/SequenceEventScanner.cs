using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners
{
    internal sealed class SequenceEventScanner : AssetScanner
    {
        private static readonly Dictionary<string, SequenceEventType> SequenceEventClasses = new(StringComparer.Ordinal)
        {
            ["SeqAct_ActivateRemoteEvent"] = SequenceEventType.ActivateRemoteEvent,
            ["SeqAct_ConsoleCommand"] = SequenceEventType.ConsoleCommand,
            ["SeqEvent_RemoteEvent"] = SequenceEventType.RemoteEvent,
            ["SeqEvent_Console"] = SequenceEventType.ConsoleEvent
        };

        public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
        {
            if (e.IsDefault || !SequenceEventClasses.TryGetValue(e.ClassName, out var eventType))
            {
                return;
            }

            if (eventType == SequenceEventType.ConsoleCommand)
            {
                var commands = e.Properties.GetProp<ArrayProperty<StrProperty>>("Commands");
                if (commands == null)
                {
                    return;
                }

                foreach (var command in commands
                             .Select(commandProperty => commandProperty.Value?.Trim())
                             .Where(command => !string.IsNullOrWhiteSpace(command))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    AddSequenceEvent(command, eventType, e, db);
                }

                return;
            }

            string propertyName = eventType == SequenceEventType.ConsoleEvent ? "ConsoleEventName" : "EventName";
            AddSequenceEvent(GetPropertyValue(e.Properties, propertyName), eventType, e, db);
        }

        private static void AddSequenceEvent(string value, SequenceEventType eventType, ExportScanInfo e, ConcurrentAssetDB db)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var usage = new SequenceEventUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod);
            var key = $"{eventType}|{value.ToLowerInvariant()}";
            if (db.GeneratedSequenceEvents.TryGetValue(key, out var existingRecord))
            {
                lock (existingRecord)
                {
                    existingRecord.Usages.Add(usage);
                }
                return;
            }

            var newRecord = new SequenceEventRecord(value, eventType, e.IsDlc, e.IsMod);
            newRecord.Usages.Add(usage);
            if (!db.GeneratedSequenceEvents.TryAdd(key, newRecord))
            {
                existingRecord = db.GeneratedSequenceEvents[key];
                lock (existingRecord)
                {
                    existingRecord.Usages.Add(usage);
                }
            }
        }

        private static string GetPropertyValue(PropertyCollection properties, string propertyName)
        {
            if (properties.GetProp<NameProperty>(propertyName) is { } nameProperty)
            {
                return nameProperty.Value.Instanced;
            }

            return properties.GetProp<StrProperty>(propertyName)?.Value;
        }
    }
}
