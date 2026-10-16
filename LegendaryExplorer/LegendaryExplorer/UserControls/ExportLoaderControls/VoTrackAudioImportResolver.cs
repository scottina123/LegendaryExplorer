using System;
using System.Collections.Generic;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>Retains imported dialogue packages for the lifetime of a track's navigation and playback.</summary>
internal sealed class VoTrackAudioImportResolver : IDisposable
{
    private readonly PackageCache cache = new();
    private readonly Dictionary<ImportEntry, ExportEntry> resolvedImports = new();

    internal ExportEntry Resolve(ImportEntry import)
    {
        if (resolvedImports.TryGetValue(import, out ExportEntry resolved))
        {
            return resolved;
        }

        try
        {
            string localization = import.FileRef.Localization == MELocalization.None
                ? "INT" : import.FileRef.Localization.ToString();
            EntryImporter.TryResolveImport(import, out resolved, cache: cache, localization: localization);
        }
        catch (Exception)
        {
            // Missing game files or malformed packages leave this voice unavailable until the track reloads.
            resolved = null;
        }

        resolvedImports[import] = resolved;
        return resolved;
    }

    public void Dispose()
    {
        resolvedImports.Clear();
        cache.Dispose();
    }
}
