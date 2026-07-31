using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Matinee;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using Microsoft.Win32;
using Newtonsoft.Json;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs;

public partial class CameraPresetDialog : Window
{
    private sealed record PresetSearchResult(CameraPreset Preset, string Name, string CategoryDisplay);

    private static readonly object PreviewLevelPathsLock = new();
    private static readonly List<string> PreviewLevelPaths = [];

    private sealed class PreviewActorConfiguration
    {
        public string DisplayName { get; set; }
        public string ModelName { get; set; }
        public CameraAnchorMode AnchorMode { get; set; }
        public string SingleActorTag { get; set; }
        public string ActorTags { get; set; }
        public string PrimaryActorTag { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Roll { get; set; }
        public float Pitch { get; set; }
        public float Yaw { get; set; }

        public CameraOrigin Origin
        {
            get => new(new Vector3(X, Y, Z), new Vector3(Roll, Pitch, Yaw));
            set
            {
                X = value.Location.X;
                Y = value.Location.Y;
                Z = value.Location.Z;
                Roll = value.Rotation.X;
                Pitch = value.Rotation.Y;
                Yaw = value.Rotation.Z;
            }
        }
    }

    private static readonly Dictionary<string, string> SessionBranchChoices = new(StringComparer.Ordinal);

    private static string SavedOriginPath => Path.Combine(AppDirectories.AppDataFolder, "CameraPresetOriginV2.txt");
    private static string SavedPresetPath => Path.Combine(AppDirectories.AppDataFolder, "CameraPresetSelection.txt");
    private static string SavedDistanceScalePath => Path.Combine(AppDirectories.AppDataFolder, "CameraPresetDistanceScale.txt");
    private string SavedPreviewActorsPath => Path.Combine(AppDirectories.AppDataFolder,
        $"CameraPresetPreviewActors_{_package?.Game ?? MEGame.Unknown}.json");

    private readonly Func<CameraOrigin?> _getTrackKeyOrigin;
    private readonly Func<CameraOrigin?> _getViewportOrigin;
    private readonly Action<GeneratedCameraKey> _previewCamera;
    private readonly float? _maximumEndTime;
    private readonly IMEPackage _package;
    private readonly CameraActorAnchorContext _actorAnchorContext;
    private readonly ExportEntry _selectedTrackMove;
    private ExportEntry _selectedDirectorTrack;
    private readonly ExportEntry _interpData;
    private AssetDB _previewAssetDatabase;
    private List<(string FileName, string ContentDir)> _previewAssetFiles = [];
    private readonly ObservableCollection<PreviewActorConfiguration> _previewActors = [];
    private PreviewActorConfiguration _selectedPreviewActor;
    private bool _updatingPreviewActorControls;
    private CameraPreset _selectedPreset;
    private MulticamCameraPreset _selectedMulticamPreset;
    private bool _updatingCameraSpeed;
    private bool _updatingDistanceScale;
    private bool _updatingResolvedOrigin;
    private bool _updatingPresetSelection;
    private string _previewActorLocationScrubAxes = "X";
    private double _previewActorLocationScrubAccumulator;
    private double _previewActorLocationScrubPreviousHorizontalChange;
    private string _previewActorRotationDialAxes = "Roll";
    private bool _previewActorRotationDialDragging;
    private double _previewActorRotationDialAngleAccumulator;
    private double _previewActorRotationDialPreviousAngle;

    private ComboBox PreviewActorSelector => FindName("PreviewActorComboBox") as ComboBox;
    private Button PreviewRecentLevelsButtonControl => FindName("PreviewRecentLevelsButton") as Button;
    private ContextMenu PreviewRecentLevelsContextMenu => PreviewRecentLevelsButtonControl?.ContextMenu;
    private TextBlock PreviewLevelStatus => FindName("PreviewLevelStatusTextBlock") as TextBlock;
    private Grid PreviewActorRotationDialControl => FindName("PreviewActorRotationDial") as Grid;
    private System.Windows.Shapes.Line PreviewActorRotationDialIndicatorControl =>
        FindName("PreviewActorRotationDialIndicator") as System.Windows.Shapes.Line;

    public IReadOnlyList<GeneratedCameraKey> GeneratedKeys { get; private set; }
    public float GeneratedStartTime { get; private set; }

