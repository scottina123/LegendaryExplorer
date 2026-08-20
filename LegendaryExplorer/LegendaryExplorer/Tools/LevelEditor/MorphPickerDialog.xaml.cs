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

public sealed record LocalMorphItem(int UIndex, string DisplayName);

public sealed record DatabaseMorphItem(
    BioMorphFaceRecord Morph,
    string FileName,
    string ContentDir)
{
    public string DisplayName => Morph.MorphName;
    public string Details => $"{Morph.SpeciesDisplayName} · Base head: {Morph.BaseHeadName}";
    public string SourceDisplayName => $"{FileName}  [{ContentDir}]";
    public int UIndex => Morph.UIndex;
}

/// <summary>
/// Lets the user choose and preview a BioMorphFace from the current package or Asset Database.
/// </summary>
public partial class MorphPickerDialog : NotifyPropertyChangedWindowBase
{
    public (string FilePath, int UIndex)? SelectedResult { get; private set; }

    private readonly MEGame _game;
    private readonly IMEPackage _package;
    private readonly AssetDB _database = new();
    private readonly CancellationTokenSource _windowCts = new();
    private CancellationTokenSource _previewCts;
    private IMEPackage _previewPcc;
    private DatabaseMorphItem _lastResolvedMorph;
    private string _lastResolvedPath;
    private bool _isClosing;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private string _loadingText = "Loading asset database...";
    public string LoadingText
    {
        get => _loadingText;
        private set => SetProperty(ref _loadingText, value);
    }

    private string _previewStatus = "Select a morph to preview.";
    public string PreviewStatus
    {
        get => _previewStatus;
        private set => SetProperty(ref _previewStatus, value);
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

    public ObservableCollectionExtended<LocalMorphItem> AllLocalMorphs { get; } = [];
    public ObservableCollectionExtended<LocalMorphItem> FilteredLocalMorphs { get; } = [];

    private LocalMorphItem _selectedLocalMorph;
    public LocalMorphItem SelectedLocalMorph
    {
        get => _selectedLocalMorph;
        set
        {
            if (SetProperty(ref _selectedLocalMorph, value))
            {
                RefreshPreviewAsync();
            }
        }
    }

    public ObservableCollectionExtended<DatabaseMorphItem> AllDatabaseMorphs { get; } = [];
    public ObservableCollectionExtended<DatabaseMorphItem> FilteredDatabaseMorphs { get; } = [];

    private DatabaseMorphItem _selectedDatabaseMorph;
    public DatabaseMorphItem SelectedDatabaseMorph
    {
        get => _selectedDatabaseMorph;
        set
        {
            if (SetProperty(ref _selectedDatabaseMorph, value))
            {
                RefreshPreviewAsync();
            }
        }
    }

    public ICommand OKCommand { get; }

    public MorphPickerDialog(MEGame game, IMEPackage package, Window owner = null)
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
        PopulateLocalMorphs();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MorphPreview.StartRendering();
        _ = LoadDatabaseAsync();
    }

    private void PopulateLocalMorphs()
    {
        var morphs = _package.Exports
            .Where(export => export.ClassName == "BioMorphFace" && !export.IsDefaultObject)
            .OrderBy(export => export.InstancedFullPath, StringComparer.OrdinalIgnoreCase)
            .Select(export => new LocalMorphItem(
                export.UIndex,
                $"{export.UIndex}: {export.InstancedFullPath}"));

        AllLocalMorphs.AddRange(morphs);
        FilteredLocalMorphs.AddRange(AllLocalMorphs);
    }

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

