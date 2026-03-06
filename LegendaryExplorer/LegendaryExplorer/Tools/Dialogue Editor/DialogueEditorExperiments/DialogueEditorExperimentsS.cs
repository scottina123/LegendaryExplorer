using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.Dialogue_Editor.DialogueEditorExperiments
{
    /// <summary>
    /// Scottina's Experiments in Dialogue Editor.
    /// </summary>
    static class DialogueEditorExperimentsS
    {
        // Class names that must be local exports, not imports
        private static readonly HashSet<string> AnimClassNames =
        [
            "BioDynamicAnimSet",
            "AnimSequence",
            "BioAnimSetData"
        ];

        /// <summary>
        /// Imports all LE3 ambient performance SFXAmbPerfGameData exports from the Asset Database
        /// into a single PCC with no imports for animation data.
        /// For each ambient performance the source file (BIOG, BioP, BioA, BioD, etc.) where
        /// the export has all local dependencies is located and ported from.
        /// Any remaining animation imports are resolved from game files in a post-processing pass.
        /// </summary>
        public static async Task ImportAllAmbientPerformancesToPcc(Window owner)
        {
            // 1. Check that LE3 game path is set
            string le3Root = MEDirectories.GetDefaultGamePath(MEGame.LE3);
            if (le3Root == null || !Directory.Exists(le3Root))
            {
                MessageBox.Show("LE3 game path is not set. Please configure it in settings.", "Error");
                return;
            }

            // 2. Check that the asset database path is set
            string dbPath = Settings.AssetDBPath;
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                MessageBox.Show("Asset Database path is not set or the file does not exist.\nPlease open the Asset Database and generate/load the LE3 database first.", "Error");
                return;
            }

            // 3. Pick save location
            var sfd = new SaveFileDialog
            {
                Title = "Save Ambient Performances PCC",
                Filter = "PCC files (*.pcc)|*.pcc",
                FileName = "AmbientLE3.pcc"
            };
            if (sfd.ShowDialog() != true)
                return;

            string outputPath = sfd.FileName;

            // 4. Load the LE3 Asset Database
            var db = new AssetDB();
            try
            {
                await AssetDatabaseWindow.LoadDatabase(dbPath, MEGame.LE3, db, CancellationToken.None);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load LE3 Asset Database:\n{ex.Message}", "Error");
                return;
            }

            // 5. Collect all ambient performance records, deduplicated by name.
            var ambPerfs = db.Animations.Where(a => a.IsAmbPerf).ToList();
            if (ambPerfs.Count == 0)
            {
                MessageBox.Show("No ambient performances found in the LE3 Asset Database.", "Error");
                return;
            }

            // 6. Create the destination PCC
            MEPackageHandler.CreateAndSavePackage(outputPath, MEGame.LE3);
            using var destPcc = MEPackageHandler.OpenMEPackage(outputPath, forceLoadFromDisk: true);

            // Build game file lookup once
            var gameFiles = MELoadedFiles.GetFilesLoadedInGame(MEGame.LE3, forceUseCached: true);

            // 7. For each unique ambient performance, find the best source and port it.
            using var cache = new PackageCache();
            int imported = 0;
            int skipped = 0;
            int failed = 0;

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var anim in ambPerfs)
            {
                if (!seenNames.Add(anim.AnimSequence))
                    continue;

                ExportEntry bestSource = null;
                foreach (var usage in anim.Usages)
                {
                    var (fileName, dirKey) = db.FileList[usage.FileKey];
                    var contentDir = db.ContentDir[dirKey];
                    string filePath = ResolveFilePath(le3Root, fileName, contentDir);
                    if (filePath == null)
                        continue;

                    IMEPackage sourcePcc;
                    try
                    {
                        sourcePcc = cache.GetCachedPackage(filePath, true);
                    }
                    catch { continue; }

                    if (sourcePcc == null || !sourcePcc.IsUExport(usage.UIndex))
                        continue;

                    var sourceExport = sourcePcc.GetUExport(usage.UIndex);
                    if (sourceExport.ClassName != "SFXAmbPerfGameData")
                        continue;

                    // Try to find this in a master file (BIOG, BioP, BioA, etc.) where
                    // all BioDynamicAnimSets/AnimSequences are local exports.
                    var masterExport = FindExportInMasterFile(sourceExport, gameFiles, cache);
                    bestSource = masterExport ?? sourceExport;
                    break;
                }

                if (bestSource == null)
                {
                    failed++;
                    continue;
                }

                if (destPcc.FindExport(bestSource.InstancedFullPath) != null)
                {
                    skipped++;
                    continue;
                }

                // Pre-resolve: if the source has any import children that are anim classes,
                // resolve and port them first so the main port finds local exports.
                PreResolveImportDependencies(bestSource, destPcc, cache);

                IEntry parentEntry = EnsureParentPackageChain(bestSource, destPcc);

                var rop = new RelinkerOptionsPackage
                {
                    Cache = cache,
                    PortExportsAsImportsWhenPossible = false,
                    ImportExportDependencies = true,
                    ImportChildrenOfPackages = true,
                };

                try
                {
                    EntryImporter.ImportAndRelinkEntries(
                        EntryImporter.PortingOption.CloneTreeAsChild,
                        bestSource,
                        destPcc,
                        parentEntry,
                        shouldRelink: true,
                        rop,
                        out _);

                    imported++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error importing {anim.AnimSequence}: {ex.Message}");
                    failed++;
                }
            }

            // 8. Post-process: resolve any remaining anim imports that slipped through.
            int resolvedImports = ResolveRemainingAnimImports(destPcc, cache);

            // 9. Save
            destPcc.Save();
            MessageBox.Show(
                $"Done! Imported {imported} ambient performances into:\n{outputPath}\n\n" +
                $"Skipped {skipped} duplicates.\n" +
                $"Resolved {resolvedImports} remaining imports to local exports.\n" +
                $"{failed} entries could not be resolved.",
                "Import Complete");
        }

        /// <summary>
        /// Given an SFXAmbPerfGameData export, tries to find the same export in a master
        /// file (BIOG, BioP, BioA, BioD, etc.) where all children are local exports.
        /// Checks both the root-package-named file and any file that contains a matching export.
        /// </summary>
        private static ExportEntry FindExportInMasterFile(ExportEntry sourceExport, CaseInsensitiveDictionary<string> gameFiles, PackageCache cache)
        {
            string ifp = sourceExport.InstancedFullPath;
            string rootName = GetRootPackageName(sourceExport);

            // 1. Try the file matching the root package name (e.g. BIOG_GesturesConfig.pcc)
            if (rootName != null)
            {
                var found = FindExportInGameFile(rootName, ifp, rootName, gameFiles, cache);
                if (found != null && !HasAnimImportChildren(found))
                    return found;
            }

            // 2. Try the source file's own package name (it might be a BioP/BioA/BioD master)
            string sourceFileName = Path.GetFileNameWithoutExtension(sourceExport.FileRef.FilePath);
            if (sourceFileName != null && !string.Equals(sourceFileName, rootName, StringComparison.OrdinalIgnoreCase))
            {
                var found = FindExportInGameFile(sourceFileName, ifp, rootName, gameFiles, cache);
                if (found != null && !HasAnimImportChildren(found))
                    return found;
            }

            // 3. If the source itself has no import children, it's already good
            if (!HasAnimImportChildren(sourceExport))
                return sourceExport;

            return null;
        }

        /// <summary>
        /// Tries to find an export by InstancedFullPath in a game file.
        /// </summary>
        private static ExportEntry FindExportInGameFile(string fileName, string ifp, string rootName, CaseInsensitiveDictionary<string> gameFiles, PackageCache cache)
        {
            foreach (var ext in new[] { ".pcc", ".upk" })
            {
                if (!gameFiles.TryGetValue($"{fileName}{ext}", out var filePath))
                    continue;

                IMEPackage pkg;
                try { pkg = cache.GetCachedPackage(filePath, true); }
                catch { continue; }
                if (pkg == null) continue;

                // Try full path
                var found = pkg.FindExport(ifp);
                if (found != null) return found;

                // Try stripping root package name (ForcedExport: BIOG_GesturesConfig.X → X)
                if (rootName != null && ifp.StartsWith($"{rootName}.", StringComparison.OrdinalIgnoreCase))
                {
                    found = pkg.FindExport(ifp.Substring(rootName.Length + 1));
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns true if the export or any of its children reference an import
        /// that is a BioDynamicAnimSet, AnimSequence, or BioAnimSetData.
        /// </summary>
        private static bool HasAnimImportChildren(ExportEntry export)
        {
            foreach (var child in export.GetChildren())
            {
                if (child is ImportEntry imp && AnimClassNames.Contains(imp.ClassName))
                    return true;
                if (child is ExportEntry childExp)
                {
                    // Check grandchildren too (BioDynamicAnimSet → AnimSequence)
                    foreach (var grandchild in childExp.GetChildren())
                    {
                        if (grandchild is ImportEntry gImp && AnimClassNames.Contains(gImp.ClassName))
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Before porting the SFXAmbPerfGameData, resolve all its import dependencies
        /// (BioDynamicAnimSet, AnimSequence, BioAnimSetData) from game files and port
        /// them into the destination first. This way the main port finds local exports.
        /// </summary>
        private static void PreResolveImportDependencies(ExportEntry sourceExport, IMEPackage destPcc, PackageCache cache)
        {
            var sourcePcc = sourceExport.FileRef;

            // Collect all import children that are animation-related
            var importsToResolve = new List<ImportEntry>();
            CollectAnimImportsRecursive(sourcePcc, sourceExport, importsToResolve);

            foreach (var imp in importsToResolve)
            {
                // Check if this already exists as an export in the destination
                if (destPcc.FindExport(imp.InstancedFullPath) != null)
                    continue;

                // Resolve the import to find the actual export in game files
                ExportEntry resolved = null;
                try
                {
                    if (EntryImporter.TryResolveImport(imp, out resolved, cache: cache))
                    {
                        // Resolved! Port it into the destination
                        IEntry parent = EnsureParentChainForImport(imp, destPcc);

                        var rop = new RelinkerOptionsPackage
                        {
                            Cache = cache,
                            PortExportsAsImportsWhenPossible = false,
                            ImportExportDependencies = true,
                            ImportChildrenOfPackages = true,
                        };

                        EntryImporter.ImportAndRelinkEntries(
                            EntryImporter.PortingOption.CloneTreeAsChild,
                            resolved,
                            destPcc,
                            parent,
                            shouldRelink: true,
                            rop,
                            out _);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Pre-resolve failed for {imp.InstancedFullPath}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Recursively collects all animation-class imports under an export.
        /// </summary>
        private static void CollectAnimImportsRecursive(IMEPackage pcc, IEntry parent, List<ImportEntry> results)
        {
            foreach (var child in parent.GetChildren())
            {
                if (child is ImportEntry imp && AnimClassNames.Contains(imp.ClassName))
                {
                    results.Add(imp);
                }

                // Recurse into both exports and imports (imports can have import children)
                CollectAnimImportsRecursive(pcc, child, results);
            }
        }

        /// <summary>
        /// Ensures the parent chain for an import exists as exports in the destination.
        /// Walks up the import's parent chain, creating Package exports as needed.
        /// </summary>
        private static IEntry EnsureParentChainForImport(ImportEntry imp, IMEPackage destPcc)
        {
            var chain = new List<IEntry>();
            IEntry current = imp;
            while (current.HasParent)
            {
                chain.Add(current.Parent);
                current = current.Parent;
            }
            chain.Reverse();

            IEntry destParent = null;
            foreach (var entry in chain)
            {
                var existing = destPcc.FindExport(entry.InstancedFullPath);
                if (existing != null)
                {
                    destParent = existing;
                }
                else
                {
                    destParent = ExportCreator.CreatePackageExport(destPcc, entry.ObjectName, destParent);
                }
            }
            return destParent;
        }

        /// <summary>
        /// Post-processing: resolves any remaining BioDynamicAnimSet, AnimSequence, or
        /// BioAnimSetData imports in the destination PCC by finding them in game files,
        /// porting them, and repointing all references.
        /// Loops until no more progress is made.
        /// </summary>
        private static int ResolveRemainingAnimImports(IMEPackage destPcc, PackageCache cache)
        {
            int totalResolved = 0;
            bool madeProgress = true;

            while (madeProgress)
            {
                madeProgress = false;
                var importsToFix = destPcc.Imports
                    .Where(imp => AnimClassNames.Contains(imp.ClassName))
                    .ToList();

                foreach (var imp in importsToFix)
                {
                    // Already replaced?
                    if (destPcc.FindExport(imp.InstancedFullPath) != null)
                    {
                        // Repoint the import to the existing export
                        var existingExport = destPcc.FindExport(imp.InstancedFullPath);
                        Relinker.RepointObject(imp, existingExport);
                        totalResolved++;
                        madeProgress = true;
                        continue;
                    }

                    ExportEntry resolved = null;
                    try
                    {
                        EntryImporter.TryResolveImport(imp, out resolved, cache: cache);
                    }
                    catch { /* continue */ }

                    if (resolved == null)
                        continue;

                    IEntry parent = imp.Parent;

                    var rop = new RelinkerOptionsPackage
                    {
                        Cache = cache,
                        PortExportsAsImportsWhenPossible = false,
                        ImportExportDependencies = true,
                        ImportChildrenOfPackages = true,
                    };

                    try
                    {
                        EntryImporter.ImportAndRelinkEntries(
                            EntryImporter.PortingOption.CloneTreeAsChild,
                            resolved,
                            destPcc,
                            parent,
                            shouldRelink: true,
                            rop,
                            out var newEntry);

                        if (newEntry is ExportEntry newExport)
                        {
                            Relinker.RepointObject(imp, newExport);
                            totalResolved++;
                            madeProgress = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Post-resolve failed for {imp.InstancedFullPath}: {ex.Message}");
                    }
                }
            }

            return totalResolved;
        }

        /// <summary>
        /// Gets the root package name of an export by walking up the parent chain.
        /// </summary>
        private static string GetRootPackageName(ExportEntry export)
        {
            IEntry current = export;
            while (current.HasParent)
            {
                current = current.Parent;
            }
            return current is ExportEntry rootExp ? rootExp.ObjectName.Instanced : null;
        }

        /// <summary>
        /// Ensures the full parent package chain for an export exists in the destination PCC.
        /// Returns the immediate parent entry, or null if the export is at the root level.
        /// </summary>
        private static IEntry EnsureParentPackageChain(ExportEntry sourceExport, IMEPackage destPcc)
        {
            var parentChain = new List<IEntry>();
            IEntry current = sourceExport;
            while (current.HasParent)
            {
                parentChain.Add(current.Parent);
                current = current.Parent;
            }

            parentChain.Reverse();

            IEntry destParent = null;
            foreach (var srcParent in parentChain)
            {
                var existing = destPcc.FindExport(srcParent.InstancedFullPath);
                if (existing != null)
                {
                    destParent = existing;
                }
                else
                {
                    destParent = ExportCreator.CreatePackageExport(destPcc, srcParent.ObjectName, destParent);
                }
            }

            return destParent;
        }

        private static string ResolveFilePath(string rootPath, string fileName, string contentDir)
        {
            var files = Directory.GetFiles(rootPath, $"{fileName}.*", SearchOption.AllDirectories);
            return files.FirstOrDefault(f => f.Contains(contentDir, StringComparison.OrdinalIgnoreCase));
        }
    }
}
