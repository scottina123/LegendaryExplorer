using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LegendaryExplorerCore.Coalesced;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners
{
    internal sealed class TlkScanner : AssetScanner
    {
        private static readonly Regex CoalescedIniAssignmentRegex = new(@"^(?<key>[^=;\r\n]+?)\s*=\s*(?<value>\d+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex LargeIntegerRegex = new(@"^\d{5,10}$", RegexOptions.Compiled);
        private static readonly Regex CoalescedTlkReferenceRegex = new(@"\b\d{5,10}\b", RegexOptions.Compiled);
        private static readonly string[] TlkLikeKeyTokens =
        [
            "strref",
            "string",
            "text",
            "title",
            "description",
            "name",
            "subtitle",
            "message",
            "caption",
            "hint",
            "help",
            "body"
        ];

        public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
        {
            if (e.IsDefault)
            {
                return;
            }

            switch (e.ClassName)
            {
                case "BioCodexMap":
                    ScanCodexMap(e, db);
                    break;
                case "BioQuestMap":
                    ScanQuestMap(e, db);
                    break;
            }

            ScanPropertyCollection(e.Properties, db, e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, e.ClassName);
        }

        public void ScanCoalescedFile(string filePath, int fileKey, ConcurrentAssetDB db)
        {
            InferCoalescedFlags(filePath, out bool isInDlc, out bool isInMod);
            using var fs = File.OpenRead(filePath);
            ScanCoalescedStream(fs, filePath, fileKey, isInDlc, isInMod, db);
        }

        public void ScanCoalescedFile(string filePath, int fileKey, ConcurrentAssetDB db, Stream inputStream)
        {
            InferCoalescedFlags(filePath, out bool isInDlc, out bool isInMod);
            ScanCoalescedStream(inputStream, filePath, fileKey, isInDlc, isInMod, db);
        }

        private static void ScanCoalescedStream(Stream inputStream, string filePath, int fileKey, bool isInDlc, bool isInMod, ConcurrentAssetDB db)
        {
            inputStream.Position = 0;
            if (CoalescedConverter.IsGame3Coalesced(inputStream))
            {
                inputStream.Position = 0;
                var xmlMap = CoalescedConverter.DecompileGame3ToMemory(inputStream);
                foreach (var (innerFileName, content) in xmlMap)
                {
                    ScanGame3CoalescedContent(content, innerFileName, fileKey, isInDlc, isInMod, db);
                }
            }
            else
            {
                inputStream.Position = 0;
                var iniMap = CoalescedConverter.DecompileLE1LE2ToMemory(inputStream, Path.GetFileName(filePath));
                foreach (var (innerFileName, content) in iniMap)
                {
                    ScanLegacyCoalescedContent(content.ToString(), innerFileName, fileKey, isInDlc, isInMod, db);
                }
            }
        }

        private static void ScanCodexMap(ExportScanInfo e, ConcurrentAssetDB db)
        {
            var codexMap = ObjectBinary.From<BioCodexMap>(e.Export);
            foreach (var section in codexMap.Sections)
            {
                AddUsage(db, section.Title, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Codex, section.ID, null, "Section Title"));
                AddUsage(db, section.Description, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Codex, section.ID, null, "Section Description"));
            }

            foreach (var page in codexMap.Pages)
            {
                AddUsage(db, page.Title, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Codex, page.ID, null, "Page Title"));
                AddUsage(db, page.Description, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Codex, page.ID, null, "Page Description"));
            }
        }

        private static void ScanQuestMap(ExportScanInfo e, ConcurrentAssetDB db)
        {
            var questMap = ObjectBinary.From<BioQuestMap>(e.Export);
            foreach (var quest in questMap.Quests)
            {
                foreach (var goal in quest.Goals)
                {
                    AddUsage(db, goal.Name, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Quest, quest.ID, null, "Goal Name"));
                    AddUsage(db, goal.Description, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Quest, quest.ID, null, "Goal Description"));
                }

                foreach (var task in quest.Tasks)
                {
                    AddUsage(db, task.Name, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Quest, quest.ID, null, "Task Name"));
                    AddUsage(db, task.Description, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Quest, quest.ID, null, "Task Description"));
                }

                foreach (var plotItem in quest.PlotItems)
                {
                    AddUsage(db, plotItem.Name, new TlkUsage(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, TlkUsageContext.Quest, quest.ID, null, "Plot Item Name"));
                }
            }
        }

        private static void ScanPropertyCollection(PropertyCollection properties, ConcurrentAssetDB db, int fileKey, int uIndex, bool isInDlc, bool isInMod, string referencePrefix)
        {
            if (properties == null)
            {
                return;
            }

            foreach (var property in properties)
            {
                var propertyName = property.Name.Name;
                var referenceName = string.IsNullOrWhiteSpace(referencePrefix) ? propertyName : $"{referencePrefix}.{propertyName}";

                switch (property)
                {
                    case StringRefProperty stringRefProperty:
                        AddUsage(db, stringRefProperty.Value, new TlkUsage(fileKey, uIndex, isInDlc, isInMod, TlkUsageContext.Package, null, null, referenceName));
                        break;
                    case StructProperty structProperty:
                        ScanPropertyCollection(structProperty.Properties, db, fileKey, uIndex, isInDlc, isInMod, referenceName);
                        break;
                    case ArrayProperty<StringRefProperty> stringRefArray:
                        for (int i = 0; i < stringRefArray.Count; i++)
                        {
                            AddUsage(db, stringRefArray[i].Value, new TlkUsage(fileKey, uIndex, isInDlc, isInMod, TlkUsageContext.Package, null, null, $"{referenceName}[{i}]"));
                        }
                        break;
                    case ArrayProperty<StructProperty> structArray:
                        for (int i = 0; i < structArray.Count; i++)
                        {
                            ScanPropertyCollection(structArray[i].Properties, db, fileKey, uIndex, isInDlc, isInMod, $"{referenceName}[{i}]/{structArray[i].StructType}");
                        }
                        break;
                }
            }
        }

        private static void ScanGame3CoalescedContent(string content, string innerFileName, int fileKey, bool isInDlc, bool isInMod, ConcurrentAssetDB db)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            foreach (Match match in CoalescedTlkReferenceRegex.Matches(content))
            {
                if (int.TryParse(match.Value, out int matchedValue))
                {
                    AddUsage(db, matchedValue, new TlkUsage(fileKey, 0, isInDlc, isInMod, TlkUsageContext.Coalesced, null, innerFileName, "XML Text"));
                }
            }

            try
            {
                var document = XDocument.Parse(content, LoadOptions.None);
                foreach (var property in document.Descendants("Property"))
                {
                    var key = property.Attribute("name")?.Value;
                    if (TryParsePotentialTlkStringId(property.Value, out int directValue))
                    {
                        AddUsage(db, directValue, new TlkUsage(fileKey, 0, isInDlc, isInMod, TlkUsageContext.Coalesced, null, innerFileName, key));
                    }

                    foreach (var valueElement in property.Elements("Value"))
                    {
                        if (TryParsePotentialTlkStringId(valueElement.Value, out int nestedValue))
                        {
                            AddUsage(db, nestedValue, new TlkUsage(fileKey, 0, isInDlc, isInMod, TlkUsageContext.Coalesced, null, innerFileName, key));
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void ScanLegacyCoalescedContent(string content, string innerFileName, int fileKey, bool isInDlc, bool isInMod, ConcurrentAssetDB db)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            foreach (Match match in CoalescedIniAssignmentRegex.Matches(content))
            {
                var key = match.Groups["key"].Value.Trim();
                if (!LooksLikeTlkKey(key))
                {
                    continue;
                }

                if (int.TryParse(match.Groups["value"].Value, out int stringId))
                {
                    AddUsage(db, stringId, new TlkUsage(fileKey, 0, isInDlc, isInMod, TlkUsageContext.Coalesced, null, innerFileName, key));
                }
            }
        }

        private static bool TryParsePotentialTlkStringId(string value, out int stringId)
        {
            stringId = 0;
            var trimmedValue = value?.Trim();
            return !string.IsNullOrWhiteSpace(trimmedValue)
                   && LargeIntegerRegex.IsMatch(trimmedValue)
                   && int.TryParse(trimmedValue, out stringId)
                   && stringId > 0;
        }

        private static void InferCoalescedFlags(string filePath, out bool isInDlc, out bool isInMod)
        {
            isInMod = filePath.Contains("DLC_MOD", StringComparison.OrdinalIgnoreCase);
            isInDlc = !isInMod && filePath.Contains("DLC_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeTlkKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return TlkLikeKeyTokens.Any(token => key.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static void AddUsage(ConcurrentAssetDB db, int stringId, TlkUsage usage)
        {
            if (stringId <= 0)
            {
                return;
            }

            var record = db.GeneratedTlkStrings.GetOrAdd(stringId, static id => new TlkStringRecord(id));
            lock (record)
            {
                if (!record.Usages.Contains(usage))
                {
                    record.Usages.Add(usage);
                }
            }
        }
    }
}
