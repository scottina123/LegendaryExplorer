using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.AssetDatabase.VFXPreview;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LegendaryExplorer.Tools.LevelEditor;

public record LocalParticleSystemItem(int UIndex, string DisplayName);

public record ParticleSystemUsageItem(string FileName, string ContentDir, int UIndex)
{
    public string DisplayName => $"{FileName}  [{ContentDir}]";
}

/// <summary>
/// Lets the user choose a ParticleSystem from the current package or the Asset Database.
/// </summary>
public partial class ParticleSystemPickerDialog : NotifyPropertyChangedWindowBase
{
    public (string FilePath, int UIndex)? SelectedResult { get; private set; }

    private readonly MEGame _game;
    private readonly IMEPackage _package;
    private readonly AssetDB _database = new();

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private string _loadingText = "Loading asset database...";
    public string LoadingText
    {
        get => _loadingText;
        set => SetProperty(ref _loadingText, value);
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                RefreshPreviewAsync();
            }
        }
    }

    public ObservableCollectionExtended<LocalParticleSystemItem> AllLocalParticleSystems { get; } = [];
    public ObservableCollectionExtended<LocalParticleSystemItem> FilteredLocalParticleSystems { get; } = [];

    private LocalParticleSystemItem _selectedLocalParticleSystem;
    public LocalParticleSystemItem SelectedLocalParticleSystem
    {
        get => _selectedLocalParticleSystem;
        set
        {
            if (SetProperty(ref _selectedLocalParticleSystem, value))
            {
                RefreshPreviewAsync();
            }
        }
    }

    public ObservableCollectionExtended<AssetDatabase.ParticleSysRecord> AllParticleRecords { get; } = [];
    public ObservableCollectionExtended<AssetDatabase.ParticleSysRecord> FilteredParticleRecords { get; } = [];
    public ObservableCollectionExtended<ParticleSystemUsageItem> UsageItems { get; } = [];

    private AssetDatabase.ParticleSysRecord _selectedParticleRecord;
    public AssetDatabase.ParticleSysRecord SelectedParticleRecord
    {
        get => _selectedParticleRecord;
        set
        {
            if (SetProperty(ref _selectedParticleRecord, value))
            {
                RefreshUsages();
            }
        }
    }

    private ParticleSystemUsageItem _selectedUsage;
    public ParticleSystemUsageItem SelectedUsage
    {
        get => _selectedUsage;
        set
        {
            if (SetProperty(ref _selectedUsage, value))
            {
                RefreshPreviewAsync();
            }
        }
    }

    private IMEPackage _previewPackage;
    private CancellationTokenSource _previewCts;
    private CancellationTokenSource _loadCts;
    private ParticleSystemUsageItem _lastResolvedUsage;
    private string _lastResolvedPath;
    private readonly Dictionary<int, string> _resolvedDatabasePaths = [];

    public ICommand OKCommand { get; }

    public ParticleSystemPickerDialog(MEGame game, IMEPackage package, Window owner = null)
    {
        _game = game;
        _package = package;
        DataContext = this;
        OKCommand = new GenericCommand(AcceptSelection, CanAccept);

        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        InitializeComponent();
        PopulateLocalParticleSystems();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        VfxPreviewEnabledCheckBox.IsChecked = true;
        RefreshPreviewAsync();
        _loadCts = new CancellationTokenSource();
        _ = LoadDatabaseAsync(_loadCts.Token);
    }

    private void PopulateLocalParticleSystems()
    {
        foreach (ExportEntry export in _package.Exports.Where(export => export.IsA("ParticleSystem")))
        {
            AllLocalParticleSystems.Add(new LocalParticleSystemItem(
                export.UIndex,
                $"{export.UIndex}: {export.InstancedFullPath}"));
        }

        FilteredLocalParticleSystems.AddRange(AllLocalParticleSystems);
    }

    private async Task LoadDatabaseAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            string dbPath = AssetDatabaseWindow.GetDBPath(_game);
            if (!File.Exists(dbPath))
            {
                LoadingText = $"No asset database found for {_game}.\nPlease generate one using the Asset Database tool.";
                return;
            }

            await AssetDatabaseWindow.LoadDatabase(dbPath, _game, _database, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var particleSystems = _database.Particles
                .Where(record => record.VFXType == AssetDatabase.ParticleSysRecord.VFXClass.ParticleSystem)
                .OrderBy(record => record.PSName)
                .ToList();
            AllParticleRecords.AddRange(particleSystems);
            FilteredParticleRecords.AddRange(particleSystems);
            RefreshPreviewAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LocalFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = LocalFilterBox.Text.Trim();
        FilteredLocalParticleSystems.Clear();
        FilteredLocalParticleSystems.AddRange(
            string.IsNullOrEmpty(filter)
                ? AllLocalParticleSystems
                : AllLocalParticleSystems.Where(item => item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private void DbFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = DbFilterBox.Text.Trim();
        FilteredParticleRecords.Clear();
        FilteredParticleRecords.AddRange(
            string.IsNullOrEmpty(filter)
                ? AllParticleRecords
                : AllParticleRecords.Where(record => record.PSName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private void RefreshUsages()
    {
        UsageItems.Clear();
        SelectedUsage = null;
        if (SelectedParticleRecord is null)
        {
            return;
        }

        foreach (AssetDatabase.ParticleSysUsage usage in SelectedParticleRecord.Usages)
        {
            if (usage.FileKey < 0 || usage.FileKey >= _database.FileList.Count)
            {
                continue;
            }

            var filePair = _database.FileList[usage.FileKey];
            string contentDir = filePair.DirectoryKey >= 0 && filePair.DirectoryKey < _database.ContentDir.Count
                ? _database.ContentDir[filePair.DirectoryKey]
                : string.Empty;
            UsageItems.Add(new ParticleSystemUsageItem(filePair.FileName, contentDir, usage.UIndex));
        }

        if (UsageItems.Count > 0)
        {
            SelectedUsage = UsageItems[0];
        }
    }

    private async void RefreshPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _previewCts.Token;

        VfxPreview.UnloadExport();
        _previewPackage?.Dispose();
        _previewPackage = null;

        if (SelectedTabIndex == 0)
        {
            if (SelectedLocalParticleSystem is { } local
                && _package.TryGetUExport(local.UIndex, out ExportEntry localParticleSystem))
            {
                VfxPreview.LoadExport(localParticleSystem, ResolveVfxImportFallbacks);
            }
            return;
        }

        ParticleSystemUsageItem usage = SelectedUsage;
        AssetDatabase.ParticleSysRecord record = SelectedParticleRecord;
        if (usage is null || record is null)
        {
            return;
        }

        string filePath;
        try
        {
            filePath = await ResolveUsagePathAsync(usage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || filePath is null)
        {
            VfxPreview.ShowUnavailable($"Could not locate {usage.FileName} in the configured {_game} game directory.");
            return;
        }

        try
        {
            _lastResolvedUsage = usage;
            _lastResolvedPath = filePath;
            _previewPackage = MEPackageHandler.OpenMEPackage(filePath);
            ExportEntry particleSystem = _previewPackage.TryGetUExport(usage.UIndex, out ExportEntry indexedExport)
                                             && IsMatchingParticleSystem(indexedExport, record)
                ? indexedExport
                : _previewPackage.Exports.FirstOrDefault(export => IsMatchingParticleSystem(export, record));
            if (particleSystem is null)
            {
                VfxPreview.ShowUnavailable($"{record.PSName} was not found in {usage.FileName}. The Asset Database may need to be rebuilt.");
                return;
            }

            VfxPreview.LoadExport(particleSystem, ResolveVfxImportFallbacks);
        }
        catch (Exception exception)
        {
            _previewPackage?.Dispose();
            _previewPackage = null;
            VfxPreview.ShowUnavailable($"{record.PSName} could not be previewed: {exception.Message}");
        }
    }

    private Task<string> ResolveUsagePathAsync(ParticleSystemUsageItem usage, CancellationToken cancellationToken)
    {
        string gameRoot = MEDirectories.GetDefaultGamePath(_game);
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return Task.FromResult<string>(null);
        }

        return Task.Run(() => Directory
            .EnumerateFiles(gameRoot, $"{usage.FileName}.*", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains(usage.ContentDir, StringComparison.OrdinalIgnoreCase)), cancellationToken);
    }

    private static bool IsMatchingParticleSystem(ExportEntry export, AssetDatabase.ParticleSysRecord record) =>
        export?.ClassName == "ParticleSystem"
        && string.Equals(export.ObjectName.Instanced, record.PSName, StringComparison.Ordinal);

    private IEnumerable<VfxImportFallback> ResolveVfxImportFallbacks(ImportEntry import)
    {
        string importPath = import?.InstancedFullPath;
        if (string.IsNullOrWhiteSpace(importPath))
        {
            yield break;
        }

        IEnumerable<AssetDatabase.MaterialRecord> matchingMaterials = _database.Materials.Where(material =>
            string.Equals(AssetDatabaseWindow.GetVfxMaterialPath(material), importPath, StringComparison.OrdinalIgnoreCase));
        foreach (AssetDatabase.MatUsage usage in matchingMaterials.SelectMany(material => material.Usages)
                     .Where(usage => IsValidFileKey(usage.FileKey))
                     .OrderBy(usage => usage.IsInDLC))
        {
            string filePath = ResolveDatabaseFilePath(usage.FileKey);
            if (filePath is not null)
            {
                yield return new VfxImportFallback(filePath, usage.UIndex);
            }
        }

        if (!import.ClassName.StartsWith("Texture", StringComparison.Ordinal))
        {
            yield break;
        }

        IEnumerable<AssetDatabase.TextureRecord> matchingTextures = _database.Textures.Where(texture =>
            string.Equals(AssetDatabaseWindow.GetVfxTexturePath(texture), importPath, StringComparison.OrdinalIgnoreCase));
        foreach (AssetDatabase.TextureUsage usage in matchingTextures.SelectMany(texture => texture.Usages)
                     .Where(usage => IsValidFileKey(usage.FileKey))
                     .OrderBy(usage => usage.IsInDLC))
        {
            string filePath = ResolveDatabaseFilePath(usage.FileKey);
            if (filePath is not null)
            {
                yield return new VfxImportFallback(filePath, usage.UIndex);
            }
        }
    }

    private bool IsValidFileKey(int fileKey) => fileKey >= 0 && fileKey < _database.FileList.Count;

    private string ResolveDatabaseFilePath(int fileKey)
    {
        if (!IsValidFileKey(fileKey))
        {
            return null;
        }
        if (_resolvedDatabasePaths.TryGetValue(fileKey, out string cachedPath))
        {
            return cachedPath;
        }

        AssetDatabase.FileNameDirKeyPair filePair = _database.FileList[fileKey];
        string contentDir = filePair.DirectoryKey >= 0 && filePair.DirectoryKey < _database.ContentDir.Count
            ? _database.ContentDir[filePair.DirectoryKey]
            : string.Empty;
        string gameRoot = MEDirectories.GetDefaultGamePath(_game);
        string filePath = string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)
            ? null
            : Directory.EnumerateFiles(gameRoot, $"{filePair.FileName}.*", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains(contentDir, StringComparison.OrdinalIgnoreCase));
        _resolvedDatabasePaths[fileKey] = filePath;
        return filePath;
    }

    private bool CanAccept() => SelectedTabIndex == 0
        ? SelectedLocalParticleSystem is not null
        : SelectedUsage is not null;

    private void AcceptSelection()
    {
        if (SelectedTabIndex == 0)
        {
            if (SelectedLocalParticleSystem is null)
            {
                return;
            }

            SelectedResult = (null, SelectedLocalParticleSystem.UIndex);
            DialogResult = true;
            return;
        }

        if (SelectedUsage is null)
        {
            return;
        }

        string filePath = SelectedUsage == _lastResolvedUsage ? _lastResolvedPath : null;
        if (filePath is null)
        {
            string gameRoot = MEDirectories.GetDefaultGamePath(_game);
            if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            {
                MessageBox.Show(
                    $"Game path for {_game} not found. Check your Legendary Explorer settings.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            filePath = Directory
                .EnumerateFiles(gameRoot, $"{SelectedUsage.FileName}.*", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains(SelectedUsage.ContentDir, StringComparison.OrdinalIgnoreCase));
            if (filePath is null)
            {
                MessageBox.Show(
                    $"Could not locate '{SelectedUsage.FileName}' in the game directory.",
                    "File Not Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        SelectedResult = (filePath, SelectedUsage.UIndex);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _loadCts?.Cancel();
        _previewCts?.Cancel();
        VfxPreview.UnloadExport();
        _previewPackage?.Dispose();
        _previewPackage = null;
    }
}
