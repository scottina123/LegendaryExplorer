using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
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
/// Represents a single file-level usage of a mesh record from the asset database.
/// </summary>
public record MeshUsageItem(string FileName, string ContentDir, int UIndex)
{
    public string DisplayName => $"{FileName}  [{ContentDir}]";
}

/// <summary>
/// A picker dialog that lets the user choose a static mesh from the Asset Database
/// to import into the current package.
/// </summary>
public partial class StaticMeshPickerDialog : NotifyPropertyChangedWindowBase
{
    /// <summary>Result of the dialog: source file path and UIndex of the chosen static mesh.</summary>
    public (string FilePath, int UIndex)? SelectedResult { get; private set; }

    private readonly MEGame _game;
    private readonly AssetDB _database = new();

    private bool _isLoading = true;
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

    public ObservableCollectionExtended<AssetDatabase.MeshRecord> AllMeshRecords { get; } = [];
    public ObservableCollectionExtended<AssetDatabase.MeshRecord> FilteredMeshRecords { get; } = [];
    public ObservableCollectionExtended<MeshUsageItem> UsageItems { get; } = [];

    private AssetDatabase.MeshRecord _selectedMesh;
    public AssetDatabase.MeshRecord SelectedMesh
    {
        get => _selectedMesh;
        set
        {
            if (SetProperty(ref _selectedMesh, value))
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

    // Preview state
    private IMEPackage _previewPcc;
    private CancellationTokenSource _previewCts;
    private MeshUsageItem _lastResolvedUsage;
    private string _lastResolvedPath;

    public ICommand OKCommand { get; }

    public StaticMeshPickerDialog(MEGame game, Window owner = null)
    {
        _game = game;
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
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Start the MeshRenderer's render loop now that the control is fully loaded
        // and the D3D device has been initialised by LegacySceneRenderControl.
        MeshPreview.StartRendering();
        _ = LoadDatabaseAsync();
    }

    private async Task LoadDatabaseAsync()
    {
        string dbPath = AssetDatabaseWindow.GetDBPath(_game);
        if (!File.Exists(dbPath))
        {
            LoadingText = $"No asset database found for {_game}.\nPlease generate one using the Asset Database tool.";
            IsLoading = false;
            return;
        }

        using var cts = new CancellationTokenSource();
        await AssetDatabaseWindow.LoadDatabase(dbPath, _game, _database, cts.Token);

        var staticMeshes = _database.Meshes
            .Where(m => !m.IsSkeleton)
            .OrderBy(m => m.MeshName)
            .ToList();

        AllMeshRecords.AddRange(staticMeshes);
        FilteredMeshRecords.AddRange(staticMeshes);
        IsLoading = false;
    }

    private void RefreshUsages()
    {
        UsageItems.Clear();
        SelectedUsage = null;
        if (_selectedMesh is null) return;

        foreach (var usage in _selectedMesh.Usages)
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

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = FilterBox.Text.Trim();
        FilteredMeshRecords.Clear();
        FilteredMeshRecords.AddRange(
            string.IsNullOrEmpty(filter)
                ? AllMeshRecords
                : AllMeshRecords.Where(m => m.MeshName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private bool CanAccept() => SelectedUsage is not null;

    private void AcceptSelection()
    {
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

    private async void RefreshPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        MeshPreview.UnloadExport();
        _previewPcc?.Dispose();
        _previewPcc = null;

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
            // Nudge the render control so the first frame draws without needing a resize.
            MeshPreview.InvalidateMeasure();
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _previewCts?.Cancel();
        MeshPreview.StopRendering();
        MeshPreview.UnloadExport();
        MeshPreview.Dispose();
        _previewPcc?.Dispose();
        _previewPcc = null;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
