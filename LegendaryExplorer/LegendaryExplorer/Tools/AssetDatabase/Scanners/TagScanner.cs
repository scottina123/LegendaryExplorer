using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.AssetDatabase.Scanners
{
    internal sealed class TagScanner : AssetScanner
    {
        private readonly Dictionary<int, string> _directTags = [];
        private readonly Dictionary<int, PropertyCollection> _parsedProperties = [];
        private readonly List<ScopedExport> _scopedExports = [];

        public override void ScanExport(ExportScanInfo e, ConcurrentAssetDB db, AssetDBScanOptions options)
        {
            bool isInReferenceScope = !e.IsDefault && IsUnderPersistentLevelOrSequence(e.Export);
            if (isInReferenceScope)
            {
                _scopedExports.Add(new ScopedExport(e.Export, e.FileKey, e.IsDlc, e.IsMod, e.ObjectNameInstanced, e.ClassName));
            }

            // ClassScanner already performs a cheap raw parse of the top-level property headers.
            // Only materialize properties here when it found an actual Tag declaration.
            if (!e.HasTopLevelTagProperty)
            {
                return;
            }

            PropertyCollection properties = e.Properties;
            string tag = NormalizeTag(GetTag(properties));
            if (tag is null)
            {
                return;
            }

            _directTags[e.Export.UIndex] = tag;
            if (isInReferenceScope)
            {
                _parsedProperties[e.Export.UIndex] = properties;
            }

            // Default objects may supply an inherited tag, but they are not themselves search results.
            if (!e.IsDefault)
            {
                AddUsage(db, tag, CreateUsage(e, TagUsageContext.TaggedObject, "Tag"));
            }
        }

        public void CompletePackageScan(IMEPackage package, ConcurrentAssetDB db, Func<bool> isCanceled)
        {
            if (_scopedExports.Count == 0 || isCanceled())
            {
                return;
            }

            // Resolve effective tags once per package. Following a reference now becomes a dictionary
            // lookup instead of repeatedly deserializing the referenced export and its archetype chain.
            var effectiveTags = new Dictionary<int, string>(_directTags);
            foreach (ExportEntry export in package.Exports)
            {
                if (TryResolveEffectiveTag(export, effectiveTags, out string tag))
                {
                    effectiveTags[export.UIndex] = tag;
                }
            }

            HashSet<int> tagReferenceNameIndices = GetTagReferenceNameIndices(package);
            foreach (ScopedExport scopedExport in _scopedExports)
            {
                if (isCanceled())
                {
                    return;
                }

                string archetypeTag = null;
                bool hasArchetypeTag = scopedExport.Export.Archetype is ExportEntry archetype
                                       && TryResolveEffectiveTag(archetype, effectiveTags, out archetypeTag);

                (bool hasTagReferenceCandidate, bool hasObjectReferenceCandidate) =
                    FindPotentialReferences(scopedExport.Export, tagReferenceNameIndices, effectiveTags);
                if (!hasArchetypeTag && !hasTagReferenceCandidate && !hasObjectReferenceCandidate)
                {
                    continue;
                }

                var indexedUsages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (hasArchetypeTag)
                {
                    AddUsage(db, archetypeTag,
                        CreateUsage(scopedExport, TagUsageContext.ArchetypeReference, "Archetype"), indexedUsages);
                }

                if (!hasTagReferenceCandidate && !hasObjectReferenceCandidate)
                {
                    continue;
                }

                PropertyCollection properties;
                try
                {
                    if (!_parsedProperties.TryGetValue(scopedExport.Export.UIndex, out properties))
                    {
                        properties = scopedExport.Export.GetProperties();
                    }
                }
                catch
                {
                    continue;
                }

                if (hasTagReferenceCandidate)
                {
                    ScanTagProperties(properties, scopedExport, db, indexedUsages);
                }

                if (hasObjectReferenceCandidate)
                {
                    foreach ((int uIndex, string reference) in EnumerateObjectReferences(properties))
                    {
                        if (effectiveTags.TryGetValue(uIndex, out string referencedTag))
                        {
                            AddUsage(db, referencedTag,
                                CreateUsage(scopedExport, TagUsageContext.ObjectReference, reference), indexedUsages);
                        }
                    }
                }
            }
        }

        private bool TryResolveEffectiveTag(ExportEntry export, Dictionary<int, string> effectiveTags, out string tag)
        {
            int remainingDepth = export.FileRef.ExportCount + 1;
            for (ExportEntry current = export; current is not null && remainingDepth-- > 0;
                 current = current.Archetype as ExportEntry)
            {
                if (effectiveTags.TryGetValue(current.UIndex, out tag)
                    || _directTags.TryGetValue(current.UIndex, out tag))
                {
                    return true;
                }
            }

            tag = null;
            return false;
        }

        private static (bool HasTagReference, bool HasObjectReference) FindPotentialReferences(
            ExportEntry export,
            HashSet<int> tagReferenceNameIndices,
            Dictionary<int, string> effectiveTags)
        {
            bool checkTagReferences = tagReferenceNameIndices.Count > 0;
            bool checkObjectReferences = effectiveTags.Count > 0;
            if (!checkTagReferences && !checkObjectReferences)
            {
                return (false, false);
            }

            // propsEnd() uses a lightweight header walk on native-endian packages. For uncommon
            // non-native packages, retain the exact behavior by allowing the full property scan.
            if (!export.FileRef.Endian.IsNative)
            {
                return (checkTagReferences, checkObjectReferences);
            }

            ReadOnlySpan<byte> data = export.DataReadOnly;
            int start = export.GetPropertyStart();
            int end;
            try
            {
                end = Math.Min(export.propsEnd(), data.Length);
            }
            catch
            {
                return (checkTagReferences, checkObjectReferences);
            }

            bool hasTagReference = false;
            bool hasObjectReference = false;
            for (int pos = start; pos + sizeof(int) <= end; pos++)
            {
                int value = MemoryMarshal.Read<int>(data[pos..]);
                if (!hasTagReference && value >= 0 && value < export.FileRef.NameCount
                    && tagReferenceNameIndices.Contains(value))
                {
                    hasTagReference = true;
                }

                if (!hasObjectReference && value > 0 && value <= export.FileRef.ExportCount
                    && effectiveTags.ContainsKey(value))
                {
                    hasObjectReference = true;
                }

                if ((!checkTagReferences || hasTagReference) && (!checkObjectReferences || hasObjectReference))
                {
                    break;
                }
            }

            return (hasTagReference, hasObjectReference);
        }

        private static HashSet<int> GetTagReferenceNameIndices(IMEPackage package)
        {
            var indices = new HashSet<int>();
            for (int i = 0; i < package.NameCount; i++)
            {
                if (IsTagReferenceProperty(package.GetNameEntry(i)))
                {
                    indices.Add(i);
                }
            }

            return indices;
        }

        private static bool IsUnderPersistentLevelOrSequence(ExportEntry export)
        {
            for (IEntry ancestor = export.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (string.Equals(ancestor.ObjectName.Name, "PersistentLevel", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ancestor.ClassName, "Sequence", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetTag(PropertyCollection properties)
        {
            if (properties?.GetProp<NameProperty>("Tag") is { } nameTag)
            {
                return nameTag.Value.Instanced;
            }

            return properties?.GetProp<StrProperty>("Tag")?.Value;
        }

        private static void ScanTagProperties(
            PropertyCollection properties,
            ScopedExport e,
            ConcurrentAssetDB db,
            HashSet<string> indexedUsages,
            string prefix = null)
        {
            if (properties is null)
            {
                return;
            }

            foreach (Property property in properties)
            {
                string propertyName = property.Name.Name;
                string reference = string.IsNullOrWhiteSpace(prefix) ? propertyName : $"{prefix}.{propertyName}";

                switch (property)
                {
                    case NameProperty nameProperty when IsTagReferenceProperty(propertyName):
                        AddUsage(db, nameProperty.Value.Instanced, CreateUsage(e, TagUsageContext.TagPropertyReference, reference), indexedUsages);
                        break;
                    case StrProperty stringProperty when IsTagReferenceProperty(propertyName):
                        AddUsage(db, stringProperty.Value, CreateUsage(e, TagUsageContext.TagPropertyReference, reference), indexedUsages);
                        break;
                    case StructProperty structProperty:
                        ScanTagProperties(structProperty.Properties, e, db, indexedUsages, reference);
                        break;
                    case ArrayProperty<NameProperty> nameArray when IsTagReferenceProperty(propertyName):
                        for (int i = 0; i < nameArray.Count; i++)
                        {
                            AddUsage(db, nameArray[i].Value.Instanced, CreateUsage(e, TagUsageContext.TagPropertyReference, $"{reference}[{i}]"), indexedUsages);
                        }
                        break;
                    case ArrayProperty<StrProperty> stringArray when IsTagReferenceProperty(propertyName):
                        for (int i = 0; i < stringArray.Count; i++)
                        {
                            AddUsage(db, stringArray[i].Value, CreateUsage(e, TagUsageContext.TagPropertyReference, $"{reference}[{i}]"), indexedUsages);
                        }
                        break;
                    case ArrayProperty<StructProperty> structArray:
                        for (int i = 0; i < structArray.Count; i++)
                        {
                            ScanTagProperties(structArray[i].Properties, e, db, indexedUsages, $"{reference}[{i}]");
                        }
                        break;
                }
            }
        }

        private static bool IsTagReferenceProperty(string propertyName) =>
            !string.Equals(propertyName, "Tag", StringComparison.OrdinalIgnoreCase)
            && propertyName?.Contains("Tag", StringComparison.OrdinalIgnoreCase) == true;

        private static IEnumerable<(int UIndex, string Reference)> EnumerateObjectReferences(PropertyCollection properties, string prefix = null)
        {
            if (properties is null)
            {
                yield break;
            }

            foreach (Property property in properties)
            {
                string propertyName = property.Name.Name;
                string reference = string.IsNullOrWhiteSpace(prefix) ? propertyName : $"{prefix}.{propertyName}";

                switch (property)
                {
                    case ObjectProperty objectProperty when objectProperty.Value != 0:
                        yield return (objectProperty.Value, reference);
                        break;
                    case DelegateProperty delegateProperty when delegateProperty.Value.ContainingObjectUIndex != 0:
                        yield return (delegateProperty.Value.ContainingObjectUIndex, reference);
                        break;
                    case StructProperty structProperty:
                        foreach (var nestedReference in EnumerateObjectReferences(structProperty.Properties, reference))
                        {
                            yield return nestedReference;
                        }
                        break;
                    case ArrayProperty<ObjectProperty> objectArray:
                        for (int i = 0; i < objectArray.Count; i++)
                        {
                            if (objectArray[i].Value != 0)
                            {
                                yield return (objectArray[i].Value, $"{reference}[{i}]");
                            }
                        }
                        break;
                    case ArrayProperty<StructProperty> structArray:
                        for (int i = 0; i < structArray.Count; i++)
                        {
                            foreach (var nestedReference in EnumerateObjectReferences(structArray[i].Properties, $"{reference}[{i}]"))
                            {
                                yield return nestedReference;
                            }
                        }
                        break;
                }
            }
        }

        private static TagUsage CreateUsage(ExportScanInfo e, TagUsageContext context, string reference) =>
            new(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, e.ObjectNameInstanced, e.ClassName, context, reference);

        private static TagUsage CreateUsage(ScopedExport e, TagUsageContext context, string reference) =>
            new(e.FileKey, e.Export.UIndex, e.IsDlc, e.IsMod, e.ObjectName, e.ClassName, context, reference);

        private static string NormalizeTag(string tag)
        {
            string normalizedTag = tag?.Trim();
            return string.IsNullOrWhiteSpace(normalizedTag)
                   || string.Equals(normalizedTag, "None", StringComparison.OrdinalIgnoreCase)
                ? null
                : normalizedTag;
        }

        private static void AddUsage(
            ConcurrentAssetDB db,
            string tag,
            TagUsage usage,
            HashSet<string> indexedUsages = null)
        {
            string normalizedTag = NormalizeTag(tag);
            if (normalizedTag is null
                || indexedUsages is not null && !indexedUsages.Add($"{(int)usage.Context}\0{normalizedTag}"))
            {
                return;
            }

            TagRecord record = db.GeneratedTags.GetOrAdd(normalizedTag, static value => new TagRecord(value));
            lock (record)
            {
                record.Usages.Add(usage);
            }
        }

        private readonly record struct ScopedExport(
            ExportEntry Export,
            int FileKey,
            bool IsDlc,
            bool IsMod,
            string ObjectName,
            string ClassName);
    }
}
