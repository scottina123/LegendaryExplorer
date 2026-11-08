using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using LegendaryExplorerCore.Coalesced;

namespace LegendaryExplorer.MainWindow
{
    /// <summary>
    /// Compiles or decompiles Coalesced files dropped onto the main menu, in place, matching ME3Tweaks Mod Manager:
    /// <list type="bullet">
    /// <item>Coalesced .bin (any game) → decompiled to a folder next to it, named after the file</item>
    /// <item>ME3/LE3 .xml manifest (CoalesceFile root) → compiled to the .bin it names, in the same folder as the manifest</item>
    /// <item>LE1/LE2 mele.extractedbin manifest → packed to the .bin it names, in the same folder as the manifest</item>
    /// <item>A decompiled folder containing either manifest → compiled to the .bin it names, in the folder it was dragged from</item>
    /// </list>
    /// </summary>
    internal static class CoalescedDropHandler
    {
        private const string LEManifestFileName = "mele.extractedbin";

        private enum OperationKind { DecompileGame3, DecompileLE, CompileGame3, CompileLE }

        private sealed record Operation(OperationKind Kind, string Source, string Destination)
        {
            public bool IsCompile => Kind is OperationKind.CompileGame3 or OperationKind.CompileLE;
        }

        /// <summary>
        /// Returns a coalesced operation for the dropped path, or null if it's not a coalesced file/manifest/folder
        /// </summary>
        /// <param name="path">The dropped path</param>
        /// <param name="compileOutputDirectory">Where compiled .bin files are written. Defaults to the folder containing the dropped file.</param>
        private static Operation GetOperation(string path, string compileOutputDirectory = null)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // A dropped folder compiles to the folder it was dragged from
                    var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                    var outputDir = Path.GetDirectoryName(folder) ?? folder;

                    var leManifest = Path.Combine(folder, LEManifestFileName);
                    if (File.Exists(leManifest))
                        return GetOperation(leManifest, outputDir);

                    var game3Manifest = Path.Combine(folder, Path.GetFileName(folder) + ".xml");
                    return File.Exists(game3Manifest) ? GetOperation(game3Manifest, outputDir) : null;
                }

                if (!File.Exists(path)) return null;

                var fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath);
                compileOutputDirectory ??= directory;
                switch (Path.GetExtension(fullPath).ToLowerInvariant())
                {
                    case ".bin":
                        if (CoalescedConverter.IsGame3Coalesced(fullPath))
                            return new Operation(OperationKind.DecompileGame3, fullPath, Path.ChangeExtension(fullPath, null));
                        if (IsLECoalescedFileName(fullPath))
                            return new Operation(OperationKind.DecompileLE, fullPath, Path.ChangeExtension(fullPath, null));
                        return null;

                    case ".extractedbin":
                    {
                        var binName = LECoalescedConverter.GetDestinationPathFromManifest(fullPath);
                        if (string.IsNullOrWhiteSpace(binName)) return null;
                        return new Operation(OperationKind.CompileLE, fullPath, Path.Combine(compileOutputDirectory, binName.Trim()));
                    }

                    case ".xml":
                    {
                        var root = XDocument.Load(fullPath).Root;
                        if (root?.Name.LocalName != "CoalesceFile") return null;
                        var binName = root.Attribute("name")?.Value;
                        if (string.IsNullOrWhiteSpace(binName))
                            binName = Path.GetFileNameWithoutExtension(fullPath) + ".bin";
                        return new Operation(OperationKind.CompileGame3, fullPath, Path.Combine(compileOutputDirectory, binName));
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable/invalid file: let the normal open-file handling deal with it
            }
            return null;
        }

        /// <summary>
        /// LE1/LE2 coalesced files have no magic number, so they're identified by name (Coalesced_INT.bin, Coalesced.bin, Default_DLC_*.bin, etc.)
        /// </summary>
        private static bool IsLECoalescedFileName(string path)
        {
            var name = Path.GetFileName(path);
            return name.StartsWith("Coalesced", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("Default_DLC_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Splits dropped paths into coalesced operations and everything else
        /// </summary>
        /// <returns>The paths that were not handled as coalesced operations</returns>
        public static List<string> HandleDrop(IEnumerable<string> paths, Action<string> reportFailure)
        {
            var unhandled = new List<string>();
            var operations = new List<Operation>();
            foreach (var path in paths)
            {
                var op = GetOperation(path);
                if (op is null) unhandled.Add(path);
                else operations.Add(op);
            }

            if (operations.Count > 0)
            {
                Task.Run(() =>
                {
                    var failed = new List<string>();
                    foreach (var op in operations)
                    {
                        try
                        {
                            Run(op);
                        }
                        catch (Exception ex)
                        {
                            failed.Add($"Failed to {(op.IsCompile ? "compile" : "decompile")} {op.Source}:\n{ex.Message}");
                        }
                    }
                    return failed;
                }).ContinueWith(t =>
                {
                    // Only errors are reported; successful conversions are silent
                    if (t.Result.Count > 0)
                        reportFailure(string.Join("\n\n", t.Result));
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            return unhandled;
        }

        private static void Run(Operation op)
        {
            switch (op.Kind)
            {
                case OperationKind.DecompileGame3:
                    CoalescedConverter.ConvertToXML(op.Source, op.Destination);
                    break;
                case OperationKind.DecompileLE:
                    LECoalescedConverter.Unpack(op.Source, op.Destination);
                    break;
                case OperationKind.CompileGame3:
                    CoalescedConverter.ConvertToBin(op.Source, op.Destination);
                    break;
                case OperationKind.CompileLE:
                    LECoalescedConverter.Pack(Path.GetDirectoryName(op.Source), op.Destination);
                    break;
            }
        }
    }
}
