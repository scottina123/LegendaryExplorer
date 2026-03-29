using System;
using System.Collections;
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
using System.Windows.Threading;
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
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.Tools.CoalescedEditor;
using LegendaryExplorer.Tools.AssetDatabase.Filters;
using LegendaryExplorer.Tools.AssetViewer;
using LegendaryExplorer.Tools.LiveLevelEditor;
using LegendaryExplorer.Tools.PlotDatabase;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Memory;
using LegendaryExplorerCore.PlotDatabase;
using Microsoft.WindowsAPICodePack.Dialogs;
using TerraFX.Interop.Windows;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Tools.AssetDatabase
{
    /// <summary>
    /// Interaction logic for AssetDB
    /// </summary>
    public partial class AssetDatabaseWindow : TrackingNotifyPropertyChangedWindowBase
    {
        private sealed class ConversationLoadContext : IDisposable
        {
            private readonly IDisposable[] _resources;

            public ConversationLoadContext(ConversationExtended conversationData, params IDisposable[] resources)
            {
                ConversationData = conversationData;
                _resources = resources ?? [];
            }

            public ConversationExtended ConversationData { get; }

            public void Dispose()
            {
                foreach (var resource in _resources)
                {
                    resource?.Dispose();
                }
            }
        }

        public sealed class TlkDisplayRecord
        {
            public int StringID { get; init; }

            public string ParsedValue { get; init; }

            public string SourceName { get; init; }

            public List<TlkUsage> Usages { get; init; } = [];

            public string DisplayValue => string.IsNullOrWhiteSpace(ParsedValue) ? "No Data" : ParsedValue;
        }

        public sealed class MaterialTextureFilterCriterion : INotifyPropertyChanged
        {
            private string _typeLabel = "Texture Type:";
            private string _selectedTextureType = AllMaterialTextureTypeFilterOption;
            private string _selectedTextureCount = AllMaterialTextureCountFilterOption;
            private string _selectedTextureParameter = AllMaterialTextureParameterFilterOption;
            private bool _canRemove;

            public event PropertyChangedEventHandler PropertyChanged;

            public ObservableCollectionExtended<string> CountFilters { get; } = new();

            public ObservableCollectionExtended<string> ParameterFilters { get; } = new();

            public string TypeLabel
            {
                get => _typeLabel;
                set
                {
                    if (_typeLabel != value)
                    {
                        _typeLabel = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeLabel)));
                    }
                }
            }

            public string SelectedTextureType
            {
                get => _selectedTextureType;
                set
                {
                    if (_selectedTextureType != value)
                    {
                        _selectedTextureType = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTextureType)));
                    }
                }
            }

            public string SelectedTextureCount
            {
                get => _selectedTextureCount;
                set
                {
                    if (_selectedTextureCount != value)
                    {
                        _selectedTextureCount = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTextureCount)));
                    }
                }
            }

            public string SelectedTextureParameter
            {
                get => _selectedTextureParameter;
                set
                {
                    if (_selectedTextureParameter != value)
                    {
                        _selectedTextureParameter = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTextureParameter)));
                    }
                }
            }

            public bool CanRemove
            {
                get => _canRemove;
                set
                {
                    if (_canRemove != value)
                    {
                        _canRemove = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRemove)));
                    }
                }
            }
        }

        #region Declarations
        // v9.7: Added TLK usage records and coalesced TLK scanning for database browsing.
        public const string dbCurrentBuild = "9.7"; //If changes are made that invalidate old databases edit this.

        private int previousView { get; set; }
        private readonly bool _isMaterialSelectionMode;
        private readonly bool _selectMaterialInstancesOnly;
        private readonly string _initialMaterialSearchText;
        private Action<MaterialSelectionResult> _materialSelectionHandler;
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

        public bool IsMaterialSelectionMode => _isMaterialSelectionMode;

        public bool HasSelectedMaterialRecord => lstbx_Materials?.SelectedItem is MaterialRecord;

        public string MaterialSelectionPrompt => _selectMaterialInstancesOnly
            ? "Select the donor MaterialInstanceConstant to restore from."
            : "Select the donor Material to restore from.";

        public MaterialSelectionResult SelectedMaterialDialogResult { get; private set; }

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
        private string _filterText = string.Empty;
        public string FilterText
        {
            get => _filterText;
            set => SetProperty(ref _filterText, value);
        }
        private string _filterWatermark = "Search";
        public string FilterWatermark
        {
            get => _filterWatermark;
            set => SetProperty(ref _filterWatermark, value);
        }
        private string _usageFilterText = string.Empty;
        public string UsageFilterText
        {
            get => _usageFilterText;
            set
            {
                if (SetProperty(ref _usageFilterText, value))
                {
                    RefreshUsageViews();
                }
            }
        }
        private bool _showUsageFilter;
        public bool ShowUsageFilter
        {
            get => _showUsageFilter;
            set => SetProperty(ref _showUsageFilter, value);
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
        private readonly Dictionary<string, (string FileName, string Location)> _convoFileInfoCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _lineSearchDebounceTimer;

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
        public ObservableCollectionExtended<string> MaterialUsageFilters { get; } = new();
        public ObservableCollectionExtended<string> MaterialTextureTypeFilters { get; } = new();
        public ObservableCollectionExtended<MaterialTextureFilterCriterion> MaterialTextureCriteria { get; } = new();
        public ObservableCollectionExtended<string> TextureTypeFilters { get; } = new();
        public ObservableCollectionExtended<string> TextureSizeFilters { get; } = new();
        public ObservableCollectionExtended<string> SequenceEventTypeFilters { get; } = new()
        {
            AllSequenceEventFilterOption,
            ActivateRemoteEventFilterOption,
            ConsoleCommandFilterOption,
            RemoteEventFilterOption,
            ConsoleEventFilterOption
        };
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
        public ObservableCollectionExtended<string> TlkSourceFilters { get; } = new();
        public ObservableCollectionExtended<TlkDisplayRecord> DisplayedTlkStrings { get; } = new();

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
        private const string MaterialUsageFilterDelimiter = ", ";
        private const string AllMaterialTextureTypeFilterOption = "All";
        private const string AllMaterialTextureCountFilterOption = "All";
        private const string AllMaterialTextureParameterFilterOption = "All";
        private const string AllTextureFilterOption = "All";
        private const string AllSequenceEventFilterOption = "All";
        private const string ActivateRemoteEventFilterOption = "Activate Remote Events";
        private const string ConsoleCommandFilterOption = "Console Commands";
        private const string RemoteEventFilterOption = "Remote Events";
        private const string ConsoleEventFilterOption = "Console Events";
        private const string AllLineSearchColumnsOption = "All Columns";
        private const string SpeakerLineSearchColumn = "Speaker";
        private const string TlkStringRefLineSearchColumn = "TLK String Ref";
        private const string LineTextSearchColumn = "Line";
        private const string LineConversationSearchColumn = "Line Conversation";
        private const string FileLineSearchColumn = "File";
        private const string LocationLineSearchColumn = "Location";
        private const string AllTlkSourceFilterOption = "All TLKs";

        public sealed record MaterialSelectionResult(MaterialRecord Material);

        private enum OutdatedDatabaseAction
        {
            Cancel,
            RebuildCurrentGame,
            RebuildAllGames
        }

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

        private string _selectedTlkSourceFilter = AllTlkSourceFilterOption;
        public string SelectedTlkSourceFilter
        {
            get => _selectedTlkSourceFilter;
            set
            {
                if (SetProperty(ref _selectedTlkSourceFilter, value))
                {
                    RefreshTlkDisplayRecords();
                }
            }
        }

        private TlkDisplayRecord _selectedTlkString;
        public TlkDisplayRecord SelectedTlkString
        {
            get => _selectedTlkString;
            set
            {
                if (SetProperty(ref _selectedTlkString, value))
                {
                    tlkUsagesPanel?.RefreshFilter();
                }
            }
        }

        private readonly List<(string SourceName, Dictionary<int, string> Values)> _loadedTlkSources = [];
        private readonly Dictionary<int, string> _mergedTlkValues = [];
        private readonly Dictionary<int, TlkStringRecord> _tlkUsageLookup = [];

        private string _selectedSequenceEventTypeFilter = AllSequenceEventFilterOption;
        public string SelectedSequenceEventTypeFilter
        {
            get => _selectedSequenceEventTypeFilter;
            set
            {
                if (SetProperty(ref _selectedSequenceEventTypeFilter, value))
                {
                    Filter();
                }
            }
        }

        private string _selectedMaterialTextureNameFilter = string.Empty;
        public string SelectedMaterialTextureNameFilter
        {
            get => _selectedMaterialTextureNameFilter;
            set
            {
                if (SetProperty(ref _selectedMaterialTextureNameFilter, value))
                {
                    Filter();
                }
            }
        }

        private bool _isRefreshingMaterialTextureFilters;
        private bool _isRefreshingMaterialUsageFilters;

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

        private string _selectedMaterialUsageFilters = string.Empty;
        public string SelectedMaterialUsageFilters
        {
            get => _selectedMaterialUsageFilters;
            set
            {
                if (SetProperty(ref _selectedMaterialUsageFilters, value))
                {
                    if (_isRefreshingMaterialUsageFilters)
                    {
                        return;
                    }

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
        private bool _isGeneratingAllDatabases;
        private readonly Dictionary<MEGame, string> _allGamesProgressStatus = new();
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
        public ObservableCollectionExtended<string> AllGamesScanProgress { get; } = new();
        private bool _isGettingTLKs;
        public bool IsGettingTLKs
        {
            get => _isGettingTLKs;
            set => SetProperty(ref _isGettingTLKs, value);
        }
        public const string CustomListDesc = "Custom File Lists allow the database to be filtered so only assets that are in certain files or groups of files are shown. Lists can be saved/reloaded.";
        public ICommand GenerateDBCommand { get; set; }
        public ICommand GenerateAllDBCommand { get; set; }
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
            return currentView == 1 && lstbx_Classes?.SelectedIndex >= 0;
        }

        private bool IsUsageSelected(object obj)
        {
            return (currentView == 1 && lstbx_Usages?.SelectedIndex >= 0)
                || (currentView == 2 && materialsUsagesPanel?.SelectedIndex >= 0)
                || (currentView == 3 && meshesUsagesPanel?.SelectedIndex >= 0)
                || (currentView == 4 && texturesUsagesPanel?.SelectedIndex >= 0)
                || (currentView == 5 && animationsUsagesPanel?.SelectedIndex >= 0)
                || (currentView == 6 && vfxUsagesPanel?.SelectedIndex >= 0)
                || (currentView == 7 && guiUsagesPanel?.SelectedIndex >= 0)
                || (currentView == 8 && lstbx_Lines?.SelectedIndex >= 0)
                || (currentView == 9 && lstbx_PlotUsages?.SelectedIndex >= 0)
                || (currentView == 10 && sequenceEventsUsagesPanel?.SelectedIndex >= 0)
                || (currentView == 11 && tlkUsagesPanel?.SelectedIndex >= 0)
                || (currentView == 0 && IsNotCND(lstbx_Files?.SelectedItem));
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
            return currentView == 5
                && CurrentGame == MEGame.ME3
                && lstbx_Anims?.SelectedIndex >= 0
                && !((lstbx_Anims?.SelectedItem as AnimationRecord)?.IsAmbPerf ?? true);
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

            _lineSearchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            _lineSearchDebounceTimer.Tick += (_, _) =>
            {
                _lineSearchDebounceTimer.Stop();
                if (currentView == 8)
                {
                    Filter();
                }
            };

            EnsureMaterialTextureCriteria();
            InitializeComponent();
        }

        public AssetDatabaseWindow(MEGame game, bool materialSelectionMode, bool selectMaterialInstancesOnly, string initialMaterialSearchText = null) : this()
        {
            CurrentGame = game;
            _isMaterialSelectionMode = materialSelectionMode;
            _selectMaterialInstancesOnly = selectMaterialInstancesOnly;
            _initialMaterialSearchText = initialMaterialSearchText?.Trim();
        }

        public static void ShowMaterialPicker(Window owner, MEGame game, bool selectMaterialInstancesOnly, Action<MaterialSelectionResult> onMaterialSelected, string initialMaterialSearchText = null)
        {
            var picker = new AssetDatabaseWindow(game, true, selectMaterialInstancesOnly, initialMaterialSearchText)
            {
                Owner = owner,
                WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
            };

            picker._materialSelectionHandler = onMaterialSelected;
            picker.Show();
            picker.Activate();
        }

        private void LoadCommands()
        {
            GenerateDBCommand = new GenericCommand(GenerateDatabase);
            GenerateAllDBCommand = new GenericCommand(GenerateAllDatabases);
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

            if (_isMaterialSelectionMode)
            {
                ConfigureMaterialSelectionMode();
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
            await SaveDatabaseAsync();
        }

        private async Task SaveDatabaseAsync(bool preserveBusyState = false, bool suppressFinalSummary = false)
        {
            if (!preserveBusyState)
            {
                BusyHeader = "Saving database";
                BusyText = "Please wait...";
                BusyBarInd = true;
                IsBusy = true;
                CurrentOverallOperationText = "Database saving...";
            }

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
            if (!preserveBusyState)
            {
                IsBusy = false;
                await Task.Delay(3000);
                if (!suppressFinalSummary)
                {
                    CurrentOverallOperationText = GetDatabaseSummaryText();
                }
            }
        }

        private string GetDatabaseSummaryText()
        {
            return $"Database generated {CurrentDataBase.GenerationDate} Classes: {CurrentDataBase.ClassRecords.Count} Animations: {CurrentDataBase.Animations.Count} Materials: {CurrentDataBase.Materials.Count} Meshes: {CurrentDataBase.Meshes.Count} Particles: {CurrentDataBase.Particles.Count} Textures: {CurrentDataBase.Textures.Count} Elements: {CurrentDataBase.GUIElements.Count} Lines: {CurrentDataBase.Lines.Count} Sequence Events: {CurrentDataBase.SequenceEvents.Count} TLKs: {CurrentDataBase.TlkStrings.Count}";
        }

        private void RefreshTlkLookup()
        {
            _tlkUsageLookup.Clear();
            foreach (var tlkRecord in CurrentDataBase.TlkStrings)
            {
                _tlkUsageLookup[tlkRecord.StringID] = new TlkStringRecord(tlkRecord.StringID)
                {
                    Usages = tlkRecord.Usages.ToList()
                };
            }

            foreach (var line in CurrentDataBase.Lines)
            {
                if (line.StrRef <= 0 || !TryGetConversation(line.Convo, out var conversation))
                {
                    continue;
                }

                InferTlkUsageFlags(conversation.ConvFile.FileKey, out bool isInDlc, out bool isInMod);
                AddTlkUsageToLookup(line.StrRef, new TlkUsage(
                    conversation.ConvFile.FileKey,
                    conversation.ConvFile.UIndex,
                    isInDlc,
                    isInMod,
                    TlkUsageContext.Package,
                    null,
                    null,
                    $"Conversation: {line.Convo}"));
            }
        }

        private void LoadTlkData()
        {
            _loadedTlkSources.Clear();
            _mergedTlkValues.Clear();
            RefreshTlkLookup();

            TlkSourceFilters.ReplaceAll([AllTlkSourceFilterOption]);
            SelectedTlkSourceFilter = AllTlkSourceFilterOption;

            var gamePath = MEDirectories.GetDefaultGamePath(CurrentGame);
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                RefreshTlkDisplayRecords();
                return;
            }

            var talkFiles = CurrentGame.IsGame1()
                ? TLKSystem.LoadTLKs(CurrentGame, Localization, male: false, gamePath)
                    .Concat(TLKSystem.LoadTLKs(CurrentGame, Localization, male: true, gamePath))
                : TLKSystem.LoadTLKs(CurrentGame, Localization, male: true, gamePath);

            foreach (var talkFile in talkFiles)
            {
                var values = talkFile.StringRefs
                    .Where(sr => sr.StringID > 0)
                    .GroupBy(sr => sr.StringID)
                    .ToDictionary(group => group.Key, group => NormalizeTlkText(group.Last().Data));

                if (values.Count == 0)
                {
                    continue;
                }

                var sourceName = GetTlkSourceDisplayName(talkFile.Source);
                _loadedTlkSources.Add((sourceName, values));
                foreach (var (stringId, parsedValue) in values)
                {
                    _mergedTlkValues[stringId] = parsedValue;
                }
            }

            TlkSourceFilters.ReplaceAll([AllTlkSourceFilterOption, .. _loadedTlkSources.Select(source => source.SourceName).Distinct(StringComparer.OrdinalIgnoreCase)]);
            RefreshTlkDisplayRecords();
        }

        private void RefreshTlkDisplayRecords()
        {
            Dictionary<int, string> sourceValues = null;
            if (!string.Equals(SelectedTlkSourceFilter, AllTlkSourceFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                sourceValues = _loadedTlkSources.FirstOrDefault(source => string.Equals(source.SourceName, SelectedTlkSourceFilter, StringComparison.OrdinalIgnoreCase)).Values;
            }

            var previousSelection = SelectedTlkString?.StringID;
            var keys = sourceValues is null
                ? _mergedTlkValues.Keys.Concat(_tlkUsageLookup.Keys).Distinct().OrderBy(key => key)
                : sourceValues.Keys.OrderBy(key => key);
            var records = keys
                .Select(key => new TlkDisplayRecord
                {
                    StringID = key,
                    ParsedValue = (sourceValues ?? _mergedTlkValues).TryGetValue(key, out var parsedValue) ? parsedValue : null,
                    SourceName = sourceValues == null ? GetMergedTlkSourceName(key) : SelectedTlkSourceFilter,
                    Usages = _tlkUsageLookup.TryGetValue(key, out var tlkRecord) ? tlkRecord.Usages : []
                })
                .ToList();

            DisplayedTlkStrings.ReplaceAll(records);
            Filter();

            if (previousSelection.HasValue)
            {
                SelectedTlkString = DisplayedTlkStrings.FirstOrDefault(record => record.StringID == previousSelection.Value);
            }
            else if (DisplayedTlkStrings.Count > 0)
            {
                SelectedTlkString = DisplayedTlkStrings[0];
            }
            else
            {
                SelectedTlkString = null;
            }
        }

        private string GetMergedTlkSourceName(int stringId)
        {
            for (int i = _loadedTlkSources.Count - 1; i >= 0; i--)
            {
                if (_loadedTlkSources[i].Values.ContainsKey(stringId))
                {
                    return _loadedTlkSources[i].SourceName;
                }
            }

            return AllTlkSourceFilterOption;
        }

        private static string GetTlkSourceDisplayName(string source)
        {
            return string.IsNullOrWhiteSpace(source) ? "Unknown TLK" : Path.GetFileName(source);
        }

        private static string NormalizeTlkText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "No Data", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”')))
            {
                return text[1..^1];
            }

            return text;
        }

        private void AddTlkUsageToLookup(int stringId, TlkUsage usage)
        {
            if (!_tlkUsageLookup.TryGetValue(stringId, out var tlkRecord))
            {
                tlkRecord = new TlkStringRecord(stringId);
                _tlkUsageLookup[stringId] = tlkRecord;
            }

            if (!tlkRecord.Usages.Contains(usage))
            {
                tlkRecord.Usages.Add(usage);
            }
        }

        private void InferTlkUsageFlags(int fileKey, out bool isInDlc, out bool isInMod)
        {
            isInDlc = false;
            isInMod = false;

            if (fileKey < 0 || fileKey >= FileListExtended.Count)
            {
                return;
            }

            var directory = FileListExtended[fileKey].Directory ?? string.Empty;
            isInMod = directory.Contains("DLC_MOD", StringComparison.OrdinalIgnoreCase)
                      || directory.Contains(@"CookedPCConsole\Mods", StringComparison.OrdinalIgnoreCase)
                      || directory.Contains(@"CookedPC\Mods", StringComparison.OrdinalIgnoreCase);
            isInDlc = !isInMod && directory.Contains("DLC_", StringComparison.OrdinalIgnoreCase);
        }

        public void ClearDataBase()
        {
            CurrentDataBase.Clear();
            CurrentDataBase.Game = CurrentGame;
            CurrentDataBase.Localization = Localization;
            _conversationLookup.Clear();
            _convoFileInfoCache.Clear();
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
            SelectedMaterialTextureNameFilter = string.Empty;
            RefreshMaterialUsageDropdownFilters();
            RefreshMaterialTextureDropdownFilters();
            RefreshTextureDropdownFilters();
            SelectedSequenceEventTypeFilter = AllSequenceEventFilterOption;
            FilterText = string.Empty;
            _loadedTlkSources.Clear();
            _mergedTlkValues.Clear();
            _tlkUsageLookup.Clear();
            TlkSourceFilters.ReplaceAll([AllTlkSourceFilterOption]);
            DisplayedTlkStrings.ClearEx();
            SelectedTlkString = null;
            Filter();
        }

        private void RebuildConversationLookup()
        {
            _conversationLookup.Clear();
            _convoFileInfoCache.Clear();

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

            var ownerName = TryGetCachedOwnerName(new ConversationKey(conversation.PackageName, conversation.ConversationExportIndex));
            if (string.IsNullOrWhiteSpace(ownerName))
            {
                return line.Speaker;
            }

            return $"{line.Speaker} ({ownerName})";
        }

        private string GetSpeakerDisplayForSearch(ConvoLine line)
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

            return TryGetCachedOwnerDisplay(line, conversation);
        }

        private string GetSpeakerFilterValue(ConvoLine line)
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

            return TryGetCachedOwnerName(new ConversationKey(conversation.PackageName, conversation.ConversationExportIndex)) ?? line.Speaker;
        }

        private static string TryGetCachedOwnerDisplay(ConvoLine line, Conversation conversation)
        {
            var key = new ConversationKey(conversation.PackageName, conversation.ConversationExportIndex);
            var cachedName = TryGetCachedOwnerName(key);
            if (string.IsNullOrWhiteSpace(cachedName))
            {
                return line.Speaker;
            }

            return $"{line.Speaker} ({cachedName})";
        }

        private static string TryGetCachedOwnerName(ConversationKey key)
        {
            return OwnerNameCache.TryGetValue(key, out var cachedName) && !string.IsNullOrWhiteSpace(cachedName)
                ? cachedName
                : null;
        }

        private void PreResolveOwnerNames()
        {
            var ownerConversationKeys = CurrentDataBase.Lines
                .Where(line => string.Equals(line.Speaker, "Owner", StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Convo)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(convoName => TryGetConversation(convoName, out var conversation)
                    && !string.IsNullOrWhiteSpace(conversation.PackageName)
                    && conversation.ConversationExportIndex > 0
                        ? new ConversationKey(conversation.PackageName, conversation.ConversationExportIndex)
                        : default)
                .Where(key => !string.IsNullOrWhiteSpace(key.PackageName) && key.ExportIndex > 0)
                .Distinct()
                .ToList();

            foreach (var key in ownerConversationKeys)
            {
                ResolveOwnerName(key);
            }
        }

        private void StartOwnerNamePrewarm()
        {
            Task.Run(PreResolveOwnerNames).ContinueWithOnUIThread(_ =>
            {
                RefreshSpeakerList();
                RefreshLinesView();
            });
        }

        private void RefreshSpeakerList()
        {
            var selectedSpeaker = cmbbx_filterSpkrs?.SelectedItem as string;
            var speakers = CurrentDataBase.Lines
                .SelectMany(line =>
                {
                    var values = new List<string>(2) { line.Speaker };
                    var filterValue = GetSpeakerFilterValue(line);
                    if (!string.IsNullOrWhiteSpace(filterValue)
                        && !string.Equals(filterValue, line.Speaker, StringComparison.CurrentCultureIgnoreCase))
                    {
                        values.Add(filterValue);
                    }

                    return values;
                })
                .Where(speaker => !string.IsNullOrWhiteSpace(speaker))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(speaker => speaker, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            SpeakerList.ReplaceAll(speakers);

            if (!string.IsNullOrWhiteSpace(selectedSpeaker) && speakers.Contains(selectedSpeaker, StringComparer.CurrentCultureIgnoreCase))
            {
                cmbbx_filterSpkrs.SelectedItem = speakers.First(s => string.Equals(s, selectedSpeaker, StringComparison.CurrentCultureIgnoreCase));
            }
        }

        private void RefreshLinesView()
        {
            if (currentView != 8)
            {
                return;
            }

            CollectionViewSource.GetDefaultView(lstbx_Lines.ItemsSource)?.Refresh();
        }

        private void ScheduleLineSearchFilter()
        {
            if (currentView != 8)
            {
                Filter();
                return;
            }

            _lineSearchDebounceTimer.Stop();
            _lineSearchDebounceTimer.Start();
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

                    if (actorEntry is ExportEntry actorExport)
                    {
                        var actorTag = actorExport.GetProperty<NameProperty>("Tag")?.Value.Instanced;
                        if (!string.IsNullOrWhiteSpace(actorTag))
                        {
                            return actorTag;
                        }

                        if (actorExport.HasArchetype && actorExport.Archetype is ExportEntry archetype)
                        {
                            var archetypeTag = archetype.GetProperty<NameProperty>("Tag")?.Value.Instanced;
                            if (!string.IsNullOrWhiteSpace(archetypeTag))
                            {
                                return archetypeTag;
                            }
                        }
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
                return CurrentDataBase.Materials.Where(material => MatchesMaterialUsageFilter(material, SelectedMaterialUsageFilters));
            }

            return CurrentDataBase.Materials.Where(material => AssetFilters.MaterialFilter.Filter(material)
                && MatchesMaterialUsageFilter(material, SelectedMaterialUsageFilters));
        }

        private void RefreshMaterialUsageDropdownFilters(bool preserveSelection = false)
        {
            var previousSelection = preserveSelection ? SelectedMaterialUsageFilters : null;
            var usageFilters = CurrentDataBase.Materials
                .SelectMany(material => material.UsedOn ?? [])
                .Where(usage => !string.IsNullOrWhiteSpace(usage))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(usage => usage, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selectedUsageFilters = ParseSelectedMaterialUsageFilters(previousSelection)
                .Where(selectedUsage => usageFilters.Contains(selectedUsage, StringComparer.OrdinalIgnoreCase))
                .ToList();

            _isRefreshingMaterialUsageFilters = true;
            try
            {
                MaterialUsageFilters.ReplaceAll(usageFilters);
                SelectedMaterialUsageFilters = string.Join(MaterialUsageFilterDelimiter, selectedUsageFilters);
            }
            finally
            {
                _isRefreshingMaterialUsageFilters = false;
            }
        }

        private void RefreshMaterialTextureDropdownFilters(bool preserveSelections = false)
        {
            var previousSelections = MaterialTextureCriteria
                .Select(criterion => (TextureType: criterion.SelectedTextureType, TextureCount: criterion.SelectedTextureCount, TextureParameter: criterion.SelectedTextureParameter))
                .ToList();
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
                EnsureMaterialTextureCriteria();

                for (int i = 0; i < MaterialTextureCriteria.Count; i++)
                {
                    var criterion = MaterialTextureCriteria[i];
                    (string TextureType, string TextureCount, string TextureParameter) previousSelection = preserveSelections && i < previousSelections.Count
                        ? previousSelections[i]
                        : (AllMaterialTextureTypeFilterOption, AllMaterialTextureCountFilterOption, AllMaterialTextureParameterFilterOption);
                    criterion.SelectedTextureType = textureTypeFilters.Contains(previousSelection.TextureType, StringComparer.OrdinalIgnoreCase)
                        ? previousSelection.TextureType
                        : AllMaterialTextureTypeFilterOption;
                    RefreshMaterialTextureCountFilters(criterion, preserveSelections, previousSelection.TextureCount);
                    RefreshMaterialTextureParameterFilters(criterion, preserveSelections, previousSelection.TextureParameter);
                }

                UpdateMaterialTextureCriteriaMetadata();
            }
            finally
            {
                _isRefreshingMaterialTextureFilters = false;
            }

            Filter();
        }

        private void RefreshMaterialTextureCountFilters(MaterialTextureFilterCriterion criterion, bool preserveSelections = false, string previousCountSelection = null)
        {
            var materialSource = GetMaterialTextureDropdownSource().ToList();
            var textureCountFilters = new[] { AllMaterialTextureCountFilterOption }.Concat(materialSource
                .Select(material => GetMaterialTextureCountForFilter(material, criterion.SelectedTextureType))
                .Distinct()
                .OrderBy(count => count)
                .Select(count => count.ToString()))
                .ToList();

            criterion.CountFilters.ReplaceAll(textureCountFilters);
            criterion.SelectedTextureCount = preserveSelections && !string.IsNullOrWhiteSpace(previousCountSelection)
                                              && textureCountFilters.Contains(previousCountSelection, StringComparer.OrdinalIgnoreCase)
                ? previousCountSelection
                : AllMaterialTextureCountFilterOption;
        }

        private void RefreshMaterialTextureParameterFilters(MaterialTextureFilterCriterion criterion, bool preserveSelections = false, string previousParameterSelection = null)
        {
            var materialSource = GetMaterialTextureDropdownSource().ToList();
            var textureParameterFilters = new[] { AllMaterialTextureParameterFilterOption }.Concat(MaterialFilter.GetTextureParameterNames(materialSource, criterion.SelectedTextureType)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            criterion.ParameterFilters.ReplaceAll(textureParameterFilters);
            criterion.SelectedTextureParameter = preserveSelections && !string.IsNullOrWhiteSpace(previousParameterSelection)
                                                   && textureParameterFilters.Contains(previousParameterSelection, StringComparer.OrdinalIgnoreCase)
                ? previousParameterSelection
                : AllMaterialTextureParameterFilterOption;
        }

        private void EnsureMaterialTextureCriteria()
        {
            if (MaterialTextureCriteria.Count == 0)
            {
                AddMaterialTextureCriterion();
            }
            else
            {
                UpdateMaterialTextureCriteriaMetadata();
            }
        }

        private void AddMaterialTextureCriterion()
        {
            var criterion = new MaterialTextureFilterCriterion();
            criterion.PropertyChanged += MaterialTextureCriterion_PropertyChanged;
            MaterialTextureCriteria.Add(criterion);
            UpdateMaterialTextureCriteriaMetadata();
            RefreshMaterialTextureCountFilters(criterion);
            RefreshMaterialTextureParameterFilters(criterion);
        }

        private void RemoveMaterialTextureCriterion(MaterialTextureFilterCriterion criterion)
        {
            if (criterion is null)
            {
                return;
            }

            criterion.PropertyChanged -= MaterialTextureCriterion_PropertyChanged;
            MaterialTextureCriteria.Remove(criterion);
            EnsureMaterialTextureCriteria();
            Filter();
        }

        private void UpdateMaterialTextureCriteriaMetadata()
        {
            for (int i = 0; i < MaterialTextureCriteria.Count; i++)
            {
                MaterialTextureCriteria[i].TypeLabel = i == 0 ? "Texture Type:" : "And Type:";
                MaterialTextureCriteria[i].CanRemove = MaterialTextureCriteria.Count > 1;
            }
        }

        private void MaterialTextureCriterion_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isRefreshingMaterialTextureFilters || sender is not MaterialTextureFilterCriterion criterion)
            {
                return;
            }

            if (e.PropertyName == nameof(MaterialTextureFilterCriterion.SelectedTextureType))
            {
                _isRefreshingMaterialTextureFilters = true;
                try
                {
                    RefreshMaterialTextureCountFilters(criterion, preserveSelections: true, previousCountSelection: criterion.SelectedTextureCount);
                    RefreshMaterialTextureParameterFilters(criterion, preserveSelections: true, previousParameterSelection: criterion.SelectedTextureParameter);
                }
                finally
                {
                    _isRefreshingMaterialTextureFilters = false;
                }
            }

            Filter();
        }

        private void AddMaterialTextureCriterion_Click(object sender, RoutedEventArgs e)
        {
            AddMaterialTextureCriterion();
        }

        private void RemoveMaterialTextureCriterion_Click(object sender, RoutedEventArgs e)
        {
            RemoveMaterialTextureCriterion((sender as FrameworkElement)?.Tag as MaterialTextureFilterCriterion);
        }

        private static int GetMaterialTextureCountForFilter(MaterialRecord material, string textureTypeFilter)
        {
            if (string.Equals(textureTypeFilter, AllMaterialTextureTypeFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return MaterialFilter.GetTextureParameterTypeCount(material);
            }

            return MaterialFilter.GetTextureParameterTypeCount(material, textureTypeFilter);
        }

        private static IEnumerable<MatSetting> GetMaterialTextureSettingsForFilter(MaterialRecord material, string textureTypeFilter)
        {
            var textureSettings = MaterialFilter.GetTextureSettings(material);
            if (string.Equals(textureTypeFilter, AllMaterialTextureTypeFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return textureSettings;
            }

            return textureSettings.Where(setting => string.Equals(MaterialFilter.GetTextureParameterType(setting), textureTypeFilter, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesMaterialTextureCriterion(MaterialRecord material, string textureTypeFilter, string textureCountFilter, string textureParameterFilter)
        {
            var matchingTextureSettings = GetMaterialTextureSettingsForFilter(material, textureTypeFilter).ToList();
            if (!string.Equals(textureParameterFilter, AllMaterialTextureParameterFilterOption, StringComparison.OrdinalIgnoreCase)
                && !matchingTextureSettings.Any(setting => string.Equals(MaterialFilter.GetTextureParameterName(setting), textureParameterFilter, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var textureCount = matchingTextureSettings.Count;
            if (!string.Equals(textureTypeFilter, AllMaterialTextureTypeFilterOption, StringComparison.OrdinalIgnoreCase)
                && string.Equals(textureCountFilter, AllMaterialTextureCountFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return textureCount > 0;
            }

            if (string.Equals(textureCountFilter, AllMaterialTextureCountFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return int.TryParse(textureCountFilter, out int countFilter) && textureCount == countFilter;
        }

        private static bool MatchesMaterialTextureNameFilter(MaterialRecord material, string textureNameFilter)
        {
            if (string.IsNullOrWhiteSpace(textureNameFilter))
            {
                return true;
            }

            return MaterialFilter.GetTextureSettings(material)
                .Any(setting => !string.IsNullOrWhiteSpace(setting?.Parm2)
                    && setting.Parm2.Contains(textureNameFilter, StringComparison.OrdinalIgnoreCase));
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

            if (!MatchesMaterialUsageFilter(materialRecord, SelectedMaterialUsageFilters))
            {
                return false;
            }

            if (!MatchesMaterialTextureNameFilter(materialRecord, SelectedMaterialTextureNameFilter))
            {
                return false;
            }

            return MaterialTextureCriteria.All(criterion => MatchesMaterialTextureCriterion(materialRecord, criterion.SelectedTextureType, criterion.SelectedTextureCount, criterion.SelectedTextureParameter));
        }

        private static bool MatchesMaterialUsageFilter(MaterialRecord material, string usageFilters)
        {
            var selectedUsageFilters = ParseSelectedMaterialUsageFilters(usageFilters);
            return selectedUsageFilters.Count == 0
                   || material?.UsedOn?.Any(usedOn => selectedUsageFilters.Contains(usedOn)) == true;
        }

        private static HashSet<string> ParseSelectedMaterialUsageFilters(string usageFilters)
        {
            return string.IsNullOrWhiteSpace(usageFilters)
                ? []
                : usageFilters.Split([MaterialUsageFilterDelimiter], StringSplitOptions.RemoveEmptyEntries)
                    .Select(filter => filter.Trim())
                    .Where(filter => !string.IsNullOrWhiteSpace(filter))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                RefreshSpeakerList();
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
            foreach (var line in CurrentDataBase.Lines)
            {
                if (GeneratedDB.GeneratedLines.ContainsKey(line.StrRef.ToString()))
                {
                    line.Line = GeneratedDB.GeneratedLines[line.StrRef.ToString()].Line;
                }
            }

            int lineCountWithEmptyLines = CurrentDataBase.Lines.Count;
            CurrentDataBase.Lines.RemoveAll(l => l.Line == "No Data");
            int numEmptyLines = lineCountWithEmptyLines - CurrentDataBase.Lines.Count;

            GeneratedDB.GeneratedLines.Clear();
            RefreshSpeakerList();
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

        public async void GenerateAllDatabases()
        {
            var shouldGenerate = MessageBox.Show("Generate new databases for all games?", "Generating all databases", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!shouldGenerate)
            {
                return;
            }

            var originalGame = CurrentGame;
            var originalCurrentView = currentView;
            _isGeneratingAllDatabases = true;
            InitializeAllGamesScanProgress();

            BusyHeader = "Generating databases for all games";
            BusyText = "Preparing scans...";
            BusyBarInd = false;
            IsBusy = true;
            TopDock.IsEnabled = false;
            MidDock.IsEnabled = false;

            try
            {
                foreach (var game in DatabaseGenerationGames)
                {
                    string rootPath = MEDirectories.GetDefaultGamePath(game);
                    if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                    {
                        SetAllGamesScanStatus(game, "Skipped (game not found)");
                        continue;
                    }

                    SetAllGamesScanStatus(game, "Queued");

                    try
                    {
                        await ScanGameAsync(game, updateUiAfterScan: false, showCompletionMessage: false, preserveBusyState: true, manageWindowState: false, showMissingGameMessage: false);
                        SetAllGamesScanStatus(game, "Completed");
                    }
                    catch (Exception ex)
                    {
                        SetAllGamesScanStatus(game, $"Failed ({ex.GetBaseException().Message})");
                    }
                }
            }
            finally
            {
                _isGeneratingAllDatabases = false;
                IsBusy = false;
                TopDock.IsEnabled = true;
                MidDock.IsEnabled = true;
            }

            if (originalGame != MEGame.Unknown)
            {
                SwitchGame(GetSwitchGameParameter(originalGame));
                currentView = originalCurrentView;
            }

            string summary = string.Join("\n", AllGamesScanProgress);
            MessageBox.Show(this, summary, "All database generation finished", MessageBoxButton.OK, MessageBoxImage.Information);
            ClearAllGamesScanProgress();
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
                        var warn = PromptOutdatedDatabaseAction(CurrentDataBase.DatabaseVersion, dbCurrentBuild);
                        if (warn == OutdatedDatabaseAction.Cancel)
                        {
                            ClearDataBase();
                            IsBusy = false;
                        }
                        else if (warn == OutdatedDatabaseAction.RebuildAllGames)
                        {
                            GenerateAllDatabases();
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
                        RefreshMaterialUsageDropdownFilters();
                        RefreshMaterialTextureDropdownFilters();
                        RefreshTextureDropdownFilters();
                        LoadTlkData();
                        IsBusy = false;
                        CurrentOverallOperationText = $"Database generated {CurrentDataBase.GenerationDate} Classes: {CurrentDataBase.ClassRecords.Count} " +
                                                      $"Animations: {CurrentDataBase.Animations.Count} Materials: {CurrentDataBase.Materials.Count} Meshes: {CurrentDataBase.Meshes.Count} " +
                                                      $"Particles: {CurrentDataBase.Particles.Count} Textures: {CurrentDataBase.Textures.Count} Elements: {CurrentDataBase.GUIElements.Count} " +
                                                      $"Lines: {CurrentDataBase.Lines.Count} TLKs: {CurrentDataBase.TlkStrings.Count}";
#if DEBUG
                        var end = DateTime.UtcNow;
                        double length = (end - start).TotalMilliseconds;
                        CurrentOverallOperationText = $"{CurrentOverallOperationText} LoadTime: {length}ms";
#endif

                        GetConvoLinesBackground();
                        StartOwnerNamePrewarm();

                        if (_isMaterialSelectionMode)
                        {
                            ConfigureMaterialSelectionMode();
                        }
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

        private static readonly MEGame[] DatabaseGenerationGames =
        {
            MEGame.ME1,
            MEGame.ME2,
            MEGame.ME3,
            MEGame.LE1,
            MEGame.LE2,
            MEGame.LE3
        };

        private static string GetSwitchGameParameter(MEGame game) => game.ToString();

        private void RefreshAllGamesScanProgress()
        {
            AllGamesScanProgress.ReplaceAll(DatabaseGenerationGames.Select(game =>
            {
                string status = _allGamesProgressStatus.TryGetValue(game, out var value) ? value : "Pending";
                return $"{game}: {status}";
            }));
        }

        private void SetAllGamesScanStatus(MEGame game, string status)
        {
            _allGamesProgressStatus[game] = status;
            RefreshAllGamesScanProgress();
        }

        private void InitializeAllGamesScanProgress()
        {
            _allGamesProgressStatus.Clear();
            foreach (var game in DatabaseGenerationGames)
            {
                _allGamesProgressStatus[game] = "Pending";
            }

            RefreshAllGamesScanProgress();
        }

        private void ClearAllGamesScanProgress()
        {
            _allGamesProgressStatus.Clear();
            AllGamesScanProgress.ClearEx();
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
            if (FilterText != null)
            {
                FilterText = string.Empty;
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
            else if (tlkUsagesPanel?.SelectedIndex >= 0 && currentView == 11)
            {
                var tu = (TlkUsage)tlkUsagesPanel.SelectedItem;
                (usagepkg, contentdir, usagemount) = FileListExtended[tu.FileKey];
                usageUID = tu.UIndex;
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
            else if (tlkUsagesPanel?.SelectedIndex >= 0 && currentView == 11)
            {
                OpenSelectedTlkUsage(tool);
                return;
            }

            if (usagepkg == null)
            {
                MessageBox.Show("File not found.");
                return;
            }

            OpenInToolkit(tool, GetFilePath(usagepkg, contentdir), usageUID, strRef, realFileName: usagepkg);
        }

        private void OpenSelectedTlkUsage(string tool = null)
        {
            if (tlkUsagesPanel?.SelectedItem is not TlkUsage usage)
            {
                return;
            }

            var (fileName, contentDir, _) = FileListExtended[usage.FileKey];
            var filePath = GetFilePath(fileName, contentDir);
            if (filePath == null)
            {
                return;
            }

            if (string.Equals(tool, "CoalescedEd", StringComparison.OrdinalIgnoreCase))
            {
                OpenCoalescedUsage(filePath, usage.InnerFileName, SelectedTlkString?.StringID ?? 0);
                return;
            }

            if (!string.IsNullOrWhiteSpace(usage.ReferenceName)
                && usage.ReferenceName.StartsWith("Conversation:", StringComparison.OrdinalIgnoreCase))
            {
                OpenInToolkit("DlgEd", filePath, usage.UIndex, SelectedTlkString?.StringID ?? 0, realFileName: fileName);
                return;
            }

            switch (usage.Context)
            {
                case TlkUsageContext.Codex:
                    OpenInPlotEditor(filePath, new PlotUsage(usage.FileKey, usage.UIndex, usage.IsInMod, PlotUsageContext.Codex, usage.ContainerID));
                    return;
                case TlkUsageContext.Quest:
                    OpenInPlotEditor(filePath, new PlotUsage(usage.FileKey, usage.UIndex, usage.IsInMod, PlotUsageContext.Quest, usage.ContainerID));
                    return;
                case TlkUsageContext.Coalesced:
                    OpenCoalescedUsage(filePath, usage.InnerFileName, SelectedTlkString?.StringID ?? 0);
                    return;
                default:
                    OpenInToolkit("PackageEditor", filePath, usage.UIndex, realFileName: fileName);
                    return;
            }
        }

        private static void OpenCoalescedUsage(string filePath, string innerFileName, int stringId)
        {
            var coalescedEditor = Application.Current.Windows.OfType<CoalescedEditorWindow>().FirstOrDefault() ?? new CoalescedEditorWindow();
            if (!coalescedEditor.IsVisible)
            {
                coalescedEditor.Show();
            }

            coalescedEditor.NavigateToReference(filePath, innerFileName, stringId > 0 ? stringId.ToString() : null);
            coalescedEditor.Activate();
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

            if (filePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                OpenCoalescedUsage(filePath, null, strRef);
                return;
            }


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
                FilterText = string.Empty;
                UsageFilterText = string.Empty;
                ShowUsageFilter = currentView is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 9 or 10 or 11;
                Filter();
                switch (currentView)
                {
                    case 2:
                        if (MaterialTextureTypeFilters.Count <= 1 && CurrentDataBase.Materials.Count > 0)
                        {
                            RefreshMaterialTextureDropdownFilters();
                        }
                        FilterWatermark = "Search (by material name or parent package)";
                        break;
                    case 4:
                        FilterWatermark = "Search (by texture name, package, type, size, or CRC if compiled)";
                        break;
                    case 0:
                        FilterWatermark = "Search (by filename or source directory)";
                        break;
                    case 10:
                        FilterWatermark = "Search (by event name, command text, or type)";
                        break;
                    case 11:
                        FilterWatermark = "Search (by TLK string id, parsed value, or source)";
                        break;
                    default:
                        FilterWatermark = "Search";
                        break;
                }

                RefreshUsageViews();

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

        private void ConfigureMaterialSelectionMode()
        {
            Title = _selectMaterialInstancesOnly ? "Select donor MaterialInstanceConstant" : "Select donor Material";
            FilterWatermark = _selectMaterialInstancesOnly ? "Search donor MaterialInstanceConstants" : "Search donor materials";

            if (MainTabControl != null)
            {
                for (int i = 0; i < MainTabControl.Items.Count; i++)
                {
                    if (MainTabControl.Items[i] is TabItem tab)
                    {
                        tab.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }

            currentView = 2;
            SelectedMaterialTypeFilter = _selectMaterialInstancesOnly ? MaterialInstanceConstantFilterOption : NormalMaterialFilterOption;
            FilterText = _initialMaterialSearchText ?? string.Empty;
            Filter();

            if (lstbx_Materials?.SelectedItem is null && lstbx_Materials?.Items.Count > 0)
            {
                lstbx_Materials.SelectedIndex = 0;
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
                FilterText = string.Empty;
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

                RefreshUsageView(lstbx_PlotUsages);
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

        private void ExtractFilteredLinesAudio_Click(object sender, RoutedEventArgs e)
        {
            if (currentView != 8)
            {
                return;
            }

            var visibleLines = lstbx_Lines.Items.Cast<ConvoLine>().ToList();
            if (visibleLines.Count == 0)
            {
                MessageBox.Show(this, "There are no shown lines to extract audio from.", "Extract Filtered Audio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (cmbbx_filterSpkrs.SelectedIndex < 0 && string.IsNullOrWhiteSpace(LineSearchText))
            {
                MessageBox.Show(this, "Apply a speaker filter or line search first.", "Extract Filtered Audio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!PromptAudioGenderSelection(out bool extractMale, out bool extractFemale))
            {
                return;
            }

            var dlg = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                EnsurePathExists = true,
                Title = "Select Output Folder for Filtered Audio"
            };
            if (dlg.ShowDialog(this) != CommonFileDialogResult.Ok)
            {
                return;
            }

            string outputFolder = dlg.FileName;
            bool includeText = MessageBox.Show(this,
                "Include dialogue text in filenames?",
                "Filename Options",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;

            BusyHeader = "Extracting filtered line audio";
            BusyBarInd = true;
            IsBusy = true;
            BusyText = "Preparing to extract audio...";

            Task.Run(() => ExtractFilteredLinesAudio(visibleLines, outputFolder, includeText, extractMale, extractFemale))
                .ContinueWithOnUIThread(task =>
                {
                    IsBusy = false;
                    if (task.IsFaulted)
                    {
                        MessageBox.Show(this,
                            $"Error during extraction:\n{task.Exception?.InnerException?.Message ?? task.Exception?.Message}",
                            "Extraction Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    var result = task.Result;
                    MessageBox.Show(this,
                        $"Extraction complete!\nConversations processed: {result.ConversationsProcessed}\nAudio files extracted: {result.AudioFilesExtracted}",
                        "Extraction Complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
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

        private bool PromptAudioGenderSelection(out bool extractMale, out bool extractFemale)
        {
            extractMale = true;
            extractFemale = true;

            var genderDialog = new Window
            {
                Title = "Select Genders",
                Width = 350,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };
            genderDialog.SetResourceReference(Window.BackgroundProperty, SystemColors.WindowBrushKey);
            genderDialog.SetResourceReference(Window.ForegroundProperty, SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(genderDialog);

            string genderChoice = null;

            var textBlock = new TextBlock
            {
                Text = "Which audio files would you like to extract?",
                Margin = new Thickness(10, 15, 10, 10),
                TextWrapping = TextWrapping.Wrap
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10)
            };

            var bothBtn = new Button { Content = "Both", Width = 70, Margin = new Thickness(5) };
            var maleBtn = new Button { Content = "Male", Width = 70, Margin = new Thickness(5) };
            var femaleBtn = new Button { Content = "Female", Width = 70, Margin = new Thickness(5) };
            var cancelBtn = new Button { Content = "Cancel", Width = 70, Margin = new Thickness(5), IsCancel = true };

            bothBtn.Click += (_, _) => { genderChoice = "both"; genderDialog.DialogResult = true; };
            maleBtn.Click += (_, _) => { genderChoice = "male"; genderDialog.DialogResult = true; };
            femaleBtn.Click += (_, _) => { genderChoice = "female"; genderDialog.DialogResult = true; };

            buttonPanel.Children.Add(bothBtn);
            buttonPanel.Children.Add(maleBtn);
            buttonPanel.Children.Add(femaleBtn);
            buttonPanel.Children.Add(cancelBtn);

            var mainPanel = new StackPanel();
            mainPanel.Children.Add(textBlock);
            mainPanel.Children.Add(buttonPanel);
            genderDialog.Content = mainPanel;

            if (genderDialog.ShowDialog() != true)
            {
                return false;
            }

            extractMale = genderChoice is "both" or "male";
            extractFemale = genderChoice is "both" or "female";
            return true;
        }

        private OutdatedDatabaseAction PromptOutdatedDatabaseAction(string currentVersion, string requiredVersion)
        {
            var actionDialog = new Window
            {
                Title = "Database Out of Date",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };
            actionDialog.SetResourceReference(Window.BackgroundProperty, SystemColors.WindowBrushKey);
            actionDialog.SetResourceReference(Window.ForegroundProperty, SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(actionDialog);

            OutdatedDatabaseAction selectedAction = OutdatedDatabaseAction.Cancel;

            var textBlock = new TextBlock
            {
                Text = $"This database is out of date (v {currentVersion} versus v {requiredVersion}).\nA new version is required. Choose what to rebuild:",
                Margin = new Thickness(10, 15, 10, 10),
                TextWrapping = TextWrapping.Wrap,
                Width = 390
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10)
            };

            var currentGameBtn = new Button { Content = "Rebuild Current Game", MinWidth = 150, Margin = new Thickness(5) };
            var allGamesBtn = new Button { Content = "Rebuild All Games", MinWidth = 150, Margin = new Thickness(5) };
            var cancelBtn = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(5), IsCancel = true };

            currentGameBtn.Click += (_, _) =>
            {
                selectedAction = OutdatedDatabaseAction.RebuildCurrentGame;
                actionDialog.DialogResult = true;
            };
            allGamesBtn.Click += (_, _) =>
            {
                selectedAction = OutdatedDatabaseAction.RebuildAllGames;
                actionDialog.DialogResult = true;
            };

            buttonPanel.Children.Add(currentGameBtn);
            buttonPanel.Children.Add(allGamesBtn);
            buttonPanel.Children.Add(cancelBtn);

            var mainPanel = new StackPanel();
            mainPanel.Children.Add(textBlock);
            mainPanel.Children.Add(buttonPanel);
            actionDialog.Content = mainPanel;

            return actionDialog.ShowDialog() == true ? selectedAction : OutdatedDatabaseAction.Cancel;
        }

        private (int ConversationsProcessed, int AudioFilesExtracted) ExtractFilteredLinesAudio(List<ConvoLine> visibleLines, string outputFolder, bool includeText, bool extractMale, bool extractFemale)
        {
            int conversationsProcessed = 0;
            int audioFilesExtracted = 0;

            foreach (var conversationGroup in visibleLines
                         .GroupBy(line => line.Convo, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryGetConversation(conversationGroup.Key, out var conversation))
                {
                    continue;
                }

                using var conversationContext = TryCreateConversationLoadContext(conversation);
                if (conversationContext == null)
                {
                    continue;
                }

                var convoData = conversationContext.ConversationData;
                convoData.LoadConversation(TLKManagerWPF.GlobalFindStrRefbyID, true);

                int extractedForConversation = 0;
                foreach (var speakerGroup in conversationGroup
                             .GroupBy(line => GetSpeakerExtractionLabel(line), StringComparer.CurrentCultureIgnoreCase))
                {
                    var matchingNodes = speakerGroup
                        .SelectMany(line => FindMatchingDialogueNodes(convoData, line))
                        .DistinctBy(node => (node.IsReply, node.NodeCount, node.LineStrRef))
                        .ToList();

                    if (matchingNodes.Count == 0)
                    {
                        continue;
                    }

                    var convoFolder = Path.Combine(outputFolder, SanitizePathSegment(conversationGroup.Key));
                    Directory.CreateDirectory(convoFolder);

                    extractedForConversation += DialogueEditorWindow.ExtractAudioFilesForSpeaker(
                        matchingNodes,
                        speakerGroup.Key,
                        includeText,
                        extractMale,
                        extractFemale,
                        convoFolder);
                }

                if (extractedForConversation > 0)
                {
                    conversationsProcessed++;
                    audioFilesExtracted += extractedForConversation;
                }

                Dispatcher.Invoke(() => BusyText = $"Extracting audio...\nConversations: {conversationsProcessed}\nAudio files: {audioFilesExtracted}");
            }

            return (conversationsProcessed, audioFilesExtracted);
        }

        private IEnumerable<DialogueNodeExtended> FindMatchingDialogueNodes(ConversationExtended convoData, ConvoLine line)
        {
            if (line == null)
            {
                return [];
            }

            if (string.Equals(line.Speaker, "Shepard", StringComparison.OrdinalIgnoreCase))
            {
                return convoData.ReplyList.Where(node => node.LineStrRef == line.StrRef);
            }

            if (string.Equals(line.Speaker, "Owner", StringComparison.OrdinalIgnoreCase))
            {
                return convoData.EntryList.Where(node => node.LineStrRef == line.StrRef
                    && string.Equals(node.SpeakerTag?.SpeakerName, "owner", StringComparison.OrdinalIgnoreCase));
            }

            return convoData.EntryList.Where(node => node.LineStrRef == line.StrRef
                && string.Equals(node.SpeakerTag?.SpeakerName, line.Speaker, StringComparison.OrdinalIgnoreCase));
        }

        private string GetSpeakerExtractionLabel(ConvoLine line) => GetSpeakerFilterValue(line) ?? line?.Speaker ?? "speaker";

        private ConversationLoadContext TryCreateConversationLoadContext(Conversation conversation)
        {
            if (conversation == null)
            {
                return null;
            }

            if (TryOpenConversationPackage(conversation, out var conversationPackage)
                && TryResolveConversationExport(conversationPackage, conversation, out var conversationExport))
            {
                return new ConversationLoadContext(new ConversationExtended(conversationExport), conversationPackage);
            }

            conversationPackage?.Dispose();

            if (TryResolveConversationExportFromStartConversation(conversation, out var resolvedConversationExport, out var startConversationPackage, out var packageCache))
            {
                return new ConversationLoadContext(new ConversationExtended(resolvedConversationExport), startConversationPackage, packageCache);
            }

            startConversationPackage?.Dispose();
            packageCache?.Dispose();
            return null;
        }

        private bool TryResolveConversationExport(IMEPackage package, Conversation conversation, out ExportEntry conversationExport)
        {
            conversationExport = null;
            if (package == null || conversation == null)
            {
                return false;
            }

            if (conversation.ConvFile.UIndex > 0
                && package.TryGetUExport(conversation.ConvFile.UIndex, out var indexedExport)
                && IsMatchingConversationExport(indexedExport, conversation.ConvName))
            {
                conversationExport = indexedExport;
                return true;
            }

            conversationExport = package.Exports.FirstOrDefault(export => IsMatchingConversationExport(export, conversation.ConvName));
            return conversationExport != null;
        }

        private bool TryResolveConversationExportFromStartConversation(Conversation conversation, out ExportEntry conversationExport, out IMEPackage startConversationPackage, out PackageCache packageCache)
        {
            conversationExport = null;
            startConversationPackage = null;
            packageCache = null;

            if (conversation == null || string.IsNullOrWhiteSpace(conversation.PackageName) || conversation.ConversationExportIndex <= 0)
            {
                return false;
            }

            startConversationPackage = OpenOwnerResolverPackage(conversation.PackageName);
            if (startConversationPackage == null
                || !startConversationPackage.TryGetUExport(conversation.ConversationExportIndex, out var startConversationExport))
            {
                return false;
            }

            var conversationRef = startConversationExport.GetProperty<ObjectProperty>("Conv");
            if (conversationRef == null || conversationRef.Value == 0)
            {
                return false;
            }

            if (conversationRef.Value > 0
                && startConversationPackage.TryGetUExport(conversationRef.Value, out var localConversationExport)
                && IsMatchingConversationExport(localConversationExport, conversation.ConvName))
            {
                conversationExport = localConversationExport;
                return true;
            }

            packageCache = new PackageCache();
            conversationExport = ResolveToExport(startConversationPackage, conversationRef.Value, packageCache);
            if (!IsMatchingConversationExport(conversationExport, conversation.ConvName))
            {
                conversationExport = null;
                return false;
            }

            return true;
        }

        private static bool IsMatchingConversationExport(ExportEntry export, string conversationName)
        {
            return export?.ClassName == "BioConversation"
                && string.Equals(export.ObjectName.Instanced, conversationName, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryOpenConversationPackage(Conversation conversation, out IMEPackage package)
        {
            package = null;
            if (conversation == null || conversation.ConvFile.FileKey < 0 || conversation.ConvFile.FileKey >= CurrentDataBase.FileList.Count)
            {
                return false;
            }

            var (fileName, directoryKey) = CurrentDataBase.FileList[conversation.ConvFile.FileKey];
            var contentDir = CurrentDataBase.ContentDir[directoryKey];
            var filePath = GetFilePath(fileName, contentDir);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            package = fetchPackage(filePath, conversation.ConvFile.FileKey, fileName);
            return package != null;
        }

        private IMEPackage OpenConversationPackage(Conversation conversation)
        {
            if (conversation == null || conversation.ConvFile.FileKey < 0 || conversation.ConvFile.FileKey >= CurrentDataBase.FileList.Count)
            {
                return null;
            }

            var (fileName, _) = CurrentDataBase.FileList[conversation.ConvFile.FileKey];
            return OpenOwnerResolverPackage(fileName);
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Conversation";
            }

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return value;
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
                    copytext = line.DisplayLine;
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
                    string selectedSpeaker = cmbbx_filterSpkrs.SelectedItem.ToString();
                    string displaySpeaker = GetSpeakerDisplayForSearch(line);
                    string filterSpeaker = GetSpeakerFilterValue(line);
                    showthis = string.Equals(line.Speaker, selectedSpeaker, StringComparison.CurrentCultureIgnoreCase)
                               || string.Equals(filterSpeaker, selectedSpeaker, StringComparison.CurrentCultureIgnoreCase)
                               || string.Equals(displaySpeaker, selectedSpeaker, StringComparison.CurrentCultureIgnoreCase);
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

            var speakerDisplay = GetSpeakerDisplayForSearch(line);

            return SelectedLineSearchColumn switch
            {
                SpeakerLineSearchColumn => ContainsText(speakerDisplay, searchText),
                TlkStringRefLineSearchColumn => ContainsText(line.StrRef.ToString(), searchText),
                LineTextSearchColumn => ContainsText(line.DisplayLine, searchText),
                LineConversationSearchColumn => ContainsText(line.Convo, searchText),
                FileLineSearchColumn => ContainsText(GetConvoFileValue(line.Convo), searchText),
                LocationLineSearchColumn => ContainsText(GetConvoLocationValue(line.Convo), searchText),
                _ => ContainsText(speakerDisplay, searchText)
                     || ContainsText(line.StrRef.ToString(), searchText)
                     || ContainsText(line.DisplayLine, searchText)
                     || ContainsText(line.Convo, searchText)
                     || ContainsText(GetConvoFileValue(line.Convo), searchText)
                     || ContainsText(GetConvoLocationValue(line.Convo), searchText)
            };
        }

        private bool ContainsText(string source, string searchText)
        {
            return !string.IsNullOrEmpty(source) && source.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
        }

        private bool TlkTabFilter(object obj)
        {
            if (obj is not TlkDisplayRecord tlkRecord)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(FilterText)
                   || ContainsText(tlkRecord.StringID.ToString(), FilterText)
                   || ContainsText(tlkRecord.DisplayValue, FilterText)
                   || ContainsText(tlkRecord.SourceName, FilterText);
        }

        public string GetUsageDisplayText(object usage)
        {
            if (usage is TlkUsage tlkUsage)
            {
                var baseText = TryGetUsageKeys(tlkUsage, out int tlkFileKey, out int tlkUIndex)
                    && tlkFileKey >= 0
                    && tlkFileKey < FileListExtended.Count
                    ? $"{FileListExtended[tlkFileKey].FileName}  # {tlkUIndex}   {FileListExtended[tlkFileKey].Directory}"
                    : usage.ToString();
                return $"{baseText} {tlkUsage.ReferenceDisplay} {tlkUsage.InnerFileName}";
            }

            if (!TryGetUsageKeys(usage, out int fileKey, out int uIndex)
                || fileKey < 0
                || fileKey >= FileListExtended.Count)
            {
                return usage?.ToString() ?? string.Empty;
            }

            var (fileName, directory, _) = FileListExtended[fileKey];
            return $"{fileName}  # {uIndex}   {directory} ";
        }

        public bool UsageMatchesSearch(object usage, string searchText)
        {
            return string.IsNullOrWhiteSpace(searchText) || ContainsText(GetUsageDisplayText(usage), searchText);
        }

        private static bool TryGetUsageKeys(object usage, out int fileKey, out int uIndex)
        {
            fileKey = -1;
            uIndex = 0;

            if (usage is IAssetUsage assetUsage)
            {
                fileKey = assetUsage.FileKey;
                uIndex = assetUsage.UIndex;
                return true;
            }

            var usageType = usage?.GetType();
            var fileKeyProperty = usageType?.GetProperty(nameof(IAssetUsage.FileKey));
            var uIndexProperty = usageType?.GetProperty(nameof(IAssetUsage.UIndex));
            if (fileKeyProperty?.PropertyType == typeof(int)
                && uIndexProperty?.PropertyType == typeof(int)
                && fileKeyProperty.GetValue(usage) is int reflectedFileKey
                && uIndexProperty.GetValue(usage) is int reflectedUIndex)
            {
                fileKey = reflectedFileKey;
                uIndex = reflectedUIndex;
                return true;
            }

            return false;
        }

        private void RefreshUsageViews()
        {
            materialsUsagesPanel?.RefreshFilter();
            meshesUsagesPanel?.RefreshFilter();
            texturesUsagesPanel?.RefreshFilter();
            animationsUsagesPanel?.RefreshFilter();
            vfxUsagesPanel?.RefreshFilter();
            guiUsagesPanel?.RefreshFilter();
            sequenceEventsUsagesPanel?.RefreshFilter();
            tlkUsagesPanel?.RefreshFilter();

            RefreshUsageView(lstbx_Usages);
            RefreshUsageView(lstbx_PlotUsages);
        }

        private void RefreshUsageView(ItemsControl listControl)
        {
            if (listControl?.ItemsSource == null)
            {
                return;
            }

            var view = CollectionViewSource.GetDefaultView(listControl.ItemsSource);
            if (view == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(UsageFilterText))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = item => UsageMatchesSearch(item, UsageFilterText);
            }

            view.Refresh();
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

            if (_convoFileInfoCache.TryGetValue(convoName, out var cachedInfo))
            {
                fileName = cachedInfo.FileName;
                location = cachedInfo.Location;
                return fileName != null;
            }

            if (!TryGetConversation(convoName, out var convo))
            {
                _convoFileInfoCache[convoName] = (null, null);
                return false;
            }

            int fileKey = convo.ConvFile.FileKey;
            if (fileKey < 0 || fileKey >= FileListExtended.Count)
            {
                _convoFileInfoCache[convoName] = (null, null);
                return false;
            }

            fileName = FileListExtended[fileKey].FileName;
            location = FileListExtended[fileKey].Directory;
            _convoFileInfoCache[convoName] = (fileName, location);
            return true;
        }

        private IComparer CreateLineSortComparer(string header, ListSortDirection direction)
        {
            int directionMultiplier = direction == ListSortDirection.Ascending ? 1 : -1;
            return Comparer<object>.Create((left, right) => directionMultiplier * CompareLines(left as ConvoLine, right as ConvoLine, header));
        }

        private int CompareLines(ConvoLine left, ConvoLine right, string header)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int comparison = header switch
            {
                SpeakerLineSearchColumn => CompareLineValues(GetSpeakerDisplayForSearch(left), GetSpeakerDisplayForSearch(right)),
                TlkStringRefLineSearchColumn => left.StrRef.CompareTo(right.StrRef),
                LineTextSearchColumn => CompareLineValues(left.DisplayLine, right.DisplayLine),
                LineConversationSearchColumn => CompareLineValues(left.Convo, right.Convo),
                FileLineSearchColumn => CompareLineValues(GetConvoFileValue(left.Convo), GetConvoFileValue(right.Convo)),
                LocationLineSearchColumn => CompareLineValues(GetConvoLocationValue(left.Convo), GetConvoLocationValue(right.Convo)),
                _ => 0
            };

            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareLineValues(left.Convo, right.Convo);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.StrRef.CompareTo(right.StrRef);
            if (comparison != 0)
            {
                return comparison;
            }

            return CompareLineValues(left.DisplayLine, right.DisplayLine);
        }

        private static int CompareLineValues(string left, string right)
        {
            return StringComparer.CurrentCultureIgnoreCase.Compare(left ?? string.Empty, right ?? string.Empty);
        }

        private bool FileFilter(object d)
        {
            bool showthis = true;
            var f = (FileDirPair)d;
            var t = FilterText;
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

        private bool SequenceEventTabFilter(object obj)
        {
            if (obj is not SequenceEventRecord sequenceEventRecord)
            {
                return false;
            }

            if (!AssetFilters.SequenceEventFilter.Filter(sequenceEventRecord))
            {
                return false;
            }

            return SelectedSequenceEventTypeFilter switch
            {
                ActivateRemoteEventFilterOption => sequenceEventRecord.EventType == SequenceEventType.ActivateRemoteEvent,
                ConsoleCommandFilterOption => sequenceEventRecord.EventType == SequenceEventType.ConsoleCommand,
                RemoteEventFilterOption => sequenceEventRecord.EventType == SequenceEventType.RemoteEvent,
                ConsoleEventFilterOption => sequenceEventRecord.EventType == SequenceEventType.ConsoleEvent,
                _ => true
            };
        }

        private void Filter()
        {
            AssetFilters.SetSearch(FilterText);
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
                case 10: // SequenceEvents
                    ICollectionView viewSE = CollectionViewSource.GetDefaultView(CurrentDataBase.SequenceEvents);
                    viewSE.Filter = SequenceEventTabFilter;
                    lstbx_SequenceEvents.ItemsSource = viewSE;
                    break;
                case 11: // TLK Strings
                    ICollectionView viewTlk = CollectionViewSource.GetDefaultView(DisplayedTlkStrings);
                    viewTlk.Filter = TlkTabFilter;
                    lstbx_TlkStrings.ItemsSource = viewTlk;
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
                            linedataView.SortDescriptions.Clear();
                            if (linedataView is ListCollectionView listCollectionView)
                            {
                                listCollectionView.CustomSort = CreateLineSortComparer(headerClicked.Column.Header?.ToString(), direction);
                            }
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
            ScheduleLineSearchFilter();
        }

        private void ClearSpeakerFilter_Click(object sender, RoutedEventArgs e)
        {
            cmbbx_filterSpkrs.SelectedIndex = -1;
            ScheduleLineSearchFilter();
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

            RefreshUsageView(lstbx_Usages);
        }

        #endregion

        #region Scan

        // 05/02/2025 - Add .sfar
        private static List<string> SupportedFileExtensions = new List<string> { ".u", ".upk", ".sfm", ".pcc", ".cnd", ".sfar", ".bin" };

        private async void ScanGame()
        {
            await ScanGameAsync(CurrentGame);
        }

        private async Task ScanGameAsync(MEGame game, bool updateUiAfterScan = true, bool showCompletionMessage = true, bool preserveBusyState = false, bool manageWindowState = true, bool showMissingGameMessage = true)
        {
            string rootPath = MEDirectories.GetDefaultGamePath(game);

            if (rootPath == null || !Directory.Exists(rootPath))
            {
                if (showMissingGameMessage)
                {
                    MessageBox.Show($"{game} has not been found. Please check your Legendary Explorer settings");
                }
                return;
            }

            rootPath = Path.GetFullPath(rootPath);

            string ShaderCacheName = game.IsLEGame() ? "RefShaderCache-PC-D3D-SM5.upk" : "RefShaderCache-PC-D3D-SM3.upk";
            List<string> files = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .Where(s => SupportedFileExtensions.Contains(Path.GetExtension(s.ToLower()))
                            && !s.EndsWith(ShaderCacheName))
                .ToList();

            await dumpPackages(files, game, updateUiAfterScan, showCompletionMessage, preserveBusyState, manageWindowState);
        }

        private async Task dumpPackages(List<string> files, MEGame game, bool updateUiAfterScan = true, bool showCompletionMessage = true, bool preserveBusyState = false, bool manageWindowState = true)
        {
            var beginTime = DateTime.Now;
            if (manageWindowState)
            {
                TopDock.IsEnabled = false;
                MidDock.IsEnabled = false;
            }

            CurrentGame = game;
            CurrentDBPath = GetDBPath(game);
            OverallProgressMaximum = files.Count;
            OverallProgressValue = 0;
            BusyBarInd = false;
            CurrentOverallOperationText = $"Generating database for {game}...";
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
            BusyHeader = $"Generating database for {game}";
            ProcessingQueue = new ActionBlock<SingleFileScanner>(x =>
            {
                if (x.DumpCanceled)
                {
                    return;
                }
                x.DumpPackageFile(game, GeneratedDB);
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    string currentStatus = $"Scanning {OverallProgressValue}/{OverallProgressMaximum} files";
                    if (_isGeneratingAllDatabases)
                    {
                        SetAllGamesScanStatus(game, currentStatus);
                    }

                    BusyText = $"{game}: {currentStatus}\n\n{GeneratedDB.GetProgressString()}";
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
                    if (_isGeneratingAllDatabases)
                    {
                        SetAllGamesScanStatus(game, "Canceled");
                    }
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
                if (!preserveBusyState)
                {
                    IsBusy = false;
                }
                isProcessing = false;
                if (manageWindowState)
                {
                    TopDock.IsEnabled = true;
                    MidDock.IsEnabled = true;
                }
                throw caughtException;
            }

            BusyHeader += "Collating and sorting the database";
            BusyText = "Please wait...";
            BusyBarInd = true;
            if (_isGeneratingAllDatabases)
            {
                SetAllGamesScanStatus(game, "Collating and sorting");
            }
            CommandManager.InvalidateRequerySuggested();

            AssetDB pdb = await Task.Run(GeneratedDB.CollateDataBase);
            GeneratedDB.Clear();
            //Add and sort Classes
            CurrentDataBase.AddRecords(pdb);
            RebuildConversationLookup();

            if (updateUiAfterScan)
            {
                var dlcs = MELoadedDLC.GetDLCNamesWithMounts(game);
                dlcs.Add("BioGame", 0);
                foreach ((string fileName, int directoryKey) in CurrentDataBase.FileList)
                {
                    var cd = CurrentDataBase.ContentDir[directoryKey];
                    int mount = -1;
                    dlcs.TryGetValue(cd, out mount);
                    FileListExtended.Add(new(fileName, cd, mount));
                }

                AssetFilters.MaterialFilter.LoadFromDatabase(CurrentDataBase);
                RefreshMaterialUsageDropdownFilters();
                RefreshMaterialTextureDropdownFilters();
                RefreshTextureDropdownFilters();
                LoadTlkData();
            }

            Settings.AssetDBGame = CurrentDataBase.Game.ToString();
            isProcessing = false;
            if (_isGeneratingAllDatabases)
            {
                SetAllGamesScanStatus(game, "Saving database");
            }

            await SaveDatabaseAsync(preserveBusyState, suppressFinalSummary: preserveBusyState || !updateUiAfterScan);
            if (manageWindowState)
            {
                TopDock.IsEnabled = true;
                MidDock.IsEnabled = true;
            }
            if (!preserveBusyState)
            {
                IsBusy = false;
            }
            var elapsed = DateTime.Now - beginTime;
            if (showCompletionMessage)
            {
                MessageBox.Show(this, $"{game} Database generated in {elapsed:mm\\:ss}");
            }

            MemoryAnalyzer.ForceFullGC(true);
            if (updateUiAfterScan)
            {
                // 08/27/2023 - Removed !IsGame1() check on GetConvoLinesBackground()
                GetConvoLinesBackground();
                StartOwnerNamePrewarm();
                CurrentDataBase.PlotUsages.LoadPlotPaths(game);
            }
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
                10 => sequenceEventsUsagesPanel.SelectedItem as IAssetUsage,
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
            OnPropertyChanged(nameof(HasSelectedMaterialRecord));
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

        private void SelectedMaterial_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_isMaterialSelectionMode)
            {
                ConfirmSelectedMaterial();
            }
        }

        private void SelectMaterialButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmSelectedMaterial();
        }

        private void CancelMaterialSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ConfirmSelectedMaterial()
        {
            if (lstbx_Materials?.SelectedItem is not MaterialRecord materialRecord)
            {
                return;
            }

            SelectedMaterialDialogResult = new MaterialSelectionResult(materialRecord);

            if (_isMaterialSelectionMode)
            {
                _materialSelectionHandler?.Invoke(SelectedMaterialDialogResult);
                Close();
                return;
            }

            DialogResult = true;
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
