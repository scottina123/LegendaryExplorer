using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Represents a SkeletalMesh export found in the local package.
/// </summary>
public record LocalSkeletalMeshItem(int UIndex, string DisplayName);

/// <summary>
/// A picker dialog that lets the user choose a skeletal mesh either from the
/// current package or from the Asset Database to import.
/// </summary>
public partial class SkeletalMeshPickerDialog : NotifyPropertyChangedWindowBase
{
    /// <summary>
    /// Result of the dialog.  FilePath is null when the mesh was chosen from the local package.
    /// </summary>
    public (string FilePath, int UIndex)? SelectedResult { get; private set; }

    private readonly MEGame _game;
    private readonly IMEPackage _package;
    private readonly AssetDB _database = new();

    #region Loading state (Asset Database tab)

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

    #endregion

    #region Tab selection

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

    #endregion

    #region Tab 1 – Package Meshes

    public ObservableCollectionExtended<LocalSkeletalMeshItem> AllLocalMeshes { get; } = [];
    public ObservableCollectionExtended<LocalSkeletalMeshItem> FilteredLocalMeshes { get; } = [];

    private LocalSkeletalMeshItem _selectedLocalMesh;
    public LocalSkeletalMeshItem SelectedLocalMesh
    {
        get => _selectedLocalMesh;
        set
        {
            if (SetProperty(ref _selectedLocalMesh, value))
            {
                RefreshPreviewAsync();
            }
        }
    }

    #endregion

    #region Tab 2 – Asset Database

    public ObservableCollectionExtended<AssetDatabase.MeshRecord> AllDbMeshRecords { get; } = [];
    public ObservableCollectionExtended<AssetDatabase.MeshRecord> FilteredDbMeshRecords { get; } = [];
    public ObservableCollectionExtended<MeshUsageItem> UsageItems { get; } = [];

    private AssetDatabase.MeshRecord _selectedDbMesh;
    public AssetDatabase.MeshRecord SelectedDbMesh
    {
        get => _selectedDbMesh;
        set
        {
            if (SetProperty(ref _selectedDbMesh, value))
            {
                RefreshUsages();
            }
        }
    }

    private MeshUsageItem _selectedUsage;
    public MeshUsageItem SelectedUsage
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

    #endregion

    // Preview state
    private IMEPackage _previewPcc;
    private CancellationTokenSource _previewCts;
    private MeshUsageItem _lastResolvedUsage;
    private string _lastResolvedPath;

    public ICommand OKCommand { get; }

