using System.IO;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.Workflows.Editing;

namespace LE1GalaxyMapEditor.Workflows.Queries;

public sealed record CommitPreviewEntry(
    string Title,
    IReadOnlyList<string> Details,
    string Badge = "")
{
    public bool HasBadge => Badge.Length > 0;
    public bool HasDetails => Details.Count > 0;
}

public sealed record CommitPreviewSection(
    string ModuleDisplay,
    string FileName,
    IReadOnlyList<CommitPreviewEntry> Entries);

public sealed record CommitPreview(
    string SummaryText,
    int ChangeCount,
    int FileCount,
    IReadOnlyList<CommitPreviewSection> Sections)
{
    public string CommitButtonText => $"Commit {ChangeCount} {Plural(ChangeCount, "change", "changes")}";

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
}

/// <summary>
/// Produces a read-only, semantic description of the staged data consumed by Commit.
/// CSV values use the writer's token boundary so the preview matches disk output.
/// </summary>
public sealed class CommitPreviewBuilder(
    GalaxyMapModuleManifestStore? manifestStore = null,
    CsvGalaxyMapLoader? loader = null,
    PccGalaxyMapLoader? pccLoader = null,
    GalaxyMapModuleProfileStore? profileStore = null)
{
    private readonly GalaxyMapModuleManifestStore _manifestStore = manifestStore ?? new GalaxyMapModuleManifestStore();
    private readonly CsvGalaxyMapLoader _loader = loader ?? new CsvGalaxyMapLoader();
    private readonly PccGalaxyMapLoader _pccLoader = pccLoader ?? new PccGalaxyMapLoader(loader);
    private readonly GalaxyMapModuleProfileStore _profileStore = profileStore ?? new GalaxyMapModuleProfileStore();

    public CommitPreview Build(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var workspace = session.Workspace;
        if (workspace is null || !session.Changes.HasChanges)
        {
            return new CommitPreview("No staged changes.", 0, 0, []);
        }

        var sections = new List<CommitPreviewSection>();
        var changeCount = 0;
        var files = new HashSet<(string ModuleTag, string FileName)>();

        foreach (var (tag, tables) in session.Changes.DirtyTables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var layer = FindLayer(workspace, tag);
            if (layer is null)
            {
                continue;
            }
            var committedLayer = LoadCommittedLayer(layer.Module);

            foreach (var table in tables.OrderBy(table => table))
            {
                var currentRows = layer.Rows(table).ToArray();
                var entries = currentRows
                    .Where(row => row.CsvSnapshot?.HasChanges == true)
                    .Select(row => BuildRowEntry(row, workspace))
                    .Concat((committedLayer?.Rows(table) ?? [])
                        .Where(committed => currentRows.All(current => current.RowId != committed.RowId))
                        .Select(BuildDeletedRowEntry))
                    .OrderBy(entry => RowId(entry.Title))
                    .ToArray();
                if (entries.Length == 0)
                {
                    entries =
                    [
                        new CommitPreviewEntry(
                            $"{TableDisplay(table)} table",
                            ["The staged table contents will be written to disk."])
                    ];
                }

                var fileName = layer.SourcePackagePath is null
                    ? $"GalaxyMap_{table}_part.csv"
                    : Path.GetFileName(layer.SourcePackagePath);
                sections.Add(new CommitPreviewSection(ModuleDisplay(layer.Module), fileName, entries));
                files.Add((tag, fileName));
                changeCount += entries.Sum(ChangeUnits);
            }
        }

        foreach (var tag in session.Changes.DirtyModuleMetadata.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase))
        {
            var module = FindLayer(workspace, tag)?.Module;
            if (module is null)
            {
                continue;
            }

            var details = BuildMetadataDetails(module);
            sections.Add(new CommitPreviewSection(
                ModuleDisplay(module),
                module.IsPccBacked ? module.ProfileId + ".json" : GalaxyMapModuleManifestStore.FileName,
                [new CommitPreviewEntry("Module settings", details)]));
            files.Add((tag, module.IsPccBacked ? module.ProfileId + ".json" : GalaxyMapModuleManifestStore.FileName));
            changeCount += Math.Max(1, details.Count);
        }

        foreach (var group in session.Changes.PendingFiles
                     .OrderBy(file => file.ModuleTag, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .GroupBy(file => file.ModuleTag, StringComparer.OrdinalIgnoreCase))
        {
            var module = FindLayer(workspace, group.Key)?.Module;
            var entries = group.Select(file => BuildFileEntry(module, file)).ToArray();
            sections.Add(new CommitPreviewSection(
                module is null ? group.Key : ModuleDisplay(module),
                "Resources",
                entries));
            foreach (var file in group)
            {
                files.Add((group.Key, file.RelativePath));
            }
            changeCount += entries.Length;
        }

        var workspaceChanges = session.Changes.WorkspaceModuleChanges
            .OrderBy(change => change.ModuleTag, StringComparer.OrdinalIgnoreCase)
            .Select(change => new CommitPreviewEntry(
                $"{change.ModuleName} [{change.ModuleTag}]",
                [change.Kind == WorkspaceModuleChangeKind.Add
                    ? "This module will be added to the remembered workspace."
                    : "This module will be removed from the remembered workspace."],
                change.Kind == WorkspaceModuleChangeKind.Add ? "ADD" : "REMOVE"))
            .ToArray();
        if (workspaceChanges.Length > 0)
        {
            sections.Add(new CommitPreviewSection("Workspace", GalaxyMapWorkspaceStore.FileName, workspaceChanges));
            files.Add(("Workspace", GalaxyMapWorkspaceStore.FileName));
            changeCount += workspaceChanges.Length;
        }

        var summary = $"Across {files.Count} {Plural(files.Count, "file", "files")}, " +
                      $"{changeCount} staged {Plural(changeCount, "change", "changes")}.";
        return new CommitPreview(summary, changeCount, files.Count, sections);
    }

    private static GalaxyMapLayer? FindLayer(GalaxyMapWorkspace workspace, string tag)
        => workspace.ModuleLayers.FirstOrDefault(layer =>
            string.Equals(layer.Module.Tag, tag, StringComparison.OrdinalIgnoreCase));

    private static CommitPreviewEntry BuildRowEntry(GalaxyMapRow row, GalaxyMapWorkspace workspace)
    {
        var snapshot = row.CsvSnapshot!;
        var isNew = snapshot.SourceRowNumber == 0;
        var details = isNew
            ? []
            : OrderedDirtyColumns(snapshot)
                .Where(column => !string.Equals(
                    column, CsvRowSnapshot.RowIdColumnName, StringComparison.OrdinalIgnoreCase))
                .Select(column => $"{column}: {FormatToken(snapshot.GetOriginalValue(column))}  →  " +
                                  FormatToken(GalaxyMapRowValueAccessor.GetCsvToken(row, column)))
                .ToArray();

        return new CommitPreviewEntry(
            isNew ? NewRowTitle(row, workspace) : $"{TableDisplay(row.Table)} #{row.RowId}",
            details,
            isNew ? "NEW" : string.Empty);
    }

    private static string NewRowTitle(GalaxyMapRow physical, GalaxyMapWorkspace workspace)
    {
        return physical switch
        {
            Cluster cluster => $"Cluster #{cluster.RowId} / {InternalName(cluster.NameText, cluster.DisplayName)}",
            GalaxySystem system => SystemTitle(system, workspace),
            Planet planet => PlanetTitle(planet, workspace),
            PlotPlanetEntry plot => PlotPlanetTitle(plot, workspace),
            MapEntry map => MapTitle(map, workspace),
            RelayConnection relay => RelayTitle(relay, workspace),
            _ => $"{TableDisplay(physical.Table)} #{physical.RowId}"
        };
    }

    private static string SystemTitle(GalaxySystem system, GalaxyMapWorkspace workspace)
    {
        var cluster = workspace.EffectiveDocument.ClustersByRowId.GetValueOrDefault(system.ClusterRowId);
        return JoinPath(
            $"System #{system.RowId}",
            InternalName(system.NameText, system.DisplayName),
            cluster?.DisplayName ?? $"Cluster #{system.ClusterRowId}");
    }

    private static string PlanetTitle(Planet planet, GalaxyMapWorkspace workspace)
    {
        var system = workspace.EffectiveDocument.SystemsByRowId.GetValueOrDefault(planet.SystemRowId);
        return JoinPath(
            $"Planet #{planet.RowId}",
            InternalName(planet.NameText, planet.DisplayName),
            system?.Cluster?.DisplayName,
            system?.DisplayName ?? $"System #{planet.SystemRowId}");
    }

    private static string PlotPlanetTitle(PlotPlanetEntry plot, GalaxyMapWorkspace workspace)
    {
        var planet = workspace.EffectiveDocument.PlanetsByRowId.GetValueOrDefault(plot.RowId);
        return JoinPath(
            $"PlotPlanet #{plot.RowId}",
            InternalName(plot.NameText, planet?.DisplayName ?? string.Empty),
            planet?.System?.Cluster?.DisplayName,
            planet?.System?.DisplayName);
    }

    private static string MapTitle(MapEntry map, GalaxyMapWorkspace workspace)
    {
        var planet = workspace.EffectiveDocument.Planets.FirstOrDefault(planet => planet.MapRowId == map.RowId);
        return JoinPath(
            $"Map #{map.RowId}",
            map.MapName,
            planet?.DisplayName,
            planet?.System?.Cluster?.DisplayName,
            planet?.System?.DisplayName);
    }

    private static string RelayTitle(RelayConnection relay, GalaxyMapWorkspace workspace)
        => JoinPath(
            $"Relay #{relay.RowId}",
            $"{RelayEndpoint(relay.StartClusterEncoded, workspace.EffectiveDocument)} → " +
            RelayEndpoint(relay.EndClusterEncoded, workspace.EffectiveDocument));

    private static string RelayEndpoint(int encoded, GalaxyMapDocument document)
    {
        var cluster = document.Clusters.FirstOrDefault(candidate =>
            document.TryGetRelayCode(candidate, out var code, out _) && code == encoded);
        return cluster is null ? encoded.ToString() : $"{encoded} ({cluster.DisplayName})";
    }

    private static string JoinPath(params string?[] parts)
        => string.Join(" / ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string InternalName(string nameText, string fallback)
        => string.IsNullOrWhiteSpace(nameText) ? fallback : nameText;

    private static CommitPreviewEntry BuildDeletedRowEntry(GalaxyMapRow row)
        => new(
            $"{TableDisplay(row.Table)} #{row.RowId}",
            ["This row will be removed from the module's partial 2DA."],
            "DELETE");

    private GalaxyMapLayer? LoadCommittedLayer(GalaxyMapModule module)
    {
        if (module.FolderPath is null)
        {
            return null;
        }

        try
        {
            if (module.IsPccBacked && module.GalaxyMapPackagePath is not null)
            {
                return _pccLoader.Load(module.GalaxyMapPackagePath, module, allowEmpty: true);
            }
            return _loader.LoadPartFolder(module.FolderPath, module);
        }
        catch (GalaxyMapLoadException)
        {
            return null;
        }
    }

    private IReadOnlyList<string> BuildMetadataDetails(GalaxyMapModule current)
    {
        if (current.IsPccBacked && current.ProfileId is not null)
        {
            try
            {
                var original = _profileStore.Load(current.ProfileId);
                var details = new List<string>();
                AddChanged(details, "Display name", original.DisplayName ?? current.Name, current.Name);
                AddChanged(details, "Colour", original.ModuleColor.ToString(), current.Color.ToString());
                AddChanged(details, "TLK locale", original.TlkLocale.ToString(), current.TlkLocale.ToString());
                foreach (var table in Enum.GetValues<GalaxyMapTable>().Where(table => table != GalaxyMapTable.PlotPlanet))
                {
                    AddChanged(details, $"{TableDisplay(table)} ID range",
                        original.Reservations.GetRange(table)?.ToString() ?? "(none)",
                        current.Reservations.GetRange(table)?.ToString() ?? "(none)");
                }
                if (!original.ResourcePackages.SequenceEqual(
                        GalaxyMapModuleProfile.FromModule(current).ResourcePackages,
                        StringComparer.OrdinalIgnoreCase))
                {
                    details.Add("Resource PCC registrations: updated");
                }
                return details.Count == 0 ? ["Module profile will be updated."] : details;
            }
            catch (GalaxyMapLoadException)
            {
                return ["Module profile will be updated."];
            }
        }

        if (current.FolderPath is null ||
            !File.Exists(Path.Combine(current.FolderPath, GalaxyMapModuleManifestStore.FileName)))
        {
            return ["Module metadata will be updated."];
        }

        try
        {
            var original = _manifestStore.Load(current.FolderPath);
            var details = new List<string>();
            AddChanged(details, "Name", original.Name, current.Name);
            AddChanged(details, "Tag", original.Tag, current.Tag);
            AddChanged(details, "Colour", original.Color.ToString(), current.Color.ToString());
            AddChanged(details, "Mount priority", original.LoadOrder.ToString(), current.LoadOrder.ToString());
            foreach (var table in Enum.GetValues<GalaxyMapTable>().Where(table => table != GalaxyMapTable.PlotPlanet))
            {
                AddChanged(
                    details,
                    $"{TableDisplay(table)} ID range",
                    original.Reservations.GetRange(table)?.ToString() ?? "(none)",
                    current.Reservations.GetRange(table)?.ToString() ?? "(none)");
            }

            if (!DictionaryEqual(original.ClusterTextureLinks, current.ClusterTextureLinks))
            {
                details.Add("Cluster texture links: updated");
            }
            if (!original.PlanetTextureLinks.SequenceEqual(current.PlanetTextureLinks))
            {
                details.Add("Planet texture links: updated");
            }
            return details.Count == 0 ? ["Module metadata will be updated."] : details;
        }
        catch (GalaxyMapLoadException)
        {
            return ["Module metadata will be updated."];
        }
    }

    private static CommitPreviewEntry BuildFileEntry(GalaxyMapModule? module, PendingFileWrite file)
    {
        var exists = module?.FolderPath is { } folder &&
                     GalaxyMapTextureService.ResolveModuleTexturePath(module, file.RelativePath) is { } path &&
                     File.Exists(path);
        return new CommitPreviewEntry(
            file.Purpose,
            [$"File: {file.RelativePath}", $"Size: {FormatBytes(file.Contents.Length)}"],
            exists ? "REPLACE" : "NEW");
    }

    private static IEnumerable<string> OrderedDirtyColumns(CsvRowSnapshot snapshot)
    {
        foreach (var header in snapshot.Headers)
        {
            var column = string.IsNullOrWhiteSpace(header) ? CsvRowSnapshot.RowIdColumnName : header;
            if (snapshot.IsDirty(column))
            {
                yield return column;
            }
        }
    }

    private static int ChangeUnits(CommitPreviewEntry entry)
        => entry.Badge is "NEW" or "DELETE" ? 1 : Math.Max(1, entry.Details.Count);

    private static int RowId(string title)
        => int.TryParse(title[(title.LastIndexOf('#') + 1)..], out var rowId) ? rowId : int.MaxValue;

    private static void AddChanged(List<string> details, string label, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            details.Add($"{label}: {FormatToken(before)}  →  {FormatToken(after)}");
        }
    }

    private static bool DictionaryEqual<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> left,
        IReadOnlyDictionary<TKey, TValue> right) where TKey : notnull
        => left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && EqualityComparer<TValue>.Default.Equals(pair.Value, value));

    private static string FormatToken(string? value)
        => string.IsNullOrEmpty(value) ? "(blank)" : $"\"{value}\"";

    private static string FormatBytes(int bytes)
        => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024d:0.#} KB";

    private static string ModuleDisplay(GalaxyMapModule module) => $"{module.Name} [{module.Tag}]";

    private static string TableDisplay(GalaxyMapTable table) => table switch
    {
        GalaxyMapTable.PlotPlanet => "PlotPlanet",
        _ => table.ToString()
    };

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
}
