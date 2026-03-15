using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.Extensions.Collections.Generic;
using Microsoft.Win32;
using AnimSequence = LegendaryExplorerCore.Unreal.BinaryConverters.AnimSequence;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.TLK;
using Microsoft.WindowsAPICodePack.Taskbar;
using BinaryPack;
using LegendaryExplorer.GameInterop;
using LegendaryExplorer.SharedUI.Controls;
using LegendaryExplorer.Tools.AssetDatabase.Filters;
using LegendaryExplorer.Tools.AssetViewer;
using LegendaryExplorer.Tools.LiveLevelEditor;
using LegendaryExplorer.Tools.PlotDatabase;
using LegendaryExplorerCore.Memory;
using LegendaryExplorerCore.PlotDatabase;
using TerraFX.Interop.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.AssetDatabase
{
    /// <summary>
    /// Interaction logic for AssetDB
    /// </summary>
    public partial class AssetDatabaseWindow : TrackingNotifyPropertyChangedWindowBase
    {
        #region Declarations
        // v9.2: Conversation records now store StartConversation owner metadata for lazy speaker resolution.
        public const string dbCurrentBuild = "9.2"; //If changes are made that invalidate old databases edit this.

        private int previousView { get; set; }
        private int _currentView;
        public int currentView
        {
            get => _currentView;
            set
            {
                previousView = _currentView;
                SetProperty(ref _currentView, value);
            }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }
        private string _busyText;
        public string BusyText
        {
            get => _busyText;
            set => SetProperty(ref _busyText, value);
        }
        private string _busyHeader;
        public string BusyHeader
        {
            get => _busyHeader;
            set => SetProperty(ref _busyHeader, value);
        }
        private bool _BusyBarInd;
        public bool BusyBarInd
        {
            get => _BusyBarInd;
            set => SetProperty(ref _BusyBarInd, value);
        }
        public MEGame currentGame;
        public MEGame CurrentGame
        {
            get => currentGame;
            set => SetProperty(ref currentGame, value);
        }

        private MELocalization _localization = MELocalization.INT;
        public MELocalization Localization
        {
            get => _localization;
            set => SetProperty(ref _localization, value);
        }

        public ObservableCollectionExtended<MELocalization> AvailableLocalizations { get; set; } = new()
        {
            MELocalization.INT,
            MELocalization.DEU,
            MELocalization.FRA,
            MELocalization.ITA,
            MELocalization.POL,
            MELocalization.RUS
        };

        private string CurrentDBPath { get; set; }
        public AssetDB CurrentDataBase { get; } = new();
        private readonly Dictionary<string, Conversation> _conversationLookup = new(StringComparer.OrdinalIgnoreCase);

        public static ConcurrentDictionary<ConversationKey, string> OwnerNameCache { get; } = new();

        private static readonly ConcurrentDictionary<ConversationKey, Lazy<string>> OwnerNameResolvers = new();
        private static readonly ConcurrentDictionary<string, Lazy<IMEPackage>> OwnerPackageCache = new(StringComparer.OrdinalIgnoreCase);

        public FileListSpecification FileListFilter { get; } = new();
        public AssetFilters AssetFilters { get; private set; }

        public ObservableCollectionExtended<FileDirPair> FileListExtended { get; } = new();
        public ObservableCollectionExtended<string> MeshTypeFilters { get; } = new()
        {
            AllMeshFilterOption,
            SkeletalMeshFilterOption,
            StaticMeshFilterOption
        };
        public ObservableCollectionExtended<string> VfxTypeFilters { get; } = new()
        {
            AllVfxFilterOption,
            ParticleSystemFilterOption,
            ClientEffectFilterOption
        };
        public ObservableCollectionExtended<string> AnimationTypeFilters { get; } = new()
        {
            AllAnimationFilterOption,
            NormalAnimationFilterOption,
            AmbientPerformanceFilterOption
        };
        public ObservableCollectionExtended<string> MaterialTypeFilters { get; } = new()
        {
            AllMaterialFilterOption,
            NormalMaterialFilterOption,
            MaterialInstanceConstantFilterOption
        };
        public ObservableCollectionExtended<string> MaterialTextureTypeFilters { get; } = new();
        public ObservableCollectionExtended<string> MaterialTextureCountFilters { get; } = new();
        public ObservableCollectionExtended<string> TextureTypeFilters { get; } = new();
        public ObservableCollectionExtended<string> TextureSizeFilters { get; } = new();
        public ObservableCollectionExtended<string> LineSearchColumns { get; } = new()
        {
            AllLineSearchColumnsOption,
            SpeakerLineSearchColumn,
            TlkStringRefLineSearchColumn,
            LineTextSearchColumn,
            LineConversationSearchColumn,
            FileLineSearchColumn,
            LocationLineSearchColumn
        };

        private const string AllMeshFilterOption = "All";
        private const string SkeletalMeshFilterOption = "Skeletal Meshes";
        private const string StaticMeshFilterOption = "Static Meshes";
        private const string AllVfxFilterOption = "All";
        private const string ParticleSystemFilterOption = "Particle Systems";
        private const string ClientEffectFilterOption = "Client Effects";
        private const string AllAnimationFilterOption = "All";
        private const string NormalAnimationFilterOption = "Normal Animations";
        private const string AmbientPerformanceFilterOption = "Ambient Performances";
        private const string AllMaterialFilterOption = "All";
        private const string NormalMaterialFilterOption = "Normal Materials";
        private const string MaterialInstanceConstantFilterOption = "MaterialInstanceConstant";
        private const string AllMaterialTextureTypeFilterOption = "All";
        private const string AllMaterialTextureCountFilterOption = "All";
        private const string AllTextureFilterOption = "All";
        private const string AllLineSearchColumnsOption = "All Columns";
        private const string SpeakerLineSearchColumn = "Speaker";
        private const string TlkStringRefLineSearchColumn = "TLK String Ref";
        private const string LineTextSearchColumn = "Line";
        private const string LineConversationSearchColumn = "Line Conversation";
        private const string FileLineSearchColumn = "File";
        private const string LocationLineSearchColumn = "Location";

        private string _selectedMeshTypeFilter = AllMeshFilterOption;
        public string SelectedMeshTypeFilter
        {
            get => _selectedMeshTypeFilter;
            set
            {
                if (SetProperty(ref _selectedMeshTypeFilter, value))
                {
                    Filter();
                }
            }
        }

        private string _selectedMaterialTextureTypeFilter = AllMaterialTextureTypeFilterOption;
        private bool _isRefreshingMaterialTextureFilters;
        public string SelectedMaterialTextureTypeFilter
        {
            get => _selectedMaterialTextureTypeFilter;
            set
            {
                if (SetProperty(ref _selectedMaterialTextureTypeFilter, value))
                {
                    if (!_isRefreshingMaterialTextureFilters)
                    {
                        RefreshMaterialTextureCountFilters();
                        Filter();
                    }
                }
            }
        }

        private string _selectedMaterialTextureCountFilter = AllMaterialTextureCountFilterOption;
        public string SelectedMaterialTextureCountFilter
        {
            get => _selectedMaterialTextureCountFilter;
            set
            {
                if (SetProperty(ref _selectedMaterialTextureCountFilter, value))
                {
                    if (!_isRefreshingMaterialTextureFilters)
                    {
                        Filter();
                    }
                }
            }
        }

        private string _selectedMaterialTypeFilter = AllMaterialFilterOption;
        public string SelectedMaterialTypeFilter
        {
            get => _selectedMaterialTypeFilter;
            set
            {
                if (SetProperty(ref _selectedMaterialTypeFilter, value))
                {
                    ApplyMaterialTypeFilter();
                    RefreshMaterialTextureDropdownFilters(preserveSelections: true);
                    Filter();
                }
            }
        }

        private string _selectedAnimationTypeFilter = AllAnimationFilterOption;
        public string SelectedAnimationTypeFilter
        {
            get => _selectedAnimationTypeFilter;
            set
            {
                if (SetProperty(ref _selectedAnimationTypeFilter, value))
                {
                    ApplyAnimationTypeFilter();
                    Filter();
                }
            }
        }

        private string _selectedVfxTypeFilter = AllVfxFilterOption;
        public string SelectedVfxTypeFilter
        {
            get => _selectedVfxTypeFilter;
            set
            {
                if (SetProperty(ref _selectedVfxTypeFilter, value))
                {
                    Filter();
                }
            }
        }

        private string _selectedTextureTypeFilter = AllTextureFilterOption;
        public string SelectedTextureTypeFilter
        {
            get => _selectedTextureTypeFilter;
            set
            {
                if (SetProperty(ref _selectedTextureTypeFilter, value))
                {
                    Filter();
                }
            }
        }

        private string _selectedTextureSizeFilter = AllTextureFilterOption;
        public string SelectedTextureSizeFilter
        {
            get => _selectedTextureSizeFilter;
            set
            {
                if (SetProperty(ref _selectedTextureSizeFilter, value))
                {
                    Filter();
                }
            }
        }

        private string _selectedLineSearchColumn = AllLineSearchColumnsOption;
        public string SelectedLineSearchColumn
        {
            get => _selectedLineSearchColumn;
            set
            {
                if (SetProperty(ref _selectedLineSearchColumn, value))
                {
                    Filter();
                }
            }
        }

        private string _lineSearchText;
        public string LineSearchText
        {
            get => _lineSearchText;
            set
            {
                if (SetProperty(ref _lineSearchText, value))
                {
                    Filter();
                }
            }
        }

        private ClassRecord _selectedClass;
        public ClassRecord SelectedClass
        {
            get => _selectedClass;
            set
            {
                if (SetProperty(ref _selectedClass, value))
                {
                    UpdateSelectedClassUsages();
                }
            }
        }

        private ICollection<ClassUsage> _selectedClassUsages;
        public ICollection<ClassUsage> SelectedClassUsages
        {
            get => _selectedClassUsages;
            set => SetProperty(ref _selectedClassUsages, value);
        }

        private bool _showAllClassUsages;
        public bool ShowAllClassUsages
        {
            get => _showAllClassUsages;
            set
            {
                if (SetProperty(ref _showAllClassUsages, value))
                {
                    UpdateSelectedClassUsages();
                }
            }
        }

        public ObservableCollectionExtended<PlotUsage> SelectedPlotUsages { get; set; } = new();

        public record FileDirPair(string FileName, string Directory, int Mount);

        private ConcurrentAssetDB GeneratedDB = new();

        /// <summary>
        /// All items in the queue
        /// </summary>
        private List<SingleFileScanner> AllDumpingItems;

        private static BackgroundWorker dbworker = new();

        private ActionBlock<SingleFileScanner> ProcessingQueue;
        /// <summary>
        /// Cancelation of dumping
        /// </summary>
        private bool DumpCanceled;

        /// <summary>
        /// used to switch queue countdown on
        /// </summary>
        public bool isProcessing;
        private CancellationTokenSource cancelloading;
        private string _currentOverallOperationText;
        public string CurrentOverallOperationText
        {
            get => _currentOverallOperationText;
            set => SetProperty(ref _currentOverallOperationText, value);
        }
        private int _overallProgressValue;
        public int OverallProgressValue
        {
            get => _overallProgressValue;
            set
            {
                if (SetProperty(ref _overallProgressValue, value) && OverallProgressMaximum > 0)
                {
                    TaskbarHelper.SetProgressState(TaskbarProgressBarState.NoProgress);
                    TaskbarHelper.SetProgress(value, OverallProgressMaximum);
                }
            }
        }

        private int _overallProgressMaximum;
        public int OverallProgressMaximum
        {
            get => _overallProgressMaximum;
            set => SetProperty(ref _overallProgressMaximum, value);
        }
        private IMEPackage meshPcc;
        private IMEPackage textPcc;
        private IMEPackage audioPcc;
        private IMEPackage animPcc;
        private IMEPackage _ambPerfMasterPcc;
        private record struct AmbPerfStep(ExportEntry AnimExport, float BlendInTime);
        private List<AmbPerfStep> _ambPerfAnimQueue;
        private int _ambPerfAnimIndex;
        private int _ambPerfVersion;

        private string _ambPerfMasterPccPath;
        public string AmbPerfMasterPccPath
        {
            get => _ambPerfMasterPccPath;
            set => SetProperty(ref _ambPerfMasterPccPath, value);
        }
        private GridViewColumnHeader _lastHeaderClicked = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;

        private BlockingCollection<ConvoLine> _linequeue = [];
        private Tuple<string, string, int, string, bool> _currentConvo = new(null, null, -1, null, false); //ConvoName, FileName, export, contentdir, isAmbient
        public Tuple<string, string, int, string, bool> CurrentConvo
        {
            get => _currentConvo;
            set => SetProperty(ref _currentConvo, value);
        }
        public ObservableCollectionExtended<string> SpeakerList { get; } = new();
        private bool _isGettingTLKs;
        public bool IsGettingTLKs
        {
            get => _isGettingTLKs;
            set => SetProperty(ref _isGettingTLKs, value);
        }
        public const string CustomListDesc = "Custom File Lists allow the database to be filtered so only assets that are in certain files or groups of files are shown. Lists can be saved/reloaded.";
        public ICommand GenerateDBCommand { get; set; }
        public ICommand SaveDBCommand { get; set; }
        public ICommand SwitchMECommand { get; set; }
        public ICommand CancelDumpCommand { get; set; }
        public ICommand OpenSourcePkgCommand { get; set; }
        public ICommand GoToSuperclassCommand { get; set; }
        public ICommand OpenUsagePkgCommand { get; set; }
        public ICommand OpenInAnimViewerCommand { get; set; }
        public ICommand ExportToPSACommand { get; set; }
        public ICommand OpenInAnimationImporterCommand { get; set; }
        public ICommand SetFilterCommand { get; set; }
        public ICommand SetCRCCommand { get; set; }
        public ICommand FilterFilesCommand { get; set; }
        public ICommand LoadFileListCommand { get; set; }
        public ICommand SaveFileListCommand { get; set; }
        public ICommand EditFileListCommand { get; set; }
        public ICommand CopyToClipboardCommand { get; set; }
        public ICommand OpenInWindowsExplorerCommand { get; set; }
        public ICommand OpenInPlotDBCommand { get; set; }
        public ICommand OpenPEDefinitionCommand { get; set; }
        public ICommand ChangeLocalizationCommand { get; set; }
        public ICommand BrowseAmbPerfMasterPccCommand { get; set; }
        public ICommand ClearAmbPerfMasterPccCommand { get; set; }

        private bool CanCancelDump(object obj)
        {
            return ProcessingQueue != null && ProcessingQueue.Completion.Status == TaskStatus.WaitingForActivation && !DumpCanceled;
        }

        private bool IsClassSelected(object obj)
        {
            return lstbx_Classes.SelectedIndex >= 0 && currentView == 1;
        }

        private bool IsUsageSelected(object obj)
        {
            return (lstbx_Usages.SelectedIndex >= 0 && currentView == 1)
                || (materialsUsagesPanel.SelectedIndex >= 0 && currentView == 2)
                || (meshesUsagesPanel.SelectedIndex >= 0 && currentView == 3)
                || (texturesUsagesPanel.SelectedIndex >= 0 && currentView == 4)
                || (animationsUsagesPanel.SelectedIndex >= 0 && currentView == 5)
                || (vfxUsagesPanel.SelectedIndex >= 0 && currentView == 6)
                || (guiUsagesPanel.SelectedIndex >= 0 && currentView == 7)
                || (lstbx_Lines.SelectedIndex >= 0 && currentView == 8)
                || (currentView == 9 && lstbx_PlotUsages.SelectedIndex >= 0)
                || (currentView == 0 && IsNotCND(lstbx_Files.SelectedItem));
        }

        private bool IsNotCND(object obj)
        {
            if (obj != null && obj is FileDirPair fdp)
            {
                return !fdp.FileName.EndsWith(".cnd", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private bool CanSetFilter(object obj)
        {
            if (obj is "") // This makes the LODGroups submenu work.
            {
                return true;
            }
            // If we did a better job of MVVM we wouldn't need to do this much reflection, but I just want it to work
            // IE we could put the command on the AssetFilter or on the AssetSpecification
            var tabIndex = obj switch
            {
                IAssetSpecification<ClassRecord> => 1,
                IAssetSpecification<MaterialRecord> => 2,
                IAssetSpecification<MeshRecord> => 3,
                IAssetSpecification<TextureRecord> => 4,
                IAssetSpecification<AnimationRecord> => 5,
                IAssetSpecification<ParticleSysRecord> => 6,
                _ => -1
            };
            return currentView == tabIndex;
        }

        private bool CanUseAnimViewer(object obj)
        {
            return currentView == 5 && CurrentGame == MEGame.ME3 && lstbx_Anims.SelectedIndex >= 0 && !((lstbx_Anims.SelectedItem as AnimationRecord)?.IsAmbPerf ?? true);
        }

        private bool IsAnimSequenceSelected() => currentView == 5 && lstbx_Anims.SelectedIndex >= 0 && !((lstbx_Anims.SelectedItem as AnimationRecord)?.IsAmbPerf ?? true);

        private bool IsPlotElementSelected() => GetSelectedPlotRecord() != null;

        #endregion

        #region Startup/Exit

        public AssetDatabaseWindow() : base("Asset Database", true)
        {
            LoadCommands();
            AssetFilters = new AssetFilters(FileListFilter);

            //Get default db / game
            CurrentDBPath = Settings.AssetDBPath;
            Enum.TryParse(Settings.AssetDBGame, out MEGame game);
            CurrentGame = game;

            InitializeComponent();
        }

        private void LoadCommands()
        {
            GenerateDBCommand = new GenericCommand(GenerateDatabase);
            SaveDBCommand = new GenericCommand(SaveDatabase);
            SetFilterCommand = new RelayCommand(SetFilters, CanSetFilter);
            SwitchMECommand = new RelayCommand(SwitchGame);
            CancelDumpCommand = new RelayCommand(CancelDump, CanCancelDump);
            OpenSourcePkgCommand = new RelayCommand(OpenSourcePkg, IsClassSelected);
            GoToSuperclassCommand = new RelayCommand(GoToSuperClass, IsClassSelected);
            OpenUsagePkgCommand = new RelayCommand(OpenUsagePkg, IsUsageSelected);
            SetCRCCommand = new RelayCommand(SetCRCScan);
            OpenInAnimViewerCommand = new RelayCommand(OpenInAnimViewer, CanUseAnimViewer);
            ExportToPSACommand = new GenericCommand(ExportToPSA, IsAnimSequenceSelected);
            OpenInAnimationImporterCommand = new GenericCommand(OpenInAnimationImporter, IsAnimSequenceSelected);
            FilterFilesCommand = new RelayCommand(SetFilters);
            LoadFileListCommand = new GenericCommand(LoadCustomFileList);
            SaveFileListCommand = new GenericCommand(SaveCustomFileList);
            EditFileListCommand = new RelayCommand(EditCustomFileList);
            CopyToClipboardCommand = new RelayCommand(CopyStringToClipboard);
            OpenInWindowsExplorerCommand = new RelayCommand(OpenFileInWindowsExplorer, IsUsageSelected);
            OpenInPlotDBCommand = new GenericCommand(OpenInPlotDB, IsPlotElementSelected);
            OpenPEDefinitionCommand = new GenericCommand(OpenPEDefinitionInToolset, IsPlotElementSelected);
            ChangeLocalizationCommand = new RelayCommand((e) => { Localization = (MELocalization)e; });
            BrowseAmbPerfMasterPccCommand = new GenericCommand(BrowseAmbPerfMasterPcc);
            ClearAmbPerfMasterPccCommand = new GenericCommand(ClearAmbPerfMasterPcc, () => AmbPerfMasterPccPath != null);
        }

        private void AssetDB_Loaded(object sender, RoutedEventArgs e)
        {
            CurrentOverallOperationText = "Starting Up";
            BusyHeader = "Loading database";
            BusyText = "Please wait...";
            IsBusy = true;
            BusyBarInd = true;

            // Restore saved master PCC for ambient performances
            RestoreAmbPerfMasterPcc();

            if (CurrentDBPath != null && CurrentDBPath.EndsWith("zip") && File.Exists(CurrentDBPath) && CurrentGame != MEGame.Unknown && CurrentGame != MEGame.UDK)
            {
                SwitchGame(CurrentGame.ToString());
            }
            else
            {
                CurrentDBPath = null;
                var gameDbToLoad = "ME3";
                if (Enum.TryParse<MEGame>(Settings.AssetDB_DefaultGame, out var game))
                {
                    gameDbToLoad = game.ToString();
                }
                SwitchGame(gameDbToLoad);
            }
            Activate();
        }

        private void AssetDB_Closing(object sender, CancelEventArgs e)
        {
            if (e.Cancel)
                return;

            Settings.AssetDBPath = CurrentDBPath;
            Settings.AssetDBGame = CurrentGame.ToString();
            Settings.AssetDB_AmbPerfMasterPccPath = AmbPerfMasterPccPath ?? "";

            MeshRendererTab_MeshRenderer?.Dispose();
            SoundpanelWPF_ADB?.Dispose();
            BIKExternalExportLoaderTab_BIKExternalExportLoader?.Dispose();
            EmbeddedTextureViewerTab_EmbeddedTextureViewer?.Dispose();
            MaterialEditorExportLoader_Control?.Dispose();
            AnimPreviewControl.IsPlayingChanged -= OnAmbPerfAnimLooped;
            AnimPreviewControl.AnimationCompleted -= OnAmbPerfStepCompleted;
            _ambPerfAnimQueue = null;
            AnimPreviewControl?.Dispose();

            audioPcc?.Dispose();
            meshPcc?.Dispose();
            textPcc?.Dispose();
            animPcc?.Dispose();
            _ambPerfMasterPcc?.Dispose();

            audioPcc = null;
            meshPcc = null;
            textPcc = null;
            animPcc = null;
            _ambPerfMasterPcc = null;
            AmbPerfMasterPccPath = null;

            dbworker.DoWork -= GetLineStrings;
            dbworker.RunWorkerCompleted -= dbworker_LineWorkCompleted;

            ClearDataBase();
        }

        #endregion

        #region Database I/O

        /// <summary>
        /// Load the database or a particular database table.
        /// </summary>
        /// <param name="currentDbPath"></param>
        /// <param name="game"></param>
        /// <param name="database"></param>
        /// <param name="cancelloadingToken"></param>
        /// <param name="dbTable">Table parameter returns a database with only that table in it. Master = all.</param>
        /// <returns></returns>
        public static async Task LoadDatabase(string currentDbPath, MEGame game, AssetDB database, CancellationToken cancelloadingToken)
        {
            var build = dbCurrentBuild.Trim(' ', '*', '.');
            //Async load
            AssetDB pdb = await ParseDBAsync(game, currentDbPath, build, cancelloadingToken);
            if (pdb is null)
            {
                return;
            }
            database.Game = pdb.Game;
            database.GenerationDate = pdb.GenerationDate;
            database.DatabaseVersion = pdb.DatabaseVersion;
            database.Localization = pdb.Localization;
            database.FileList.AddRange(pdb.FileList);
            database.ContentDir.AddRange(pdb.ContentDir);
            database.AddRecords(pdb);
            database.PlotUsages.LoadPlotPaths(game);
        }

        private static async Task<AssetDB> ParseDBAsync(MEGame dbgame, string dbpath, string build, CancellationToken cancel)
        {
            try
            {
                return await Task.Run(() =>
                {
                    using ZipArchive archive = new(new FileStream(dbpath, FileMode.Open));
                    if (archive.Entries.FirstOrDefault(e => e.Name == $"MasterDB.{dbgame}_{build}.bin") is ZipArchiveEntry entry)
                    {
                        var ms = new MemoryStream((int)entry.Length);
                        using (Stream estream = entry.Open())
                        {
                            estream.CopyTo(ms);
                        }
                        ms.Position = 0;
                        return DeserializeDB(ms, cancel);
                    }
                    //Wrong build - send dummy pdb back and ask user to refresh
                    AssetDB pdb = new();
                    var oldEntry = archive.Entries.FirstOrDefault(z => z.Name.StartsWith("Master"));
                    pdb.DatabaseVersion = "pre 2.0";
                    if (oldEntry != null)
                    {
                        var split = Path.GetFileNameWithoutExtension(oldEntry.Name).Split('_');
                        if (split.Length == 2)
                        {
                            pdb.DatabaseVersion = split[1];
                        }
                    }
                    return pdb;
                });
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error parsing DB: {e.Message}");
            }
            return null;
        }

        private static AssetDB DeserializeDB(MemoryStream ms, CancellationToken ct)
        {
            try
            {
                var readData = BinaryConverter.Deserialize<AssetDB>(ms.GetBuffer().AsSpan(0, (int)ms.Length));
                if (ct.IsCancellationRequested)
                {
                    Console.WriteLine("Cancelled ParseDB");
                    return null;
                }
                return readData;
            }
            catch
            {
                MessageBox.Show($"Failure deserializing database");
                return null;
            }
        }

        private async void SaveDatabase()
        {
            BusyHeader = "Saving database";
            BusyText = "Please wait...";
            BusyBarInd = true;
            IsBusy = true;
            CurrentOverallOperationText = "Database saving...";

            await using (var fileStream = new FileStream(CurrentDBPath, FileMode.Create))
            {
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, true))
                {
                    string build = dbCurrentBuild.Trim(' ', '*', '.');
                    ZipArchiveEntry archiveEntry = archive.CreateEntry($"MasterDB.{CurrentGame}_{build}.bin");
                    await using Stream entryStream = archiveEntry.Open();
                    await Task.Run(() => BinaryConverter.Serialize(CurrentDataBase, entryStream));
                }
            }
            menu_SaveXEmptyLines.IsEnabled = false;
            CurrentOverallOperationText = $"Database saved.";
            IsBusy = false;
            await Task.Delay(3000);
            CurrentOverallOperationText = $"Database generated {CurrentDataBase.GenerationDate} Classes: {CurrentDataBase.ClassRecords.Count} Animations: {CurrentDataBase.Animations.Count} Materials: {CurrentDataBase.Materials.Count} Meshes: {CurrentDataBase.Meshes.Count} Particles: {CurrentDataBase.Particles.Count} Textures: {CurrentDataBase.Textures.Count} Elements: {CurrentDataBase.GUIElements.Count}";
        }

        public void ClearDataBase()
        {
            CurrentDataBase.Clear();
            CurrentDataBase.Game = CurrentGame;
            CurrentDataBase.Localization = Localization;
            _conversationLookup.Clear();
            ClearOwnerNameResolverCache();

            FileListExtended.ClearEx();
            FileListFilter.CustomFileList.Clear();
            FileListFilter.IsSelected = false;
            expander_CustomFiles.IsExpanded = false;
            SpeakerList.ClearEx();
            SelectedMeshTypeFilter = AllMeshFilterOption;
            SelectedVfxTypeFilter = AllVfxFilterOption;
            SelectedAnimationTypeFilter = AllAnimationFilterOption;
            SelectedMaterialTypeFilter = AllMaterialFilterOption;
            RefreshMaterialTextureDropdownFilters();
            RefreshTextureDropdownFilters();
            FilterBox.Clear();
            Filter();
        }

        private void RebuildConversationLookup()
        {
            _conversationLookup.Clear();

            foreach (var conversation in CurrentDataBase.Conversations)
            {
                if (!string.IsNullOrWhiteSpace(conversation.ConvName) && !_conversationLookup.ContainsKey(conversation.ConvName))
                {
                    _conversationLookup[conversation.ConvName] = conversation;
                }
            }
        }

        private bool TryGetConversation(string convoName, out Conversation conversation)
        {
            conversation = null;
            return !string.IsNullOrWhiteSpace(convoName) && _conversationLookup.TryGetValue(convoName, out conversation);
        }

        private static void ClearOwnerNameResolverCache()
        {
            OwnerNameCache.Clear();
            OwnerNameResolvers.Clear();

            foreach (var package in OwnerPackageCache.Values)
            {
                if (package.IsValueCreated)
                {
                    package.Value?.Dispose();
                }
            }

            OwnerPackageCache.Clear();
        }

        public string GetSpeakerDisplay(ConvoLine line)
        {
            if (line == null || !string.Equals(line.Speaker, "Owner", StringComparison.OrdinalIgnoreCase))
            {
                return line?.Speaker;
            }

            if (!TryGetConversation(line.Convo, out var conversation)
                || string.IsNullOrWhiteSpace(conversation.PackageName)
                || conversation.ConversationExportIndex <= 0)
            {
                return line.Speaker;
            }

            var ownerName = ResolveOwnerName(new ConversationKey(conversation.PackageName, conversation.ConversationExportIndex));
            return string.IsNullOrWhiteSpace(ownerName) ? line.Speaker : $"{line.Speaker} ({ownerName})";
        }

        public static string ResolveOwnerName(ConversationKey key)
        {
            if (string.IsNullOrWhiteSpace(key.PackageName) || key.ExportIndex <= 0)
            {
                return null;
            }

            if (OwnerNameCache.TryGetValue(key, out var cachedName))
            {
                return string.IsNullOrEmpty(cachedName) ? null : cachedName;
            }

            var lazyName = OwnerNameResolvers.GetOrAdd(key,
                                                       static k => new Lazy<string>(() => ResolveOwnerNameCore(k), LazyThreadSafetyMode.ExecutionAndPublication));

            var resolvedName = lazyName.Value;
            OwnerNameCache[key] = resolvedName ?? string.Empty;
            OwnerNameResolvers.TryRemove(key, out _);
            return resolvedName;
        }

        private static string ResolveOwnerNameCore(ConversationKey key)
        {
            var package = GetOwnerResolverPackage(key.PackageName);
            if (package == null || !package.TryGetUExport(key.ExportIndex, out var startConversationExport))
            {
                return null;
            }

            var ownerObjectRef = GetOwnerObjectRef(startConversationExport);
            if (ownerObjectRef <= 0)
            {
                return null;
            }

            return ResolveFriendlyOwnerName(package, ownerObjectRef);
        }

        private static int GetOwnerObjectRef(ExportEntry startConversationExport)
        {
            var links = startConversationExport.GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
            if (links == null)
            {
                return 0;
            }

            foreach (var link in links)
            {
                var description = link.GetProp<StrProperty>("LinkDesc")?.Value;
                if (!string.Equals(description, "Owner", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var linkedVars = link.GetProp<ArrayProperty<ObjectProperty>>("LinkedVariables");
                if (linkedVars is { Count: > 0 })
                {
                    return linkedVars[0].Value;
                }
            }

            return 0;
        }

        private static string ResolveFriendlyOwnerName(IMEPackage package, int ownerObjectRef)
        {
            if (!package.TryGetUExport(ownerObjectRef, out var ownerVar))
            {
                return null;
            }

            switch (ownerVar.ClassName)
            {
                case "SeqVar_Object":
                {
                    var objValue = ownerVar.GetProperty<ObjectProperty>("ObjValue");
                    if (objValue == null || objValue.Value <= 0 || !package.TryGetEntry(objValue.Value, out var actorEntry))
                    {
                        return null;
                    }

                    return actorEntry.ObjectName.Instanced;
                }
                case "BioSeqVar_ObjectFindByTag":
                {
                    var tagName = ownerVar.GetProperty<NameProperty>("m_sObjectTagToFind")?.Value.Instanced;
                    if (!string.IsNullOrWhiteSpace(tagName))
                    {
                        return tagName;
                    }

                    return ownerVar.GetProperty<StrProperty>("m_sObjectTagToFind")?.Value;
                }
                default:
                    return ownerVar.ObjectName.Instanced;
            }
        }

        private static IMEPackage GetOwnerResolverPackage(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                return null;
            }

            var lazyPackage = OwnerPackageCache.GetOrAdd(packageName,
                                                         static name => new Lazy<IMEPackage>(() => OpenOwnerResolverPackage(name), LazyThreadSafetyMode.ExecutionAndPublication));
            return lazyPackage.Value;
        }

        private static IMEPackage OpenOwnerResolverPackage(string packageName)
        {
            if (File.Exists(packageName))
            {
                return MEPackageHandler.OpenMEPackage(Path.GetFullPath(packageName));
            }

            if (!Enum.TryParse(Settings.AssetDBGame, out MEGame game) || game == MEGame.Unknown)
            {
                return null;
            }

            if (MELoadedFiles.TryGetHighestMountedFile(game, Path.GetFileName(packageName), out var mountedFilePath))
            {
                return MEPackageHandler.OpenMEPackage(mountedFilePath);
            }

            if (game != MEGame.ME3)
            {
                return null;
            }

            var gameRoot = MEDirectories.GetDefaultGamePath(game);
            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                return null;
            }

            var relativePackagePath = packageName.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var packageDirectory = Directory.GetParent(Path.Combine(gameRoot, relativePackagePath));
            if (packageDirectory == null)
            {
                return null;
            }

            var sfarPath = Path.Combine(packageDirectory.FullName, "Default.sfar");
            if (!File.Exists(sfarPath))
            {
                return null;
            }

            var dlcPackage = new DLCPackage(sfarPath);
            var sfarEntryIndex = dlcPackage.FindFileEntry(Path.GetFileName(packageName));
            return sfarEntryIndex == -1
                ? null
                : MEPackageHandler.OpenMEPackageFromStream(dlcPackage.DecompressEntry(sfarEntryIndex), packageName);
        }

        private void ApplyMaterialTypeFilter()
        {
            if (AssetFilters?.MaterialFilter?.Types is null)
            {
                return;
            }

            var hideMaterials = AssetFilters.MaterialFilter.Types
                .OfType<MaterialClassSpec>()
                .FirstOrDefault(spec => spec.IsMaterial);
            var hideMaterialInstances = AssetFilters.MaterialFilter.Types
                .OfType<MaterialClassSpec>()
                .FirstOrDefault(spec => !spec.IsMaterial);

            if (hideMaterials is null || hideMaterialInstances is null)
            {
                return;
            }

            hideMaterials.IsSelected = SelectedMaterialTypeFilter == MaterialInstanceConstantFilterOption;
            hideMaterialInstances.IsSelected = SelectedMaterialTypeFilter == NormalMaterialFilterOption;
        }

        private IEnumerable<MaterialRecord> GetMaterialTextureDropdownSource()
        {
            if (AssetFilters?.MaterialFilter is null)
            {
                return CurrentDataBase.Materials;
            }

            return CurrentDataBase.Materials.Where(material => AssetFilters.MaterialFilter.Filter(material));
        }

        private void RefreshMaterialTextureDropdownFilters(bool preserveSelections = false)
        {
            var previousTypeSelection = SelectedMaterialTextureTypeFilter;
            var previousCountSelection = SelectedMaterialTextureCountFilter;
            var materialSource = GetMaterialTextureDropdownSource().ToList();
            var textureTypeFilters = new[] { AllMaterialTextureTypeFilterOption }.Concat(MaterialFilter.GetKnownTextureParameterTypes()
                .Concat(materialSource
                    .SelectMany(MaterialFilter.GetTextureSettings)
                    .Select(MaterialFilter.GetTextureParameterType)
                    .Where(type => !string.IsNullOrWhiteSpace(type)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase))
                .ToList();

            _isRefreshingMaterialTextureFilters = true;
            try
            {
                MaterialTextureTypeFilters.ReplaceAll(textureTypeFilters);
                SelectedMaterialTextureTypeFilter = preserveSelections
                                                    && textureTypeFilters.Contains(previousTypeSelection, StringComparer.OrdinalIgnoreCase)
                    ? previousTypeSelection
                    : AllMaterialTextureTypeFilterOption;
                RefreshMaterialTextureCountFilters(preserveSelections, previousCountSelection);
            }
            finally
            {
                _isRefreshingMaterialTextureFilters = false;
            }

            Filter();
        }

        private void RefreshMaterialTextureCountFilters(bool preserveSelections = false, string previousCountSelection = null)
        {
            var materialSource = GetMaterialTextureDropdownSource().ToList();
            var textureCountFilters = new[] { AllMaterialTextureCountFilterOption }.Concat(materialSource
                .Select(GetMaterialTextureCountForCurrentFilter)
                .Distinct()
                .OrderBy(count => count)
                .Select(count => count.ToString()))
                .ToList();

            _isRefreshingMaterialTextureFilters = true;
            try
            {
                MaterialTextureCountFilters.ReplaceAll(textureCountFilters);
                SelectedMaterialTextureCountFilter = preserveSelections && !string.IsNullOrWhiteSpace(previousCountSelection)
                    ? previousCountSelection
                    : AllMaterialTextureCountFilterOption;
            }
            finally
            {
                _isRefreshingMaterialTextureFilters = false;
            }
        }

        private int GetMaterialTextureCountForCurrentFilter(MaterialRecord material)
        {
            if (string.Equals(SelectedMaterialTextureTypeFilter, AllMaterialTextureTypeFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return MaterialFilter.GetTextureParameterTypeCount(material);
            }

            return MaterialFilter.GetTextureParameterTypeCount(material, SelectedMaterialTextureTypeFilter);
        }

        private bool MaterialTabFilter(object obj)
        {
            if (obj is not MaterialRecord materialRecord)
            {
                return false;
            }

            if (!AssetFilters.MaterialFilter.Filter(materialRecord))
            {
                return false;
            }

            var textureCount = GetMaterialTextureCountForCurrentFilter(materialRecord);
            if (!string.Equals(SelectedMaterialTextureTypeFilter, AllMaterialTextureTypeFilterOption, StringComparison.OrdinalIgnoreCase)
                && string.Equals(SelectedMaterialTextureCountFilter, AllMaterialTextureCountFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return textureCount > 0;
            }

            if (string.Equals(SelectedMaterialTextureCountFilter, AllMaterialTextureCountFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return int.TryParse(SelectedMaterialTextureCountFilter, out int countFilter) && textureCount == countFilter;
        }

        private void ApplyAnimationTypeFilter()
        {
            if (AssetFilters?.AnimationFilter?.Filters is null)
            {
                return;
            }

            var normalFilter = AssetFilters.AnimationFilter.Filters
                .FirstOrDefault(f => f.FilterName == "Only Animations");
            var perfFilter = AssetFilters.AnimationFilter.Filters
                .FirstOrDefault(f => f.FilterName == "Only Performances (ME3)");

            if (normalFilter is null || perfFilter is null)
            {
                return;
            }

            normalFilter.IsSelected = SelectedAnimationTypeFilter == NormalAnimationFilterOption;
            perfFilter.IsSelected = SelectedAnimationTypeFilter == AmbientPerformanceFilterOption;
        }

        private bool MeshTabFilter(object obj)
        {
            if (obj is not MeshRecord meshRecord)
            {
                return false;
            }

            if (!AssetFilters.MeshFilter.Filter(meshRecord))
            {
                return false;
            }

            return SelectedMeshTypeFilter switch
            {
                SkeletalMeshFilterOption => meshRecord.IsSkeleton,
                StaticMeshFilterOption => !meshRecord.IsSkeleton,
                _ => true
            };
        }

        private bool VfxTabFilter(object obj)
        {
            if (obj is not ParticleSysRecord particleRecord)
            {
                return false;
            }

            if (!AssetFilters.ParticleFilter.Filter(particleRecord))
            {
                return false;
            }

            return SelectedVfxTypeFilter switch
            {
                ParticleSystemFilterOption => particleRecord.VFXType == ParticleSysRecord.VFXClass.ParticleSystem,
                ClientEffectFilterOption => particleRecord.VFXType == ParticleSysRecord.VFXClass.RvrClientEffect,
                _ => true
            };
        }

        private void RefreshTextureDropdownFilters()
        {
            TextureTypeFilters.ClearEx();
            TextureSizeFilters.ClearEx();

            TextureTypeFilters.Add(AllTextureFilterOption);
            TextureTypeFilters.AddRange(CurrentDataBase.Textures
                .Select(t => t.CFormat)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));

            TextureSizeFilters.Add(AllTextureFilterOption);
            TextureSizeFilters.AddRange(CurrentDataBase.Textures
                .Select(t => new { t.SizeX, t.SizeY, Display = $"{t.SizeX}x{t.SizeY}" })
                .GroupBy(t => t.Display, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.First().SizeX)
                .ThenBy(g => g.First().SizeY)
                .Select(g => g.Key));

            SelectedTextureTypeFilter = AllTextureFilterOption;
            SelectedTextureSizeFilter = AllTextureFilterOption;
        }

        private bool TextureTabFilter(object obj)
        {
            if (obj is not TextureRecord textureRecord)
            {
                return false;
            }

            if (!AssetFilters.TextureFilter.Filter(textureRecord))
            {
                return false;
            }

            if (!string.Equals(SelectedTextureTypeFilter, AllTextureFilterOption, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(textureRecord.CFormat, SelectedTextureTypeFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var textureSize = $"{textureRecord.SizeX}x{textureRecord.SizeY}";
            return string.Equals(SelectedTextureSizeFilter, AllTextureFilterOption, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(textureSize, SelectedTextureSizeFilter, StringComparison.OrdinalIgnoreCase);
        }

        private void GetConvoLinesBackground()
        {
            if (CurrentGame.IsGame1())
            {
                var spkrs = new List<string>();
                foreach (var line in CurrentDataBase.Lines)
                {
                    if (spkrs.All(s => s != line.Speaker))
                        spkrs.Add(line.Speaker);
                }
                spkrs.Sort();
                SpeakerList.AddRange(spkrs);
                return;
            }
#if DEBUG
            System.Diagnostics.Debug.WriteLine("Line worker getting Strings from TLK");
#endif
            IsGettingTLKs = true;
            GeneratedDB.GeneratedLines.Clear();
            _linequeue = new BlockingCollection<ConvoLine>();
            dbworker = new BackgroundWorker();
            dbworker.WorkerSupportsCancellation = true;
            dbworker.DoWork += GetLineStrings;
            dbworker.RunWorkerCompleted += dbworker_LineWorkCompleted;
            dbworker.RunWorkerAsync();

            foreach (var line in CurrentDataBase.Lines)
            {
                _linequeue.Add(line);
            }
            _linequeue.CompleteAdding();
        }

        private void dbworker_LineWorkCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            dbworker.CancelAsync();
            CommandManager.InvalidateRequerySuggested();
            var spkrs = new List<string>();
            foreach (var line in CurrentDataBase.Lines)
            {
                if (GeneratedDB.GeneratedLines.ContainsKey(line.StrRef.ToString()))
                {
                    line.Line = GeneratedDB.GeneratedLines[line.StrRef.ToString()].Line;
                }
                if (spkrs.All(s => s != line.Speaker))
                    spkrs.Add(line.Speaker);
            }

            int lineCountWithEmptyLines = CurrentDataBase.Lines.Count;
            CurrentDataBase.Lines.RemoveAll(l => l.Line == "No Data");
            int numEmptyLines = lineCountWithEmptyLines - CurrentDataBase.Lines.Count;

            GeneratedDB.GeneratedLines.Clear();
            spkrs.Sort();
            SpeakerList.AddRange(spkrs);
            if (numEmptyLines > 0)
            {
                menu_SaveXEmptyLines.IsEnabled = true;
            }
            IsGettingTLKs = false;
            if (CurrentDataBase.Lines.Count == 0)
            {
                MessageBox.Show("Line list is empty! Make sure you have TLKs loaded in TLK Manager.");
            }
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"ADB: {numEmptyLines} empty lines");
            System.Diagnostics.Debug.WriteLine("Line worker done");
#endif
        }

        private void GetLineStrings(object sender, DoWorkEventArgs e)
        {
            foreach (var ol in _linequeue.GetConsumingEnumerable(CancellationToken.None))
            {
                switch (CurrentGame)
                {
                    case MEGame.ME1:
                    case MEGame.LE1:
                        //Shouldn't be called in ME1/LE1
                        break;
                    case MEGame.ME2:
                        ol.Line = ME2TalkFiles.FindDataById(ol.StrRef);
                        break;
                    case MEGame.ME3:
                        ol.Line = ME3TalkFiles.FindDataById(ol.StrRef);
                        break;
                    case MEGame.LE2:
                        ol.Line = LE2TalkFiles.FindDataById(ol.StrRef);
                        break;
                    case MEGame.LE3:
                        ol.Line = LE3TalkFiles.FindDataById(ol.StrRef);
                        break;
                }
                GeneratedDB.GeneratedLines.TryAdd(ol.StrRef.ToString(), ol);
            }
        }

        #endregion

        #region UserCommands

        public void GenerateDatabase()
        {
            var shouldGenerate = MessageBox.Show($"Generate a new database for {CurrentGame}?", "Generating new DB", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (shouldGenerate)
            {
                ScanGame();
            }
        }

        public void SwitchGame(object param)
        {
            var p = param as string;
            switchME1_menu.IsChecked = false;
            switchME2_menu.IsChecked = false;
            switchME3_menu.IsChecked = false;
            switchLE1_menu.IsChecked = false;
            switchLE2_menu.IsChecked = false;
            switchLE3_menu.IsChecked = false;
            ClearDataBase();
            currentView = 0;
            MeshRendererTab_MeshRenderer.UnloadExport();
            meshPcc?.Dispose();
            btn_MeshRenderToggle.IsChecked = false;
            btn_MeshRenderToggle.Content = "Toggle Mesh Rendering";
            EmbeddedTextureViewerTab_EmbeddedTextureViewer.UnloadExport();
            textPcc?.Dispose();
            btn_TextRenderToggle.IsChecked = false;
            btn_TextRenderToggle.Content = "Toggle Texture Rendering";
            AnimPreviewControl?.Clear();
            AnimPreviewControl.IsPlayingChanged -= OnAmbPerfAnimLooped;
            AnimPreviewControl.AnimationCompleted -= OnAmbPerfStepCompleted;
            _ambPerfAnimQueue = null;
            animPcc?.Dispose();
            animPcc = null;
            _ambPerfMasterPcc?.Dispose();
            _ambPerfMasterPcc = null;
            AmbPerfMasterPccPath = null;
            RestoreAmbPerfMasterPcc();
            btn_AnimPreviewToggle.IsChecked = false;
            btn_AnimPreviewToggle.Content = "Toggle Animation Preview";
            MaterialEditorExportLoader_Control?.UnloadExport();
            SoundpanelWPF_ADB.UnloadExport();
            audioPcc?.Dispose();
            SoundpanelWPF_ADB.FreeAudioResources();
            btn_LinePlaybackToggle.IsChecked = false;
            btn_LinePlaybackToggle.IsEnabled = true;
            tabCtrl_plotUsage.SelectedIndex = 0;
            SelectedPlotUsages.ClearEx();
            lstbx_PlotBool.SelectedIndex = -1;
            lstbx_PlotInt.SelectedIndex = -1;
            lstbx_PlotFloat.SelectedIndex = -1;
            lstbx_PlotTrans.SelectedIndex = -1;
            lstbx_PlotCond.SelectedIndex = -1;
            bool updateDefaultDB = CurrentGame != MEGame.Unknown;
            switch (p)
            {
                case "ME1":
                    CurrentGame = MEGame.ME1;
                    switchME1_menu.IsChecked = true;
                    break;
                case "ME2":
                    CurrentGame = MEGame.ME2;
                    switchME2_menu.IsChecked = true;
                    break;
                case "ME3":
                    CurrentGame = MEGame.ME3;
                    switchME3_menu.IsChecked = true;
                    break;
                case "LE1":
                    CurrentGame = MEGame.LE1;
                    switchLE1_menu.IsChecked = true;
                    break;
                case "LE2":
                    CurrentGame = MEGame.LE2;
                    switchLE2_menu.IsChecked = true;
                    break;
                case "LE3":
                    CurrentGame = MEGame.LE3;
                    switchLE3_menu.IsChecked = true;
                    break;
            }

            if (updateDefaultDB)
            {
                Settings.AssetDB_DefaultGame = CurrentGame.ToString();
            }
            CurrentDBPath = GetDBPath(CurrentGame);

            if (CurrentDBPath != null && File.Exists(CurrentDBPath))
            {
                Settings.AssetDBGame = CurrentGame.ToString();
                CurrentOverallOperationText = "Loading database";
                BusyHeader = $"Loading database for {CurrentGame}";
                BusyText = "Please wait...";
                BusyBarInd = true;
                IsBusy = true;
                cancelloading?.Cancel();
                cancelloading = new CancellationTokenSource();
                var start = DateTime.UtcNow;
                LoadDatabase(CurrentDBPath, CurrentGame, CurrentDataBase, cancelloading.Token).ContinueWithOnUIThread(prevTask =>
                {
                    if (CurrentDataBase.DatabaseVersion == null || CurrentDataBase.DatabaseVersion != dbCurrentBuild)
                    {
                        var warn = MessageBox.Show($"This database is out of date (v {CurrentDataBase.DatabaseVersion} versus v {dbCurrentBuild})\nA new version is required. Do you wish to rebuild?", "Warning", MessageBoxButton.OKCancel);
                        if (warn == MessageBoxResult.Cancel)
                        {
                            ClearDataBase();
                            IsBusy = false;
                        }
                        else
                        {
                            ScanGame();
                        }
                    }
                    else
                    {
                        var dlcs = MELoadedDLC.GetDLCNamesWithMounts(CurrentGame);
                        dlcs.Add("BioGame", 0);
                        foreach ((string fileName, int directoryKey) in CurrentDataBase.FileList)
                        {
                            var cd = CurrentDataBase.ContentDir[directoryKey];
                            int mount = -1;
                            dlcs.TryGetValue(cd, out mount);
                            FileListExtended.Add(new(fileName, cd, mount));
                        }

                        Localization = CurrentDataBase.Localization;
                        RebuildConversationLookup();
                        AssetFilters.MaterialFilter.LoadFromDatabase(CurrentDataBase);
                        RefreshMaterialTextureDropdownFilters();
                        RefreshTextureDropdownFilters();
                        IsBusy = false;
                        CurrentOverallOperationText = $"Database generated {CurrentDataBase.GenerationDate} Classes: {CurrentDataBase.ClassRecords.Count} " +
                                                      $"Animations: {CurrentDataBase.Animations.Count} Materials: {CurrentDataBase.Materials.Count} Meshes: {CurrentDataBase.Meshes.Count} " +
                                                      $"Particles: {CurrentDataBase.Particles.Count} Textures: {CurrentDataBase.Textures.Count} Elements: {CurrentDataBase.GUIElements.Count} " +
                                                      $"Lines: {CurrentDataBase.Lines.Count}";
#if DEBUG
                        var end = DateTime.UtcNow;
                        double length = (end - start).TotalMilliseconds;
                        CurrentOverallOperationText = $"{CurrentOverallOperationText} LoadTime: {length}ms";
#endif

                        GetConvoLinesBackground();
                    }
                }).ContinueWith(x =>
                {
                    // RESEARCH
                    //var shaderCacheF = @"S:\SteamLibrary\steamapps\common\Mass Effect Legendary Edition\Game\ME3\BioGame\CookedPCConsole\RefShaderCache-PC-D3D-SM5.upk";
                    //var shaderCacheP = MEPackageHandler.OpenMEPackage(shaderCacheF);
                    //var shaderCache = ObjectBinary.From<ShaderCache>(shaderCacheP.Exports.FirstOrDefault());
                    //Dictionary<Guid, string> refGuidMap = new();
                    //foreach (var sm in shaderCache.MaterialShaderMaps)
                    //{
                    //    refGuidMap[sm.Value.ID] = sm.Value.FriendlyName;
                    //}

                    //Dictionary<Guid, string> materialGuidMap = new();
                    //int testIdx = 0;
                    //foreach (var mat in CurrentDataBase.Materials)
                    //{
                    //    testIdx++;
                    //    if (testIdx % 40 == 0)
                    //    {
                    //        Debug.WriteLine($"Reading materials... {testIdx}/{CurrentDataBase.Materials.Count}");
                    //    }
                    //    var usage = mat.AssetUsages.First();
                    //    var path = GetFilePath(usage.FileKey);
                    //    using var package = MEPackageHandler.UnsafePartialLoad(path, x => x.UIndex == usage.UIndex);
                    //    var uMat = package.GetUExport(usage.UIndex);
                    //    var guid = ObjectBinary.From<Material>(uMat).SM3MaterialResource.ID;
                    //    materialGuidMap[guid] = uMat.ObjectName.Instanced;
                    //}

                    //foreach (var mgm in materialGuidMap)
                    //{
                    //    if (refGuidMap.Remove(mgm.Key))
                    //    {
                    //        Debug.WriteLine($"Removed {mgm.Key} from ref map");
                    //    }
                    //}

                    //Debug.WriteLine($"Unreferenced shaders");
                    //foreach (var extraRef in refGuidMap.OrderBy(x=>x.Value))
                    //{
                    //    Debug.WriteLine($"{extraRef.Value} ({extraRef.Key})");
                    //}
                });
            }
            else
            {
                IsBusy = false;
                CurrentOverallOperationText = "No database found.";
            }
        }

        public static string GetDBPath(MEGame game)
        {
            return Path.Combine(AppDirectories.AppDataFolder, $"AssetDB{game}.zip");
        }

        private ListBoxScroll GetSelectedPlotListBox()
        {
            if (currentView == 9)
            {
                return tabCtrl_plotUsage.SelectedIndex switch
                {
                    0 => lstbx_PlotBool,
                    1 => lstbx_PlotInt,
                    2 => lstbx_PlotFloat,
                    3 => lstbx_PlotTrans,
                    4 => lstbx_PlotCond,
                    _ => null
                };
            }

            return null;
        }

        private PlotRecord GetSelectedPlotRecord()
        {
            var lstbx = GetSelectedPlotListBox();
            if (lstbx is { SelectedIndex: > -1 })
            {
                return (PlotRecord)lstbx.SelectedItem;
            }
            return null;
        }

        private List<PlotRecord> GetSelectedPlotSource()
        {
            if (currentView == 9 && CurrentDataBase.PlotUsages != null)
            {
                return tabCtrl_plotUsage.SelectedIndex switch
                {
                    0 => CurrentDataBase.PlotUsages.Bools,
                    1 => CurrentDataBase.PlotUsages.Ints,
                    2 => CurrentDataBase.PlotUsages.Floats,
                    3 => CurrentDataBase.PlotUsages.Transitions,
                    4 => CurrentDataBase.PlotUsages.Conditionals,
                    _ => null
                };
            }

            return null;
        }

        private void GoToSuperClass(object obj)
        {
            var cr = (ClassRecord)lstbx_Classes.SelectedItem;
            var sClass = cr.SuperClass;
            if (sClass == null)
            {
                MessageBox.Show("SuperClass unknown.");
                return;
            }
            if (FilterBox.Text != null)
            {
                FilterBox.Clear();
                Filter();
            }
            var scidx = CurrentDataBase.ClassRecords.IndexOf(CurrentDataBase.ClassRecords.FirstOrDefault(r => r.Class == sClass));
            if (scidx >= 0)
            {
                lstbx_Classes.SelectedIndex = scidx;
            }
            else
            {
                MessageBox.Show("SuperClass not found.");
            }
        }

        private (string, string, int, int) GetSelectedUsageInfo()
        {
            string usagepkg = null;
            string contentdir = null;
            int usagemount = 0;
            int usageUID = 0;
            if (lstbx_Usages.SelectedIndex >= 0 && currentView == 1)
            {
                var c = (ClassUsage)lstbx_Usages.SelectedItem;
                (usagepkg, contentdir, usagemount) = FileListExtended[c.FileKey];
                usageUID = c.UIndex;
            }
            else if (GetSelectedPanelUsage(currentView) is IAssetUsage usage)
            {
                (usagepkg, contentdir, usagemount) = FileListExtended[usage.FileKey];
                usageUID = usage.UIndex;
            }
            else if (lstbx_Lines.SelectedIndex >= 0 && currentView == 8)
            {
                var lu = (ConvoLine)lstbx_Lines.SelectedItem;
                usagepkg = CurrentConvo.Item2;
                contentdir = CurrentConvo.Item4;
                usageUID = CurrentConvo.Item3;
            }
            else if (lstbx_PlotUsages.SelectedIndex >= 0 && currentView == 9)
            {
                var pu = (PlotUsage)lstbx_PlotUsages.SelectedItem;
                (usagepkg, contentdir, usagemount) = FileListExtended[pu.FileKey];
                usageUID = pu.UIndex;
            }
            else if (lstbx_Files.SelectedIndex >= 0 && currentView == 0)
            {
                (usagepkg, contentdir, usagemount) = (FileDirPair)lstbx_Files.SelectedItem;
            }

            return (usagepkg, contentdir, usagemount, usageUID);
        }

        private void OpenUsagePkg(object obj)
        {
            var tool = obj as string;
            string usagepkg = null;
            int usagemount = 0;
            int usageUID = 0;
            int strRef = 0;
            string contentdir = null;

            (usagepkg, contentdir, usagemount, usageUID) = GetSelectedUsageInfo();

            if (lstbx_Lines.SelectedIndex >= 0 && currentView == 8)
            {
                var lu = (ConvoLine)lstbx_Lines.SelectedItem;
                strRef = lu.StrRef;
            }
            else if (lstbx_PlotUsages.SelectedIndex >= 0 && currentView == 9)
            {
                var pu = (PlotUsage)lstbx_PlotUsages.SelectedItem;
                tool = pu.Context.ToTool();
                if (tool == "PlotEd")
                {
                    OpenInPlotEditor(GetFilePath(usagepkg, contentdir), pu);
                    return;
                }
                if (tool == "DlgEd" && pu.ContainerID.HasValue)
                {
                    strRef = pu.ContainerID.Value;
                }
            }

            if (usagepkg == null)
            {
                MessageBox.Show("File not found.");
                return;
            }

            OpenInToolkit(tool, GetFilePath(usagepkg, contentdir), usageUID, strRef, realFileName: usagepkg);
        }

        private void OpenSourcePkg(object obj)
        {
            var cr = (ClassRecord)lstbx_Classes.SelectedItem;
            var sourcepkg = cr.DefinitionFile;
            var sourceexp = cr.DefinitionUIndex;

            if (sourcepkg < 0)
            {
                MessageBox.Show("Definition file unknown.");
                return;
            }
            (string filename, string dir, _) = FileListExtended[sourcepkg];

            OpenInToolkit("PackageEditor", GetFilePath(filename, dir), sourceexp);
        }

        private string GetFilePath(string filename, string contentdir)
        {
            string filePath = null;
            string rootPath = MEDirectories.GetDefaultGamePath(CurrentGame);

            if (rootPath == null || !Directory.Exists(rootPath))
            {
                MessageBox.Show($"{CurrentGame} has not been found. Please check your Legendary Explorer settings");
                return null;
            }

            filePath = Directory.EnumerateFiles(rootPath, filename, SearchOption.AllDirectories).FirstOrDefault(f => f.Contains(contentdir));

            if (filePath == null)
            {
                if (CurrentGame == MEGame.ME3)
                {
                    // This is very inefficient...
                    var testFile = Directory.EnumerateFiles(rootPath, "Default.sfar", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains(contentdir));
                    if (testFile != null)
                    {
                        DLCPackage dlp = new DLCPackage(testFile);
                        var dlpFile = dlp.FindFileEntry(filename);
                        if (dlpFile != -1)
                        {
                            return testFile; // It's in the SFAR
                        }
                    }
                }
                MessageBox.Show($"File {filename} not found in content directory {contentdir}.");
                return null;
            }

            return filePath;
        }

        /// <summary>
        /// Fetches a package from disk or SFAR. When done from disk, a reference is created, so you must dispose of it when done to decrement the reference.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="fileIndex"></param>
        /// <returns></returns>
        private IMEPackage fetchPackage(string filePath, int? fileIndex, string realFilename)
        {
            if (Path.GetFileName(filePath) == "Default.sfar" && (fileIndex != null || realFilename != null))
            {
                // Must open sfar
                if (fileIndex != null)
                {
                    // Get name of package in SFAR.
                    (string filename, string contentdir, int mount) = FileListExtended[fileIndex.Value];
                    realFilename = filename;
                }

                DLCPackage dlp = new DLCPackage(filePath);
                var dlpFile = dlp.FindFileEntry(realFilename);
                if (dlpFile != -1)
                {
                    return MEPackageHandler.OpenMEPackageFromStream(dlp.DecompressEntry(dlpFile), realFilename);
                }

                return null; // File not found.
            }
            else
            {
                return MEPackageHandler.OpenMEPackage(filePath);
            }
        }

        private void OpenInToolkit(string tool, string filePath, int uindex = 0, int strRef = 0, int? fileIndex = null, string realFileName = null)
        {
            if (filePath == null)
                return; // Do nothing.


            IMEPackage package = null;
            ExportEntry exportEntry = null;
            if (tool != "CndEd") // don't try to OpenMEPackage on a .cnd file
            {
                package = fetchPackage(filePath, fileIndex, realFileName);

                if (package == null)
                {
                    if (fileIndex != null)
                    {
                        var (name, dir, mount) = FileListExtended[fileIndex.Value];
                        filePath = name;
                    }
                    MessageBox.Show($"Could not locate file: {filePath}");
                    return;
                }

                if (package.TryGetUExport(uindex, out var goodExport)) exportEntry = goodExport;
            }

            switch (tool)
            {
                case "Meshplorer":
                    var meshPlorer = new Meshplorer.MeshplorerWindow();
                    meshPlorer.Show();
                    if (uindex != 0)
                    {
                        meshPlorer.LoadFile(filePath, uindex);
                    }
                    else
                    {
                        meshPlorer.LoadFile(filePath);
                    }
                    break;
                case "PathEd":
                    var pathEd = new PathfindingEditor.PathfindingEditorWindow(package);
                    pathEd.Show();
                    break;
                case "DlgEd":
                    var diagEd = new DialogueEditor.DialogueEditorWindow();
                    diagEd.Show();
                    if (uindex != 0)
                    {
                        diagEd.LoadFile(filePath, uindex);
                        if (strRef != 0) diagEd.TrySelectStrRef(strRef);
                    }
                    else
                    {
                        diagEd.LoadFile(filePath);
                    }
                    break;
                case "SeqEd":
                    if (exportEntry is not null)
                    {
                        var SeqEd = new Sequence_Editor.SequenceEditorWPF(exportEntry);
                        SeqEd.Show();
                    }
                    else
                    {
                        var SeqEd = new Sequence_Editor.SequenceEditorWPF(package);
                        SeqEd.Show();
                    }
                    break;
                case "SoundExplorer":
                    var soundplorer = new Soundplorer.SoundplorerWPF();
                    soundplorer.Show();
                    soundplorer.LoadFile(filePath);
                    break;
                case "CndEd":
                    var cndEd = new ConditionalsEditor.ConditionalsEditorWindow();
                    cndEd.Show();
                    if (uindex != 0)
                    {
                        cndEd.LoadFile(filePath, uindex);
                    }
                    else
                    {
                        cndEd.LoadFile(filePath);
                    }

                    break;
                default:
                    var packEditor = new PackageEditor.PackageEditorWindow();
                    packEditor.Show();
                    if (uindex != 0)
                    {
                        packEditor.LoadPackage(package, uindex);
                    }
                    else
                    {
                        packEditor.LoadPackage(package);
                    }
                    break;
            }
            // We dispose of package here so it loses the package handler reference, since we don't open it in a using block.
            package?.Dispose();
        }

        /// <summary>
        /// Open in Toolkit with some extra logic to go directly to a transition/quest/codex
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="usage"></param>
        private void OpenInPlotEditor(string filePath, PlotUsage usage)
        {
            if (filePath == null)
                return;
            var plotEditor = new PlotEditor.PlotEditorWindow();
            plotEditor.Show();
            plotEditor.LoadFile(filePath);
            if (usage.ContainerID.HasValue)
            {
                switch (usage.Context)
                {
                    case PlotUsageContext.Transition:
                        plotEditor.GoToStateEvent(usage.ContainerID.Value);
                        break;
                    case PlotUsageContext.Codex:
                        plotEditor.GoToCodex(usage.ContainerID.Value);
                        break;
                    case PlotUsageContext.Quest:
                        plotEditor.GoToQuest(usage.ContainerID.Value);
                        break;
                    case PlotUsageContext.BoolTaskEval:
                    case PlotUsageContext.IntTaskEval:
                    case PlotUsageContext.FloatTaskEval:
                    default:
                        break;
                }
            }
        }

        private void OpenFileInWindowsExplorer(object obj)
        {
            var (filename, contentDir, _, _) = GetSelectedUsageInfo();
            if (filename is null || contentDir is null) return;

            string filePath = GetFilePath(filename, contentDir);

            if (File.Exists(filePath))
            {
                string cmd = "explorer.exe";
                string arg = "/select, " + filePath;
                System.Diagnostics.Process.Start(cmd, arg);
            }
        }

        private void OpenInPlotDB()
        {
            var record = GetSelectedPlotRecord();
            var plotElement = PlotDatabases.FindPlotElementFromID(record.ElementID, record.ElementType.ToPlotElementType(), CurrentGame);
            var plotDB = new PlotManagerWindow(CurrentGame.ToLEVersion(), plotElement);
            plotDB.Show();
            plotDB.SelectPlotElement(plotElement, CurrentGame.ToLEVersion());
            //plotDB.NoAutoSelection = false;
        }

        private void OpenPEDefinitionInToolset()
        {
            var record = GetSelectedPlotRecord();

            if (record.ElementType is PlotRecordType.Conditional or PlotRecordType.Transition && record.BaseUsage != null)
            {
                (string usagepkg, string contentdir, int usagemount) = FileListExtended[record.BaseUsage.FileKey];
                int usageUID = record.BaseUsage.UIndex;
                if (record.BaseUsage.Context is PlotUsageContext.Conditional)
                {
                    OpenInToolkit("", GetFilePath(usagepkg, contentdir), usageUID);
                }
                else if (record.BaseUsage.Context is PlotUsageContext.Transition)
                {
                    OpenInPlotEditor(GetFilePath(usagepkg, contentdir), record.BaseUsage);
                }
                else if (record.BaseUsage.Context is PlotUsageContext.CndFile)
                {
                    OpenInToolkit("CndEd", GetFilePath(usagepkg, contentdir), usageUID);
                }
            }
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) //Fires if Tab moves away
        {
            e.Handled = true;

            if (currentView != previousView)
            {
                FilterBox.Clear();
                Filter();
                switch (currentView)
                {
                    case 2:
                        if (MaterialTextureTypeFilters.Count <= 1 && CurrentDataBase.Materials.Count > 0)
                        {
                            RefreshMaterialTextureDropdownFilters();
                        }
                        FilterBox.Watermark = "Search (by material name or parent package)";
                        break;
                    case 4:
                        FilterBox.Watermark = "Search (by texture name, package, type, size, or CRC if compiled)";
                        break;
                    case 0:
                        FilterBox.Watermark = "Search (by filename or source directory)";
                        break;
                    default:
                        FilterBox.Watermark = "Search";
                        break;
                }

                if (previousView == 3)
                {
                    ToggleRenderMesh();
                    btn_MeshRenderToggle.IsChecked = false;
                    btn_MeshRenderToggle.Content = "Toggle Mesh Rendering";
                }

                if (previousView == 4)
                {
                    ToggleRenderTexture();
                    btn_TextRenderToggle.IsChecked = false;
                    btn_TextRenderToggle.Content = "Toggle Texture Rendering";
                }

                if (previousView == 5)
                {
                    AnimPreviewControl.IsPlayingChanged -= OnAmbPerfAnimLooped;
                    AnimPreviewControl.AnimationCompleted -= OnAmbPerfStepCompleted;
                    _ambPerfAnimQueue = null;
                    AnimPreviewControl.Clear();
                    animPcc?.Dispose();
                    animPcc = null;
                    btn_AnimPreviewToggle.IsChecked = false;
                    btn_AnimPreviewToggle.Content = "Toggle Animation Preview";
                }

                if (currentView == 0)
                {
                    menu_OpenUsage.Header = "Open File";
                }

                if (previousView == 0)
                {
                    menu_OpenUsage.Header = "Open Usage";
                }
                previousView = currentView;
            }
        }

        private void lstbx_Meshes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (currentView == 3 && lstbx_Meshes.SelectedIndex >= 0)
            {
                ToggleRenderMesh();
            }
        }

        private void lstbx_Textures_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (currentView == 4 && lstbx_Textures.SelectedIndex >= 0)
            {
                ToggleRenderTexture();
            }
        }

        private void lstbx_Anims_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (currentView == 5 && lstbx_Anims.SelectedIndex >= 0)
            {
                ToggleRenderAnimation();
            }
        }

        private void lstbx_Lines_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (currentView == 8 && lstbx_Lines.SelectedIndex >= 0)
            {
                if (UpdateCurrentConvoForLine(lstbx_Lines.SelectedItem as ConvoLine))
                {
                    ToggleLinePlayback();
                    return;
                }
            }
            CurrentConvo = new Tuple<string, string, int, string, bool>(null, null, 0, null, false);
        }

        private bool UpdateCurrentConvoForLine(ConvoLine line)
        {
            if (line == null)
            {
                return false;
            }

            var convo = CurrentDataBase.Conversations.FirstOrDefault(x => x.ConvName == line.Convo);
            if (convo == null)
            {
                return false;
            }

            (string fileName, int directoryKey) = CurrentDataBase.FileList[convo.ConvFile.FileKey];
            CurrentConvo = new Tuple<string, string, int, string, bool>(convo.ConvName, fileName, convo.ConvFile.UIndex, CurrentDataBase.ContentDir[directoryKey], convo.IsAmbient);
            return true;
        }

        private void lstbx_Lines_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            while (source is not ListViewItem && source != null)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is ListViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }

        private void PETabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (currentView == 9)
            {
                FilterBox.Clear();
                Filter();
            }
        }

        private void lstbx_PlotElement_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (currentView == 9)
            {
                PlotRecord selectedRecord = GetSelectedPlotRecord();
                if (selectedRecord != null)
                {
                    SelectedPlotUsages.Clear();
                    SelectedPlotUsages.AddRange(selectedRecord.Usages);
                }
            }
        }

        private void btn_TextRenderToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleRenderTexture();
            if (btn_TextRenderToggle.IsChecked == true)
            {
                btn_TextRenderToggle.Content = "Untoggle Texture Rendering";
            }
            else
            {
                btn_TextRenderToggle.Content = "Toggle Texture Rendering";
            }
        }

        private void btn_MeshRenderToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleRenderMesh();
            if (btn_MeshRenderToggle.IsChecked == true)
            {
                btn_MeshRenderToggle.Content = "Untoggle Mesh Rendering";
            }
            else
            {
                btn_MeshRenderToggle.Content = "Toggle Mesh Rendering";
            }
        }

        private void btn_AnimPreviewToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleRenderAnimation();
            if (btn_AnimPreviewToggle.IsChecked == true)
            {
                btn_AnimPreviewToggle.Content = "Untoggle Animation Preview";
            }
            else
            {
                btn_AnimPreviewToggle.Content = "Toggle Animation Preview";
            }
        }

        private void btn_LinePlaybackToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleLinePlayback();
        }

        private void PlayMaleLineAudio_Click(object sender, RoutedEventArgs e)
        {
            PlaySelectedLineAudio(0);
        }

        private void PlayFemaleLineAudio_Click(object sender, RoutedEventArgs e)
        {
            PlaySelectedLineAudio(1);
        }

        private void ExtractMaleLineAudio_Click(object sender, RoutedEventArgs e)
        {
            ExtractSelectedLineAudio(0);
        }

        private void ExtractFemaleLineAudio_Click(object sender, RoutedEventArgs e)
        {
            ExtractSelectedLineAudio(1);
        }

        private void PlaySelectedLineAudio(int genderTabIndex)
        {
            if (currentView != 8 || lstbx_Lines.SelectedIndex < 0)
            {
                return;
            }

            if (!UpdateCurrentConvoForLine(lstbx_Lines.SelectedItem as ConvoLine))
            {
                return;
            }

            btn_LinePlaybackToggle.IsChecked = true;

            if (genderTabs.SelectedIndex != genderTabIndex)
            {
                genderTabs.SelectedIndex = genderTabIndex;
            }

            ToggleLinePlayback(startPlayback: true);
        }

        private void ExtractSelectedLineAudio(int genderTabIndex)
        {
            if (currentView != 8 || lstbx_Lines.SelectedIndex < 0)
            {
                return;
            }

            if (!UpdateCurrentConvoForLine(lstbx_Lines.SelectedItem as ConvoLine))
            {
                return;
            }

            btn_LinePlaybackToggle.IsChecked = true;

            if (genderTabs.SelectedIndex != genderTabIndex)
            {
                genderTabs.SelectedIndex = genderTabIndex;
            }

            ToggleLinePlayback();

            if (SoundpanelWPF_ADB.ExportAudioCommand?.CanExecute(null) == true)
            {
                SoundpanelWPF_ADB.ExportAudioCommand.Execute(null);
            }
        }

        private void ToggleRenderMesh()
        {
            bool showmesh = btn_MeshRenderToggle.IsChecked == true && lstbx_Meshes.SelectedIndex >= 0 && CurrentDataBase.Meshes[lstbx_Meshes.SelectedIndex].Usages.Count > 0 && currentView == 3;

            if (!showmesh)
            {
                MeshRendererTab_MeshRenderer.UnloadExport();
                meshPcc?.Dispose();
                return;
            }
            string rootPath = MEDirectories.GetDefaultGamePath(CurrentGame);
            var selecteditem = (MeshRecord)lstbx_Meshes.SelectedItem;
            var filekey = selecteditem.Usages[0].FileKey;
            var (filename, dirKey) = CurrentDataBase.FileList[filekey];
            var cdir = CurrentDataBase.ContentDir[dirKey];

            if (rootPath == null)
            {
                MessageBox.Show($"{CurrentGame} has not been found. Please check your Legendary Explorer settings");
                return;
            }
            filename = $"{filename}.*";

            var files = Directory.GetFiles(rootPath, filename, SearchOption.AllDirectories);
            if (files.IsEmpty())
            {
                MessageBox.Show($"File {filename} not found.");
                return;
            }

            if (meshPcc != null) //unload existing file
            {
                MeshRendererTab_MeshRenderer.UnloadExport();
                meshPcc.Dispose();
            }

            foreach (var filePath in files) //handle cases of mods/dlc having same file.
            {
                bool isBaseFile = cdir.ToLower() == "biogame";
                bool isDLCFile = filePath.ToLower().Contains("dlc");
                if (isBaseFile == isDLCFile)
                {
                    continue;
                }
                meshPcc = MEPackageHandler.OpenMEPackage(filePath);
                var uexpIdx = selecteditem.Usages[0].UIndex;
                if (uexpIdx <= meshPcc.ExportCount)
                {
                    var meshExp = meshPcc.GetUExport(uexpIdx);
                    if (meshExp.ObjectName == selecteditem.MeshName)
                    {
                        MeshRendererTab_MeshRenderer.LoadExport(meshExp);
                        break;
                    }
                }
                meshPcc.Dispose();
            }
        }

        private void ToggleRenderAnimation()
        {
            bool showAnim = btn_AnimPreviewToggle.IsChecked == true
                && lstbx_Anims.SelectedIndex >= 0
                && currentView == 5;

            if (!showAnim)
            {
                AnimPreviewControl.AnimationCompleted -= OnAmbPerfStepCompleted;
                AnimPreviewControl.IsPlayingChanged -= OnAmbPerfAnimLooped;
                _ambPerfAnimQueue = null;
                AnimPreviewControl.Clear();
                animPcc?.Dispose();
                animPcc = null;
                return;
            }

            var anim = (AnimationRecord)lstbx_Anims.SelectedItem;
            if (!anim.Usages.Any()) return;

            int animUIndex = 0;
            string filePath = null;

            // find the first usage that we can actually resolve to a file; this will skip over mods that were uninstalled since the database was generated
            foreach (var usage in anim.Usages)
            {
                int fileListIndex;
                (fileListIndex, animUIndex, _) = usage;
                filePath = GetFilePath(fileListIndex);
                if (filePath != null)
                {
                    break;
                }
            }

            if (filePath == null)
            {
                AnimPreviewControl.Clear();
                return;
            }

            animPcc?.Dispose();
            animPcc = MEPackageHandler.OpenMEPackage(filePath);

            if (animPcc.IsUExport(animUIndex))
            {
                var animExp = animPcc.GetUExport(animUIndex);
                LoadSkeletalMeshForAnimPreview();

                if (anim.IsAmbPerf)
                {
                    // Unsubscribe old handlers
                    AnimPreviewControl.AnimationCompleted -= OnAmbPerfStepCompleted;
                    AnimPreviewControl.IsPlayingChanged -= OnAmbPerfAnimLooped;
                    _ambPerfVersion++; // invalidate any pending InvokeAsync from previous selection

                    // If a master PCC is set, try to find the matching SFXAmbPerfGameData in it
                    ExportEntry ambPerfSource = animExp;
                    if (_ambPerfMasterPcc != null)
                    {
                        var masterExp = _ambPerfMasterPcc.Exports.FirstOrDefault(
                            e => e.ClassName == animExp.ClassName && e.ObjectName == animExp.ObjectName);
                        if (masterExp != null)
                        {
                            ambPerfSource = masterExp;
                        }
                    }

                    // Build the pose graph step sequence with blend times
                    _ambPerfAnimQueue = BuildAmbPerfStepSequence(ambPerfSource);
                    _ambPerfAnimIndex = 0;

                    if (_ambPerfAnimQueue.Count > 0)
                    {
                        var firstStep = _ambPerfAnimQueue[0];
                        AnimPreviewControl.LoadAnimSequenceNonLooping(firstStep.AnimExport);
                        AnimPreviewControl.Play();
                        if (_ambPerfAnimQueue.Count > 1)
                        {
                            AnimPreviewControl.AnimationCompleted += OnAmbPerfStepCompleted;
                        }
                    }
                }
                else
                {
                    AnimPreviewControl.AnimationCompleted -= OnAmbPerfStepCompleted;
                    AnimPreviewControl.IsPlayingChanged -= OnAmbPerfAnimLooped;
                    _ambPerfVersion++;
                    _ambPerfAnimQueue = null;
                    AnimPreviewControl.LoadAnimSequence(animExp);
                    AnimPreviewControl.Play();
                }
            }
        }

        private void RestoreAmbPerfMasterPcc()
        {
            string savedPath = Settings.AssetDB_AmbPerfMasterPccPath;
            if (!string.IsNullOrEmpty(savedPath) && File.Exists(savedPath))
            {
                try
                {
                    _ambPerfMasterPcc = MEPackageHandler.OpenMEPackage(savedPath);
                    AmbPerfMasterPccPath = savedPath;
                }
                catch
                {
                    _ambPerfMasterPcc = null;
                    AmbPerfMasterPccPath = null;
                    Settings.AssetDB_AmbPerfMasterPccPath = "";
                }
            }
        }

        private void BrowseAmbPerfMasterPcc()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Master PCC for Ambient Performances",
                Filter = "Package files (*.pcc;*.sfm;*.upk;*.u)|*.pcc;*.sfm;*.upk;*.u|All files (*.*)|*.*"
            };

            string rootPath = MEDirectories.GetDefaultGamePath(CurrentGame);
            if (rootPath != null && Directory.Exists(rootPath))
            {
                dlg.InitialDirectory = rootPath;
            }

            if (dlg.ShowDialog() == true)
            {
                // Dispose previous master PCC if any
                _ambPerfMasterPcc?.Dispose();
                _ambPerfMasterPcc = null;

                try
                {
                    _ambPerfMasterPcc = MEPackageHandler.OpenMEPackage(dlg.FileName);
                    AmbPerfMasterPccPath = dlg.FileName;
                    Settings.AssetDB_AmbPerfMasterPccPath = dlg.FileName;

                    // Re-render if currently previewing an ambient performance
                    if (btn_AnimPreviewToggle.IsChecked == true
                        && lstbx_Anims.SelectedIndex >= 0
                        && lstbx_Anims.SelectedItem is AnimationRecord { IsAmbPerf: true })
                    {
                        ToggleRenderAnimation();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open package: {ex.Message}");
                    _ambPerfMasterPcc = null;
                    AmbPerfMasterPccPath = null;
                    Settings.AssetDB_AmbPerfMasterPccPath = "";
                }
            }
        }

        private void ClearAmbPerfMasterPcc()
        {
            _ambPerfMasterPcc?.Dispose();
            _ambPerfMasterPcc = null;
            AmbPerfMasterPccPath = null;
            Settings.AssetDB_AmbPerfMasterPccPath = "";

            // Re-render if currently previewing an ambient performance
            if (btn_AnimPreviewToggle.IsChecked == true
                && lstbx_Anims.SelectedIndex >= 0
                && lstbx_Anims.SelectedItem is AnimationRecord { IsAmbPerf: true })
            {
                ToggleRenderAnimation();
            }
        }

        private void OnAmbPerfAnimLooped(bool isPlaying)
        {
            if (!isPlaying || _ambPerfAnimQueue == null || _ambPerfAnimQueue.Count <= 1) return;

            // Immediately unsubscribe to prevent re-entry from render thread or Play() callbacks
            AnimPreviewControl.IsPlayingChanged -= OnAmbPerfAnimLooped;

            _ambPerfAnimIndex = (_ambPerfAnimIndex + 1) % _ambPerfAnimQueue.Count;
            var expectedVersion = _ambPerfVersion;
            Dispatcher.InvokeAsync(() =>
            {
                // Discard if the user switched to a different animation while this was queued
                if (_ambPerfVersion != expectedVersion) return;

                AnimPreviewControl.LoadAnimSequence(_ambPerfAnimQueue[_ambPerfAnimIndex].AnimExport);
                AnimPreviewControl.Play();
                AnimPreviewControl.IsPlayingChanged += OnAmbPerfAnimLooped;
            });
        }

        /// <summary>
        /// Called when a non-looping ambient performance animation step reaches its end.
        /// Crossfades into the next step in the pose graph sequence.
        /// </summary>
        private void OnAmbPerfStepCompleted()
        {
            if (_ambPerfAnimQueue == null || _ambPerfAnimQueue.Count <= 1) return;

            // Immediately unsubscribe to prevent re-entry
            AnimPreviewControl.AnimationCompleted -= OnAmbPerfStepCompleted;

            _ambPerfAnimIndex = (_ambPerfAnimIndex + 1) % _ambPerfAnimQueue.Count;
            var expectedVersion = _ambPerfVersion;
            Dispatcher.InvokeAsync(() =>
            {
                // Discard if the user switched to a different animation while this was queued
                if (_ambPerfVersion != expectedVersion) return;

                var step = _ambPerfAnimQueue[_ambPerfAnimIndex];
                if (step.BlendInTime > 0)
                {
                    AnimPreviewControl.CrossfadeToAnimSequence(step.AnimExport, step.BlendInTime);
                }
                else
                {
                    AnimPreviewControl.LoadAnimSequenceNonLooping(step.AnimExport);
                    AnimPreviewControl.Play();
                }
                AnimPreviewControl.AnimationCompleted += OnAmbPerfStepCompleted;
            });
        }

        private static List<ExportEntry> FindAllAnimSequencesInAmbPerf(ExportEntry ambPerfExport)
        {
            // SFXAmbPerfGameData has:
            //   m_aAnimsets: ArrayProperty<ObjectProperty> referencing BioDynamicAnimSet exports (or imports)
            //   m_aPoses: ArrayProperty<StructProperty> of SFXAPGDPose structs
            //   m_nStartPoseIndex: which pose to start with
            // Each pose has aTrans (transitions) with mAnimSet/mAnimSeq NameProperties
            // and aGests (gestures) with mAnimSet/mAnimSeq NameProperties.
            // We resolve these names against the BioDynamicAnimSets' Sequences.
            //
            // BioDynamicAnimSets may be local exports (often children of the SFXAmbPerfGameData),
            // or imports to other packages. We gather from both m_aAnimsets and children.

            var result = new List<ExportEntry>();
            var pkg = ambPerfExport.FileRef;

            var seqNameToExport = new Dictionary<(string setName, string seqName), ExportEntry>();
            using var cache = new PackageCache();

            // Collect BioDynamicAnimSet exports from two sources:
            // 1) m_aAnimsets property (may contain exports and/or unresolvable imports)
            // 2) Direct children of the SFXAmbPerfGameData export in the tree
            var animSetExports = new HashSet<ExportEntry>();

            var animSetRefs = ambPerfExport.GetProperty<ArrayProperty<ObjectProperty>>("m_aAnimsets");
            if (animSetRefs != null)
            {
                foreach (var animSetRef in animSetRefs)
                {
                    if (animSetRef.Value > 0 && pkg.TryGetUExport(animSetRef.Value, out var localExp))
                    {
                        animSetExports.Add(localExp);
                    }
                }
            }

            // Also scan children of the ambPerfExport for BioDynamicAnimSet exports
            // that may not be in m_aAnimsets (or when m_aAnimsets only has imports)
            foreach (var child in ambPerfExport.GetChildren())
            {
                if (child is ExportEntry childExp && childExp.ClassName == "BioDynamicAnimSet")
                {
                    animSetExports.Add(childExp);
                }
            }

            // For each BioDynamicAnimSet, read its Sequences and collect AnimSequence exports
            foreach (var animSetExport in animSetExports)
            {
                string setName = animSetExport.GetProperty<NameProperty>("m_nmOrigSetName")?.Value.Instanced
                                 ?? animSetExport.ObjectName.Instanced;

                var sequences = animSetExport.GetProperty<ArrayProperty<ObjectProperty>>("Sequences");
                if (sequences == null) continue;

                foreach (var seqRef in sequences)
                {
                    var seqExport = ResolveToExport(animSetExport.FileRef, seqRef.Value, cache);
                    if (seqExport is { ClassName: "AnimSequence" })
                    {
                        var seqName = seqExport.GetProperty<NameProperty>("SequenceName")?.Value.Instanced;
                        if (seqName != null)
                        {
                            seqNameToExport[(setName, seqName)] = seqExport;
                        }
                    }
                }
            }

            // Walk poses and collect AnimSequences in order
            var poses = ambPerfExport.GetProperty<ArrayProperty<StructProperty>>("m_aPoses");
            if (poses == null)
            {
                // Fallback: return all sequences from all AnimSets
                result.AddRange(seqNameToExport.Values);
                return result;
            }

            var added = new HashSet<ExportEntry>();
            foreach (var pose in poses)
            {
                // Collect from transitions (aTrans)
                CollectAnimSeqsFromStructArray(pose.GetProp<ArrayProperty<StructProperty>>("aTrans"), seqNameToExport, result, added);
                // Collect from gestures (aGests)
                CollectAnimSeqsFromStructArray(pose.GetProp<ArrayProperty<StructProperty>>("aGests"), seqNameToExport, result, added);
            }

            // If no poses matched, fall back to all sequences
            if (result.Count == 0)
            {
                result.AddRange(seqNameToExport.Values);
            }

            return result;
        }

        private static void CollectAnimSeqsFromStructArray(
            ArrayProperty<StructProperty> structs,
            Dictionary<(string setName, string seqName), ExportEntry> lookup,
            List<ExportEntry> result,
            HashSet<ExportEntry> added)
        {
            if (structs == null) return;

            foreach (var s in structs)
            {
                var setName = s.GetProp<NameProperty>("mAnimSet")?.Value.Instanced;
                var seqName = s.GetProp<NameProperty>("mAnimSeq")?.Value.Instanced;
                if (setName != null && seqName != null
                    && lookup.TryGetValue((setName, seqName), out var exp)
                    && added.Add(exp))
                {
                    result.Add(exp);
                }
            }
        }

        /// <summary>
        /// Builds a sequence of animation steps by walking the SFXAmbPerfGameData pose graph.
        /// Each step includes the animation to play and the blend time for transitioning into it.
        /// The sequence follows: pose idle → transition anim → dest pose idle → transition → ...
        /// </summary>
        private static List<AmbPerfStep> BuildAmbPerfStepSequence(ExportEntry ambPerfExport)
        {
            var pkg = ambPerfExport.FileRef;
            var seqNameToExport = new Dictionary<(string setName, string seqName), ExportEntry>();
            using var cache = new PackageCache();

            // Collect BioDynamicAnimSet exports (same resolution as FindAllAnimSequencesInAmbPerf)
            var animSetExports = new HashSet<ExportEntry>();

            var animSetRefs = ambPerfExport.GetProperty<ArrayProperty<ObjectProperty>>("m_aAnimsets");
            if (animSetRefs != null)
            {
                foreach (var animSetRef in animSetRefs)
                {
                    if (animSetRef.Value > 0 && pkg.TryGetUExport(animSetRef.Value, out var localExp))
                    {
                        animSetExports.Add(localExp);
                    }
                }
            }

            foreach (var child in ambPerfExport.GetChildren())
            {
                if (child is ExportEntry childExp && childExp.ClassName == "BioDynamicAnimSet")
                {
                    animSetExports.Add(childExp);
                }
            }

            foreach (var animSetExport in animSetExports)
            {
                string setName = animSetExport.GetProperty<NameProperty>("m_nmOrigSetName")?.Value.Instanced
                                 ?? animSetExport.ObjectName.Instanced;

                var sequences = animSetExport.GetProperty<ArrayProperty<ObjectProperty>>("Sequences");
                if (sequences == null) continue;

                foreach (var seqRef in sequences)
                {
                    var seqExport = ResolveToExport(animSetExport.FileRef, seqRef.Value, cache);
                    if (seqExport is { ClassName: "AnimSequence" })
                    {
                        var seqName = seqExport.GetProperty<NameProperty>("SequenceName")?.Value.Instanced;
                        if (seqName != null)
                        {
                            seqNameToExport[(setName, seqName)] = seqExport;
                        }
                    }
                }
            }

            // Walk the pose graph starting from m_nStartPoseIndex
            var poses = ambPerfExport.GetProperty<ArrayProperty<StructProperty>>("m_aPoses");
            var startIdx = ambPerfExport.GetProperty<IntProperty>("m_nStartPoseIndex")?.Value ?? 0;
            var result = new List<AmbPerfStep>();

            if (poses == null || poses.Count == 0)
            {
                // Fallback: return all sequences with no blend
                foreach (var exp in seqNameToExport.Values)
                {
                    result.Add(new AmbPerfStep(exp, 0f));
                }
                return result;
            }

            var visited = new HashSet<int>();
            int currentIdx = Math.Clamp(startIdx, 0, poses.Count - 1);
            bool isFirst = true;
            const float defaultIdleBlend = 0.25f;

            while (currentIdx >= 0 && currentIdx < poses.Count && visited.Add(currentIdx))
            {
                var pose = poses[currentIdx];

                // Add the pose's idle animation
                var idleSet = pose.GetProp<NameProperty>("mAnimSet")?.Value.Instanced;
                var idleSeq = pose.GetProp<NameProperty>("mAnimSeq")?.Value.Instanced;
                ExportEntry idleExport = null;
                if (idleSet != null && idleSeq != null)
                {
                    seqNameToExport.TryGetValue((idleSet, idleSeq), out idleExport);
                }
                if (idleExport != null)
                {
                    result.Add(new AmbPerfStep(idleExport, isFirst ? 0f : defaultIdleBlend));
                    isFirst = false;
                }

                // Pick the best transition (highest nPlayChance)
                var transitions = pose.GetProp<ArrayProperty<StructProperty>>("aTrans");
                if (transitions == null || transitions.Count == 0) break;

                StructProperty bestTrans = null;
                int bestChance = -1;
                foreach (var trans in transitions)
                {
                    int chance = trans.GetProp<IntProperty>("nPlayChance")?.Value ?? 0;
                    if (chance > bestChance)
                    {
                        bestChance = chance;
                        bestTrans = trans;
                    }
                }

                if (bestTrans == null) break;

                var transSet = bestTrans.GetProp<NameProperty>("mAnimSet")?.Value.Instanced;
                var transSeq = bestTrans.GetProp<NameProperty>("mAnimSeq")?.Value.Instanced;
                float blendTime = bestTrans.GetProp<FloatProperty>("fBlendTime")?.Value ?? 0f;
                int destIdx = bestTrans.GetProp<IntProperty>("nDestPoseIndex")?.Value ?? -1;

                if (transSet != null && transSeq != null
                    && seqNameToExport.TryGetValue((transSet, transSeq), out var transExport))
                {
                    // Skip if the transition animation is identical to the idle we just added
                    if (transExport != idleExport)
                    {
                        result.Add(new AmbPerfStep(transExport, blendTime));
                        isFirst = false;
                    }
                }

                currentIdx = destIdx;
            }

            // Remove consecutive duplicate animations (can happen when transition anim == dest idle)
            for (int i = result.Count - 1; i > 0; i--)
            {
                if (result[i].AnimExport == result[i - 1].AnimExport)
                {
                    result.RemoveAt(i);
                }
            }

            if (result.Count == 0)
            {
                // Fallback: return all sequences with no blend
                foreach (var exp in seqNameToExport.Values)
                {
                    result.Add(new AmbPerfStep(exp, 0f));
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves an object reference (positive = export, negative = import) to an ExportEntry.
        /// Uses a direct file lookup as fallback when standard import resolution fails
        /// (e.g. when the package has RequireImportsAlreadyLoaded set).
        /// Returns null if the reference cannot be resolved.
        /// </summary>
        private static ExportEntry ResolveToExport(IMEPackage pkg, int uIndex, PackageCache cache)
        {
            if (uIndex > 0 && pkg.TryGetUExport(uIndex, out var exp))
                return exp;
            if (uIndex < 0 && pkg.TryGetImport(uIndex, out var imp))
            {
                // Try standard resolution first
                try
                {
                    var resolved = EntryImporter.ResolveImport(imp, cache);
                    if (resolved != null) return resolved;
                }
                catch { /* fall through to manual lookup */ }

                // Fallback: directly look up the source file by the import's root package name
                try
                {
                    string rootName = imp.GetRootName();
                    var gameFiles = MELoadedFiles.GetFilesLoadedInGame(pkg.Game, forceUseCached: true);
                    string ext = pkg.Game == MEGame.ME1 ? ".sfm" : ".pcc";
                    foreach (var tryName in new[] { $"{rootName}{ext}", $"{rootName}.upk", $"{rootName}.u" })
                    {
                        if (gameFiles.TryGetValue(tryName, out var filePath))
                        {
                            var sourcePkg = cache.GetCachedPackage(filePath, true);
                            if (sourcePkg == null) continue;

                            string ifp = imp.InstancedFullPath;
                            // Try full path first (non-ForcedExport)
                            var sourceExport = sourcePkg.FindExport(ifp);
                            if (sourceExport != null) return sourceExport;

                            // Try stripping root package name (ForcedExport style)
                            if (ifp.StartsWith($"{rootName}.", StringComparison.OrdinalIgnoreCase))
                            {
                                sourceExport = sourcePkg.FindExport(ifp.Substring(rootName.Length + 1));
                                if (sourceExport != null) return sourceExport;
                            }
                            break;
                        }
                    }
                }
                catch { /* give up */ }
            }
            return null;
        }

        private void AnimPreview_MeshSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadSkeletalMeshForAnimPreview();
        }

        private void LoadSkeletalMeshForAnimPreview()
        {
            if (cbx_AnimPreviewMesh.SelectedItem is MeshRecord meshRecord && meshRecord.Usages.Any())
            {
                string filePath = null;
                int uIndex = 0;
                // find the first usage that we can actually resolve. This will skip over mods that have been removed since the database was generated
                foreach (var (fileKey, tempUIndex, _) in meshRecord.Usages)
                {
                    filePath = GetFilePath(fileKey);
                    if (filePath != null)
                    {
                        uIndex = tempUIndex;
                        break;
                    }
                }

                // in case we can't find a resolvable usage, clear the animation preview
                if (filePath == null)
                {
                    AnimPreviewControl.Clear();
                    return;
                }

                using var meshPackage = MEPackageHandler.OpenMEPackage(filePath);
                if (meshPackage.IsUExport(uIndex))
                {
                    AnimPreviewControl.LoadSkeletalMesh(meshPackage.GetUExport(uIndex));
                }
            }
        }

        private void ToggleRenderTexture()
        {
            bool showText = btn_TextRenderToggle.IsChecked == true && lstbx_Textures.SelectedIndex >= 0 && CurrentDataBase.Textures[lstbx_Textures.SelectedIndex].Usages.Count > 0 && currentView == 4;

            var selecteditem = (TextureRecord)lstbx_Textures.SelectedItem;
            if (!showText || selecteditem.CFormat == "TextureCube")
            {
                EmbeddedTextureViewerTab_EmbeddedTextureViewer.UnloadExport();
                BIKExternalExportLoaderTab_BIKExternalExportLoader.UnloadExport();
                EmbeddedTextureViewerTab_EmbeddedTextureViewer.Visibility = Visibility.Visible;
                BIKExternalExportLoaderTab_BIKExternalExportLoader.Visibility = Visibility.Collapsed;
                textPcc?.Dispose();
                return;
            }

            var filekey = selecteditem.Usages[0].FileKey;
            var (filename, dirKey) = CurrentDataBase.FileList[filekey];
            string rootPath = MEDirectories.GetDefaultGamePath(CurrentGame);
            var cdir = CurrentDataBase.ContentDir[dirKey];
            if (rootPath == null)
            {
                MessageBox.Show($"{CurrentGame} has not been found. Please check your Legendary Explorer settings");
                return;
            }

            filename = $"{filename}.*";
            var files = Directory.GetFiles(rootPath, filename, SearchOption.AllDirectories).ToList();
            if (files.IsEmpty())
            {
                MessageBox.Show($"File {filename} not found.");
                return;
            }

            if (textPcc != null)
            {
                EmbeddedTextureViewerTab_EmbeddedTextureViewer.UnloadExport();
                BIKExternalExportLoaderTab_BIKExternalExportLoader.UnloadExport();
                textPcc.Dispose();
            }

            foreach (var filePath in files) //handle cases of mods/dlc having same file.
            {
                bool isBaseFile = cdir.ToLower() == "biogame";
                bool isDLCFile = filePath.ToLower().Contains("dlc");
                if (isBaseFile == isDLCFile)
                {
                    continue;
                }
                //textPcc = MEPackageHandler.UnsafePartialLoad(filePath, x=>x.UIndex); // maybe use unsafe load?
                textPcc = MEPackageHandler.UnsafePartialLoad(filePath, x => x.UIndex == selecteditem.Usages[0].UIndex); // maybe use unsafe load?
                var uexpIdx = selecteditem.Usages[0].UIndex;
                if (uexpIdx <= textPcc.ExportCount)
                {
                    var textExp = textPcc.GetUExport(uexpIdx);
                    string cubemapParent = null;
                    if (textExp.Parent != null)
                    {
                        cubemapParent = textExp.Parent.ClassName == "CubeMap" ? selecteditem.TextureName.Substring(textExp.Parent.ObjectName.ToString().Length + 1) : null;
                    }
                    string indexedName = $"{textExp.ObjectNameString}_{textExp.indexValue - 1}";
                    if (textExp.ClassName.StartsWith("Texture") && (textExp.ObjectNameString == selecteditem.TextureName || selecteditem.TextureName == indexedName || (cubemapParent != null && textExp.ObjectNameString == cubemapParent)))
                    {
                        if (selecteditem.CFormat == "TextureMovie")
                        {
                            BIKExternalExportLoaderTab_BIKExternalExportLoader.LoadExport(textExp);
                            BIKExternalExportLoaderTab_BIKExternalExportLoader.Visibility = Visibility.Visible;
                            EmbeddedTextureViewerTab_EmbeddedTextureViewer.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            EmbeddedTextureViewerTab_EmbeddedTextureViewer.LoadExport(textExp);
                            EmbeddedTextureViewerTab_EmbeddedTextureViewer.Visibility = Visibility.Visible;
                            BIKExternalExportLoaderTab_BIKExternalExportLoader.Visibility = Visibility.Collapsed;
                        }
                        break;
                    }
                }
                textPcc.Dispose();
            }
        }

        private void ToggleLinePlayback(bool startPlayback = false)
        {
            bool showAudio = btn_LinePlaybackToggle.IsChecked == true && lstbx_Lines.SelectedIndex >= 0 && CurrentConvo.Item1 != null && currentView == 8;

            if (!showAudio)
            {
                SoundpanelWPF_ADB.UnloadExport();
                audioPcc?.Dispose();
                return;
            }

            var selecteditem = (ConvoLine)lstbx_Lines.SelectedItem;
            var filename = CurrentConvo.Item2;
            var cdir = CurrentConvo.Item4;
            string rootPath = MEDirectories.GetDefaultGamePath(CurrentGame);
            if (rootPath == null)
            {
                MessageBox.Show($"{CurrentGame} has not been found. Please check your Legendary Explorer settings");
                return;
            }

            filename = $"{filename}.*";
            var files = Directory.GetFiles(rootPath, filename, SearchOption.AllDirectories).ToList();
            if (files.IsEmpty())
            {
                MessageBox.Show($"File {filename} not found.");
                return;
            }

            string searchWav = $"{selecteditem.StrRef}_m";
            if (genderTabs.SelectedIndex == 1)
                searchWav = $"{selecteditem.StrRef}_f";

            if (audioPcc != null)
            {
                if (Path.GetFileNameWithoutExtension(audioPcc.FilePath) == CurrentConvo.Item2) //if switching gender file is already loaded
                {
                    var stream = audioPcc.Exports.FirstOrDefault(x => x.ClassName == "WwiseStream" && x.ObjectNameString.ToLower().Contains(searchWav));
                    if (stream != null)
                    {
                        SoundpanelWPF_ADB.LoadExport(stream);
                        if (startPlayback)
                        {
                            SoundpanelWPF_ADB.StartPlayingCurrentSelection();
                        }
                        return;
                    }
                }
                SoundpanelWPF_ADB.UnloadExport();
                audioPcc.Dispose();
            }

            foreach (var filePath in files) //handle cases of mods/dlc having same file.
            {
                bool isBaseFile = cdir.ToLower() == "biogame";
                bool isDLCFile = filePath.ToLower().Contains("dlc");
                if (isBaseFile == isDLCFile)
                {
                    continue;
                }
                audioPcc = MEPackageHandler.OpenMEPackage(filePath);
                if (currentGame.IsGame1())
                {
                    var stream = audioPcc.Exports.FirstOrDefault(x => x.ClassName == "SoundNodeWave" && x.InstancedFullPath.ToLower().EndsWith(searchWav));
                    if (stream != null)
                    {
                        SoundpanelWPF_ADB.LoadExport(stream);
                        if (startPlayback)
                        {
                            SoundpanelWPF_ADB.StartPlayingCurrentSelection();
                        }
                        break;
                    }
                    audioPcc.Dispose();
                }
                else
                {
                    var stream = audioPcc.Exports.FirstOrDefault(x => x.ClassName == "WwiseStream" && x.ObjectNameString.ToLower().Contains(searchWav));
                    if (stream != null)
                    {
                        SoundpanelWPF_ADB.LoadExport(stream);
                        if (startPlayback)
                        {
                            SoundpanelWPF_ADB.StartPlayingCurrentSelection();
                        }
                        break;
                    }
                    audioPcc.Dispose();
                }
            }
        }

        private void SetCRCScan(object obj)
        {
            if (menu_checkCRC.IsChecked)
            {
                menu_checkCRC.IsChecked = false;
            }
            else
            {
                var crcdlg = MessageBox.Show("Do you want to turn on CRC checking? This will significantly increase scan times.", "Asset Database", MessageBoxButton.YesNo);
                if (crcdlg == MessageBoxResult.Yes)
                {
                    menu_checkCRC.IsChecked = true;
                }
            }
        }

        private void OpenInAnimViewer(object obj)
        {
            if (lstbx_Anims.SelectedItem is AnimationRecord anim)
            {
                if (!Application.Current.Windows.OfType<AnimationViewer.AnimationViewerWindow>().Any())
                {
                    var av = new AnimationViewer.AnimationViewerWindow(CurrentDataBase, anim);
                    av.Show();
                }
                else
                {
                    var aexp = Application.Current.Windows.OfType<AnimationViewer.AnimationViewerWindow>().First();
                    if (aexp.ReadyToView)
                    {
                        aexp.LoadAnimation(anim);
                    }
                    else
                    {
                        aexp.AnimQueuedForFocus = anim;
                    }
                    aexp.Focus();
                }
            }
        }

        private void ExportToPSA()
        {
            if (lstbx_Anims.SelectedItem is AnimationRecord anim && anim.Usages.Any())
            {
                var (fileListIndex, animUIndex, _) = anim.Usages[0];
                string filePath = GetFilePath(fileListIndex);
                using IMEPackage pcc = MEPackageHandler.OpenMEPackage(filePath);
                if (pcc.IsUExport(animUIndex) && pcc.GetUExport(animUIndex) is ExportEntry animSeqExp && ObjectBinary.From(animSeqExp) is AnimSequence animSequence)
                {
                    var dlg = new SaveFileDialog
                    {
                        Filter = AnimationImporterExporter.AnimationImporterExporterWindow.PSAFilter,
                        FileName = $"{anim.SeqName}.psa",
                        AddExtension = true
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        PSA.CreateFrom(animSequence).ToFile(dlg.FileName);
                        MessageBox.Show("Done!", "PSA Export", MessageBoxButton.OK);
                    }
                }
            }
        }

        private void OpenInAnimationImporter()
        {
            if (lstbx_Anims.SelectedItem is AnimationRecord anim && anim.Usages.Any())
            {
                (int fileListIndex, int animUIndex, bool _) = anim.Usages[0];
                string filePath = GetFilePath(fileListIndex);
                var animImporter = new AnimationImporterExporter.AnimationImporterExporterWindow(filePath, animUIndex);
                animImporter.Show();
                animImporter.Activate();
            }
        }

        private string GetFilePath(int fileListIndex)
        {
            (string filename, string contentdir, int mount) = FileListExtended[fileListIndex];
            var retFile = Directory.GetFiles(MEDirectories.GetDefaultGamePath(CurrentGame), $"{filename}.*", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains(contentdir));
            if (retFile != null)
                return retFile;
            if (CurrentGame == MEGame.ME3)
            {
                var sfar = Path.Combine(MEDirectories.GetDLCPath(MEGame.ME3), contentdir, "CookedPCConsole", "Default.sfar");
                if (File.Exists(sfar))
                {
                    DLCPackage dlp = new DLCPackage(sfar);
                    var dlpFile = dlp.FindFileEntry(filename);
                    if (dlpFile != -1)
                    {
                        // Technically we should check this is not an override by checking the uindex, but I don't care.
                        return sfar; // It's in the SFAR
                    }
                }
            }

            return null;
        }

        private void genderTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (currentView == 8 && (btn_LinePlaybackToggle.IsChecked ?? false))
            {
                ToggleLinePlayback();
                ;
            }
        }

        private void CopyStringToClipboard(object obj)
        {
            if (!(obj is string cmd))
                return;
            Clipboard.Clear();
            string copytext = null;
            switch (cmd)
            {
                case "Line":
                    var line = (ConvoLine)lstbx_Lines.SelectedItem;
                    copytext = line.Line;
                    break;
                case "StrRef":
                    var lineref = (ConvoLine)lstbx_Lines.SelectedItem;
                    copytext = lineref.StrRef.ToString();
                    break;
                default:
                    break;
            }

            if (copytext == null)
                return;

            Clipboard.SetText(copytext);
        }

        #endregion

        #region Filters

        bool LineFilter(object d)
        {
            if (d is ConvoLine line)
            {
                bool showthis = true;
                if (cmbbx_filterSpkrs.SelectedIndex >= 0)
                {
                    showthis = string.Equals(line.Speaker, cmbbx_filterSpkrs.SelectedItem.ToString(), StringComparison.CurrentCultureIgnoreCase);
                }
                if (showthis && !string.IsNullOrWhiteSpace(LineSearchText))
                {
                    showthis = LineMatchesSearch(line, LineSearchText);
                }

                return showthis;
            }

            return false;
        }

        private bool LineMatchesSearch(ConvoLine line, string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            return SelectedLineSearchColumn switch
            {
                SpeakerLineSearchColumn => ContainsText(line.Speaker, searchText),
                TlkStringRefLineSearchColumn => ContainsText(line.StrRef.ToString(), searchText),
                LineTextSearchColumn => ContainsText(line.Line, searchText),
                LineConversationSearchColumn => ContainsText(line.Convo, searchText),
                FileLineSearchColumn => ContainsText(GetConvoFileValue(line.Convo), searchText),
                LocationLineSearchColumn => ContainsText(GetConvoLocationValue(line.Convo), searchText),
                _ => ContainsText(line.Speaker, searchText)
                     || ContainsText(line.StrRef.ToString(), searchText)
                     || ContainsText(line.Line, searchText)
                     || ContainsText(line.Convo, searchText)
                     || ContainsText(GetConvoFileValue(line.Convo), searchText)
                     || ContainsText(GetConvoLocationValue(line.Convo), searchText)
            };
        }

        private bool ContainsText(string source, string searchText)
        {
            return !string.IsNullOrEmpty(source) && source.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
        }

        private string GetConvoFileValue(string convoName)
        {
            if (!TryGetConvoFileInfo(convoName, out var fileName, out _))
            {
                return null;
            }

            return fileName;
        }

        private string GetConvoLocationValue(string convoName)
        {
            if (!TryGetConvoFileInfo(convoName, out _, out var location))
            {
                return null;
            }

            return location;
        }

        private bool TryGetConvoFileInfo(string convoName, out string fileName, out string location)
        {
            fileName = null;
            location = null;

            if (string.IsNullOrEmpty(convoName))
            {
                return false;
            }

            var convo = CurrentDataBase.Conversations.FirstOrDefault(c => c.ConvName == convoName);
            if (convo == null)
            {
                return false;
            }

            int fileKey = convo.ConvFile.FileKey;
            if (fileKey < 0 || fileKey >= FileListExtended.Count)
            {
                return false;
            }

            fileName = FileListExtended[fileKey].FileName;
            location = FileListExtended[fileKey].Directory;
            return true;
        }

        private bool FileFilter(object d)
        {
            bool showthis = true;
            var f = (FileDirPair)d;
            var t = FilterBox.Text;
            if (!string.IsNullOrEmpty(t))
            {
                showthis = f.FileName.Contains(t, StringComparison.CurrentCultureIgnoreCase);
                if (!showthis)
                {
                    showthis = f.Directory.Contains(t, StringComparison.CurrentCultureIgnoreCase);
                }
            }
            return showthis;
        }

        private void Filter()
        {
            AssetFilters.SetSearch(FilterBox.Text);
            switch (currentView)
            {
                case 1: //Classes
                    ICollectionView viewC = CollectionViewSource.GetDefaultView(CurrentDataBase.ClassRecords);
                    viewC.Filter = AssetFilters.ClassFilter.Filter;
                    lstbx_Classes.ItemsSource = viewC;
                    break;
                case 2: //Materials
                    ICollectionView viewM = CollectionViewSource.GetDefaultView(CurrentDataBase.Materials);
                    viewM.Filter = MaterialTabFilter;
                    lstbx_Materials.ItemsSource = viewM;
                    break;
                case 3: //Meshes
                    ICollectionView viewS = CollectionViewSource.GetDefaultView(CurrentDataBase.Meshes);
                    viewS.Filter = MeshTabFilter;
                    lstbx_Meshes.ItemsSource = viewS;
                    break;
                case 4: //Textures
                    ICollectionView viewT = CollectionViewSource.GetDefaultView(CurrentDataBase.Textures);
                    viewT.Filter = TextureTabFilter;
                    lstbx_Textures.ItemsSource = viewT;
                    break;
                case 5: //Animations
                    ICollectionView viewA = CollectionViewSource.GetDefaultView(CurrentDataBase.Animations);
                    viewA.Filter = AssetFilters.AnimationFilter.Filter;
                    lstbx_Anims.ItemsSource = viewA;
                    List<MeshRecord> meshRecords = CurrentDataBase.Meshes.Where(m => m.IsSkeleton).ToList();
                    cbx_AnimPreviewMesh.ItemsSource = meshRecords;

                    //Tali
                    string defaultMesh = CurrentGame switch
                    {
                        MEGame.LE1 => "QRN_FAC_ARM_LGTa_MDL",
                        MEGame.ME1 => "QRN_FAC_ARM_LGTa_MDL",
                        MEGame.LE2 => "QRN_TLI_LGTa_MDL",
                        MEGame.ME2 => "QRN_TLI_LGTa_MDL",
                        //LE3/ME3
                        _ => "QRN_ARM_TLIa_MDL"
                    };
                    if (meshRecords.FindIndex(mr => mr.MeshName == defaultMesh) is int idx and > 0)
                    {
                        cbx_AnimPreviewMesh.SelectedIndex = idx;
                    }
                    break;
                case 6: //Particles
                    ICollectionView viewP = CollectionViewSource.GetDefaultView(CurrentDataBase.Particles);
                    viewP.Filter = VfxTabFilter;
                    lstbx_Particles.ItemsSource = viewP;
                    break;
                case 7: //Scaleform
                    ICollectionView viewG = CollectionViewSource.GetDefaultView(CurrentDataBase.GUIElements);
                    viewG.Filter = AssetFilters.GUIFilter.Filter;
                    lstbx_Scaleform.ItemsSource = viewG;
                    break;
                case 8: //Lines
                    ICollectionView viewL = CollectionViewSource.GetDefaultView(CurrentDataBase.Lines);
                    viewL.Filter = LineFilter;
                    lstbx_Lines.ItemsSource = viewL;
                    break;
                case 9: // PlotElements
                    var lstbx = GetSelectedPlotListBox();
                    var plotSource = GetSelectedPlotSource();
                    if (plotSource is null || lstbx is null) break;
                    ICollectionView viewPE = CollectionViewSource.GetDefaultView(plotSource);
                    viewPE.Filter = AssetFilters.PlotElementFilter.Filter;
                    lstbx.ItemsSource = viewPE;
                    break;
                default: //Files
                    lstbx_Files.Items.Filter = FileFilter;
                    break;
            }
        }

        private void SetFilters(object obj)
        {
            if (!AssetFilters.ToggleFilter(obj))
            {
                var param = obj as string;
                switch (param)
                {
                    case "CustFiles":
                        if (FileListFilter.IsSelected)
                        {
                            btn_custFilter.Content = "Filtered";
                            expander_CustomFiles.IsExpanded = true;
                        }
                        else
                        {
                            btn_custFilter.Content = "Filter";
                            if (FileListFilter.CustomFileList.IsEmpty())
                                expander_CustomFiles.IsExpanded = false;
                        }
                        break;
                    default:
                        break;
                }
            }
            Filter();
        }

        private void FilterBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (IsGettingTLKs && currentView == 8)
            {
                MessageBox.Show("Currently parsing TLK line data. Please wait.", "Asset Database", MessageBoxButton.OK);
                return;
            }
            Filter();
        }

        private void views_ColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewColumnHeader headerClicked)
            {
                if (headerClicked.Role != GridViewColumnHeaderRole.Padding)
                {
                    ListSortDirection direction;
                    if (headerClicked != _lastHeaderClicked)
                    {
                        direction = ListSortDirection.Ascending;
                    }
                    else
                    {
                        if (_lastDirection == ListSortDirection.Ascending)
                        {
                            direction = ListSortDirection.Descending;
                        }
                        else
                        {
                            direction = ListSortDirection.Ascending;
                        }
                    }

                    string primarySort;
                    string secondarySort;
                    switch (currentView)
                    {
                        case 0:
                            ICollectionView dataView = CollectionViewSource.GetDefaultView(lstbx_Files.ItemsSource);
                            primarySort = "Directory";
                            secondarySort = "FileName";
                            var header = headerClicked.Column.Header.ToString();
                            switch (header)
                            {
                                case "FileName":
                                    primarySort = "FileName";
                                    secondarySort = "Directory";
                                    break;
                                case "Mount":
                                    primarySort = "Mount";
                                    secondarySort = "FileName";
                                    break;
                            }

                            dataView.SortDescriptions.Clear();
                            dataView.SortDescriptions.Add(new SortDescription(primarySort, direction));
                            dataView.SortDescriptions.Add(new SortDescription(secondarySort, direction));
                            dataView.Refresh();
                            lstbx_Files.ItemsSource = dataView;
                            break;
                        case 8:
                            ICollectionView linedataView = CollectionViewSource.GetDefaultView(lstbx_Lines.ItemsSource);
                            primarySort = headerClicked.Column.Header.ToString();
                            linedataView.SortDescriptions.Clear();
                            linedataView.SortDescriptions.Add(new SortDescription(primarySort, direction));
                            linedataView.Refresh();
                            lstbx_Lines.ItemsSource = linedataView;
                            break;
                        default:
                            return;
                    }

                    if (direction == ListSortDirection.Ascending)
                    {
                        headerClicked.Column.HeaderTemplate = Resources["HeaderTemplateArrowUp"] as DataTemplate;
                    }
                    else
                    {
                        headerClicked.Column.HeaderTemplate = Resources["HeaderTemplateArrowDown"] as DataTemplate;
                    }

                    // Remove arrow from previously sorted header
                    if (_lastHeaderClicked != null && _lastHeaderClicked != headerClicked)
                    {
                        _lastHeaderClicked.Column.HeaderTemplate = null;
                    }

                    _lastHeaderClicked = headerClicked;
                    _lastDirection = direction;
                }
            }
        }

        private void cmbbx_filterSpkrs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            Filter();
        }

        private void ClearSpeakerFilter_Click(object sender, RoutedEventArgs e)
        {
            cmbbx_filterSpkrs.SelectedIndex = -1;
            Filter();
        }

        private void SaveCustomFileList()
        {
            if (FileListFilter.CustomFileList.IsEmpty())
            {
                MessageBox.Show("You cannot save an empty file list.", "Save File List", MessageBoxButton.OK);
                return;
            }

            string directory = Path.GetDirectoryName(CurrentDBPath);

            SaveFileDialog d = new()
            {
                Filter = $"*.txt|*.txt",
                InitialDirectory = directory,
                FileName = $"ADB_{CurrentGame}_*.txt",
                AddExtension = true
            };
            if (d.ShowDialog() == true)
            {
                TextWriter tw = new StreamWriter(d.FileName);
                foreach (KeyValuePair<int, string> file in FileListFilter.CustomFileList)
                {
                    tw.WriteLine($"{file.Value} {file.Key}");
                }
                tw.Close();
                MessageBox.Show("Done.");
            }
        }

        private void LoadCustomFileList()
        {
            string directory = Path.GetDirectoryName(CurrentDBPath);
            OpenFileDialog d = new()
            {
                Filter = $"*.txt|*.txt",
                InitialDirectory = directory,
                FileName = $"ADB_{CurrentGame}_*.txt",
                AddExtension = true
            };
            if (d.ShowDialog() == true)
            {
                TextReader tr = new StreamReader(d.FileName);
                string name = "";
                var nameslist = new List<string>();
                while ((name = tr.ReadLine()) != null)
                {
                    nameslist.Add(name);
                }

                var cdlg = MessageBox.Show($"Replace current list with these names:\n{string.Join("\n", nameslist)}", "Asset Database", MessageBoxButton.YesNo);
                if (cdlg == MessageBoxResult.No)
                    return;
                FileListFilter.CustomFileList.Clear();
                var errorlist = new List<string>();
                foreach (var n in nameslist)
                {
                    string[] parts = n.Split(' ');
                    if (parts.Length >= 2)
                    {
                        FileDirPair fdp = null;
                        int key = -1;
                        var (fileName, fileDir) = (parts[0], parts[1]);
                        if (parts.Length > 2 && int.TryParse(parts[2], out key) && key < FileListExtended.Count)
                        {
                            fdp = FileListExtended[key];
                            if (fdp.FileName != fileName || fdp.Directory != fileDir) fdp = null;
                        }

                        if (fdp is null)
                        {
                            fdp = FileListExtended.FirstOrDefault(t => t.FileName == fileName && t.Directory == fileDir);
                            key = FileListExtended.IndexOf(fdp);
                        }

                        if (fdp is not null)
                        {
                            FileListFilter.CustomFileList.Add(key, $"{fdp.FileName} {fdp.Directory}");
                            continue;
                        }
                    }
                    errorlist.Add(n);
                }

                if (!errorlist.IsEmpty())
                {
                    MessageBox.Show($"The following files are not in the {CurrentGame} database:\n{string.Join(", ", errorlist)}");
                }
            }
        }

        private void EditCustomFileList(object obj)
        {
            var action = obj as string;
            int FileKey = -1;
            switch (action)
            {
                case "Add":
                    if (lstbx_Usages.SelectedIndex >= 0 && currentView == 1)
                    {
                        var c = (ClassUsage)lstbx_Usages.SelectedItem;
                        FileKey = c.FileKey;
                    }
                    else if (GetSelectedPanelUsage(currentView) is IAssetUsage panelUsage)
                    {
                        FileKey = panelUsage.FileKey;
                    }
                    else if (lstbx_Lines.SelectedIndex >= 0 && currentView == 8)
                    {
                        FileKey = FileListExtended.FindIndex(f => f.FileName == CurrentConvo.Item2);
                    }
                    else if (currentView == 9 && lstbx_PlotUsages.SelectedIndex >= 0)
                    {
                        var pu = (PlotUsage)lstbx_PlotUsages.SelectedItem;
                        FileKey = pu.FileKey;
                    }
                    else if (lstbx_Files.SelectedIndex >= 0 && currentView == 0)
                    {
                        foreach (var fr in lstbx_Files.SelectedItems)
                        {
                            var fileref = (FileDirPair)fr;
                            FileKey = FileListExtended.IndexOf(fileref);
                            if (!FileListFilter.CustomFileList.ContainsKey(FileKey))
                            {
                                var file = FileListExtended[FileKey];
                                FileListFilter.CustomFileList.Add(FileKey, $"{file.FileName} {file.Directory}");
                            }
                        }
                        FileKey = -1;
                    }
                    if (!expander_CustomFiles.IsExpanded)
                        expander_CustomFiles.IsExpanded = true;
                    if (FileKey >= 0 && !FileListFilter.CustomFileList.ContainsKey(FileKey))
                    {
                        var file = FileListExtended[FileKey];
                        FileListFilter.CustomFileList.Add(FileKey, $"{file.FileName} {file.Directory}");
                    }
                    SortedDictionary<int, string> orderlist = new SortedDictionary<int, string>();
                    foreach (KeyValuePair<int, string> file in FileListFilter.CustomFileList)
                    {
                        orderlist.Add(file.Key, file.Value);
                    }
                    FileListFilter.CustomFileList.Clear();
                    FileListFilter.CustomFileList.AddRange(orderlist);
                    break;
                case "Remove":
                    if (lstbx_CustomFiles.SelectedIndex >= 0 && currentView == 0)
                    {
                        var cf = (KeyValuePair<int, string>)lstbx_CustomFiles.SelectedItem;
                        FileKey = cf.Key;
                    }
                    if (FileKey >= 0 && FileListFilter.CustomFileList.ContainsKey(FileKey))
                        FileListFilter.CustomFileList.Remove(FileKey);
                    break;
                case "Clear":
                    FileListFilter.CustomFileList.Clear();
                    break;
                default:
                    break;
            }
            Filter();
        }

        public void UpdateSelectedClassUsages()
        {
            if (ShowAllClassUsages)
            {
                SelectedClassUsages = SelectedClass?.Usages.OrderBy(u => u.FileKey).ToList();
            }
            else
            {
                SelectedClassUsages = SelectedClass?.Usages.OrderBy(u => u.FileKey).Aggregate(new List<ClassUsage>(), (list, usage) =>
                {
                    if (list.Count == 0 || usage.IsDefault || list[list.Count - 1].FileKey != usage.FileKey)
                    {
                        list.Add(usage);
                    }

                    return list;
                });
            }
        }

        #endregion

        #region Scan

        // 05/02/2025 - Add .sfar
        private static List<string> SupportedFileExtensions = new List<string> { ".u", ".upk", ".sfm", ".pcc", ".cnd", ".sfar" };

        private async void ScanGame()
        {
            string rootPath = MEDirectories.GetDefaultGamePath(CurrentGame);

            if (rootPath == null || !Directory.Exists(rootPath))
            {
                MessageBox.Show($"{CurrentGame} has not been found. Please check your Legendary Explorer settings");
                return;
            }

            rootPath = Path.GetFullPath(rootPath);

            string ShaderCacheName = CurrentGame.IsLEGame() ? "RefShaderCache-PC-D3D-SM5.upk" : "RefShaderCache-PC-D3D-SM3.upk";
            List<string> files = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories).Where(s => SupportedFileExtensions.Contains(Path.GetExtension(s.ToLower())) && !s.EndsWith(ShaderCacheName)).ToList();

            await dumpPackages(files, CurrentGame);
        }

        private async Task dumpPackages(List<string> files, MEGame game)
        {
            var beginTime = DateTime.Now;
            TopDock.IsEnabled = false;
            MidDock.IsEnabled = false;
            OverallProgressMaximum = files.Count;
            OverallProgressValue = 0;
            BusyBarInd = false;
            CurrentOverallOperationText = $"Generating Database...";
            bool scanCRC = menu_checkCRC.IsChecked;

            //Clear database
            ClearDataBase();
            CurrentDataBase.GenerationDate = beginTime.ToString();
            CurrentDataBase.DatabaseVersion = dbCurrentBuild;

            GeneratedDB.Clear();

            //Build filelists
            CurrentDataBase.ContentDir.Add("Unknown");
            var fileKeys = new List<(int, string)>();
            files = files.OrderBy(Path.GetFileName, StringComparer.InvariantCultureIgnoreCase).ToList();
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (ext != ".sfar")
                {
                    // File on disk.
                    var contdir = GetContentPath(new DirectoryInfo(f));
                    if (contdir == null)
                    {
                        continue;
                    }
                    var dirkey = CurrentDataBase.ContentDir.IndexOf(contdir.Name);
                    if (dirkey < 0)
                    {
                        dirkey = CurrentDataBase.ContentDir.Count;
                        CurrentDataBase.ContentDir.Add(contdir.Name);
                    }
                    var filekey = CurrentDataBase.FileList.Count;
                    CurrentDataBase.FileList.Add(new(Path.GetFileName(f), dirkey));
                    fileKeys.Add((filekey, f));
                }
                else
                {
                    // ME3 DLC package.
                    var contdir = GetContentPath(new DirectoryInfo(f));
                    if (contdir == null)
                    {
                        continue;
                    }
                    var dirkey = CurrentDataBase.ContentDir.IndexOf(contdir.Name);
                    if (dirkey < 0)
                    {
                        dirkey = CurrentDataBase.ContentDir.Count;
                        CurrentDataBase.ContentDir.Add(contdir.Name);
                    }

                    DLCPackage dlc = new DLCPackage(f);
                    foreach (var entry in dlc.Files.Where(s => SupportedFileExtensions.Contains(Path.GetExtension(s.FileName.ToLower()))))
                    {
                        var filekey = CurrentDataBase.FileList.Count;
                        CurrentDataBase.FileList.Add(new(Path.GetFileName(entry.FileName), dirkey));
                        fileKeys.Add((filekey, entry.FileName));
                        OverallProgressMaximum++;
                    }
                }
            }

            //Shuffle filekeys randomly to avoid localizations concurrently accessing
            //int n = fileKeys.Count;
            //var rng = new Random();
            //while (n > 1)
            //{
            //    n--;
            //    int k = rng.Next(n + 1);
            //    var value = fileKeys[k];
            //    fileKeys[k] = fileKeys[n];
            //    fileKeys[n] = value;
            //}

            IsBusy = true;
            BusyHeader = $"Generating database for {CurrentGame}";
            ProcessingQueue = new ActionBlock<SingleFileScanner>(x =>
            {
                if (x.DumpCanceled)
                {
                    return;
                }
                x.DumpPackageFile(game, GeneratedDB);
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    BusyText = $"Scanned {OverallProgressValue}/{OverallProgressMaximum} files\n\n{GeneratedDB.GetProgressString()}";
                    OverallProgressValue++; //Concurrency 
                });
            }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) });

            AllDumpingItems = new List<SingleFileScanner>();
            var scanOptions = new AssetDBScanOptions(scanCRC, CurrentDataBase.Localization);
            foreach (var fkey in fileKeys)
            {
                var threadtask = new SingleFileScanner(fkey.Item2, fkey.Item1, scanOptions);
                AllDumpingItems.Add(threadtask); //For setting cancelation value
                ProcessingQueue.Post(threadtask); // Post all items to the block
            }

            Exception caughtException = null;
            try
            {
                ProcessingQueue.Complete(); // Signal completion
                CommandManager.InvalidateRequerySuggested();
                await ProcessingQueue.Completion;
                isProcessing = true;
            }
            catch (Exception e)
            {
                caughtException = e;
            }
            finally
            {
                if (DumpCanceled)
                {
                    DumpCanceled = false;
                    BusyHeader = "Dump canceled. ";
                }
                else
                {
                    OverallProgressValue = 100;
                    OverallProgressMaximum = 100;
                    BusyHeader = "Dump completed. ";
                }

                TaskbarHelper.SetProgressState(TaskbarProgressBarState.NoProgress);
            }

            if (caughtException != null)
            {
                GeneratedDB.Clear();
                CurrentOverallOperationText = "Database generation failed";
                IsBusy = false;
                isProcessing = false;
                TopDock.IsEnabled = true;
                MidDock.IsEnabled = true;
                throw caughtException;
            }

            BusyHeader += "Collating and sorting the database";
            BusyText = "Please wait...";
            BusyBarInd = true;
            CommandManager.InvalidateRequerySuggested();

            AssetDB pdb = await Task.Run(GeneratedDB.CollateDataBase);
            GeneratedDB.Clear();
            //Add and sort Classes
            CurrentDataBase.AddRecords(pdb);
            RebuildConversationLookup();

            var dlcs = MELoadedDLC.GetDLCNamesWithMounts(CurrentGame);
            dlcs.Add("BioGame", 0);
            foreach ((string fileName, int directoryKey) in CurrentDataBase.FileList)
            {
                var cd = CurrentDataBase.ContentDir[directoryKey];
                int mount = -1;
                dlcs.TryGetValue(cd, out mount);
                FileListExtended.Add(new(fileName, cd, mount));
            }

            AssetFilters.MaterialFilter.LoadFromDatabase(CurrentDataBase);
            RefreshMaterialTextureDropdownFilters();
            RefreshTextureDropdownFilters();
            Settings.AssetDBGame = CurrentDataBase.Game.ToString();
            isProcessing = false;
            SaveDatabase();
            TopDock.IsEnabled = true;
            MidDock.IsEnabled = true;
            IsBusy = false;
            var elapsed = DateTime.Now - beginTime;
            MessageBox.Show(this, $"{CurrentGame} Database generated in {elapsed:mm\\:ss}");
            MemoryAnalyzer.ForceFullGC(true);
            // 08/27/2023 - Removed !IsGame1() check on GetConvoLinesBackground()
            GetConvoLinesBackground();
            CurrentDataBase.PlotUsages.LoadPlotPaths(game);
        }

        private void CancelDump(object obj)
        {
            DumpCanceled = true;
            AllDumpingItems?.ForEach(x => x.DumpCanceled = true);
            CommandManager.InvalidateRequerySuggested(); //Refresh commands
        }

        public void CopyUsagesFromPanel(AssetUsagesPanel panel)
        {
            if (FileListExtended == null || !FileListExtended.Any())
                return;

            if (panel.UsagesSource is not IEnumerable<IAssetUsage> usages)
                return;

            var text = string.Join("\n", usages.Select(x => FileListExtended[x.FileKey]?.FileName).Distinct());
            if (text != null)
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error copying to clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private IAssetUsage GetSelectedPanelUsage(int view)
        {
            return view switch
            {
                2 => materialsUsagesPanel.SelectedItem as IAssetUsage,
                3 => meshesUsagesPanel.SelectedItem as IAssetUsage,
                4 => texturesUsagesPanel.SelectedItem as IAssetUsage,
                5 => animationsUsagesPanel.SelectedItem as IAssetUsage,
                6 => vfxUsagesPanel.SelectedItem as IAssetUsage,
                7 => guiUsagesPanel.SelectedItem as IAssetUsage,
                _ => null
            };
        }


        private void Animation_PlayInAssetViewer(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is AnimationRecord ar)
            {
                var usage = ar.AssetUsages.First();
                var fpath = GetFilePath(usage.FileKey);
                if (GameController.IsGameOpen(CurrentGame) && File.Exists(fpath))
                {
                    Debug.WriteLine($"File exists: {fpath}");
                    using var package = MEPackageHandler.OpenMEPackage(fpath);
                    if (package.TryGetUExport(usage.UIndex, out var export) && AssetViewerWindow.SupportsAsset(export))
                    {
                        AssetViewerWindow.PreviewAsset(export);
                    }
                }
                else
                {
                    Debug.WriteLine($"File doesn't exist: {fpath}");
                }
            }
        }

        private void VFX_PlayInAssetViewer(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ParticleSysRecord psr)
            {
                var usage = psr.AssetUsages.First();
                var fpath = GetFilePath(usage.FileKey);
                if (GameController.IsGameOpen(CurrentGame) && File.Exists(fpath))
                {
                    Debug.WriteLine($"File exists: {fpath}");
                    using var package = MEPackageHandler.OpenMEPackage(fpath);
                    if (package.TryGetUExport(usage.UIndex, out var export) && AssetViewerWindow.SupportsAsset(export))
                    {
                        AssetViewerWindow.PreviewAsset(export);
                    }
                }
                else
                {
                    Debug.WriteLine($"File doesn't exist: {fpath}");
                }
            }
        }

        private void Mesh_PlayInAssetViewer(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is MeshRecord psr)
            {
                var usage = psr.AssetUsages.First();
                var fpath = GetFilePath(usage.FileKey);
                if (GameController.IsGameOpen(CurrentGame) && File.Exists(fpath))
                {
                    Debug.WriteLine($"File exists: {fpath}");
                    using var package = MEPackageHandler.OpenMEPackage(fpath);
                    if (package.TryGetUExport(usage.UIndex, out var export) && AssetViewerWindow.SupportsAsset(export))
                    {
                        AssetViewerWindow.PreviewAsset(export);
                    }
                }
                else
                {
                    Debug.WriteLine($"File doesn't exist: {fpath}");
                }
            }
        }

        private void SelectedMaterial_Changed(object sender, SelectionChangedEventArgs e)
        {
            MaterialEditorExportLoader_Control?.UnloadExport();
            if (sender is ListBoxScroll lbs && lbs.SelectedItem is MaterialRecord mr)
            {
                var usage = mr.Usages.FirstOrDefault();
                if (usage != null)
                {
                    var (fileListIndex, expUIndex, _) = usage;
                    string filePath = GetFilePath(fileListIndex);
                    using var pcc = fetchPackage(filePath, fileListIndex, null);
                    if (pcc != null)
                    {
                        if (pcc.IsUExport(expUIndex) && pcc.GetUExport(expUIndex) is ExportEntry exp && MaterialEditorExportLoader_Control.CanParse(exp))
                        {
                            MaterialEditorExportLoader_Control.LoadExport(exp);
                        }

                    }
                    else
                    {
                        MessageBox.Show($"File not found: {filePath}");
                    }
                }
            }
        }

        private void Material_LoadInLiveMaterialEditor(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is MaterialRecord mr && LELiveLevelEditorWindow.Instance(CurrentGame) != null)
            {
                var usage = mr.AssetUsages.First();
                var fpath = GetFilePath(usage.FileKey);
                if (GameController.IsGameOpen(CurrentGame) && File.Exists(fpath))
                {
                    using var package = MEPackageHandler.OpenMEPackage(fpath);
                    if (package.TryGetUExport(usage.UIndex, out var export))
                    {
                        LELiveLevelEditorWindow.Instance(CurrentGame).SetCustomMaterial(export);
                    }
                }
                else
                {
                    Debug.WriteLine($"File doesn't exist: {fpath}");
                }
            }
        }

        private DirectoryInfo GetContentPath(DirectoryInfo directory)
        {
            if (directory == null)
            {
                return null;
            }
            var parent = directory.Parent;
            if (!directory.Name.StartsWith("Cooked"))
            {
                return GetContentPath(parent);
            }
            else
            {
                return parent;
            }
        }

        #endregion
    }

    public class FileIndexToNameConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            int fileindex = (int)values[0];
            var listofFiles = values[1] as ObservableCollectionExtended<AssetDatabaseWindow.FileDirPair>;
            if (listofFiles == null || fileindex < 0 || fileindex >= listofFiles.Count || listofFiles.Count == 0)
            {
                return "Error: file name not found";
            }
            var export = (int)values[2];
            (string fileName, string directory, int mount) = listofFiles[fileindex];
            return $"{fileName}  # {export}   {directory} ";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return null; //not needed
        }
    }

    public readonly struct ConversationKey : IEquatable<ConversationKey>
    {
        public ConversationKey(string packageName, int exportIndex)
        {
            PackageName = packageName;
            ExportIndex = exportIndex;
        }

        public string PackageName { get; }

        public int ExportIndex { get; }

        public bool Equals(ConversationKey other)
        {
            return ExportIndex == other.ExportIndex
                && string.Equals(PackageName, other.PackageName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return obj is ConversationKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(PackageName ?? string.Empty), ExportIndex);
        }
    }

    public class ConvoLineSpeakerConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] is not ConvoLine line || values[1] is not AssetDatabaseWindow window)
            {
                return values[0] is ConvoLine fallbackLine ? fallbackLine.Speaker : string.Empty;
            }

            return window.GetSpeakerDisplay(line) ?? line.Speaker;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    public class ConvoLineFileConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values[0] is not string convoName ||
                values[1] is not List<Conversation> conversations ||
                values[2] is not ObservableCollectionExtended<AssetDatabaseWindow.FileDirPair> fileList)
            {
                return "";
            }

            var convo = conversations.FirstOrDefault(c => c.ConvName == convoName);
            if (convo == null) return "";

            int fileKey = convo.ConvFile.FileKey;
            if (fileKey < 0 || fileKey >= fileList.Count) return "";

            return string.Equals(parameter as string, "Location", StringComparison.OrdinalIgnoreCase)
                ? fileList[fileKey].Directory
                : fileList[fileKey].FileName;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

}
