using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LegendaryExplorerCore.Diagnostics
{
    public static class PackageDiags
    {
        private static bool hasValidFEChain(ExportEntry exp, bool isExpectingFE)
        {
            var parent = exp.Parent;
            if (parent == null)
                return true; // Nothing left to check
            if (parent is ImportEntry imp)
            {
                // Exports under imports must ALWAYS be forced exports
                // how could they possibly originate here?
                return isExpectingFE; // If we expect FE, this is valid
            }

            var parentE = parent as ExportEntry;
            if (parentE.IsForcedExport ^ isExpectingFE)
            {
                // It differs
                return false;
            }

            return hasValidFEChain(parentE, isExpectingFE);
        }

        /// <summary>
        /// Scans the package for forced export leaves that have inconsistent forced export flags in their parent chain
        /// </summary>
        /// <param name="package"></param>
        /// <returns></returns>
        public static List<EntryStringPair> GetBadForcedExportLeaves(IMEPackage package)
        {
            // Get list of leaves
            List<ExportEntry> leaves = new();
            foreach (var exp in package.Exports)
            {
                var children = exp.GetChildren();
                if (!children.Any())
                {
                    leaves.Add(exp);
                }
            }

            // Enumerate leaves and check their chains
            List<EntryStringPair> badLeaves = new();
            foreach (var leaf in leaves)
            {
                var originalValue = (leaf.ExportFlags & UnrealFlags.EExportFlags.ForcedExport) != 0;
                var isValidChain = hasValidFEChain(leaf, originalValue);
                if (!isValidChain)
                {
                    badLeaves.Add(new EntryStringPair(leaf, $"Inconsistent forced export on {leaf.InstancedFullPath}, should be forced: {(leaf.GetRoot() is ExportEntry root ? root.IsForcedExport : false)}"));
                }
            }
            return badLeaves;
        }
    }
}