    public SkeletalMeshPickerDialog(MEGame game, IMEPackage package, Window owner = null)
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
        PopulateLocalMeshes();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MeshPreview.StartRendering();
        _ = LoadDatabaseAsync();
    }

    #region Local mesh population

    private void PopulateLocalMeshes()
    {
        foreach (var exp in _package.Exports)
        {
            if (exp.IsA("SkeletalMesh"))
            {
                AllLocalMeshes.Add(new LocalSkeletalMeshItem(exp.UIndex,
                    $"{exp.UIndex}: {exp.ObjectName.Instanced}"));
            }
        }

        FilteredLocalMeshes.AddRange(AllLocalMeshes);
    }

    #endregion

    #region Asset Database loading

    private async Task LoadDatabaseAsync()
    {
        IsLoading = true;
        string dbPath = AssetDatabaseWindow.GetDBPath(_game);
        if (!File.Exists(dbPath))
        {
            LoadingText = $"No asset database found for {_game}.\nPlease generate one using the Asset Database tool.";
            IsLoading = false;
            return;
        }

        using var cts = new CancellationTokenSource();
        await AssetDatabaseWindow.LoadDatabase(dbPath, _game, _database, cts.Token);

        var skeletalMeshes = _database.Meshes
            .Where(m => m.IsSkeleton)
            .OrderBy(m => m.MeshName)
            .ToList();

        AllDbMeshRecords.AddRange(skeletalMeshes);
        FilteredDbMeshRecords.AddRange(skeletalMeshes);
        IsLoading = false;
    }

    #endregion

    #region Filtering

    private void LocalFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = LocalFilterBox.Text.Trim();
        FilteredLocalMeshes.Clear();
        FilteredLocalMeshes.AddRange(
            string.IsNullOrEmpty(filter)
                ? AllLocalMeshes
                : AllLocalMeshes.Where(m => m.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private void DbFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = DbFilterBox.Text.Trim();
        FilteredDbMeshRecords.Clear();
        FilteredDbMeshRecords.AddRange(
            string.IsNullOrEmpty(filter)
                ? AllDbMeshRecords
                : AllDbMeshRecords.Where(m => m.MeshName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    #endregion

    #region Usage list (Asset Database tab)

    private void RefreshUsages()
    {
        UsageItems.Clear();
        SelectedUsage = null;
        if (_selectedDbMesh is null) return;

        foreach (var usage in _selectedDbMesh.Usages)
        {
            if (usage.FileKey < 0 || usage.FileKey >= _database.FileList.Count) continue;
            var filePair = _database.FileList[usage.FileKey];
            string contentDir = filePair.DirectoryKey >= 0 && filePair.DirectoryKey < _database.ContentDir.Count
                ? _database.ContentDir[filePair.DirectoryKey]
                : string.Empty;
            UsageItems.Add(new MeshUsageItem(filePair.FileName, contentDir, usage.UIndex));
        }

        if (UsageItems.Count > 0)
        {
            SelectedUsage = UsageItems[0];
        }
    }

    #endregion

    #region Preview

    private async void RefreshPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        MeshPreview.UnloadExport();
        _previewPcc?.Dispose();
        _previewPcc = null;

        if (SelectedTabIndex == 0)
        {
            // Local mesh – load directly from the package
            var localMesh = SelectedLocalMesh;
            if (localMesh is null) return;
            if (localMesh.UIndex > 0 && localMesh.UIndex <= _package.ExportCount)
            {
                MeshPreview.LoadExport(_package.GetUExport(localMesh.UIndex));
                MeshPreview.InvalidateMeasure();
            }
        }
        else
        {
            // Asset Database mesh – resolve file and load
            var usage = SelectedUsage;
            if (usage is null) return;

            string gameRoot = MEDirectories.GetDefaultGamePath(_game);
            if (gameRoot is null || !Directory.Exists(gameRoot)) return;

            string filePath;
            try
            {
                filePath = await Task.Run(() =>
                    Directory.GetFiles(gameRoot, $"{usage.FileName}.*", SearchOption.AllDirectories)
                        .FirstOrDefault(f => f.Contains(usage.ContentDir, StringComparison.OrdinalIgnoreCase)),
                    ct);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested || filePath is null) return;

            _lastResolvedUsage = usage;
            _lastResolvedPath = filePath;

            _previewPcc = MEPackageHandler.OpenMEPackage(filePath);
            if (!ct.IsCancellationRequested && usage.UIndex <= _previewPcc.ExportCount)
            {
                MeshPreview.LoadExport(_previewPcc.GetUExport(usage.UIndex));
                MeshPreview.InvalidateMeasure();
            }
        }
    }

    #endregion

    #region Accept / Cancel

    private bool CanAccept()
    {
        return SelectedTabIndex == 0
            ? SelectedLocalMesh is not null
            : SelectedUsage is not null;
    }

    private void AcceptSelection()
    {
        if (SelectedTabIndex == 0)
        {
            // Local mesh – no import needed
            if (SelectedLocalMesh is null) return;
            SelectedResult = (null, SelectedLocalMesh.UIndex);
            DialogResult = true;
        }
        else
        {
            // Asset Database mesh – resolve file path
            if (SelectedUsage is null) return;

            string filePath;
            if (SelectedUsage == _lastResolvedUsage && _lastResolvedPath is not null)
            {
                filePath = _lastResolvedPath;
            }
            else
            {
                string gameRoot = MEDirectories.GetDefaultGamePath(_game);
                if (gameRoot is null || !Directory.Exists(gameRoot))
                {
                    MessageBox.Show(
                        $"Game path for {_game} not found. Check your Legendary Explorer settings.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                filePath = Directory
                    .GetFiles(gameRoot, $"{SelectedUsage.FileName}.*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Contains(SelectedUsage.ContentDir, StringComparison.OrdinalIgnoreCase));

                if (filePath is null)
                {
                    MessageBox.Show(
                        $"Could not locate '{SelectedUsage.FileName}' in the game directory.",
                        "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            SelectedResult = (filePath, SelectedUsage.UIndex);
            DialogResult = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    #endregion

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _previewCts?.Cancel();
        MeshPreview.StopRendering();
        MeshPreview.UnloadExport();
        MeshPreview.Dispose();
        _previewPcc?.Dispose();
        _previewPcc = null;
    }
}