        try
        {
            await AssetDatabaseWindow.LoadDatabase(dbPath, _game, _database, _windowCts.Token);
            if (_isClosing || _windowCts.IsCancellationRequested)
            {
                return;
            }

            var morphs = _database.MorphFaces
                .Where(morph => morph.FileKey >= 0 && morph.FileKey < _database.FileList.Count)
                .Select(morph =>
                {
                    FileNameDirKeyPair file = _database.FileList[morph.FileKey];
                    string contentDir = file.DirectoryKey >= 0 && file.DirectoryKey < _database.ContentDir.Count
                        ? _database.ContentDir[file.DirectoryKey]
                        : string.Empty;
                    return new DatabaseMorphItem(morph, file.FileName, contentDir);
                })
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourceDisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AllDatabaseMorphs.AddRange(morphs);
            FilteredDatabaseMorphs.AddRange(morphs);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                LoadingText = $"Could not load the {_game} Asset Database:\n{exception.GetBaseException().Message}";
            }
        }
        finally
        {
            if (!_isClosing)
            {
                IsLoading = false;
            }
        }
    }

    private void LocalFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = LocalFilterBox.Text.Trim();
        FilteredLocalMorphs.Clear();
        FilteredLocalMorphs.AddRange(
            string.IsNullOrEmpty(filter)
                ? AllLocalMorphs
                : AllLocalMorphs.Where(item =>
                    item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private void DbFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = DbFilterBox.Text.Trim();
        FilteredDatabaseMorphs.Clear();
        FilteredDatabaseMorphs.AddRange(
            string.IsNullOrEmpty(filter)
                ? AllDatabaseMorphs
                : AllDatabaseMorphs.Where(item =>
                    item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || item.Details.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || item.SourceDisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private async void RefreshPreviewAsync()
    {
        if (_isClosing)
        {
            return;
        }

        _previewCts?.Cancel();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
        CancellationToken cancellationToken = _previewCts.Token;
        ClearPreview();

        try
        {
            if (SelectedTabIndex == 0)
            {
                LocalMorphItem localMorph = SelectedLocalMorph;
                if (localMorph is null)
                {
                    PreviewStatus = "Select a morph to preview.";
                    return;
                }

                if (!_package.TryGetUExport(localMorph.UIndex, out ExportEntry export)
                    || !MorphPreview.CanParse(export))
                {
                    PreviewStatus = "The selected export is not a previewable BioMorphFace.";
                    return;
                }

                PreviewStatus = null;
                MorphPreview.LoadExport(export);
                MorphPreview.InvalidateMeasure();
                return;
            }

            DatabaseMorphItem databaseMorph = SelectedDatabaseMorph;
            if (databaseMorph is null)
            {
                PreviewStatus = "Select a morph to preview.";
                return;
            }

            PreviewStatus = "Loading preview...";
            string filePath = await ResolveDatabaseFileAsync(databaseMorph, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (filePath is null)
            {
                PreviewStatus = $"Could not locate {databaseMorph.FileName} in the game installation.";
                return;
            }

            IMEPackage previewPackage = MEPackageHandler.OpenMEPackage(filePath);
            if (cancellationToken.IsCancellationRequested)
            {
                previewPackage.Dispose();
                return;
            }

            if (!previewPackage.TryGetUExport(databaseMorph.UIndex, out ExportEntry databaseExport)
                || !MorphPreview.CanParse(databaseExport))
            {
                previewPackage.Dispose();
                PreviewStatus = $"Export #{databaseMorph.UIndex} is not a previewable BioMorphFace.";
                return;
            }

            _previewPcc = previewPackage;
            _lastResolvedMorph = databaseMorph;
            _lastResolvedPath = filePath;
            PreviewStatus = null;
            MorphPreview.LoadExport(databaseExport);
            MorphPreview.InvalidateMeasure();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                ClearPreview();
                PreviewStatus = $"Could not preview this morph: {exception.GetBaseException().Message}";
            }
        }
    }

    private async Task<string> ResolveDatabaseFileAsync(
        DatabaseMorphItem databaseMorph,
        CancellationToken cancellationToken)
        => await Task.Run(() => ResolveDatabaseFile(databaseMorph), cancellationToken);

    private string ResolveDatabaseFile(DatabaseMorphItem databaseMorph)
    {
        string gameRoot = MEDirectories.GetDefaultGamePath(_game);
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return null;
        }

        string searchPattern = Path.HasExtension(databaseMorph.FileName)
            ? databaseMorph.FileName
            : $"{databaseMorph.FileName}.*";
        return Directory.EnumerateFiles(gameRoot, searchPattern, SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                string.IsNullOrEmpty(databaseMorph.ContentDir)
                || path.Contains(databaseMorph.ContentDir, StringComparison.OrdinalIgnoreCase));
    }

    private void ClearPreview()
    {
        MorphPreview?.UnloadExport();
        _previewPcc?.Dispose();
        _previewPcc = null;
    }

    private bool CanAccept() => SelectedTabIndex == 0
        ? SelectedLocalMorph is not null
        : SelectedDatabaseMorph is not null;

    private void AcceptSelection()
    {
        if (SelectedTabIndex == 0)
        {
            if (SelectedLocalMorph is null)
            {
                return;
            }

            SelectedResult = (null, SelectedLocalMorph.UIndex);
            DialogResult = true;
            return;
        }

        if (SelectedDatabaseMorph is null)
        {
            return;
        }

        string filePath = SelectedDatabaseMorph == _lastResolvedMorph
            ? _lastResolvedPath
            : ResolveDatabaseFile(SelectedDatabaseMorph);
        if (filePath is null)
        {
            MessageBox.Show(
                this,
                $"Could not locate '{SelectedDatabaseMorph.FileName}' in the game directory.",
                "File Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedResult = (filePath, SelectedDatabaseMorph.UIndex);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _isClosing = true;
        _windowCts.Cancel();
        _previewCts?.Cancel();
        MorphPreview.StopRendering();
        ClearPreview();
        MorphPreview.Dispose();
        _windowCts.Dispose();
        _previewCts?.Dispose();
    }
}