    public CameraPresetDialog(Func<CameraOrigin?> getTrackKeyOrigin, Func<CameraOrigin?> getViewportOrigin,
        Action<GeneratedCameraKey> previewCamera, float initialStartTime = 0, float? maximumEndTime = null,
        IMEPackage package = null, CameraActorAnchorContext actorAnchorContext = null,
        ExportEntry selectedTrackMove = null, ExportEntry selectedDirectorTrack = null,
        ExportEntry interpData = null)
    {
        _getTrackKeyOrigin = getTrackKeyOrigin;
        _getViewportOrigin = getViewportOrigin;
        _previewCamera = previewCamera;
        _maximumEndTime = maximumEndTime;
        _package = package;
        _actorAnchorContext = actorAnchorContext;
        _selectedTrackMove = selectedTrackMove;
        _selectedDirectorTrack = selectedDirectorTrack;
        _interpData = interpData;

        InitializeComponent();
        CustomWindowChrome.ApplyCustomChrome(this);

        StaticPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.StaticShots);
        DynamicPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.DynamicShots);
        ReactionPresetList.ItemsSource = CameraPresetCatalog.GetByCategory(CameraPresetCategory.ReactionShots);
        SavedPresetList.ItemsSource = SavedCameraPresetManager.Presets;
        SaveTrackMovePresetButton.IsEnabled = _selectedTrackMove is not null;
        SaveMulticamPresetButton.IsEnabled = _selectedDirectorTrack is not null && _interpData is not null;
        UseTrackKeyButton.IsEnabled = _getTrackKeyOrigin?.Invoke() is not null;
        UseOtherTrackKeyButton.IsEnabled = _package is not null;
        bool hasViewport = _getViewportOrigin?.Invoke() is not null;
        UseViewportLocationButton.IsEnabled = hasViewport;
        UseViewportTransformButton.IsEnabled = hasViewport;
        PreviewButton.IsEnabled = _previewCamera is not null && hasViewport;
        StatusTextBlock.Text = hasViewport ? "Preview uses the connected Level Editor viewport." : "Connect a Level Editor to enable viewport actions and preview.";
        StartTimeTextBox.Text = Format(initialStartTime);
        SetOrigin(LoadSavedOrigin());
        SetDistanceScale(LoadSavedDistanceScale());
        InitializeActorAnchorControls();
        InitializePreviewActorLayout();
        CameraPreviewControl.SelectedActorTransformChanged += PreviewActorGizmo_TransformChanged;
        CameraPreviewControl.SelectedActorSnapRequested += PreviewActorViewportSnapRequested;
        foreach (TextBox textBox in new[]
        {
            OriginXTextBox, OriginYTextBox, OriginZTextBox, OriginRollTextBox, OriginPitchTextBox, OriginYawTextBox,
            ForwardDistanceTextBox, SideOffsetTextBox, HeightOffsetTextBox, LookAtHeightTextBox,
            LocalRollTextBox, LocalPitchTextBox, LocalYawTextBox, DurationTextBox, MovementAmountTextBox
        })
        {
            textBox.TextChanged += PreviewParameter_TextChanged;
        }
        SelectSavedPreset();
        if (_selectedDirectorTrack is not null)
        {
            SingleCamTab.Visibility = Visibility.Collapsed;
            MulticamTab.Visibility = Visibility.Visible;
            CameraModeTabs.SelectedIndex = 1;
            RefreshMulticamList();
            SelectFirstMulticamPreset();
        }
        else if (_selectedTrackMove is not null)
        {
            SingleCamTab.Visibility = Visibility.Visible;
            MulticamTab.Visibility = Visibility.Collapsed;
            CameraModeTabs.SelectedItem = SingleCamTab;
        }
        _ = InitializePreviewActorModelsAsync();
        _ = RestorePreviewLevelsAsync();
    }

    private static string RecentLevelSetsFile => Path.Combine(
        Directory.CreateDirectory(Path.Combine(AppDirectories.AppDataFolder, "LevelEditor")).FullName,
        "RECENTSETS");

    private async Task RestorePreviewLevelsAsync()
    {
        List<string> paths;
        lock (PreviewLevelPathsLock)
        {
            paths = PreviewLevelPaths.Where(File.Exists).ToList();
        }

        foreach (string path in paths)
        {
            await LoadPreviewLevelAsync(path, replace: false, updateSession: false).ConfigureAwait(true);
        }
    }

    private async void OpenPreviewLevel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDirectories.GetOpenPackageDialog();
        if (DirectoryMemory.ShowDialog(dialog) == true)
        {
            await LoadPreviewLevelAsync(dialog.FileName, replace: true).ConfigureAwait(true);
        }
    }

    private async void AddPreviewLevel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDirectories.GetOpenPackageDialog();
        if (DirectoryMemory.ShowDialog(dialog) == true)
        {
            await LoadPreviewLevelAsync(dialog.FileName, replace: false).ConfigureAwait(true);
        }
    }

    private void UnloadPreviewLevel_Click(object sender, RoutedEventArgs e)
    {
        CameraPreviewControl.UnloadLevels();
        UpdatePreviewLevelSession();
        UpdatePreviewLevelStatus();
    }

    private void PreviewRecentLevelsButton_Click(object sender, RoutedEventArgs e)
    {
        PreviewRecentLevelsContextMenu.PlacementTarget = PreviewRecentLevelsButtonControl;
        PreviewRecentLevelsContextMenu.IsOpen = true;
    }

    private void PreviewRecentLevelsMenu_Opened(object sender, RoutedEventArgs e)
    {
        PreviewRecentLevelsContextMenu.Items.Clear();
        List<RecentFileSet> recentSets = LoadRecentLevelSets();
        if (recentSets.Count == 0)
        {
            PreviewRecentLevelsContextMenu.Items.Add(new MenuItem { Header = "No recent levels", IsEnabled = false });
            return;
        }

        foreach (RecentFileSet set in recentSets)
        {
            var item = new MenuItem
            {
                Header = set.DisplayName.Replace("_", "__"),
                ToolTip = set.TooltipText,
                Tag = set
            };
            item.Click += PreviewRecentLevel_Click;
            PreviewRecentLevelsContextMenu.Items.Add(item);
        }
    }

    private async void PreviewRecentLevel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RecentFileSet set })
        {
            return;
        }

        List<string> paths = set.FilePaths.Where(File.Exists).ToList();
        if (paths.Count == 0)
        {
            MessageBox.Show("None of the recent level files exist anymore.");
            return;
        }

        for (int index = 0; index < paths.Count; index++)
        {
            await LoadPreviewLevelAsync(paths[index], replace: index == 0).ConfigureAwait(true);
        }
    }

    private async Task LoadPreviewLevelAsync(string path, bool replace, bool updateSession = true)
    {
        PreviewLevelStatus.Text = $"Loading {Path.GetFileName(path)}...";
        try
        {
            await CameraPreviewControl.LoadLevelAsync(path, replace).ConfigureAwait(true);
            if (updateSession)
            {
                UpdatePreviewLevelSession();
            }
            RecordRecentLevelSet();
            UpdatePreviewLevelStatus();
        }
        catch (Exception exception)
        {
            PreviewLevelStatus.Text = $"Failed to load {Path.GetFileName(path)}.";
            MessageBox.Show($"Unable to open level file:\n{exception.Message}");
        }
    }

    private void UpdatePreviewLevelSession()
    {
        lock (PreviewLevelPathsLock)
        {
            PreviewLevelPaths.Clear();
            PreviewLevelPaths.AddRange(CameraPreviewControl.LevelPaths);
        }
    }

    private void UpdatePreviewLevelStatus()
    {
        int count = CameraPreviewControl.LevelPaths.Count;
        PreviewLevelStatus.Text = $"{count} level backdrop file(s).";
    }

    private static List<RecentFileSet> LoadRecentLevelSets()
    {
        if (!File.Exists(RecentLevelSetsFile))
        {
            return [];
        }

        try
        {
            List<RecentFileSet> sets = JsonConvert.DeserializeObject<List<RecentFileSet>>(
                File.ReadAllText(RecentLevelSetsFile)) ?? [];
            foreach (RecentFileSet set in sets)
            {
                set.FilePaths.RemoveAll(path => !File.Exists(path));
                set.ReadOnlyFilePaths.RemoveAll(path => !File.Exists(path));
            }
            return sets.Where(set => set.FilePaths.Count > 0).ToList();
        }
        catch
        {
            return [];
        }
    }

    private void RecordRecentLevelSet()
    {
        if (CameraPreviewControl.LevelPaths.Count == 0)
        {
            return;
        }

        List<RecentFileSet> sets = LoadRecentLevelSets();
        sets.RemoveAll(set => set.FilePaths.Count > 0
            && set.FilePaths[0].Equals(CameraPreviewControl.LevelPaths[0], StringComparison.OrdinalIgnoreCase));
        sets.Insert(0, new RecentFileSet
        {
            Game = CameraPreviewControl.LevelGame,
            FilePaths = [.. CameraPreviewControl.LevelPaths],
            ReadOnlyFilePaths = []
        });
        if (sets.Count > 10)
        {
            sets.RemoveRange(10, sets.Count - 10);
        }
        File.WriteAllText(RecentLevelSetsFile, JsonConvert.SerializeObject(sets, Formatting.Indented));
    }

    private async Task InitializePreviewActorModelsAsync()
    {
        if (_package is null)
        {
            SetPreviewActorStatus("Open the camera preset dialog from a package to load actor models.");
            return;
        }

        MEGame game = _package.Game;
        string databasePath = AssetDatabaseWindow.GetDBPath(game);
        if (!File.Exists(databasePath))
        {
            SetPreviewActorStatus($"No {game} Asset Database found. Generate one in the Asset Database tool.");
            return;
        }

        try
        {
            SetPreviewActorStatus($"Loading {game} actor models...");
            var database = new AssetDB();
            await AssetDatabaseWindow.LoadDatabase(databasePath, game, database, CancellationToken.None);
            if (database.DatabaseVersion != AssetDatabaseWindow.dbCurrentBuild)
            {
                SetPreviewActorStatus($"The {game} Asset Database is out of date. Regenerate it to select actor models.");
                return;
            }

            List<MeshRecord> meshes = database.Meshes
                .Where(mesh => mesh.IsSkeleton && mesh.Usages.Count > 0)
                .OrderBy(mesh => mesh.MeshName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _previewAssetDatabase = database;
            _previewAssetFiles = database.FileList
                .Select(file => (file.FileName, database.ContentDir[file.DirectoryKey]))
                .ToList();
            PreviewActorComboBox.ItemsSource = meshes;

            string defaultMeshName = GetDefaultPreviewModelName(game);
            foreach (PreviewActorConfiguration actor in _previewActors)
            {
                MeshRecord mesh = meshes.FirstOrDefault(item => string.Equals(item.MeshName,
                    actor.ModelName, StringComparison.OrdinalIgnoreCase))
                    ?? meshes.FirstOrDefault(item => string.Equals(item.MeshName,
                        defaultMeshName, StringComparison.OrdinalIgnoreCase));
                if (mesh is null)
                {
                    continue;
                }
                actor.ModelName = mesh.MeshName;
                TryLoadPreviewActorModel(_previewActors.IndexOf(actor), mesh, out _);
            }
            SynchronizePreviewActorControls();
            SetPreviewActorStatus(meshes.Count == 0
                ? $"The {game} Asset Database contains no skeletal meshes."
                : $"{meshes.Count:N0} skeletal actor models available.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetPreviewActorStatus($"Unable to load actor models: {exception.Message}");
        }
    }

    private void SetPreviewActorStatus(string status)
    {
        if (FindName("PreviewActorStatusTextBlock") is TextBlock statusTextBlock)
        {
            statusTextBlock.Text = status;
        }
    }

    private void PreviewActorModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPreviewActorControls || _selectedPreviewActor is null
            || sender is not ComboBox { SelectedItem: MeshRecord meshRecord })
        {
            return;
        }

        int actorIndex = _previewActors.IndexOf(_selectedPreviewActor);
        if (!TryLoadPreviewActorModel(actorIndex, meshRecord, out string error))
        {
            SetPreviewActorStatus(error);
            return;
        }

        _selectedPreviewActor.ModelName = meshRecord.MeshName;
        SetPreviewActorStatus($"{_selectedPreviewActor.DisplayName}: {meshRecord.MeshName}");
        RefreshLivePreview();
    }

    private bool TryLoadPreviewActorModel(int actorIndex, MeshRecord meshRecord, out string error)
    {
        error = null;
        if (_package is null || _previewAssetDatabase is null)
        {
            error = "The actor model database is not loaded.";
            return false;
        }

        string gamePath = MEDirectories.GetDefaultGamePath(_package.Game);
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
        {
            error = $"The configured {_package.Game} game directory could not be found.";
            return false;
        }

        foreach (MeshUsage usage in meshRecord.Usages)
        {
            if (usage.FileKey < 0 || usage.FileKey >= _previewAssetFiles.Count)
            {
                continue;
            }

            (string fileName, string contentDir) = _previewAssetFiles[usage.FileKey];
            string filePath = Directory.EnumerateFiles(gamePath, $"{fileName}.*", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Contains(contentDir, StringComparison.OrdinalIgnoreCase));
            if (filePath is null)
            {
                continue;
            }

            using IMEPackage meshPackage = MEPackageHandler.OpenMEPackage(filePath);
            if (!meshPackage.IsUExport(usage.UIndex))
            {
                continue;
            }

            ExportEntry meshExport = meshPackage.GetUExport(usage.UIndex);
            if (!string.Equals(meshExport.ClassName, "SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                CameraPreviewControl.LoadActorModel(actorIndex, meshExport);
                return true;
            }
            catch (Exception exception)
            {
                error = $"Unable to render {meshRecord.MeshName}: {exception.Message}";
                return false;
            }
        }

        error = $"No installed package containing {meshRecord.MeshName} could be resolved.";
        return false;
    }

    private static string GetDefaultPreviewModelName(MEGame game) => game switch
    {
        MEGame.LE1 or MEGame.ME1 => "QRN_FAC_ARM_LGTa_MDL",
        MEGame.LE2 or MEGame.ME2 => "QRN_TLI_LGTa_MDL",
        _ => "QRN_ARM_TLIa_MDL"
    };

    private void InitializePreviewActorLayout()
    {
        PreviewActorListBox.ItemsSource = _previewActors;
        string[] actorTags = _actorAnchorContext?.ActorTags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        PreviewActorSingleTagComboBox.ItemsSource = actorTags;
        PreviewActorPrimaryTagComboBox.ItemsSource = actorTags;
        LoadPreviewActorLayout();
    }

    private void LoadPreviewActorLayout()
    {
        try
        {
            if (File.Exists(SavedPreviewActorsPath))
            {
                List<PreviewActorConfiguration> actors = JsonConvert.DeserializeObject<List<PreviewActorConfiguration>>(
                    File.ReadAllText(SavedPreviewActorsPath));
                if (actors is { Count: > 0 })
                {
                    foreach (PreviewActorConfiguration actor in actors)
                    {
                        actor.AnchorMode = Enum.IsDefined(actor.AnchorMode)
                            ? actor.AnchorMode : CameraAnchorMode.ManualOrigin;
                        actor.ModelName ??= GetDefaultPreviewModelName(_package?.Game ?? MEGame.Unknown);
                        _previewActors.Add(actor);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetPreviewActorStatus($"Saved actor layout could not be loaded: {exception.Message}");
        }

        if (_previewActors.Count == 0)
        {
            AddDefaultPreviewActor();
            return;
        }
        RenumberPreviewActors();
        PreviewActorListBox.SelectedIndex = 0;
        UpdatePreviewActorTransforms();
    }

    private void SavePreviewActorLayout()
    {
        try
        {
            File.WriteAllText(SavedPreviewActorsPath,
                JsonConvert.SerializeObject(_previewActors.ToList(), Formatting.Indented));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetPreviewActorStatus($"Actor layout could not be saved: {exception.Message}");
        }
    }

    private void AddDefaultPreviewActor()
    {
        CameraOrigin origin = TryReadOrigin(out CameraOrigin cameraOrigin) ? cameraOrigin : default;
        var actor = new PreviewActorConfiguration
        {
            AnchorMode = CameraAnchorMode.ManualOrigin,
            ModelName = GetDefaultPreviewModelName(_package?.Game ?? MEGame.Unknown),
            Origin = origin
        };
        _previewActors.Add(actor);
        RenumberPreviewActors();
        PreviewActorListBox.SelectedItem = actor;
    }

    private void AddPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        AddDefaultPreviewActor();
        LoadSelectedPreviewActorModel();
        RefreshLivePreview();
    }

    private void RemovePreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreviewActor is null || _previewActors.Count <= 1)
        {
            return;
        }

        int removedIndex = _previewActors.IndexOf(_selectedPreviewActor);
        _previewActors.RemoveAt(removedIndex);
        CameraPreviewControl.RemoveActorModel(removedIndex);
        RenumberPreviewActors();
        PreviewActorListBox.SelectedIndex = Math.Min(removedIndex, _previewActors.Count - 1);
        RefreshLivePreview();
    }

    private void ClearPreviewActors_Click(object sender, RoutedEventArgs e)
    {
        _previewActors.Clear();
        CameraPreviewControl.ClearActorModels();
        AddDefaultPreviewActor();
        LoadSelectedPreviewActorModel();
        SetPreviewActorStatus("Preview actors reset to one Tali actor at the camera anchor.");
        RefreshLivePreview();
    }

    private void RenumberPreviewActors()
    {
        for (int index = 0; index < _previewActors.Count; index++)
        {
            _previewActors[index].DisplayName = $"Actor {index + 1}";
        }
        PreviewActorListBox.Items.Refresh();
        RemovePreviewActorButton.IsEnabled = _previewActors.Count > 1;
        CameraPreviewControl.SetActorTransforms(_previewActors.Select(actor => actor.Origin).ToArray());
    }

    private void PreviewActorListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPreviewActor = PreviewActorListBox.SelectedItem as PreviewActorConfiguration;
        SynchronizePreviewActorControls();
        CameraPreviewControl.SelectActor(PreviewActorListBox.SelectedIndex);
    }

    private void PreviewActorListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        int actorIndex = PreviewActorListBox.SelectedIndex;
        if (actorIndex >= 0)
        {
            CameraPreviewControl.FocusActor(actorIndex);
        }
    }

    private void SynchronizePreviewActorControls()
    {
        if (_selectedPreviewActor is null)
        {
            return;
        }

        _updatingPreviewActorControls = true;
        PreviewActorAnchorModeComboBox.SelectedIndex = (int)_selectedPreviewActor.AnchorMode;
        PreviewActorSingleTagComboBox.Text = _selectedPreviewActor.SingleActorTag ?? string.Empty;
        PreviewActorTagsTextBox.Text = _selectedPreviewActor.ActorTags ?? string.Empty;
        PreviewActorPrimaryTagComboBox.Text = _selectedPreviewActor.PrimaryActorTag ?? string.Empty;
        SetPreviewActorOriginFields(_selectedPreviewActor.Origin);
        if (PreviewActorComboBox.ItemsSource is IEnumerable<MeshRecord> meshes)
        {
            PreviewActorComboBox.SelectedItem = meshes.FirstOrDefault(mesh => string.Equals(mesh.MeshName,
                _selectedPreviewActor.ModelName, StringComparison.OrdinalIgnoreCase));
        }
        UpdatePreviewActorAnchorPanels();
        UpdatePreviewActorRotationDialIndicator();
        _updatingPreviewActorControls = false;
    }

    private void PreviewActorLocationScrubAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axes })
        {
            _previewActorLocationScrubAxes = axes;
        }
    }

    private void PreviewActorLocationScrub_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (_selectedPreviewActor is null)
        {
            e.Handled = true;
            return;
        }
        _previewActorLocationScrubAccumulator = 0;
        _previewActorLocationScrubPreviousHorizontalChange = 0;
    }

    private void PreviewActorLocationScrub_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_selectedPreviewActor is null || !double.IsFinite(e.HorizontalChange))
        {
            return;
        }

        double horizontalChange = e.HorizontalChange - _previewActorLocationScrubPreviousHorizontalChange;
        _previewActorLocationScrubPreviousHorizontalChange = e.HorizontalChange;
        _previewActorLocationScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_previewActorLocationScrubAccumulator / dragStep);
        if (stepCount == 0)
        {
            return;
        }

        _previewActorLocationScrubAccumulator -= stepCount * dragStep;
        float delta = stepCount;
        Vector3 location = _selectedPreviewActor.Origin.Location;
        if (_previewActorLocationScrubAxes is "X" or "All") location.X += delta;
        if (_previewActorLocationScrubAxes is "Y" or "All") location.Y += delta;
        if (_previewActorLocationScrubAxes is "Z" or "All") location.Z += delta;
        SetSelectedPreviewActorOrigin(new CameraOrigin(location, _selectedPreviewActor.Origin.Rotation), true);
    }

    private void PreviewActorLocationScrub_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SavePreviewActorLayout();
    }

    private void PreviewActorRotationDialAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axes })
        {
            _previewActorRotationDialAxes = axes;
            UpdatePreviewActorRotationDialIndicator();
        }
    }

    private void PreviewActorRotationDial_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_selectedPreviewActor is null || PreviewActorRotationDialControl is null)
        {
            return;
        }
        _previewActorRotationDialPreviousAngle = GetPreviewActorRotationDialPointerAngle(
            e.GetPosition(PreviewActorRotationDialControl));
        _previewActorRotationDialAngleAccumulator = 0;
        _previewActorRotationDialDragging = PreviewActorRotationDialControl.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewActorRotationDial_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_previewActorRotationDialDragging || _selectedPreviewActor is null
            || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        double pointerAngle = GetPreviewActorRotationDialPointerAngle(e.GetPosition(PreviewActorRotationDialControl));
        double angleDelta = NormalizePreviewActorRotationDialAngle(pointerAngle - _previewActorRotationDialPreviousAngle);
        _previewActorRotationDialPreviousAngle = pointerAngle;
        _previewActorRotationDialAngleAccumulator += angleDelta;
        const float increment = 5f;
        int stepCount = (int)(_previewActorRotationDialAngleAccumulator / increment);
        if (stepCount == 0)
        {
            return;
        }

        _previewActorRotationDialAngleAccumulator -= stepCount * increment;
        float delta = stepCount * increment;
        Vector3 rotation = _selectedPreviewActor.Origin.Rotation;
        if (_previewActorRotationDialAxes is "Roll" or "All") rotation.X += delta;
        if (_previewActorRotationDialAxes is "Pitch" or "All") rotation.Y += delta;
        if (_previewActorRotationDialAxes is "Yaw" or "All") rotation.Z += delta;
        SetSelectedPreviewActorOrigin(new CameraOrigin(_selectedPreviewActor.Origin.Location, rotation), true);
        e.Handled = true;
    }

    private void PreviewActorRotationDial_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_previewActorRotationDialDragging)
        {
            return;
        }
        _previewActorRotationDialDragging = false;
        PreviewActorRotationDialControl.ReleaseMouseCapture();
        SavePreviewActorLayout();
        e.Handled = true;
    }

    private void PreviewActorRotationDial_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewActorRotationDialDragging = false;
    }

    private void UpdatePreviewActorRotationDialIndicator()
    {
        if (PreviewActorRotationDialIndicatorControl?.RenderTransform is not System.Windows.Media.RotateTransform transform)
        {
            return;
        }
        Vector3 rotation = _selectedPreviewActor?.Origin.Rotation ?? Vector3.Zero;
        transform.Angle = _previewActorRotationDialAxes switch
        {
            "Roll" => rotation.X,
            "Pitch" => rotation.Y,
            "Yaw" => rotation.Z,
            _ => (rotation.X + rotation.Y + rotation.Z) / 3f
        };
    }

    private static double GetPreviewActorRotationDialPointerAngle(Point point)
        => Math.Atan2(point.Y - 45d, point.X - 45d) * 180d / Math.PI + 90d;

    private static double NormalizePreviewActorRotationDialAngle(double angle)
    {
        while (angle > 180d) angle -= 360d;
        while (angle < -180d) angle += 360d;
        return angle;
    }

    private void PreviewActorAnchorMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPreviewActorControls || _selectedPreviewActor is null)
        {
            return;
        }
        _selectedPreviewActor.AnchorMode = GetPreviewActorAnchorMode();
        UpdatePreviewActorAnchorPanels();
        ResolveSelectedPreviewActorAnchor(false);
    }

    private void ApplyPreviewActorOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSelectedPreviewActorAnchor(true))
        {
            _selectedPreviewActor.AnchorMode = CameraAnchorMode.ManualOrigin;
            SynchronizePreviewActorControls();
            SetPreviewActorStatus($"{_selectedPreviewActor.DisplayName} actor origin applied for manual editing.");
        }
    }

    private bool ResolveSelectedPreviewActorAnchor(bool showErrors)
    {
        if (_selectedPreviewActor is null)
        {
            return false;
        }
        if (_selectedPreviewActor.AnchorMode == CameraAnchorMode.ManualOrigin)
        {
            return true;
        }
        if (_actorAnchorContext is null)
        {
            if (showErrors)
            {
                MessageBox.Show("Actor anchor modes require a selected conversation node in the Dialogue Editor.",
                    "Actor Anchor Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        string[] actorTags = _selectedPreviewActor.AnchorMode == CameraAnchorMode.SingleActor
            ? string.IsNullOrWhiteSpace(_selectedPreviewActor.SingleActorTag)
                ? [] : [_selectedPreviewActor.SingleActorTag.Trim()]
            : (_selectedPreviewActor.ActorTags ?? string.Empty)
                .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        int minimumActors = _selectedPreviewActor.AnchorMode == CameraAnchorMode.MultipleActors ? 2 : 1;
        if (actorTags.Length < minimumActors)
        {
            if (showErrors)
            {
                MessageBox.Show(minimumActors == 2 ? "Enter at least two actor tags." : "Select or enter an actor tag.",
                    "Actor Anchor Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        IReadOnlyList<ActorSceneStatePath> paths = CameraActorSceneStateResolver.ResolvePaths(_actorAnchorContext, actorTags);
        ActorSceneStatePath[] completePaths = paths.Where(candidate =>
            actorTags.All(candidate.ActorTransforms.ContainsKey)).ToArray();
        ActorSceneStatePath path = SelectActorSceneStatePath(completePaths, actorTags, showErrors);
        if (path is null)
        {
            if (showErrors && completePaths.Length == 0)
            {
                MessageBox.Show($"No matching actor transforms were found for: {string.Join(", ", actorTags)}.",
                    "Actor Transform Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        ActorAnchorResolution resolution = CameraActorAnchorResolver.Resolve(path, actorTags,
            _selectedPreviewActor.PrimaryActorTag);
        if (resolution is null)
        {
            return false;
        }
        _selectedPreviewActor.Origin = resolution.Origin;
        _updatingPreviewActorControls = true;
        SetPreviewActorOriginFields(resolution.Origin);
        _updatingPreviewActorControls = false;
        UpdatePreviewActorTransforms();
        SetPreviewActorStatus($"{_selectedPreviewActor.DisplayName} resolved from {resolution.Path.PathId}.");
        return true;
    }

    private CameraAnchorMode GetPreviewActorAnchorMode() =>
        PreviewActorAnchorModeComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
        && Enum.TryParse(tag, out CameraAnchorMode mode)
            ? mode
            : CameraAnchorMode.ManualOrigin;

    private void UpdatePreviewActorAnchorPanels()
    {
        CameraAnchorMode mode = GetPreviewActorAnchorMode();
        PreviewActorSingleAnchorPanel.Visibility = mode == CameraAnchorMode.SingleActor
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewActorMultipleAnchorPanel.Visibility = mode == CameraAnchorMode.MultipleActors
            ? Visibility.Visible : Visibility.Collapsed;
        bool manual = mode == CameraAnchorMode.ManualOrigin;
        foreach (TextBox textBox in GetPreviewActorTransformTextBoxes())
        {
            textBox.IsReadOnly = !manual;
        }
    }

    private void PreviewActorAnchorValue_Changed(object sender, EventArgs e)
    {
        if (_updatingPreviewActorControls || _selectedPreviewActor is null)
        {
            return;
        }
        _selectedPreviewActor.SingleActorTag = PreviewActorSingleTagComboBox.Text.Trim();
        _selectedPreviewActor.ActorTags = PreviewActorTagsTextBox.Text;
        _selectedPreviewActor.PrimaryActorTag = PreviewActorPrimaryTagComboBox.Text.Trim();
        ResolveSelectedPreviewActorAnchor(false);
    }

    private void PreviewActorTransform_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingPreviewActorControls || _selectedPreviewActor is null
            || _selectedPreviewActor.AnchorMode != CameraAnchorMode.ManualOrigin
            || !TryReadPreviewActorOrigin(out CameraOrigin origin))
        {
            return;
        }
        _selectedPreviewActor.Origin = origin;
        UpdatePreviewActorTransforms();
        CameraPreviewControl.SetSelectedActorTransform(origin);
    }

    private TextBox[] GetPreviewActorTransformTextBoxes() =>
    [
        PreviewActorXTextBox, PreviewActorYTextBox, PreviewActorZTextBox,
        PreviewActorRollTextBox, PreviewActorPitchTextBox, PreviewActorYawTextBox
    ];

    private bool TryReadPreviewActorOrigin(out CameraOrigin origin)
    {
        origin = default;
        if (!TryReadFloat(PreviewActorXTextBox, out float x)
            || !TryReadFloat(PreviewActorYTextBox, out float y)
            || !TryReadFloat(PreviewActorZTextBox, out float z)
            || !TryReadFloat(PreviewActorRollTextBox, out float roll)
            || !TryReadFloat(PreviewActorPitchTextBox, out float pitch)
            || !TryReadFloat(PreviewActorYawTextBox, out float yaw))
        {
            return false;
        }
        origin = new CameraOrigin(new Vector3(x, y, z), new Vector3(roll, pitch, yaw));
        return true;
    }

    private void SetPreviewActorOriginFields(CameraOrigin origin)
    {
        PreviewActorXTextBox.Text = Format(origin.Location.X);
        PreviewActorYTextBox.Text = Format(origin.Location.Y);
        PreviewActorZTextBox.Text = Format(origin.Location.Z);
        PreviewActorRollTextBox.Text = Format(origin.Rotation.X);
        PreviewActorPitchTextBox.Text = Format(origin.Rotation.Y);
        PreviewActorYawTextBox.Text = Format(origin.Rotation.Z);
    }

    private void SetSelectedPreviewActorOrigin(CameraOrigin origin, bool useManualMode)
    {
        if (_selectedPreviewActor is null)
        {
            return;
        }
        _selectedPreviewActor.Origin = origin;
        if (useManualMode)
        {
            _selectedPreviewActor.AnchorMode = CameraAnchorMode.ManualOrigin;
        }
        SynchronizePreviewActorControls();
        UpdatePreviewActorTransforms();
        CameraPreviewControl.SetSelectedActorTransform(origin);
    }

    private void PreviewActorMoveGizmo_Checked(object sender, RoutedEventArgs e)
    {
        CameraPreviewControl?.SetActorGizmoMode(rotate: false);
    }

    private void PreviewActorRotateGizmo_Checked(object sender, RoutedEventArgs e)
    {
        CameraPreviewControl?.SetActorGizmoMode(rotate: true);
    }

    private void PreviewActorGizmo_TransformChanged(CameraOrigin origin)
    {
        if (_selectedPreviewActor is null)
        {
            return;
        }
        _selectedPreviewActor.Origin = origin;
        _selectedPreviewActor.AnchorMode = CameraAnchorMode.ManualOrigin;
        _updatingPreviewActorControls = true;
        SetPreviewActorOriginFields(origin);
        UpdatePreviewActorRotationDialIndicator();
        _updatingPreviewActorControls = false;
        UpdatePreviewActorTransforms();
    }

    private void PreviewActorViewportSnapRequested(Vector3 location)
    {
        if (_selectedPreviewActor is null)
        {
            return;
        }
        SetSelectedPreviewActorOrigin(new CameraOrigin(location, _selectedPreviewActor.Origin.Rotation), true);
        SavePreviewActorLayout();
    }

    private void UseCameraAnchorForPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (TryResolveGenerationOrigin(true, out CameraOrigin origin))
        {
            SetSelectedPreviewActorOrigin(origin, true);
        }
    }

    private void UseViewportLocationForPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (_getViewportOrigin?.Invoke() is CameraOrigin viewport)
        {
            CameraOrigin current = _selectedPreviewActor?.Origin ?? default;
            SetSelectedPreviewActorOrigin(new CameraOrigin(viewport.Location, current.Rotation), true);
        }
    }

    private void UseViewportTransformForPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (_getViewportOrigin?.Invoke() is CameraOrigin viewport)
        {
            SetSelectedPreviewActorOrigin(viewport, true);
        }
    }

    private void ResetPreviewActor_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreviewActor is null)
        {
            return;
        }
        CameraOrigin origin = TryResolveGenerationOrigin(false, out CameraOrigin resolved) ? resolved : default;
        _selectedPreviewActor.ModelName = GetDefaultPreviewModelName(_package?.Game ?? MEGame.Unknown);
        SetSelectedPreviewActorOrigin(origin, true);
        LoadSelectedPreviewActorModel();
    }

    private void LoadSelectedPreviewActorModel()
    {
        if (_selectedPreviewActor is null || PreviewActorComboBox.ItemsSource is not IEnumerable<MeshRecord> meshes)
        {
            return;
        }
        MeshRecord mesh = meshes.FirstOrDefault(item => string.Equals(item.MeshName,
            _selectedPreviewActor.ModelName, StringComparison.OrdinalIgnoreCase));
        if (mesh is not null)
        {
            TryLoadPreviewActorModel(_previewActors.IndexOf(_selectedPreviewActor), mesh, out _);
        }
    }

    private void UpdatePreviewActorTransforms()
    {
        CameraPreviewControl.SetActorTransforms(_previewActors.Select(actor => actor.Origin).ToArray());
    }

    private void SaveMulticamPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDirectorTrack is null || _interpData is null)
        {
            MessageBox.Show("Select a Director track before saving a multicam preset.", "Save Multicam Preset",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryResolveGenerationOrigin(true, out CameraOrigin origin))
        {
            return;
        }
        if (!MulticamCameraPresetCapture.TryCapture(_selectedDirectorTrack, _interpData, origin,
                "Pending", null, null, out MulticamCameraPreset captured, out string error))
        {
            MessageBox.Show(error, "Unable to Save Multicam Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var saveDialog = new MulticamPresetSaveDialog(captured.Type, SavedMulticamCameraPresetManager.ContainsName)
        {
            Owner = this
        };
        if (saveDialog.ShowDialog() != true)
        {
            return;
        }
        if (!MulticamCameraPresetCapture.TryCapture(_selectedDirectorTrack, _interpData, origin,
                saveDialog.PresetName, saveDialog.Description, saveDialog.PresetType,
                out MulticamCameraPreset preset, out error))
        {
            MessageBox.Show(error, "Unable to Save Multicam Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SavedMulticamCameraPresetManager.Add(preset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(exception.Message, "Unable to Save Multicam Preset", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _selectedMulticamPreset = SavedMulticamCameraPresetManager.Presets.First(item =>
            string.Equals(item.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        MulticamPresetTabs.SelectedIndex = 4;
        RefreshMulticamList();
        CustomMulticamPresetList.SelectedItem = _selectedMulticamPreset;
        CustomMulticamPresetList.ScrollIntoView(_selectedMulticamPreset);
        StatusTextBlock.Text = $"Saved complete Director preset '{preset.Name}'.";
    }

    private void DeleteMulticamPreset_Click(object sender, RoutedEventArgs e)
    {
        if (CustomMulticamPresetList.SelectedItem is not MulticamCameraPreset { IsBuiltIn: false } preset)
        {
            return;
        }
        if (MessageBox.Show($"Delete saved multicam preset '{preset.Name}'?", "Delete Multicam Preset",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SavedMulticamCameraPresetManager.Delete(preset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, "Unable to Delete Multicam Preset", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _selectedMulticamPreset = null;
        RefreshMulticamList();
        SelectFirstMulticamPreset();
        StatusTextBlock.Text = $"Deleted multicam preset '{preset.Name}'.";
    }

    private void ImportMulticamPresets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Multicam Director Presets",
            Filter = "Multicam preset list (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        IReadOnlyList<MulticamCameraPreset> imported = SavedMulticamCameraPresetManager.ReadCollection(dialog.FileName);
        if (imported.Count == 0)
        {
            MessageBox.Show("The file contains no valid multicam Director presets.", "Import Multicam Presets",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string[] duplicates = imported.Where(preset => SavedMulticamCameraPresetManager.ContainsName(preset.Name))
            .Select(preset => preset.Name).ToArray();
        bool replaceDuplicates = false;
        if (duplicates.Length > 0)
        {
            MessageBoxResult result = MessageBox.Show(
                $"The following preset names already exist:\n\n{string.Join("\n", duplicates)}\n\n" +
                "Choose Yes to replace all duplicates, No to skip all duplicates, or Cancel to stop importing.",
                "Duplicate Multicam Presets", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
            {
                return;
            }
            replaceDuplicates = result == MessageBoxResult.Yes;
        }

        try
        {
            (int added, int replaced, int skipped) = SavedMulticamCameraPresetManager.Merge(imported, replaceDuplicates);
            RefreshMulticamList();
            StatusTextBlock.Text = $"Imported {added} multicam preset(s); replaced {replaced}; skipped {skipped}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(exception.Message, "Unable to Import Multicam Presets", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportMulticamPresets_Click(object sender, RoutedEventArgs e)
    {
        if (SavedMulticamCameraPresetManager.Presets.Count == 0)
        {
            MessageBox.Show("There are no saved multicam presets to export.", "Export Multicam Presets",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Multicam Director Presets",
            Filter = "Multicam preset list (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "MulticamDirectorPresets.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SavedMulticamCameraPresetManager.Export(dialog.FileName);
            StatusTextBlock.Text = $"Exported {SavedMulticamCameraPresetManager.Presets.Count} multicam preset(s).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, "Unable to Export Multicam Presets", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AnchorModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ManualOriginPanel is null)
        {
            return;
        }

        CameraAnchorMode mode = GetAnchorMode();
        ManualOriginPanel.Visibility = Visibility.Visible;
        bool isManualOrigin = mode == CameraAnchorMode.ManualOrigin;
        ApplyActorOriginButton.IsEnabled = !isManualOrigin && _actorAnchorContext is not null;
        ManualOriginButtonsPanel.IsEnabled = isManualOrigin;
        foreach (TextBox textBox in new[]
                 {
                     OriginXTextBox, OriginYTextBox, OriginZTextBox,
                     OriginRollTextBox, OriginPitchTextBox, OriginYawTextBox
                 })
        {
            textBox.IsReadOnly = !isManualOrigin;
        }
        SingleActorPanel.Visibility = mode == CameraAnchorMode.SingleActor ? Visibility.Visible : Visibility.Collapsed;
        MultipleActorsPanel.Visibility = mode == CameraAnchorMode.MultipleActors ? Visibility.Visible : Visibility.Collapsed;
        UpdateResolvedOriginDisplay();
        RefreshLivePreview();
    }

    private void ApplyActorOriginButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveGenerationOrigin(true, out CameraOrigin origin))
        {
            return;
        }

        SetResolvedOriginDisplay(origin);
        AnchorModeComboBox.SelectedIndex = 0;
        StatusTextBlock.Text = "Actor anchor copied to Manual Origin.";
    }

    private void AnchorSelection_Changed(object sender, EventArgs e)
    {
        UpdateResolvedOriginDisplay();
        RefreshLivePreview();
    }

    private void AnchorSelection_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        UpdateResolvedOriginDisplay();
        RefreshLivePreview();
    }

    private CameraAnchorMode GetAnchorMode()
    {
        if (AnchorModeComboBox?.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out CameraAnchorMode mode))
        {
            return mode;
        }

        return CameraAnchorMode.ManualOrigin;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (TryReadOrigin(out CameraOrigin origin))
        {
            SaveOrigin(origin);
        }
        if (_selectedPreset is not null)
        {
            SavePreset(_selectedPreset);
        }
        if (TryReadFloat(DistanceScaleTextBox, out float distanceScale)
            && distanceScale >= DistanceScaleSlider.Minimum
            && distanceScale <= DistanceScaleSlider.Maximum)
        {
            SaveDistanceScale(distanceScale);
        }
        SavePreviewActorLayout();

        CameraPreviewControl.SelectedActorTransformChanged -= PreviewActorGizmo_TransformChanged;
        CameraPreviewControl.SelectedActorSnapRequested -= PreviewActorViewportSnapRequested;
        CameraPreviewControl.Dispose();
        base.OnClosed(e);
    }

    public static bool GenerateForTrack(Window owner, ExportEntry export, float initialStartTime = 0,
        Func<CameraOrigin?> getViewportOrigin = null, Action<GeneratedCameraKey> previewCamera = null,
        CameraActorAnchorContext actorAnchorContext = null)
    {
        if (export?.ClassName != "InterpTrackMove")
        {
            return false;
        }

        var track = new InterpTrackMove(export);
        float? maximumEndTime = FindOwningInterpData(export)?.GetProperty<FloatProperty>("InterpLength")?.Value;
        var dialog = new CameraPresetDialog(
            () => track.GetCameraOriginNearestTime(initialStartTime),
            getViewportOrigin,
            previewCamera,
            initialStartTime,
            maximumEndTime,
            export.FileRef,
            actorAnchorContext,
            export)
        {
            Owner = owner
        };

        if (dialog.ShowDialog() != true || dialog.GeneratedKeys is not { Count: > 0 })
        {
            return false;
        }

        track.InsertCameraPresetKeys(dialog.GeneratedStartTime, dialog.GeneratedKeys);
        return true;
    }

    public static bool GenerateForGroup(Window owner, ExportEntry group, float initialStartTime = 0,
        Func<CameraOrigin?> getViewportOrigin = null, Action<GeneratedCameraKey> previewCamera = null,
        CameraActorAnchorContext actorAnchorContext = null)
    {
        if (group?.ClassName != "InterpGroup")
        {
            return false;
        }

        ExportEntry trackMove = group.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks")?
            .Select(reference => group.FileRef.TryGetUExport(reference.Value, out ExportEntry track) ? track : null)
            .FirstOrDefault(track => track?.ClassName == "InterpTrackMove");
        var existingTrack = trackMove is null ? null : new InterpTrackMove(trackMove);
        ExportEntry interpData = FindOwningInterpData(group);
        var dialog = new CameraPresetDialog(
            () => existingTrack?.GetCameraOriginNearestTime(initialStartTime),
            getViewportOrigin,
            previewCamera,
            initialStartTime,
            interpData?.GetProperty<FloatProperty>("InterpLength")?.Value,
            group.FileRef,
            actorAnchorContext,
            trackMove)
        {
            Owner = owner
        };
        dialog.SingleCamTab.Visibility = Visibility.Visible;
        dialog.MulticamTab.Visibility = Visibility.Collapsed;
        dialog.CameraModeTabs.SelectedItem = dialog.SingleCamTab;

        if (dialog.ShowDialog() != true || dialog.GeneratedKeys is not { Count: > 0 })
        {
            return false;
        }

        if (trackMove is null)
        {
            trackMove = MatineeHelper.AddNewTrackToGroup(group, "InterpTrackMove");
            MatineeHelper.AddDefaultPropertiesToTrack(trackMove);
        }
        new InterpTrackMove(trackMove).InsertCameraPresetKeys(dialog.GeneratedStartTime, dialog.GeneratedKeys);
        return true;
    }

    public static bool GenerateForDirector(Window owner, ExportEntry directorTrack,
        Func<CameraOrigin?> getViewportOrigin = null, Action<GeneratedCameraKey> previewCamera = null,
        CameraActorAnchorContext actorAnchorContext = null)
    {
        if (directorTrack?.ClassName != "InterpTrackDirector")
        {
            return false;
        }

        ExportEntry interpData = FindOwningInterpData(directorTrack);
        if (interpData is null)
        {
            return false;
        }

        var dialog = new CameraPresetDialog(
            () => GetDirectorOrigin(directorTrack, interpData), getViewportOrigin, previewCamera,
            maximumEndTime: interpData.GetProperty<FloatProperty>("InterpLength")?.Value,
            package: directorTrack.FileRef, actorAnchorContext: actorAnchorContext,
            selectedDirectorTrack: directorTrack, interpData: interpData)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true;
    }

    public static bool GenerateForInterpData(Window owner, ExportEntry interpData,
        Func<CameraOrigin?> getViewportOrigin = null, Action<GeneratedCameraKey> previewCamera = null,
        CameraActorAnchorContext actorAnchorContext = null)
    {
        if (interpData?.ClassName != "InterpData")
        {
            return false;
        }

        ExportEntry directorTrack = FindDirectorTrack(interpData);
        if (directorTrack is not null)
        {
            return GenerateForDirector(owner, directorTrack, getViewportOrigin, previewCamera, actorAnchorContext);
        }

        CameraPresetDialog dialog = null;
        dialog = new CameraPresetDialog(
            () => dialog?._selectedDirectorTrack is null
                ? null
                : GetDirectorOrigin(dialog._selectedDirectorTrack, interpData),
            getViewportOrigin, previewCamera,
            maximumEndTime: interpData.GetProperty<FloatProperty>("InterpLength")?.Value,
            package: interpData.FileRef, actorAnchorContext: actorAnchorContext, interpData: interpData)
        {
            Owner = owner
        };
        dialog.SingleCamTab.Visibility = Visibility.Collapsed;
        dialog.MulticamTab.Visibility = Visibility.Visible;
        dialog.CameraModeTabs.SelectedIndex = 1;
        dialog.RefreshMulticamList();
        dialog.SelectFirstMulticamPreset();
        return dialog.ShowDialog() == true;
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPresetSelection || sender is not ListBox { SelectedItem: CameraPreset preset })
        {
            return;
        }

        _updatingPresetSelection = true;
        foreach (ListBox list in new[] { StaticPresetList, DynamicPresetList, ReactionPresetList, SavedPresetList })
        {
            if (list != sender)
            {
                list.SelectedItem = null;
            }
        }
        PresetSearchResultsList.SelectedItem = null;
        _updatingPresetSelection = false;

        SelectPreset(preset);
    }

    private void PresetSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PresetTabs is null || PresetSearchResultsList is null || CameraModeTabs is null)
        {
            return;
        }

        if (CameraModeTabs.SelectedIndex == 1)
        {
            RefreshMulticamList();
            return;
        }

        string search = PresetSearchTextBox.Text.Trim();
        bool isSearching = search.Length > 0;
        PresetTabs.Visibility = isSearching ? Visibility.Collapsed : Visibility.Visible;
        PresetSearchResultsList.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        if (!isSearching)
        {
            PresetSearchResultsList.ItemsSource = null;
            return;
        }

        PresetSearchResultsList.ItemsSource = GetAllPresets()
            .Where(preset => preset.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || GetCategoryDisplay(preset.Category).Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(preset => new PresetSearchResult(preset, preset.Name, GetCategoryDisplay(preset.Category)))
            .ToList();
    }

    private void CameraModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != CameraModeTabs || SingleCamParametersPanel is null)
        {
            return;
        }

        bool isMulticam = CameraModeTabs.SelectedIndex == 1;
        SingleCamParametersPanel.Visibility = isMulticam ? Visibility.Collapsed : Visibility.Visible;
        MulticamDetailsPanel.Visibility = isMulticam ? Visibility.Visible : Visibility.Collapsed;
        PresetDetailsGroup.Header = isMulticam ? "Multicam Director Sequence" : "Local Composition and Movement";
        GenerateButton.Content = isMulticam ? "Apply Multicam" : "Generate";
        RefreshPresetSearchForCurrentMode();
        if (isMulticam && _selectedMulticamPreset is null)
        {
            SelectFirstMulticamPreset();
        }
        RefreshLivePreview();
    }

    private void MulticamPresetTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == MulticamPresetTabs)
        {
            RefreshMulticamList();
            SelectFirstMulticamPreset();
        }
    }

    private void RefreshMulticamList()
    {
        if (StaticToStaticPresetList is null || CustomMulticamPresetList is null)
        {
            return;
        }

        string search = PresetSearchTextBox?.Text.Trim() ?? string.Empty;
        MulticamCameraPreset selected = _selectedMulticamPreset;
        SetMulticamList(StaticToStaticPresetList, MulticamCameraPresetCatalog.GetByType(MulticamPresetType.StaticToStatic));
        SetMulticamList(StaticToDynamicPresetList, MulticamCameraPresetCatalog.GetByType(MulticamPresetType.StaticToDynamic));
        SetMulticamList(DynamicToStaticPresetList, MulticamCameraPresetCatalog.GetByType(MulticamPresetType.DynamicToStatic));
        SetMulticamList(DynamicToDynamicPresetList, MulticamCameraPresetCatalog.GetByType(MulticamPresetType.DynamicToDynamic));
        SetMulticamList(CustomMulticamPresetList, SavedMulticamCameraPresetManager.Presets);

        void SetMulticamList(ListBox list, IEnumerable<MulticamCameraPreset> source)
        {
            List<MulticamCameraPreset> filtered = source
                .Where(preset => string.IsNullOrEmpty(search) || MulticamMatchesSearch(preset, search))
                .ToList();
            list.ItemsSource = filtered;
            list.SelectedItem = selected is null ? null : filtered.FirstOrDefault(preset =>
                string.Equals(preset.Name, selected.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private ListBox GetActiveMulticamPresetList() => MulticamPresetTabs?.SelectedIndex switch
    {
        0 => StaticToStaticPresetList,
        1 => StaticToDynamicPresetList,
        2 => DynamicToStaticPresetList,
        3 => DynamicToDynamicPresetList,
        4 => CustomMulticamPresetList,
        _ => null
    };

    private void SelectFirstMulticamPreset()
    {
        ListBox list = GetActiveMulticamPresetList();
        if (list?.Items.Count > 0 && list.SelectedItem is null)
        {
            list.SelectedIndex = 0;
        }
    }

    private static bool MulticamMatchesSearch(MulticamCameraPreset preset, string search)
    {
        IEnumerable<string> values = new[] { preset.Name, preset.Description, preset.TypeDisplay }
            .Concat(preset.CameraGroups.SelectMany(group => new[] { group.GroupName, group.FindActorName, group.MovementName }))
            .Concat(preset.SearchableMetadata ?? []);
        return values.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void MulticamPresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: MulticamCameraPreset preset })
        {
            return;
        }

        foreach (ListBox list in new[] { StaticToStaticPresetList, StaticToDynamicPresetList, DynamicToStaticPresetList,
                     DynamicToDynamicPresetList, CustomMulticamPresetList })
        {
            if (list != sender)
            {
                list.SelectedItem = null;
            }
        }

        _selectedMulticamPreset = preset;
        DeleteMulticamPresetButton.IsEnabled = !preset.IsBuiltIn
            && SavedMulticamCameraPresetManager.Presets.Contains(preset);
        MulticamNameTextBlock.Text = preset.Name;
        MulticamTypeTextBlock.Text = preset.TypeDisplay;
        MulticamDescriptionTextBlock.Text = string.IsNullOrWhiteSpace(preset.Description)
            ? "No description."
            : preset.Description;
        MulticamCutsItemsControl.ItemsSource = preset.DirectorKeys
            .OrderBy(key => key.TimeOffset)
            .Select(key => $"{key.TimeOffset:0.###}s  →  {key.GroupName}")
            .ToArray();
        MulticamGroupsItemsControl.ItemsSource = preset.CameraGroups
            .Select(group => $"{group.GroupName} ({(group.IsStatic ? "Static" : "Dynamic")}) — {group.MovementName}")
            .ToArray();
        RefreshLivePreview();
    }

    private void RefreshPresetSearchForCurrentMode()
    {
        if (CameraModeTabs?.SelectedIndex == 1)
        {
            PresetTabs.Visibility = Visibility.Visible;
            PresetSearchResultsList.Visibility = Visibility.Collapsed;
            RefreshMulticamList();
        }
        else
        {
            PresetSearchTextBox_TextChanged(PresetSearchTextBox, null);
        }
    }

    private void PresetSearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPresetSelection || PresetSearchResultsList.SelectedItem is not PresetSearchResult result)
        {
            return;
        }

        _updatingPresetSelection = true;
        foreach (ListBox list in new[] { StaticPresetList, DynamicPresetList, ReactionPresetList, SavedPresetList })
        {
            list.SelectedItem = null;
        }
        _updatingPresetSelection = false;
        SelectPreset(result.Preset);
    }

    private void SelectPreset(CameraPreset preset)
    {
        _selectedPreset = preset;
        SetPresetFields(preset);
        RefreshLivePreview();
    }

    private void SaveTrackMovePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTrackMove is null)
        {
            MessageBox.Show("No TrackMove is selected.", "Save Camera Preset",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryResolveGenerationOrigin(true, out CameraOrigin origin))
        {
            return;
        }

        string name = PromptDialog.Prompt(this, "Preset name:", "Save TrackMove Camera Preset",
            validator: value =>
            {
                string trimmed = value?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    return (false, "Enter a preset name.");
                }
                return SavedCameraPresetManager.ContainsName(trimmed)
                    ? (false, $"A preset named '{trimmed}' already exists.")
                    : (true, null);
            });
        if (name is null)
        {
            return;
        }

        if (!CameraPresetTrackCapture.TryCapture(_selectedTrackMove, origin, name,
                out CameraPreset preset, out string error))
        {
            MessageBox.Show(error, "Unable to Save Camera Preset",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            SavedCameraPresetManager.Add(preset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(exception.Message, "Unable to Save Camera Preset",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        PresetTabs.SelectedIndex = 3;
        SavedPresetList.SelectedItem = preset;
        SavedPresetList.ScrollIntoView(preset);
        RefreshPresetSearch();
        StatusTextBlock.Text = $"Saved TrackMove preset '{preset.Name}' relative to the current origin.";
    }

    private void DeleteSavedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (SavedPresetList.SelectedItem is not CameraPreset preset || !preset.IsSavedTrackMove)
        {
            return;
        }
        if (MessageBox.Show($"Delete saved camera preset '{preset.Name}'?", "Delete Camera Preset",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SavedCameraPresetManager.Delete(preset);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, "Unable to Delete Camera Preset",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DeleteSavedPresetButton.IsEnabled = false;
        RefreshPresetSearch();
        if (SavedCameraPresetManager.Presets.FirstOrDefault() is { } nextPreset)
        {
            SavedPresetList.SelectedItem = nextPreset;
        }
        else
        {
            PresetTabs.SelectedIndex = 0;
            StaticPresetList.SelectedIndex = 0;
        }
        StatusTextBlock.Text = $"Deleted saved preset '{preset.Name}'.";
    }

    private void ImportSavedPresets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Camera TrackMove Presets",
            Filter = "Camera preset list (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        IReadOnlyList<CameraPreset> imported = SavedCameraPresetManager.ReadCollection(dialog.FileName);
        if (imported.Count == 0)
        {
            MessageBox.Show("The file contains no valid saved TrackMove camera presets.", "Import Camera Presets",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool replaceDuplicates = false;
        string[] duplicateNames = imported.Where(preset => SavedCameraPresetManager.ContainsName(preset.Name))
            .Select(preset => preset.Name).ToArray();
        if (duplicateNames.Length > 0)
        {
            MessageBoxResult result = MessageBox.Show(
                $"The following preset names already exist:\n\n{string.Join("\n", duplicateNames)}\n\n" +
                "Choose Yes to replace all duplicates, No to skip all duplicates, or Cancel to stop importing.",
                "Duplicate Camera Presets", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
            {
                return;
            }
            replaceDuplicates = result == MessageBoxResult.Yes;
        }

        try
        {
            (int added, int replaced, int skipped) = SavedCameraPresetManager.Merge(imported, replaceDuplicates);
            RefreshPresetSearch();
            StatusTextBlock.Text = $"Imported {added} preset(s); replaced {replaced}; skipped {skipped}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(exception.Message, "Unable to Import Camera Presets",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportSavedPresets_Click(object sender, RoutedEventArgs e)
    {
        if (SavedCameraPresetManager.Presets.Count == 0)
        {
            MessageBox.Show("There are no saved TrackMove camera presets to export.", "Export Camera Presets",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Camera TrackMove Presets",
            Filter = "Camera preset list (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "CameraTrackMovePresets.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SavedCameraPresetManager.Export(dialog.FileName);
            StatusTextBlock.Text = $"Exported {SavedCameraPresetManager.Presets.Count} saved camera preset(s).";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, "Unable to Export Camera Presets",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshPresetSearch()
    {
        RefreshPresetSearchForCurrentMode();
    }

    private void SelectSavedPreset()
    {
        CameraPreset preset = LoadSavedPreset() ?? CameraPresetCatalog.All[0];
        (ListBox list, int tabIndex) = preset.Category switch
        {
            CameraPresetCategory.DynamicShots => (DynamicPresetList, 1),
            CameraPresetCategory.ReactionShots => (ReactionPresetList, 2),
            CameraPresetCategory.SavedTrackMoves => (SavedPresetList, 3),
            _ => (StaticPresetList, 0)
        };
        PresetTabs.SelectedIndex = tabIndex;
        list.SelectedItem = preset;
        list.Dispatcher.BeginInvoke(() => list.ScrollIntoView(preset),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string GetCategoryDisplay(CameraPresetCategory category) => category switch
    {
        CameraPresetCategory.StaticShots => "Static Shot",
        CameraPresetCategory.DynamicShots => "Dynamic Shot",
        CameraPresetCategory.ReactionShots => "Reaction Shot",
        CameraPresetCategory.SavedTrackMoves => "Saved TrackMove",
        _ => category.ToString()
    };

    private static IEnumerable<CameraPreset> GetAllPresets() =>
        CameraPresetCatalog.All.Concat(SavedCameraPresetManager.Presets);

    private void SetPresetFields(CameraPreset preset)
    {
        ForwardDistanceTextBox.Text = Format(preset.ForwardDistance);
        SideOffsetTextBox.Text = Format(preset.SideOffset);
        HeightOffsetTextBox.Text = Format(preset.HeightOffset);
        LookAtHeightTextBox.Text = Format(preset.LookAtHeight);
        LocalRollTextBox.Text = Format(preset.LocalRoll);
        LocalPitchTextBox.Text = Format(preset.LocalPitch);
        LocalYawTextBox.Text = Format(preset.LocalYaw);
        DurationTextBox.Text = Format(preset.Duration);
        KeyCountTextBox.Text = CameraPresetGenerator.GetKeyCount(preset).ToString(CultureInfo.InvariantCulture);
        MovementAmountTextBox.Text = Format(preset.MovementAmount);
        bool isSavedTrackMove = preset.IsSavedTrackMove;
        foreach (TextBox textBox in new[]
                 {
                     ForwardDistanceTextBox, SideOffsetTextBox, HeightOffsetTextBox, LookAtHeightTextBox,
                     LocalRollTextBox, LocalPitchTextBox, LocalYawTextBox, MovementAmountTextBox
                 })
        {
            textBox.IsEnabled = !isSavedTrackMove;
        }
        bool isMoving = preset.Category == CameraPresetCategory.DynamicShots || isSavedTrackMove;
        CameraSpeedSlider.IsEnabled = isMoving;
        CameraSpeedTextBox.IsEnabled = isMoving;
        DeleteSavedPresetButton.IsEnabled = isSavedTrackMove;
        SetCameraSpeed(1);
    }

    private void TogglePreviewButton_Checked(object sender, RoutedEventArgs e)
    {
        PreviewColumn.Width = WindowState == WindowState.Maximized
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(520);
        PreviewPanel.Visibility = Visibility.Visible;
        Width = Math.Max(Width, 1360);
        RefreshLivePreview();
    }

    private void CameraPresetDialog_StateChanged(object sender, EventArgs e)
    {
        if (TogglePreviewButton is not { IsChecked: true })
        {
            return;
        }
        PreviewColumn.Width = WindowState == WindowState.Maximized
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(520);
    }

    private void TogglePreviewButton_Unchecked(object sender, RoutedEventArgs e)
    {
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(0);
        CameraPreviewControl.Visibility = Visibility.Collapsed;
        Width = 820;
    }

    private void PreviewParameter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingResolvedOrigin)
        {
            RefreshLivePreview();
        }
    }

    private void RefreshLivePreview()
    {
        if (TogglePreviewButton is { IsChecked: true }
            && CameraModeTabs?.SelectedIndex == 1
            && _selectedMulticamPreset is not null
            && TryResolveGenerationOrigin(false, out CameraOrigin multicamOrigin))
        {
            CameraPreviewControl.Visibility = Visibility.Visible;
            CameraPreviewControl.SetMulticamPreview(_selectedMulticamPreset, multicamOrigin,
                BuildMulticamPreview(_selectedMulticamPreset, multicamOrigin), _previewCamera);
            return;
        }
        if (TogglePreviewButton is not { IsChecked: true }
            || CameraPreviewControl is null
            || _selectedPreset is null
            || !TryResolveGenerationOrigin(false, out CameraOrigin origin)
            || !TryCreateConfiguredPreset(out CameraPreset configured, out int sampleCount, out float pathFraction,
                out float distanceScale))
        {
            return;
        }

        CameraPreviewControl.Visibility = Visibility.Visible;
        CameraPreviewControl.SetPreview(_selectedPreset, origin,
            CameraPresetGenerator.Generate(configured, origin, sampleCount, pathFraction, distanceScale));
    }

    private bool TryCreateConfiguredPreset(out CameraPreset configured, out int sampleCount, out float pathFraction,
        out float distanceScale)
    {
        configured = null;
        sampleCount = 0;
        pathFraction = 1;
        distanceScale = 1;
        if (!TryReadFloat(ForwardDistanceTextBox, out float distance)
            || !TryReadFloat(DistanceScaleTextBox, out float distanceScalePercent)
            || !TryReadFloat(SideOffsetTextBox, out float side)
            || !TryReadFloat(HeightOffsetTextBox, out float height)
            || !TryReadFloat(LookAtHeightTextBox, out float lookHeight)
            || !TryReadFloat(LocalRollTextBox, out float roll)
            || !TryReadFloat(LocalPitchTextBox, out float pitch)
            || !TryReadFloat(LocalYawTextBox, out float yaw)
            || !TryReadFloat(DurationTextBox, out float duration)
            || !TryReadFloat(CameraSpeedTextBox, out float cameraSpeed)
            || !TryReadFloat(MovementAmountTextBox, out float movement)
            || duration < 0 || cameraSpeed <= 0
            || distanceScalePercent < DistanceScaleSlider.Minimum
            || distanceScalePercent > DistanceScaleSlider.Maximum)
        {
            return false;
        }

        var samplingPreset = _selectedPreset with
        {
            ForwardDistance = distance,
            SideOffset = side,
            HeightOffset = height,
            LookAtHeight = lookHeight,
            LocalYaw = yaw,
            LocalPitch = pitch,
            LocalRoll = roll,
            Duration = duration,
            MovementAmount = movement
        };
        sampleCount = CameraPresetGenerator.GetKeyCount(samplingPreset);
        float generatedDuration = IsMovingPreset(samplingPreset)
            ? duration / cameraSpeed
            : duration;
        configured = samplingPreset with { Duration = generatedDuration };
        distanceScale = distanceScalePercent / 100f;
        return true;
    }

    private void DistanceScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingDistanceScale || DistanceScaleTextBox is null)
        {
            return;
        }

        _updatingDistanceScale = true;
        DistanceScaleTextBox.Text = e.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
        DistanceScaleTextBox.CaretIndex = DistanceScaleTextBox.Text.Length;
        _updatingDistanceScale = false;
        RefreshLivePreview();
    }

    private void DistanceScaleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingDistanceScale || DistanceScaleSlider is null
            || !double.TryParse(DistanceScaleTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale)
            || scale < DistanceScaleSlider.Minimum || scale > DistanceScaleSlider.Maximum)
        {
            return;
        }

        _updatingDistanceScale = true;
        DistanceScaleSlider.Value = scale;
        _updatingDistanceScale = false;
        RefreshLivePreview();
    }

    private void CameraSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingCameraSpeed || CameraSpeedTextBox is null)
        {
            return;
        }

        _updatingCameraSpeed = true;
        CameraSpeedTextBox.Text = e.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
        CameraSpeedTextBox.CaretIndex = CameraSpeedTextBox.Text.Length;
        _updatingCameraSpeed = false;
        RefreshLivePreview();
    }

    private void CameraSpeedTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingCameraSpeed || CameraSpeedSlider is null
            || !double.TryParse(CameraSpeedTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)
            || speed < CameraSpeedSlider.Minimum || speed > CameraSpeedSlider.Maximum)
        {
            return;
        }

        _updatingCameraSpeed = true;
        CameraSpeedSlider.Value = speed;
        _updatingCameraSpeed = false;
        RefreshLivePreview();
    }

    private void SetCameraSpeed(double speed)
    {
        _updatingCameraSpeed = true;
        CameraSpeedSlider.Value = speed;
        CameraSpeedTextBox.Text = speed.ToString("0.##", CultureInfo.InvariantCulture);
        _updatingCameraSpeed = false;
    }

    private void EnterManually_Click(object sender, RoutedEventArgs e)
    {
        OriginXTextBox.Focus();
        OriginXTextBox.SelectAll();
    }

    private void UseTrackKey_Click(object sender, RoutedEventArgs e)
    {
        if (_getTrackKeyOrigin?.Invoke() is { } origin)
        {
            SetOrigin(origin);
        }
    }

    private void UseOtherTrackKey_Click(object sender, RoutedEventArgs e)
    {
        if (_package is null)
        {
            return;
        }

        var picker = new TrackMoveOriginPicker(_package)
        {
            Owner = this
        };
        if (picker.ShowDialog() == true)
        {
            SetOrigin(picker.SelectedOrigin);
            StatusTextBlock.Text = "Origin loaded from the selected PCC TrackMove key.";
        }
    }

    private void UseViewportLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_getViewportOrigin?.Invoke() is { } origin)
        {
            CameraOrigin existing = TryReadOrigin(out var current) ? current : new CameraOrigin(Vector3.Zero, Vector3.Zero);
            SetOrigin(new CameraOrigin(origin.Location, existing.Rotation));
        }
    }

    private void UseViewportTransform_Click(object sender, RoutedEventArgs e)
    {
        if (_getViewportOrigin?.Invoke() is { } origin)
        {
            SetOrigin(origin);
        }
    }

    private void UseSelectedPreviewActorLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPreviewActor is null)
        {
            StatusTextBlock.Text = "Select a preview actor before using its location as the camera origin.";
            return;
        }

        SetOrigin(_selectedPreviewActor.Origin);
        StatusTextBlock.Text = $"Camera origin set to {_selectedPreviewActor.DisplayName}'s location and rotation.";
    }

    private void CopyOrigin_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadOrigin(out CameraOrigin origin))
        {
            ShowInvalidOrigin();
            return;
        }

        Clipboard.SetText(string.Join(", ", new[]
        {
            origin.Location.X, origin.Location.Y, origin.Location.Z,
            origin.Rotation.X, origin.Rotation.Y, origin.Rotation.Z
        }.Select(Format)));
        StatusTextBlock.Text = "Origin copied.";
    }

    private void PasteOrigin_Click(object sender, RoutedEventArgs e)
    {
        string text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        string[] values = text.Split(new[] { ',', ';', '\t', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 6 || values.Any(value => !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            MessageBox.Show("Clipboard text must contain six numbers: X, Y, Z, Roll (X), Pitch (Y), Yaw (Z).", "Invalid Origin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var parsed = values.Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        SetOrigin(new CameraOrigin(new Vector3(parsed[0], parsed[1], parsed[2]), new Vector3(parsed[3], parsed[4], parsed[5])));
    }

    private void ResetOrigin_Click(object sender, RoutedEventArgs e) => SetOrigin(new CameraOrigin(Vector3.Zero, Vector3.Zero));

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (CameraModeTabs.SelectedIndex == 1)
        {
            PreviewMulticamPreset();
            return;
        }
        if (!TryGenerate(out IReadOnlyList<GeneratedCameraKey> keys))
        {
            return;
        }

        _previewCamera?.Invoke(keys[keys.Count / 2]);
        StatusTextBlock.Text = $"Previewing {_selectedPreset.Name}.";
    }

    private void PreviewMulticamPreset()
    {
        if (_selectedMulticamPreset is null || !TryResolveGenerationOrigin(true, out CameraOrigin origin))
        {
            return;
        }

        IReadOnlyDictionary<string, IReadOnlyList<GeneratedCameraKey>> cameras = BuildMulticamPreview(_selectedMulticamPreset, origin);
        float previewTime = _selectedMulticamPreset.Duration / 2f;
        MulticamDirectorKey activeCut = _selectedMulticamPreset.DirectorKeys
            .Where(key => key.TimeOffset <= previewTime)
            .OrderBy(key => key.TimeOffset)
            .LastOrDefault();
        if (cameras.TryGetValue(activeCut.GroupName, out IReadOnlyList<GeneratedCameraKey> keys) && keys.Count > 0)
        {
            GeneratedCameraKey activeKey = keys.OrderBy(key => Math.Abs(key.TimeOffset - previewTime)).First();
            _previewCamera?.Invoke(activeKey);
        }
        StatusTextBlock.Text = $"Previewing complete Director sequence '{_selectedMulticamPreset.Name}'.";
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<GeneratedCameraKey>> BuildMulticamPreview(
        MulticamCameraPreset preset, CameraOrigin origin)
    {
        CameraPresetGenerator.BuildBasis(origin.Rotation, out Vector3 forward, out Vector3 right, out Vector3 up);
        return preset.CameraGroups.ToDictionary(group => group.GroupName, group =>
            (IReadOnlyList<GeneratedCameraKey>)group.TrackMoveKeys.Select(key => new GeneratedCameraKey(
                key.TimeOffset,
                origin.Location + forward * key.LocalPosition.X + right * key.LocalPosition.Y + up * key.LocalPosition.Z,
                CameraPresetGenerator.LocalRotationToWorld(key.LocalRotation, origin.Rotation),
                key.Interpolation)).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (CameraModeTabs.SelectedIndex == 1)
        {
            ApplyMulticamPreset();
            return;
        }
        if (!TryGenerate(out IReadOnlyList<GeneratedCameraKey> keys))
        {
            return;
        }

        GeneratedKeys = keys;
        DialogResult = true;
        Close();
    }

    private void ApplyMulticamPreset()
    {
        if (_selectedMulticamPreset is null)
        {
            MessageBox.Show("Select a multicam preset.", "No Preset Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_interpData is null)
        {
            MessageBox.Show("Select an InterpData as the multicam destination.", "No InterpData Selected",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryResolveGenerationOrigin(true, out CameraOrigin origin))
        {
            return;
        }

        if (_selectedDirectorTrack is null)
        {
            ExportEntry directorGroup = MatineeHelper.AddNewGroupDirectorToInterpData(_interpData);
            _selectedDirectorTrack = directorGroup?.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks")?
                .Select(reference => directorGroup.FileRef.TryGetUExport(reference.Value, out ExportEntry track) ? track : null)
                .FirstOrDefault(track => track?.ClassName == "InterpTrackDirector");
            if (_selectedDirectorTrack is null)
            {
                MessageBox.Show("The Director group was created, but its Director track could not be resolved.",
                    "Unable to Create Director", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        float destinationDuration = _maximumEndTime ?? _selectedMulticamPreset.Duration;
        if (!MulticamCameraPresetApplicator.TryApply(_selectedMulticamPreset, _selectedDirectorTrack,
                _interpData, origin, destinationDuration, out string error))
        {
            MessageBox.Show(error, "Unable to Apply Multicam Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private bool TryGenerate(out IReadOnlyList<GeneratedCameraKey> keys)
    {
        keys = null;
        if (_selectedPreset is null)
        {
            MessageBox.Show("Select a camera preset.", "No Preset Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!TryResolveGenerationOrigin(true, out CameraOrigin origin))
        {
            return false;
        }

        if (!TryReadFloat(ForwardDistanceTextBox, out float distance)
            || !TryReadFloat(DistanceScaleTextBox, out float distanceScalePercent)
            || !TryReadFloat(SideOffsetTextBox, out float side)
            || !TryReadFloat(HeightOffsetTextBox, out float height)
            || !TryReadFloat(LookAtHeightTextBox, out float lookHeight)
            || !TryReadFloat(LocalRollTextBox, out float roll)
            || !TryReadFloat(LocalPitchTextBox, out float pitch)
            || !TryReadFloat(LocalYawTextBox, out float yaw)
            || !TryReadFloat(DurationTextBox, out float duration)
            || !TryReadFloat(CameraSpeedTextBox, out float cameraSpeed)
            || !TryReadFloat(MovementAmountTextBox, out float movement)
            || !TryReadFloat(StartTimeTextBox, out float startTime)
            || duration < 0
            || distanceScalePercent < DistanceScaleSlider.Minimum
            || distanceScalePercent > DistanceScaleSlider.Maximum
            || cameraSpeed < CameraSpeedSlider.Minimum
            || cameraSpeed > CameraSpeedSlider.Maximum)
        {
            MessageBox.Show($"All composition fields must contain valid numbers. Distance scale must be between {DistanceScaleSlider.Minimum:0.##}% and {DistanceScaleSlider.Maximum:0.##}%, duration cannot be negative, and movement speed must be between {CameraSpeedSlider.Minimum:0.##}x and {CameraSpeedSlider.Maximum:0.##}x.",
                "Invalid Preset Parameters", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var samplingPreset = _selectedPreset with
        {
            ForwardDistance = distance,
            SideOffset = side,
            HeightOffset = height,
            LookAtHeight = lookHeight,
            LocalYaw = yaw,
            LocalPitch = pitch,
            LocalRoll = roll,
            Duration = duration,
            MovementAmount = movement
        };
        int sampleCount = CameraPresetGenerator.GetKeyCount(samplingPreset);
        float distanceScale = distanceScalePercent / 100f;
        float pathFraction = 1f;
        float generatedDuration = duration;
        float movementRate = 0;
        if (IsMovingPreset(samplingPreset))
        {
            float pathLength = CameraPresetGenerator.GetPathLength(samplingPreset, origin, distanceScale);
            if (duration <= float.Epsilon && pathLength > float.Epsilon)
            {
                MessageBox.Show("Dynamic camera movement duration must be greater than zero.",
                    "Invalid Preset Parameters", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            movementRate = pathLength <= float.Epsilon ? 0 : pathLength / duration * cameraSpeed;
            generatedDuration = movementRate <= float.Epsilon ? duration : pathLength / movementRate;
        }

        if (IsMovingPreset(samplingPreset) && _maximumEndTime is float maximumEndTime)
        {
            float remainingDuration = maximumEndTime - startTime;
            if (remainingDuration <= 0)
            {
                MessageBox.Show($"Dynamic shots must start before the InterpData length of {maximumEndTime:0.###} seconds.",
                    "No Timeline Time Available", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (generatedDuration > remainingDuration)
            {
                pathFraction = generatedDuration > float.Epsilon ? remainingDuration / generatedDuration : 1f;
                generatedDuration = remainingDuration;
                StatusTextBlock.Text = $"Timeline fits {pathFraction:P0} of the path at {movementRate:0.##} units/second; movement will stop at the InterpData end.";
            }
        }

        var configured = samplingPreset with { Duration = generatedDuration };
        KeyCountTextBox.Text = sampleCount.ToString(CultureInfo.InvariantCulture);
        GeneratedStartTime = startTime;
        keys = CameraPresetGenerator.Generate(configured, origin, sampleCount, pathFraction, distanceScale);
        return true;
    }

    private static bool IsMovingPreset(CameraPreset preset) =>
        preset.Category == CameraPresetCategory.DynamicShots || preset.IsSavedTrackMove;

    private static ExportEntry FindOwningInterpData(ExportEntry export)
    {
        for (ExportEntry current = export?.Parent as ExportEntry; current is not null; current = current.Parent as ExportEntry)
        {
            if (current.ClassName == "InterpData")
            {
                return current;
            }
        }

        return null;
    }

    private static ExportEntry FindDirectorTrack(ExportEntry interpData) =>
        interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups")?
            .Select(reference => interpData.FileRef.TryGetUExport(reference.Value, out ExportEntry group) ? group : null)
            .Where(group => group?.ClassName is "InterpGroupDirector" or "InterpDirector")
            .SelectMany(group => (IEnumerable<ObjectProperty>)group.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks")
                ?? Enumerable.Empty<ObjectProperty>())
            .Select(reference => interpData.FileRef.TryGetUExport(reference.Value, out ExportEntry track) ? track : null)
            .FirstOrDefault(track => track?.ClassName == "InterpTrackDirector");

    private static CameraOrigin? GetDirectorOrigin(ExportEntry directorTrack, ExportEntry interpData)
    {
        StructProperty firstCut = directorTrack.GetProperty<ArrayProperty<StructProperty>>("CutTrack")?
            .OrderBy(cut => cut.GetProp<FloatProperty>("Time")?.Value ?? 0)
            .FirstOrDefault();
        string groupName = firstCut?.GetProp<NameProperty>("TargetCamGroup")?.Value.Instanced;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return null;
        }

        ArrayProperty<ObjectProperty> groupRefs = interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
        ExportEntry group = groupRefs?.Select(reference => interpData.FileRef.TryGetUExport(reference.Value, out ExportEntry item) ? item : null)
            .FirstOrDefault(item => item?.ClassName == "InterpGroup"
                && string.Equals(item.GetProperty<NameProperty>("GroupName")?.Value.Instanced,
                    groupName, StringComparison.OrdinalIgnoreCase));
        ExportEntry trackMove = group?.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks")?
            .Select(reference => group.FileRef.TryGetUExport(reference.Value, out ExportEntry item) ? item : null)
            .FirstOrDefault(item => item?.ClassName == "InterpTrackMove");
        return trackMove is null ? null : new InterpTrackMove(trackMove).GetCameraOriginNearestTime(0);
    }

    private void InitializeActorAnchorControls()
    {
        string[] actorTags = _actorAnchorContext?.ActorTags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        SingleActorComboBox.ItemsSource = actorTags;
        MultipleActorListBox.ItemsSource = actorTags;
        PrimaryActorComboBox.ItemsSource = actorTags;

        bool actorAnchorsAvailable = _actorAnchorContext is not null;
        foreach (ComboBoxItem item in AnchorModeComboBox.Items)
        {
            if (item.Tag is string tag && tag != nameof(CameraAnchorMode.ManualOrigin))
            {
                item.IsEnabled = actorAnchorsAvailable;
            }
        }
        AnchorModeAvailabilityText.Text = actorAnchorsAvailable
            ? "Actor transforms resolve from the selected conversation node."
            : "Actor modes require a selected node in the Dialogue Editor.";
    }

    private bool TryResolveGenerationOrigin(bool showErrors, out CameraOrigin origin)
    {
        origin = default;
        CameraAnchorMode mode = GetAnchorMode();
        if (mode == CameraAnchorMode.ManualOrigin)
        {
            if (TryReadOrigin(out origin))
            {
                return true;
            }

            if (showErrors)
            {
                ShowInvalidOrigin();
            }
            return false;
        }

        if (_actorAnchorContext is null)
        {
            if (showErrors)
            {
                MessageBox.Show("Actor anchor modes require a selected conversation node in the Dialogue Editor.",
                    "Actor Anchor Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        string[] actorTags = GetSelectedActorTags(mode);
        int minimumActors = mode == CameraAnchorMode.MultipleActors ? 2 : 1;
        if (actorTags.Length < minimumActors)
        {
            if (showErrors)
            {
                MessageBox.Show(mode == CameraAnchorMode.MultipleActors
                        ? "Select or enter at least two actor tags."
                        : "Select or enter an actor tag.",
                    "Actor Anchor Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        IReadOnlyList<ActorSceneStatePath> paths = CameraActorSceneStateResolver.ResolvePaths(_actorAnchorContext, actorTags);
        ActorSceneStatePath[] completePaths = paths.Where(candidate =>
            actorTags.All(candidate.ActorTransforms.ContainsKey)).ToArray();
        ActorSceneStatePath path = SelectActorSceneStatePath(completePaths, actorTags, showErrors);
        if (path is null)
        {
            if (showErrors && completePaths.Length == 0)
            {
                string unresolved = string.Join(", ", actorTags.Where(tag =>
                    paths.All(pathCandidate => !pathCandidate.ActorTransforms.ContainsKey(tag))));
                MessageBox.Show($"No matching TrackMove or initial actor transform was found for: {unresolved}.",
                    "Actor Transform Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        string primaryActorTag = PrimaryActorComboBox.Text.Trim();
        ActorAnchorResolution resolution = CameraActorAnchorResolver.Resolve(path, actorTags, primaryActorTag);
        if (resolution is null)
        {
            return false;
        }

        origin = resolution.Origin;
        SetResolvedOriginDisplay(origin);
        StatusTextBlock.Text = $"Actor anchor resolved from {resolution.Path.PathId}.";
        return true;
    }

    private void UpdateResolvedOriginDisplay()
    {
        if (GetAnchorMode() != CameraAnchorMode.ManualOrigin
            && TryResolveGenerationOrigin(false, out CameraOrigin origin))
        {
            SetResolvedOriginDisplay(origin);
        }
    }

    private void SetResolvedOriginDisplay(CameraOrigin origin)
    {
        _updatingResolvedOrigin = true;
        SetOrigin(origin);
        _updatingResolvedOrigin = false;
    }

    private ActorSceneStatePath SelectActorSceneStatePath(IReadOnlyList<ActorSceneStatePath> paths,
        IReadOnlyList<string> actorTags, bool showPrompt)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        if (paths.Count == 1 || paths.Skip(1).All(path =>
                CameraActorAnchorResolver.HaveEquivalentTransforms(paths[0], path, actorTags)))
        {
            return paths[0];
        }

        string cacheKey = GetBranchChoiceCacheKey(actorTags);
        if (SessionBranchChoices.TryGetValue(cacheKey, out string selectedPathId)
            && paths.FirstOrDefault(path => path.PathId == selectedPathId) is { } cachedPath)
        {
            return cachedPath;
        }

        if (!showPrompt)
        {
            return null;
        }

        IReadOnlyList<string> differingActors = CameraActorAnchorResolver.GetDifferingActors(paths, actorTags);
        var choices = paths.ToDictionary(FormatPathChoice, path => path, StringComparer.Ordinal);
        string selectedChoice = StringSelectorDialog.GetValue(this,
            $"Incoming conversation paths resolve different transforms for: {string.Join(", ", differingActors)}. " +
            "Choose the executed path to use for this editing session.",
            "Choose Actor Transform Path", choices.Keys);
        if (string.IsNullOrEmpty(selectedChoice) || !choices.TryGetValue(selectedChoice, out ActorSceneStatePath selectedPath))
        {
            return null;
        }

        SessionBranchChoices[cacheKey] = selectedPath.PathId;
        return selectedPath;
    }

    private string GetBranchChoiceCacheKey(IEnumerable<string> actorTags) =>
        $"{_actorAnchorContext.Conversation.Export.FileRef.GetHashCode()}:{_actorAnchorContext.Conversation.UIndex}:" +
        $"{(_actorAnchorContext.SelectedNode.IsReply ? 'R' : 'E')}{_actorAnchorContext.SelectedNode.NodeCount}:" +
        string.Join("|", actorTags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));

    private static string FormatPathChoice(ActorSceneStatePath path) =>
        $"{path.PathId} | " + string.Join("; ", path.ActorTransforms.Values.Select(transform =>
            $"{transform.ActorTag}: ({transform.Location.X:0.##}, {transform.Location.Y:0.##}, {transform.Location.Z:0.##}) from {transform.SourceDescription}"));

    private string[] GetSelectedActorTags(CameraAnchorMode mode)
    {
        if (mode == CameraAnchorMode.SingleActor)
        {
            string actorTag = SingleActorComboBox.Text.Trim();
            return string.IsNullOrEmpty(actorTag) ? [] : [actorTag];
        }

        return MultipleActorListBox.SelectedItems.Cast<string>()
            .Concat(MultipleActorTagsTextBox.Text.Split([',', ';', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryReadOrigin(out CameraOrigin origin)
    {
        origin = default;
        if (!TryReadFloat(OriginXTextBox, out float x)
            || !TryReadFloat(OriginYTextBox, out float y)
            || !TryReadFloat(OriginZTextBox, out float z)
            || !TryReadFloat(OriginRollTextBox, out float roll)
            || !TryReadFloat(OriginPitchTextBox, out float pitch)
            || !TryReadFloat(OriginYawTextBox, out float yaw))
        {
            return false;
        }

        origin = new CameraOrigin(new Vector3(x, y, z), new Vector3(roll, pitch, yaw));
        return true;
    }

    private void SetOrigin(CameraOrigin origin)
    {
        OriginXTextBox.Text = Format(origin.Location.X);
        OriginYTextBox.Text = Format(origin.Location.Y);
        OriginZTextBox.Text = Format(origin.Location.Z);
        OriginRollTextBox.Text = Format(origin.Rotation.X);
        OriginPitchTextBox.Text = Format(origin.Rotation.Y);
        OriginYawTextBox.Text = Format(origin.Rotation.Z);
    }

    private void SetDistanceScale(float distanceScale)
    {
        _updatingDistanceScale = true;
        DistanceScaleSlider.Value = distanceScale;
        DistanceScaleTextBox.Text = Format(distanceScale);
        _updatingDistanceScale = false;
    }

    private static CameraOrigin LoadSavedOrigin()
    {
        try
        {
            if (File.Exists(SavedOriginPath) && TryParseOrigin(File.ReadAllText(SavedOriginPath), out CameraOrigin origin))
            {
                return origin;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new CameraOrigin(Vector3.Zero, Vector3.Zero);
    }

    private static CameraPreset LoadSavedPreset()
    {
        try
        {
            if (File.Exists(SavedPresetPath))
            {
                string[] values = File.ReadAllLines(SavedPresetPath);
                if (values.Length == 2
                    && Enum.TryParse(values[0], out CameraPresetCategory category))
                {
                    return GetAllPresets().FirstOrDefault(preset =>
                        preset.Category == category && preset.Name == values[1]);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static float LoadSavedDistanceScale()
    {
        try
        {
            if (File.Exists(SavedDistanceScalePath)
                && float.TryParse(File.ReadAllText(SavedDistanceScalePath), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float distanceScale)
                && distanceScale is >= 10 and <= 200)
            {
                return distanceScale;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return 100;
    }

    private static void SaveOrigin(CameraOrigin origin)
    {
        try
        {
            File.WriteAllText(SavedOriginPath, string.Join(",", new[]
            {
                origin.Location.X, origin.Location.Y, origin.Location.Z,
                origin.Rotation.X, origin.Rotation.Y, origin.Rotation.Z
            }.Select(Format)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void SavePreset(CameraPreset preset)
    {
        try
        {
            File.WriteAllLines(SavedPresetPath, new[] { preset.Category.ToString(), preset.Name });
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void SaveDistanceScale(float distanceScale)
    {
        try
        {
            File.WriteAllText(SavedDistanceScalePath, Format(distanceScale));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryParseOrigin(string text, out CameraOrigin origin)
    {
        origin = default;
        string[] values = text.Split(new[] { ',', ';', '\t', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 6 || values.Any(value => !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var parsed = values.Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        origin = new CameraOrigin(new Vector3(parsed[0], parsed[1], parsed[2]), new Vector3(parsed[3], parsed[4], parsed[5]));
        return true;
    }

    private static bool TryReadFloat(TextBox textBox, out float value) =>
        float.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void ShowInvalidOrigin() =>
        MessageBox.Show("Enter valid Origin X, Y, Z, Roll (X), Pitch (Y), and Yaw (Z) values before generating or previewing.",
            "Origin Required", MessageBoxButton.OK, MessageBoxImage.Warning);
}
