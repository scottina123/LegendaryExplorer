using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.Win32;
using Path = System.IO.Path;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using LECTexture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;
using Texture2DMipInfo = LegendaryExplorerCore.Unreal.Classes.Texture2DMipInfo;

namespace LegendaryExplorer.Tools.SFXGalaxyEditor;

/// <summary>
/// LE2/LE3 editor for the SFXGalaxy object hierarchy stored in the Normandy galaxy map packages.
/// The viewport is intentionally package-data-driven and does not depend on the legacy galaxy map tool.
/// </summary>
public partial class SFXGalaxyEditorWindow : WPFBase, IRecents
{
    private const int MapExtent = 1024;
    private const string LE2GalaxyMapFile = "BioD_Nor_103aGalaxyMap.pcc";
    private const string LE2GalaxyMapObjectsFile = "BioD_Nor_103bGalaxyMapObjs.pcc";
    private const string LE2GalaxyArtFile = "BioA_Nor_103aGalaxyMap.pcc";
    private const string LE3GalaxyMapFile = "BioD_Nor_203aGalaxyMap.pcc";
    private const string LE3CompanionGalaxyMapFile = "BioD_Nor_203CIC.pcc";
    private const string LE3GalaxyArtFile = "BioA_Nor_203aGalaxyMap.pcc";
    private const string GalaxyTexturePath = "BIOA_GalaxyMap_T.galaxy";
    private const string PlanetMeshPath = "BIOA_GalaxyMap_S.Planet";
    private const string CloudMeshPath = "BIOA_GalaxyMap_S.CloudMask";

    private sealed record GalaxyMapPackageSet(MEGame Game, string GalaxyMapFile, string SecondaryFile,
        string GalaxyArtFile, bool SynchronizesSecondary)
    {
        public string GameLabel => Game == MEGame.LE2 ? "LE2" : "LE3";
    }

    private static readonly GalaxyMapPackageSet LE2PackageSet = new(MEGame.LE2, LE2GalaxyMapFile,
        LE2GalaxyMapObjectsFile, LE2GalaxyArtFile, SynchronizesSecondary: false);
    private static readonly GalaxyMapPackageSet LE3PackageSet = new(MEGame.LE3, LE3GalaxyMapFile,
        LE3CompanionGalaxyMapFile, LE3GalaxyArtFile, SynchronizesSecondary: true);

    private readonly Dictionary<int, SFXGalaxyNode> _nodesByUIndex = [];
    private readonly Dictionary<int, string> _tlkCache = [];
    private readonly Dictionary<SFXGalaxyNode, FrameworkElement> _markerElements = [];
    private readonly Dictionary<SFXGalaxyNode, Point> _visibleCenters = [];
    private readonly Dictionary<string, ImageSource> _backgroundTextureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly PackageCache _texturePackageCache = new();
    private string _queuedFile;
    private int _queuedExportUIndex;
    private string _queuedExportPath;
    private string _queuedExportClass;
    private bool _handledInitialLoad;
    private bool _suppressPackageRefresh;
    private bool _refreshQueued;
    private GalaxyMapPackageSet _packageSet;
    private IMEPackage _companionPcc;
    private IMEPackage _supportPcc;
    private bool _companionNeedsFullSync;
    private readonly HashSet<int> _pendingCompanionSyncUIndexes = [];
    private bool _pendingCompanionFullSync;
    private ImageSource _galaxyBackground;
    private string _galaxyArtPackagePath;
    private IMEPackage _galaxyArtPcc;

    public string AuthoritativePackagePath => Pcc?.FilePath ?? string.Empty;
    public string CompanionPackagePath => _companionPcc?.FilePath ?? _supportPcc?.FilePath ?? string.Empty;
    public string SecondaryPackageLabel => _packageSet?.SynchronizesSecondary == true
        ? "Synced companion:"
        : "Map objects:";
    public string GalaxyArtPackagePath => _galaxyArtPackagePath ?? string.Empty;
    public bool SupportsLE3ObjectTypes => Pcc?.Game == MEGame.LE3;

    private string _companionSyncStatus = "Secondary package not loaded.";
    public string CompanionSyncStatus
    {
        get => _companionSyncStatus;
        private set
        {
            if (SetProperty(ref _companionSyncStatus, value))
            {
                UpdateStatus();
            }
        }
    }

    private SFXGalaxyNode _dragNode;
    private Point _dragStart;
    private Point _dragOrigin;
    private SFXGalaxyNode _relaySource;
    private Line _relayPreview;

    public ObservableCollectionExtended<SFXGalaxyNode> HierarchyRoots { get; } = [];
    public ObservableCollectionExtended<SFXGalaxyNode> SearchResults { get; } = [];
    public ObservableCollectionExtended<SFXGalaxyEditableExport> EditableExports { get; } = [];
    public ObservableCollectionExtended<SFXGalaxyPlanetMaterialSlot> PlanetMaterialSlots { get; } = [];

