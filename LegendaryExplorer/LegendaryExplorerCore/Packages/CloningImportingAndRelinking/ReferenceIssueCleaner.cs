using System;
using System.Collections.Generic;
using System.IO;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

/// <summary>Changes successfully applied by <see cref="ReferenceIssueCleaner"/> to an open package.</summary>
public sealed class ReferenceCleanupResult
{
    public int RemovedPropertyCount { get; internal set; }
    public int ClearedPropertyReferenceCount { get; internal set; }
    public int ClearedBinaryReferenceCount { get; internal set; }
    public IReadOnlyList<ExportEntry> ChangedExports => changedExports;
    public IReadOnlyList<EntryStringPair> Failures => failures;

    internal readonly List<ExportEntry> changedExports = [];
    internal readonly List<EntryStringPair> failures = [];
}

/// <summary>Repairs invalid, trashed and wrong-type property references, without changing entry headers.</summary>
public static class ReferenceIssueCleaner
{
    /// <summary>Checks table membership and trash entries. Property typing is checked separately using its metadata.</summary>
    public static bool IsBadReference(IMEPackage package, int uIndex)
    {
        if (uIndex == 0)
            return false;
        if (!package.TryGetEntry(uIndex, out var entry))
            return true;

        string objectName = entry.ObjectName.Name;
        return string.Equals(objectName, UnrealPackageFile.TrashPackageName, StringComparison.OrdinalIgnoreCase)
               || entry.ClassName == "Package" && string.Equals(objectName, "Trash", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes bad tagged object/delegate properties and bad array elements, recursively, including object references
    /// whose types do not match the property metadata. Fixed-layout struct fields
    /// are cleared instead of removed so their layout and valid sibling values remain intact. Typed binary references
    /// are set to zero. Each export is written only after its properties and supported binary have been read successfully;
    /// failed exports are left unchanged. This edits the open package and does not save it to disk.
    /// </summary>
    public static ReferenceCleanupResult RemoveBadReferences(IMEPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var result = new ReferenceCleanupResult();
        foreach (var export in package.Exports)
        {
            var changes = new ReferenceCleanupResult();
            string stage = "read properties";
            try
            {
                // Component property offsets walk the outer chain. Check it before invoking any parser
                // or path formatter, which otherwise may never return for a cyclic outer reference.
                ValidateOuterChain(export);
                // GetProperties can return a partial collection instead of throwing on malformed data.
                var properties = export.GetProperties(includeNoneProperties: true);
                ValidateProperties(properties);
                var cleanedProperties = properties.DeepClone();
                CleanProperties(cleanedProperties, export, export.ClassName, changes);
                bool propertiesChanged = changes.RemovedPropertyCount + changes.ClearedPropertyReferenceCount > 0;

                stage = "read pre-property binary";
                int propertyStart = export.GetPropertyStart();
                byte[] preProperties = export.DataReadOnly[..propertyStart].ToArray();
                if (export.HasStack)
                {
                    ClearPrePropertyReference(preProperties, 0, package, changes);
                    ClearPrePropertyReference(preProperties, 4, package, changes);
                }
                else if (export.TemplateOwnerClassIdx is int templateOwnerOffset and >= 0)
                {
                    ClearPrePropertyReference(preProperties, templateOwnerOffset, package, changes);
                }
                int clearedPreProperties = changes.ClearedBinaryReferenceCount;

                stage = "read binary";
                ObjectBinary binary = null;
                byte[] originalMeshBinary = null;
                List<(int Value, int Offset, int ExpectedValue)> meshFileOffsets = null;
                if (!export.IsDefaultObject && !(export.Game == MEGame.UDK && export.ClassName == "ShaderCache"))
                {
                    binary = ObjectBinary.From(export);
                    if (binary is not null && binary is not GenericObjectBinary
                        && (propertiesChanged || binary.GetUIndexes(package.Game).Exists(index => IsBadReference(package, index))))
                    {
                        if (binary is StaticMesh)
                        {
                            // StaticMesh saving normalizes mesh fields and can discard unparsed bytes. Its known
                            // reference slots can instead be cleared in the original binary without those changes.
                            (originalMeshBinary, meshFileOffsets) = ClearOriginalMeshReferences(binary, export, changes);
                        }
                        else
                        {
                            // Other converters must reproduce the original binary before we trust them to rewrite it.
                            if (binary.GetNames(package.Game).Exists(name => string.IsNullOrEmpty(name.Item1.Name)))
                                throw new InvalidDataException("The binary contains an unresolved name reference.");
                            byte[] roundTrip = binary.ToBytes(package, export.DataOffset + export.propsEnd());
                            if (!export.GetBinaryData().AsSpan().SequenceEqual(roundTrip))
                                throw new InvalidDataException("The binary does not round-trip without changes.");
                        }
                    }
                    if (originalMeshBinary is null)
                        binary?.ForEachUIndex(package.Game, new ClearBadBinaryReferences(package, changes));
                }

                if (!propertiesChanged && changes.ClearedBinaryReferenceCount == 0)
                    continue;

                stage = "write repaired data";
                if (propertiesChanged)
                {
                    // Refuse to rewrite a partial/lossy property parse, including unknown array layouts.
                    using var originalProperties = new MemoryStream();
                    properties.WriteTo(new EndianWriter(originalProperties) { Endian = package.Endian }, package);
                    int propertyLength = export.propsEnd() - propertyStart;
                    if (!export.DataReadOnly.Slice(propertyStart, propertyLength).SequenceEqual(originalProperties.ToArray()))
                        throw new InvalidDataException("The properties do not round-trip without changes.");
                }

                // Construct the complete replacement before assigning Data, so a serialization failure cannot
                // leave a partially repaired export. Keep the original property bytes when only binary changed.
                using var output = new MemoryStream();
                var writer = new EndianWriter(output) { Endian = package.Endian };
                writer.Write(preProperties);
                if (propertiesChanged)
                    cleanedProperties.WriteTo(writer, package);
                else
                    writer.Write(export.DataReadOnly.Slice(propertyStart, export.propsEnd() - propertyStart));
                if (originalMeshBinary is not null)
                    WriteOriginalMeshBinary(originalMeshBinary, meshFileOffsets, writer, export);
                else if (binary is not null && binary is not GenericObjectBinary
                    && (propertiesChanged || changes.ClearedBinaryReferenceCount > clearedPreProperties))
                    WriteCleanedBinary(binary, writer, export, cleanedProperties);
                else
                    writer.Write(export.GetBinaryData());
                export.Data = output.ToArray();

                result.RemovedPropertyCount += changes.RemovedPropertyCount;
                result.ClearedPropertyReferenceCount += changes.ClearedPropertyReferenceCount;
                result.ClearedBinaryReferenceCount += changes.ClearedBinaryReferenceCount;
                result.changedExports.Add(export);
            }
            catch (Exception exception)
            {
                result.failures.Add(new EntryStringPair(export, $"Export {export.UIndex}: Could not {stage}: {exception.Message}"));
            }
        }

        return result;
    }

    private static (byte[] Binary, List<(int Value, int Offset, int ExpectedValue)> FileOffsets) ClearOriginalMeshReferences(
        ObjectBinary binary, ExportEntry export, ReferenceCleanupResult changes)
    {
        var expectedReferences = new Dictionary<int, int>();
        foreach (int reference in binary.GetUIndexes(export.Game))
        {
            if (IsBadReference(export.FileRef, reference))
            {
                expectedReferences.TryGetValue(reference, out int count);
                expectedReferences[reference] = count + 1;
            }
        }

        var (references, fileOffsets) = binary.GetSerializedOffsets();
        byte[] originalBinary = export.GetBinaryData();
        int binaryStart = export.propsEnd();
        var referenceSlots = new HashSet<int>();
        foreach (var reference in references)
        {
            int offset = reference.Offset - binaryStart;
            ValidateOriginalBinaryValue(originalBinary, offset, reference.UIndex, export.FileRef);
            if (!referenceSlots.Add(offset))
                throw new InvalidDataException("The mesh contains overlapping reference fields.");
            if (!IsBadReference(export.FileRef, reference.UIndex))
                continue;
            if (!expectedReferences.TryGetValue(reference.UIndex, out int remaining) || remaining == 0)
                throw new InvalidDataException("The mesh's serialized references do not match its reference list.");
            expectedReferences[reference.UIndex] = remaining - 1;
            originalBinary.AsSpan(offset, sizeof(int)).Clear();
            changes.ClearedBinaryReferenceCount++;
        }
        foreach (int remaining in expectedReferences.Values)
        {
            if (remaining != 0)
                throw new InvalidDataException("A bad mesh reference could not be located in the original binary.");
        }

        foreach (var fileOffset in fileOffsets)
        {
            int offset = fileOffset.Offset - binaryStart;
            ValidateOriginalBinaryValue(originalBinary, offset, fileOffset.Value, export.FileRef);
            if (!referenceSlots.Add(offset))
                throw new InvalidDataException("The mesh contains overlapping offset fields.");
        }
        return (originalBinary, fileOffsets);
    }

    private static void ValidateOriginalBinaryValue(byte[] binary, int offset, int expectedValue, IMEPackage package)
    {
        if (offset < 0 || offset > binary.Length - sizeof(int)
            || EndianReader.ToInt32(binary, offset, package.Endian) != expectedValue)
            throw new InvalidDataException("A mesh field could not be verified in the original binary.");
    }

    private static void WriteOriginalMeshBinary(byte[] binary, List<(int Value, int Offset, int ExpectedValue)> fileOffsets,
        EndianWriter writer, ExportEntry export)
    {
        int originalBinaryStart = export.propsEnd();
        int shift = checked((int)writer.BaseStream.Position - originalBinaryStart);
        if (shift != 0)
        {
            using var binaryWriter = new EndianWriter(new MemoryStream(binary, writable: true)) { Endian = export.FileRef.Endian };
            foreach (var fileOffset in fileOffsets)
            {
                // Meshes can store inline pointers as absolute file offsets or relative to the export. Preserve
                // their original convention, rebasing only pointers that address this known inline payload.
                if (fileOffset.Value != fileOffset.ExpectedValue
                    && fileOffset.Value != fileOffset.ExpectedValue - export.DataOffset)
                    continue;
                binaryWriter.BaseStream.Position = fileOffset.Offset - originalBinaryStart;
                binaryWriter.Write(checked(fileOffset.Value + shift));
            }
        }
        writer.Write(binary);
    }

    private static void WriteCleanedBinary(ObjectBinary binary, EndianWriter writer, ExportEntry export,
        PropertyCollection cleanedProperties)
    {
        if (binary is StaticCollectionActor collection)
        {
            // The transform at each index belongs to the component at the same index in the property array.
            // The regular converter uses the ORIGINAL export's component count while writing; serialize the
            // retained pairs here so removing a property array element also removes its transform atomically.
            if (collection.Components.Count != collection.LocalToWorldTransforms.Count)
                throw new InvalidDataException("The static collection's component and transform counts do not match.");
            var retainedComponents = cleanedProperties.GetProp<ArrayProperty<ObjectProperty>>(collection.ComponentPropName);
            int retainedCount = retainedComponents?.Count ?? 0;
            int retainedIndex = 0;
            var serializer = new SerializingContainer(writer.BaseStream, export.FileRef, offset: export.DataOffset);
            for (int i = 0; i < collection.Components.Count; i++)
            {
                // The cleaned array is a subsequence of the original. Use those same cleanup decisions for
                // transforms, including wrong-type references and repeated component indices.
                if (retainedIndex == retainedCount || collection.Components[i] != retainedComponents[retainedIndex].Value)
                    continue;
                var transform = collection.LocalToWorldTransforms[i];
                serializer.Serialize(ref transform);
                retainedIndex++;
            }
            if (retainedIndex != retainedCount)
                throw new InvalidDataException("The retained static collection components do not match their transforms.");
        }
        else
        {
            binary.WriteTo(writer, export.FileRef, export.DataOffset);
        }
    }

    internal static void ValidateOuterChain(IEntry origin)
    {
        var visited = new HashSet<int>();
        IEntry entry = origin;
        while (entry is not null)
        {
            if (!visited.Add(entry.UIndex))
                throw new InvalidDataException($"The outer chain contains a cycle at entry {entry.UIndex}.");
            if (entry.idxLink == 0)
                return;
            if (!origin.FileRef.TryGetEntry(entry.idxLink, out var parent))
                throw new InvalidDataException($"Entry {entry.UIndex} has an outer reference outside the entry tables ({entry.idxLink}).");
            entry = parent;
        }
    }

    private static void ClearPrePropertyReference(byte[] data, int offset, IMEPackage package, ReferenceCleanupResult changes)
    {
        int reference = EndianReader.ToInt32(data, offset, package.Endian);
        if (IsBadReference(package, reference))
        {
            data.AsSpan(offset, sizeof(int)).Clear();
            changes.ClearedBinaryReferenceCount++;
        }
    }

    private static void ValidateProperties(PropertyCollection properties)
    {
        if (!properties.IsImmutable && (properties.Count == 0 || properties[^1] is not NoneProperty))
            throw new InvalidDataException("The property list is incomplete (missing its None terminator).");

        foreach (var property in properties)
            ValidateProperty(property);
    }

    private static void ValidateProperty(Property property)
    {
        switch (property)
        {
            case UnknownProperty:
                throw new InvalidDataException("The property list contains unparsed property data.");
            case NameProperty name when string.IsNullOrEmpty(name.Value.Name):
            case EnumProperty enumValue when string.IsNullOrEmpty(enumValue.Value.Name):
            case DelegateProperty function when string.IsNullOrEmpty(function.Value.FunctionName.Name):
                throw new InvalidDataException("The property list contains an unresolved name reference.");
            case StructProperty structure:
                if (string.IsNullOrEmpty(structure.StructType))
                    throw new InvalidDataException("The property list contains an unresolved struct name.");
                ValidateProperties(structure.Properties);
                break;
            case ArrayPropertyBase array:
                foreach (var element in array.Properties)
                    ValidateProperty(element);
                break;
        }
    }

    private static void CleanProperties(PropertyCollection properties, ExportEntry export,
        string containingClassOrStructName, ReferenceCleanupResult changes)
    {
        for (int i = properties.Count - 1; i >= 0; i--)
        {
            if (CleanProperty(properties[i], properties.IsImmutable, export, containingClassOrStructName,
                    properties[i].Name, changes))
                properties.RemoveAt(i);
        }
    }

    // Returns true only when the caller must remove the property from its containing tagged list or array.
    private static bool CleanProperty(Property property, bool fixedLayout, ExportEntry export,
        string containingClassOrStructName, NameReference propertyName, ReferenceCleanupResult changes)
    {
        switch (property)
        {
            case ObjectProperty reference when IsBadReference(export.FileRef, reference.Value)
                                               || ObjectReferenceTypeChecker.TryGetMismatch(export, reference,
                                                   containingClassOrStructName, propertyName, out _, out _):
                if (fixedLayout)
                {
                    reference.Value = 0;
                    changes.ClearedPropertyReferenceCount++;
                    return false;
                }
                changes.RemovedPropertyCount++;
                return true;
            case DelegateProperty reference when IsBadReference(export.FileRef, reference.Value.ContainingObjectUIndex):
                if (fixedLayout)
                {
                    reference.Value = new ScriptDelegate(0, reference.Value.FunctionName);
                    changes.ClearedPropertyReferenceCount++;
                    return false;
                }
                changes.RemovedPropertyCount++;
                return true;
            case StructProperty structure:
                CleanProperties(structure.Properties, export, structure.StructType, changes);
                break;
            case ArrayPropertyBase array:
                // Dynamic arrays carry their count even inside fixed-layout structs.
                for (int i = array.Properties.Count - 1; i >= 0; i--)
                {
                    if (CleanProperty(array[i], false, export, containingClassOrStructName, array.Name, changes))
                        array.RemoveAt(i);
                }
                break;
        }

        return false;
    }

    private readonly struct ClearBadBinaryReferences(IMEPackage package, ReferenceCleanupResult changes) : IUIndexAction
    {
        public void Invoke(ref int uIndex, string propName)
        {
            if (IsBadReference(package, uIndex))
            {
                uIndex = 0;
                changes.ClearedBinaryReferenceCount++;
            }
        }
    }
}
