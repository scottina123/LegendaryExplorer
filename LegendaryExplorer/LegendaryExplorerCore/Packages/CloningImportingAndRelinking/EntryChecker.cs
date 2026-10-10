using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorerCore.Packages.CloningImportingAndRelinking
{
    public class ReferenceCheckPackage
    {
        // The list of generated warnings, errors, and blocking errors
        private List<EntryStringPair> BlockingErrors { get; } = [];
        private List<EntryStringPair> SignificantIssues { get; } = [];
        private List<EntryStringPair> InfoWarnings { get; } = [];

        private Lock syncLock = new();

        public IReadOnlyCollection<EntryStringPair> GetBlockingErrors() => BlockingErrors;
        public IReadOnlyCollection<EntryStringPair> GetSignificantIssues() => SignificantIssues;
        public IReadOnlyCollection<EntryStringPair> GetInfoWarnings() => InfoWarnings;

        public void AddBlockingError(string message, IEntry entry = null)
        {
            lock (syncLock)
            {
                BlockingErrors.Add(new EntryStringPair(entry, message));
            }
        }

        public void AddBlockingError(string message, LEXOpenable entry)
        {
            lock (syncLock)
            {
                BlockingErrors.Add(new EntryStringPair(entry, message));
            }
        }

        public void AddBlockingError(string message, IEntry entry, ReferenceIssueLocation location, int offset)
        {
            lock (syncLock)
            {
                BlockingErrors.Add(new ReferenceIssue(entry, message, location, offset));
            }
        }

        public void AddSignificantIssue(string message, IEntry entry = null)
            => AddSignificantIssue(message, entry, ReferenceIssueLocation.Entry);

        public void AddSignificantIssue(string message, IEntry entry, ReferenceIssueLocation location,
            int? offset = null, int? valueOffset = null, int? referencedUIndex = null)
        {
            lock (syncLock)
            {
                SignificantIssues.Add(new ReferenceIssue(entry, message, location, offset, valueOffset, referencedUIndex));
            }
        }

        public void AddSignificantIssue(string message, LEXOpenable entry)
        {
            lock (syncLock)
            {
                SignificantIssues.Add(new EntryStringPair(entry, message));
            }
        }

        public void AddInfoWarning(string message, IEntry entry = null)
        {
            lock (syncLock)
            {
                InfoWarnings.Add(new EntryStringPair(entry, message));
            }
        }

        public void AddInfoWarning(string message, LEXOpenable entry)
        {
            lock (syncLock)
            {
                InfoWarnings.Add(new EntryStringPair(entry, message));
            }
        }

        public void ClearMessages()
        {
            BlockingErrors.Clear();
            SignificantIssues.Clear();
            InfoWarnings.Clear();
        }
    }

    /// <summary>
    /// Checks property types against the database to determine if property types are of the correct typing.
    /// </summary>
    public class EntryChecker
    {
        /// <summary>
        /// Checks object and name references for invalid values and if values are of the incorrect typing. Returns localized messages, if you do not want localized messages, pass it the NonLocalizedStringConverter delegate from this class.
        /// </summary>
        /// <param name="item"></param>
        public static void CheckReferences(ReferenceCheckPackage item, string basePath, LECLocalizationShim.GetLocalizedStringDelegate localizationDelegate, Action<string> statusUpdateDelegate, Action<string> logMessageDelegate = null, List<string> referencedFiles = null, CancellationTokenSource cts = null)
        {
            referencedFiles ??= Directory.GetFiles(basePath, "*.*", SearchOption.AllDirectories).Where(x => x.RepresentsPackageFilePath()).ToList();
            int numChecked = 0;
            Parallel.ForEach(referencedFiles,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(3, Environment.ProcessorCount)
                },
                f =>
                {
                    if (cts != null && cts.IsCancellationRequested)
                        return;
                    //if (!f.Contains("BioA_Nor")) return;
                    var lnumChecked = Interlocked.Increment(ref numChecked);
                    statusUpdateDelegate?.Invoke(localizationDelegate(LECLocalizationShim.string_checkingNameAndObjectReferences) + $@" [{lnumChecked - 1}/{referencedFiles.Count}]");

                    var relativePath = f.Substring(basePath.Length + 1);
                    logMessageDelegate?.Invoke($@"Checking package and name references in {relativePath}");
                    var package = MEPackageHandler.OpenMEPackage(f, forceLoadFromDisk: true);
                    CheckReferences(item, package, localizationDelegate, relativePath, cts);
                });
        }

        public static void CheckReferences(ReferenceCheckPackage item, IMEPackage package, LECLocalizationShim.GetLocalizedStringDelegate localizationDelegate, string relativePath = null, CancellationTokenSource cts = null)
        {
            string fName = Path.GetFileName(package.FilePath);
            foreach (ExportEntry exp in package.Exports)
            {
                if (cts != null && cts.IsCancellationRequested)
                    return;

                // Has to be done before accessing the name because it will cause infinite crash loop
                //Debug.WriteLine($"Checking {exp.UIndex} {exp.InstancedFullPath} in {exp.FileRef.FilePath}");
                if (exp.idxLink == exp.UIndex)
                {
                    item.AddBlockingError(localizationDelegate(LECLocalizationShim.string_interp_fatalExportCircularReference, relativePath ?? fName, exp.UIndex), exp, ReferenceIssueLocation.Header, ExportEntry.OFFSET_idxLink);
                    continue;
                }
                if (!CheckOuterChain(item, exp, relativePath ?? fName))
                    continue;

                // UDK-specific checks.
                if (exp.Game == MEGame.UDK)
                {
                    if (exp.Parent is ImportEntry)
                    {
                        item.AddBlockingError($"UDK does not support exports under imports in non-cooked packages - UDK will crash loading this package file!  Entry: {exp.InstancedFullPath}", exp);
                    }
                }

                var prefix = localizationDelegate(LECLocalizationShim.string_interp_warningGenericExportPrefix, relativePath ?? fName, exp.UIndex, exp.ObjectName.Name, exp.ClassName);
                try
                {
                    checkName(item, localizationDelegate, () => exp.ObjectName, "Object Name", $"export {exp.UIndex}", relativePath, fName, exp);

                    if (exp.idxArchetype != 0)
                    {
                        if (!package.IsEntry(exp.idxArchetype))
                        {
                            item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningArchetypeOutsideTables, prefix, exp.idxArchetype), exp, ReferenceIssueLocation.Header, 0x14);
                        }
                        else if (IsTrashedReference(package.GetEntry(exp.idxArchetype)))
                        {
                            item.AddSignificantIssue($"{prefix} Header Archetype ({exp.idxArchetype}) is a Trashed object", exp, ReferenceIssueLocation.Header, 0x14);
                        }
                    }

                    if (exp.idxSuperClass != 0 && !package.IsEntry(exp.idxSuperClass))
                    {
                        item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningSuperclassOutsideTables, prefix, exp.idxSuperClass), exp, ReferenceIssueLocation.Header, 0x4);
                    }

                    if (exp.idxClass != 0 && !package.IsEntry(exp.idxClass))
                    {
                        item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningClassOutsideTables, prefix, exp.idxClass), exp, ReferenceIssueLocation.Header, 0x0);
                    }

                    if (exp.HasComponentMap)
                    {
                        foreach ((_, int index) in exp.ComponentMap)
                        {
                            int uindex = index + 1;
                            if (uindex != 0 && !package.IsEntry(uindex))
                            {
                                // Can components point to 0? I don't think so
                                item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningComponentMapItemOutsideTables, prefix, uindex), exp);
                            }
                        }
                    }

                    //find stack references
                    if (exp.HasStack && exp.DataReadOnly is var data)
                    {
                        var stack1 = EndianReader.ToInt32(data, 0, exp.FileRef.Endian);
                        var stack2 = EndianReader.ToInt32(data, 4, exp.FileRef.Endian);
                        if (stack1 != 0 && !package.IsEntry(stack1))
                        {
                            item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningExportStackElementOutsideTables, prefix, 0, stack1), exp, ReferenceIssueLocation.Binary, 0, referencedUIndex: stack1);
                        }

                        if (stack2 != 0 && !package.IsEntry(stack2))
                        {
                            item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningExportStackElementOutsideTables, prefix, 1, stack2), exp, ReferenceIssueLocation.Binary, 4, referencedUIndex: stack2);
                        }
                    }
                    else if (exp.TemplateOwnerClassIdx is var toci and >= 0)
                    {
                        var TemplateOwnerClassIdx = EndianReader.ToInt32(exp.DataReadOnly, toci, exp.FileRef.Endian);
                        if (TemplateOwnerClassIdx != 0 && !package.IsEntry(TemplateOwnerClassIdx))
                        {
                            item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningTemplateOwnerClassOutsideTables, prefix, toci.ToString(@"X6"), TemplateOwnerClassIdx), exp, ReferenceIssueLocation.Binary, toci, referencedUIndex: TemplateOwnerClassIdx);
                        }
                    }

                    var props = exp.GetProperties();
                    foreach (var p in props)
                    {
                        recursiveCheckProperty(item, localizationDelegate, relativePath ?? fName, exp.ClassName, exp, p);
                    }
                }
                catch (Exception e)
                {
                    item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningExceptionParsingProperties, prefix, e.Message), exp);
                    continue;
                }

                //find binary references - DO THIS LAST IN THE LOOP
                try
                {
                    if (exp.Game == MEGame.UDK && exp.ClassName == "ShaderCache")
                        continue; // ShaderCache parsing is not working //01/11/2025
                    if (!exp.IsDefaultObject && ObjectBinary.From(exp) is ObjectBinary objBin)
                    {
                        List<int> indices = objBin.GetUIndexes(exp.FileRef.Game);
                        var referenceOffsets = indices.Any(index => ReferenceIssueCleaner.IsBadReference(package, index))
                            ? GetBinaryReferenceOffsets(objBin, indices)
                            : new Dictionary<int, Queue<int>>();
                        foreach (int uIndex in indices)
                        {
                            int? offset = referenceOffsets.TryGetValue(uIndex, out var offsets) && offsets.Count > 0
                                ? offsets.Dequeue()
                                : null;
                            if (uIndex != 0 && !exp.FileRef.IsEntry(uIndex))
                            {
                                item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningBinaryReferenceOutsideTables, prefix, uIndex), exp,
                                    ReferenceIssueLocation.Binary, offset, referencedUIndex: uIndex);
                            }
                            else if (ReferenceIssueCleaner.IsBadReference(package, uIndex))
                            {
                                item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningBinaryReferenceTrashed, prefix, uIndex), exp,
                                    ReferenceIssueLocation.Binary, offset, referencedUIndex: uIndex);
                            }
                        }

                        var nameIndicies = objBin.GetNames(exp.FileRef.Game);
                        foreach (var ni in nameIndicies)
                        {
                            if (ni.Item1 == "")
                            {
                                item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningBinaryNameReferenceOutsideNameTable, prefix), exp);
                            }
                        }
                    }
                }
                catch (Exception e) /* when (!App.IsDebug)*/
                {
                    item.AddSignificantIssue(localizationDelegate(LECLocalizationShim.string_interp_warningUnableToParseBinary, prefix, e.Message), exp);
                }
            }

            foreach (ImportEntry imp in package.Imports)
            {
                if (cts != null && cts.IsCancellationRequested)
                    return;
                if (imp.idxLink == imp.UIndex)
                {
                    item.AddBlockingError(localizationDelegate(LECLocalizationShim.string_interp_fatalImportCircularReference, relativePath ?? fName, imp.UIndex), imp, ReferenceIssueLocation.Header, ImportEntry.OFFSET_idxLink);
                    continue;
                }
                if (!CheckOuterChain(item, imp, relativePath ?? fName))
                    continue;

                // UDK specific checks.
                if (imp.Game == MEGame.UDK)
                {
                    if (imp.ObjectName == "SFXGame" && imp.Parent == null)
                    {
                        item.AddBlockingError("UDK does not work with SFXGame imports!");
                    }
                }

                if (imp.Game == MEGame.UDK && imp.Parent is ExportEntry)
                {
                    item.AddBlockingError($"UDK does not support imports under exports in non-cooked packages - UDK will crash loading this package file! Entry: {imp.InstancedFullPath}", imp);
                }

                // Values check
                checkName(item, localizationDelegate, () => imp.PackageFile, "Package file", $"import {imp.UIndex}", relativePath, fName, imp);
                checkName(item, localizationDelegate, () => imp.ClassName, "Class name", $"import {imp.UIndex}", relativePath, fName, imp);
            }
        }

        private static bool CheckOuterChain(ReferenceCheckPackage item, IEntry entry, string fileName)
        {
            try
            {
                ReferenceIssueCleaner.ValidateOuterChain(entry);
                return true;
            }
            catch (InvalidDataException exception)
            {
                item.AddSignificantIssue($"{fileName}, entry {entry.UIndex}: {exception.Message}", entry,
                    ReferenceIssueLocation.Header,
                    entry is ExportEntry ? ExportEntry.OFFSET_idxLink : ImportEntry.OFFSET_idxLink);
                return false;
            }
        }

        private static Dictionary<int, Queue<int>> GetBinaryReferenceOffsets(ObjectBinary binary, List<int> indices)
        {
            try
            {
                var counts = indices.GroupBy(index => index).ToDictionary(group => group.Key, group => group.Count());
                // ForEachUIndex need not visit fields in serialization order. Equal references are interchangeable
                // for reporting, but only assign offsets if the two readers agree on every occurrence of that value.
                return binary.GetUIndexOffsets().GroupBy(reference => reference.UIndex)
                    .Where(group => counts.TryGetValue(group.Key, out int count) && count == group.Count())
                    .ToDictionary(group => group.Key, group => new Queue<int>(group.Select(reference => reference.Offset)));
            }
            catch
            {
                // Navigation metadata is optional; an older converter must not suppress actual reference warnings.
                return new Dictionary<int, Queue<int>>();
            }
        }

        private static void checkName(ReferenceCheckPackage item, LECLocalizationShim.GetLocalizedStringDelegate localizationDelegate,
            Func<string> getName, string nameBeingChecked, string itemBeingChecked, string relativePath, string fName, IEntry entry)
        {
            try
            {
                // Can't access idx vars so we have to do this
                var pf = getName();
            }
            catch (Exception)
            {
                item.AddBlockingError(localizationDelegate(LECLocalizationShim.string_interp_refCheckInvalidNameValue, relativePath ?? fName, nameBeingChecked, itemBeingChecked), entry);
            }
        }

        private static bool IsTrashedReference(IEntry entry)
        {
            if (entry is null)
            {
                return false;
            }

            string objectName = entry.ObjectName.Name;
            return objectName.CaseInsensitiveEquals(UnrealPackageFile.TrashPackageName)
                || entry.ClassName == "Package" && objectName.CaseInsensitiveEquals("Trash");
        }

        private static void recursiveCheckProperty(ReferenceCheckPackage item, LECLocalizationShim.GetLocalizedStringDelegate localizationDelegate, string relativePath, string containingClassOrStructName, IEntry entry, Property property, NameReference? arrayPropertyName = null)
        {
            void AddPropertyIssue(string message) => item.AddSignificantIssue(message, entry,
                ReferenceIssueLocation.Property, property.StartOffset, property.ValueOffset,
                property switch
                {
                    ObjectProperty op => op.Value,
                    DelegateProperty dp => dp.Value.ContainingObjectUIndex,
                    _ => null
                });
            var prefix = localizationDelegate(LECLocalizationShim.string_interp_warningPropertyTypingWrongPrefix, relativePath, entry.UIndex, entry.ObjectName.Instanced, entry.ClassName, property.StartOffset.ToString(@"X6"));
            if (property is UnknownProperty up)
            {
                AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_warningFoundBrokenPropertyData, prefix));
            }
            else if (property is ObjectProperty op)
            {
                bool validRef = true;
                if (op.Value > 0 && op.Value > entry.FileRef.ExportCount)
                {
                    //bad
                    if (op.Name.Name != null)
                    {
                        AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_warningReferenceNotInExportTable, prefix, op.Name.Name, op.Value));
                        validRef = false;
                    }
                    else
                    {
                        AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_nested_warningReferenceNoInExportTable, prefix, op.Value));
                        validRef = false;
                    }
                }
                else if (op.Value < 0 && !entry.FileRef.IsEntry(op.Value))
                {
                    //bad
                    if (op.Name.Name != null)
                    {
                        AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_warningReferenceNotInImportTable, prefix, op.Name.Name, op.Value));
                        validRef = false;
                    }
                    else
                    {
                        AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_nested_warningReferenceNoInImportTable, prefix, op.Value));
                        validRef = false;
                    }
                }
                else if (ReferenceIssueCleaner.IsBadReference(entry.FileRef, op.Value))
                {
                    AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_nested_warningTrashedExportReference,
                            prefix, op.Value));
                    validRef = false;
                }

                // Check object is of correct typing?
                if (validRef && ObjectReferenceTypeChecker.TryGetMismatch(entry, op, containingClassOrStructName,
                        arrayPropertyName ?? op.Name, out string expectedType, out bool expectsClass))
                {
                    var referencedEntry = op.ResolveToEntry(entry.FileRef);
                    if (expectsClass)
                    {
                        if (op.Name.Name != null)
                        {
                            AddPropertyIssue(localizationDelegate(
                                LECLocalizationShim.string_interp_warningWrongPropertyTypingWrongMessage, prefix,
                                op.Name.Name, op.Value, referencedEntry.InstancedFullPath,
                                expectedType, referencedEntry.ClassName));
                        }
                        else
                        {
                            AddPropertyIssue(localizationDelegate(
                                LECLocalizationShim.string_interp_nested_warningWrongClassPropertyTypingWrongMessage,
                                prefix, op.Value, referencedEntry.InstancedFullPath,
                                expectedType, referencedEntry.ClassName));
                        }
                    }
                    else if (op.Name.Name != null)
                    {
                        AddPropertyIssue(localizationDelegate(
                            LECLocalizationShim.string_interp_warningWrongObjectPropertyTypingWrongMessage,
                            prefix, op.Name.Name, op.Value, referencedEntry.InstancedFullPath,
                            expectedType, referencedEntry.ClassName));
                    }
                    else
                    {
                        AddPropertyIssue(localizationDelegate(
                            LECLocalizationShim.string_interp_nested_warningWrongObjectPropertyTypingWrongMessage,
                            prefix, op.Value, referencedEntry.InstancedFullPath,
                            expectedType, referencedEntry.ClassName));
                    }
                }
            }
            else if (property is ArrayProperty<ObjectProperty> aop)
            {
                foreach (var p in aop)
                {
                    recursiveCheckProperty(item, localizationDelegate, relativePath, containingClassOrStructName,
                        entry, p, aop.Name);
                }
            }
            else if (property is StructProperty sp)
            {
                foreach (var p in sp.Properties)
                {
                    recursiveCheckProperty(item, localizationDelegate, relativePath, sp.StructType, entry, p);
                }
            }
            else if (property is ArrayProperty<StructProperty> asp)
            {
                foreach (var p in asp)
                {
                    recursiveCheckProperty(item, localizationDelegate, relativePath, p.StructType, entry, p);
                }
            }
            else if (property is DelegateProperty dp)
            {
                if (dp.Value.ContainingObjectUIndex != 0 && !entry.FileRef.IsEntry(dp.Value.ContainingObjectUIndex))
                {
                    AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_warningDelegatePropertyIsOutsideOfExportTable, prefix, dp.Name.Name));
                }
                else if (ReferenceIssueCleaner.IsBadReference(entry.FileRef, dp.Value.ContainingObjectUIndex))
                {
                    AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_nested_warningTrashedExportReference,
                        prefix, dp.Value.ContainingObjectUIndex));
                }
            }
            else if (property is NameProperty np)
            {
                if (np.Value.Name == "") // Failed to resolve
                {
                    AddPropertyIssue(localizationDelegate(LECLocalizationShim.string_interp_invalidNameIndexonNameProperty, prefix, property.Name.Instanced));
                }
            }
        }

        /// <summary>
        /// Key class for comparing two entries
        /// </summary>
        /// <param name="entry"></param>
        class ObjectComparer(IEntry entry)
        {
            protected bool Equals(ObjectComparer other)
            {
                return InstancedFullPath == other.InstancedFullPath && ClassName == other.ClassName;
            }

            public override bool Equals(object obj)
            {
                if (obj is null) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != GetType()) return false;
                return Equals((ObjectComparer)obj);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(InstancedFullPath, ClassName);
            }

            /// <summary>
            /// This should technically be a memory path, for game consistency...
            /// </summary>
            public readonly string InstancedFullPath = entry.InstancedFullPath;

            public readonly string ClassName = entry.ClassName;
        }

        /// <summary>
        /// Returns a list of duplicate named objects in a package file. Trash exports are ignored.
        /// </summary>
        /// <param name="Pcc">Package file to check against</param>
        /// <returns>A list of <see cref="EntryStringPair"/> objects that detail the second or further duplicate. If this list is empty, there are no duplicates detected.</returns>
        public static List<EntryStringPair> CheckForDuplicateIndices(IMEPackage Pcc)
        {
            var duplicates = new List<EntryStringPair>();
            var duplicatesPackagePathIndexMapping = new Dictionary<ObjectComparer, List<int>>();
            foreach (ExportEntry exp in Pcc.Exports)
            {
                var key = new ObjectComparer(exp);
                if (key.InstancedFullPath.StartsWith(UnrealPackageFile.TrashPackageName, StringComparison.OrdinalIgnoreCase) && key.ClassName == "Package")
                    continue; //Do not report these as requiring re-indexing.
                if (!duplicatesPackagePathIndexMapping.TryGetValue(key, out List<int> indexList))
                {
                    indexList = new List<int>();
                    duplicatesPackagePathIndexMapping[key] = indexList;
                }
                else
                {
                    duplicates.Add(new EntryStringPair(exp, $"{exp.UIndex} {exp.InstancedFullPath} has duplicate index (index value {exp.indexValue})"));
                }

                indexList.Add(exp.UIndex);
            }

            // IMPORTS TOO
            foreach (ImportEntry imp in Pcc.Imports)
            {
                var key = new ObjectComparer(imp);
                if (key.InstancedFullPath.StartsWith(UnrealPackageFile.TrashPackageName, StringComparison.OrdinalIgnoreCase) && key.ClassName == "Package")
                    continue; //Do not report these as requiring re-indexing.
                if (!duplicatesPackagePathIndexMapping.TryGetValue(key, out List<int> indexList))
                {
                    indexList = new List<int>();
                    duplicatesPackagePathIndexMapping[key] = indexList;
                }
                else
                {
                    duplicates.Add(new EntryStringPair(imp, $"{imp.UIndex} {imp.InstancedFullPath} has duplicate index (index value {imp.indexValue})"));
                }

                indexList.Add(imp.UIndex);
            }

            return duplicates;
        }
    }
}