    private SFXGalaxyNode _rootNode;
    private SFXGalaxyNode _currentNode;
    public SFXGalaxyNode CurrentNode
    {
        get => _currentNode;
        private set
        {
            if (SetProperty(ref _currentNode, value))
            {
                OnPropertyChanged(nameof(CurrentViewLabel));
                OnPropertyChanged(nameof(CurrentObjectCountText));
                UpdateStatus();
                BuildBreadcrumbs();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private SFXGalaxyNode _selectedNode;
    public SFXGalaxyNode SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                RefreshPropertyExports();
                OnPropertyChanged(nameof(CanOpenPlanetMaterialEditor));
                if (IsPlanetMaterialEditorOpen)
                {
                    if (CanOpenPlanetMaterialEditor)
                    {
                        RefreshPlanetMaterialSlots();
                    }
                    else
                    {
                        ClosePlanetMaterialEditor();
                    }
                }
                UpdateStatus();
                RenderCurrentLevel();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanOpenPlanetMaterialEditor => SelectedNode?.Export is { } export
        && export.IsA("BioPlanet")
        && HasPlanetMaterialReference(export);

    private bool _isPlanetMaterialEditorOpen;
    public bool IsPlanetMaterialEditorOpen
    {
        get => _isPlanetMaterialEditorOpen;
        private set
        {
            if (SetProperty(ref _isPlanetMaterialEditorOpen, value))
            {
                OnPropertyChanged(nameof(PlanetMaterialSplitterWidth));
                OnPropertyChanged(nameof(PlanetMaterialEditorWidth));
            }
        }
    }

    public GridLength PlanetMaterialSplitterWidth => IsPlanetMaterialEditorOpen
        ? new GridLength(5)
        : new GridLength(0);

    public GridLength PlanetMaterialEditorWidth => IsPlanetMaterialEditorOpen
        ? new GridLength(780)
        : new GridLength(0);

    private SFXGalaxyPlanetMaterialSlot _selectedPlanetMaterialSlot;
    public SFXGalaxyPlanetMaterialSlot SelectedPlanetMaterialSlot
    {
        get => _selectedPlanetMaterialSlot;
        set
        {
            if (SetProperty(ref _selectedPlanetMaterialSlot, value) && IsPlanetMaterialEditorOpen)
            {
                LoadSelectedPlanetMaterialPreview();
            }
        }
    }

    private bool _showCoordinateGrid;
    public bool ShowCoordinateGrid
    {
        get => _showCoordinateGrid;
        set
        {
            if (SetProperty(ref _showCoordinateGrid, value))
            {
                RenderCurrentLevel();
            }
        }
    }

    private string _statusText = "Choose LE2 or LE3 highest-mounted galaxy map files to begin.";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentViewLabel => CurrentNode?.Kind switch
    {
        SFXGalaxyNodeKind.Galaxy => "GALAXY VIEW",
        SFXGalaxyNodeKind.Cluster => "CLUSTER VIEW",
        SFXGalaxyNodeKind.System => "SYSTEM VIEW",
        SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.Anomaly => "PLANET VIEW",
        _ => "OBJECT VIEW"
    };

    public string CurrentObjectCountText => CurrentNode is null
        ? string.Empty
        : $"{CurrentNode.Children.Count} {ObjectNoun(CurrentNode.Children.Count)}";

    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand NavigateIntoCommand { get; }
    public ICommand FocusSearchCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }

    public string Toolname => "SFXGalaxyEditorLE3";

    public SFXGalaxyEditorWindow() : base("SFXGalaxy Editor")
    {
        OpenCommand = new GenericCommand(OpenPackage);
        SaveCommand = new GenericCommand(SavePackage, CanSavePackageSet);
        SaveAsCommand = new GenericCommand(SavePackageAs, CanSavePackageSet);
        BackCommand = new GenericCommand(NavigateBack, () => CurrentNode?.Parent is not null);
        NavigateIntoCommand = new GenericCommand(NavigateIntoSelected, () => CanEnter(SelectedNode));
        FocusSearchCommand = new GenericCommand(() => SearchBox?.Focus(), () => Pcc is not null);
        DuplicateCommand = new GenericCommand(DuplicateSelected, () => SelectedNode is { Parent: not null, IsImplicitStar: false });
        DeleteCommand = new GenericCommand(DeleteSelected, () => SelectedNode is { Parent: not null, IsImplicitStar: false });

        InitializeComponent();
        PlanetMaterialMeshViewer.RenderGameShader = true;
        PlanetMaterialMeshViewer.SaveLiveMaterialToCurrentOverride = SavePlanetMaterialToCurrent;
        PlanetMaterialMeshViewer.SaveLiveMaterialAsNewOverride = SavePlanetMaterialAsNew;
        PlanetMaterialMeshViewer.RandomizeLiveMaterialScalarsOverride = RandomizePlanetMaterialScalars;
        PlanetMaterialMeshViewer.RandomizeLiveMaterialVectorsOverride = RandomizePlanetMaterialVectors;
        PlanetMaterialMeshViewer.ShowLiveMaterialRandomizationControls = true;
        PlanetMaterialMeshViewer.LiveMaterialSaveCurrentLabel = "Overwrite MIC";
        PlanetMaterialMeshViewer.LiveMaterialSaveAsNewLabel = "Create new MIC...";
        PlanetMaterialMeshViewer.LiveMaterialSaveHelpText =
            "Overwrite updates the referenced MIC everywhere it is shared. Create new makes a named MIC for only this planet layer and repoints the BioPlanet property.";
        SearchResultsList.ItemsSource = SearchResults;
        RecentsController.InitRecentControl(Toolname, Recents_MenuItem, LoadFile);
    }

    public SFXGalaxyEditorWindow(ExportEntry export) : this()
    {
        _queuedFile = export.FileRef.FilePath;
        _queuedExportUIndex = export.UIndex;
        _queuedExportPath = export.InstancedFullPath;
        _queuedExportClass = export.ClassName;
    }

    public static bool CanOpenExport(ExportEntry export) =>
        TryGetGalaxyObject(export, out _) && export.Game is MEGame.LE2 or MEGame.LE3;

    private bool CanSavePackageSet() => Pcc is not null && _packageSet is not null
        && (_packageSet.SynchronizesSecondary ? _companionPcc is not null : _supportPcc is not null);

    public static bool TryGetGalaxyObject(ExportEntry export, [NotNullWhen(true)] out ExportEntry galaxyObject)
    {
        for (ExportEntry current = export; current is not null; current = current.Parent as ExportEntry)
        {
            if (current.ClassName == "SFXGalaxy" || current.IsA("SFXGalaxyMapObject"))
            {
                galaxyObject = current;
                return true;
            }
        }

        galaxyObject = null;
        return false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_handledInitialLoad)
        {
            return;
        }
        _handledInitialLoad = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!string.IsNullOrWhiteSpace(_queuedFile))
            {
                string file = _queuedFile;
                int exportIndex = _queuedExportUIndex;
                string exportPath = _queuedExportPath;
                string exportClass = _queuedExportClass;
                _queuedFile = null;
                _queuedExportUIndex = 0;
                _queuedExportPath = null;
                _queuedExportClass = null;
                LoadFile(file);
                SFXGalaxyNode node = _nodesByUIndex.GetValueOrDefault(exportIndex);
                if (!string.IsNullOrWhiteSpace(exportPath))
                {
                    node = _nodesByUIndex.Values.FirstOrDefault(candidate =>
                        candidate.Export.ClassName.CaseInsensitiveEquals(exportClass)
                        && candidate.Export.InstancedFullPath.CaseInsensitiveEquals(exportPath)) ?? node;
                }
                if (node is not null)
                {
                    NavigateToSearchResult(node);
                }
                Activate();
                return;
            }

            Activate();
        }));
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (e.Cancel)
        {
            return;
        }

        if (_companionPcc is { IsModified: true } && Pcc?.IsModified != true
            && MessageBox.Show(this,
                $"{Path.GetFileName(_companionPcc.FilePath)} has unsaved synchronized changes. Close without saving the package pair?",
                "Unsaved galaxy map changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        RecentsController?.Dispose();
        PropertiesInterpreter?.UnloadExport();
        MetadataLoader?.UnloadExport();
        HierarchyRoots.ClearEx();
        SearchResults.ClearEx();
        EditableExports.ClearEx();
        UnloadVisualAssets();
        UnloadSecondaryPackages();
        UnLoadMEPackage();
    }

    private void OpenPackage()
    {
        OpenFileDialog dialog = AppDirectories.GetOpenPackageDialog();
        if (DirectoryMemory.ShowDialog(dialog) == true)
        {
            LoadFile(dialog.FileName);
        }
    }

    private void OpenHighestMountedLE2_Click(object sender, RoutedEventArgs e)
    {
        OpenHighestMountedGalaxyMap(MEGame.LE2, showErrors: true);
    }

    private void OpenHighestMountedLE3_Click(object sender, RoutedEventArgs e)
    {
        OpenHighestMountedGalaxyMap(MEGame.LE3, showErrors: true);
    }

    private void OpenHighestMountedGalaxyMap(MEGame game, bool showErrors)
    {
        if (TryResolveHighestMountedPackageSet(game, showErrors, out GalaxyMapPackageSet packageSet,
                out string galaxyMapPath, out string secondaryPath, out string galaxyArtPath))
        {
            LoadPackageSet(packageSet, galaxyMapPath, secondaryPath, galaxyArtPath);
        }
    }

    private bool TryResolveHighestMountedPackageSet(MEGame game, bool showErrors,
        out GalaxyMapPackageSet packageSet, out string galaxyMapPath, out string secondaryPath,
        out string galaxyArtPath)
    {
        packageSet = game switch
        {
            MEGame.LE2 => LE2PackageSet,
            MEGame.LE3 => LE3PackageSet,
            _ => null
        };
        galaxyMapPath = null;
        secondaryPath = null;
        galaxyArtPath = null;
        if (packageSet is null)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(MEDirectories.GetDefaultGamePath(game)))
        {
            if (showErrors)
            {
                MessageBox.Show(this,
                    $"Configure your Legendary Edition installation path before opening the highest-mounted {packageSet.GameLabel} galaxy map.",
                    "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return false;
        }

        bool foundGalaxyMap = MELoadedFiles.TryGetHighestMountedFile(game, packageSet.GalaxyMapFile, out galaxyMapPath);
        bool foundSecondary = MELoadedFiles.TryGetHighestMountedFile(game, packageSet.SecondaryFile, out secondaryPath);
        bool foundGalaxyArt = MELoadedFiles.TryGetHighestMountedFile(game, packageSet.GalaxyArtFile, out galaxyArtPath);
        if (!foundGalaxyMap || !foundSecondary || !foundGalaxyArt)
        {
            if (showErrors)
            {
                string missing = string.Join(" and ", new[]
                {
                    foundGalaxyMap ? null : packageSet.GalaxyMapFile,
                    foundSecondary ? null : packageSet.SecondaryFile,
                    foundGalaxyArt ? null : packageSet.GalaxyArtFile
                }.Where(name => name is not null));
                MessageBox.Show(this, $"Could not locate the highest-mounted {missing} in the configured {packageSet.GameLabel} installation.",
                    "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }
        return true;
    }

    public void LoadFile(string fileName)
    {
        try
        {
            using IMEPackage selectedPackage = MEPackageHandler.QuickOpenMEPackage(fileName);
            OpenHighestMountedGalaxyMap(selectedPackage.Game, showErrors: true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Unable to determine the game for the selected package:\n\n{exception.Message}",
                "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadPackageSet(GalaxyMapPackageSet packageSet, string galaxyMapPath, string secondaryPath,
        string galaxyArtPath)
    {
        try
        {
            PropertiesInterpreter.UnloadExport();
            MetadataLoader.UnloadExport();
            UnloadVisualAssets();
            UnloadSecondaryPackages();
            LoadMEPackage(galaxyMapPath);

            if (Pcc.Game != packageSet.Game)
            {
                throw new InvalidDataException($"{packageSet.GalaxyMapFile} is not a {packageSet.GameLabel} package.");
            }

            ExportEntry galaxy = GetGalaxyRoot(Pcc);
            if (galaxy is null)
            {
                throw new InvalidDataException($"{packageSet.GalaxyMapFile} does not contain an SFXGalaxy instance.");
            }

            if (packageSet.SynchronizesSecondary)
            {
                _companionPcc = MEPackageHandler.OpenMEPackage(secondaryPath);
                if (_companionPcc.Game != packageSet.Game || GetGalaxyRoot(_companionPcc) is null)
                {
                    throw new InvalidDataException($"{packageSet.SecondaryFile} does not contain a compatible {packageSet.GameLabel} SFXGalaxy instance.");
                }
            }
            else
            {
                _supportPcc = MEPackageHandler.OpenMEPackage(secondaryPath);
                if (_supportPcc.Game != packageSet.Game)
                {
                    throw new InvalidDataException($"{packageSet.SecondaryFile} is not a {packageSet.GameLabel} package.");
                }
            }

            _packageSet = packageSet;
            LoadGalaxyBackground(galaxyArtPath);
            RebuildHierarchy(galaxy.UIndex, galaxy.UIndex);
            if (packageSet.SynchronizesSecondary)
            {
                _companionNeedsFullSync = true;
                (int sourceCount, int companionCount, int differences) = CompareGalaxyStructure(Pcc, _companionPcc);
                CompanionSyncStatus = differences == 0
                    ? $"Both galaxy hierarchies loaded ({sourceCount} exports). Changes are mirrored from {packageSet.GalaxyMapFile}."
                    : $"Loaded with {differences} structural differences ({sourceCount} map / {companionCount} companion exports). The next edit will port the authoritative hierarchy.";
            }
            else
            {
                CompanionSyncStatus = $"Loaded required map object package {packageSet.SecondaryFile}.";
            }
            // Match Level Editor: keep the exact loaded package path visible in the window title.
            Title = $"SFXGalaxy Editor ({packageSet.GameLabel}) — {Pcc.FilePath}";
            RecentsController.AddRecent(galaxyMapPath, false, Pcc.Game);
            RecentsController.SaveRecentList(true);
            OnPropertyChanged(nameof(Pcc));
            OnPropertyChanged(nameof(AuthoritativePackagePath));
            OnPropertyChanged(nameof(CompanionPackagePath));
            OnPropertyChanged(nameof(SecondaryPackageLabel));
            OnPropertyChanged(nameof(GalaxyArtPackagePath));
            OnPropertyChanged(nameof(SupportsLE3ObjectTypes));
            UpdateStatus();
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception exception)
        {
            PropertiesInterpreter.UnloadExport();
            MetadataLoader.UnloadExport();
            UnloadVisualAssets();
            UnloadSecondaryPackages();
            UnLoadMEPackage();
            HierarchyRoots.ClearEx();
            MessageBox.Show(this, $"Unable to open the galaxy map package set:\n\n{exception.Message}",
                "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SavePackage()
    {
        try
        {
            // The authoritative hierarchy is always persisted first. LE3 then mirrors it into 203CIC;
            // LE2's 103b package is a read-only support level and has no SFXGalaxy hierarchy to mirror.
            await Pcc.SaveAsync();
            if (_packageSet.SynchronizesSecondary)
            {
                if (!SynchronizeCompanionFromAuthoritative(fullHierarchy: true, changedExports: null, showErrors: true))
                {
                    return;
                }
                await _companionPcc.SaveAsync();
                CompanionSyncStatus = "Both highest-mounted galaxy map packages are synchronized and saved.";
            }
            else
            {
                CompanionSyncStatus = $"Saved {_packageSet.GalaxyMapFile}; required map object package {_packageSet.SecondaryFile} remains loaded.";
            }
            UpdateStatus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Unable to save the galaxy map package set:\n\n{exception.Message}",
                "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SavePackageAs()
    {
        SaveFileDialog dialog = new()
        {
            Filter = "Unreal package|*.pcc;*.upk;*.u|All files|*.*",
            FileName = Path.GetFileName(Pcc.FilePath)
        };
        if (DirectoryMemory.ShowDialog(dialog) == true)
        {
            try
            {
                await Pcc.SaveAsync(dialog.FileName);
                if (_packageSet.SynchronizesSecondary)
                {
                    string companionSavePath = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, _packageSet.SecondaryFile);
                    if (!SynchronizeCompanionFromAuthoritative(fullHierarchy: true, changedExports: null, showErrors: true))
                    {
                        return;
                    }
                    await _companionPcc.SaveAsync(companionSavePath);
                    CompanionSyncStatus = "Both galaxy map packages were synchronized and saved to the selected folder.";
                }
                else
                {
                    CompanionSyncStatus = $"Saved {_packageSet.GalaxyMapFile} to the selected path; {_packageSet.SecondaryFile} remains loaded as support data.";
                }
                Title = $"SFXGalaxy Editor ({_packageSet.GameLabel}) — {Pcc.FilePath}";
                OnPropertyChanged(nameof(Pcc));
                OnPropertyChanged(nameof(AuthoritativePackagePath));
                OnPropertyChanged(nameof(CompanionPackagePath));
                UpdateStatus();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Unable to save the galaxy map package set:\n\n{exception.Message}",
                    "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static ExportEntry GetGalaxyRoot(IMEPackage package) => package?.Exports
        .FirstOrDefault(export => export.ClassName == "SFXGalaxy" && !export.IsDefaultObject && !export.IsTrash());

    private static List<ExportEntry> GetGalaxyExports(ExportEntry galaxy) =>
        [galaxy, .. galaxy.GetAllDescendants().OfType<ExportEntry>().Where(export => !export.IsTrash())];

    private static string GalaxySyncKey(ExportEntry export) => $"{export.ClassName}|{export.InstancedFullPath}";

    private static (int SourceCount, int CompanionCount, int Differences) CompareGalaxyStructure(
        IMEPackage sourcePackage, IMEPackage companionPackage)
    {
        ExportEntry sourceGalaxy = GetGalaxyRoot(sourcePackage);
        ExportEntry companionGalaxy = GetGalaxyRoot(companionPackage);
        HashSet<string> sourceKeys = GetGalaxyExports(sourceGalaxy).Select(GalaxySyncKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> companionKeys = GetGalaxyExports(companionGalaxy).Select(GalaxySyncKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int differences = sourceKeys.Except(companionKeys, StringComparer.OrdinalIgnoreCase).Count()
                          + companionKeys.Except(sourceKeys, StringComparer.OrdinalIgnoreCase).Count();
        return (sourceKeys.Count, companionKeys.Count, differences);
    }

    private void UnloadSecondaryPackages()
    {
        _companionPcc?.Release();
        _companionPcc = null;
        _supportPcc?.Release();
        _supportPcc = null;
        _companionNeedsFullSync = false;
        _pendingCompanionSyncUIndexes.Clear();
        _pendingCompanionFullSync = false;
        _packageSet = null;
        OnPropertyChanged(nameof(CompanionPackagePath));
        OnPropertyChanged(nameof(SecondaryPackageLabel));
        OnPropertyChanged(nameof(SupportsLE3ObjectTypes));
        CompanionSyncStatus = "Secondary package not loaded.";
    }

    private void LoadGalaxyBackground(string galaxyArtPath)
    {
        _galaxyArtPackagePath = galaxyArtPath;
        OnPropertyChanged(nameof(GalaxyArtPackagePath));
        _galaxyArtPcc = MEPackageHandler.OpenMEPackage(galaxyArtPath);
        if (_galaxyArtPcc.Game != _packageSet.Game)
        {
            throw new InvalidDataException($"{_packageSet.GalaxyArtFile} is not a {_packageSet.GameLabel} package.");
        }
        ExportEntry galaxyTexture = _galaxyArtPcc.FindExport(GalaxyTexturePath, "Texture2D")
            ?? throw new InvalidDataException($"{_packageSet.GalaxyArtFile} does not contain {GalaxyTexturePath}.");
        _galaxyBackground = DecodeBackgroundTexture(galaxyTexture)
            ?? throw new InvalidDataException($"Could not decode {GalaxyTexturePath} from {_packageSet.GalaxyArtFile}.");
    }

    private void UnloadVisualAssets()
    {
        ClosePlanetMaterialEditor();
        _galaxyArtPcc?.Release();
        _galaxyArtPcc = null;
        _galaxyBackground = null;
        _galaxyArtPackagePath = null;
        _backgroundTextureCache.Clear();
        _texturePackageCache.ReleasePackages();
        OnPropertyChanged(nameof(GalaxyArtPackagePath));
    }

    private ImageSource DecodeBackgroundTexture(ExportEntry textureExport)
    {
        if (textureExport is null || !textureExport.IsA("Texture2D"))
        {
            return null;
        }

        string cacheKey = $"{textureExport.FileRef.FilePath}|{textureExport.UIndex}|{textureExport.DataSize}";
        if (_backgroundTextureCache.TryGetValue(cacheKey, out ImageSource cached))
        {
            return cached;
        }

        try
        {
            LECTexture2D texture = new(textureExport);
            Texture2DMipInfo mip = texture.Mips
                .Where(candidate => candidate.storageType != StorageTypes.empty
                                    && candidate.width <= MapExtent && candidate.height <= MapExtent)
                .OrderByDescending(candidate => (long)candidate.width * candidate.height)
                .FirstOrDefault() ?? texture.GetTopMip();
            if (mip is null)
            {
                return null;
            }

            byte[] png;
            try
            {
                png = texture.GetPNG(mip);
            }
            catch (FileNotFoundException)
            {
                Texture2DMipInfo packageMip = texture.Mips
                    .Where(candidate => candidate.storageType is StorageTypes.pccUnc or StorageTypes.pccLZO
                        or StorageTypes.pccZlib or StorageTypes.pccOodle)
                    .OrderByDescending(candidate => (long)candidate.width * candidate.height)
                    .FirstOrDefault();
                if (packageMip is null || ReferenceEquals(packageMip, mip))
                {
                    throw;
                }
                png = texture.GetPNG(packageMip);
            }

            using MemoryStream stream = new(png, writable: false);
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = MapExtent;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _backgroundTextureCache[cacheKey] = bitmap;
            return bitmap;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not decode galaxy map texture {textureExport.InstancedFullPath}: {exception}");
            return null;
        }
    }

    private bool SynchronizeCompanionFromAuthoritative(bool fullHierarchy,
        IEnumerable<ExportEntry> changedExports, bool showErrors, bool includeExplicitExternalExports = false)
    {
        if (Pcc is null)
        {
            return false;
        }
        if (_packageSet?.SynchronizesSecondary != true)
        {
            return true;
        }
        if (_companionPcc is null)
        {
            return false;
        }

        try
        {
            ExportEntry sourceGalaxy = GetGalaxyRoot(Pcc)
                ?? throw new InvalidDataException($"{_packageSet.GalaxyMapFile} no longer contains SFXGalaxy.");
            ExportEntry companionGalaxy = GetGalaxyRoot(_companionPcc)
                ?? throw new InvalidDataException($"{_packageSet.SecondaryFile} no longer contains SFXGalaxy.");

            fullHierarchy |= _companionNeedsFullSync;
            List<ExportEntry> sourcesToPort;
            if (fullHierarchy)
            {
                sourcesToPort = PrepareFullGalaxySync(sourceGalaxy, companionGalaxy);
            }
            else
            {
                string galaxyPathPrefix = $"{sourceGalaxy.InstancedFullPath}.";
                sourcesToPort = changedExports?.Where(export => export is not null && !export.IsTrash()
                        && (includeExplicitExternalExports || export == sourceGalaxy
                            || export.InstancedFullPath.StartsWith(galaxyPathPrefix, StringComparison.OrdinalIgnoreCase)))
                    .DistinctBy(export => export.UIndex).ToList() ?? [];
                if (sourcesToPort.Count == 0)
                {
                    return true;
                }
            }

            RelinkerOptionsPackage relinkerOptions = new()
            {
                ImportExportDependencies = true
            };
            List<string> portErrors = [];
            relinkerOptions.ErrorOccurredCallback = portErrors.Add;
            AddExistingCompanionMappings(relinkerOptions, sourcesToPort);
            Dictionary<string, ExportEntry> companionByKey = _companionPcc.Exports.Where(export => !export.IsTrash())
                .GroupBy(GalaxySyncKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (ExportEntry source in sourcesToPort)
            {
                string key = GalaxySyncKey(source);
                if (!companionByKey.TryGetValue(key, out ExportEntry target))
                {
                    if (!fullHierarchy && !includeExplicitExternalExports)
                    {
                        return SynchronizeCompanionFromAuthoritative(fullHierarchy: true, changedExports: null, showErrors);
                    }
                    if (source.Parent is not ExportEntry sourceParent
                        || !relinkerOptions.CrossPackageMap.TryGetValue(sourceParent, out IEntry targetParent))
                    {
                        throw new InvalidDataException($"Could not resolve the companion parent for {source.InstancedFullPath}.");
                    }
                    target = EntryImporter.ImportExport(_companionPcc, source, targetParent.UIndex, relinkerOptions) as ExportEntry
                             ?? throw new InvalidDataException($"Could not port {source.InstancedFullPath} into {_packageSet.SecondaryFile}.");
                    companionByKey[key] = target;
                }
                relinkerOptions.CrossPackageMap[source] = target;
                relinkerOptions.RelinkMapEntriesToSkip.Remove(source);
            }

            // Copy through LegendaryExplorerCore's existing export serializer first. This makes the
            // companion's property/binary boundaries match the authoritative export before Relinker
            // rewrites package-local references (whose UIndexes are intentionally allowed to differ).
            foreach (ExportEntry source in sourcesToPort)
            {
                if (relinkerOptions.CrossPackageMap[source] is not ExportEntry target
                    || !EntryImporter.ReplaceExportDataWithAnother(source, target, relinkerOptions))
                {
                    throw new InvalidDataException($"Could not serialize {source.InstancedFullPath} into {_packageSet.SecondaryFile}.");
                }
            }

            Relinker.RelinkAll(relinkerOptions);
            if (portErrors.Count > 0)
            {
                throw new InvalidDataException(string.Join(Environment.NewLine, portErrors));
            }

            _companionNeedsFullSync = false;
            int warningCount = relinkerOptions.RelinkReport.Count;
            CompanionSyncStatus = warningCount == 0
                ? $"Synchronized {sourcesToPort.Count} exports from {_packageSet.GalaxyMapFile} to {_packageSet.SecondaryFile}."
                : $"Synchronized with {warningCount} relinker warnings; save will retry a full synchronization.";
            return warningCount == 0;
        }
        catch (Exception exception)
        {
            _companionNeedsFullSync = true;
            CompanionSyncStatus = $"Companion synchronization failed: {exception.Message}";
            if (showErrors)
            {
                MessageBox.Show(this,
                    $"{_packageSet.GalaxyMapFile} remains the authoritative edited package, but its changes could not be ported to {_packageSet.SecondaryFile}:\n\n{exception.Message}",
                    "Galaxy map synchronization", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
    }

    private void AddExistingCompanionMappings(RelinkerOptionsPackage relinkerOptions,
        IReadOnlyCollection<ExportEntry> sourcesToPort)
    {
        HashSet<int> portUIndexes = sourcesToPort.Select(export => export.UIndex).ToHashSet();
        Dictionary<string, IEntry> companionEntries = _companionPcc.Exports.Cast<IEntry>()
            .Concat(_companionPcc.Imports)
            .GroupBy(EntrySyncKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (IEntry sourceEntry in Pcc.Exports.Cast<IEntry>().Concat(Pcc.Imports))
        {
            if (sourceEntry is ExportEntry sourceExport && portUIndexes.Contains(sourceExport.UIndex))
            {
                continue;
            }
            if (companionEntries.TryGetValue(EntrySyncKey(sourceEntry), out IEntry companionEntry))
            {
                relinkerOptions.CrossPackageMap[sourceEntry] = companionEntry;
                relinkerOptions.RelinkMapEntriesToSkip.Add(sourceEntry);
            }
        }
    }

    private static string EntrySyncKey(IEntry entry) => $"{entry.ClassName}|{entry.InstancedFullPath}";

    private List<ExportEntry> PrepareFullGalaxySync(ExportEntry sourceGalaxy, ExportEntry companionGalaxy)
    {
        List<ExportEntry> sourceExports = GetGalaxyExports(sourceGalaxy);
        // Retail BioPlanet MICs are referenced by the hierarchy but are not consistently outered
        // beneath SFXGalaxy. Include those exports in every full two-package synchronization.
        sourceExports.AddRange(sourceExports.Where(export => export.IsA("BioPlanet"))
            .SelectMany(planet => new[]
            {
                planet.GetProperty<ObjectProperty>("PlanetMaterial")?.ResolveToEntry(Pcc) as ExportEntry,
                planet.GetProperty<ObjectProperty>("CloudMaterial")?.ResolveToEntry(Pcc) as ExportEntry
            })
            .Where(material => material is not null && !material.IsTrash()));
        sourceExports = sourceExports.DistinctBy(export => export.UIndex).ToList();
        HashSet<string> sourceKeys = sourceExports.Select(GalaxySyncKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ExportEntry> companionOnly = GetGalaxyExports(companionGalaxy)
            .Where(export => !sourceKeys.Contains(GalaxySyncKey(export))).ToList();
        HashSet<int> companionOnlyIndexes = companionOnly.Select(export => export.UIndex).ToHashSet();
        foreach (ExportEntry extraRoot in companionOnly.Where(export =>
                     export.Parent is not ExportEntry parent || !companionOnlyIndexes.Contains(parent.UIndex)))
        {
            EntryPruner.TrashEntryAndDescendants(extraRoot);
        }
        return sourceExports;
    }

    private void BuildHierarchy(ExportEntry galaxy)
    {
        _nodesByUIndex.Clear();
        _tlkCache.Clear();
        HashSet<int> visited = [];
        _rootNode = BuildNode(galaxy, null, visited);
        if (_rootNode is null)
        {
            throw new InvalidDataException("The SFXGalaxy hierarchy could not be read.");
        }
    }

    private SFXGalaxyNode BuildNode(ExportEntry export, SFXGalaxyNode parent, HashSet<int> visited)
    {
        if (!visited.Add(export.UIndex))
        {
            return null;
        }

        PropertyCollection properties = export.GetProperties();
        SFXGalaxyNodeKind kind = Classify(export, properties);
        SFXGalaxyNode node = new()
        {
            Export = export,
            Parent = parent,
            Kind = kind,
            DisplayName = ResolveDisplayName(export, properties, kind),
            Description = ResolveDescription(properties),
            PosX = properties.GetProp<IntProperty>("PosX")?.Value ?? MapExtent / 2,
            PosY = properties.GetProp<IntProperty>("PosY")?.Value ?? MapExtent / 2
        };
        _nodesByUIndex[export.UIndex] = node;

        if (kind == SFXGalaxyNodeKind.System)
        {
            node.Children.Add(new SFXGalaxyNode
            {
                Export = export,
                Parent = node,
                Kind = SFXGalaxyNodeKind.Star,
                DisplayName = $"{node.DisplayName} star",
                Description = "The system's implicit central star. Its appearance is stored on SFXSystem as SunColor, StarColor, and FlareTint.",
                IsImplicitStar = true,
                PosX = MapExtent / 2,
                PosY = MapExtent / 2
            });
        }

        // LE3 serializes the hierarchy through Children and keeps its typed arrays as table-ID
        // lookups. LE2 has no Children property and serializes its hierarchy through the typed
        // Clusters, Systems, Planets, and Features arrays declared in LE2's SFXGame.
        foreach (string childArrayName in GetSerializedChildArrayNames(export))
        {
            foreach (ObjectProperty childReference in properties
                         .GetProp<ArrayProperty<ObjectProperty>>(childArrayName)?.ToList() ?? [])
            {
                if (childReference.ResolveToEntry(Pcc) is not ExportEntry childExport || childExport.IsTrash())
                {
                    continue;
                }

                SFXGalaxyNode child = BuildNode(childExport, node, visited);
                if (child is not null)
                {
                    node.Children.Add(child);
                }
            }
        }

        return node;
    }

    private static IEnumerable<string> GetSerializedChildArrayNames(ExportEntry export)
    {
        if (export.Game == MEGame.LE3)
        {
            return ["Children"];
        }
        if (export.Game != MEGame.LE2)
        {
            return [];
        }
        return export.ClassName switch
        {
            "SFXGalaxy" => ["Clusters"],
            "SFXCluster" => ["Systems"],
            "SFXSystem" => ["Planets"],
            "BioPlanet" => ["Features"],
            _ => []
        };
    }

    private static SFXGalaxyNodeKind Classify(ExportEntry export, PropertyCollection properties)
    {
        if (export.ClassName == "SFXGalaxy") return SFXGalaxyNodeKind.Galaxy;
        if (export.ClassName == "SFXCluster") return SFXGalaxyNodeKind.Cluster;
        if (export.ClassName == "SFXSystem") return SFXGalaxyNodeKind.System;
        if (export.ClassName.Contains("MassRelay", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.MassRelay;
        if (export.ClassName.Contains("FuelDepot", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.FuelDepot;
        if (export.ClassName.Contains("Reaper", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.Reaper;
        if (export.ClassName == "SFXPlanetFeatureGAWAsset") return SFXGalaxyNodeKind.WarAsset;
        if (export.IsA("SFXPlanetFeature")) return SFXGalaxyNodeKind.Feature;
        if (export.IsA("BioPlanet"))
        {
            string systemType = properties.GetProp<EnumProperty>("SystemLevelType")?.Value.Name ?? string.Empty;
            string orbitType = properties.GetProp<EnumProperty>("OrbitRing")?.Value.Name ?? string.Empty;
            if (orbitType.Contains("ASTEROID", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.AsteroidBelt;
            if (systemType.Contains("ANOMALY", StringComparison.OrdinalIgnoreCase)) return SFXGalaxyNodeKind.Anomaly;
            return SFXGalaxyNodeKind.Planet;
        }
        return SFXGalaxyNodeKind.Object;
    }

    private string ResolveDisplayName(ExportEntry export, PropertyCollection properties, SFXGalaxyNodeKind kind)
    {
        if (properties.GetProp<StringRefProperty>("DisplayName") is { Value: > 0 } displayName)
        {
            string resolved = ResolveTlk(displayName.Value);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        string assetName = properties.GetProp<StrProperty>("AssetName")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(assetName))
        {
            return assetName;
        }

        if (kind == SFXGalaxyNodeKind.Galaxy)
        {
            return "The Milky Way";
        }
        string objectName = export.ObjectNameString.Replace('_', ' ').Trim();
        return !string.IsNullOrWhiteSpace(objectName) ? objectName : $"{KindName(kind)} {export.ObjectName.Number}";
    }

    private string ResolveDescription(PropertyCollection properties)
    {
        foreach (string propertyName in new[] { "Description", "PlanetPlotLabel", "LandingSiteText", "ButtonLabel" })
        {
            if (properties.GetProp<StringRefProperty>(propertyName) is { Value: > 0 } stringRef)
            {
                string resolved = ResolveTlk(stringRef.Value);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved.Replace("\r", " ").Replace("\n", " ");
                }
            }
        }
        return string.Empty;
    }

    private string ResolveTlk(int stringRef)
    {
        if (_tlkCache.TryGetValue(stringRef, out string cached))
        {
            return cached;
        }

        string value;
        try
        {
            value = TLKManagerWPF.GlobalFindStrRefbyID(stringRef, Pcc)?.Trim().Trim('"') ?? string.Empty;
            if (value.Equals("No Data", StringComparison.OrdinalIgnoreCase) || value.StartsWith("No TLK", StringComparison.OrdinalIgnoreCase))
            {
                value = string.Empty;
            }
        }
        catch
        {
            value = string.Empty;
        }
        _tlkCache[stringRef] = value;
        return value;
    }

    private void RebuildHierarchy(int currentUIndex = 0, int selectedUIndex = 0, bool selectedWasStar = false, int editableExportUIndex = 0)
    {
        if (Pcc is null)
        {
            return;
        }

        ExportEntry galaxy = GetGalaxyRoot(Pcc);
        if (galaxy is null)
        {
            return;
        }

        BuildHierarchy(galaxy);
        HierarchyRoots.ClearEx();
        HierarchyRoots.Add(_rootNode);

        CurrentNode = _nodesByUIndex.GetValueOrDefault(currentUIndex) ?? _rootNode;
        SFXGalaxyNode selected = _nodesByUIndex.GetValueOrDefault(selectedUIndex) ?? CurrentNode;
        if (selectedWasStar && selected.Kind == SFXGalaxyNodeKind.System)
        {
            selected = selected.Children.FirstOrDefault(c => c.IsImplicitStar) ?? selected;
        }
        SelectedNode = selected;
        if (editableExportUIndex != 0 && EditableExports.FirstOrDefault(option => option.Export.UIndex == editableExportUIndex) is { } preferred)
        {
            EditableExportCombo.SelectedItem = preferred;
        }
        RenderCurrentLevel();
        SelectTreeNode(selected);
    }

    private void NavigateTo(SFXGalaxyNode node)
    {
        if (!CanEnter(node))
        {
            return;
        }
        CurrentNode = node;
        SelectedNode = node;
        RenderCurrentLevel();
        SelectTreeNode(node);
    }

    private static bool CanEnter(SFXGalaxyNode node) => node is not null && !node.IsImplicitStar
        && node.Kind is SFXGalaxyNodeKind.Galaxy or SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System;

    private void NavigateIntoSelected() => NavigateTo(SelectedNode);

    private void NavigateBack()
    {
        if (CurrentNode?.Parent is SFXGalaxyNode parent)
        {
            CurrentNode = parent;
            SelectedNode = parent;
            RenderCurrentLevel();
            SelectTreeNode(parent);
        }
    }

    private void BuildBreadcrumbs()
    {
        if (BreadcrumbPanel is null)
        {
            return;
        }
        BreadcrumbPanel.Children.Clear();
        if (CurrentNode is null)
        {
            return;
        }

        List<SFXGalaxyNode> path = [];
        for (SFXGalaxyNode node = CurrentNode; node is not null; node = node.Parent)
        {
            path.Add(node);
        }
        path.Reverse();

        foreach (SFXGalaxyNode node in path)
        {
            if (BreadcrumbPanel.Children.Count > 0)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "›", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray
                });
            }
            Button button = new()
            {
                Content = node.DisplayName,
                Padding = new Thickness(4, 1, 4, 1),
                Tag = node,
                FontWeight = node == CurrentNode ? FontWeights.SemiBold : FontWeights.Normal
            };
            button.Click += (_, _) => NavigateTo((SFXGalaxyNode)button.Tag);
            BreadcrumbPanel.Children.Add(button);
        }
    }

    private void RenderCurrentLevel()
    {
        if (MapCanvas is null)
        {
            return;
        }

        MapCanvas.Children.Clear();
        _markerElements.Clear();
        _visibleCenters.Clear();
        if (CurrentNode is null)
        {
            return;
        }

        DrawLevelBackground();
        if (ShowCoordinateGrid)
        {
            DrawCoordinateGrid();
        }
        if (CurrentNode.Kind == SFXGalaxyNodeKind.Galaxy)
        {
            DrawRelayConnections();
        }
        if (CurrentNode.Kind == SFXGalaxyNodeKind.System)
        {
            DrawSystemOrbits();
        }

        foreach (SFXGalaxyNode node in CurrentNode.Children)
        {
            DrawMarker(node);
        }
        OnPropertyChanged(nameof(CurrentObjectCountText));
    }

    private void DrawLevelBackground()
    {
        ImageSource background = CurrentNode.Kind switch
        {
            SFXGalaxyNodeKind.Galaxy => _galaxyBackground,
            SFXGalaxyNodeKind.Cluster => GetClusterBackground(CurrentNode),
            _ => null
        };
        if (background is null)
        {
            DrawSpaceBackground();
            return;
        }

        MapCanvas.Children.Add(new Image
        {
            Width = MapExtent,
            Height = MapExtent,
            Source = background,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        });
    }

    private ImageSource GetClusterBackground(SFXGalaxyNode cluster)
    {
        ObjectProperty textureReference = cluster.Export.GetProperty<ObjectProperty>("ClusterTexture");
        if (textureReference is null || textureReference.Value == 0)
        {
            return null;
        }

        try
        {
            IEntry textureEntry = textureReference.ResolveToEntry(cluster.Export.FileRef);
            ExportEntry textureExport = textureEntry as ExportEntry;
            if (textureExport is null && textureEntry is ImportEntry textureImport)
            {
                EntryImporter.TryResolveImport(textureImport, out textureExport, cache: _texturePackageCache);
            }
            return DecodeBackgroundTexture(textureExport);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not resolve ClusterTexture for {cluster.Export.InstancedFullPath}: {exception}");
            return null;
        }
    }

    private void DrawSpaceBackground()
    {
        Ellipse glow = new()
        {
            Width = 880,
            Height = 880,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush(Color.FromArgb(70, 24, 83, 113), Color.FromArgb(0, 1, 5, 10))
        };
        Canvas.SetLeft(glow, 72);
        Canvas.SetTop(glow, 72);
        MapCanvas.Children.Add(glow);

        Random random = new(203);
        for (int i = 0; i < 180; i++)
        {
            double size = random.NextDouble() * 1.8 + 0.5;
            Ellipse star = new()
            {
                Width = size,
                Height = size,
                Fill = i % 13 == 0 ? Brushes.LightSkyBlue : Brushes.White,
                Opacity = random.NextDouble() * 0.65 + 0.25,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(star, random.NextDouble() * MapExtent);
            Canvas.SetTop(star, random.NextDouble() * MapExtent);
            MapCanvas.Children.Add(star);
        }
    }

    private void DrawCoordinateGrid()
    {
        for (int coordinate = 0; coordinate <= MapExtent; coordinate += 64)
        {
            bool major = coordinate % 256 == 0;
            Brush stroke = new SolidColorBrush(major ? Color.FromArgb(85, 65, 137, 165) : Color.FromArgb(38, 91, 139, 158));
            MapCanvas.Children.Add(new Line { X1 = coordinate, Y1 = 0, X2 = coordinate, Y2 = MapExtent, Stroke = stroke, StrokeThickness = major ? 1.2 : 0.6, IsHitTestVisible = false });
            MapCanvas.Children.Add(new Line { X1 = 0, Y1 = coordinate, X2 = MapExtent, Y2 = coordinate, Stroke = stroke, StrokeThickness = major ? 1.2 : 0.6, IsHitTestVisible = false });
            if (major && coordinate < MapExtent)
            {
                TextBlock label = new() { Text = (coordinate / (double)MapExtent).ToString("0.00"), Foreground = Brushes.LightBlue, FontSize = 12, IsHitTestVisible = false };
                Canvas.SetLeft(label, coordinate + 3);
                Canvas.SetTop(label, 2);
                MapCanvas.Children.Add(label);
            }
        }
    }

    private void DrawRelayConnections()
    {
        HashSet<(int, int)> drawn = [];
        foreach (SFXGalaxyNode cluster in CurrentNode.Children.Where(n => n.Kind == SFXGalaxyNodeKind.Cluster))
        {
            ArrayProperty<ObjectProperty> links = cluster.Export.GetProperty<ArrayProperty<ObjectProperty>>("RelayConnections");
            if (links is null)
            {
                continue;
            }
            foreach (ObjectProperty link in links)
            {
                if (!_nodesByUIndex.TryGetValue(link.Value, out SFXGalaxyNode other) || other.Parent != CurrentNode)
                {
                    continue;
                }
                (int, int) key = cluster.Export.UIndex < other.Export.UIndex
                    ? (cluster.Export.UIndex, other.Export.UIndex)
                    : (other.Export.UIndex, cluster.Export.UIndex);
                if (!drawn.Add(key))
                {
                    continue;
                }
                MapCanvas.Children.Add(new Line
                {
                    X1 = cluster.PosX, Y1 = cluster.PosY, X2 = other.PosX, Y2 = other.PosY,
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 224, 61, 77)), StrokeThickness = 2.4,
                    IsHitTestVisible = false
                });
            }
        }
    }

    private void DrawSystemOrbits()
    {
        foreach (SFXGalaxyNode node in CurrentNode.Children.Where(n => !n.IsImplicitStar && ShouldShowOrbit(n)))
        {
            double radius = Math.Sqrt(Math.Pow(node.PosX - MapExtent / 2.0, 2) + Math.Pow(node.PosY - MapExtent / 2.0, 2));
            if (radius < 16)
            {
                continue;
            }
            Ellipse orbit = new()
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = node.Kind == SFXGalaxyNodeKind.AsteroidBelt
                    ? new SolidColorBrush(Color.FromArgb(175, 194, 169, 121))
                    : new SolidColorBrush(Color.FromArgb(90, 88, 150, 177)),
                StrokeThickness = node.Kind == SFXGalaxyNodeKind.AsteroidBelt ? 4 : 1.2,
                StrokeDashArray = node.Kind == SFXGalaxyNodeKind.AsteroidBelt ? new DoubleCollection([1, 3]) : null,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(orbit, MapExtent / 2.0 - radius);
            Canvas.SetTop(orbit, MapExtent / 2.0 - radius);
            MapCanvas.Children.Add(orbit);
        }
    }

    private static bool ShouldShowOrbit(SFXGalaxyNode node)
    {
        if (!node.Export.IsA("BioPlanet"))
        {
            return false;
        }
        PropertyCollection properties = node.Export.GetProperties();
        return properties.GetProp<EnumProperty>("OrbitRing")?.Value.Name != "OR_NONE";
    }

    private void DrawMarker(SFXGalaxyNode node)
    {
        double size = node.Kind switch
        {
            SFXGalaxyNodeKind.Star => 42,
            SFXGalaxyNodeKind.Cluster => 28,
            SFXGalaxyNodeKind.System => 24,
            SFXGalaxyNodeKind.Planet => 22,
            SFXGalaxyNodeKind.AsteroidBelt => 15,
            _ => 18
        };
        double x = Math.Clamp(node.PosX, 0, MapExtent);
        double y = Math.Clamp(node.PosY, 0, MapExtent);
        Canvas marker = new() { Width = 230, Height = 58, Tag = node, Cursor = Cursors.Hand };
        Ellipse body = new()
        {
            Width = size,
            Height = size,
            Fill = node.KindBrush,
            Stroke = node == SelectedNode ? Brushes.White : new SolidColorBrush(Color.FromArgb(210, 22, 91, 118)),
            StrokeThickness = node == SelectedNode ? 3 : 1.5
        };
        marker.Children.Add(body);

        if (node.Kind == SFXGalaxyNodeKind.Star)
        {
            Ellipse corona = new() { Width = size + 24, Height = size + 24, Stroke = new SolidColorBrush(Color.FromArgb(100, 255, 190, 42)), StrokeThickness = 8, IsHitTestVisible = false };
            Canvas.SetLeft(corona, -12);
            Canvas.SetTop(corona, -12);
            marker.Children.Insert(0, corona);
        }

        TextBlock label = new()
        {
            Text = node.DisplayName,
            Foreground = Brushes.White,
            FontSize = node.Kind is SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System ? 15 : 13,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(145, 6, 13, 20)),
            Padding = new Thickness(3, 1, 3, 1),
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, size + 6);
        Canvas.SetTop(label, Math.Max(0, size / 2 - 10));
        marker.Children.Add(label);

        if (node.Kind == SFXGalaxyNodeKind.Cluster && CurrentNode.Kind == SFXGalaxyNodeKind.Galaxy)
        {
            Ellipse connector = new()
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.IndianRed,
                Stroke = Brushes.White,
                StrokeThickness = 1,
                Cursor = Cursors.Cross,
                ToolTip = "Drag to another cluster to create a relay connection",
                Tag = node
            };
            Canvas.SetLeft(connector, size - 3);
            Canvas.SetTop(connector, size / 2 - 5);
            connector.MouseLeftButtonDown += RelayHandle_MouseLeftButtonDown;
            marker.Children.Add(connector);
        }

        marker.MouseLeftButtonDown += Marker_MouseLeftButtonDown;
        marker.MouseRightButtonUp += Marker_MouseRightButtonUp;
        Canvas.SetLeft(marker, x - size / 2);
        Canvas.SetTop(marker, y - size / 2);
        MapCanvas.Children.Add(marker);
        _markerElements[node] = marker;
        _visibleCenters[node] = new Point(x, y);
    }

    private void Marker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { Cursor: { } cursor } && cursor == Cursors.Cross)
        {
            return;
        }
        if (sender is not FrameworkElement { Tag: SFXGalaxyNode node })
        {
            return;
        }
        SelectedNode = node;
        if (e.ClickCount == 2 && node.Kind is SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System)
        {
            _dragNode = null;
            Mouse.Capture(null);
            NavigateTo(node);
            e.Handled = true;
            return;
        }
        if (node.IsImplicitStar)
        {
            e.Handled = true;
            return;
        }
        _dragNode = node;
        _dragStart = e.GetPosition(MapCanvas);
        _dragOrigin = new Point(node.PosX, node.PosY);
        Mouse.Capture(MapCanvas);
        e.Handled = true;
    }

    private void RelayHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SFXGalaxyNode cluster })
        {
            return;
        }
        SelectedNode = cluster;
        _relaySource = cluster;
        Point pointer = e.GetPosition(MapCanvas);
        _relayPreview = new Line
        {
            X1 = cluster.PosX, Y1 = cluster.PosY, X2 = pointer.X, Y2 = pointer.Y,
            Stroke = Brushes.OrangeRed, StrokeThickness = 2, StrokeDashArray = new DoubleCollection([4, 3]),
            IsHitTestVisible = false
        };
        MapCanvas.Children.Add(_relayPreview);
        Mouse.Capture(MapCanvas);
        e.Handled = true;
    }

    private void MapCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        Point pointer = e.GetPosition(MapCanvas);
        if (_relaySource is not null && _relayPreview is not null)
        {
            _relayPreview.X2 = Math.Clamp(pointer.X, 0, MapExtent);
            _relayPreview.Y2 = Math.Clamp(pointer.Y, 0, MapExtent);
            return;
        }
        if (_dragNode is null || e.LeftButton != MouseButtonState.Pressed || !_markerElements.TryGetValue(_dragNode, out FrameworkElement marker))
        {
            return;
        }
        Vector delta = pointer - _dragStart;
        _dragNode.PosX = (int)Math.Round(Math.Clamp(_dragOrigin.X + delta.X, 0, MapExtent));
        _dragNode.PosY = (int)Math.Round(Math.Clamp(_dragOrigin.Y + delta.Y, 0, MapExtent));
        double markerSize = MarkerSize(_dragNode);
        Canvas.SetLeft(marker, _dragNode.PosX - markerSize / 2);
        Canvas.SetTop(marker, _dragNode.PosY - markerSize / 2);
        _visibleCenters[_dragNode] = new Point(_dragNode.PosX, _dragNode.PosY);
        UpdateStatus();
    }

    private void MapCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_relaySource is not null)
        {
            Point pointer = e.GetPosition(MapCanvas);
            SFXGalaxyNode target = _visibleCenters
                .Where(pair => pair.Key.Kind == SFXGalaxyNodeKind.Cluster && pair.Key != _relaySource)
                .OrderBy(pair => (pair.Value - pointer).Length)
                .FirstOrDefault(pair => (pair.Value - pointer).Length <= 45).Key;
            SFXGalaxyNode source = _relaySource;
            CancelRelayDrag();
            if (target is not null)
            {
                AddRelayConnection(source, target);
            }
            return;
        }
        if (_dragNode is not null)
        {
            SFXGalaxyNode node = _dragNode;
            _dragNode = null;
            Mouse.Capture(null);
            PropertyCollection properties = node.Export.GetProperties();
            properties.AddOrReplaceProp(new IntProperty(node.PosX, "PosX"));
            properties.AddOrReplaceProp(new IntProperty(node.PosY, "PosY"));
            _suppressPackageRefresh = true;
            node.Export.WriteProperties(properties);
            _suppressPackageRefresh = false;
            SynchronizeCompanionFromAuthoritative(fullHierarchy: false, [node.Export], showErrors: false);
            RenderCurrentLevel();
            UpdateStatus();
        }
    }

    private void MapCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released && _relaySource is not null)
        {
            CancelRelayDrag();
        }
    }

    private void CancelRelayDrag()
    {
        if (_relayPreview is not null)
        {
            MapCanvas.Children.Remove(_relayPreview);
        }
        _relayPreview = null;
        _relaySource = null;
        Mouse.Capture(null);
    }

    private void Marker_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SFXGalaxyNode node } marker)
        {
            return;
        }
        SelectedNode = node;
        ContextMenu menu = BuildObjectContextMenu(node);
        marker.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildObjectContextMenu(SFXGalaxyNode node)
    {
        ContextMenu menu = new();
        if (CanEnter(node))
        {
            MenuItem open = new() { Header = "Open" };
            open.Click += (_, _) => NavigateTo(node);
            menu.Items.Add(open);
        }

        MenuItem add = new() { Header = "Add" };
        AddCreationMenuItems(add.Items, node, null);
        if (add.Items.Count > 0)
        {
            menu.Items.Add(add);
        }

        if (node.Kind == SFXGalaxyNodeKind.Cluster)
        {
            MenuItem deleteConnection = new() { Header = "Delete connection" };
            foreach (SFXGalaxyNode connected in GetRelayConnections(node).OrderBy(n => n.DisplayName))
            {
                MenuItem item = new() { Header = connected.DisplayName };
                item.Click += (_, _) => RemoveRelayConnection(node, connected);
                deleteConnection.Items.Add(item);
            }
            deleteConnection.IsEnabled = deleteConnection.Items.Count > 0;
            menu.Items.Add(deleteConnection);
        }

        if (node.Parent is not null && !node.IsImplicitStar)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }
            MenuItem clone = new() { Header = "Clone object" };
            clone.Click += (_, _) =>
            {
                SelectedNode = node;
                DuplicateSelected();
            };
            menu.Items.Add(clone);

            MenuItem delete = new() { Header = "Delete object..." };
            delete.Click += (_, _) =>
            {
                SelectedNode = node;
                DeleteSelected();
            };
            menu.Items.Add(delete);
        }
        return menu;
    }

    private ContextMenu BuildAddMenu(SFXGalaxyNode context, Point? position)
    {
        ContextMenu menu = new();
        AddCreationMenuItems(menu.Items, context, position);
        return menu;
    }

    private void AddCreationMenuItems(ItemCollection items, SFXGalaxyNode context, Point? position)
    {
        if (context is null)
        {
            return;
        }
        SFXGalaxyNodeKind[] kinds = context.Kind switch
        {
            SFXGalaxyNodeKind.Galaxy => [SFXGalaxyNodeKind.Cluster],
            SFXGalaxyNodeKind.Cluster => [SFXGalaxyNodeKind.System],
            SFXGalaxyNodeKind.System =>
            [
                SFXGalaxyNodeKind.Planet,
                SFXGalaxyNodeKind.AsteroidBelt,
                SFXGalaxyNodeKind.Anomaly,
                SFXGalaxyNodeKind.MassRelay,
                SFXGalaxyNodeKind.FuelDepot,
                SFXGalaxyNodeKind.Reaper
            ],
            SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly =>
                [SFXGalaxyNodeKind.Feature, SFXGalaxyNodeKind.WarAsset],
            _ => []
        };
        foreach (SFXGalaxyNodeKind kind in kinds.Where(IsObjectKindSupported))
        {
            MenuItem item = new() { Header = KindName(kind) };
            item.Click += (_, _) => CreateCustomObject(kind, context, position);
            items.Add(item);
        }
    }

    private bool IsObjectKindSupported(SFXGalaxyNodeKind kind) => Pcc?.Game switch
    {
        MEGame.LE2 => kind is SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System
            or SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly
            or SFXGalaxyNodeKind.Feature,
        MEGame.LE3 => true,
        _ => false
    };

    private static SFXGalaxyNode ResolveAddContext(SFXGalaxyNode node)
    {
        for (SFXGalaxyNode current = node; current is not null; current = current.Parent)
        {
            if (current.Kind is SFXGalaxyNodeKind.Galaxy or SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System
                or SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly)
            {
                return current;
            }
        }
        return null;
    }

    private void MapCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || CurrentNode is null)
        {
            return;
        }
        Point position = e.GetPosition(MapCanvas);
        position.X = Math.Clamp(position.X, 0, MapExtent);
        position.Y = Math.Clamp(position.Y, 0, MapExtent);
        ContextMenu menu = BuildAddMenu(CurrentNode, position);
        if (menu.Items.Count == 0)
        {
            return;
        }
        menu.PlacementTarget = MapCanvas;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddRelayConnection(SFXGalaxyNode first, SFXGalaxyNode second)
    {
        if (first == second || HasRelayConnection(first.Export, second.Export.UIndex))
        {
            return;
        }
        _suppressPackageRefresh = true;
        WriteRelayReference(first.Export, second.Export, true);
        WriteRelayReference(second.Export, first.Export, true);
        _suppressPackageRefresh = false;
        SynchronizeCompanionFromAuthoritative(fullHierarchy: false, [first.Export, second.Export], showErrors: false);
        RenderCurrentLevel();
    }

    private void RemoveRelayConnection(SFXGalaxyNode first, SFXGalaxyNode second)
    {
        _suppressPackageRefresh = true;
        WriteRelayReference(first.Export, second.Export, false);
        WriteRelayReference(second.Export, first.Export, false);
        _suppressPackageRefresh = false;
        SynchronizeCompanionFromAuthoritative(fullHierarchy: false, [first.Export, second.Export], showErrors: false);
        RenderCurrentLevel();
    }

    private static void WriteRelayReference(ExportEntry cluster, ExportEntry other, bool add)
    {
        PropertyCollection properties = cluster.GetProperties();
        ArrayProperty<ObjectProperty> connections = properties.GetProp<ArrayProperty<ObjectProperty>>("RelayConnections");
        if (connections is null)
        {
            if (!add) return;
            connections = GetOrCreateSerializedObjectArray(cluster, properties, "RelayConnections");
        }
        if (add)
        {
            if (connections.All(reference => reference.Value != other.UIndex))
            {
                connections.Add(new ObjectProperty(other));
            }
        }
        else
        {
            for (int i = connections.Count - 1; i >= 0; i--)
            {
                if (connections[i].Value == other.UIndex) connections.RemoveAt(i);
            }
        }
        cluster.WriteProperties(properties);
    }

    private static bool HasRelayConnection(ExportEntry cluster, int otherUIndex) =>
        cluster.GetProperty<ArrayProperty<ObjectProperty>>("RelayConnections")?.Any(reference => reference.Value == otherUIndex) == true;

    private IEnumerable<SFXGalaxyNode> GetRelayConnections(SFXGalaxyNode cluster)
    {
        ArrayProperty<ObjectProperty> connections = cluster.Export.GetProperty<ArrayProperty<ObjectProperty>>("RelayConnections");
        return connections?.Select(reference => _nodesByUIndex.GetValueOrDefault(reference.Value)).Where(node => node is not null)
               ?? [];
    }

    private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SFXGalaxyNode node)
        {
            SelectedNode = node;
        }
    }

    private void HierarchyTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HierarchyTree.SelectedItem is SFXGalaxyNode node)
        {
            NavigateTo(node);
            e.Handled = true;
        }
    }

    private void HierarchyTree_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject) is not { DataContext: SFXGalaxyNode node } item)
        {
            return;
        }
        item.IsSelected = true;
        SelectedNode = node;
        ContextMenu menu = BuildObjectContextMenu(node);
        item.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static T FindVisualParent<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }
        return null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        SearchResults.ClearEx();
        if (_rootNode is null || query.Length < 2)
        {
            SearchResultsList.Visibility = Visibility.Collapsed;
            return;
        }
        SearchResults.AddRange(_rootNode.SelfAndDescendants()
            .Where(node => node.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100));
        SearchResultsList.Visibility = SearchResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchResultsList.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.Key == Key.Enter)
        {
            SFXGalaxyNode node = SearchResultsList.SelectedItem as SFXGalaxyNode ?? SearchResults.FirstOrDefault();
            if (node is not null)
            {
                NavigateToSearchResult(node);
                e.Handled = true;
            }
        }
    }

    private void SearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is SFXGalaxyNode node && SearchResultsList.IsKeyboardFocusWithin)
        {
            NavigateToSearchResult(node);
        }
    }

    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is SFXGalaxyNode node)
        {
            NavigateToSearchResult(node);
        }
    }

    private void NavigateToSearchResult(SFXGalaxyNode node)
    {
        CurrentNode = FindOwningView(node);
        SelectedNode = node;
        SearchResultsList.Visibility = Visibility.Collapsed;
        RenderCurrentLevel();
        SelectTreeNode(node);
    }

    private static SFXGalaxyNode FindOwningView(SFXGalaxyNode node)
    {
        for (SFXGalaxyNode current = node; current is not null; current = current.Parent)
        {
            if (current.Kind is SFXGalaxyNodeKind.Galaxy or SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System)
            {
                return current == node && current.Parent is not null ? current.Parent : current;
            }
        }
        return node;
    }

    private void RefreshPropertyExports()
    {
        EditableExports.ClearEx();
        PropertiesInterpreter?.UnloadExport();
        MetadataLoader?.UnloadExport();
        if (SelectedNode?.Export is not ExportEntry export)
        {
            return;
        }

        string label = SelectedNode.IsImplicitStar ? "SFXSystem (star properties)" : $"Object: {export.ObjectName.Instanced}";
        EditableExports.Add(new SFXGalaxyEditableExport(export, label));
        if (!SelectedNode.IsImplicitStar && export.GetProperty<ObjectProperty>("Appearance")?.ResolveToEntry(Pcc) is ExportEntry appearance)
        {
            EditableExports.Add(new SFXGalaxyEditableExport(appearance, $"Appearance: {appearance.ObjectName.Instanced}"));
        }
        EditableExportCombo.SelectedIndex = 0;
    }

    private static bool HasPlanetMaterialReference(ExportEntry planet)
    {
        PropertyCollection properties = planet?.GetProperties();
        return properties?.GetProp<ObjectProperty>("PlanetMaterial")?.Value != 0
               || properties?.GetProp<ObjectProperty>("CloudMaterial")?.Value != 0;
    }

    private void OpenPlanetMaterialEditor_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenPlanetMaterialEditor)
        {
            return;
        }

        IsPlanetMaterialEditorOpen = true;
        RefreshPlanetMaterialSlots();
    }

    private void ClosePlanetMaterialEditor_Click(object sender, RoutedEventArgs e) => ClosePlanetMaterialEditor();

    private void ClosePlanetMaterialEditor()
    {
        IsPlanetMaterialEditorOpen = false;
        PlanetMaterialMeshViewer?.UnloadExport();
        if (PlanetMaterialMeshViewer is not null)
        {
            PlanetMaterialMeshViewer.OverlayMaterials = null;
            PlanetMaterialMeshViewer.LiveMaterialSourceOverrides = null;
        }
        PlanetMaterialSlots.ClearEx();
        SelectedPlanetMaterialSlot = null;
    }

    private void RefreshPlanetMaterialSlots()
    {
        string selectedProperty = SelectedPlanetMaterialSlot?.PropertyName;
        SelectedPlanetMaterialSlot = null;
        PlanetMaterialSlots.ClearEx();

        if (SelectedNode?.Export is not { } planet || !planet.IsA("BioPlanet"))
        {
            return;
        }

        PropertyCollection properties = planet.GetProperties();
        if (properties.GetProp<ObjectProperty>("PlanetMaterial")?.Value != 0)
        {
            PlanetMaterialSlots.Add(new SFXGalaxyPlanetMaterialSlot("PlanetMaterial", "Planet surface", PlanetMeshPath));
        }
        if (properties.GetProp<ObjectProperty>("CloudMaterial")?.Value != 0)
        {
            PlanetMaterialSlots.Add(new SFXGalaxyPlanetMaterialSlot("CloudMaterial", "Cloud layer", CloudMeshPath));
        }

        SelectedPlanetMaterialSlot = PlanetMaterialSlots.FirstOrDefault(slot =>
                                         slot.PropertyName.Equals(selectedProperty, StringComparison.OrdinalIgnoreCase))
                                     ?? PlanetMaterialSlots.FirstOrDefault();
    }

    private ExportEntry ResolveSelectedPlanetMaterial(out ObjectProperty materialReference)
    {
        materialReference = null;
        if (SelectedNode?.Export is not { } planet || SelectedPlanetMaterialSlot is not { } slot)
        {
            return null;
        }

        materialReference = planet.GetProperty<ObjectProperty>(slot.PropertyName);
        return materialReference?.ResolveToExport(Pcc, _texturePackageCache);
    }

    private void LoadSelectedPlanetMaterialPreview()
    {
        PlanetMaterialMeshViewer?.UnloadExport();
        if (!IsPlanetMaterialEditorOpen || _galaxyArtPcc is null || SelectedPlanetMaterialSlot is not { } slot)
        {
            return;
        }

        ExportEntry material = ResolveSelectedPlanetMaterial(out _);
        ExportEntry mesh = _galaxyArtPcc.FindExport(slot.MeshPath, "StaticMesh");
        if (material is null || mesh is null)
        {
            MessageBox.Show(this,
                material is null
                    ? $"The selected planet's {slot.PropertyName} reference could not be resolved."
                    : $"{_packageSet.GalaxyArtFile} does not contain the preview mesh {slot.MeshPath}.",
                "Planet material preview", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PlanetMaterialMeshViewer.LiveMaterialSourceOverrides = [material];
        PlanetMaterialMeshViewer.OverlayMaterials = [material];
        PlanetMaterialMeshViewer.LoadExport(mesh);
    }

    private async Task RandomizePlanetMaterialScalars(LiveMaterialEditorMaterial material)
    {
        if (SelectedNode?.Export is not { } planet || SelectedPlanetMaterialSlot is not { } slot)
        {
            return;
        }

        BioPlanetReferenceCatalog catalog = await LoadBioPlanetRandomizationCatalog();
        if (catalog is null || !ReferenceEquals(PlanetMaterialMeshViewer.SelectedLiveMaterial, material)
                            || SelectedNode?.Export != planet || SelectedPlanetMaterialSlot != slot)
        {
            return;
        }

        BioPlanetMaterialLayer layer = GetMaterialLayer(slot);
        HashSet<string> targetNames = material.ScalarParameters.Select(parameter => parameter.ParameterName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<BioPlanetReferenceProfile> profiles = catalog.Profiles
            .Where(profile => profile.Layer == layer && profile.Scalars.Keys.Any(targetNames.Contains))
            .ToList();
        if (profiles.Count == 0)
        {
            ShowMissingBioPlanetProfiles(slot, "scalar");
            return;
        }

        (BioPlanetReferenceProfile first, BioPlanetReferenceProfile second, float firstWeight, float secondWeight) =
            PickBioPlanetProfiles(profiles);
        int changed = 0;
        foreach (LiveScalarMaterialParameter parameter in material.ScalarParameters)
        {
            bool hasFirst = first.Scalars.TryGetValue(parameter.ParameterName, out float firstValue);
            bool hasSecond = second.Scalars.TryGetValue(parameter.ParameterName, out float secondValue);
            if (!hasFirst && !hasSecond)
            {
                continue;
            }

            parameter.Value = hasFirst && hasSecond
                ? firstValue * firstWeight + secondValue * secondWeight
                : hasFirst ? firstValue : secondValue;
            changed++;
        }
        StatusText = $"Randomized {changed} {slot.DisplayName.ToLowerInvariant()} scalar parameters by blending official BioPlanet profiles.";
    }

    private async Task RandomizePlanetMaterialVectors(LiveMaterialEditorMaterial material)
    {
        if (SelectedNode?.Export is not { } planet || SelectedPlanetMaterialSlot is not { } slot)
        {
            return;
        }

        BioPlanetReferenceCatalog catalog = await LoadBioPlanetRandomizationCatalog();
        if (catalog is null || !ReferenceEquals(PlanetMaterialMeshViewer.SelectedLiveMaterial, material)
                            || SelectedNode?.Export != planet || SelectedPlanetMaterialSlot != slot)
        {
            return;
        }

        BioPlanetMaterialLayer layer = GetMaterialLayer(slot);
        HashSet<string> targetNames = material.VectorParameters.Select(parameter => parameter.ParameterName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<BioPlanetReferenceProfile> profiles = catalog.Profiles
            .Where(profile => profile.Layer == layer && profile.Vectors.Keys.Any(targetNames.Contains))
            .ToList();
        if (profiles.Count == 0)
        {
            ShowMissingBioPlanetProfiles(slot, "vector");
            return;
        }

        (BioPlanetReferenceProfile first, BioPlanetReferenceProfile second, float firstWeight, float secondWeight) =
            PickBioPlanetProfiles(profiles);
        int changed = 0;
        foreach (LiveVectorMaterialParameter parameter in material.VectorParameters)
        {
            bool hasFirst = first.Vectors.TryGetValue(parameter.ParameterName, out BioPlanetReferenceVector firstValue);
            bool hasSecond = second.Vectors.TryGetValue(parameter.ParameterName, out BioPlanetReferenceVector secondValue);
            if (!hasFirst && !hasSecond)
            {
                continue;
            }

            BioPlanetReferenceVector value = hasFirst && hasSecond
                ? new BioPlanetReferenceVector(
                    firstValue.R * firstWeight + secondValue.R * secondWeight,
                    firstValue.G * firstWeight + secondValue.G * secondWeight,
                    firstValue.B * firstWeight + secondValue.B * secondWeight,
                    firstValue.A * firstWeight + secondValue.A * secondWeight)
                : hasFirst ? firstValue : secondValue;
            parameter.SetValue(value.R, value.G, value.B, value.A);
            changed++;
        }
        StatusText = $"Randomized {changed} {slot.DisplayName.ToLowerInvariant()} vector parameters by blending official BioPlanet profiles.";
    }

    private async Task<BioPlanetReferenceCatalog> LoadBioPlanetRandomizationCatalog()
    {
        IsBusy = true;
        BusyText = "Loading official BioPlanet material profiles from the LE3 Asset Database...";
        try
        {
            BioPlanetReferenceCatalog catalog = await BioPlanetRandomizationCatalog.GetCatalogAsync(MEGame.LE3);
            if (!string.IsNullOrWhiteSpace(catalog.Error))
            {
                MessageBox.Show(this, catalog.Error, "BioPlanet material randomization",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return catalog;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static BioPlanetMaterialLayer GetMaterialLayer(SFXGalaxyPlanetMaterialSlot slot) =>
        slot.PropertyName.Equals("CloudMaterial", StringComparison.OrdinalIgnoreCase)
            ? BioPlanetMaterialLayer.Cloud
            : BioPlanetMaterialLayer.Planet;

    private static (BioPlanetReferenceProfile First, BioPlanetReferenceProfile Second,
        float FirstWeight, float SecondWeight) PickBioPlanetProfiles(IReadOnlyList<BioPlanetReferenceProfile> profiles)
    {
        BioPlanetReferenceProfile first = profiles[Random.Shared.Next(profiles.Count)];
        BioPlanetReferenceProfile second = first;
        if (profiles.Count > 1)
        {
            do
            {
                second = profiles[Random.Shared.Next(profiles.Count)];
            } while (ReferenceEquals(first, second));
        }

        float firstWeight = profiles.Count > 1 ? 0.4f + Random.Shared.NextSingle() * 0.2f : 1f;
        return (first, second, firstWeight, 1f - firstWeight);
    }

    private void ShowMissingBioPlanetProfiles(SFXGalaxyPlanetMaterialSlot slot, string parameterType)
    {
        MessageBox.Show(this,
            $"The LE3 Asset Database has no official {slot.DisplayName.ToLowerInvariant()} profiles matching this material's {parameterType} parameter names.",
            "BioPlanet material randomization", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool SavePlanetMaterialToCurrent(LiveMaterialEditorMaterial material)
    {
        ExportEntry currentMaterial = ResolveSelectedPlanetMaterial(out _)
            ?? throw new InvalidOperationException("The selected BioPlanet material reference can no longer be resolved.");
        if (material.SourceEntry is not ExportEntry source || currentMaterial != source)
        {
            throw new InvalidOperationException("The previewed material is no longer assigned to this planet layer.");
        }
        if (source.FileRef != Pcc || !source.IsA("MaterialInstanceConstant"))
        {
            throw new InvalidOperationException("Only a MaterialInstanceConstant stored in the authoritative galaxy map package can be overwritten.");
        }

        _suppressPackageRefresh = true;
        try
        {
            MeshRenderer.WriteLiveMaterialParameters(source, material);
            if (SynchronizeCompanionFromAuthoritative(fullHierarchy: false, [source], showErrors: true,
                    includeExplicitExternalExports: true) && _packageSet.SynchronizesSecondary)
            {
                CompanionSyncStatus = $"Updated {source.InstancedFullPath} and mirrored its serialized MIC parameters.";
            }
            UpdateStatus();
            return true;
        }
        finally
        {
            _suppressPackageRefresh = false;
        }
    }

    private bool SavePlanetMaterialAsNew(LiveMaterialEditorMaterial material)
    {
        if (SelectedNode?.Export is not { } planet || SelectedPlanetMaterialSlot is not { } slot)
        {
            throw new InvalidOperationException("Select a BioPlanet material layer before creating a MIC.");
        }
        ExportEntry source = ResolveSelectedPlanetMaterial(out ObjectProperty sourceReference)
            ?? throw new InvalidOperationException("The selected BioPlanet material reference can no longer be resolved.");
        if (material.SourceEntry is not ExportEntry previewSource || source != previewSource)
        {
            throw new InvalidOperationException("The previewed material is no longer assigned to this planet layer.");
        }

        string defaultName = $"{source.ObjectName.Name}_Edited";
        string newName = PromptDialog.Prompt(this,
            "Name the new MaterialInstanceConstant:",
            $"Create {slot.DisplayName} MIC",
            defaultName,
            selectText: true,
            validator: value => ValidatePlanetMaterialName(planet, value));
        if (newName is null)
        {
            return false;
        }

        ExportEntry newMaterial = null;
        bool planetRepointed = false;
        _suppressPackageRefresh = true;
        try
        {
            newMaterial = Pcc.CreateExport(new NameReference(newName.Trim()),
                "MaterialInstanceConstant", planet, indexed: false);
            newMaterial.WriteProperties(new PropertyCollection
            {
                new ObjectProperty(sourceReference.Value, "Parent"),
                CommonStructs.GuidProp(Guid.NewGuid(), "m_Guid")
            });
            MeshRenderer.WriteLiveMaterialParameters(newMaterial, material);

            PropertyCollection planetProperties = planet.GetProperties();
            planetProperties.AddOrReplaceProp(new ObjectProperty(newMaterial, slot.PropertyName));
            planet.WriteProperties(planetProperties);
            planetRepointed = true;

            bool synchronized = SynchronizeCompanionFromAuthoritative(fullHierarchy: true, changedExports: null, showErrors: true);
            RefreshPropertyExports();
            if (synchronized && _packageSet.SynchronizesSecondary)
            {
                CompanionSyncStatus = $"Created {newMaterial.InstancedFullPath}, repointed {slot.PropertyName}, and synchronized the galaxy hierarchy.";
            }
            UpdateStatus();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshPlanetMaterialSlots));
            return true;
        }
        catch
        {
            if (newMaterial is not null && !planetRepointed)
            {
                EntryPruner.TrashEntryAndDescendants(newMaterial);
            }
            throw;
        }
        finally
        {
            _suppressPackageRefresh = false;
        }
    }

    private static (bool, string) ValidatePlanetMaterialName(ExportEntry planet, string value)
    {
        string name = value?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return (false, "Enter a material name.");
        }
        if (!(char.IsLetter(name[0]) || name[0] == '_')
            || name.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            return (false, "Use letters, numbers, and underscores; the first character cannot be a number.");
        }

        string path = $"{planet.InstancedFullPath}.{name}";
        return planet.FileRef.FindEntry(path) is null
            ? (true, null)
            : (false, "An entry with that name already exists under this planet.");
    }

    private void EditableExportCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PropertiesInterpreter.UnloadExport();
        MetadataLoader.UnloadExport();
        if (EditableExportCombo.SelectedItem is SFXGalaxyEditableExport selected)
        {
            PropertiesInterpreter.LoadExport(selected.Export);
            MetadataLoader.LoadExport(selected.Export);
        }
    }

    private void AddCluster_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Cluster);
    private void AddSystem_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.System);
    private void AddPlanet_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Planet);
    private void AddAsteroidBelt_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.AsteroidBelt);
    private void AddAnomaly_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Anomaly);
    private void AddMassRelay_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.MassRelay);
    private void AddFuelDepot_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.FuelDepot);
    private void AddReaper_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Reaper);
    private void AddFeature_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.Feature);
    private void AddWarAsset_Click(object sender, RoutedEventArgs e) => AddKnownKind(SFXGalaxyNodeKind.WarAsset);

    private void AddKnownKind(SFXGalaxyNodeKind kind)
    {
        if (_rootNode is null || !IsObjectKindSupported(kind))
        {
            return;
        }
        SFXGalaxyNode parent = FindCreationParent(kind);
        if (parent is null)
        {
            MessageBox.Show(this, CreationParentMessage(kind), "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CreateCustomObject(kind, parent, null);
    }

    private void AddForSelection_Click(object sender, RoutedEventArgs e)
    {
        SFXGalaxyNode context = ResolveAddContext(SelectedNode ?? CurrentNode);
        ContextMenu menu = BuildAddMenu(context, null);
        if (menu.Items.Count == 0)
        {
            MessageBox.Show(this, "Select the galaxy, a cluster, a system, or a planet before adding an object.",
                "SFXGalaxy Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void DuplicateSelected()
    {
        if (SelectedNode is { Parent: not null, IsImplicitStar: false } selected)
        {
            CloneObject(selected, selected.Parent);
        }
    }

    private void CreateCustomObject(SFXGalaxyNodeKind kind, SFXGalaxyNode parent, Point? requestedPosition)
    {
        string kindName = KindName(kind);
        string className = ClassNameForKind(kind);
        string label = PromptDialog.Prompt(this, "Custom object name:", $"Add {kindName}", $"New {kindName}", true)?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }
        int displayNameStringRef = 0;
        if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "DisplayName", className) is not null)
        {
            string stringRefText = PromptDialog.Prompt(this,
                "DisplayName TLK StringRef ID (leave blank or use 0 to edit it later in Properties):",
                $"Add {kindName}", "0", true);
            if (stringRefText is null)
            {
                return;
            }
            int.TryParse(stringRefText, out displayNameStringRef);
        }

        ExportEntry created = null;
        bool parentReferencesAdded = false;
        int currentIndex = CurrentNode?.Export.UIndex ?? parent.Export.UIndex;
        _suppressPackageRefresh = true;
        try
        {
            string objectName = MakeObjectName(label, kind);
            created = Pcc.CreateExport(objectName, className, parent.Export, indexed: true);
            PropertyCollection properties = CreateInitialProperties(kind, label, displayNameStringRef, parent, requestedPosition);

            if (Pcc.Game == MEGame.LE3
                && kind is SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly)
            {
                ExportEntry appearance = Pcc.CreateExport($"{objectName}_Appearance", "SFXGalaxyMapPlanetAppearance", created, indexed: true);
                appearance.WriteProperties(new PropertyCollection());
                AddSerializedProperty(properties, "BioPlanet", new ObjectProperty(appearance, "Appearance"));
            }

            created.WriteProperties(properties);
            AddChildReferences(parent, created);
            parentReferencesAdded = true;
            RebuildHierarchy(currentIndex, created.UIndex);
            SynchronizeCompanionFromAuthoritative(fullHierarchy: true, changedExports: null, showErrors: false);
            if (_nodesByUIndex.TryGetValue(created.UIndex, out SFXGalaxyNode createdNode))
            {
                SelectTreeNode(createdNode);
            }
        }
        catch (Exception exception)
        {
            if (created is not null)
            {
                if (parentReferencesAdded)
                {
                    RemoveChildReferences(parent.Export, created);
                }
                EntryPruner.TrashEntryAndDescendants(created);
            }
            MessageBox.Show(this, $"Could not create this custom object:\n\n{exception.Message}", "SFXGalaxy Editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _suppressPackageRefresh = false;
        }
    }

    private PropertyCollection CreateInitialProperties(SFXGalaxyNodeKind kind, string label, int displayNameStringRef,
        SFXGalaxyNode parent, Point? requestedPosition)
    {
        int ordinal = parent.Children.Count(child => !child.IsImplicitStar);
        Point position = requestedPosition ?? new Point(
            Math.Clamp(256 + ordinal * 73 % 640, 0, MapExtent),
            Math.Clamp(300 + ordinal * 109 % 560, 0, MapExtent));
        string className = ClassNameForKind(kind);
        PropertyCollection properties = [];
        AddSerializedProperty(properties, className, new IntProperty((int)Math.Round(position.X), "PosX"));
        AddSerializedProperty(properties, className, new IntProperty((int)Math.Round(position.Y), "PosY"));
        if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "DisplayName", className) is not null)
        {
            AddSerializedProperty(properties, className, new StringRefProperty(displayNameStringRef, "DisplayName"));
        }

        if (kind == SFXGalaxyNodeKind.WarAsset)
        {
            AddSerializedProperty(properties, className, new StrProperty(label, "AssetName"));
        }

        if (kind is SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly)
        {
            AddSerializedProperty(properties, className, new EnumProperty(
                kind == SFXGalaxyNodeKind.Planet ? "SL_PLANET" : "SL_ANOMALY",
                "ESystemLevelType", Pcc.Game, "SystemLevelType"));
            AddSerializedProperty(properties, className, new EnumProperty(
                kind switch
                {
                    SFXGalaxyNodeKind.Planet => "OR_ORBIT",
                    SFXGalaxyNodeKind.AsteroidBelt => "OR_ASTEROID",
                    _ => "OR_NONE"
                }, "EOrbitRingType", Pcc.Game, "OrbitRing"));
        }
        return properties;
    }

    private void AddSerializedProperty(PropertyCollection properties, string className, Property property)
    {
        if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, property.Name, className) is null)
        {
            throw new InvalidOperationException($"{property.Name} is not a serialized {className} property in {Pcc.Game} metadata.");
        }
        properties.AddOrReplaceProp(property);
    }

    private static string ClassNameForKind(SFXGalaxyNodeKind kind) => kind switch
    {
        SFXGalaxyNodeKind.Cluster => "SFXCluster",
        SFXGalaxyNodeKind.System => "SFXSystem",
        SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly => "BioPlanet",
        SFXGalaxyNodeKind.MassRelay => "SFXGalaxyMapMassRelay",
        SFXGalaxyNodeKind.FuelDepot => "SFXGalaxyMapDestroyedFuelDepot",
        SFXGalaxyNodeKind.Reaper => "SFXGalaxyMapReaper",
        SFXGalaxyNodeKind.Feature => "SFXPlanetFeature",
        SFXGalaxyNodeKind.WarAsset => "SFXPlanetFeatureGAWAsset",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This galaxy map object type cannot be created.")
    };

    private static string MakeObjectName(string label, SFXGalaxyNodeKind kind)
    {
        char[] characters = label.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray();
        string sanitized = new string(characters).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? $"Custom{kind}" : sanitized;
    }

    private void CloneObject(SFXGalaxyNode template, SFXGalaxyNode parent)
    {
        string className = template.Export.ClassName;
        string label = PromptDialog.Prompt(this, "Custom name for the clone:", "Clone galaxy map object",
            $"{template.DisplayName} Copy", true)?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }
        int displayNameStringRef = 0;
        if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "DisplayName", className) is not null)
        {
            string stringRefText = PromptDialog.Prompt(this,
                "DisplayName TLK StringRef ID (leave blank or use 0 to use the custom name):",
                "Clone galaxy map object", "0", true);
            if (stringRefText is null)
            {
                return;
            }
            int.TryParse(stringRefText, out displayNameStringRef);
        }

        ExportEntry clone = null;
        bool parentReferencesAdded = false;
        _suppressPackageRefresh = true;
        try
        {
            clone = EntryCloner.CloneEntry(template.Export, incrementIndex: true, newParentUIndex: parent.Export.UIndex);
            clone.ObjectName = Pcc.GetNextIndexedName(MakeObjectName(label, template.Kind));
            PropertyCollection cloneProperties = clone.GetProperties();
            ResetClonedObjectProperties(cloneProperties, template.Kind, Pcc.Game);
            int ordinal = parent.Children.Count(child => !child.IsImplicitStar);
            if (template.Kind == SFXGalaxyNodeKind.WarAsset)
            {
                AddSerializedProperty(cloneProperties, className, new StrProperty(label, "AssetName"));
            }
            AddSerializedProperty(cloneProperties, className,
                new IntProperty(Math.Clamp(256 + ordinal * 73 % 640, 0, MapExtent), "PosX"));
            AddSerializedProperty(cloneProperties, className,
                new IntProperty(Math.Clamp(300 + ordinal * 109 % 560, 0, MapExtent), "PosY"));
            if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "DisplayName", className) is not null)
            {
                AddSerializedProperty(cloneProperties, className, new StringRefProperty(displayNameStringRef, "DisplayName"));
            }
            clone.WriteProperties(cloneProperties);
            CloneOwnedAppearance(template.Export, clone);
            AddChildReferences(parent, clone);
            parentReferencesAdded = true;
            int currentIndex = CurrentNode?.Export.UIndex ?? parent.Export.UIndex;
            RebuildHierarchy(currentIndex, clone.UIndex);
            SynchronizeCompanionFromAuthoritative(fullHierarchy: true, changedExports: null, showErrors: false);
            if (_nodesByUIndex.TryGetValue(clone.UIndex, out SFXGalaxyNode created))
            {
                SelectTreeNode(created);
            }
        }
        catch (Exception exception)
        {
            if (clone is not null)
            {
                if (parentReferencesAdded)
                {
                    RemoveChildReferences(parent.Export, clone);
                }
                EntryPruner.TrashEntryAndDescendants(clone);
            }
            MessageBox.Show(this, $"Could not clone this object:\n\n{exception.Message}", "SFXGalaxy Editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _suppressPackageRefresh = false;
        }
    }

    private static void ResetClonedObjectProperties(PropertyCollection properties, SFXGalaxyNodeKind kind, MEGame game)
    {
        string[] arrayNames = game == MEGame.LE2
            ? kind switch
            {
                SFXGalaxyNodeKind.Cluster => ["Systems", "RelayConnections"],
                SFXGalaxyNodeKind.System => ["Planets"],
                SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly => ["Features"],
                _ => []
            }
            : kind switch
            {
                SFXGalaxyNodeKind.Cluster => ["Children", "Systems", "RelayConnections"],
                SFXGalaxyNodeKind.System => ["Children", "Planets"],
                SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly => ["Children"],
                _ => []
            };
        foreach (string arrayName in arrayNames)
        {
            properties.GetProp<ArrayProperty<ObjectProperty>>(arrayName)?.Clear();
        }
    }

    private void CloneOwnedAppearance(ExportEntry template, ExportEntry clone)
    {
        if (template.GetProperty<ObjectProperty>("Appearance")?.ResolveToEntry(Pcc) is not ExportEntry appearance
            || appearance.Parent != template || appearance.IsDefaultObject)
        {
            return;
        }
        ExportEntry appearanceClone = EntryCloner.CloneEntry(appearance, incrementIndex: true, newParentUIndex: clone.UIndex);
        clone.WriteProperty(new ObjectProperty(appearanceClone, "Appearance"));
    }

    private static void AddChildReferences(SFXGalaxyNode parent, ExportEntry child)
    {
        PropertyCollection parentProperties = parent.Export.GetProperties();
        if (parent.Export.Game == MEGame.LE2)
        {
            string typedArrayName = GetLE2ChildArrayName(parent.Kind);
            ArrayProperty<ObjectProperty> typed = GetOrCreateSerializedObjectArray(parent.Export, parentProperties, typedArrayName);
            if (typed.All(reference => reference.Value != child.UIndex))
            {
                typed.Add(new ObjectProperty(child));
            }
            parent.Export.WriteProperties(parentProperties);
            return;
        }

        // Preserve the existing LE3 behavior: Children owns the hierarchy while the typed arrays
        // remain sparse tables indexed by the game's table IDs.
        ArrayProperty<ObjectProperty> children = GetOrCreateSerializedObjectArray(parent.Export, parentProperties, "Children");
        if (children.All(reference => reference.Value != child.UIndex))
        {
            children.Add(new ObjectProperty(child));
        }
        string le3TypedArrayName = parent.Kind switch
        {
            SFXGalaxyNodeKind.Galaxy when child.ClassName == "SFXCluster" => "Clusters",
            SFXGalaxyNodeKind.Cluster when child.ClassName == "SFXSystem" => "Systems",
            // LE3's Planets array is really the table for every SFXSystemLevelObject,
            // including relays, depots, Reapers, and anomalies—not only BioPlanet.
            SFXGalaxyNodeKind.System => "Planets",
            _ => null
        };
        if (le3TypedArrayName is not null)
        {
            ArrayProperty<ObjectProperty> typed = GetOrCreateSerializedObjectArray(parent.Export, parentProperties, le3TypedArrayName);
            if (typed.All(reference => reference.Value != child.UIndex))
            {
                typed.Add(new ObjectProperty(child));
            }
        }
        parent.Export.WriteProperties(parentProperties);
    }

    private static string GetLE2ChildArrayName(SFXGalaxyNodeKind parentKind) => parentKind switch
    {
        SFXGalaxyNodeKind.Galaxy => "Clusters",
        SFXGalaxyNodeKind.Cluster => "Systems",
        SFXGalaxyNodeKind.System => "Planets",
        SFXGalaxyNodeKind.Planet or SFXGalaxyNodeKind.AsteroidBelt or SFXGalaxyNodeKind.Anomaly => "Features",
        _ => throw new InvalidOperationException($"{parentKind} cannot contain an LE2 galaxy map child.")
    };

    private static ArrayProperty<ObjectProperty> GetOrCreateSerializedObjectArray(ExportEntry owner,
        PropertyCollection properties, string propertyName)
    {
        if (GlobalUnrealObjectInfo.GetPropertyInfo(owner.Game, propertyName, owner.ClassName, containingExport: owner) is null)
        {
            throw new InvalidOperationException($"{propertyName} is not a serialized {owner.ClassName} property in {owner.Game} metadata.");
        }
        ArrayProperty<ObjectProperty> array = properties.GetProp<ArrayProperty<ObjectProperty>>(propertyName);
        if (array is null)
        {
            array = new ArrayProperty<ObjectProperty>(propertyName);
            properties.Add(array);
        }
        return array;
    }

    private void DeleteSelected()
    {
        if (SelectedNode is not { Parent: not null, IsImplicitStar: false } target)
        {
            return;
        }
        int descendantCount = target.SelfAndDescendants().Count(node => !node.IsImplicitStar) - 1;
        string detail = descendantCount > 0 ? $" and its {descendantCount} descendant objects" : string.Empty;
        if (MessageBox.Show(this, $"Delete {target.DisplayName}{detail}?\n\nThe exports will be moved to the package Trash tree.",
                "Delete galaxy map object", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SFXGalaxyNode parent = target.Parent;
        _suppressPackageRefresh = true;
        try
        {
            if (target.Kind == SFXGalaxyNodeKind.Cluster)
            {
                foreach (SFXGalaxyNode connected in GetRelayConnections(target).ToList())
                {
                    WriteRelayReference(connected.Export, target.Export, false);
                }
            }
            RemoveChildReferences(parent.Export, target.Export);
            EntryPruner.TrashEntryAndDescendants(target.Export);
            RebuildHierarchy(parent.Export.UIndex, parent.Export.UIndex);
            SynchronizeCompanionFromAuthoritative(fullHierarchy: true, changedExports: null, showErrors: false);
        }
        finally
        {
            _suppressPackageRefresh = false;
        }
    }

    private static void RemoveChildReferences(ExportEntry parent, ExportEntry child)
    {
        PropertyCollection properties = parent.GetProperties();
        if (parent.Game == MEGame.LE2)
        {
            foreach (string arrayName in new[] { "Clusters", "Systems", "Planets", "Features" })
            {
                if (properties.GetProp<ArrayProperty<ObjectProperty>>(arrayName) is not { } array) continue;
                foreach (ObjectProperty reference in array.Where(reference => reference.Value == child.UIndex))
                {
                    reference.Value = 0;
                }
            }
            parent.WriteProperties(properties);
            return;
        }
        foreach (string arrayName in new[] { "Children" })
        {
            if (properties.GetProp<ArrayProperty<ObjectProperty>>(arrayName) is not { } array) continue;
            for (int i = array.Count - 1; i >= 0; i--)
            {
                if (array[i].Value == child.UIndex) array.RemoveAt(i);
            }
        }
        foreach (string arrayName in new[] { "Clusters", "Systems", "Planets" })
        {
            if (properties.GetProp<ArrayProperty<ObjectProperty>>(arrayName) is not { } array) continue;
            foreach (ObjectProperty reference in array.Where(reference => reference.Value == child.UIndex))
            {
                reference.Value = 0;
            }
        }
        parent.WriteProperties(properties);
    }

    private SFXGalaxyNode FindCreationParent(SFXGalaxyNodeKind kind) => kind switch
    {
        SFXGalaxyNodeKind.Cluster => _rootNode,
        SFXGalaxyNodeKind.System => FindContextNode(SFXGalaxyNodeKind.Cluster),
        SFXGalaxyNodeKind.Feature or SFXGalaxyNodeKind.WarAsset =>
            FindContextNode(SFXGalaxyNodeKind.Planet, SFXGalaxyNodeKind.Anomaly, SFXGalaxyNodeKind.AsteroidBelt),
        _ => FindContextNode(SFXGalaxyNodeKind.System)
    };

    private SFXGalaxyNode FindContextNode(params SFXGalaxyNodeKind[] kinds)
    {
        for (SFXGalaxyNode node = SelectedNode ?? CurrentNode; node is not null; node = node.Parent)
        {
            if (kinds.Contains(node.Kind)) return node;
        }
        for (SFXGalaxyNode node = CurrentNode; node is not null; node = node.Parent)
        {
            if (kinds.Contains(node.Kind)) return node;
        }
        return null;
    }

    private static string CreationParentMessage(SFXGalaxyNodeKind kind) => kind switch
    {
        SFXGalaxyNodeKind.System => "Select a cluster before adding a system.",
        SFXGalaxyNodeKind.Feature => "Select a planet, asteroid belt, or anomaly before adding a scannable feature.",
        SFXGalaxyNodeKind.WarAsset => "Select a planet, asteroid belt, or anomaly before adding a war asset.",
        SFXGalaxyNodeKind.Cluster => "Open an SFXGalaxy package before adding a cluster.",
        _ => "Select a system before adding this object."
    };

    private void UpdateStatus()
    {
        if (Pcc is null)
        {
            StatusText = "Choose LE2 or LE3 highest-mounted galaxy map files to begin.";
            return;
        }
        string path = CurrentNode is null ? string.Empty : string.Join(" › ", GetPath(CurrentNode).Select(node => node.DisplayName));
        string selection = SelectedNode is null ? string.Empty : $"  |  Selected: {SelectedNode.DisplayName} ({SelectedNode.PosX}, {SelectedNode.PosY})";
        StatusText = $"{Path.GetFileName(Pcc.FilePath)}  |  {path}{selection}  |  {CompanionSyncStatus}";
    }

    private static IEnumerable<SFXGalaxyNode> GetPath(SFXGalaxyNode node)
    {
        Stack<SFXGalaxyNode> path = new();
        for (; node is not null; node = node.Parent) path.Push(node);
        return path;
    }

    private void SelectTreeNode(SFXGalaxyNode node)
    {
        if (HierarchyTree is null || node is null)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ExpandAncestors(node);
            TreeViewItem container = FindTreeViewItem(HierarchyTree, node);
            if (container is not null)
            {
                container.IsSelected = true;
                container.BringIntoView();
            }
        }));
    }

    private void ExpandAncestors(SFXGalaxyNode node)
    {
        foreach (SFXGalaxyNode ancestor in GetPath(node).TakeWhile(item => item != node))
        {
            if (FindTreeViewItem(HierarchyTree, ancestor) is TreeViewItem item)
            {
                item.IsExpanded = true;
                item.UpdateLayout();
            }
        }
    }

    private static TreeViewItem FindTreeViewItem(ItemsControl parent, object target)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem direct)
        {
            return direct;
        }
        foreach (object item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem child) continue;
            TreeViewItem result = FindTreeViewItem(child, target);
            if (result is not null) return result;
        }
        return null;
    }

    public override void HandleUpdate(List<PackageUpdate> updates)
    {
        if (_suppressPackageRefresh || Pcc is null)
        {
            return;
        }
        List<PackageUpdate> exportUpdates = updates.Where(update => update.Change.HasFlag(PackageChange.Export)).ToList();
        if (exportUpdates.Count == 0)
        {
            return;
        }
        foreach (PackageUpdate update in exportUpdates.Where(update => update.Index > 0))
        {
            _pendingCompanionSyncUIndexes.Add(update.Index);
        }
        _pendingCompanionFullSync |= exportUpdates.Any(update =>
            update.Change.HasFlag(PackageChange.Add) || update.Change.HasFlag(PackageChange.Remove));
        if (_refreshQueued)
        {
            return;
        }
        _refreshQueued = true;
        int currentIndex = CurrentNode?.Export?.UIndex ?? 0;
        int selectedIndex = SelectedNode?.Export?.UIndex ?? 0;
        bool selectedWasStar = SelectedNode?.IsImplicitStar == true;
        int editableExportIndex = (EditableExportCombo?.SelectedItem as SFXGalaxyEditableExport)?.Export.UIndex ?? 0;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            List<ExportEntry> changedExports = _pendingCompanionSyncUIndexes
                .Where(index => index <= Pcc.ExportCount)
                .Select(index => Pcc.GetUExport(index)).Where(export => export is not null).ToList();
            bool fullSync = _pendingCompanionFullSync;
            _pendingCompanionSyncUIndexes.Clear();
            _pendingCompanionFullSync = false;
            RebuildHierarchy(currentIndex, selectedIndex, selectedWasStar, editableExportIndex);
            SynchronizeCompanionFromAuthoritative(fullSync, changedExports, showErrors: false);
        }));
    }

    public void PropogateRecentsChange(string propogationToolSource, IEnumerable<RecentsControl.RecentItem> newRecents) =>
        RecentsController.PropogateRecentsChange(false, newRecents);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length != 1 || !files[0].EndsWith(".pcc", StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            LoadFile(files[0]);
        }
    }

    private static string KindName(SFXGalaxyNodeKind kind) => kind switch
    {
        SFXGalaxyNodeKind.AsteroidBelt => "Asteroid Belt",
        SFXGalaxyNodeKind.MassRelay => "Mass Relay",
        SFXGalaxyNodeKind.FuelDepot => "Fuel Depot",
        SFXGalaxyNodeKind.Feature => "Scannable Feature",
        SFXGalaxyNodeKind.WarAsset => "War Asset",
        _ => kind.ToString()
    };

    private static string ObjectNoun(int count) => count == 1 ? "object" : "objects";
    private static double MarkerSize(SFXGalaxyNode node) => node.Kind switch
    {
        SFXGalaxyNodeKind.Star => 42,
        SFXGalaxyNodeKind.Cluster => 28,
        SFXGalaxyNodeKind.System => 24,
        SFXGalaxyNodeKind.Planet => 22,
        SFXGalaxyNodeKind.AsteroidBelt => 15,
        _ => 18
    };
}
