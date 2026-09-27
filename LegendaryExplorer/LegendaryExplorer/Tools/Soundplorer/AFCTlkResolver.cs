using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK.ME2ME3;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.Tools.Soundplorer;

/// <summary>
/// AFCs have no dialogue IDs. Recover them from the WwiseStreams that reference their audio.
/// This scan reads only stream exports and keeps local TLKs separate from the global TLK selection.
/// </summary>
internal static class AFCTlkResolver
{
    public static void Resolve(IReadOnlyList<AFCFileEntry> entries, string language,
        Func<bool> cancellationPending = null, Action<string> reportProgress = null,
        Func<int, MEGame, string> resolveStringRef = null)
    {
        if (entries.Count == 0)
            return;

        string afcPath = Path.GetFullPath(entries[0].AFCPath);
        string afcName = Path.GetFileNameWithoutExtension(afcPath);
        string directory = Path.GetDirectoryName(afcPath);
        string searchRoot = GetSearchRoot(directory);
        var unresolved = entries.ToDictionary(entry => (entry.Offset, entry.DataSize));
        var localTlks = new List<ME2ME3LazyTLK>();
        resolveStringRef ??= (id, game) => TLKManagerWPF.GlobalFindStrRefbyID(id, game);

        try
        {
            // Mods often keep audio in subfolders and their TLKs in CookedPCConsole itself.
            for (var folder = new DirectoryInfo(directory); folder != null; folder = folder.Parent)
            {
                foreach (string path in Directory.EnumerateFiles(folder.FullName, "*.tlk")
                             .Where(path => Path.GetFileNameWithoutExtension(path).EndsWith("_" + language, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (cancellationPending?.Invoke() == true)
                        return;
                    try
                    {
                        var tlk = new ME2ME3LazyTLK();
                        tlk.LoadTlkData(path);
                        localTlks.Add(tlk);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Soundplorer could not read TLK {path}: {ex.Message}");
                    }
                }

                if (string.Equals(folder.FullName, searchRoot, StringComparison.OrdinalIgnoreCase))
                    break;
            }

            var lookups = new Dictionary<MEGame, NameTlkLookup>();
            // Prefer packages beside the AFC, then search the rest of its cooked folder.
            var packagePaths = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".pcc", StringComparison.OrdinalIgnoreCase)
                               || Path.GetExtension(path).Equals(".xxx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => !string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (string path in packagePaths)
            {
                if (cancellationPending?.Invoke() == true || unresolved.Count == 0)
                    return;

                reportProgress?.Invoke($"Finding AFC TLK text: {Path.GetFileName(path)} ({entries.Count - unresolved.Count}/{entries.Count})");
                try
                {
                    // A sibling folder may contain a different AFC with the same name.
                    string siblingAfc = Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(afcPath));
                    if (File.Exists(siblingAfc) && !string.Equals(siblingAfc, afcPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var package = MEPackageHandler.UnsafePartialLoad(path, export => export.ClassName == "WwiseStream" && !export.IsDefaultObject);
                    if (!package.Game.IsGame2() && !package.Game.IsGame3())
                        continue;

                    if (!lookups.TryGetValue(package.Game, out var lookup))
                    {
                        var game = package.Game;
                        lookup = new NameTlkLookup(id =>
                        {
                            foreach (var tlk in localTlks)
                            {
                                string text = tlk.FindDataById(id, returnNullIfNotFound: true);
                                if (text != null)
                                    return text;
                            }
                            return resolveStringRef(id, game);
                        });
                        lookups.Add(game, lookup);
                    }

                    foreach (var export in package.Exports.Where(export => export.ClassName == "WwiseStream" && !export.IsDefaultObject))
                    {
                        if (cancellationPending?.Invoke() == true)
                            return;
                        try
                        {
                            var stream = export.GetBinaryData<WwiseStream>();
                            if (stream.IsPCCStored || !string.Equals(stream.Filename, afcName, StringComparison.OrdinalIgnoreCase)
                                || !unresolved.TryGetValue((stream.DataOffset, stream.DataSize), out var entry))
                                continue;

                            if (NameTlkLookup.GetStringRef(export.ObjectName.Name) is not int stringRef)
                                continue;

                            entry.TLKStringRef = stringRef;
                            string text = lookup.CreateName(0, export.ObjectName.Name).TlkText;
                            if (text != null)
                            {
                                entry.TLKString = text;
                                unresolved.Remove((entry.Offset, entry.DataSize));
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Soundplorer could not read stream {export.UIndex} in {path}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // One unreadable package must not prevent playback or resolving other entries.
                    Debug.WriteLine($"Soundplorer could not scan {path}: {ex.Message}");
                }
            }
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Soundplorer AFC TLK scan failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Soundplorer AFC TLK scan failed: {ex.Message}");
        }
    }

    private static string GetSearchRoot(string directory)
    {
        for (var folder = new DirectoryInfo(directory); folder != null; folder = folder.Parent)
        {
            if (folder.Name.Equals("CookedPCConsole", StringComparison.OrdinalIgnoreCase)
                || folder.Name.Equals("CookedPC", StringComparison.OrdinalIgnoreCase))
                return folder.FullName;
        }
        return directory;
    }
}
