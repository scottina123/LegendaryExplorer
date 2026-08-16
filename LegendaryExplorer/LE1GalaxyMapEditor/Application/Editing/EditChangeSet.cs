using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public sealed record PendingFileWrite(
    string ModuleTag,
    string RelativePath,
    byte[] Contents,
    string Purpose,
    GalaxyMapRowKey? RelatedRow = null,
    string? CacheKey = null);

public enum WorkspaceModuleChangeKind
{
    Add,
    Remove
}

public sealed record WorkspaceModuleChange(
    string FolderPath,
    string ModuleName,
    string ModuleTag,
    WorkspaceModuleChangeKind Kind);

public sealed record EditChangeSetSnapshot(
    IReadOnlyDictionary<string, IReadOnlySet<GalaxyMapTable>> DirtyTables,
    IReadOnlySet<string> DirtyModuleMetadata,
    IReadOnlyList<PendingFileWrite> PendingFiles,
    IReadOnlyList<WorkspaceModuleChange> WorkspaceModuleChanges);

public sealed class EditChangeSet
{
    private readonly Dictionary<string, HashSet<GalaxyMapTable>> _dirtyTables =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyModuleMetadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string ModuleTag, string RelativePath), PendingFileWrite> _pendingFiles = [];
    private readonly Dictionary<string, WorkspaceModuleChange> _workspaceModuleChanges =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlySet<GalaxyMapTable>> DirtyTables
        => _dirtyTables.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<GalaxyMapTable>)pair.Value,
            StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> DirtyModuleMetadata => _dirtyModuleMetadata;
    public IReadOnlyCollection<PendingFileWrite> PendingFiles => _pendingFiles.Values;
    public IReadOnlyCollection<WorkspaceModuleChange> WorkspaceModuleChanges => _workspaceModuleChanges.Values;
    public bool HasWorkspaceChanges => _workspaceModuleChanges.Count > 0;
    public bool HasChanges => _dirtyTables.Count > 0 || _dirtyModuleMetadata.Count > 0 ||
                              _pendingFiles.Count > 0 || _workspaceModuleChanges.Count > 0;
    public int Count => _dirtyTables.Values.Sum(tables => tables.Count) +
                        _dirtyModuleMetadata.Count + _pendingFiles.Count + _workspaceModuleChanges.Count;

    internal void StageWorkspaceModuleAdded(GalaxyMapModule module)
        => StageWorkspaceModuleChange(module, WorkspaceModuleChangeKind.Add);

    internal void StageWorkspaceModuleRemoved(GalaxyMapModule module)
        => StageWorkspaceModuleChange(module, WorkspaceModuleChangeKind.Remove);

    private void StageWorkspaceModuleChange(GalaxyMapModule module, WorkspaceModuleChangeKind kind)
    {
        var folderPath = module.FolderPath
            ?? throw new InvalidOperationException("A staged workspace module must have a folder.");
        if (_workspaceModuleChanges.TryGetValue(folderPath, out var existing) && existing.Kind != kind)
        {
            _workspaceModuleChanges.Remove(folderPath);
            return;
        }

        _workspaceModuleChanges[folderPath] = new WorkspaceModuleChange(
            folderPath, module.Name, module.Tag, kind);
    }

    internal bool MarkTable(string moduleTag, GalaxyMapTable table)
    {
        if (!_dirtyTables.TryGetValue(moduleTag, out var tables))
        {
            tables = [];
            _dirtyTables[moduleTag] = tables;
        }

        return tables.Add(table);
    }

    internal bool MarkTables(string moduleTag, IEnumerable<GalaxyMapTable> dirtyTables)
    {
        var changed = false;
        foreach (var table in dirtyTables)
        {
            changed |= MarkTable(moduleTag, table);
        }

        return changed;
    }

    internal bool MarkMetadata(string moduleTag) => _dirtyModuleMetadata.Add(moduleTag);

    internal void StageFile(PendingFileWrite pending)
        => _pendingFiles[(pending.ModuleTag, pending.RelativePath)] = Clone(pending);

    internal bool RemovePendingFile(string moduleTag, string relativePath)
        => _pendingFiles.Remove((moduleTag, relativePath));

    public bool ContainsModule(string moduleTag)
        => _dirtyTables.ContainsKey(moduleTag) ||
           _dirtyModuleMetadata.Contains(moduleTag) ||
           _pendingFiles.Keys.Any(key => string.Equals(key.ModuleTag, moduleTag, StringComparison.OrdinalIgnoreCase));

    internal IEnumerable<string> ModuleTags()
        => _dirtyTables.Keys
            .Concat(_dirtyModuleMetadata)
            .Concat(_pendingFiles.Keys.Select(key => key.ModuleTag))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlySet<GalaxyMapTable> TablesFor(string moduleTag)
        => _dirtyTables.TryGetValue(moduleTag, out var tables)
            ? tables
            : new HashSet<GalaxyMapTable>();

    internal IReadOnlyList<PendingFileWrite> FilesFor(string moduleTag)
        => _pendingFiles.Values.Where(file =>
                string.Equals(file.ModuleTag, moduleTag, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    internal bool IsMetadataDirty(string moduleTag) => _dirtyModuleMetadata.Contains(moduleTag);

    internal void ClearModule(string moduleTag)
    {
        _dirtyTables.Remove(moduleTag);
        _dirtyModuleMetadata.Remove(moduleTag);
        foreach (var key in _pendingFiles.Keys.Where(key =>
                     string.Equals(key.ModuleTag, moduleTag, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _pendingFiles.Remove(key);
        }
    }

    internal void RemoveModule(string moduleTag) => ClearModule(moduleTag);

    internal void MigrateModuleTag(string oldTag, string newTag)
    {
        if (string.Equals(oldTag, newTag, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_dirtyTables.Remove(oldTag, out var tables))
        {
            _dirtyTables[newTag] = tables;
        }

        if (_dirtyModuleMetadata.Remove(oldTag))
        {
            _dirtyModuleMetadata.Add(newTag);
        }

        foreach (var pair in _pendingFiles.Where(pair =>
                     string.Equals(pair.Key.ModuleTag, oldTag, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _pendingFiles.Remove(pair.Key);
            var migrated = pair.Value with { ModuleTag = newTag };
            _pendingFiles[(newTag, migrated.RelativePath)] = migrated;
        }
    }

    internal EditChangeSetSnapshot Capture()
        => new(
            _dirtyTables.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<GalaxyMapTable>)new HashSet<GalaxyMapTable>(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(_dirtyModuleMetadata, StringComparer.OrdinalIgnoreCase),
            _pendingFiles.Values.Select(Clone).ToArray(),
            _workspaceModuleChanges.Values.ToArray());

    internal void Restore(EditChangeSetSnapshot snapshot)
    {
        Clear();
        foreach (var pair in snapshot.DirtyTables)
        {
            _dirtyTables[pair.Key] = new HashSet<GalaxyMapTable>(pair.Value);
        }

        foreach (var tag in snapshot.DirtyModuleMetadata)
        {
            _dirtyModuleMetadata.Add(tag);
        }

        foreach (var pending in snapshot.PendingFiles)
        {
            StageFile(pending);
        }

        foreach (var change in snapshot.WorkspaceModuleChanges)
        {
            _workspaceModuleChanges[change.FolderPath] = change;
        }
    }

    internal void Clear()
    {
        _dirtyTables.Clear();
        _dirtyModuleMetadata.Clear();
        _pendingFiles.Clear();
        _workspaceModuleChanges.Clear();
    }

    internal void ClearWorkspaceChanges() => _workspaceModuleChanges.Clear();

    private static PendingFileWrite Clone(PendingFileWrite pending)
        => pending with { Contents = pending.Contents.ToArray() };
}
