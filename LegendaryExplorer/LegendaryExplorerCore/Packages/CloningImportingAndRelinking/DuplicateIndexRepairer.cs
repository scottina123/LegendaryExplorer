using System;
using System.Collections.Generic;
using System.Linq;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

/// <summary>
/// Repairs the duplicate object identities reported by <see cref="EntryChecker.CheckForDuplicateIndices"/>.
/// </summary>
public static class DuplicateIndexRepairer
{
    /// <summary>
    /// Gives duplicate entries an unused object-name number, preserving the first occurrence in
    /// export-then-import table order. Only name numbers change; entry indexes, links and data are retained.
    /// </summary>
    /// <returns>The number of entries whose object-name number changed.</returns>
    public static int FixDuplicateIndices(IMEPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var entries = package.Exports.Cast<IEntry>().Concat(package.Imports).ToArray();
        var depths = new Dictionary<IEntry, int>();
        foreach (IEntry entry in entries)
        {
            // Validate before changing any headers: paths cannot be read safely through circular outers.
            ReferenceIssueCleaner.ValidateOuterChain(entry);
            int depth = 0;
            for (IEntry parent = entry.Parent; parent is not null; parent = parent.Parent)
                depth++;
            depths.Add(entry, depth);
        }

        var changedEntries = new HashSet<IEntry>();
        while (true)
        {
            var duplicates = EntryChecker.CheckForDuplicateIndices(package);
            if (duplicates.Count == 0)
                return changedEntries.Count;

            // A parent rename can resolve its entire subtree. Recheck before touching deeper entries,
            // including collisions introduced by the parent's new path beneath differently typed objects.
            int shallowestDepth = duplicates.Min(issue => depths[issue.Entry]);
            var occupied = new HashSet<(string Path, string ClassName)>(
                entries.Select(entry => (entry.InstancedFullPath, entry.ClassName)));
            var nextNumbers = new Dictionary<(string ParentPath, string Name, string ClassName), int>();
            foreach (IEntry duplicate in duplicates.Select(issue => issue.Entry)
                         .Where(entry => depths[entry] == shallowestDepth))
            {
                var stem = (duplicate.ParentInstancedFullPath, duplicate.ObjectName.Name, duplicate.ClassName);
                int number = nextNumbers.GetValueOrDefault(stem, 1);
                while (true)
                {
                    var name = new NameReference(duplicate.ObjectName.Name, number);
                    string path = stem.ParentInstancedFullPath.Length == 0
                        ? name.Instanced
                        : name.AddToPath(stem.ParentInstancedFullPath);
                    // Compare rendered paths, so e.g. literal "Object_0" also reserves Object number 1.
                    if (occupied.Add((path, duplicate.ClassName)))
                        break;
                    number = checked(number + 1);
                }

                duplicate.indexValue = number;
                changedEntries.Add(duplicate);
                nextNumbers[stem] = checked(number + 1);
            }
        }
    }
}
