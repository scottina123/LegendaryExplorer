using Gammtek.Conduit.MassEffect3.SFXGame.StateEventMap;
using GongSolutions.Wpf.DragDrop;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.Packages;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.SharedUI.Controls;
using LegendaryExplorer.Tools.ConditionalsEditor;
using LegendaryExplorer.Tools.CustomFilesManager;
using LegendaryExplorer.Tools.PlotEditor;
using LegendaryExplorer.Tools.Sequence_Editor.Experiments;
using LegendaryExplorer.Tools.SequenceObjects;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.Extensions.Collections.Generic;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Matinee;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Misc.ME3Tweaks;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json;
using Piccolo;
using Piccolo.Event;
using Piccolo.Nodes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorer.Tools.ObjectReferenceViewer;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.Libraries;
using LegendaryExplorer.UserControls.PackageEditorControls;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.Matinee;
using Xceed.Wpf.Toolkit;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using WindowStartupLocation = System.Windows.WindowStartupLocation;

namespace LegendaryExplorer.Tools.Sequence_Editor
{
    /// <summary>
    /// Interaction logic for SequenceEditorWPF.xaml
    /// </summary>
    public partial class SequenceEditorWPF : WPFBase, IRecents, IDropTarget
    {
        private readonly SequenceGraphEditor graphEditor;
        private Window floatingToolboxWindow;
        private System.Windows.Point floatingToolboxScreenLocation;
        private ClassToolBox floatingFavoritesToolBox;
        private ClassToolBox floatingEventsToolBox;
        private ClassToolBox floatingActionsToolBox;
        private ClassToolBox floatingConditionsToolBox;
        private ClassToolBox floatingVariablesToolBox;
        private ClassToolBox floatingSceneShopToolBox;
        private GenericToolBox floatingCustomSequencesToolBox;
        private SearchBox floatingToolboxSearchBox;
        private GenericToolBox floatingToolboxSearchResults;
        private TabControl floatingToolboxTabs;
        private TabItem floatingSceneShopTab;
        private TabItem floatingCustomSequencesTab;
        private List<MenuItem> experimentMenuItems;
        public ObservableCollectionExtended<SObj> CurrentObjects { get; } = new();
        public ObservableCollectionExtended<SObj> SelectedObjects { get; } = new();
        public ObservableCollectionExtended<ExportEntry> SequenceExports { get; } = new();
        public ObservableCollectionExtended<TreeViewEntry> TreeViewRootNodes { get; } = new();
        public ObservableCollectionExtended<TreeViewEntry> InterpDataTreeNodes { get; } = new();
        public ObservableCollectionExtended<string> CustomSequenceObjectSourceFiles { get; } = new();
        public string CurrentFile;
        public string JSONpath;

        private bool _useSavedViews = true; // Should probably be a global setting
        private int suppressInterpreterUnloadDepth;
        private int suppressInterpDataInterpreterUnloadDepth;
        private DispatcherOperation pendingInterpDataEditorsReload;
        private bool isEmbedded;
        private bool isEmbeddedContentLoaded;
        private bool isDisposed;

        public bool UseSavedViews
        {
            get => _useSavedViews;
            set
            {
                if (SetProperty(ref _useSavedViews, value) && SelectedSequence != null)
                {
                    LoadSequence(SelectedSequence);
                }
            }
        }

        private ExportEntry _selectedSequence;
        private bool _isSceneShopSequenceSelected;

        public ExportEntry SelectedSequence
        {
            get => _selectedSequence;
            set
            {
                if (SetProperty(ref _selectedSequence, value))
                {
                    IsSceneShopSequenceSelected = value?.IsA("SFXSceneShopGameData") == true;
                }
            }
        }

        public bool IsSceneShopSequenceSelected
        {
            get => _isSceneShopSequenceSelected;
            private set
            {
                if (SetProperty(ref _isSceneShopSequenceSelected, value))
                {
                    if (!string.IsNullOrWhiteSpace(toolboxSearchBox?.Text))
                    {
                        UpdateToolboxSearchResults(toolboxSearchBox.Text);
                    }

                    if (!string.IsNullOrWhiteSpace(floatingToolboxSearchBox?.Text))
                    {
                        UpdateFloatingToolboxSearchResults(floatingToolboxSearchBox.Text);
                    }
                }
            }
        }

        public record SavedViewData(Dictionary<int, PointF> Positions, RectangleF ViewBounds);
        private record CopiedInputConnection(int SourceUIndex, string OutputDescription, int OutputIndex, int InputIndex);
        private record CopiedOutputConnection(string OutputDescription, int OutputIndex, int TargetUIndex, int InputIndex);
        private record CopiedVariableConnection(string VariableDescription, int VariableIndex, int TargetUIndex);
        private record CopiedConnectionSet(
            List<CopiedInputConnection> InputConnections,
            List<CopiedOutputConnection> OutputConnections,
            List<CopiedVariableConnection> VariableConnections,
            string SourceFilePath);
        private record SelectionHistoryEntry(string FilePath, int ObjectUIndex);
        private record QuickCreateMenuEntry(string Header, params string[] ClassNames);
        private record FloatingToolboxSearchEntry(string Category, object Item)
        {
            public override string ToString()
            {
                string itemName = Item is ClassInfo classInfo ? classInfo.ClassName : Item.ToString() ?? string.Empty;
                return $"{itemName} [{Category}]";
            }
        }

        private static readonly QuickCreateMenuEntry[] QuickCreateEventEntries =
        [
            new("Console", "SeqEvent_Console"),
            new("ConvNode", "BioSeqEvt_ConvNode"),
            new("Level is live", "SeqEvent_LevelIsLive"),
            new("Level is loaded", "SeqEvent_LevelLoaded"),
            new("RemoteEvent", "SeqEvent_RemoteEvent"),
            new("Sequence Activated", "SeqEvent_SequenceActivated"),
            new("Touch", "SeqEvent_Touch"),
            new("Touch (SFX)", "SFXSeqEvt_Touch"),
            new("Used", "SeqEvent_Used")
        ];

        private static readonly QuickCreateMenuEntry[] QuickCreateActionEntries =
        [
            new("ActivateRemoteEvent", "SeqAct_ActivateRemoteEvent"),
            new("AddToParty", "BioSeqAct_AddToParty"),
            new("AttachToActor", "SeqAct_AttachToActor"),
            new("AttachToEvent", "SeqAct_AttachToEvent"),
            new("Ambient Performance", "SFXSeqAct_SetAmbientPerformance"),
            new("BlackScreen", "BioSeqAct_BlackScreen", "SFXSeqAct_BlackScreen"),
            new("CombatPawn", "BioSeqVar_CombatPawn"),
            new("Delay", "SeqAct_Delay"),
            new("EnableAI", "BioSeqAct_EnableAI"),
            new("Finish Sequence", "SeqAct_FinishSequence"),
            new("Gate", "SeqAct_Gate"),
            new("GetTag", "SeqAct_GetTag"),
            new("LevelStreaming", "BioSeqAct_LevelStreaming"),
            new("MailGUI", "SFXSeqAct_MailGUI_Sorted"),
            new("Random Switch", "SeqAct_RandomSwitch"),
            new("RemoveFromParty", "BioSeqAct_RemoveFromParty"),
            new("SetActive", "BioSeqAct_SetActive"),
            new("SetLocation", "SeqAct_SetLocation"),
            new("SetMultipleStreamingStates", "BioSeqAct_SetMultipleStreamingStates"),
            new("SetObject", "SeqAct_SetObject"),
            new("SetStreamingState", "BioSeqAct_SetStreamingState"),
            new("SetTag", "SeqAct_SetTag"),
            new("SetTargetable", "BioSeqAct_SetTargetable"),
            new("Teleport", "SeqAct_Teleport"),
            new("ToggleHidden", "SeqAct_ToggleHidden"),
            new("WwisePostEvent", "SeqAct_WwisePostEvent")
        ];

        private static readonly QuickCreateMenuEntry[] QuickCreateConversationActionEntries =
        [
            new("EndCurrentConvNode", "BioSeqAct_EndCurrentConvNode"),
            new("FaceOnly VO", "SFXSeqAct_FaceOnlyVO"),
            new("Interp", "SeqAct_Interp"),
            new("Start Ambient Conversation", "BioSeqAct_StartAmbientConv", "SFXSeqAct_StartAmbientConv", "SeqAct_StartAmbientConv"),
            new("Start Conversation", "BioSeqAct_StartConversation", "SFXSeqAct_StartConversation", "SeqAct_StartConversation"),
            new("WwisePostEvent", "SeqAct_WwisePostEvent")
        ];

        private static readonly QuickCreateMenuEntry[] QuickCreatePlotActionEntries =
        [
            new("CheckConditional", "BioSeqAct_PMCheckConditional"),
            new("CheckState", "BioSeqAct_PMCheckState"),
            new("ExecuteTransition", "BioSeqAct_PMExecuteTransition"),
            new("SetBool", "SeqAct_SetBool"),
            new("SetInt", "SeqAct_SetInt")
        ];

        private static readonly QuickCreateMenuEntry[] QuickCreateConditionalEntries =
        [
            new("Compare bool", "SeqCond_CompareBool"),
            new("Compareint", "SeqCond_CompareInt")
        ];

        private static readonly QuickCreateMenuEntry[] QuickCreateVariableEntries =
        [
            new("Bool", "SeqVar_Bool"),
            new("Interpdata", "InterpData"),
            new("Name", "SeqVar_Name"),
            new("Object", "SeqVar_Object"),
            new("Object FindBy Tag", "BioSeqVar_ObjectFindByTag"),
            new("Player", "SeqVar_Player"),
            new("SeqVarInt", "SeqVar_Int"),
            new("Story Manager Bool", "BioSeqVar_StoryManagerBool"),
            new("StoryManager Int", "BioSeqVar_StoryManagerInt"),
            new("StrRef", "BioSeqVar_StrRef")
        ];

        private SavedViewData SavedView;
        private bool forceAutoLayoutOnInitialPackageLoad = true;
        private bool forceAutoLayoutForCurrentPackage;
        private readonly HashSet<int> autoLaidOutSequencesForCurrentPackage = [];
        private PointF? backgroundContextMenuGraphLocation;
        private PointF? pendingNewObjectPosition;
        private List<CopiedInputConnection> copiedInputConnections;
        private string copiedInputConnectionsSourceFilePath;
        private List<CopiedOutputConnection> copiedOutputConnections;
        private string copiedOutputConnectionsSourceFilePath;
        private List<CopiedVariableConnection> copiedVariableConnections;
        private string copiedVariableConnectionsSourceFilePath;
        private CopiedConnectionSet copiedAllConnections;
        private readonly List<SelectionHistoryEntry> selectionHistory = [];
        private int selectionHistoryIndex = -1;
        private bool suppressSelectionHistory;

        public static readonly string SequenceEditorDataFolder =
            Path.Combine(AppDirectories.AppDataFolder, @"SequenceEditor\");

        public static readonly string
            OptionsPath = Path.Combine(SequenceEditorDataFolder, "SequenceEditorOptions.JSON");

        public static readonly string CustomSequenceObjectSourcesPath =
            Path.Combine(SequenceEditorDataFolder, "CustomSequenceObjectSources.json");

        public static readonly string ME3ViewsPath = Path.Combine(SequenceEditorDataFolder, @"ME3SequenceViews\");
        public static readonly string ME2ViewsPath = Path.Combine(SequenceEditorDataFolder, @"ME2SequenceViews\");
        public static readonly string ME1ViewsPath = Path.Combine(SequenceEditorDataFolder, @"ME1SequenceViews\");
        public static readonly string LE3ViewsPath = Path.Combine(SequenceEditorDataFolder, @"LE3SequenceViews\");
        public static readonly string LE2ViewsPath = Path.Combine(SequenceEditorDataFolder, @"LE2SequenceViews\");
        public static readonly string LE1ViewsPath = Path.Combine(SequenceEditorDataFolder, @"LE1SequenceViews\");

        public SequenceEditorWPF() : base("Sequence Editor")
        {
            LoadCommands();
            DataContext = this;
            StatusText = "Select package file to load";
            InitializeComponent();
            InitializeExperimentsBrowser();

            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, x => LoadFile(x));

            // Apply theme-appropriate colors based on current dark mode setting
            ApplyThemeDefaults();

            // Subscribe to theme changes to update graph colors dynamically
            ThemeManager.ThemeChanged += OnThemeChanged;

            graphEditor = (SequenceGraphEditor)GraphHost.Child;
            graphEditor.BackColor = GraphEditorBackColor;
            graphEditor.Camera.MouseDown += backMouseDown_Handler;
            graphEditor.Camera.MouseUp += back_MouseUp;

            graphEditor.Click += graphEditor_Click;
            graphEditor.DragDrop += SequenceEditor_DragDrop;
            graphEditor.DragEnter += SequenceEditor_DragEnter;

            favoritesToolBox.DoubleClickCallback = CreateNewObject;
            eventsToolBox.DoubleClickCallback = CreateNewObject;
            actionsToolBox.DoubleClickCallback = CreateNewObject;
            conditionsToolBox.DoubleClickCallback = CreateNewObject;
            variablesToolBox.DoubleClickCallback = CreateNewObject;
            sceneShopToolBox.DoubleClickCallback = CreateNewObject;
            customSequencesToolBox.DoubleClickCallback = CreateCustomSequence;
            toolboxSearchResults.DoubleClickCallback = CreateFloatingToolboxSearchResult;
            toolboxSearchResults.ShiftClickCallback = ToggleFloatingToolboxSearchResultFavorite;

            favoritesToolBox.ShiftClickCallback = RemoveFavorite;
            eventsToolBox.ShiftClickCallback = SetFavorite;
            actionsToolBox.ShiftClickCallback = SetFavorite;
            conditionsToolBox.ShiftClickCallback = SetFavorite;
            variablesToolBox.ShiftClickCallback = SetFavorite;
            // Custom sequences are not ClassInfo so they cannot be set as a favorite

            AutoSaveView_MenuItem.IsChecked = Settings.SequenceEditor_AutoSaveViewV2;
            ShowOutputNumbers_MenuItem.IsChecked = Settings.SequenceEditor_ShowOutputNumbers;
            SObj.OutputNumbers = ShowOutputNumbers_MenuItem.IsChecked;

            // Initialize color pickers with loaded colors
            ClrPcker_Background.SelectedColor = GraphEditorBackColor.ToWPFColor();
            ClrPcker_BoxFill.SelectedColor = BoxFillColor.ToWPFColor();
            ClrPcker_TitleBox.SelectedColor = TitleBoxColor.ToWPFColor();
            ClrPcker_CommentText.SelectedColor = CommentTextColor.ToWPFColor();
            ClrPcker_BoxText.SelectedColor = BoxTextColor.ToWPFColor();
            ClrPcker_Connection.SelectedColor = ConnectionColor.ToWPFColor();
            ClrPcker_VarLink.SelectedColor = VarLinkColor.ToWPFColor();

            LoadRememberedCustomSequenceObjectSources();
        }

        private void InitializeExperimentsBrowser()
        {
            experimentMenuItems = ExperimentsMenuItem.Items.OfType<MenuItem>().ToList();
            foreach (MenuItem menuItem in experimentMenuItems)
            {
                menuItem.DataContext = this;
            }

            ExperimentsMenuItem.Items.Clear();
            ExperimentsMenuItem.Click += ExperimentsMenuItem_Click;
        }

        private void ExperimentsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<ExperimentBrowserItem> experiments = ExperimentBrowserCatalog.Create(
                experimentMenuItems,
                menuItem => menuItem.Items.OfType<MenuItem>().Any()
                    ? ExperimentBrowserCatalog.GetHeader(menuItem)
                    : "General");
            var browser = new ExperimentsBrowserWindow(this, "Sequence Editor Experiments", experiments);
            if (browser.ShowDialog() == true)
            {
                browser.SelectedExperiment?.Invoke();
            }
        }
        
        /// <summary>
        /// Handles theme changes from the ThemeManager.
        /// Resets graph colors to theme defaults when user switches between light/dark mode.
        /// </summary>
        private void OnThemeChanged(object sender, bool isDarkMode)
        {
            // Apply theme defaults (overrides any user customizations)
            ApplyThemeDefaults();

            // Update the graph editor background
            if (graphEditor != null)
            {
                graphEditor.BackColor = GraphEditorBackColor;
            }

            // Update color pickers to reflect the new theme colors
            ClrPcker_Background.SelectedColor = GraphEditorBackColor.ToWPFColor();
            ClrPcker_BoxFill.SelectedColor = BoxFillColor.ToWPFColor();
            ClrPcker_TitleBox.SelectedColor = TitleBoxColor.ToWPFColor();
            ClrPcker_CommentText.SelectedColor = CommentTextColor.ToWPFColor();
            ClrPcker_BoxText.SelectedColor = BoxTextColor.ToWPFColor();
            ClrPcker_Connection.SelectedColor = ConnectionColor.ToWPFColor();
            ClrPcker_VarLink.SelectedColor = VarLinkColor.ToWPFColor();

            // Refresh the view if there are objects loaded
            if (CurrentObjects.Any())
            {
                RefreshView();
            }
        }

        private void CreateCustomSequence(object obj)
        {
            var customInfo = customSequencesToolBox.SelectedItem as CustomAsset;
            if (customInfo == null || !File.Exists(customInfo.PackageFilePath) || SelectedSequence == null)
                return;

            using var p = MEPackageHandler.OpenMEPackage(customInfo.PackageFilePath);
            var sourceExp = p.FindExport(customInfo.InstancedFullPath);
            if (sourceExp == null)
            {
                MessageBox.Show(
                    $"Cannot find export '{customInfo.InstancedFullPath}' in package file '{customInfo.PackageFilePath}'.");
                return;
            }

            SequenceEditorExperimentsM.InstallSequencePrefab(sourceExp, SelectedSequence);
        }

        public SequenceEditorWPF(ExportEntry export) : this()
        {
            PackageQueuedForLoad = export.FileRef;
            ExportQueuedForFocusing = export;
        }

        public SequenceEditorWPF(IMEPackage package) : this()
        {
            PackageQueuedForLoad = package;
        }

        public FrameworkElement TakeContentForEmbedding()
        {
            if (Content is not FrameworkElement content)
            {
                return null;
            }

            isEmbedded = true;
            Content = null;
            content.DataContext = this;
            content.Loaded += EmbeddedContent_Loaded;
            content.Unloaded += EmbeddedContent_Unloaded;
            return content;
        }

        private void EmbeddedContent_Loaded(object sender, RoutedEventArgs e)
        {
            isEmbeddedContentLoaded = true;
            if (ExportQueuedForFocusing is ExportEntry export)
            {
                ExportQueuedForFocusing = null;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => GoToExport(export)));
            }
        }

        private void EmbeddedContent_Unloaded(object sender, RoutedEventArgs e)
        {
            isEmbeddedContentLoaded = false;
        }

        public void LoadEmbeddedPackage(IMEPackage package, ExportEntry exportToFocus = null)
        {
            if (package == null)
            {
                return;
            }

            if (!ReferenceEquals(Pcc, package))
            {
                LoadFile(package.FilePath, () => RegisterPackage(package));
            }

            if (exportToFocus != null)
            {
                GoToExport(exportToFocus);
            }
            else
            {
                ExportQueuedForFocusing = null;
            }
        }

        public ICommand OpenCommand { get; set; }
        public ICommand SaveCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand SaveImageCommand { get; set; }
        public ICommand SaveViewCommand { get; set; }
        public ICommand NavigateSelectionBackCommand { get; set; }
        public ICommand NavigateSelectionForwardCommand { get; set; }
        public ICommand AutoLayoutCommand { get; set; }
        public ICommand UseSavedViewsCommand { get; set; }
        public ICommand ScanFolderForLoopsCommand { get; set; }
        public ICommand CheckSequenceSetsCommand { get; set; }
        public ICommand ConvertSeqActLogCommentCommand { get; set; }
        public ICommand GotoCommand { get; set; }
        public ICommand InstallKismetLoggerCommand { get; set; }
        public ICommand KismetLogCommand { get; set; }
        public ICommand KismetLogCurrentSequenceCommand { get; set; }
        public ICommand SearchCommand { get; set; }
        public ICommand ForceReloadPackageCommand { get; set; }
        public ICommand ResetFavoritesCommand { get; set; }
        public ICommand OpenOtherVersionCommand { get; set; }
        public ICommand ComparePackagesCommand { get; set; }
        public ICommand CompareToUnmoddedCommand { get; set; }
        public ICommand DesignerCreateInputCommand { get; set; }
        public ICommand DesignerCreateOutputCommand { get; set; }
        public ICommand DesignerCreateExternCommand { get; set; }
        public ICommand OpenHighestMountedCommand { get; set; }
        public ICommand OpenHighestMountedLinkedFileCommand { get; set; }

        private void LoadCommands()
        {
            ForceReloadPackageCommand = new GenericCommand(ForceReloadPackageWithoutSharing, CanForceReload);
            OpenCommand = new GenericCommand(OpenPackage);
            SaveCommand = new GenericCommand(SavePackage, PackageIsLoaded);
            SaveAsCommand = new GenericCommand(SavePackageAs, PackageIsLoaded);
            SaveImageCommand = new GenericCommand(SaveImage, () => CurrentObjects.Any);
            SaveViewCommand = new GenericCommand(() => saveView(), () => CurrentObjects.Any);
            NavigateSelectionBackCommand = new GenericCommand(NavigateSelectionBack, CanNavigateSelectionBack);
            NavigateSelectionForwardCommand = new GenericCommand(NavigateSelectionForward, CanNavigateSelectionForward);
            AutoLayoutCommand = new GenericCommand(() => AutoLayout(), () => CurrentObjects.Any);
            GotoCommand = new GenericCommand(GoTo, PackageIsLoaded);
            InstallKismetLoggerCommand = new GenericCommand(InstallKismetLogger, CanInstallKismetLogger);
            KismetLogCommand = new RelayCommand(OpenKismetLogParser, CanOpenKismetLog);
            ScanFolderForLoopsCommand = new GenericCommand(ScanFolderPackagesForTightLoops);
            CheckSequenceSetsCommand = new GenericCommand(() => SequenceEditorExperimentsM.CheckSequenceSets(this),
                () => CurrentObjects.Any);
            ConvertSeqActLogCommentCommand = new GenericCommand(
                () => SequenceEditorExperimentsM.ConvertSeqAct_Log_objComments(Pcc), () => SequenceExports.Any);
            SearchCommand = new GenericCommand(SearchDialogue, () => CurrentObjects.Any);
            UseSavedViewsCommand = new GenericCommand(ToggleSavedViews,
                () => Pcc != null && (Pcc is { Game: MEGame.ME1 } || Pcc.Game.IsLEGame()));
            ResetFavoritesCommand = new GenericCommand(ResetFavorites, () => Pcc != null);
            OpenOtherVersionCommand = new GenericCommand(OpenOtherVersion, () => Pcc != null && Pcc.Game.IsMEGame());
            CompareToUnmoddedCommand =
                new GenericCommand(() => SharedPackageTools.ComparePackageToUnmodded(this, entryDoubleClick),
                    () => SharedPackageTools.CanCompareToUnmodded(this));
            ComparePackagesCommand =
                new GenericCommand(() => SharedPackageTools.ComparePackageToAnother(this, entryDoubleClick),
                    PackageIsLoaded);
            OpenHighestMountedCommand = new GenericCommand(OpenHighestMountedVersion, IsLoadedPackageME);
            OpenHighestMountedLinkedFileCommand = new GenericCommand(OpenHighestMountedLinkedFile, IsLoadedPackageME);

            DesignerCreateExternCommand = new GenericCommand(CreateExtern, () => SelectedSequence != null);
            DesignerCreateInputCommand = new GenericCommand(CreateInput, () => SelectedSequence != null);
            DesignerCreateOutputCommand = new GenericCommand(CreateOutput, () => SelectedSequence != null);
        }

        private int GetKismetLoggerASIId(MEGame game)
        {
            return game switch
            {
                MEGame.ME2 => ASIModIDs.ME2_KISMET_LOGGER,
                MEGame.ME3 => ASIModIDs.ME3_KISMET_LOGGER,
                MEGame.LE1 => ASIModIDs.LE1_KISMET_LOGGER,
                MEGame.LE2 => ASIModIDs.LE2_KISMET_LOGGER,
                MEGame.LE3 => ASIModIDs.LE3_KISMET_LOGGER,
                _ => 0
            };
        }

        private void InstallKismetLogger()
        {
            if (ModManagerIntegration.GetModManagerBuildNumber() >= 126)
            {
                int modId = GetKismetLoggerASIId(Pcc.Game);
                if (modId != 0)
                {
                    ModManagerIntegration.RequestASIInstallation(Pcc.Game, modId);
                }
            }
        }

        private void LoadRememberedCustomSequenceObjectSources()
        {
            CustomSequenceObjectSourceFiles.ClearEx();

            if (!File.Exists(CustomSequenceObjectSourcesPath))
            {
                return;
            }

            List<string> sourceFiles;
            try
            {
                sourceFiles = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(CustomSequenceObjectSourcesPath)) ?? [];
            }
            catch when (!App.IsDebug)
            {
                return;
            }

            bool saveList = false;
            foreach (string filePath in sourceFiles
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.InvariantCultureIgnoreCase))
            {
                if (!File.Exists(filePath))
                {
                    saveList = true;
                    continue;
                }

                CustomSequenceObjectSourceFiles.Add(filePath);

                try
                {
                    LoadCustomSequenceObjectSource(filePath);
                }
                catch when (!App.IsDebug)
                {
                    // Keep the entry in the list so the user can see which source failed to load.
                }
            }

            if (saveList)
            {
                SaveRememberedCustomSequenceObjectSources();
            }
        }

        private void SaveRememberedCustomSequenceObjectSources()
        {
            Directory.CreateDirectory(SequenceEditorDataFolder);
            File.WriteAllText(CustomSequenceObjectSourcesPath,
                JsonConvert.SerializeObject(CustomSequenceObjectSourceFiles.ToList(), Formatting.Indented));
        }

        private bool LoadCustomSequenceObjectSource(string filePath)
        {
            using var package = MEPackageHandler.OpenMEPackage(filePath, forceLoadFromDisk: true);
            return SequenceEditorExperimentsM.LoadCustomClassesFromPackage(package);
        }

        public void LoadAndRememberCustomSequenceObjectSource(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                bool reload = LoadCustomSequenceObjectSource(filePath);
                bool isNewSource = CustomSequenceObjectSourceFiles.All(existingPath =>
                    !string.Equals(existingPath, filePath, StringComparison.InvariantCultureIgnoreCase));

                if (isNewSource)
                {
                    CustomSequenceObjectSourceFiles.Add(filePath);
                    SaveRememberedCustomSequenceObjectSources();
                }

                if (reload && Pcc != null)
                {
                    RefreshToolboxItems();
                }

                StatusText = isNewSource
                    ? $"Loaded custom sequence objects from {Path.GetFileName(filePath)} and saved it for future sessions."
                    : $"Loaded custom sequence objects from {Path.GetFileName(filePath)}.";
            }
            catch (Exception ex) when (!App.IsDebug)
            {
                MessageBox.Show(this, $"Unable to load custom sequence objects from file:\n{ex.Message}",
                    "Sequence Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CanInstallKismetLogger()
        {
            if (Pcc == null || Pcc.Game == MEGame.ME1)
            {
                return false;
            }

            // Detecting OT ASIs is difficult due to versioning info not being available.
            if (Pcc.Game.IsOTGame())
            {
                return false;
            }

            var kismetLoggerAsiId = GetKismetLoggerASIId(Pcc.Game);

            // We use enumerator so once we find the one we care about we don't do anything
            // with the rest.
            foreach(var asi in ASIModIDs.GetInstalledASIModIds(MEDirectories.GetExecutableFolderPath(Pcc.Game)))
            {
                if (asi.id == kismetLoggerAsiId)
                {
                    // Already installed.
                    return false;
                }
            }

            // Not installed
            return true;
        }

        private void CreateOutput()
        {
            var outputLabel = PromptDialog.Prompt(this, "Enter an output label for this sequence.", "Enter label",
                "Out", true);
            if (string.IsNullOrWhiteSpace(outputLabel))
                return;

            // Create an add activation to sequence
            var finished = SequenceObjectCreator.CreateSequenceObject(Pcc, "SeqAct_FinishSequence");
            finished.WriteProperty(new StrProperty(outputLabel, "OutputLabel"));
            finished.idxLink = SelectedSequence.UIndex;
            // Reindex if necessary
            var expCount = Pcc.Exports.Count(x => x.InstancedFullPath == finished.InstancedFullPath);
            if (expCount > 1)
            {
                // update the index
                finished.ObjectName = Pcc.GetNextIndexedName(finished.ObjectName.Name);
            }

            KismetHelper.AddObjectToSequence(finished, SelectedSequence);

            // Add output link to sequence
            var outputLinks = SelectedSequence.GetProperty<ArrayProperty<StructProperty>>("OutputLinks");
            if (outputLinks == null)
            {
                outputLinks = new ArrayProperty<StructProperty>("OutputLinks");
            }

            // Add struct
            PropertyInfo p = GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "OutputLinks", "Sequence");
            if (p == null)
            {
                Debugger.Break();
            }

            if (p != null)
            {
                string typeName = p.Reference;
                PropertyCollection props = GlobalUnrealObjectInfo.getDefaultStructValue(Pcc.Game, typeName, true, Pcc);
                props.AddOrReplaceProp(new NameProperty(finished.ObjectName, "LinkAction"));
                props.AddOrReplaceProp(new StrProperty(outputLabel, "LinkDesc"));
                props.AddOrReplaceProp(new ObjectProperty(finished, "LinkedOp"));
                outputLinks.Add(new StructProperty(typeName, props, isImmutable: false));
            }

            SelectedSequence.WriteProperty(outputLinks);
        }

        private void CreateInput()
        {
            var inputLabel = PromptDialog.Prompt(this, "Enter an input label for this activation.", "Enter label", "In",
                true);
            if (string.IsNullOrWhiteSpace(inputLabel))
                return;

            // Create an add activation to sequence
            var activation = SequenceObjectCreator.CreateSequenceObject(Pcc, "SeqEvent_SequenceActivated");
            activation.idxLink = SelectedSequence.UIndex;
            activation.WriteProperty(new StrProperty(inputLabel, "InputLabel"));

            // Reindex if necessary
            var expCount = Pcc.Exports.Count(x => x.InstancedFullPath == activation.InstancedFullPath);
            if (expCount > 1)
            {
                // update the index
                activation.ObjectName = Pcc.GetNextIndexedName(activation.ObjectName.Name);
            }

            KismetHelper.AddObjectToSequence(activation, SelectedSequence);

            // Add input link to sequence
            var inputLinks = SelectedSequence.GetProperty<ArrayProperty<StructProperty>>("InputLinks");
            if (inputLinks == null)
            {
                inputLinks = new ArrayProperty<StructProperty>("InputLinks");
            }

            // Add struct
            PropertyInfo p = GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "InputLinks", "Sequence");
            if (p == null)
            {
                Debugger.Break();
            }

            if (p != null)
            {
                string typeName = p.Reference;
                PropertyCollection props = GlobalUnrealObjectInfo.getDefaultStructValue(Pcc.Game, typeName, true, Pcc);
                props.AddOrReplaceProp(new NameProperty(activation.ObjectName, "LinkAction"));
                props.AddOrReplaceProp(new StrProperty(inputLabel, "LinkDesc"));
                props.AddOrReplaceProp(new ObjectProperty(activation, "LinkedOp"));
                inputLinks.Add(new StructProperty(typeName, props, isImmutable: false));
            }

            SelectedSequence.WriteProperty(inputLinks);
        }

        private void CreateExtern()
        {
            var externName = PromptDialog.Prompt(this, "Enter an variable label for this external variable.",
                "Enter label", "", true);
            if (string.IsNullOrWhiteSpace(externName))
                return;

            var classOptions = GlobalUnrealObjectInfo.GetClasses(Pcc.Game).Values
                .Where(x => x.IsA("SequenceVariable", Pcc.Game)).Select(x => x.ClassName).OrderBy(x => x).ToList();
            var externDataType = InputComboBoxDialog.GetValue(this, "Select datatype for this external variable.",
                "Select datatype",
                classOptions);

            if (string.IsNullOrWhiteSpace(externDataType))
            {
                return;
            }

            // Create a new extern
            var externalVar = SequenceObjectCreator.CreateSequenceObject(Pcc, "SeqVar_External");
            externalVar.idxLink = SelectedSequence.UIndex;

            var expectedDataTypeClass =
                EntryImporter.EnsureClassIsInFile(Pcc, externDataType, new RelinkerOptionsPackage());
            externalVar.WriteProperty(new StrProperty(externName, "VariableLabel"));
            externalVar.WriteProperty(new ObjectProperty(expectedDataTypeClass, "ExpectedType"));
            // Reindex if necessary
            var expCount = Pcc.Exports.Count(x => x.InstancedFullPath == externalVar.InstancedFullPath);
            if (expCount > 1)
            {
                // update the index
                externalVar.ObjectName = Pcc.GetNextIndexedName(externalVar.ObjectName.Name);
            }

            KismetHelper.AddObjectToSequence(externalVar, SelectedSequence);

            // Add input link to sequence
            var variableLinks = SelectedSequence.GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
            if (variableLinks == null)
            {
                variableLinks = new ArrayProperty<StructProperty>("VariableLinks");
            }

            // Add struct to VariableLinks
            PropertyInfo p = GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "VariableLinks", "Sequence");
            if (p == null)
            {
                Debugger.Break();
            }

            if (p != null)
            {
                string typeName = p.Reference;
                PropertyCollection props = GlobalUnrealObjectInfo.getDefaultStructValue(Pcc.Game, typeName, true, Pcc);
                props.AddOrReplaceProp(new NameProperty(externalVar.ObjectName, "LinkVar"));
                props.AddOrReplaceProp(new StrProperty(externName, "LinkDesc"));
                props.AddOrReplaceProp(new ObjectProperty(expectedDataTypeClass, "ExpectedType"));
                variableLinks.Add(new StructProperty(typeName, props, isImmutable: false));
            }

            SelectedSequence.WriteProperty(variableLinks);
        }

        private void entryDoubleClick(EntryStringPair clickedItem)
        {
            if (clickedItem?.Entry != null && clickedItem.Entry.UIndex != 0)
            {
                GoToExport(clickedItem.Entry.UIndex);
            }
        }

        private void ToggleSavedViews()
        {
            UseSavedViews = !UseSavedViews;
        }

        private bool CanNavigateSelectionBack() => Pcc != null && selectionHistoryIndex > 0;

        private bool CanNavigateSelectionForward() =>
            Pcc != null && selectionHistoryIndex >= 0 && selectionHistoryIndex < selectionHistory.Count - 1;

        private void NavigateSelectionBack()
        {
            NavigateSelectionHistory(-1);
        }

        private void NavigateSelectionForward()
        {
            NavigateSelectionHistory(1);
        }

        private void NavigateSelectionHistory(int step)
        {
            int targetIndex = selectionHistoryIndex + step;
            if (targetIndex < 0 || targetIndex >= selectionHistory.Count)
            {
                return;
            }

            var historyEntry = selectionHistory[targetIndex];
            if (Pcc == null
                || !string.Equals(historyEntry.FilePath, Pcc.FilePath, StringComparison.InvariantCultureIgnoreCase)
                || !Pcc.TryGetUExport(historyEntry.ObjectUIndex, out var export))
            {
                return;
            }

            selectionHistoryIndex = targetIndex;
            UpdateSelectionHistoryNavigationState();

            suppressSelectionHistory = true;
            try
            {
                GoToExport(export);
            }
            finally
            {
                suppressSelectionHistory = false;
            }
        }

        private void RecordSelectedObjectHistory(SObj selectedObject)
        {
            if (suppressSelectionHistory || selectedObject?.Export == null || Pcc == null)
            {
                return;
            }

            var historyEntry = new SelectionHistoryEntry(Pcc.FilePath, selectedObject.Export.UIndex);
            if (selectionHistoryIndex >= 0 && selectionHistory[selectionHistoryIndex] == historyEntry)
            {
                return;
            }

            if (selectionHistoryIndex < selectionHistory.Count - 1)
            {
                selectionHistory.RemoveRange(selectionHistoryIndex + 1, selectionHistory.Count - selectionHistoryIndex - 1);
            }

            selectionHistory.Add(historyEntry);
            selectionHistoryIndex = selectionHistory.Count - 1;
            UpdateSelectionHistoryNavigationState();
        }

        private void ClearSelectionHistory()
        {
            selectionHistory.Clear();
            selectionHistoryIndex = -1;
            UpdateSelectionHistoryNavigationState();
        }

        private void UpdateSelectionHistoryNavigationState()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        private bool CanForceReload() => App.IsDebug && PackageIsLoaded();

        private string searchtext = "";

        private void SearchDialogue()
        {
            const string input = "Enter text to search comments for";
            searchtext = PromptDialog.Prompt(this, input, "Search Comments", searchtext, true);

            if (!string.IsNullOrEmpty(searchtext))
            {
                SObj selectedObj = SelectedObjects.FirstOrDefault();
                var tgt = CurrentObjects.AfterThenBefore(selectedObj).FirstOrDefault(d =>
                    d.Comment.Contains(searchtext, StringComparison.InvariantCultureIgnoreCase));
                if (tgt != null)
                {
                    GoToExport(tgt.Export);
                }
                else
                {
                    MessageBox.Show($"No comment with \"{searchtext}\" found");
                }
            }
        }

        private void ScanFolderPackagesForTightLoops()
        {
            //This method ignores gates because they always link to themselves. Well, mostly.
            var dlg = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                EnsurePathExists = true,
                Title = "Select folder containing package files"
            };
            //SirC is going to love this level of indention
            //lol just kidding
            //sorry in advance
            //-Mgamerz
            if (DirectoryMemory.ShowDialog(dlg, this) == CommonFileDialogResult.Ok)
            {
                var packageFolderPath = dlg.FileName;
                var packageFiles =
                    Directory.EnumerateFiles(packageFolderPath, "*.pcc",
                        SearchOption.TopDirectoryOnly); //pcc only for now. not sure upk/u/sfm is worth it, maybe.
                List<string> tightLoops = new List<string>();
                foreach (var file in packageFiles)
                {
                    Debug.WriteLine("Opening package " + file);
                    using var p = MEPackageHandler.OpenMEPackage(file);
                    //find sequence objects
                    var sequences = p.Exports.Where(x => !x.IsDefaultObject && x.ClassName == "Sequence");
                    foreach (var sequence in sequences)
                    {
                        //get list of items in the sequence
                        var seqObjectsList = sequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects");
                        if (seqObjectsList != null)
                        {
                            foreach (var seqObjectRef in seqObjectsList)
                            {
                                var seqObj = p.GetUExport(seqObjectRef.Value);
                                if (seqObj.ClassName is "SeqAct_Gate") continue;
                                ; //skip gates
                                var outputLinks = seqObj.GetProperty<ArrayProperty<StructProperty>>("OutputLinks");
                                if (outputLinks != null)
                                {
                                    foreach (var outlink in outputLinks)
                                    {
                                        var links = outlink.GetProp<ArrayProperty<StructProperty>>("Links");
                                        if (links != null)
                                        {
                                            foreach (var link in links)
                                            {
                                                var linkedOp = link.GetProp<ObjectProperty>("LinkedOp");
                                                if (linkedOp != null)
                                                {
                                                    //this is what we are looking for. See if reference to self
                                                    if (linkedOp.Value == seqObj.UIndex)
                                                    {
                                                        //!! Self reference
                                                        tightLoops.Add(
                                                            $"Tight loop in {Path.GetFileName(file)}, export {seqObjectRef.Value} {seqObj.InstancedFullPath}");
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (tightLoops.Any())
                {
                    var ld = new ListDialog(tightLoops, "Tight sequence loops found",
                        "The following sequence objects link to themselves on an output and may cause significant harm to game performance.",
                        this);
                    ld.Show();
                }
                else
                {
                    MessageBox.Show("No tight loops found");
                }
            }
        }

        private async void CreateNewObject(ClassInfo info)
        {
            if (SelectedSequence == null)
            {
                return;
            }

            PointF creationPosition = ConsumePendingNewObjectPosition();

            int? randomSwitchLinkCount = null;
            if (info.ClassName == "SeqAct_RandomSwitch")
            {
                randomSwitchLinkCount = PromptForPositiveCount(
                    "How many links would you like to create?",
                    "Create random switch",
                    "2");
                if (!randomSwitchLinkCount.HasValue)
                {
                    return;
                }
            }

            IEntry classEntry;
            if (Pcc.Exports.Any(exp => exp.ObjectName == info.ClassName) ||
                Pcc.Imports.Any(imp => imp.ObjectName == info.ClassName) ||
                GlobalUnrealObjectInfo.GetClassOrStructInfo(Pcc.Game, info.ClassName) is { } classInfo &&
                EntryImporter.IsSafeToImportFrom(classInfo.pccPath, Pcc.Game, Pcc.FilePath))
            {
                var rop = new RelinkerOptionsPackage();
                classEntry = EntryImporter.EnsureClassIsInFile(Pcc, info.ClassName, rop);
                EntryImporterExtended.ShowRelinkResultsIfAny(rop);
            }
            else
            {
                SetBusy($"Adding {info.ClassName}");
                classEntry = await Task.Run(() =>
                {
                    var rop = new RelinkerOptionsPackage();
                    var result = EntryImporter.EnsureClassIsInFile(Pcc, info.ClassName, rop);
                    EntryImporterExtended.ShowRelinkResultsIfAny(rop);
                    return result;
                }).ConfigureAwait(true);
            }

            if (classEntry is null)
            {
                EndBusy();
                MessageBox.Show(this,
                    $"Could not import {info.ClassName}'s class definition! It may be defined in a DLC you don't have.");
                return;
            }

            using var packageCache = new PackageCache { AlwaysOpenFromDisk = false };
            packageCache.InsertIntoCache(Pcc);

            if (randomSwitchLinkCount.HasValue)
            {
                var randomSwitch = SequenceObjectCreator.CreateRandSwitch(SelectedSequence, randomSwitchLinkCount.Value,
                    packageCache);
                packageCache.RemoveFromCache(Pcc); // This prevents ref decrementing when cache is disposed
                customSaveData[randomSwitch.UIndex] = creationPosition;
                EndBusy();
                return;
            }

            var defaultProperties = SequenceObjectCreator.GetSequenceObjectDefaults(Pcc, info, packageCache);
            ApplyEditorCreationDefaults(info, defaultProperties);
            var newSeqObj = new ExportEntry(Pcc, SelectedSequence, Pcc.GetNextIndexedName(info.ClassName),
                properties: defaultProperties)
            {
                Class = classEntry,
            };
            packageCache.RemoveFromCache(Pcc); // This prevents ref decrementing when cache is disposed
            newSeqObj.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            Pcc.AddExport(newSeqObj);

            if (info.ClassName == "SFXSeqAct_SetAmbientPerformance" &&
                MessageBox.Show(this,
                    "Would you like to create a blank BioDynamicAnimSet and assign it to m_pDefaultPoseSet?",
                    "Create BioDynamicAnimSet",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                CreateBlankBioDynamicAnimSet(newSeqObj);
            }

            addObject(newSeqObj, preferredPosition: creationPosition);
            EndBusy();
        }

        private void ApplyEditorCreationDefaults(ClassInfo info, PropertyCollection properties)
        {
            if (Pcc == null || info == null || properties == null)
            {
                return;
            }

            if (info.ClassName == "SeqAct_Delay"
                && GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "Duration", info.ClassName, info) != null)
            {
                properties.AddOrReplaceProp(new FloatProperty(1, "Duration"));
            }

            if (info.ClassName == "SeqAct_ConsoleCommand"
                && GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "Commands", info.ClassName, info) != null)
            {
                properties.AddOrReplaceProp(new ArrayProperty<StrProperty>([new StrProperty("")], "Commands"));
            }

            if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "m_vSFXTeleportLocation", info.ClassName, info) != null)
            {
                properties.AddOrReplaceProp(CommonStructs.Vector3Prop(0, 0, 0, "m_vSFXTeleportLocation"));
            }

            if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "m_rSFXTeleportRotation", info.ClassName, info) != null)
            {
                properties.AddOrReplaceProp(CommonStructs.RotatorProp(0, 0, 0, "m_rSFXTeleportRotation"));
            }

            if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "m_bSFXTeleportDataIsValid", info.ClassName, info) != null)
            {
                properties.AddOrReplaceProp(new BoolProperty(true, "m_bSFXTeleportDataIsValid"));
            }

            if (info.ClassName == "SeqAct_SetLocation")
            {
                if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "bSetLocation", info.ClassName, info) != null)
                {
                    properties.AddOrReplaceProp(new BoolProperty(true, "bSetLocation"));
                }

                if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "bSetRotation", info.ClassName, info) != null)
                {
                    properties.AddOrReplaceProp(new BoolProperty(true, "bSetRotation"));
                }

                if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "LocationValue", info.ClassName, info) != null)
                {
                    properties.AddOrReplaceProp(CommonStructs.Vector3Prop(0, 0, 0, "LocationValue"));
                }

                if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "RotationValue", info.ClassName, info) != null)
                {
                    properties.AddOrReplaceProp(CommonStructs.RotatorProp(0, 0, 0, "RotationValue"));
                }
            }

            if (info.ClassName == "SFXSceneShopNodePlotCheck"
                && GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "VarType", info.ClassName, info) is { Reference: { } enumType })
            {
                properties.AddOrReplaceProp(new EnumProperty(new NameReference("PlotVar_State"), new NameReference(enumType), Pcc.Game, "VarType"));
            }

            if (info.ClassName == "SFXSceneShopNodePlotCheck"
                && GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "m_nIndex", info.ClassName, info) != null)
            {
                properties.AddOrReplaceProp(new IntProperty(0, "m_nIndex"));
            }

            if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "m_eBlackScreenAction", info.ClassName, info) is { Reference: { } blackScreenActionType })
            {
                properties.AddOrReplaceProp(new EnumProperty("BlackScreenAction_TurnBlackOn", blackScreenActionType,
                    Pcc.Game, "m_eBlackScreenAction"));
            }

            if (GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, "m_nmKismetBoolVarName", info.ClassName, info) != null)
            {
                properties.AddOrReplaceProp(new NameProperty("None", "m_nmKismetBoolVarName"));
            }
        }

        private void CreateBlankBioDynamicAnimSet(ExportEntry ambientPerformanceExport)
        {
            var rop = new RelinkerOptionsPackage();
            var bioDynamicAnimSetClass = EntryImporter.EnsureClassIsInFile(Pcc, "BioDynamicAnimSet", rop);
            EntryImporterExtended.ShowRelinkResultsIfAny(rop);
            if (bioDynamicAnimSetClass is null)
            {
                MessageBox.Show(this,
                    "Could not import BioDynamicAnimSet's class definition.",
                    "Create BioDynamicAnimSet",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var bioDynamicAnimSetExport = new ExportEntry(Pcc, ambientPerformanceExport,
                Pcc.GetNextIndexedName("BioDynamicAnimSet"))
            {
                Class = bioDynamicAnimSetClass,
            };
            bioDynamicAnimSetExport.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            Pcc.AddExport(bioDynamicAnimSetExport);
            bioDynamicAnimSetExport.WriteProperty(new ArrayProperty<ObjectProperty>("Sequences"));
            bioDynamicAnimSetExport.WriteBinary(BioDynamicAnimSet.Create());
            ambientPerformanceExport.WriteProperty(new ObjectProperty(bioDynamicAnimSetExport, "m_pDefaultPoseSet"));
        }

        private bool CanOpenKismetLog(object o)
        {
            switch (o)
            {
                case true:
                    return Pcc != null && File.Exists(KismetLogParser.KismetLogPath(Pcc.Game));
                case MEGame game:
                    return File.Exists(KismetLogParser.KismetLogPath(game));
                case "CurrentSequence":
                    return Pcc != null && File.Exists(KismetLogParser.KismetLogPath(Pcc.Game)) &&
                           SelectedSequence != null;
                default:
                    return false;
            }
        }

        private void OpenKismetLogParser(object obj)
        {
            if (CanOpenKismetLog(obj))
            {
                switch (obj)
                {
                    case true:
                        kismetLogParser.LoadLog(Pcc.Game, Pcc);
                        break;
                    case MEGame game:
                        kismetLogParser.LoadLog(game);
                        break;
                    case "CurrentSequence":
                        kismetLogParser.LoadLog(Pcc.Game, Pcc, SelectedSequence);
                        break;
                    default:
                        return;
                }

                kismetLogParser.Visibility = Visibility.Visible;
                kismetLogParserRow.Height = new GridLength(150);
                kismetLogParser.ExportFound = (filePath, uIndex) =>
                {
                    if (Pcc == null || Pcc.FilePath != filePath) LoadFile(filePath);
                    GoToExport(Pcc.GetUExport(uIndex), goIntoSequences: false);
                };
            }
            else
            {
                MessageBox.Show(this, "No Kismet Log!");
            }
        }

        private void GoTo()
        {
            if (EntrySelector.GetEntry<ExportEntry>(this, Pcc) is ExportEntry export)
            {
                GoToExport(export);
            }
        }

        #region Busy

        public override void SetBusy(string text = null)
        {
            Image graphImage = graphEditor.Camera.ToImage((int)graphEditor.Camera.GlobalFullWidth,
                (int)graphEditor.Camera.GlobalFullHeight, new SolidBrush(GraphEditorBackColor));
            graphImageSub.Source = graphImage.ToBitmapImage();
            graphImageSub.Width = graphGrid.ActualWidth;
            graphImageSub.Height = graphGrid.ActualHeight;
            if (toolBoxExpander.ActualHeight > 0 && toolBoxExpander.ActualWidth > 0)
            {
                // Do not draw if area == 0
                expanderImageSub.Source = toolBoxExpander.DrawToBitmapSource();
            }

            expanderImageSub.Width = toolBoxExpander.ActualWidth;
            expanderImageSub.Height = toolBoxExpander.ActualHeight;
            expanderImageSub.Visibility = Visibility.Visible;
            graphImageSub.Visibility = Visibility.Visible;
            BusyText = text;
            IsBusy = true;
        }

        public override void EndBusy()
        {
            IsBusy = false;
            graphImageSub.Visibility = expanderImageSub.Visibility = Visibility.Collapsed;
        }

        #endregion

        private string _statusText;

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private TreeViewEntry _selectedItem;

        public TreeViewEntry SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (AutoSaveView_MenuItem.IsChecked)
                {
                    saveView();
                }

                if (SetProperty(ref _selectedItem, value) && value != null)
                {
                    if (value.Entry is ExportEntry exportEntry)
                    {
                        value.IsSelected = true;
                        LoadSequence(exportEntry);
                    }
                    else
                    {
                        MessageBox.Show(this, "Can't select an imported sequence");
                    }
                }
            }
        }

        private async void SavePackageAs()
        {
            string extension = Path.GetExtension(Pcc.FilePath);
            var d = new SaveFileDialog { Filter = $"*{extension}|*{extension}" };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                await Pcc.SaveAsync(d.FileName);
                MessageBox.Show(this, "Done.");
            }
        }

        private async void SavePackage()
        {
            await Pcc.SaveAsync();
        }

        private void OpenPackage()
        {
            var d = AppDirectories.GetOpenPackageDialog();
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                try
                {
                    LoadFile(d.FileName);
                }
                catch (Exception ex) when (!App.IsDebug)
                {
                    MessageBox.Show(this, "Unable to open file:\n" + ex.Message);
                }
            }
        }

        private bool PackageIsLoaded()
        {
            return Pcc != null;
        }

        private void preloadPackage(string filePath, long packageSize)
        {
            try
            {
                ClearSelectionHistory();
                SelectedSequence = null;
                CurrentObjects.ClearEx();
                SequenceExports.ClearEx();
                SelectedObjects.ClearEx();
            }
            catch (Exception ex) when (!App.IsDebug)
            {
                MessageBox.Show(this, "Package Pre-Load Error:\n" + ex.Message);
                Title = "Sequence Editor";
                CurrentFile = null;
                UnLoadMEPackage();
            }
        }

        public void postloadPackage(string filePath)
        {
            try
            {
                forceAutoLayoutForCurrentPackage = forceAutoLayoutOnInitialPackageLoad;
                autoLaidOutSequencesForCurrentPackage.Clear();
                forceAutoLayoutOnInitialPackageLoad = false;

                LoadSequences();
                if (TreeViewRootNodes.IsEmpty())
                {
                    UnLoadMEPackage();
                    MessageBox.Show(this, "This file does not contain any sequences!");
                    StatusText = "Select package file to load";
                    return;
                }

                graphEditor.nodeLayer.RemoveAllChildren();
                graphEditor.edgeLayer.RemoveAllChildren();

                Title = $"Sequence Editor - {filePath}";
                StatusText = GetStatusBarText();

                RefreshToolboxItems(true);
            }
            catch (Exception ex) when (!App.IsDebug)
            {
                MessageBox.Show(this, "Package Post-Load Error:\n" + ex.Message);
                Title = "Sequence Editor";
                CurrentFile = null;
                UnLoadMEPackage();
            }
        }

        private void OpenHighestMountedVersion()
        {
            if (MEDirectories.GetBioGamePath(Pcc.Game) is null)
            {
                MessageBox.Show($"No {Pcc.Game} installation detected!");
                return;
            }

            string fileName = Path.GetFileName(Pcc.FilePath);
            if (!MELoadedFiles.TryGetHighestMountedFile(Pcc.Game, fileName, out string filePath))
            {
                MessageBox.Show($"No file named '{fileName}' was found in the {Pcc.Game} installation.");
            }
            else if (Path.GetFullPath(filePath) == Path.GetFullPath(Pcc.FilePath))
            {
                MessageBox.Show($"This is the highest mounted version of {fileName} in your {Pcc.Game} installation.");
            }
            else
            {
                var entry = SelectedItem?.Entry ?? SelectedSequence;
                var pe = new SequenceEditorWPF();
                pe.LoadFileAndGoTo(filePath, goToEntry: entry?.InstancedFullPath);
                pe.Show();
            }
        }

        private void OpenHighestMountedLinkedFile()
        {
            if (Pcc is null)
            {
                return;
            }

            if (MEDirectories.GetBioGamePath(Pcc.Game) is null)
            {
                MessageBox.Show($"No {Pcc.Game} installation detected!");
                return;
            }

            string currentFileName = Path.GetFileName(Pcc.FilePath);
            MELocalization currentLocalization = currentFileName.GetUnrealLocalization();

            string counterpartFilePath;
            string counterpartDescription;

            if (currentLocalization == MELocalization.None)
            {
                string locIntFileName = currentFileName.SetUnrealLocalization(Pcc.Game, MELocalization.INT, includeLOC: true);
                MELoadedFiles.TryGetHighestMountedFile(Pcc.Game, locIntFileName, out counterpartFilePath);
                counterpartDescription = "linked LOC_INT file";
            }
            else
            {
                string baseFileName = currentFileName.StripUnrealLocalization();
                MELoadedFiles.TryGetHighestMountedFile(Pcc.Game, baseFileName, out counterpartFilePath);
                counterpartDescription = "linked base file";
            }

            if (string.IsNullOrEmpty(counterpartFilePath))
            {
                MessageBox.Show($"No {counterpartDescription} was found for '{currentFileName}' in the {Pcc.Game} installation.");
                return;
            }

            if (Path.GetFullPath(counterpartFilePath) == Path.GetFullPath(Pcc.FilePath))
            {
                MessageBox.Show($"This file is already the resolved {counterpartDescription} for '{currentFileName}'.");
                return;
            }

            var entry = SelectedItem?.Entry ?? SelectedSequence;
            var seqEd = new SequenceEditorWPF();
            seqEd.LoadFileAndGoTo(counterpartFilePath, goToEntry: entry?.InstancedFullPath);
            seqEd.Show();
        }

        /// <summary>
        /// Reloads the toolbox data
        /// </summary>
        public void RefreshToolboxItems(bool includeCustomSequences = false)
        {
            if (Pcc != null)
            {
                favoritesToolBox.Classes.ClearEx();
                favoritesToolBox.Classes.AddRange(GetSavedFavorites());
                eventsToolBox.Classes.ClearEx();
                eventsToolBox.Classes.AddRange(SequenceObjectCreator.GetSequenceEvents(Pcc.Game)
                    .OrderBy(info => info.ClassName));
                actionsToolBox.Classes.ClearEx();
                actionsToolBox.Classes.AddRange(SequenceObjectCreator.GetSequenceActions(Pcc.Game)
                    .OrderBy(info => info.ClassName));
                conditionsToolBox.Classes.ClearEx();
                conditionsToolBox.Classes.AddRange(SequenceObjectCreator.GetSequenceConditions(Pcc.Game)
                    .OrderBy(info => info.ClassName));
                variablesToolBox.Classes.ClearEx();
                variablesToolBox.Classes.AddRange(SequenceObjectCreator.GetSequenceVariables(Pcc.Game)
                    .OrderBy(info => info.ClassName));
                sceneShopToolBox.Classes.ClearEx();
                sceneShopToolBox.Classes.AddRange(SequenceObjectCreator.GetSFXSceneShopNodes(Pcc.Game)
                    .OrderBy(info => info.ClassName));

                if (includeCustomSequences)
                {
                    customSequencesToolBox.Items.ClearEx();
                    customSequencesToolBox.Items.AddRange(CustomAssets.CustomSequences[Pcc.Game]);
                }

                SyncFloatingToolboxItems(includeCustomSequences);
            }
        }

        private IEnumerable<ClassInfo> GetSavedFavorites()
        {
            if (Pcc != null)
            {
                var setting = Settings.Get_SequenceEditor_Favorites(Pcc.Game);
                var classes = setting.Split(";");
                return classes.Select(className => GlobalUnrealObjectInfo.GetClassOrStructInfo(Pcc.Game, className))
                    .NonNull().OrderBy(info => info.ClassName);
            }

            return Array.Empty<ClassInfo>();
        }

        private void SaveFavorites()
        {
            if (Pcc != null)
            {
                var classes = favoritesToolBox.Classes.Select(cl => cl.ClassName);
                var favorites = new StringBuilder();
                foreach (var cl in classes)
                {
                    favorites.Append(cl + ";");
                }

                if (favorites.Length > 0) favorites.Remove(favorites.Length - 1, 1);
                Settings.Set_SequenceEditor_Favorites(Pcc.Game, favorites.ToString());
            }
        }

        private void SetFavorite(ClassInfo classInfo)
        {
            if (!favoritesToolBox.Classes.Contains(classInfo))
            {
                favoritesToolBox.Classes.Add(classInfo);
                favoritesToolBox.Classes.Sort(cl => cl.ClassName);
                SaveFavorites();
                SyncFloatingToolboxItems();
            }
        }

        private void RemoveFavorite(ClassInfo classInfo)
        {
            favoritesToolBox.Classes.Remove(classInfo);
            SaveFavorites();
            SyncFloatingToolboxItems();
        }

        private void ResetFavorites()
        {
            favoritesToolBox.Classes.Clear();
            favoritesToolBox.Classes.AddRange(SequenceObjectCreator.GetCommonObjects(Pcc.Game)
                .OrderBy(info => info.ClassName));
            SaveFavorites();
            SyncFloatingToolboxItems();
        }

        public void LoadFileFromStream(Stream stream, string associatedFilePath, int goToIndex = 0)
        {
            try
            {
                var currentFile = Path.GetFileName(associatedFilePath);
                preloadPackage(currentFile, stream.Length);
                LoadMEPackage(stream, associatedFilePath);
                CurrentFile = currentFile;
                postloadPackage(associatedFilePath);
                if (goToIndex != 0 && Pcc.TryGetUExport(goToIndex, out var exp))
                {
                    GoToExport(exp);
                }
            }
            catch (Exception ex) when (!App.IsDebug)
            {
                MessageBox.Show(this, "Package Stream-Load Error:\n" + ex.Message);
                Title = "Sequence Editor";
                CurrentFile = null;
                UnLoadMEPackage();
            }
        }

        public void LoadFileAndGoTo(string fileName, int uIndex = 0, string goToEntry = null,
            Action loadPackageDelegate = null)
        {
            LoadFile(fileName, loadPackageDelegate);
            if (uIndex > 0)
            {
                GoToExport(uIndex);
            }
            else if (goToEntry != null)
            {
                var exp = Pcc.FindExport(goToEntry);
                if (exp != null)
                {
                    GoToExport(exp);
                }
            }
        }

        /// <summary>
        /// Loads a package file into the editor for use
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="loadPackageDelegate">Delegate that can be used to set the Pcc object on this object instead of the default from-disk loader</param>
        public void LoadFile(string fileName, Action loadPackageDelegate = null)
        {
            try
            {
                preloadPackage(fileName, 0); // We don't show the size so don't bother
                if (loadPackageDelegate != null)
                {
                    // Used for loading packages from memory from another tool
                    // This is useful for dev where you have a window open for a package that no longer exists
                    // e.g. when building mods via c# and the folder is constantly being deleted
                    loadPackageDelegate.Invoke();
                }
                else
                {
                    // Used for loading package from disk (even in shared interop already).
                    LoadMEPackage(fileName);
                }

                CurrentFile = Path.GetFileName(fileName);

                // Streams don't work for recents
                RecentsController.AddRecent(fileName, false, Pcc?.Game);
                RecentsController.SaveRecentList(true);

                postloadPackage(fileName);

                var loadedPackage = Pcc;
                if (loadedPackage != null)
                {
                    Task.Run(() => ConversationExtended.WarmOwnerTagCacheForPackage(loadedPackage));
                }
            }
            catch (Exception ex) when (!App.IsDebug)
            {
                MessageBox.Show(this, "Error:\n" + ex.Message);
                Title = "Sequence Editor";
                CurrentFile = null;
                UnLoadMEPackage();
            }
        }

        private void LoadSequences()
        {
            ResetTreeView();
            var prefabs = new Dictionary<string, TreeViewEntry>();
            foreach (var export in Pcc.Exports)
            {
                switch (export.ClassName)
                {
                    case "Sequence" when !(export.HasParent && export.Parent.IsSequence()):
                        TreeViewRootNodes.Add(FindSequences(export, export.ObjectName != "Main_Sequence"));
                        SequenceExports.Add(export);
                        break;
                    case "Prefab":
                        try
                        {
                            prefabs.Add(export.ObjectName.Name, new TreeViewEntry(export, export.InstancedFullPath));
                        }
                        catch
                        {
                            // ignored
                        }

                        break;
                }
            }

            if (prefabs.Count > 0)
            {
                foreach (var export in Pcc.Exports)
                {
                    if (export.ClassName == "PrefabSequence" && export.Parent?.ClassName == "Prefab")
                    {
                        string parentName = Pcc.getObjectName(export.idxLink);
                        if (prefabs.ContainsKey(parentName))
                        {
                            prefabs[parentName].Sublinks.Add(FindSequences(export));
                        }
                    }
                }

                foreach (var item in prefabs.Values)
                {
                    if (item.Sublinks.Any())
                    {
                        TreeViewRootNodes.Add(item);
                    }
                }
            }

            // Find SFXSceneShopGameData exports and nest them under the sequence that contains them
            foreach (var export in Pcc.Exports)
            {
                if (export.IsA("SFXSceneShopGameData"))
                {
                    SequenceExports.Add(export);

                    // Walk up the parent chain to find the containing sequence's tree node
                    var entry = FindContainingTreeNode(export);
                    if (entry != null)
                    {
                        entry.Sublinks.Add(FindSequences(export));
                    }
                    else
                    {
                        // Fallback: add at root if no containing sequence found
                        TreeViewRootNodes.Add(FindSequences(export, true));
                    }
                }
            }
        }

        private TreeViewEntry FindContainingTreeNode(ExportEntry export)
        {
            // Walk up the export parent chain to find a sequence that's in the tree
            var current = export.Parent as ExportEntry;
            while (current != null)
            {
                // Look for this export in the existing tree
                var match = TreeViewRootNodes
                    .SelectMany(node => node.FlattenTree())
                    .FirstOrDefault(node => node.UIndex == current.UIndex);
                if (match != null)
                {
                    return match;
                }
                current = current.Parent as ExportEntry;
            }
            return null;
        }

        private void ResetTreeView()
        {
            foreach (TreeViewEntry tvi in TreeViewRootNodes.SelectMany(node => node.FlattenTree()))
            {
                tvi.Dispose();
            }

            TreeViewRootNodes.ClearEx();
        }

        private TreeViewEntry FindSequences(ExportEntry rootSeq, bool wantFullName = false)
        {
            string seqName = (wantFullName && !string.IsNullOrWhiteSpace(rootSeq.ParentFullPath))
                ? $"{rootSeq.ParentInstancedFullPath}."
                : "";
            if (rootSeq.GetProperty<StrProperty>("ObjName") is StrProperty objName)
            {
                seqName += objName;
            }
            else
            {
                seqName += rootSeq.ObjectName.Instanced;
            }

            var root = new TreeViewEntry(rootSeq, $"#{rootSeq.UIndex}: {seqName}")
            {
                IsExpanded = true
            };
            var pcc = rootSeq.FileRef;
            var seqObjs = rootSeq.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects");
            if (seqObjs != null)
            {
                foreach (ObjectProperty seqObj in seqObjs)
                {
                    if (!pcc.IsUExport(seqObj.Value)) continue;
                    ExportEntry exportEntry = pcc.GetUExport(seqObj.Value);
                    if (exportEntry.ClassName == "Sequence" || exportEntry.ClassName.StartsWith("PrefabSequence"))
                    {
                        TreeViewEntry t = FindSequences(exportEntry);
                        SequenceExports.Add(exportEntry);
                        root.Sublinks.Add(t);
                    }
                    else if (exportEntry.ClassName == "SequenceReference")
                    {
                        var propSequenceReference = exportEntry.GetProperty<ObjectProperty>("oSequenceReference");
                        if (propSequenceReference != null)
                        {
                            TreeViewEntry treeViewEntry = null;

                            if (pcc.TryGetUExport(propSequenceReference.Value, out var exportRef))
                            {
                                treeViewEntry = FindSequences(exportRef);
                                SequenceExports.Add(exportEntry);
                            }
                            else if (pcc.TryGetImport(propSequenceReference.Value, out var importRef))
                            {
                                treeViewEntry = new TreeViewEntry(importRef,
                                    $"#{importRef.UIndex}: {importRef.InstancedFullPath}");
                            }

                            if (treeViewEntry != null)
                            {
                                root.Sublinks.Add(treeViewEntry);
                            }
                        }
                    }
                }
            }

            return root;
        }

        private void LoadSequence(ExportEntry seqExport, bool fromFile = true)
        {
            if (seqExport == null)
            {
                return;
            }

            graphEditor.Enabled = false;
            graphEditor.UseWaitCursor = true;
            SelectedSequence = seqExport;
            SetupJSON(SelectedSequence);
            var selectedExports = SelectedObjects.Select(o => o.Export).ToList();
            bool forceAutoLayout = fromFile;
            if (fromFile)
            {
                Properties_InterpreterWPF.LoadExport(seqExport);
                if (!forceAutoLayout && UseSavedViews && File.Exists(JSONpath))
                {
                    SavedView = JsonConvert.DeserializeObject<SavedViewData>(File.ReadAllText(JSONpath));
                }
                else
                {
                    SavedView = new(new(), RectangleF.Empty);
                }

                customSaveData.Clear();
                selectedExports.Clear();
            }

            try
            {
                GenerateGraph(forceAutoLayout);
                if (selectedExports.Count == 1 &&
                    CurrentObjects.FirstOrDefault(obj => obj.Export == selectedExports[0]) is SObj selectedObj)
                {
                    panToSelection = false;
                    CurrentObjects_ListBox.SelectedItem = selectedObj;
                }

                if (fromFile)
                {
                    if (!forceAutoLayout && SavedView.ViewBounds != RectangleF.Empty)
                    {
                        graphEditor.Camera.ViewBounds = SavedView.ViewBounds;
                    }
                    else
                    {
                        RectangleF viewBounds =
                            (CurrentObjects.FirstOrDefault(obj => obj is SEvent) ?? CurrentObjects.FirstOrDefault())
                            ?.GlobalFullBounds ?? new RectangleF();
                        graphEditor.Camera.AnimateViewToCenterBounds(viewBounds, false, 0);
                    }
                }
            }
            catch (Exception e) when (!App.IsDebug)
            {
                MessageBox.Show(this, $"Error loading sequences from file:\n{e.Message}");
            }

            graphEditor.Enabled = true;
            graphEditor.UseWaitCursor = false;
        }

        private void SetupJSON(ExportEntry export)
        {
            string objectName =
                System.Text.RegularExpressions.Regex.Replace(export.ObjectName.Name, @"[<>:""/\\|?*]", "");
            string viewsPath = Pcc.Game switch
            {
                MEGame.LE1 => LE1ViewsPath,
                MEGame.LE2 => LE2ViewsPath,
                MEGame.LE3 => LE3ViewsPath,
                MEGame.ME1 => ME1ViewsPath,
                MEGame.ME2 => ME2ViewsPath,
                _ => ME3ViewsPath
            };

            JSONpath = Path.Combine(viewsPath, $"{CurrentFile}.v2#{export.UIndex - 1}{objectName}.JSON");
        }

        public void GetObjects(ExportEntry export)
        {
            CurrentObjects.ClearEx();
            var seqObjs = export.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects");

            // SFXSceneShopGameData stores children in m_aNodes instead of SequenceObjects
            if (seqObjs == null && export.IsA("SFXSceneShopGameData"))
            {
                seqObjs = export.GetProperty<ArrayProperty<ObjectProperty>>("m_aNodes");
            }

            if (seqObjs != null)
            {
                // Resolve imports
                //var convertedImports = new List<ExportEntry>();
                //var imports = seqObjs.Where(x => x.Value < 0).Select(x => x.ResolveToEntry(export.FileRef) as ImportEntry);

                //foreach (var import in imports)
                //{
                //    var resolved = EntryImporter.ResolveImport(import);
                //    if (resolved != null)
                //    {
                //        convertedImports.Add(resolved);
                //    }
                //}

                var nullCount = seqObjs.Count(x => x.Value == 0);

                var loadedExports = seqObjs.OrderBy(prop => prop.Value)
                    .Select(prop => Pcc.TryGetUExport(prop.Value, out ExportEntry sequenceObject) ? sequenceObject : null)
                    .Where(sequenceObject => sequenceObject != null)
                    .ToHashSet();

                CurrentObjects.AddRange(loadedExports.Select(LoadObject));
                //CurrentObjects.AddRange(convertedImports.Select(LoadObject));

                // Subtrack imports. But they should be shown still
                if (CurrentObjects.Count != (seqObjs.Count - nullCount))
                {
                    MessageBox.Show(this,
                        "Sequence contains invalid or duplicate exports! Correct this by editing the SequenceObject array in the Properties editor");
                }

                // For SFXSceneShopGameData, also load referenced objects (SFXSceneGroup etc.) as SVar nodes
                if (export.IsA("SFXSceneShopGameData"))
                {
                    LoadSFXSceneShopReferencedObjects(loadedExports);
                }
            }
        }

        private void LoadSFXSceneShopReferencedObjects(HashSet<ExportEntry> loadedExports)
        {
            var referencedExports = new HashSet<ExportEntry>();

            // Collect SFXSceneGroups referenced by m_pLinkedScene
            foreach (var exp in loadedExports)
            {
                var linkedScene = exp.GetProperty<ObjectProperty>("m_pLinkedScene");
                if (linkedScene is { Value: not 0 } && Pcc.IsUExport(linkedScene.Value))
                {
                    var sceneGroup = Pcc.GetUExport(linkedScene.Value);
                    if (!loadedExports.Contains(sceneGroup) && !referencedExports.Contains(sceneGroup))
                    {
                        referencedExports.Add(sceneGroup);
                    }
                }
            }

            // Also find SFXSceneGroup siblings of the SFXSceneShopGameData (children of same parent, e.g. InterpData)
            // This ensures they remain visible even when not referenced by any node
            if (SelectedSequence.Parent is ExportEntry parentExport)
            {
                foreach (var exp in Pcc.Exports)
                {
                    if (exp.idxLink == parentExport.UIndex && exp.IsA("SFXSceneGroup"))
                    {
                        if (!loadedExports.Contains(exp) && !referencedExports.Contains(exp))
                        {
                            referencedExports.Add(exp);
                        }
                    }
                }
            }

            foreach (var refExport in referencedExports)
            {
                CurrentObjects.Add(new SVar(refExport, graphEditor));
            }
        }

        public void GenerateGraph(bool forceAutoLayout = false)
        {
            graphEditor.nodeLayer.RemoveAllChildren();
            graphEditor.edgeLayer.RemoveAllChildren();
            StartPosEvents = 0;
            StartPosActions = 0;
            StartPosVars = 0;
            GetObjects(SelectedSequence);
            Layout();
            foreach (SObj o in CurrentObjects)
            {
                o.MouseDown += node_MouseDown;
                o.Click += node_Click;
                o.DoubleClick += node_DoubleClick;
            }

            if (forceAutoLayout || (SavedView.Positions.IsEmpty() && (Pcc.Game is MEGame.ME2 or MEGame.ME3)))
            {
                AutoLayout();
            }
        }

        public float StartPosEvents;
        public float StartPosActions;
        public float StartPosVars;

        public SObj LoadObject(ExportEntry export)
        {
            float x = float.NaN, y = float.NaN;
            foreach (var prop in export.GetProperties())
            {
                switch (prop)
                {
                    case IntProperty intProp when intProp.Name == "ObjPosX":
                        x = intProp.Value;
                        break;
                    case IntProperty intProp when intProp.Name == "ObjPosY":
                        y = intProp.Value;
                        break;
                }
            }

            SObj obj;
            if (export.IsA("SequenceEvent"))
            {
                obj = new SEvent(export, graphEditor);
            }
            else if (export.IsA("SequenceVariable"))
            {
                obj = new SVar(export, graphEditor);
            }
            else if (export.ClassName == "SequenceFrame" &&
                     (Pcc.Game == MEGame.ME1 || Pcc.Game == MEGame.UDK || Pcc.Game.IsLEGame()))
            {
                obj = new SFrame(export, graphEditor);
            }
            else //if (s.StartsWith("BioSeqAct_") || s.StartsWith("SeqAct_") || s.StartsWith("SFXSeqAct_") || s.StartsWith("SeqCond_") || pcc.getExport(index).ClassName == "Sequence" || pcc.getExport(index).ClassName == "SequenceReference")
            {
                obj = new SAction(export, graphEditor);
            }

            if (obj is SBox box)
            {
                box.AddLinkEntryRequested = PromptAndAddNamedLinkEntryFromGraph;
                box.EditLinkEntryRequested = PromptAndEditNamedLinkEntryFromGraph;
                box.RemoveLinkEntryRequested = PromptAndRemoveNamedLinkEntryFromGraph;
            }

            return obj;
        }

        private static bool warnedOfReload = false;

        /// <summary>
        /// Forcibly reloads the package from disk. The package loaded in this instance will no longer be shared.
        /// </summary>
        private void ForceReloadPackageWithoutSharing()
        {
            var fileOnDisk = Pcc.FilePath;
            if (fileOnDisk != null && File.Exists(fileOnDisk))
            {
                if (Pcc.IsModified)
                {
                    var warningResult = MessageBox.Show(this,
                        "The current package is modified. Reloading the package will cause you to lose all changes to this package.\n\nReload anyways?",
                        "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (warningResult != MessageBoxResult.Yes)
                        return; // Do not continue!
                }

                if (!warnedOfReload)
                {
                    var warningResult = MessageBox.Show(this,
                        "Forcibly reloading a package will drop it out of tool sharing - making changes to this package in other will not be reflected in this window, and changes to this window will not be reflected in other windows. THIS MEANS SAVING WILL OVERWRITE CHANGES FROM OTHER WINDOWS. Only continue if you know what you are doing.\n\nReload anyways?",
                        "Warning", MessageBoxButton.YesNo, MessageBoxImage.Error);
                    if (warningResult != MessageBoxResult.Yes)
                        return; // Do not continue!
                    warnedOfReload = true;
                }

                var selectedIndex = (CurrentObjects_ListBox.SelectedItem as SObj)?.Export.UIndex ?? 0;
                using var fStream = File.OpenRead(fileOnDisk);
                LoadFileFromStream(fStream, fileOnDisk, selectedIndex);
                Title += " (NOT SHARED WITH OTHER WINDOWS)";
            }
        }

        private void PromptAndRemoveNamedLinkEntryFromGraph(ExportEntry export, string propertyName, int linkIndex)
        {
            if (export == null)
            {
                return;
            }

            if (CurrentObjects.FirstOrDefault(obj => obj.Export == export) is SObj graphObject)
            {
                panToSelection = false;
                CurrentObjects_ListBox.SelectedItems.Clear();
                CurrentObjects_ListBox.SelectedItem = graphObject;
            }

            string linkKind = propertyName switch
            {
                "VariableLinks" => "variable",
                "InputLinks" => "input",
                "OutputLinks" => "output",
                _ => "link"
            };

            var contextMenu = new ContextMenu
            {
                Placement = PlacementMode.MousePoint,
                PlacementTarget = this
            };
            var removeMenuItem = new MenuItem
            {
                Header = $"Remove {linkKind} entry"
            };
            removeMenuItem.Click += (_, _) =>
            {
                RemoveNamedLinkEntry(export, propertyName, linkIndex);
            };
            contextMenu.Items.Add(removeMenuItem);
            contextMenu.IsOpen = true;
        }

        public void Layout()
        {
            var objsInNeedOfLayout = new HashSet<SObj>();
            if (CurrentObjects != null && CurrentObjects.Any())
            {
                foreach (SObj obj in CurrentObjects)
                {
                    graphEditor.addNode(obj);
                }

                List<SAction> actions = CurrentObjects.OfType<SAction>().ToList();
                List<SVar> vars = CurrentObjects.OfType<SVar>().ToList();
                List<SEvent> events = CurrentObjects.OfType<SEvent>().ToList();

                foreach (SObj obj in CurrentObjects)
                {
                    obj.CreateConnections(actions, vars, events);
                }

                foreach (SObj obj in CurrentObjects)
                {
                    if (SavedView.Positions.TryGetValue(obj.UIndex, out PointF savedInfo))
                    {
                        obj.Layout(savedInfo.X, savedInfo.Y);
                        continue;
                    }

                    if (Pcc.Game is MEGame.ME1 or MEGame.UDK || Pcc.Game.IsLEGame())
                    {
                        var props = obj.Export.GetProperties();
                        IntProperty xPos = props.GetProp<IntProperty>("ObjPosX");
                        IntProperty yPos = props.GetProp<IntProperty>("ObjPosY");
                        if (xPos is not null || yPos is not null)
                        {
                            obj.Layout(xPos?.Value ?? 0, yPos?.Value ?? 0);
                            continue;
                        }
                    }

                    objsInNeedOfLayout.Add(obj);
                    obj.Layout(0, 0);
                    //switch (obj)
                    //{
                    //    case SEvent:
                    //        obj.Layout(StartPosEvents, 0);
                    //        StartPosEvents += obj.Width + 20;
                    //        break;
                    //    case SAction:
                    //        obj.Layout(StartPosActions, 250);
                    //        StartPosActions += obj.Width + 20;
                    //        break;
                    //    case SVar:
                    //        obj.Layout(StartPosVars, 500);
                    //        StartPosVars += obj.Width + 20;
                    //        break;
                    //}
                }

                if (objsInNeedOfLayout.Any())
                {
                    AutoLayout(objsInNeedOfLayout);
                }
                else
                {
                    foreach (SeqEdEdge edge in graphEditor.edgeLayer)
                    {
                        SequenceGraphEditor.UpdateEdge(edge);
                    }
                }
            }
        }

        private void AutoLayout(ICollection<SObj> objsToLayout = null)
        {
            var visitedNodes = new HashSet<int>();

            if (objsToLayout is null)
            {
                objsToLayout = CurrentObjects;
            }
            else
            {
                visitedNodes.AddRange(CurrentObjects.Except(objsToLayout).Select(obj => obj.UIndex));
            }

            foreach (SObj obj in objsToLayout)
            {
                obj.SetOffset(0, 0); //remove existing positioning
            }

            const float HORIZONTAL_SPACING = 40;
            const float VERTICAL_SPACING = 20;
            const float VAR_SPACING = 10;
            var eventNodes = objsToLayout.OfType<SEvent>().ToList();
            SObj firstNode = eventNodes.FirstOrDefault();
            var varNodeLookup = objsToLayout.OfType<SVar>().ToDictionary(obj => obj.UIndex);
            var opNodeLookup = objsToLayout.OfType<SBox>().ToDictionary(obj => obj.UIndex);
            var rootTree = new List<SObj>();
            //SEvents are natural root nodes. ALmost everything will proceed from one of these
            foreach (SEvent eventNode in eventNodes)
            {
                LayoutTree(eventNode, 5 * VERTICAL_SPACING);
            }

            //Find SActions with no inputs. These will not have been reached from an SEvent
            var orphanRoots = objsToLayout.OfType<SAction>().Where(node => node.InputEdges.IsEmpty());
            foreach (SAction orphan in orphanRoots)
            {
                LayoutTree(orphan, VERTICAL_SPACING);
            }

            //It's possible that there are groups of otherwise unconnected SActions that form cycles.
            //Might be possible to make a better heuristic for choosing a root than sequence order, but this situation is so rare it's not worth the effort
            var cycleNodes = objsToLayout.OfType<SAction>().Where(node => !visitedNodes.Contains(node.UIndex));
            foreach (SAction cycleNode in cycleNodes)
            {
                LayoutTree(cycleNode, VERTICAL_SPACING);
            }

            //Lonely unconnected variables. Put them in a row below everything else
            var unusedVars = objsToLayout.OfType<SVar>().Where(obj => !visitedNodes.Contains(obj.UIndex));
            float varOffset = 0;
            float vertOffset = rootTree.BoundingRect().Bottom + VERTICAL_SPACING;
            foreach (SVar unusedVar in unusedVars)
            {
                unusedVar.OffsetBy(varOffset, vertOffset);
                varOffset += unusedVar.GlobalFullWidth + HORIZONTAL_SPACING;
            }

            if (firstNode != null) objsToLayout.OffsetBy(0, -firstNode.OffsetY);

            foreach (SeqEdEdge edge in graphEditor.edgeLayer)
                SequenceGraphEditor.UpdateEdge(edge);

            void LayoutTree(SBox sAction, float verticalSpacing)
            {
                firstNode ??= sAction;
                visitedNodes.Add(sAction.UIndex);
                var subTree = LayoutSubTree(sAction);
                float width = subTree.BoundingRect().Width + HORIZONTAL_SPACING;
                //ignore nodes that are further to the right than this subtree is wide. This allows tighter spacing
                float dy = rootTree.Where(node => node.GlobalFullBounds.Left < width).BoundingRect().Bottom;
                if (dy > 0) dy += verticalSpacing;
                subTree.OffsetBy(0, dy);
                rootTree.AddRange(subTree);
            }

            List<SObj> LayoutSubTree(SBox root)
            {
                //Task.WaitAll(Task.Delay(1500));
                var tree = new List<SObj>();
                var vars = new List<SVar>();
                foreach (var varLink in root.Varlinks)
                {
                    float dx = varLink.Node.GlobalFullBounds.X - SVar.RADIUS;
                    float dy = root.GlobalFullHeight + VAR_SPACING;
                    foreach (int uIndex in varLink.Links.Where(uIndex => !visitedNodes.Contains(uIndex)))
                    {
                        visitedNodes.Add(uIndex);
                        if (varNodeLookup.TryGetValue(uIndex, out SVar sVar))
                        {
                            sVar.OffsetBy(dx, dy);
                            dy += sVar.GlobalFullHeight + VAR_SPACING;
                            vars.Add(sVar);
                        }
                    }
                }

                var childTrees = new List<List<SObj>>();
                var children = root.Outlinks.SelectMany(link => link.Links)
                    .Where(uIndex => !visitedNodes.Contains(uIndex));
                foreach (int uIndex in children)
                {
                    visitedNodes.Add(uIndex);
                    if (opNodeLookup.TryGetValue(uIndex, out SBox node))
                    {
                        List<SObj> subTree = LayoutSubTree(node);
                        childTrees.Add(subTree);
                    }
                }

                if (childTrees.Any())
                {
                    float dx = root.GlobalFullWidth + (HORIZONTAL_SPACING * (1 + childTrees.Count * 0.4f));
                    foreach (List<SObj> subTree in childTrees)
                    {
                        float subTreeWidth = subTree.BoundingRect().Width + HORIZONTAL_SPACING + dx;
                        //ignore nodes that are further to the right than this subtree is wide. This allows tighter spacing
                        float dy = tree.Where(node => node.GlobalFullBounds.Left < subTreeWidth).BoundingRect().Bottom;
                        if (dy > 0) dy += VERTICAL_SPACING;
                        subTree.OffsetBy(dx, dy);
                        //TODO: fix this so it doesn't screw up some sequences. eg: BioD_ProEar_310BigFall.pcc
                        /*float treeWidth = tree.BoundingRect().Width + HORIZONTAL_SPACING;
                        //tighten spacing when this subtree is wider than existing tree.
                        dy -= subTree.Where(node => node.GlobalFullBounds.Left < treeWidth).BoundingRect().Top;
                        if (dy < 0) dy += VERTICAL_SPACING;
                        subTree.OffsetBy(0, dy);*/

                        tree.AddRange(subTree);
                    }

                    //center the root on its children
                    float centerOffset = tree.OfType<SBox>().BoundingRect().Height / 2 - root.GlobalFullHeight / 2;
                    root.OffsetBy(0, centerOffset);
                    vars.OffsetBy(0, centerOffset);
                }

                tree.AddRange(vars);
                tree.Add(root);
                return tree;
            }
        }

        public void RefreshView()
        {
            saveView(false);
            suppressInterpreterUnloadDepth++;
            try
            {
                LoadSequence(SelectedSequence, false);
            }
            finally
            {
                suppressInterpreterUnloadDepth--;
            }
        }

        private void backMouseDown_Handler(object sender, PInputEventArgs e)
        {
            if (!(e.PickedNode is PCamera) || SelectedSequence == null) return;

            if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                backgroundContextMenuGraphLocation = e.Position;
                var clickPoint = System.Windows.Forms.Control.MousePosition;
                floatingToolboxScreenLocation = ConvertScreenPixelsToDips(clickPoint);
                if (FindResource("backContextMenu") is ContextMenu contextMenu)
                {
                    contextMenu.Placement = PlacementMode.MousePoint;
                    contextMenu.IsOpen = true;
                }
            }
            else if (e.Shift)
            {
                //graphEditor.StartBoxSelection(e);
                //e.Handled = true;
            }
            else
            {
                CurrentObjects_ListBox.SelectedItems.Clear();
            }
        }

        private void back_MouseUp(object sender, PInputEventArgs e)
        {
            //var nodesToSelect = graphEditor.EndBoxSelection().OfType<SObj>();
            //foreach (SObj sObj in nodesToSelect)
            //{
            //    panToSelection = false;
            //    CurrentObjects_ListBox.SelectedItems.Add(sObj);
            //}
        }

        private void graphEditor_Click(object sender, EventArgs e)
        {
            graphEditor.Focus();
        }

        private void OpenFloatingToolbox_Click(object sender, RoutedEventArgs e)
        {
            OpenFloatingToolbox();
        }

        private void BackContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu contextMenu)
            {
                return;
            }

            contextMenu.Items.Clear();
            contextMenu.Items.Add(CreateBackContextMenuItem("Add Existing Object", AddObject_Clicked,
                "Add existing Sequence Object to this Sequence"));
            contextMenu.Items.Add(CreateBackContextMenuItem("Create Empty Subsequence", CreateEmptySubsequence_Clicked,
                "Create a new empty child sequence under the current sequence"));
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(CreateQuickCreateMenu("Events", QuickCreateEventEntries));
            contextMenu.Items.Add(CreateQuickCreateMenu("Actions", QuickCreateActionEntries));
            contextMenu.Items.Add(CreateQuickCreateMenu("Conversation Actions", QuickCreateConversationActionEntries));
            contextMenu.Items.Add(CreateQuickCreateMenu("Plot Actions", QuickCreatePlotActionEntries));
            contextMenu.Items.Add(CreateQuickCreateMenu("Conditionals", QuickCreateConditionalEntries));
            contextMenu.Items.Add(CreateQuickCreateMenu("Variables", QuickCreateVariableEntries));
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(CreateBackContextMenuItem("Open floating toolbox", OpenFloatingToolbox_Click,
                "Open the sequence toolbox in a separate floating window at the clicked viewport location"));
        }

        private void BackContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            backgroundContextMenuGraphLocation = null;
        }

        private IEnumerable<ClassInfo> GetQuickCreateSourceClasses(string header)
        {
            return header switch
            {
                "Actions" => actionsToolBox?.Classes ?? Enumerable.Empty<ClassInfo>(),
                "Conditionals" => conditionsToolBox?.Classes ?? Enumerable.Empty<ClassInfo>(),
                "Events" => eventsToolBox?.Classes ?? Enumerable.Empty<ClassInfo>(),
                "Variables" => variablesToolBox?.Classes ?? Enumerable.Empty<ClassInfo>(),
                _ => Enumerable.Empty<ClassInfo>()
            };
        }

        private static string NormalizeQuickCreateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            return new string(name
                .Replace("BioSeqAct_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("SFXSeqAct_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("SeqAct_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("BioSeqCond_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("SeqCond_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("BioSeqEvt_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("SFXSeqEvt_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("SeqEvent_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("BioSeqVar_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("SFXSeqVar_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Replace("SeqVar_", string.Empty, StringComparison.InvariantCultureIgnoreCase)
                .Where(char.IsLetterOrDigit)
                .ToArray())
                .ToLowerInvariant();
        }

        private ClassInfo ResolveQuickCreateClassInfo(string header, QuickCreateMenuEntry entry)
        {
            if (Pcc == null)
            {
                return null;
            }

            var sourceClasses = GetQuickCreateSourceClasses(header).ToList();

            foreach (string className in entry.ClassNames)
            {
                if (sourceClasses.FirstOrDefault(info => string.Equals(info.ClassName, className, StringComparison.InvariantCultureIgnoreCase)) is { } toolboxInfo)
                {
                    return toolboxInfo;
                }
            }

            string normalizedHeader = NormalizeQuickCreateName(entry.Header);
            var normalizedCandidates = entry.ClassNames
                .Append(entry.Header)
                .Select(NormalizeQuickCreateName)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.InvariantCultureIgnoreCase);

            if (!string.IsNullOrEmpty(normalizedHeader))
            {
                normalizedCandidates.Add(normalizedHeader);
            }

            if (sourceClasses.FirstOrDefault(info => normalizedCandidates.Contains(NormalizeQuickCreateName(info.ClassName))) is { } normalizedToolboxInfo)
            {
                return normalizedToolboxInfo;
            }

            foreach (string className in entry.ClassNames)
            {
                if (GlobalUnrealObjectInfo.GetClassOrStructInfo(Pcc.Game, className) is { } info)
                {
                    return info;
                }
            }

            return null;
        }

        private MenuItem CreateQuickCreateMenu(string header, IEnumerable<QuickCreateMenuEntry> entries)
        {
            var menuItem = new MenuItem { Header = header };
            var availableEntries = entries
                .Select(entry => new { Entry = entry, Info = ResolveQuickCreateClassInfo(header, entry) })
                .Where(item => item.Info is not null)
                .OrderBy(item => item.Entry.Header, StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            if (availableEntries.Count == 0)
            {
                menuItem.Items.Add(new MenuItem
                {
                    Header = "No supported entries",
                    IsEnabled = false
                });
                return menuItem;
            }

            foreach (var item in availableEntries)
            {
                menuItem.Items.Add(new MenuItem
                {
                    Header = item.Entry.Header,
                    Tag = item.Info,
                    StaysOpenOnClick = true
                });
            }

            foreach (MenuItem child in menuItem.Items)
            {
                child.Click += QuickCreateFromBackgroundMenu_Click;
            }

            return menuItem;
        }

        private static MenuItem CreateBackContextMenuItem(string header, RoutedEventHandler clickHandler, string toolTip = null)
        {
            var menuItem = new MenuItem
            {
                Header = header,
                ToolTip = toolTip
            };
            menuItem.Click += clickHandler;
            return menuItem;
        }

        private void QuickCreateFromBackgroundMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: ClassInfo info })
            {
                return;
            }

            pendingNewObjectPosition = backgroundContextMenuGraphLocation;
            CreateNewObject(info);
        }

        private void OpenFloatingToolbox()
        {
            if (floatingToolboxWindow == null)
            {
                CreateFloatingToolboxWindow();
            }

            SyncFloatingToolboxItems(includeCustomSequences: true);
            floatingToolboxWindow.Left = floatingToolboxScreenLocation.X;
            floatingToolboxWindow.Top = floatingToolboxScreenLocation.Y;

            if (!floatingToolboxWindow.IsVisible)
            {
                floatingToolboxWindow.Show();
            }

            PositionFloatingToolboxWindow();

            floatingToolboxWindow.Activate();
            floatingToolboxWindow.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => floatingToolboxSearchBox?.FocusSearchBox()));
        }

        private System.Windows.Point ConvertScreenPixelsToDips(System.Drawing.Point screenPoint)
        {
            if (PresentationSource.FromVisual(graphGrid)?.CompositionTarget is { } compositionTarget)
            {
                return compositionTarget.TransformFromDevice.Transform(
                    new System.Windows.Point(screenPoint.X, screenPoint.Y));
            }

            return new System.Windows.Point(screenPoint.X, screenPoint.Y);
        }

        private void PositionFloatingToolboxWindow()
        {
            if (floatingToolboxWindow == null)
            {
                return;
            }

            floatingToolboxWindow.Left = floatingToolboxScreenLocation.X;
            floatingToolboxWindow.Top = floatingToolboxScreenLocation.Y;
        }

        private void CreateFloatingToolboxWindow()
        {
            floatingFavoritesToolBox = CreateFloatingClassToolBox(CreateNewObject, RemoveFavorite);
            floatingEventsToolBox = CreateFloatingClassToolBox(CreateNewObject, SetFavorite);
            floatingActionsToolBox = CreateFloatingClassToolBox(CreateNewObject, SetFavorite);
            floatingConditionsToolBox = CreateFloatingClassToolBox(CreateNewObject, SetFavorite);
            floatingVariablesToolBox = CreateFloatingClassToolBox(CreateNewObject, SetFavorite);
            floatingSceneShopToolBox = CreateFloatingClassToolBox(CreateNewObject, null);
            floatingCustomSequencesToolBox = CreateFloatingGenericToolBox(CreateCustomSequence, null);
            floatingToolboxSearchResults = CreateFloatingGenericToolBox(CreateFloatingToolboxSearchResult,
                ToggleFloatingToolboxSearchResultFavorite);

            floatingFavoritesToolBox.IsSearchVisible = false;
            floatingEventsToolBox.IsSearchVisible = false;
            floatingActionsToolBox.IsSearchVisible = false;
            floatingConditionsToolBox.IsSearchVisible = false;
            floatingVariablesToolBox.IsSearchVisible = false;
            floatingSceneShopToolBox.IsSearchVisible = false;
            floatingCustomSequencesToolBox.IsSearchVisible = false;
            floatingToolboxSearchResults.IsSearchVisible = false;
            floatingToolboxSearchResults.Visibility = Visibility.Collapsed;

            floatingSceneShopTab = new TabItem
            {
                Header = "Scene Shop",
                Content = floatingSceneShopToolBox
            };
            floatingCustomSequencesTab = new TabItem
            {
                Header = "Custom sequences",
                Content = floatingCustomSequencesToolBox,
                Visibility = App.IsDebug ? Visibility.Visible : Visibility.Collapsed
            };

            floatingToolboxTabs = new TabControl
            {
                Items =
                {
                    new TabItem
                    {
                        Header = new TextBlock
                        {
                            Text = "Favorites",
                            ToolTip = "Shift-click on a sequence class to add or remove from favorites."
                        },
                        Content = floatingFavoritesToolBox
                    },
                    new TabItem { Header = "Events", Content = floatingEventsToolBox },
                    new TabItem { Header = "Actions", Content = floatingActionsToolBox },
                    new TabItem { Header = "Conditions", Content = floatingConditionsToolBox },
                    new TabItem { Header = "Variables", Content = floatingVariablesToolBox },
                    floatingSceneShopTab,
                    floatingCustomSequencesTab
                }
            };

            floatingToolboxSearchBox = new SearchBox
            {
                WatermarkText = "Search all toolbox categories"
            };
            floatingToolboxSearchBox.TextChanged += FloatingToolboxSearchBox_TextChanged;
            DockPanel.SetDock(floatingToolboxSearchBox, Dock.Top);

            var toolboxViews = new Grid();
            toolboxViews.Children.Add(floatingToolboxTabs);
            toolboxViews.Children.Add(floatingToolboxSearchResults);

            var toolboxContent = new DockPanel();
            toolboxContent.Children.Add(floatingToolboxSearchBox);
            toolboxContent.Children.Add(toolboxViews);

            floatingToolboxWindow = new Window
            {
                Title = "Sequence Toolbox",
                Width = 560,
                Height = 520,
                MinWidth = 480,
                MinHeight = 260,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = toolboxContent
            };
            if (Window.GetWindow(graphGrid) is { } owner)
            {
                floatingToolboxWindow.Owner = owner;
            }
            CustomWindowChrome.ApplyCustomChrome(floatingToolboxWindow);
            floatingToolboxWindow.Loaded += (_, _) => PositionFloatingToolboxWindow();
            floatingToolboxWindow.Closed += (_, _) =>
            {
                floatingToolboxWindow = null;
                floatingFavoritesToolBox = null;
                floatingEventsToolBox = null;
                floatingActionsToolBox = null;
                floatingConditionsToolBox = null;
                floatingVariablesToolBox = null;
                floatingSceneShopToolBox = null;
                floatingCustomSequencesToolBox = null;
                floatingToolboxSearchBox = null;
                floatingToolboxSearchResults = null;
                floatingToolboxTabs = null;
                floatingSceneShopTab = null;
                floatingCustomSequencesTab = null;
            };
        }

        private void FloatingToolboxSearchBox_TextChanged(SearchBox sender, string newText)
        {
            UpdateFloatingToolboxSearchResults(newText);
        }

        private void ToolboxSearchBox_TextChanged(SearchBox sender, string newText)
        {
            UpdateToolboxSearchResults(newText);
        }

        private void ToolboxMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem)
            {
                toolBoxExpander.IsExpanded = menuItem.IsChecked;
            }
        }

        private void UpdateToolboxSearchResults(string searchText)
        {
            bool isSearching = !string.IsNullOrWhiteSpace(searchText);
            toolboxTabs.Visibility = isSearching ? Visibility.Collapsed : Visibility.Visible;
            toolboxSearchResults.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;

            if (!isSearching)
            {
                toolboxSearchResults.Items.Clear();
                return;
            }

            var results = new List<object>();
            var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFloatingToolboxClassSearchResults(results, seenClasses, "Events", eventsToolBox, searchText);
            AddFloatingToolboxClassSearchResults(results, seenClasses, "Actions", actionsToolBox, searchText);
            AddFloatingToolboxClassSearchResults(results, seenClasses, "Conditions", conditionsToolBox, searchText);
            AddFloatingToolboxClassSearchResults(results, seenClasses, "Variables", variablesToolBox, searchText);

            if (IsSceneShopSequenceSelected)
            {
                AddFloatingToolboxClassSearchResults(results, seenClasses, "Scene Shop", sceneShopToolBox, searchText);
            }

            if (App.IsDebug)
            {
                results.AddRange(customSequencesToolBox.Items
                    .Where(item => item.ToString()?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(item => new FloatingToolboxSearchEntry("Custom sequences", item)));
            }

            toolboxSearchResults.Items.ReplaceAll(results);
        }

        private void UpdateFloatingToolboxSearchResults(string searchText)
        {
            if (floatingToolboxTabs == null || floatingToolboxSearchResults == null)
            {
                return;
            }

            bool isSearching = !string.IsNullOrWhiteSpace(searchText);
            floatingToolboxTabs.Visibility = isSearching ? Visibility.Collapsed : Visibility.Visible;
            floatingToolboxSearchResults.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;

            if (!isSearching)
            {
                floatingToolboxSearchResults.Items.Clear();
                return;
            }

            var results = new List<object>();
            var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFloatingToolboxClassSearchResults(results, seenClasses, "Events", floatingEventsToolBox, searchText);
            AddFloatingToolboxClassSearchResults(results, seenClasses, "Actions", floatingActionsToolBox, searchText);
            AddFloatingToolboxClassSearchResults(results, seenClasses, "Conditions", floatingConditionsToolBox, searchText);
            AddFloatingToolboxClassSearchResults(results, seenClasses, "Variables", floatingVariablesToolBox, searchText);

            if (IsSceneShopSequenceSelected)
            {
                AddFloatingToolboxClassSearchResults(results, seenClasses, "Scene Shop", floatingSceneShopToolBox, searchText);
            }

            if (App.IsDebug && floatingCustomSequencesToolBox != null)
            {
                results.AddRange(floatingCustomSequencesToolBox.Items
                    .Where(item => item.ToString()?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(item => new FloatingToolboxSearchEntry("Custom sequences", item)));
            }

            floatingToolboxSearchResults.Items.ReplaceAll(results);
        }

        private static void AddFloatingToolboxClassSearchResults(List<object> results, HashSet<string> seenClasses,
            string category, ClassToolBox toolBox, string searchText)
        {
            if (toolBox == null)
            {
                return;
            }

            results.AddRange(toolBox.Classes
                .Where(classInfo => classInfo.ClassName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                                    && seenClasses.Add(classInfo.ClassName))
                .Select(classInfo => new FloatingToolboxSearchEntry(category, classInfo)));
        }

        private void CreateFloatingToolboxSearchResult(object result)
        {
            if (result is not FloatingToolboxSearchEntry searchEntry)
            {
                return;
            }

            if (searchEntry.Item is ClassInfo classInfo)
            {
                CreateNewObject(classInfo);
            }
            else
            {
                CreateCustomSequence(searchEntry.Item);
            }
        }

        private void ToggleFloatingToolboxSearchResultFavorite(object result)
        {
            if (result is not FloatingToolboxSearchEntry { Item: ClassInfo classInfo })
            {
                return;
            }

            if (favoritesToolBox.Classes.Contains(classInfo))
            {
                RemoveFavorite(classInfo);
            }
            else
            {
                SetFavorite(classInfo);
            }
        }

        private static ClassToolBox CreateFloatingClassToolBox(Action<ClassInfo> doubleClickCallback,
            Action<ClassInfo> shiftClickCallback)
        {
            return new ClassToolBox
            {
                DoubleClickCallback = doubleClickCallback,
                ShiftClickCallback = shiftClickCallback
            };
        }

        private static GenericToolBox CreateFloatingGenericToolBox(Action<object> doubleClickCallback,
            Action<object> shiftClickCallback)
        {
            return new GenericToolBox
            {
                DoubleClickCallback = doubleClickCallback,
                ShiftClickCallback = shiftClickCallback
            };
        }

        private void SyncFloatingToolboxItems(bool includeCustomSequences = false)
        {
            if (floatingToolboxWindow == null || Pcc == null)
            {
                return;
            }

            floatingFavoritesToolBox?.Classes.ReplaceAll(favoritesToolBox.Classes);
            floatingEventsToolBox?.Classes.ReplaceAll(eventsToolBox.Classes);
            floatingActionsToolBox?.Classes.ReplaceAll(actionsToolBox.Classes);
            floatingConditionsToolBox?.Classes.ReplaceAll(conditionsToolBox.Classes);
            floatingVariablesToolBox?.Classes.ReplaceAll(variablesToolBox.Classes);
            floatingSceneShopToolBox?.Classes.ReplaceAll(sceneShopToolBox.Classes);

            if (includeCustomSequences)
            {
                floatingCustomSequencesToolBox?.Items.ReplaceAll(customSequencesToolBox.Items);
            }

            if (floatingSceneShopTab != null)
            {
                floatingSceneShopTab.Visibility = IsSceneShopSequenceSelected ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(floatingToolboxSearchBox?.Text))
            {
                UpdateFloatingToolboxSearchResults(floatingToolboxSearchBox.Text);
            }
        }

        private void SequenceEditor_DragEnter(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (e.Data.GetData(typeof(SequenceGraphEditor.SequenceObjectDragData)) is SequenceGraphEditor.SequenceObjectDragData dragData)
            {
                e.Effect = CanAcceptSequenceObjectDrop(dragData)
                    ? System.Windows.Forms.DragDropEffects.Copy
                    : System.Windows.Forms.DragDropEffects.None;
            }
            else if (e.Data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop))
                e.Effect = System.Windows.Forms.DragDropEffects.All;
            else
                e.Effect = System.Windows.Forms.DragDropEffects.None;
        }

        private void SequenceEditor_DragDrop(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (e.Data.GetData(typeof(SequenceGraphEditor.SequenceObjectDragData)) is SequenceGraphEditor.SequenceObjectDragData dragData)
            {
                CloneDroppedSequenceObjects(dragData);
            }
            else if (e.Data.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] DroppedFiles)
            {
                if (DroppedFiles.Any())
                {
                    LoadFile(DroppedFiles[0]);
                }
            }
        }

        private bool CanAcceptSequenceObjectDrop(SequenceGraphEditor.SequenceObjectDragData dragData)
        {
            return Pcc != null
                   && SelectedSequence != null
                   && dragData.Exports.Count > 0
                   && dragData.Exports.All(export => export.FileRef == dragData.Exports[0].FileRef)
                   && dragData.Exports[0].FileRef != Pcc;
        }

        private void CloneDroppedSequenceObjects(SequenceGraphEditor.SequenceObjectDragData dragData)
        {
            if (!CanAcceptSequenceObjectDrop(dragData))
            {
                return;
            }

            ExportEntry sourceObject = dragData.Exports[0];
            if (SelectedSequence.Game.IsLEGame() != sourceObject.Game.IsLEGame() && !App.IsDebug && sourceObject.Game != MEGame.UDK)
            {
                MessageBox.Show(
                    "Cannot port sequence objects between Original Trilogy (OT) games and Legendary Edition (LE) games in release builds of Legendary Explorer.",
                    "Cannot port sequence objects",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            MessageBoxResult cloneChoice = MessageBox.Show(
                $"Clone {dragData.Exports.Count} sequence object(s) with all referenced output, variable, and event links?\n\n" +
                "Yes: Clone the objects and all referenced links.\n" +
                "No: Clone the selected objects and their property references, but remove graph connections.",
                "Clone sequence objects",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (cloneChoice == MessageBoxResult.Cancel)
            {
                return;
            }

            bool cloneAllLinks = cloneChoice == MessageBoxResult.Yes;
            var rop = new RelinkerOptionsPackage
            {
                IsCrossGame = sourceObject.Game != Pcc.Game && sourceObject.Game != MEGame.UDK,
                Cache = new PackageCache(),
                ImportExportDependencies = true,
                GenerateImportsForGlobalFiles = true,
                PortImportsMemorySafe = Settings.PackageEditor_DefaultMemorySafeImportPorting,
                RelinkPropertyMutator = cloneAllLinks
                    ? null
                    : (sourceExport, props) =>
                    {
                        if (dragData.Exports.Contains(sourceExport))
                        {
                            KismetHelper.RemoveAllLinks(props);
                        }
                    },
            };

            var sourceParentSequences = dragData.Exports
                .Select(export => export.GetProperty<ObjectProperty>("ParentSequence")?.ResolveToEntry(export.FileRef) as ExportEntry)
                .Where(sequence => sequence != null)
                .Distinct()
                .ToList();
            foreach (ExportEntry sourceParentSequence in sourceParentSequences)
            {
                rop.CrossPackageMap[sourceParentSequence] = SelectedSequence;
                rop.RelinkMapEntriesToSkip.Add(sourceParentSequence);
            }

            foreach (ExportEntry export in dragData.Exports)
            {
                int originalIndex = export.indexValue;
                bool hadChanges = export.EntryHasPendingChanges;
                bool hadHeaderChanges = export.HeaderChanged;
                try
                {
                    if (Pcc.FindEntry(export.InstancedFullPath) != null)
                    {
                        export.indexValue = Pcc.GetNextIndexedName(export.ObjectName).Number;
                    }

                    EntryImporter.ImportAndRelinkEntries(
                        EntryImporter.PortingOption.AddSingularAsChild,
                        export,
                        Pcc,
                        SelectedSequence,
                        false,
                        rop,
                        out _);
                }
                finally
                {
                    export.indexValue = originalIndex;
                    export.HeaderChanged = hadHeaderChanges;
                    export.EntryHasPendingChanges = hadChanges;
                }
            }

            Relinker.RelinkAll(rop);

            var importedSequenceObjects = rop.CrossPackageMap
                .Where(pair => pair.Key is ExportEntry sourceExport
                               && pair.Value is ExportEntry
                               && (dragData.Exports.Contains(sourceExport)
                                   || sourceParentSequences.Contains(sourceExport.GetProperty<ObjectProperty>("ParentSequence")?.ResolveToEntry(sourceExport.FileRef) as ExportEntry)))
                .Select(pair => (ExportEntry)pair.Value)
                .Where(export => export != SelectedSequence)
                .Distinct()
                .ToList();

            foreach (ExportEntry importedObject in importedSequenceObjects)
            {
                KismetHelper.AddObjectToSequence(importedObject, SelectedSequence, keepPositioning: true);
            }

            RefreshView();
            if (rop.RelinkReport.Count > 0)
            {
                new ListDialog(rop.RelinkReport, "Relink report",
                    "The following items reported relinking issues.", this).Show();
            }
        }

        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.TargetItem is TreeViewEntry && dropInfo.Data is TreeViewEntry { Parent: not null } sourceItem)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                bool isSamePackageDrop = sourceItem.Entry?.FileRef == Pcc;
                bool isShiftHeld = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                dropInfo.Effects = isSamePackageDrop && isShiftHeld ? DragDropEffects.Move : DragDropEffects.Copy;
            }
        }

        void IDropTarget.Drop(IDropInfo dropInfo)
        {
            if (dropInfo.TargetItem is not TreeViewEntry targetItem || dropInfo.Data is not TreeViewEntry { Parent: not null } sourceItem)
            {
                return;
            }

            IEntry sourceEntry = sourceItem.Entry;
            IEntry targetEntry = targetItem.Entry;
            if (sourceItem == targetItem || sourceEntry == null || targetEntry == null)
            {
                return;
            }

            bool isShiftHeld = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            bool isSamePackageDrop = sourceEntry.FileRef == targetEntry.FileRef;
            if (isSamePackageDrop && !isShiftHeld)
            {
                return;
            }

            if (isSamePackageDrop && isShiftHeld)
            {
                var oldParent = sourceEntry.Parent as ExportEntry;
                sourceEntry.idxLink = targetEntry.UIndex;
                if (oldParent != sourceEntry.Parent)
                {
                    MatineeHelper.RemoveFromParentInterpList(sourceEntry, oldParent);
                    if (ShouldAddToInterpList(sourceEntry))
                    {
                        MatineeHelper.AddToParentInterpList(sourceEntry);
                    }
                }

                RefreshInterpDataTreePreserveState(sourceEntry.UIndex);
                return;
            }

            if (targetEntry.Game.IsLEGame() != sourceEntry.Game.IsLEGame() && !App.IsDebug && sourceEntry.Game != MEGame.UDK)
            {
                MessageBox.Show(
                    "Cannot port assets between Original Trilogy (OT) games and Legendary Edition (LE) games in release builds of Legendary Explorer.",
                    "Cannot port asset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var treeMergeDialog = new TreeMergeDialog(sourceEntry, targetEntry, Pcc.Game);
            if (treeMergeDialog.Owner == null)
            {
                treeMergeDialog.Owner = this;
            }

            treeMergeDialog.ShowDialog();
            var portingOption = treeMergeDialog.PortingOption;
            portingOption.PortUsingDonors = treeMergeDialog.PortUsingDonors;
            portingOption.PortGlobalsAsImports = treeMergeDialog.PortGlobalsAsImports;
            portingOption.PortExportsAsImportsWhenPossible = treeMergeDialog.PortExportsAsImportsWhenPossible;
            portingOption.PortExportsMemorySafe = treeMergeDialog.PortExportsMemorySafe;
            if (portingOption.PortingOptionChosen == EntryImporter.PortingOption.Cancel)
            {
                return;
            }

            IEntry targetLinkEntry = targetEntry;

            int originalIndex = -1;
            bool hadChanges = false;
            bool hadHeaderChanges = false;
            if (portingOption.PortingOptionChosen != EntryImporter.PortingOption.ReplaceSingular
                && portingOption.PortingOptionChosen != EntryImporter.PortingOption.ReplaceSingularWithRelink
                && targetEntry.FileRef.FindEntry(sourceEntry.InstancedFullPath) != null)
            {
                originalIndex = sourceEntry.indexValue;
                hadChanges = sourceEntry.EntryHasPendingChanges;
                hadHeaderChanges = sourceEntry.HeaderChanged;
                sourceEntry.indexValue = targetEntry.FileRef.GetNextIndexedName(sourceEntry.ObjectName).Number;
            }

            string objectDBPath = AppDirectories.GetObjectDatabasePath(targetEntry.Game);
            bool shouldUseDonors = portingOption.PortUsingDonors && sourceEntry.Game != targetEntry.Game && sourceEntry.Game != MEGame.UDK;
            ObjectInstanceDB objectDB = null;
            if (shouldUseDonors)
            {
                if (File.Exists(objectDBPath))
                {
                    using FileStream fs = File.OpenRead(objectDBPath);
                    objectDB = ObjectInstanceDB.Deserialize(targetEntry.Game, fs);
                }
                else if (MessageBox.Show(
                             "Port With Donors checkbox was selected, but no object database was found! Continue operation without donors?",
                             "No object database",
                             MessageBoxButton.YesNo,
                             MessageBoxImage.Warning,
                             MessageBoxResult.No) is not MessageBoxResult.Yes)
                {
                    return;
                }
            }

            var rop = new RelinkerOptionsPackage
            {
                IsCrossGame = sourceEntry.Game != targetEntry.Game && sourceEntry.Game != MEGame.UDK,
                Cache = new PackageCache(),
                TargetGameDonorDB = objectDB,
                ImportExportDependencies = portingOption.PortingOptionChosen is EntryImporter.PortingOption.CloneAllDependencies
                    or EntryImporter.PortingOption.ReplaceSingularWithRelink,
                GenerateImportsForGlobalFiles = portingOption.PortGlobalsAsImports,
                PortImportsMemorySafe = portingOption.PortExportsMemorySafe,
                PortExportsAsImportsWhenPossible = portingOption.PortExportsAsImportsWhenPossible,
            };

            var relinkResults = EntryImporter.ImportAndRelinkEntries(portingOption.PortingOptionChosen, sourceEntry, Pcc,
                targetLinkEntry, true, rop, out IEntry newEntry);

            if (originalIndex >= 0)
            {
                sourceEntry.indexValue = originalIndex;
                sourceEntry.HeaderChanged = hadHeaderChanges;
                sourceEntry.EntryHasPendingChanges = hadChanges;
            }

            if (portingOption.PortingOptionChosen is not EntryImporter.PortingOption.ReplaceSingular
                and not EntryImporter.PortingOption.ReplaceSingularWithRelink
                && newEntry != null
                && ShouldAddToInterpList(newEntry))
            {
                AddToInterpList(newEntry);
            }

            RefreshInterpDataTreePreserveState(newEntry?.UIndex ?? targetEntry.UIndex);

            if ((relinkResults?.Count ?? 0) > 0)
            {
                new ListDialog(relinkResults, "Relink report",
                    "The following items reported relinking issues.", this).Show();
            }
        }

        public override void HandleUpdate(List<PackageUpdate> updates)
        {
            if (Pcc == null)
            {
                return; //nothing is loaded
            }

            if (updates.Any(update => update.Change != PackageChange.ExportData))
            {
                InterpData_MetadataEditor.LoadPccData(Pcc);
            }

            List<PackageUpdate> relevantUpdates = updates.Where(x => x.Change.Has(PackageChange.Export)).ToList();
            List<int> updatedExports = relevantUpdates.Select(x => x.Index).ToList();

            if (InterpDataTreeNodes.Count > 0)
            {
                var interpTreeIndexes = InterpDataTreeNodes
                    .SelectMany(root => root.FlattenTree())
                    .Select(node => node.UIndex)
                    .ToHashSet();
                List<PackageUpdate> interpTreeUpdates = relevantUpdates
                    .Where(update => interpTreeIndexes.Contains(update.Index))
                    .ToList();

                if (interpTreeUpdates.Any(update => update.Change != PackageChange.ExportData))
                {
                    RefreshInterpDataTreePreserveState();
                }
                else if (GetSelectedInterpDataTreeExport() is ExportEntry selectedInterpExport
                         && interpTreeUpdates.Any(update => update.Index == selectedInterpExport.UIndex))
                {
                    if (!InterpData_InterpreterWPF.ConsumePendingPropertyWrite(selectedInterpExport))
                    {
                        QueueInterpDataEditorsReload(selectedInterpExport.UIndex);
                    }
                }
            }

            if (SelectedSequence != null && updatedExports.Contains(SelectedSequence.UIndex))
            {
                //loaded sequence is no longer a sequence (or SFXSceneShopGameData container)
                if (!SelectedSequence.IsSequence() && !SelectedSequence.IsA("SFXSceneShopGameData"))
                {
                    SelectedSequence = null;
                    graphEditor.nodeLayer.RemoveAllChildren();
                    graphEditor.edgeLayer.RemoveAllChildren();
                    CurrentObjects.ClearEx();
                    SequenceExports.ClearEx();
                    SelectedObjects.ClearEx();
                    Properties_InterpreterWPF.UnloadExport();
                    InterpData_MetadataEditor.UnloadExport();
                }

                RefreshView();
                LoadSequences();
            }
            else
            {
                if (updatedExports.Intersect(CurrentObjects.Select(obj => obj.UIndex)).Any())
                {
                    RefreshView();
                }

                foreach (var updatedExportUIndex in updatedExports)
                {
                    if (Pcc.TryGetUExport(updatedExportUIndex, out ExportEntry updatedExport) &&
                        (updatedExport.IsSequence() || updatedExport.IsA("SFXSceneShopGameData")) && updatedExport != SelectedSequence)
                    {
                        LoadSequences();
                        break;
                    }
                }
            }

            if (updatedExports.Any(uIdx => Pcc.GetEntry(uIdx) is ExportEntry { IsClass: true }))
            {
                RefreshToolboxItems();
            }
        }

        private readonly Dictionary<int, PointF> customSaveData = new();
        private bool panToSelection = true;
        private IMEPackage PackageQueuedForLoad;
        private string FileQueuedForLoad;
        private ExportEntry ExportQueuedForFocusing;
        private bool AllowWindowRefocus = true;
        
        private Color _graphEditorBackColor = Color.FromArgb(79, 79, 79);
        public Color GraphEditorBackColor
        {
            get => _graphEditorBackColor;
            set
            {
                if (_graphEditorBackColor != value)
                {
                    _graphEditorBackColor = value;
                    if (graphEditor != null)
                    {
                        graphEditor.BackColor = value;
                        if (CurrentObjects.Any())
                        {
                            RefreshView();
                        }
                    }
                }
            }
        }

        private Color _boxFillColor = Color.FromArgb(140, 140, 140);
        public Color BoxFillColor
        {
            get => _boxFillColor;
            set
            {
                if (_boxFillColor != value)
                {
                    _boxFillColor = value;
                    SObj.NodeBrushColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _titleBoxColor = Color.FromArgb(112, 112, 112);
        public Color TitleBoxColor
        {
            get => _titleBoxColor;
            set
            {
                if (_titleBoxColor != value)
                {
                    _titleBoxColor = value;
                    SObj.TitleBoxBrushColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _commentTextColor = Color.FromArgb(74, 63, 190);
        public Color CommentTextColor
        {
            get => _commentTextColor;
            set
            {
                if (_commentTextColor != value)
                {
                    _commentTextColor = value;
                    SObj.CommentTextColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _boxTextColor = Color.FromArgb(0, 0, 0);
        public Color BoxTextColor
        {
            get => _boxTextColor;
            set
            {
                if (_boxTextColor != value)
                {
                    _boxTextColor = value;
                    SObj.BoxTextColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _connectionColor = Color.Black;
        public Color ConnectionColor
        {
            get => _connectionColor;
            set
            {
                if (_connectionColor != value)
                {
                    _connectionColor = value;
                    SObj.ConnectionColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        private Color _varLinkColor = Color.Black;
        public Color VarLinkColor
        {
            get => _varLinkColor;
            set
            {
                if (_varLinkColor != value)
                {
                    _varLinkColor = value;
                    SObj.VarLinkColor = value;
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }








        /// <summary>
        /// Applies theme-appropriate default colors based on the current dark mode setting.
        /// Called when user switches themes - overrides any user customizations.
        /// User can then customize colors again via color pickers.
        /// </summary>
        private void ApplyThemeDefaults()
        {
            bool isDarkMode = Settings.Global_DarkMode_Enabled;

            if (isDarkMode)
            {
                // Dark theme - Visual Studio dark mode inspired colors
                _graphEditorBackColor = Color.FromArgb(30, 30, 30);
                _boxFillColor = Color.FromArgb(45, 45, 48);
                _titleBoxColor = Color.FromArgb(37, 37, 38);
                _commentTextColor = Color.FromArgb(87, 166, 74);
                _boxTextColor = Color.FromArgb(220, 220, 220);
                _connectionColor = Color.White;  // White connection lines for dark mode
                _varLinkColor = Color.White;  // White var link lines for dark mode
            }
            else
            {
                // Light theme defaults
                _graphEditorBackColor = Color.FromArgb(128, 128, 128);
                _boxFillColor = Color.FromArgb(140, 140, 140);
                _titleBoxColor = Color.FromArgb(112, 112, 112);
                _commentTextColor = Color.FromArgb(25, 25, 112);
                _boxTextColor = Color.FromArgb(255, 255, 255);
                _connectionColor = Color.Black;  // Black connection lines for light mode
                _varLinkColor = Color.Black;  // Black var link lines for light mode
            }

            // Apply to static properties used by SObj
            SObj.NodeBrushColor = _boxFillColor;
            SObj.TitleBoxBrushColor = _titleBoxColor;
            SObj.CommentTextColor = _commentTextColor;
            SObj.BoxTextColor = _boxTextColor;
            SObj.ConnectionColor = _connectionColor;
            SObj.VarLinkColor = _varLinkColor;
        }

        private void saveView(bool toFile = true)
        {
            if (CurrentObjects.Count == 0)
                return;
            SavedView = new(new(), graphEditor.Camera.ViewBounds);
            foreach (SObj obj in CurrentObjects)
            {
                if (obj.Pickable)
                {
                    SavedView.Positions[obj.UIndex] = new PointF(obj.X + obj.Offset.X, obj.Y + obj.Offset.Y);
                }
            }

            foreach ((int key, PointF value) in customSaveData)
            {
                SavedView.Positions[key] = value;
            }

            customSaveData.Clear();

            if (toFile)
            {
                string outputFile = JsonConvert.SerializeObject(SavedView);
                if (!Directory.Exists(Path.GetDirectoryName(JSONpath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(JSONpath));
                File.WriteAllText(JSONpath, outputFile);
                SavedView.Positions.Clear();
            }
        }

        private void AddInputEntry_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj { Export: { } export })
            {
                PromptAndAddNamedLinkEntry(export, "InputLinks", "input", "In");
            }
        }

        private void AddOutputEntry_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj { Export: { } export })
            {
                PromptAndAddNamedLinkEntry(export, "OutputLinks", "output", "Out");
            }
        }

        private void AddVariableEntry_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj { Export: { } export })
            {
                PromptAndAddVariableLinkEntry(export);
            }
        }

        private record VariableLinkEntryDialogResult(string EntryName, string ExpectedTypeName);

        private VariableLinkEntryDialogResult PromptForVariableLinkEntry()
        {
            var classOptions = GlobalUnrealObjectInfo.GetClasses(Pcc.Game).Values
                .Where(x => x.IsA("SequenceVariable", Pcc.Game))
                .OrderBy(x => x.ClassName)
                .ToList();

            var nameTextBox = new TextBox
            {
                MinWidth = 260,
                Text = "Variable"
            };
            ClassInfo selectedType = classOptions.FirstOrDefault(x => x.ClassName == "SeqVar_Object")
                                     ?? classOptions.FirstOrDefault();
            var typeTextBox = new TextBox
            {
                MinWidth = 180,
                Text = selectedType?.ClassName ?? string.Empty,
                IsReadOnly = true
            };
            var pickTypeButton = new Button
            {
                Content = "Pick...",
                MinWidth = 72,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                MinWidth = 80,
                Margin = new Thickness(0, 0, 8, 0)
            };
            System.Windows.Window dialog = null;
            dialog = new System.Windows.Window
            {
                Title = "Add variable entry",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(12),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Label"
                        },
                        nameTextBox,
                        new TextBlock
                        {
                            Margin = new Thickness(0, 10, 0, 0),
                            Text = "Expected type"
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children =
                            {
                                typeTextBox,
                                pickTypeButton
                            }
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 12, 0, 0),
                            Children =
                            {
                                okButton,
                                new Button
                                {
                                    Content = "Cancel",
                                    IsCancel = true,
                                    MinWidth = 80
                                }
                            }
                        }
                    }
                }
            };

            CustomWindowChrome.ApplyCustomChrome(dialog);

            void UpdateOkState()
            {
                okButton.IsEnabled = !string.IsNullOrWhiteSpace(nameTextBox.Text) && selectedType != null;
            }

            nameTextBox.TextChanged += (_, _) => UpdateOkState();
            pickTypeButton.Click += (_, _) =>
            {
                if (ClassPickerDlg.GetClass(dialog, classOptions, "Select datatype", "Select") is not { } chosenClass)
                {
                    return;
                }

                selectedType = chosenClass;
                typeTextBox.Text = chosenClass.ClassName;
                UpdateOkState();
            };
            typeTextBox.MouseDoubleClick += (_, _) => pickTypeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.Loaded += (_, _) =>
            {
                nameTextBox.Focus();
                nameTextBox.SelectAll();
                UpdateOkState();
            };
            okButton.Click += (_, _) => dialog.DialogResult = true;

            return dialog.ShowDialog() == true
                ? new VariableLinkEntryDialogResult(nameTextBox.Text.Trim(), selectedType?.ClassName)
                : null;
        }

        private int? PromptForPositiveCount(string prompt, string title, string defaultValue)
        {
            while (true)
            {
                var result = PromptDialog.Prompt(this, prompt, title, defaultValue, true);
                if (result == null)
                {
                    return null;
                }

                if (int.TryParse(result, out int count) && count > 0)
                {
                    return count;
                }

                MessageBox.Show(this, "Please enter a whole number greater than 0.", title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                defaultValue = result;
            }
        }

        private record VariableLinkReplacementDialogResult(string OldName, string NewName);

        private VariableLinkReplacementDialogResult ShowVariableLinkReplacementDialog()
        {
            var oldNameTextBox = new TextBox
            {
                MinWidth = 300
            };
            var newNameTextBox = new TextBox
            {
                MinWidth = 300
            };
            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                MinWidth = 80,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var dialog = new System.Windows.Window
            {
                Title = "Replace Variable Link Names",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(12),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Existing LinkDesc"
                        },
                        oldNameTextBox,
                        new TextBlock
                        {
                            Margin = new Thickness(0, 10, 0, 0),
                            Text = "Replacement LinkDesc"
                        },
                        newNameTextBox,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 12, 0, 0),
                            Children =
                            {
                                okButton,
                                new Button
                                {
                                    Content = "Cancel",
                                    IsCancel = true,
                                    MinWidth = 80
                                }
                            }
                        }
                    }
                }
            };

            CustomWindowChrome.ApplyCustomChrome(dialog);

            void UpdateOkState()
            {
                okButton.IsEnabled = !string.IsNullOrWhiteSpace(oldNameTextBox.Text)
                                     && !string.IsNullOrWhiteSpace(newNameTextBox.Text)
                                     && !string.Equals(oldNameTextBox.Text.Trim(), newNameTextBox.Text.Trim(),
                                         StringComparison.Ordinal);
            }

            oldNameTextBox.TextChanged += (_, _) => UpdateOkState();
            newNameTextBox.TextChanged += (_, _) => UpdateOkState();
            dialog.Loaded += (_, _) =>
            {
                oldNameTextBox.Focus();
                UpdateOkState();
            };
            okButton.Click += (_, _) => dialog.DialogResult = true;

            return dialog.ShowDialog() == true
                ? new VariableLinkReplacementDialogResult(oldNameTextBox.Text.Trim(), newNameTextBox.Text.Trim())
                : null;
        }

        private void ReplaceVariableLinkNames_Click(object sender, RoutedEventArgs e)
        {
            const string title = "Replace Variable Link Names";
            if (ShowVariableLinkReplacementDialog() is not { } replacement)
            {
                return;
            }

            string oldName = replacement.OldName;
            string newName = replacement.NewName;

            int replacedLinkCount = 0;
            int modifiedExportCount = 0;
            foreach (var export in Pcc.Exports)
            {
                var variableLinks = export.GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
                if (variableLinks == null)
                {
                    continue;
                }

                int exportReplacementCount = 0;
                foreach (var variableLink in variableLinks)
                {
                    var linkDesc = variableLink.GetProp<StrProperty>("LinkDesc");
                    if (string.Equals(linkDesc?.Value, oldName, StringComparison.Ordinal))
                    {
                        variableLink.Properties.AddOrReplaceProp(new StrProperty(newName, "LinkDesc"));
                        exportReplacementCount++;
                    }
                }

                if (exportReplacementCount > 0)
                {
                    export.WriteProperty(variableLinks);
                    replacedLinkCount += exportReplacementCount;
                    modifiedExportCount++;
                }
            }

            if (replacedLinkCount > 0)
            {
                RefreshView();
            }

            MessageBox.Show(this,
                $"Replaced {replacedLinkCount} variable link name{(replacedLinkCount == 1 ? string.Empty : "s")} " +
                $"in {modifiedExportCount} export{(modifiedExportCount == 1 ? string.Empty : "s")}.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private record VariableLinkEditDialogResult(string EntryName, string ExpectedTypeName);
        private record ActionLinkEditDialogResult(string EntryName, ExportEntry LinkedOp, string LinkActionName, int LinkActionNumber);

        private void PromptAndEditNamedLinkEntryFromGraph(ExportEntry export, string propertyName, int linkIndex)
        {
            if (export == null)
            {
                return;
            }

            if (CurrentObjects.FirstOrDefault(obj => obj.Export == export) is SObj graphObject)
            {
                panToSelection = false;
                CurrentObjects_ListBox.SelectedItems.Clear();
                CurrentObjects_ListBox.SelectedItem = graphObject;
            }

            switch (propertyName)
            {
                case "VariableLinks":
                    EditVariableLinkEntry(export, linkIndex);
                    break;
                case "InputLinks":
                case "OutputLinks":
                    EditInputOrOutputLinkEntry(export, propertyName, linkIndex);
                    break;
            }
        }

        private void EditVariableLinkEntry(ExportEntry export, int linkIndex)
        {
            if (!TryGetEditableNamedLinkStruct(export, "VariableLinks", linkIndex, out var variableLinks, out var variableLink))
            {
                MessageBox.Show(this, "This variable link cannot be edited.", "Sequence Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string currentName = variableLink.GetProp<StrProperty>("LinkDesc")?.Value ?? "Variable";
            string currentTypeName = variableLink.GetProp<ObjectProperty>("ExpectedType")?.ResolveToEntry(Pcc)?.ObjectName.Name
                                     ?? "SeqVar_Object";

            if (ShowVariableLinkEditDialog(currentName, currentTypeName) is { } result)
            {
                variableLink.Properties.AddOrReplaceProp(new StrProperty(result.EntryName, "LinkDesc"));

                var rop = new RelinkerOptionsPackage();
                if (EntryImporter.EnsureClassIsInFile(Pcc, result.ExpectedTypeName, rop) is IEntry expectedType)
                {
                    variableLink.Properties.AddOrReplaceProp(new ObjectProperty(expectedType, "ExpectedType"));
                }

                export.WriteProperty(variableLinks);
                RefreshView();
                EntryImporterExtended.ShowRelinkResultsIfAny(rop);
            }
        }

        private void EditInputOrOutputLinkEntry(ExportEntry export, string propertyName, int linkIndex)
        {
            if (!TryGetEditableNamedLinkStruct(export, propertyName, linkIndex, out var linkArray, out var linkStruct))
            {
                MessageBox.Show(this, "This link cannot be edited.", "Sequence Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string linkKind = propertyName == "InputLinks" ? "input" : "output";
            string currentName = linkStruct.GetProp<StrProperty>("LinkDesc")?.Value
                                 ?? (propertyName == "InputLinks" ? "In" : "Out");
            var currentLinkedOp = linkStruct.GetProp<ObjectProperty>("LinkedOp")?.ResolveToEntry(Pcc) as ExportEntry;
            NameReference currentAction = linkStruct.GetProp<NameProperty>("LinkAction")?.Value ?? new NameReference("None");

            if (ShowActionLinkEditDialog(linkKind, currentName, currentLinkedOp, currentAction) is { } result)
            {
                linkStruct.Properties.AddOrReplaceProp(new StrProperty(result.EntryName, "LinkDesc"));
                linkStruct.Properties.AddOrReplaceProp(new ObjectProperty(result.LinkedOp, "LinkedOp"));
                linkStruct.Properties.AddOrReplaceProp(new NameProperty(
                    new NameReference(result.LinkActionName, result.LinkActionNumber), "LinkAction"));
                export.WriteProperty(linkArray);
                RefreshView();
            }
        }

        private VariableLinkEditDialogResult ShowVariableLinkEditDialog(string currentName, string currentTypeName)
        {
            var classOptions = GlobalUnrealObjectInfo.GetClasses(Pcc.Game).Values
                .Where(x => x.IsA("SequenceVariable", Pcc.Game))
                .OrderBy(x => x.ClassName)
                .ToList();

            ClassInfo selectedType = classOptions.FirstOrDefault(x => x.ClassName == currentTypeName)
                                     ?? classOptions.FirstOrDefault(x => x.ClassName == "SeqVar_Object")
                                     ?? classOptions.FirstOrDefault();

            var nameTextBox = new TextBox
            {
                MinWidth = 260,
                Text = currentName
            };
            var typeTextBox = new TextBox
            {
                MinWidth = 180,
                Text = selectedType?.ClassName ?? string.Empty,
                IsReadOnly = true
            };
            var pickTypeButton = new Button
            {
                Content = "Pick...",
                MinWidth = 72,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                MinWidth = 80,
                Margin = new Thickness(0, 0, 8, 0)
            };
            System.Windows.Window dialog = null;
            dialog = new System.Windows.Window
            {
                Title = "Edit variable link",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(12),
                    Children =
                    {
                        new TextBlock { Text = "Label" },
                        nameTextBox,
                        new TextBlock
                        {
                            Margin = new Thickness(0, 10, 0, 0),
                            Text = "Expected type"
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children =
                            {
                                typeTextBox,
                                pickTypeButton
                            }
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 12, 0, 0),
                            Children =
                            {
                                okButton,
                                new Button
                                {
                                    Content = "Cancel",
                                    IsCancel = true,
                                    MinWidth = 80
                                }
                            }
                        }
                    }
                }
            };

            CustomWindowChrome.ApplyCustomChrome(dialog);

            void UpdateOkState()
            {
                okButton.IsEnabled = !string.IsNullOrWhiteSpace(nameTextBox.Text) && selectedType != null;
            }

            nameTextBox.TextChanged += (_, _) => UpdateOkState();
            pickTypeButton.Click += (_, _) =>
            {
                if (ClassPickerDlg.GetClass(dialog, classOptions, "Select datatype", "Select") is not { } chosenClass)
                {
                    return;
                }

                selectedType = chosenClass;
                typeTextBox.Text = chosenClass.ClassName;
                UpdateOkState();
            };
            typeTextBox.MouseDoubleClick += (_, _) => pickTypeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.Loaded += (_, _) =>
            {
                nameTextBox.Focus();
                nameTextBox.SelectAll();
                UpdateOkState();
            };
            okButton.Click += (_, _) => dialog.DialogResult = true;

            return dialog.ShowDialog() == true
                ? new VariableLinkEditDialogResult(nameTextBox.Text.Trim(), selectedType?.ClassName ?? "SeqVar_Object")
                : null;
        }

        private ActionLinkEditDialogResult ShowActionLinkEditDialog(string linkKind, string currentName,
            ExportEntry currentLinkedOp, NameReference currentAction)
        {
            var nameTextBox = new TextBox
            {
                MinWidth = 260,
                Text = currentName
            };
            ExportEntry selectedLinkedOp = currentLinkedOp;
            var linkedOpTextBox = new TextBox
            {
                MinWidth = 260,
                Text = currentLinkedOp?.InstancedFullPath ?? string.Empty,
                IsReadOnly = true
            };
            var pickLinkedOpButton = new Button
            {
                Content = "Pick...",
                MinWidth = 72,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var clearLinkedOpButton = new Button
            {
                Content = "Clear",
                MinWidth = 72,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var linkActionTextBox = new TextBox
            {
                MinWidth = 200,
                Text = currentAction.Name == "None" ? string.Empty : currentAction.Name
            };
            var linkActionNumberTextBox = new TextBox
            {
                MinWidth = 52,
                Width = 52,
                Margin = new Thickness(8, 0, 0, 0),
                Text = currentAction.Number.ToString()
            };
            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                MinWidth = 80,
                Margin = new Thickness(0, 0, 8, 0)
            };
            System.Windows.Window dialog = null;
            dialog = new System.Windows.Window
            {
                Title = $"Edit {linkKind} link",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(12),
                    Children =
                    {
                        new TextBlock { Text = "Label" },
                        nameTextBox,
                        new TextBlock
                        {
                            Margin = new Thickness(0, 10, 0, 0),
                            Text = "Linked op"
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children =
                            {
                                linkedOpTextBox,
                                pickLinkedOpButton,
                                clearLinkedOpButton
                            }
                        },
                        new TextBlock
                        {
                            Margin = new Thickness(0, 10, 0, 0),
                            Text = "Link action"
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children =
                            {
                                linkActionTextBox,
                                linkActionNumberTextBox
                            }
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 12, 0, 0),
                            Children =
                            {
                                okButton,
                                new Button
                                {
                                    Content = "Cancel",
                                    IsCancel = true,
                                    MinWidth = 80
                                }
                            }
                        }
                    }
                }
            };

            CustomWindowChrome.ApplyCustomChrome(dialog);

            void UpdateOkState()
            {
                okButton.IsEnabled = !string.IsNullOrWhiteSpace(nameTextBox.Text)
                    && int.TryParse(linkActionNumberTextBox.Text, out int number)
                    && number >= 0;
            }

            void RefreshLinkedOpText()
            {
                linkedOpTextBox.Text = selectedLinkedOp?.InstancedFullPath ?? string.Empty;
            }

            void SyncLinkActionToLinkedOp()
            {
                if (selectedLinkedOp != null)
                {
                    linkActionTextBox.Text = selectedLinkedOp.ObjectName.Name;
                    linkActionNumberTextBox.Text = selectedLinkedOp.ObjectName.Number.ToString();
                }
                else
                {
                    linkActionTextBox.Text = string.Empty;
                    linkActionNumberTextBox.Text = "0";
                }
            }

            nameTextBox.TextChanged += (_, _) => UpdateOkState();
            linkActionTextBox.TextChanged += (_, _) => UpdateOkState();
            linkActionNumberTextBox.TextChanged += (_, _) => UpdateOkState();
            pickLinkedOpButton.Click += (_, _) =>
            {
                if (EntrySelector.GetEntry<ExportEntry>(dialog, Pcc, $"Select linked op for {linkKind} link",
                        exp => exp.IsA("Sequence") || exp.IsA("SequenceObject"), selectedLinkedOp) is not { } linkedOp)
                {
                    return;
                }

                selectedLinkedOp = linkedOp;
                RefreshLinkedOpText();
                SyncLinkActionToLinkedOp();
            };
            clearLinkedOpButton.Click += (_, _) =>
            {
                selectedLinkedOp = null;
                RefreshLinkedOpText();
                SyncLinkActionToLinkedOp();
            };
            dialog.Loaded += (_, _) =>
            {
                nameTextBox.Focus();
                nameTextBox.SelectAll();
                UpdateOkState();
                RefreshLinkedOpText();
            };
            okButton.Click += (_, _) => dialog.DialogResult = true;

            if (selectedLinkedOp != null && string.IsNullOrWhiteSpace(linkActionTextBox.Text))
            {
                SyncLinkActionToLinkedOp();
            }

            return dialog.ShowDialog() == true
                ? new ActionLinkEditDialogResult(
                    nameTextBox.Text.Trim(),
                    selectedLinkedOp,
                    string.IsNullOrWhiteSpace(linkActionTextBox.Text) ? "None" : linkActionTextBox.Text.Trim(),
                    int.TryParse(linkActionNumberTextBox.Text, out int number) && number >= 0 ? number : 0)
                : null;
        }

        private bool TryGetEditableNamedLinkStruct(ExportEntry export, string propertyName, int linkIndex,
            out ArrayProperty<StructProperty> linkArray, out StructProperty linkStruct)
        {
            linkArray = GetOrCreateEditableNamedLinkArray(export, propertyName);
            if (linkArray != null && linkIndex >= 0 && linkIndex < linkArray.Count)
            {
                linkStruct = linkArray[linkIndex];
                return true;
            }

            linkStruct = null;
            return false;
        }

        private ArrayProperty<StructProperty> GetOrCreateEditableNamedLinkArray(ExportEntry export, string propertyName)
        {
            if (export == null)
            {
                return null;
            }

            var props = export.GetProperties();
            var linkArray = props.GetProp<ArrayProperty<StructProperty>>(propertyName);
            if (linkArray != null)
            {
                return linkArray;
            }

            using var packageCache = new PackageCache { AlwaysOpenFromDisk = false };
            packageCache.InsertIntoCache(Pcc);
            var defaults = SequenceObjectCreator.GetSequenceObjectDefaults(Pcc, export.ClassName, Pcc.Game, packageCache);
            packageCache.RemoveFromCache(Pcc);

            linkArray = defaults?.GetProp<ArrayProperty<StructProperty>>(propertyName);
            if (linkArray == null)
            {
                return null;
            }

            props.AddOrReplaceProp(linkArray);
            export.WriteProperties(props);
            return export.GetProperty<ArrayProperty<StructProperty>>(propertyName);
        }

        private void RemoveNamedLinkEntry(ExportEntry export, string propertyName, int linkIndex)
        {
            var linkArray = export.GetProperty<ArrayProperty<StructProperty>>(propertyName);
            if (linkArray == null || linkIndex < 0 || linkIndex >= linkArray.Count)
            {
                return;
            }

            switch (propertyName)
            {
                case "InputLinks":
                    RemoveInputLinkEntry(export, linkArray, linkIndex);
                    break;
                case "OutputLinks":
                case "VariableLinks":
                    linkArray.RemoveAt(linkIndex);
                    export.WriteProperty(linkArray);
                    RefreshView();
                    break;
            }
        }

        private void RemoveInputLinkEntry(ExportEntry export, ArrayProperty<StructProperty> inputLinks, int removedIndex)
        {
            foreach (var sequenceObject in Pcc.Exports.Where(exp => exp.GetProperty<ArrayProperty<StructProperty>>("OutputLinks") != null))
            {
                var outputLinks = sequenceObject.GetProperty<ArrayProperty<StructProperty>>("OutputLinks");
                bool modified = false;
                foreach (var outputLink in outputLinks)
                {
                    var links = outputLink.GetProp<ArrayProperty<StructProperty>>("Links");
                    if (links == null)
                    {
                        continue;
                    }

                    for (int i = links.Count - 1; i >= 0; i--)
                    {
                        var linkedOp = links[i].GetProp<ObjectProperty>("LinkedOp");
                        var inputLinkIdx = links[i].GetProp<IntProperty>("InputLinkIdx");
                        if (linkedOp?.Value != export.UIndex || inputLinkIdx == null)
                        {
                            continue;
                        }

                        if (inputLinkIdx.Value == removedIndex)
                        {
                            links.RemoveAt(i);
                            modified = true;
                        }
                        else if (inputLinkIdx.Value > removedIndex)
                        {
                            inputLinkIdx.Value--;
                            modified = true;
                        }
                    }
                }

                if (modified)
                {
                    sequenceObject.WriteProperty(outputLinks);
                }
            }

            inputLinks.RemoveAt(removedIndex);
            export.WriteProperty(inputLinks);
            RefreshView();
        }

        private void PromptAndAddNamedLinkEntry(ExportEntry export, string propertyName, string entryType, string defaultName)
        {
            if (export == null)
            {
                return;
            }

            if (!CanAddNamedLinkEntry(export, propertyName))
            {
                return;
            }

            var entryName = PromptDialog.Prompt(this,
                $"Enter a label for the new {entryType} entry.",
                "Enter label",
                defaultName,
                true);
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return;
            }

            AddNamedLinkEntry(export, propertyName, entryName.Trim());
            RefreshView();
        }

        private void PromptAndAddVariableLinkEntry(ExportEntry export)
        {
            if (export == null || !CanAddNamedLinkEntry(export, "VariableLinks"))
            {
                return;
            }

            if (PromptForVariableLinkEntry() is not { } variableEntry)
            {
                return;
            }

            AddNamedLinkEntry(export, "VariableLinks", variableEntry.EntryName, variableEntry.ExpectedTypeName);
            RefreshView();
        }

        private void PromptAndAddNamedLinkEntryFromGraph(ExportEntry export, string propertyName)
        {
            if (export == null)
            {
                return;
            }

            if (CurrentObjects.FirstOrDefault(obj => obj.Export == export) is SObj graphObject)
            {
                panToSelection = false;
                CurrentObjects_ListBox.SelectedItems.Clear();
                CurrentObjects_ListBox.SelectedItem = graphObject;
            }

            switch (propertyName)
            {
                case "InputLinks":
                    PromptAndAddNamedLinkEntry(export, propertyName, "input", "In");
                    break;
                case "OutputLinks":
                    PromptAndAddNamedLinkEntry(export, propertyName, "output", "Out");
                    break;
                case "VariableLinks":
                    PromptAndAddVariableLinkEntry(export);
                    break;
            }
        }

        private bool CanAddNamedLinkEntry(ExportEntry export, string propertyName)
        {
            return export != null
                   && !export.IsA("SFXSceneShopNode")
                   && GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, propertyName, export.ClassName) != null;
        }

        private void AddNamedLinkEntry(ExportEntry export, string propertyName, string entryName, string expectedTypeName = null)
        {
            var props = export.GetProperties();
            var linkArray = props.GetProp<ArrayProperty<StructProperty>>(propertyName)
                            ?? new ArrayProperty<StructProperty>(propertyName);

            if (CreateNamedLinkStruct(export, propertyName, entryName, expectedTypeName) is not { } linkStruct)
            {
                return;
            }

            linkArray.Add(linkStruct);
            props.AddOrReplaceProp(linkArray);
            export.WriteProperties(props);
        }

        private StructProperty CreateNamedLinkStruct(ExportEntry export, string propertyName, string entryName, string expectedTypeName = null)
        {
            PropertyInfo linkPropertyInfo = GlobalUnrealObjectInfo.GetPropertyInfo(Pcc.Game, propertyName, export.ClassName);
            if (linkPropertyInfo == null)
            {
                return null;
            }

            PropertyCollection linkDefaults = GlobalUnrealObjectInfo.getDefaultStructValue(Pcc.Game, linkPropertyInfo.Reference, true, Pcc);
            linkDefaults.AddOrReplaceProp(new StrProperty(entryName, "LinkDesc"));

            switch (propertyName)
            {
                case "InputLinks":
                    linkDefaults.AddOrReplaceProp(new NameProperty("None", "LinkAction"));
                    linkDefaults.AddOrReplaceProp(new ObjectProperty(0, "LinkedOp"));
                    linkDefaults.AddOrReplaceProp(new IntProperty(0, "QueuedActivations"));
                    linkDefaults.AddOrReplaceProp(new BoolProperty(false, "bHasImpulse"));
                    linkDefaults.AddOrReplaceProp(new BoolProperty(false, "bDisabled"));
                    break;
                case "OutputLinks":
                    linkDefaults.AddOrReplaceProp(new ArrayProperty<StructProperty>("Links"));
                    linkDefaults.AddOrReplaceProp(new NameProperty("None", "LinkAction"));
                    linkDefaults.AddOrReplaceProp(new ObjectProperty(0, "LinkedOp"));
                    linkDefaults.AddOrReplaceProp(new BoolProperty(false, "bHasImpulse"));
                    linkDefaults.AddOrReplaceProp(new BoolProperty(false, "bDisabled"));
                    break;
                case "VariableLinks":
                    linkDefaults.AddOrReplaceProp(new ArrayProperty<ObjectProperty>("LinkedVariables"));
                    linkDefaults.AddOrReplaceProp(new NameProperty("None", "PropertyName"));
                    linkDefaults.AddOrReplaceProp(new IntProperty(0, "MinVars"));
                    linkDefaults.AddOrReplaceProp(new IntProperty(255, "MaxVars"));

                    string resolvedExpectedTypeName = string.IsNullOrWhiteSpace(expectedTypeName)
                        ? "SeqVar_Object"
                        : expectedTypeName;
                    var rop = new RelinkerOptionsPackage();
                    if (EntryImporter.EnsureClassIsInFile(Pcc, resolvedExpectedTypeName, rop) is IEntry expectedType)
                    {
                        linkDefaults.AddOrReplaceProp(new ObjectProperty(expectedType, "ExpectedType"));
                    }
                    EntryImporterExtended.ShowRelinkResultsIfAny(rop);
                    break;
            }

            return new StructProperty(linkPropertyInfo.Reference, linkDefaults, isImmutable: false);
        }

        public void OpenNodeContextMenu(SObj obj)
        {
            if (FindResource("nodeContextMenu") is ContextMenu contextMenu)
            {
                if (contextMenu.GetChild("addLinkEntryMenuItem") is MenuItem addLinkEntryMenuItem)
                {
                    bool canAddInput = CanAddNamedLinkEntry(obj.Export, "InputLinks");
                    bool canAddOutput = CanAddNamedLinkEntry(obj.Export, "OutputLinks");
                    bool canAddVariable = CanAddNamedLinkEntry(obj.Export, "VariableLinks");

                    if (addLinkEntryMenuItem.GetChild("addInputEntryMenuItem") is MenuItem addInputEntryMenuItem)
                    {
                        addInputEntryMenuItem.Visibility = canAddInput ? Visibility.Visible : Visibility.Collapsed;
                    }

                    if (addLinkEntryMenuItem.GetChild("addOutputEntryMenuItem") is MenuItem addOutputEntryMenuItem)
                    {
                        addOutputEntryMenuItem.Visibility = canAddOutput ? Visibility.Visible : Visibility.Collapsed;
                    }

                    if (addLinkEntryMenuItem.GetChild("addVariableEntryMenuItem") is MenuItem addVariableEntryMenuItem)
                    {
                        addVariableEntryMenuItem.Visibility = canAddVariable ? Visibility.Visible : Visibility.Collapsed;
                    }

                    addLinkEntryMenuItem.Visibility = canAddInput || canAddOutput || canAddVariable
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                // BREAK LINKS CODE
                if (contextMenu.GetChild("breakLinksMenuItem") is MenuItem breakLinksMenuItem)
                {
                    if (obj is SBox sBox && (sBox.Varlinks.Any() || sBox.Outlinks.Any() || sBox.EventLinks.Any()))
                    {
                        bool hasLinks = false;
                        if (breakLinksMenuItem.GetChild("outputLinksMenuItem") is MenuItem outputLinksMenuItem)
                        {
                            outputLinksMenuItem.Visibility = Visibility.Collapsed;
                            outputLinksMenuItem.Items.Clear();
                            for (int i = 0; i < sBox.Outlinks.Count; i++)
                            {
                                for (int j = 0; j < sBox.Outlinks[i].Links.Count; j++)
                                {
                                    outputLinksMenuItem.Visibility = Visibility.Visible;
                                    hasLinks = true;
                                    string targetStr = null;
                                    if (Pcc.TryGetEntry(sBox.Outlinks[i].Links[j], out var target))
                                    {
                                        targetStr = target.ObjectName.Instanced;
                                    }

                                    var temp = new MenuItem
                                    {
                                        Header =
                                            $"Break link from {sBox.Outlinks[i].Desc} to {sBox.Outlinks[i].Links[j]} {targetStr}"
                                    };
                                    int linkConnection = i;
                                    int linkIndex = j;
                                    temp.Click += (o, args) => { sBox.RemoveOutlink(linkConnection, linkIndex); };
                                    outputLinksMenuItem.Items.Add(temp);
                                }
                            }

                            if (outputLinksMenuItem.Items.Count > 0)
                            {
                                var temp = new MenuItem { Header = "Break All", Tag = obj.Export };
                                temp.Click += removeAllOutputLinks;
                                outputLinksMenuItem.Items.Add(temp);
                            }
                        }

                        if (breakLinksMenuItem.GetChild("varLinksMenuItem") is MenuItem varLinksMenuItem)
                        {
                            varLinksMenuItem.Visibility = Visibility.Collapsed;
                            varLinksMenuItem.Items.Clear();
                            for (int i = 0; i < sBox.Varlinks.Count; i++)
                            {
                                for (int j = 0; j < sBox.Varlinks[i].Links.Count; j++)
                                {
                                    varLinksMenuItem.Visibility = Visibility.Visible;
                                    hasLinks = true;

                                    string targetStr = null;
                                    if (Pcc.TryGetEntry(sBox.Varlinks[i].Links[j], out var target))
                                    {
                                        targetStr = target.ObjectName.Instanced;
                                    }

                                    var temp = new MenuItem
                                    {
                                        Header =
                                            $"Break link from {sBox.Varlinks[i].Desc} to {sBox.Varlinks[i].Links[j]} {targetStr}"
                                    };

                                    int linkConnection = i;
                                    int linkIndex = j;
                                    temp.Click += (o, args) => { sBox.RemoveVarlink(linkConnection, linkIndex); };
                                    varLinksMenuItem.Items.Add(temp);
                                }
                            }

                            if (varLinksMenuItem.Items.Count > 0)
                            {
                                var temp = new MenuItem { Header = "Break All", Tag = obj.Export };
                                temp.Click += removeAllVarLinks;
                                varLinksMenuItem.Items.Add(temp);
                            }
                        }

                        if (breakLinksMenuItem.GetChild("eventLinksMenuItem") is MenuItem eventLinksMenuItem)
                        {
                            eventLinksMenuItem.Visibility = Visibility.Collapsed;
                            eventLinksMenuItem.Items.Clear();
                            for (int i = 0; i < sBox.EventLinks.Count; i++)
                            {
                                for (int j = 0; j < sBox.EventLinks[i].Links.Count; j++)
                                {
                                    eventLinksMenuItem.Visibility = Visibility.Visible;
                                    hasLinks = true;
                                    var temp = new MenuItem
                                    {
                                        Header =
                                            $"Break link from {sBox.EventLinks[i].Desc} to {sBox.EventLinks[i].Links[j]}"
                                    };
                                    int linkConnection = i;
                                    int linkIndex = j;
                                    temp.Click += (o, args) => { sBox.RemoveEventlink(linkConnection, linkIndex); };
                                    eventLinksMenuItem.Items.Add(temp);
                                }
                            }

                            if (eventLinksMenuItem.Items.Count > 0)
                            {
                                var temp = new MenuItem { Header = "Break All", Tag = obj.Export };
                                temp.Click += removeAllEventLinks;
                                eventLinksMenuItem.Items.Add(temp);
                            }
                        }

                        if (breakLinksMenuItem.GetChild("breakAllLinksMenuItem") is MenuItem breakAllLinksMenuItem)
                        {
                            if (hasLinks)
                            {
                                breakLinksMenuItem.Visibility = Visibility.Visible;
                                breakAllLinksMenuItem.Visibility = Visibility.Visible;
                                breakAllLinksMenuItem.Tag = obj.Export;
                            }
                            else
                            {
                                breakLinksMenuItem.Visibility = Visibility.Collapsed;
                                breakAllLinksMenuItem.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                    else
                    {
                        breakLinksMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                // SKIP SEQ OBJECT CODE
                if (contextMenu.GetChild("skipObjMenuItem") is MenuItem skipObjMenuItem)
                {
                    if (obj is SBox sBox && sBox.Outlinks.Any())
                    {
                        // TODO: LIMIT TO SINGLE INPUT CAUSE IT DOESN'T REALLY WORK
                        // WITH MULTIPLE 
                        bool hasLinks = false;
                        skipObjMenuItem.Visibility = Visibility.Collapsed;
                        skipObjMenuItem.Items.Clear();
                        for (int i = 0; i < sBox.Outlinks.Count; i++)
                        {
                            skipObjMenuItem.Visibility = Visibility.Visible;
                            hasLinks = true;
                            var temp = new MenuItem
                            {
                                Header = $"Use {sBox.Outlinks[i].Desc} as skipped path"
                            };
                            int linkConnection = i;
                            temp.Click += (o, args) =>
                            {
                                KismetHelper.SkipSequenceElement(obj.Export, outboundLinkIdx: linkConnection);
                            };
                            skipObjMenuItem.Items.Add(temp);
                        }
                    }
                    else
                    {
                        skipObjMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("interpViewerMenuItem") is MenuItem interpViewerMenuItem)
                {
                    string className = obj.Export.ClassName;
                    if (className == "InterpData"
                        || (className == "SeqAct_Interp" && obj is SAction action && action.Varlinks.Any() &&
                            action.Varlinks[0].Links.Any()
                            && Pcc.IsUExport(action.Varlinks[0].Links[0]) &&
                            Pcc.GetUExport(action.Varlinks[0].Links[0]).ClassName == "InterpData"))
                    {
                        interpViewerMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        interpViewerMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("cloneInterpDataMenuItem") is MenuItem cloneInterpDataMenuItem)
                {
                    string className = obj.Export.ClassName;
                    if (className == "InterpData")
                    {
                        cloneInterpDataMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        cloneInterpDataMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("bulkEditInterpGroupsMenuItem") is MenuItem bulkEditInterpGroupsMenuItem)
                {
                    string className = obj.Export.ClassName;
                    if (className == "InterpData"
                        || (className == "SeqAct_Interp" && obj is SAction action && action.Varlinks.Any() &&
                            action.Varlinks[0].Links.Any()
                            && Pcc.IsUExport(action.Varlinks[0].Links[0]) &&
                            Pcc.GetUExport(action.Varlinks[0].Links[0]).ClassName == "InterpData"))
                    {
                        bulkEditInterpGroupsMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        bulkEditInterpGroupsMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("cameraPresetsMenuItem") is MenuItem cameraPresetsMenuItem)
                {
                    cameraPresetsMenuItem.Visibility = TryResolveInterpData(obj, out _)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                if (contextMenu.GetChild("plotEditorMenuItem") is MenuItem plotEditorMenuItem)
                {
                    if (obj is SAction sAction &&
                        sAction.Export.ClassName == "BioSeqAct_PMExecuteTransition" &&
                        sAction.Export.GetProperty<IntProperty>("m_nIndex") != null)
                    {
                        plotEditorMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        plotEditorMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("conditionalsEditorMenuItem") is MenuItem conditionalsEditorMenuItem)
                {
                    if (Pcc.Game.IsGame3() && obj is SAction sAction &&
                        sAction.Export.ClassName == "BioSeqAct_PMCheckConditional" &&
                        sAction.Export.GetProperty<IntProperty>("m_nIndex") != null)
                    {
                        conditionalsEditorMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        conditionalsEditorMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("dialogueEditorMenuItem") is MenuItem dialogueEditorMenuItem)
                {
                    if (obj is SAction sAction &&
                        (sAction.Export.ClassName.EndsWith("SeqAct_StartConversation") ||
                         sAction.Export.ClassName.EndsWith("StartAmbientConv")) &&
                        sAction.Export.GetProperty<ObjectProperty>("Conv") != null)
                    {
                        dialogueEditorMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        dialogueEditorMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("openRefInPackEdMenuItem") is MenuItem openRefInPackEdMenuItem)
                {
                    if (Pcc.Game.IsGame3() && obj is SVar sVar &&
                        Pcc.IsEntry(sVar.Export.GetProperty<ObjectProperty>("ObjValue")?.Value ?? 0))
                    {
                        openRefInPackEdMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        openRefInPackEdMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("gestureAnimationImporterMenuItem") is MenuItem gestureAnimationImporterMenuItem)
                {
                    if (obj.Export.ClassName == "SFXSeqAct_SetAmbientPerformance")
                    {
                        gestureAnimationImporterMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        gestureAnimationImporterMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("repointIncomingReferences") is MenuItem repointIncomingReferences)
                {
                    if (obj is SVar sVar)
                    {
                        repointIncomingReferences.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        repointIncomingReferences.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("sequenceRefGotoMenuItem") is MenuItem sequenceRefGotoMenuItem)
                {
                    if (obj is SAction sAction && sAction.Export != null &&
                        (sAction.Export.ClassName is "SequenceReference" or "Sequence"
                         || SequenceExports.Contains(sAction.Export)))
                    {
                        sequenceRefGotoMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        sequenceRefGotoMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("extractSequenceMenuItem") is MenuItem extractSequenceMenuItem)
                {
#if DEBUG
                    if (obj is SAction sAction && sAction.Export != null &&
                        (sAction.Export.ClassName is "SequenceReference" or "Sequence"))
                    {
                        extractSequenceMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        extractSequenceMenuItem.Visibility = Visibility.Collapsed;
                    }
#endif
                }

                if (contextMenu.GetChild("trimSequenceVariablesMenuItem") is MenuItem trimVariableLinksMenuItem)
                {
#if DEBUG
                    if (obj.Export != null && (obj is SAction sAction || obj is SEvent))
                    {
                        trimVariableLinksMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        trimVariableLinksMenuItem.Visibility = Visibility.Collapsed;
                    }
#endif
                }

                if (contextMenu.GetChild("seqLogAddItemMenuItem") is MenuItem seqLogAddItemMenuItem)
                {
                    if (obj is SAction sAction && sAction.Export != null && sAction.Export.ClassName == "SeqAct_Log")
                    {
                        seqLogAddItemMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        seqLogAddItemMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("seqLogLogObjectMenuItem") is MenuItem seqLogLogObjectMenuItem)
                {
                    if (obj is SVar sVar && sVar.Export != null)
                    {
                        seqLogLogObjectMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        seqLogLogObjectMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("seqLogLogOutlinkFiringMenuItem") is MenuItem seqLogLogOutlinkFiringMenuItem)
                {
                    if (obj is SBox sAction && sAction.Export != null && sAction.Outlinks.Any())
                    {
                        seqLogLogOutlinkFiringMenuItem.Visibility = Visibility.Visible;

                        seqLogLogOutlinkFiringMenuItem.Items.Clear();
                        for (int i = 0; i < sAction.Outlinks.Count; i++)
                        {
                            int tempIdx = i; // Captured
                            var temp = new MenuItem
                            {
                                Header = $"Log when {sAction.Outlinks[i].Desc} fires"
                            };
                            temp.Click += (o, args) => { SeqLogLogOutlink(sAction, sAction.Outlinks[tempIdx].Desc); };
                            seqLogLogOutlinkFiringMenuItem.Items.Add(temp);
                        }
                    }
                    else
                    {
                        seqLogLogOutlinkFiringMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("addSwitchOutlinksMenuItem") is MenuItem addSwitchOutlinksMenuItem)
                {
                    if (obj is SAction sAction && sAction.Export != null &&
                        sAction.Export.Class.InheritsFrom("SeqAct_Switch"))
                    {
                        addSwitchOutlinksMenuItem.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        addSwitchOutlinksMenuItem.Visibility = Visibility.Collapsed;
                    }
                }

                if (contextMenu.GetChild("copyConnectionsMenuItem") is MenuItem copyConnectionsMenuItem)
                {
                    bool canCopyInput = obj is SAction;
                    bool canCopyOutput = obj is SBox;
                    bool canCopyVariable = obj is SBox;
                    bool canCopyAll = canCopyInput || canCopyOutput || canCopyVariable;

                    if (copyConnectionsMenuItem.GetChild("copyAllConnectionsMenuItem") is MenuItem copyAllConnectionsMenuItem)
                    {
                        copyAllConnectionsMenuItem.Visibility = canCopyAll ? Visibility.Visible : Visibility.Collapsed;
                    }

                    if (copyConnectionsMenuItem.GetChild("copyInputConnectionsMenuItem") is MenuItem copyInputConnectionsMenuItem)
                    {
                        copyInputConnectionsMenuItem.Visibility = canCopyInput ? Visibility.Visible : Visibility.Collapsed;
                    }

                    if (copyConnectionsMenuItem.GetChild("copyOutputConnectionsMenuItem") is MenuItem copyOutputConnectionsMenuItem)
                    {
                        copyOutputConnectionsMenuItem.Visibility = canCopyOutput ? Visibility.Visible : Visibility.Collapsed;
                    }

                    if (copyConnectionsMenuItem.GetChild("copyVariableConnectionsMenuItem") is MenuItem copyVariableConnectionsMenuItem)
                    {
                        copyVariableConnectionsMenuItem.Visibility = canCopyVariable ? Visibility.Visible : Visibility.Collapsed;
                    }

                    copyConnectionsMenuItem.Visibility = canCopyAll
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                if (contextMenu.GetChild("pasteConnectionsMenuItem") is MenuItem pasteConnectionsMenuItem)
                {
                    bool canPasteInput = obj is SAction;
                    bool canPasteOutput = obj is SBox;
                    bool canPasteVariable = obj is SBox;
                    bool canPasteAll = obj is SAction || obj is SBox;
                    bool inputPasteAvailable = canPasteInput && copiedInputConnections != null &&
                                               IsCopiedConnectionsFromCurrentPackage(copiedInputConnectionsSourceFilePath);
                    bool outputPasteAvailable = canPasteOutput && copiedOutputConnections != null &&
                                                IsCopiedConnectionsFromCurrentPackage(copiedOutputConnectionsSourceFilePath);
                    bool variablePasteAvailable = canPasteVariable && copiedVariableConnections != null &&
                                                  IsCopiedConnectionsFromCurrentPackage(copiedVariableConnectionsSourceFilePath);
                    bool allPasteAvailable = canPasteAll && copiedAllConnections != null &&
                                             IsCopiedConnectionsFromCurrentPackage(copiedAllConnections.SourceFilePath);

                    if (pasteConnectionsMenuItem.GetChild("pasteAllConnectionsMenuItem") is MenuItem pasteAllConnectionsMenuItem)
                    {
                        pasteAllConnectionsMenuItem.Visibility = canPasteAll ? Visibility.Visible : Visibility.Collapsed;
                        pasteAllConnectionsMenuItem.IsEnabled = allPasteAvailable;
                    }

                    if (pasteConnectionsMenuItem.GetChild("pasteInputConnectionsMenuItem") is MenuItem pasteInputConnectionsMenuItem)
                    {
                        pasteInputConnectionsMenuItem.Visibility = canPasteInput ? Visibility.Visible : Visibility.Collapsed;
                        pasteInputConnectionsMenuItem.IsEnabled = inputPasteAvailable;
                    }

                    if (pasteConnectionsMenuItem.GetChild("pasteOutputConnectionsMenuItem") is MenuItem pasteOutputConnectionsMenuItem)
                    {
                        pasteOutputConnectionsMenuItem.Visibility = canPasteOutput ? Visibility.Visible : Visibility.Collapsed;
                        pasteOutputConnectionsMenuItem.IsEnabled = outputPasteAvailable;
                    }

                    if (pasteConnectionsMenuItem.GetChild("pasteVariableConnectionsMenuItem") is MenuItem pasteVariableConnectionsMenuItem)
                    {
                        pasteVariableConnectionsMenuItem.Visibility = canPasteVariable ? Visibility.Visible : Visibility.Collapsed;
                        pasteVariableConnectionsMenuItem.IsEnabled = variablePasteAvailable;
                    }

                    pasteConnectionsMenuItem.Visibility = canPasteAll
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    pasteConnectionsMenuItem.IsEnabled = inputPasteAvailable || outputPasteAvailable || variablePasteAvailable || allPasteAvailable;
                }

                contextMenu.IsOpen = true;
                graphEditor.DisableDragging();
            }
        }

        private bool TryResolveInterpData(SObj obj, out ExportEntry interpData)
        {
            interpData = null;
            if (obj?.Export?.ClassName == "InterpData")
            {
                interpData = obj.Export;
                return true;
            }

            if (obj is not SAction { Export.ClassName: "SeqAct_Interp" } action
                || action.Varlinks.Count == 0
                || action.Varlinks[0].Links.Count == 0
                || !Pcc.TryGetUExport(action.Varlinks[0].Links[0], out ExportEntry linkedExport)
                || linkedExport.ClassName != "InterpData")
            {
                return false;
            }

            interpData = linkedExport;
            return true;
        }

        private bool TryGetSelectedInterpData(out ExportEntry interpData) =>
            TryResolveInterpData(CurrentObjects_ListBox.SelectedItem as SObj, out interpData);

        private bool IsCopiedConnectionsFromCurrentPackage(string sourceFilePath)
        {
            return string.Equals(sourceFilePath, Pcc?.FilePath, StringComparison.InvariantCultureIgnoreCase);
        }

        private List<CopiedInputConnection> GetCopiedInputConnections(SAction action)
        {
            return action.InputEdges
                .Select(edge => TryGetOutputLinkInfo(edge.Originator, edge, out int outputIndex, out string outputDescription)
                    ? new CopiedInputConnection(edge.Originator.Export.UIndex, outputDescription, outputIndex, edge.InputIndex)
                    : null)
                .Where(connection => connection != null)
                .ToList();
        }

        private static List<CopiedOutputConnection> GetCopiedOutputConnections(SBox box)
        {
            return box.Outlinks
                .SelectMany((link, outputIndex) => link.Links.Select((targetUIndex, linkIndex) =>
                    new CopiedOutputConnection(link.Desc, outputIndex, targetUIndex,
                        linkIndex < link.InputIndices.Count ? link.InputIndices[linkIndex] : 0)))
                .ToList();
        }

        private static List<CopiedVariableConnection> GetCopiedVariableConnections(SBox box)
        {
            return box.Varlinks
                .SelectMany((link, variableIndex) => link.Links.Select(targetUIndex =>
                    new CopiedVariableConnection(link.Desc, variableIndex, targetUIndex)))
                .ToList();
        }

        private static bool TryGetOutputLinkInfo(SBox sourceBox, ActionEdge edge, out int outputIndex, out string outputDescription)
        {
            for (int i = 0; i < sourceBox.Outlinks.Count; i++)
            {
                for (int j = 0; j < sourceBox.Outlinks[i].Edges.Count; j++)
                {
                    if (ReferenceEquals(sourceBox.Outlinks[i].Edges[j], edge))
                    {
                        outputIndex = i;
                        outputDescription = sourceBox.Outlinks[i].Desc;
                        return true;
                    }
                }
            }

            outputIndex = -1;
            outputDescription = null;
            return false;
        }

        private static bool TryGetNamedLinkStruct(ArrayProperty<StructProperty> links, int linkIndex, string linkDescription,
            Func<StructProperty, string> descriptionSelector, out StructProperty linkStruct)
        {
            if (links != null && linkIndex >= 0 && linkIndex < links.Count)
            {
                var indexedLink = links[linkIndex];
                if (string.Equals(descriptionSelector(indexedLink), linkDescription, StringComparison.Ordinal))
                {
                    linkStruct = indexedLink;
                    return true;
                }
            }

            linkStruct = links?.FirstOrDefault(link =>
                string.Equals(descriptionSelector(link), linkDescription, StringComparison.Ordinal));
            return linkStruct != null;
        }

        private bool TryAddOutputConnection(ExportEntry sourceExport, string outputDescription, int outputIndex, ExportEntry targetExport, int inputIndex)
        {
            if (sourceExport.IsA("SFXSceneShopNode"))
            {
                var outputPins = sourceExport.GetProperty<ArrayProperty<StructProperty>>("m_aOutputPins");
                if (!TryGetNamedLinkStruct(outputPins, outputIndex, outputDescription,
                        pin => pin.GetProp<StrProperty>("sLinkName")?.Value ?? "Pin", out var outputPin))
                {
                    return false;
                }

                var pinLinks = outputPin.GetProp<ArrayProperty<StructProperty>>("aLinks")
                               ?? new ArrayProperty<StructProperty>("aLinks");
                pinLinks.Add(new StructProperty("SFXSSNodePinLink", false,
                    new ObjectProperty(targetExport, "pLinkedNode"),
                    new IntProperty(inputIndex, "nLinkedIndex")));
                outputPin.Properties.AddOrReplaceProp(pinLinks);
                sourceExport.WriteProperty(outputPins);
                return true;
            }

            var outputLinks = sourceExport.GetProperty<ArrayProperty<StructProperty>>("OutputLinks");
            if (!TryGetNamedLinkStruct(outputLinks, outputIndex, outputDescription,
                    link => link.GetProp<StrProperty>("LinkDesc")?.Value, out var outputLink))
            {
                return false;
            }

            var links = outputLink.GetProp<ArrayProperty<StructProperty>>("Links")
                        ?? new ArrayProperty<StructProperty>("Links");
            links.Add(new StructProperty("SeqOpOutputInputLink", false,
                new ObjectProperty(targetExport, "LinkedOp"),
                new IntProperty(inputIndex, "InputLinkIdx")));
            outputLink.Properties.AddOrReplaceProp(links);
            sourceExport.WriteProperty(outputLinks);
            return true;
        }

        private bool TryAddVariableConnection(ExportEntry sourceExport, string variableDescription, int variableIndex, ExportEntry targetExport)
        {
            if (sourceExport.IsA("SFXSceneShopNode"))
            {
                if (variableDescription == "Scene")
                {
                    sourceExport.WriteProperty(new ObjectProperty(targetExport, "m_pLinkedScene"));
                    return true;
                }

                return false;
            }

            var variableLinks = sourceExport.GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
            if (!TryGetNamedLinkStruct(variableLinks, variableIndex, variableDescription,
                    link => link.GetProp<StrProperty>("LinkDesc")?.Value, out var variableLink))
            {
                return false;
            }

            var links = variableLink.GetProp<ArrayProperty<ObjectProperty>>("LinkedVariables")
                        ?? new ArrayProperty<ObjectProperty>("LinkedVariables");
            links.Add(new ObjectProperty(targetExport));
            variableLink.Properties.AddOrReplaceProp(links);
            sourceExport.WriteProperty(variableLinks);
            return true;
        }

        private void ClearAllOutputConnections(ExportEntry export)
        {
            if (export.IsA("SFXSceneShopNode"))
            {
                removeAllSFXSceneShopPinLinks(export, "m_aOutputPins");
                return;
            }

            var outLinksProp = export.GetProperty<ArrayProperty<StructProperty>>("OutputLinks");
            if (outLinksProp == null)
            {
                return;
            }

            foreach (var prop in outLinksProp)
            {
                prop.GetProp<ArrayProperty<StructProperty>>("Links")?.Clear();
            }

            export.WriteProperty(outLinksProp);
        }

        private void ClearAllVariableConnections(ExportEntry export)
        {
            if (export.IsA("SFXSceneShopNode"))
            {
                if (export.GetProperty<ObjectProperty>("m_pLinkedScene") != null)
                {
                    export.WriteProperty(new ObjectProperty(0, "m_pLinkedScene"));
                }

                return;
            }

            var varLinksProp = export.GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
            if (varLinksProp == null)
            {
                return;
            }

            foreach (var prop in varLinksProp)
            {
                prop.GetProp<ArrayProperty<ObjectProperty>>("LinkedVariables")?.Clear();
            }

            export.WriteProperty(varLinksProp);
        }

        private (int pastedCount, int skippedCount) ApplyCopiedInputConnections(SAction action, List<CopiedInputConnection> inputConnections)
        {
            ClearAllIncomingConnections(action);

            int pastedCount = 0;
            int skippedCount = 0;
            foreach (var connection in inputConnections)
            {
                if (!Pcc.TryGetUExport(connection.SourceUIndex, out var sourceExport)
                    || !TryAddOutputConnection(sourceExport, connection.OutputDescription, connection.OutputIndex,
                        action.Export, connection.InputIndex))
                {
                    skippedCount++;
                    continue;
                }

                pastedCount++;
            }

            return (pastedCount, skippedCount);
        }

        private (int pastedCount, int skippedCount) ApplyCopiedOutputConnections(SBox box, List<CopiedOutputConnection> outputConnections)
        {
            ClearAllOutputConnections(box.Export);

            int pastedCount = 0;
            int skippedCount = 0;
            foreach (var connection in outputConnections)
            {
                if (!Pcc.TryGetUExport(connection.TargetUIndex, out var targetExport)
                    || !TryAddOutputConnection(box.Export, connection.OutputDescription, connection.OutputIndex,
                        targetExport, connection.InputIndex))
                {
                    skippedCount++;
                    continue;
                }

                pastedCount++;
            }

            return (pastedCount, skippedCount);
        }

        private (int pastedCount, int skippedCount) ApplyCopiedVariableConnections(SBox box, List<CopiedVariableConnection> variableConnections)
        {
            ClearAllVariableConnections(box.Export);

            int pastedCount = 0;
            int skippedCount = 0;
            foreach (var connection in variableConnections)
            {
                if (!Pcc.TryGetUExport(connection.TargetUIndex, out var targetExport)
                    || !TryAddVariableConnection(box.Export, connection.VariableDescription, connection.VariableIndex, targetExport))
                {
                    skippedCount++;
                    continue;
                }

                pastedCount++;
            }

            return (pastedCount, skippedCount);
        }

        private static void ClearAllIncomingConnections(SAction action)
        {
            foreach (var edge in action.InputEdges.ToList())
            {
                edge.Originator.RemoveOutlink(edge);
            }
        }

        private void CopyInputConnections_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SAction action)
            {
                return;
            }

            copiedInputConnections = GetCopiedInputConnections(action);
            copiedInputConnectionsSourceFilePath = Pcc?.FilePath;
            StatusText = $"Copied {copiedInputConnections.Count} input connection(s).";
        }

        private void CopyOutputConnections_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SBox box)
            {
                return;
            }

            copiedOutputConnections = GetCopiedOutputConnections(box);
            copiedOutputConnectionsSourceFilePath = Pcc?.FilePath;
            StatusText = $"Copied {copiedOutputConnections.Count} output connection(s).";
        }

        private void CopyVariableConnections_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SBox box)
            {
                return;
            }

            copiedVariableConnections = GetCopiedVariableConnections(box);
            copiedVariableConnectionsSourceFilePath = Pcc?.FilePath;
            StatusText = $"Copied {copiedVariableConnections.Count} variable connection(s).";
        }

        private void CopyAllConnections_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SObj obj)
            {
                return;
            }

            var inputConnections = obj is SAction action ? GetCopiedInputConnections(action) : [];
            var outputConnections = obj is SBox box ? GetCopiedOutputConnections(box) : [];
            var variableConnections = obj is SBox variableBox ? GetCopiedVariableConnections(variableBox) : [];
            var sourceFilePath = Pcc?.FilePath;

            copiedInputConnections = inputConnections;
            copiedInputConnectionsSourceFilePath = sourceFilePath;
            copiedOutputConnections = outputConnections;
            copiedOutputConnectionsSourceFilePath = sourceFilePath;
            copiedVariableConnections = variableConnections;
            copiedVariableConnectionsSourceFilePath = sourceFilePath;
            copiedAllConnections = new CopiedConnectionSet(inputConnections, outputConnections, variableConnections, sourceFilePath);

            StatusText = $"Copied {inputConnections.Count + outputConnections.Count + variableConnections.Count} total connection(s).";
        }

        private void PasteInputConnections_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SAction action)
            {
                return;
            }

            if (copiedInputConnections == null)
            {
                MessageBox.Show(this, "No input connections have been copied yet.", "Sequence Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsCopiedConnectionsFromCurrentPackage(copiedInputConnectionsSourceFilePath))
            {
                MessageBox.Show(this, "Input connections can only be pasted into the package they were copied from.",
                    "Sequence Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (pastedCount, skippedCount) = ApplyCopiedInputConnections(action, copiedInputConnections);

            RefreshView();
            StatusText = skippedCount == 0
                ? $"Pasted {pastedCount} input connection(s)."
                : $"Pasted {pastedCount} input connection(s). Skipped {skippedCount}.";
        }

        private void PasteOutputConnections_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SBox box)
            {
                return;
            }

            if (copiedOutputConnections == null)
            {
                MessageBox.Show(this, "No output connections have been copied yet.", "Sequence Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsCopiedConnectionsFromCurrentPackage(copiedOutputConnectionsSourceFilePath))
            {
                MessageBox.Show(this, "Output connections can only be pasted into the package they were copied from.",
                    "Sequence Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (pastedCount, skippedCount) = ApplyCopiedOutputConnections(box, copiedOutputConnections);

            RefreshView();
            StatusText = skippedCount == 0
                ? $"Pasted {pastedCount} output connection(s)."
                : $"Pasted {pastedCount} output connection(s). Skipped {skippedCount}.";
        }

        private void PasteVariableConnections_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SBox box)
            {
                return;
            }

            if (copiedVariableConnections == null)
            {
                MessageBox.Show(this, "No variable connections have been copied yet.", "Sequence Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsCopiedConnectionsFromCurrentPackage(copiedVariableConnectionsSourceFilePath))
            {
                MessageBox.Show(this, "Variable connections can only be pasted into the package they were copied from.",
                    "Sequence Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (pastedCount, skippedCount) = ApplyCopiedVariableConnections(box, copiedVariableConnections);

            RefreshView();
            StatusText = skippedCount == 0
                ? $"Pasted {pastedCount} variable connection(s)."
                : $"Pasted {pastedCount} variable connection(s). Skipped {skippedCount}.";
        }

        private void PasteAllConnections_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SObj obj)
            {
                return;
            }

            if (copiedAllConnections == null)
            {
                MessageBox.Show(this, "No full connection set has been copied yet.", "Sequence Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!IsCopiedConnectionsFromCurrentPackage(copiedAllConnections.SourceFilePath))
            {
                MessageBox.Show(this, "Connections can only be pasted into the package they were copied from.",
                    "Sequence Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int pastedCount = 0;
            int skippedCount = 0;

            if (obj is SAction action)
            {
                var (inputPasted, inputSkipped) = ApplyCopiedInputConnections(action, copiedAllConnections.InputConnections);
                pastedCount += inputPasted;
                skippedCount += inputSkipped;
            }

            if (obj is SBox box)
            {
                var (outputPasted, outputSkipped) = ApplyCopiedOutputConnections(box, copiedAllConnections.OutputConnections);
                pastedCount += outputPasted;
                skippedCount += outputSkipped;

                var (variablePasted, variableSkipped) = ApplyCopiedVariableConnections(box, copiedAllConnections.VariableConnections);
                pastedCount += variablePasted;
                skippedCount += variableSkipped;
            }

            RefreshView();
            StatusText = skippedCount == 0
                ? $"Pasted {pastedCount} total connection(s)."
                : $"Pasted {pastedCount} total connection(s). Skipped {skippedCount}.";
        }

        private void removeAllLinks(object sender, RoutedEventArgs args)
        {
            ExportEntry export = (ExportEntry)((MenuItem)sender).Tag;
            if (export.IsA("SFXSceneShopNode"))
            {
                removeAllSFXSceneShopPinLinks(export, "m_aOutputPins");
                removeAllSFXSceneShopPinLinks(export, "m_aInputPins");
                // Also clear object property references used as var links
                if (export.GetProperty<ObjectProperty>("m_pLinkedScene") != null)
                {
                    export.WriteProperty(new ObjectProperty(0, "m_pLinkedScene"));
                }
            }
            else
            {
                KismetHelper.RemoveAllLinks(export);
            }
        }

        private void removeAllOutputLinks(object sender, RoutedEventArgs args)
        {
            ExportEntry export = (ExportEntry)((MenuItem)sender).Tag;
            ClearAllOutputConnections(export);
        }

        private static void removeAllSFXSceneShopPinLinks(ExportEntry export, string pinPropertyName)
        {
            var pins = export.GetProperty<ArrayProperty<StructProperty>>(pinPropertyName);
            if (pins != null)
            {
                foreach (var pin in pins)
                {
                    var links = pin.GetProp<ArrayProperty<StructProperty>>("aLinks");
                    links?.Clear();
                }
                export.WriteProperty(pins);
            }
        }

        private void removeAllVarLinks(object sender, RoutedEventArgs args)
        {
            ExportEntry export = (ExportEntry)((MenuItem)sender).Tag;
            if (export.IsA("SFXSceneShopNode"))
            {
                // Clear object property references used as var links
                if (export.GetProperty<ObjectProperty>("m_pLinkedScene") != null)
                {
                    export.WriteProperty(new ObjectProperty(0, "m_pLinkedScene"));
                }
                return;
            }
            var varLinksProp = export.GetProperty<ArrayProperty<StructProperty>>("VariableLinks");
            if (varLinksProp != null)
            {
                foreach (var prop in varLinksProp)
                {
                    prop.GetProp<ArrayProperty<ObjectProperty>>("LinkedVariables")?.Clear();
                }

                export.WriteProperty(varLinksProp);
            }
        }

        private void removeAllEventLinks(object sender, RoutedEventArgs args)
        {
            ExportEntry export = (ExportEntry)((MenuItem)sender).Tag;
            var eventLinksProp = export.GetProperty<ArrayProperty<StructProperty>>("EventLinks");
            if (eventLinksProp != null)
            {
                foreach (var prop in eventLinksProp)
                {
                    prop.GetProp<ArrayProperty<ObjectProperty>>("LinkedEvents")?.Clear();
                }

                export.WriteProperty(eventLinksProp);
            }
        }

        private void RemoveFromSequence_Click(object sender, RoutedEventArgs e)
        {
            RemoveFromSequence(false);
        }

        private void TrashAndRemoveFromSequence_Click(object sender, RoutedEventArgs e)
        {
            RemoveFromSequence(true);
        }

        /// <summary>
        /// Removes an object from a sequence.
        /// </summary>
        /// <param name="trash">If the object should be trashed. Most times this is desirable, however if an object is being moved to another sequence, this is not desirable.</param>
        private void RemoveFromSequence(bool trash)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj sObj)
            {
                //remove incoming connections
                switch (sObj)
                {
                    case SVar sVar:
                        foreach (VarEdge edge in sVar.Connections)
                        {
                            edge.Originator.RemoveVarlink(edge);
                        }

                        break;
                    case SAction sAction:
                        foreach (SBox.InputLink inLink in sAction.InLinks)
                        {
                            foreach (ActionEdge edge in inLink.Edges)
                            {
                                edge.Originator.RemoveOutlink(edge);
                            }
                        }

                        break;
                    case SEvent sEvent:
                        foreach (EventEdge edge in sEvent.Connections)
                        {
                            edge.Originator.RemoveEventlink(edge);
                        }

                        break;
                }

                //remove outgoing links
                if (sObj.Export.IsA("SFXSceneShopNode"))
                {
                    removeAllSFXSceneShopPinLinks(sObj.Export, "m_aOutputPins");
                    removeAllSFXSceneShopPinLinks(sObj.Export, "m_aInputPins");
                    if (sObj.Export.GetProperty<ObjectProperty>("m_pLinkedScene") != null)
                    {
                        sObj.Export.WriteProperty(new ObjectProperty(0, "m_pLinkedScene"));
                    }
                }
                else
                {
                    KismetHelper.RemoveAllLinks(sObj.Export);
                }

                //remove from sequence
                if (SelectedSequence.IsA("SFXSceneShopGameData"))
                {
                    var nodes = SelectedSequence.GetProperty<ArrayProperty<ObjectProperty>>("m_aNodes");
                    var arrayObj = nodes?.FirstOrDefault(x => x.Value == sObj.UIndex);
                    if (arrayObj != null)
                    {
                        nodes.Remove(arrayObj);
                        SelectedSequence.WriteProperty(nodes);
                    }
                }
                else
                {
                    var seqObjs = SelectedSequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects");
                    var arrayObj = seqObjs?.FirstOrDefault(x => x.Value == sObj.UIndex);
                    if (arrayObj != null)
                    {
                        seqObjs.Remove(arrayObj);
                        SelectedSequence.WriteProperty(seqObjs);
                    }
                }

                if (trash)
                {
                    //Trash
                    EntryPruner.TrashEntryAndDescendants(sObj.Export);
                }
            }
        }

        protected void node_MouseDown(object sender, PInputEventArgs e)
        {
            if (sender is SObj obj)
            {
                obj.PosAtDragStart = obj.GlobalFullBounds;
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                {
                    panToSelection = false;
                    if (SelectedObjects.Count > 1)
                    {
                        CurrentObjects_ListBox.SelectedItems.Clear();
                        panToSelection = false;
                    }

                    CurrentObjects_ListBox.SelectedItem = obj;
                    OpenNodeContextMenu(obj);
                }
                else if (e.Shift || e.Control)
                {
                    panToSelection = false;
                    if (obj.IsSelected)
                    {
                        CurrentObjects_ListBox.SelectedItems.Remove(obj);
                    }
                    else
                    {
                        CurrentObjects_ListBox.SelectedItems.Add(obj);
                    }
                }
                else if (!obj.IsSelected)
                {
                    panToSelection = false;
                    CurrentObjects_ListBox.SelectedItem = obj;
                }
            }
        }

        private void node_Click(object sender, PInputEventArgs e)
        {
            if (sender is SObj obj)
            {
                if (e.Button != System.Windows.Forms.MouseButtons.Left && obj.GlobalFullBounds == obj.PosAtDragStart)
                {
                    if (!e.Shift && !e.Control)
                    {
                        if (SelectedObjects.Count == 1 && obj.IsSelected) return;
                        panToSelection = false;
                        if (SelectedObjects.Count > 1)
                        {
                            CurrentObjects_ListBox.SelectedItems.Clear();
                            panToSelection = false;
                        }

                        CurrentObjects_ListBox.SelectedItem = obj;
                    }
                }
            }
        }

        private void node_DoubleClick(object sender, PInputEventArgs e)
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left || sender is not SObj obj
                || obj.GlobalFullBounds != obj.PosAtDragStart)
            {
                return;
            }

            if (obj is SVar { Export.ClassName: "SeqVar_Bool" } sVar)
            {
                ToggleSeqVarBool(sVar.Export);
                e.Handled = true;
            }
        }

        private void ToggleSeqVarBool(ExportEntry export)
        {
            if (export == null)
            {
                return;
            }

            var props = export.GetProperties();
            var boolProp = props.GetProp<IntProperty>("bValue");
            int newValue = boolProp?.Value == 1 ? 0 : 1;
            props.AddOrReplaceProp(new IntProperty(newValue, "bValue"));
            export.WriteProperties(props);
            RefreshView();
        }

        private void SequenceEditorWPF_Closing(object sender, CancelEventArgs e)
        {
            if (e.Cancel)
                return;

            DisposeEmbeddedContent();
        }

        public void DisposeEmbeddedContent()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;

            if (AutoSaveView_MenuItem.IsChecked)
                saveView();

            Settings.SequenceEditor_AutoSaveViewV2 = AutoSaveView_MenuItem.IsChecked;
            Settings.SequenceEditor_ShowOutputNumbers = SObj.OutputNumbers;

            // Unsubscribe from theme changes to prevent memory leaks
            ThemeManager.ThemeChanged -= OnThemeChanged;
            //Code here remove these objects from leaking the window memory
            graphEditor.Camera.MouseDown -= backMouseDown_Handler;
            graphEditor.Camera.MouseUp -= back_MouseUp;
            graphEditor.Click -= graphEditor_Click;
            graphEditor.DragDrop -= SequenceEditor_DragDrop;
            graphEditor.DragEnter -= SequenceEditor_DragEnter;
            CurrentObjects.ForEach(x =>
            {
                x.MouseDown -= node_MouseDown;
                x.Click -= node_Click;
                x.DoubleClick -= node_DoubleClick;
                x.Dispose();
            });
            CurrentObjects.Clear();
            ResetTreeView();
            ClearInterpDataTree();
            graphEditor.Dispose();
            Properties_InterpreterWPF.Dispose();
            InterpData_InterpreterWPF.Dispose();
            InterpData_MetadataEditor.Dispose();
            GraphHost.Child = null; //This seems to be required to clear OnChildGotFocus handler from WinFormsHost
            GraphHost.Dispose();
            DataContext = null;
            UnLoadMEPackage();
            DispatcherHelper.EmptyQueue();
            RecentsController?.Dispose();
        }

        private void OpenInPackageEditor_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj)
            {
                OpenEntryInPackageEditor(obj.Export);
            }
        }

        private void CurrentObjects_OpenInPackageEditor_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj)
            {
                OpenEntryInPackageEditor(obj.Export);
            }
        }

        private void SequencesTree_OpenInPackageEditor_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem?.Entry is IEntry entry)
            {
                OpenEntryInPackageEditor(entry);
            }
        }

        private void OpenEntryInPackageEditor(IEntry entry)
        {
            AllowWindowRefocus = false;
            var p = new PackageEditor.PackageEditorWindow();
            p.Show();
            p.LoadFile(entry.FileRef.FilePath, entry.UIndex);
            p.Activate();
        }

        private void OpenReferencedObjectInPackageEditor_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SVar sVar &&
                sVar.Export.GetProperty<ObjectProperty>("ObjValue") is ObjectProperty objProp)
            {
                AllowWindowRefocus =
                    false; //prevents flicker effect when windows try to focus and then package editor activates
                var p = new PackageEditor.PackageEditorWindow();
                p.Show();
                p.LoadFile(sVar.Export.FileRef.FilePath, objProp.Value);
                p.Activate(); //bring to front
            }
        }

        private void CloneInterpData_Clicked(object sender, RoutedEventArgs e)
        {
            if (SelectedObjects.HasExactly(1) && SelectedObjects[0] is SVar sVar &&
                sVar.Export.ClassName == "InterpData")
            {
                addObject(EntryCloner.CloneTree(sVar.Export));
            }
        }

        private void CloneObject_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj)
            {
                if (SelectedSequence.IsA("SFXSceneShopGameData"))
                {
                    if (obj is SVar && obj.Export.IsA("SFXSceneGroup"))
                    {
                        // SFXSceneGroup is a sibling of SFXSceneShopGameData, not a child node
                        var clonedExport = EntryCloner.CloneTree(obj.Export);
                        TryAddSceneShopGroupToParentInterpData(clonedExport);
                        customSaveData[clonedExport.UIndex] =
                            new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);
                    }
                    else
                    {
                        var clonedExport = EntryCloner.CloneEntry(obj.Export);
                        clonedExport.Parent = SelectedSequence;

                        // Strip pin links (clone without links)
                        ClearSFXSceneShopNodePinLinks(clonedExport);

                        AddObjectToSFXSceneShopGameData(clonedExport, SelectedSequence);
                        customSaveData[clonedExport.UIndex] =
                            new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);
                    }
                }
                else
                {
                    ExportEntry clonedExport = KismetHelper.CloneObject(obj.Export, SelectedSequence);
                    customSaveData[clonedExport.UIndex] =
                        new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);
                }
            }
        }

        private void CloneObjectWithLinks_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj)
            {
                if (SelectedSequence.IsA("SFXSceneShopGameData"))
                {
                    if (obj is SVar && obj.Export.IsA("SFXSceneGroup"))
                    {
                        // SFXSceneGroup is a sibling of SFXSceneShopGameData, not a child node
                        var clonedExport = EntryCloner.CloneTree(obj.Export);
                        TryAddSceneShopGroupToParentInterpData(clonedExport);
                        customSaveData[clonedExport.UIndex] =
                            new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);
                    }
                    else
                    {
                        // Clone with links preserved - don't strip pins
                        var clonedExport = EntryCloner.CloneEntry(obj.Export);
                        clonedExport.Parent = SelectedSequence;
                        AddObjectToSFXSceneShopGameData(clonedExport, SelectedSequence);
                        customSaveData[clonedExport.UIndex] =
                            new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);
                    }
                }
                else
                {
                    // Save the link properties before cloning
                    var originalProps = obj.Export.GetProperties();
                    var outputLinks = originalProps.GetProp<ArrayProperty<StructProperty>>("OutputLinks");
                    var variableLinks = originalProps.GetProp<ArrayProperty<StructProperty>>("VariableLinks");
                    var eventLinks = originalProps.GetProp<ArrayProperty<StructProperty>>("EventLinks");

                    // Clone the object (this may remove links due to the topLevel parameter)
                    ExportEntry clonedExport = KismetHelper.CloneObject(obj.Export, SelectedSequence);

                    // Restore the link properties to the cloned object
                    var clonedProps = clonedExport.GetProperties();
                    if (outputLinks != null)
                    {
                        clonedProps.AddOrReplaceProp(outputLinks);
                    }
                    if (variableLinks != null)
                    {
                        clonedProps.AddOrReplaceProp(variableLinks);
                    }
                    if (eventLinks != null)
                    {
                        clonedProps.AddOrReplaceProp(eventLinks);
                    }
                    clonedExport.WriteProperties(clonedProps);

                    customSaveData[clonedExport.UIndex] =
                        new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);
                }
            }
        }

        private static void AddObjectToSFXSceneShopGameData(ExportEntry newObject, ExportEntry container)
        {
            var nodes = container.GetProperty<ArrayProperty<ObjectProperty>>("m_aNodes")
                        ?? new ArrayProperty<ObjectProperty>("m_aNodes");
            if (nodes.All(x => x.Value != newObject.UIndex))
            {
                nodes.Add(new ObjectProperty(newObject));
                container.WriteProperty(nodes);
            }
        }

        private bool TryAddSceneShopGroupToParentInterpData(ExportEntry export)
        {
            if (export == null
                || !export.IsA("SFXSceneGroup")
                || SelectedSequence?.IsA("SFXSceneShopGameData") != true
                || SelectedSequence.Parent is not ExportEntry parentExport
                || parentExport.ClassName != "InterpData")
            {
                return false;
            }

            export.Parent = parentExport;
            MatineeHelper.AddToParentInterpList(export, parentExport);
            return true;
        }

        private static void ClearSFXSceneShopNodePinLinks(ExportEntry export)
        {
            var props = export.GetProperties();
            bool modified = false;
            foreach (string pinPropName in new[] { "m_aOutputPins", "m_aInputPins" })
            {
                var pins = props.GetProp<ArrayProperty<StructProperty>>(pinPropName);
                if (pins != null)
                {
                    foreach (var pin in pins)
                    {
                        var links = pin.GetProp<ArrayProperty<StructProperty>>("aLinks");
                        if (links is { Count: > 0 })
                        {
                            links.Clear();
                            modified = true;
                        }
                    }
                }
            }
            // Also clear object property references used as var links (e.g. m_pLinkedScene)
            var linkedScene = props.GetProp<ObjectProperty>("m_pLinkedScene");
            if (linkedScene is { Value: not 0 })
            {
                props.AddOrReplaceProp(new ObjectProperty(0, "m_pLinkedScene"));
                modified = true;
            }
            if (modified)
            {
                export.WriteProperties(props);
            }
        }

        private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            graphEditor.AllowDragging();
            if (AllowWindowRefocus)
            {
                Focus(); //this will make window bindings work, as context menu is not part of the visual tree, and focus will be on there if the user clicked it.
            }

            AllowWindowRefocus = true;
        }

        private void CurrentObjectsList_SelectedItemChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.RemovedItems?.Cast<SObj>().ToList() is List<SObj> deselectedEntries)
            {
                SelectedObjects.RemoveRange(deselectedEntries);
                foreach (SObj obj in deselectedEntries)
                {
                    obj.IsSelected = false;
                }
            }

            if (e.AddedItems?.Cast<SObj>().ToList() is IList<SObj> selectedEntries)
            {
                SelectedObjects.AddRange(selectedEntries);
                foreach (SObj obj in selectedEntries)
                {
                    obj.IsSelected = true;
                }
            }

            if (SelectedObjects.Count == 1)
            {
                RecordSelectedObjectHistory(SelectedObjects[0]);
                Properties_InterpreterWPF.LoadExport(SelectedObjects[0].Export);

                var interpData = GetInterpDataForSelectedObject(SelectedObjects[0]);
                if (interpData != null)
                {
                    BuildInterpDataTree(interpData);
                    InterpDataTab.Visibility = Visibility.Visible;
                }
                else
                {
                    ClearInterpDataTree();
                    InterpData_InterpreterWPF.UnloadExport();
                    InterpData_MetadataEditor.UnloadExport();
                    InterpDataTab.Visibility = Visibility.Collapsed;
                    if (BottomTabControl.SelectedItem == InterpDataTab)
                    {
                        BottomTabControl.SelectedIndex = 0;
                    }
                }
            }
            else if (suppressInterpreterUnloadDepth == 0 && !(Properties_InterpreterWPF.CurrentLoadedExport?.IsSequence() ?? false))
            {
                Properties_InterpreterWPF.UnloadExport();
            }

            if (SelectedObjects.Any())
            {
                ScrollCurrentObjectSelectionIntoView(CurrentObjects_ListBox.SelectedItem as SObj ?? SelectedObjects.LastOrDefault());

                if (panToSelection)
                {
                    if (SelectedObjects.Count == 1)
                    {
                        graphEditor.Camera.AnimateViewToCenterBounds(SelectedObjects[0].GlobalFullBounds, false, 100);
                    }
                    else
                    {
                        RectangleF boundingBox = SelectedObjects.Select(obj => obj.GlobalFullBounds).BoundingRect();
                        graphEditor.Camera.AnimateViewToCenterBounds(boundingBox, true, 200);
                    }
                }
            }

            panToSelection = true;
            graphEditor.Refresh();
        }

        private void ScrollCurrentObjectSelectionIntoView(SObj selectedObject)
        {
            if (selectedObject == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                CurrentObjects_ListBox?.UpdateLayout();
                CurrentObjects_ListBox?.ScrollIntoView(selectedObject);
            }));
        }

        private void SaveImage()
        {
            if (CurrentObjects.Count == 0)
                return;
            string objectName =
                System.Text.RegularExpressions.Regex.Replace(SelectedSequence.ObjectName.Name, @"[<>:""/\\|?*]", "");
            var d = new SaveFileDialog
            {
                Filter = "PNG Files (*.png)|*.png",
                FileName = $"{CurrentFile}.{objectName}"
            };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                PNode r = graphEditor.Root;
                RectangleF rr = r.GlobalFullBounds;
                PNode p = PPath.CreateRectangle(rr.X, rr.Y, rr.Width, rr.Height);
                p.Brush = Brushes.White;
                graphEditor.addBack(p);
                graphEditor.Camera.Visible = false;
                Image image = graphEditor.Root.ToImage();
                graphEditor.Camera.Visible = true;
                image.Save(d.FileName, ImageFormat.Png);
                graphEditor.backLayer.RemoveAllChildren();
                MessageBox.Show(this, "Done.");
            }
        }

        private PointF ConsumePendingNewObjectPosition()
        {
            if (pendingNewObjectPosition is { } pendingPosition)
            {
                pendingNewObjectPosition = null;
                return pendingPosition;
            }

            return new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);
        }

        private void addObject(ExportEntry exportToAdd, bool removeLinks = true, PointF? preferredPosition = null)
        {
            customSaveData[exportToAdd.UIndex] = preferredPosition ?? ConsumePendingNewObjectPosition();
            if (SelectedSequence.IsA("SFXSceneShopGameData"))
            {
                if (!TryAddSceneShopGroupToParentInterpData(exportToAdd))
                {
                    exportToAdd.Parent = SelectedSequence;
                    AddObjectToSFXSceneShopGameData(exportToAdd, SelectedSequence);
                }
            }
            else
            {
                DetachObjectFromCurrentSequence(exportToAdd, SelectedSequence);
                KismetHelper.AddObjectToSequence(exportToAdd, SelectedSequence, removeLinks);
            }
        }

        private static void DetachObjectFromCurrentSequence(ExportEntry exportToMove, ExportEntry destinationSequence)
        {
            if (exportToMove == null || destinationSequence == null)
            {
                return;
            }

            var existingSequences = new List<ExportEntry>();

            if (exportToMove.Parent is ExportEntry parentExport && parentExport.IsSequence())
            {
                existingSequences.Add(parentExport);
            }

            if (exportToMove.GetProperty<ObjectProperty>("ParentSequence")?.ResolveToEntry(exportToMove.FileRef) is ExportEntry parentSequence
                && parentSequence.IsSequence()
                && !existingSequences.Contains(parentSequence))
            {
                existingSequences.Add(parentSequence);
            }

            foreach (var existingSequence in existingSequences)
            {
                if (existingSequence == destinationSequence)
                {
                    continue;
                }

                var sequenceObjects = existingSequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects");
                if (sequenceObjects == null)
                {
                    continue;
                }

                var existingRefs = sequenceObjects.Where(objRef => objRef.Value == exportToMove.UIndex).ToList();
                if (existingRefs.Count == 0)
                {
                    continue;
                }

                foreach (var existingRef in existingRefs)
                {
                    sequenceObjects.Remove(existingRef);
                }

                existingSequence.WriteProperty(sequenceObjects);
            }
        }

        private void AddObject_Clicked(object sender, RoutedEventArgs e)
        {
            if (EntrySelector.GetEntry<ExportEntry>(this, Pcc,
                    "Select an existing sequence or sequence object",
                    exp => exp.IsA("Sequence") || exp.IsA("SequenceObject")) is ExportEntry exportToAdd)
            {
                if (!exportToAdd.IsA("Sequence") && !exportToAdd.IsA("SequenceObject"))
                {
                    MessageBox.Show(this,
                        $"#{exportToAdd.UIndex}: {exportToAdd.ObjectName.Instanced} is not a sequence or sequence object.");
                    return;
                }

                if (CurrentObjects.Any(obj => obj.Export == exportToAdd))
                {
                    MessageBox.Show(this,
                        $"#{exportToAdd.UIndex}: {exportToAdd.ObjectName.Instanced} is already in the sequence.");
                    return;
                }

                addObject(exportToAdd);
            }
        }

        private void CreateEmptySubsequence_Clicked(object sender, RoutedEventArgs e)
        {
            if (SelectedSequence == null)
            {
                return;
            }

            if (!SelectedSequence.IsSequence())
            {
                MessageBox.Show(this, "Subsequences can only be created under Sequence exports.");
                return;
            }

            var sequenceName = PromptDialog.Prompt(this, "Enter a name for the new subsequence.",
                "Create Empty Subsequence", "Sequence", true);
            if (string.IsNullOrWhiteSpace(sequenceName))
            {
                return;
            }

            var newSequence = SequenceObjectCreator.CreateSequence(SelectedSequence, sequenceName.Trim());
            customSaveData[newSequence.UIndex] =
                new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);

            var currentSequence = SelectedSequence;
            LoadSequences();
            RefreshView();

            if (currentSequence != null)
            {
                var currentTreeNode = TreeViewRootNodes.SelectMany(node => node.FlattenTree())
                    .FirstOrDefault(node => node.UIndex == currentSequence.UIndex);
                if (currentTreeNode != null)
                {
                    currentTreeNode.ExpandParents();
                    currentTreeNode.IsSelected = true;
                    _selectedItem = currentTreeNode;
                }
            }
        }

        private void showOutputNumbers_Click(object sender, EventArgs e)
        {
            SObj.OutputNumbers = ShowOutputNumbers_MenuItem.IsChecked;
            if (CurrentObjects.Any())
            {
                RefreshView();
            }
        }

        private void OpenInInterpViewer_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj)
            {
                int uIndex;
                ExportEntry exportEntry = obj.Export;
                if (exportEntry.IsA("InterpData"))
                {
                    uIndex = exportEntry.UIndex;
                }
                else if (obj is SAction sAction && sAction.Varlinks.Any() && sAction.Varlinks[0].Links.Any())
                {
                    uIndex = sAction.Varlinks[0].Links[0];
                }
                else
                {
                    MessageBox.Show(this, "No InterpData to open!", "Sorry!", MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                AllowWindowRefocus =
                    false; //prevents flicker effect when windows try to focus and then package editor activates

                var p = new InterpEditor.InterpEditorWindow();
                p.Show();
                p.LoadFile(Pcc.FilePath);
                p.SelectedInterpData = Pcc.GetUExport(uIndex);
            }
        }

        private void BulkEditInterpGroups_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj)
            {
                ExportEntry exportEntry = obj.Export;
                ExportEntry interpData = null;
                if (exportEntry.IsA("InterpData"))
                {
                    interpData = exportEntry;
                }
                else if (obj is SAction sAction && sAction.Varlinks.Any() && sAction.Varlinks[0].Links.Any()
                         && Pcc.IsUExport(sAction.Varlinks[0].Links[0])
                         && Pcc.GetUExport(sAction.Varlinks[0].Links[0]).ClassName == "InterpData")
                {
                    interpData = Pcc.GetUExport(sAction.Varlinks[0].Links[0]);
                }

                if (interpData != null)
                {
                    var dialog = new BulkInterpEditorDialog(this, interpData);
                    dialog.ShowDialog();
                }
            }
        }

        private void ApplySingleCameraPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedInterpData(out ExportEntry interpData))
            {
                return;
            }

            ExportEntry group = SelectCameraGroup(interpData, "Choose the group whose camera track should be modified:",
                "Apply Single-Camera Preset");
            if (group is not null && CameraPresetDialog.GenerateForGroup(this, group))
            {
                RefreshInterpDataTreePreserveState(group.UIndex);
            }
        }

        private void ApplyMulticamCameraPreset_Click(object sender, RoutedEventArgs e)
        {
            if (TryGetSelectedInterpData(out ExportEntry interpData)
                && CameraPresetDialog.GenerateForInterpData(this, interpData))
            {
                RefreshInterpDataTreePreserveState(interpData.UIndex);
            }
        }

        private void SaveSingleCameraPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedInterpData(out ExportEntry interpData))
            {
                return;
            }

            ExportEntry group = SelectCameraGroup(interpData, "Choose the group whose camera track should be saved:",
                "Save Single-Camera Preset");
            if (group is not null)
            {
                CameraPresetDialog.SaveGroupAsPreset(this, group);
            }
        }

        private void SaveMulticamCameraPreset_Click(object sender, RoutedEventArgs e)
        {
            if (TryGetSelectedInterpData(out ExportEntry interpData))
            {
                CameraPresetDialog.SaveInterpDataAsMulticamPreset(this, interpData);
            }
        }

        private ExportEntry SelectCameraGroup(ExportEntry interpData, string prompt, string title)
        {
            ExportEntry[] groups = interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups")?
                .Select(reference => interpData.FileRef.TryGetUExport(reference.Value, out ExportEntry group) ? group : null)
                .Where(group => group?.ClassName == "InterpGroup")
                .ToArray() ?? [];
            if (groups.Length == 0)
            {
                MessageBox.Show("This InterpData has no camera-compatible groups.", "No Interp Groups",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var choices = groups.ToDictionary(
                group => $"{group.GetProperty<NameProperty>("GroupName")?.Value.Instanced ?? group.ObjectName.Instanced} ({group.UIndex})",
                group => group,
                StringComparer.Ordinal);
            string selectedGroup = StringSelectorDialog.GetValue(this, prompt, title, choices.Keys);
            return choices.GetValueOrDefault(selectedGroup);
        }

        private void OpenInDialogueEditor_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj &&
                (obj.Export.ClassName.EndsWith("SeqAct_StartConversation") ||
                 obj.Export.ClassName.EndsWith("StartAmbientConv")) &&
                obj.Export.GetProperty<ObjectProperty>("Conv") is ObjectProperty conv)
            {
                if (Pcc.IsUExport(conv.Value))
                {
                    AllowWindowRefocus =
                        false; //prevents flicker effect when windows try to focus and then package editor activates
                    new DialogueEditor.DialogueEditorWindow(Pcc.GetUExport(conv.Value)).Show();
                    return;
                }

                if (Pcc.IsImport(conv.Value))
                {
                    ImportEntry convImport = Pcc.GetImport(conv.Value);
                    string extension = Path.GetExtension(Pcc.FilePath);
                    string noExtensionPath = Path.ChangeExtension(Pcc.FilePath, null);
                    string loc_int = Pcc.Game == MEGame.ME1 ? "_LOC_int" : "_LOC_INT";
                    string convFilePath = noExtensionPath + loc_int + extension;
                    if (File.Exists(convFilePath))
                    {
                        using var convFile = MEPackageHandler.OpenMEPackage(convFilePath);
                        var convExport = convFile.Exports.FirstOrDefault(x => x.ObjectName == convImport.ObjectName);
                        if (convExport != null)
                        {
                            AllowWindowRefocus =
                                false; //prevents flicker effect when windows try to focus and then package editor activates
                            new DialogueEditor.DialogueEditorWindow(convExport).Show();
                            return;
                        }
                    }
                    else if (EntryImporter.ResolveImport(convImport, new PackageCache()) is ExportEntry fauxExport)
                    {
                        using var convFile = MEPackageHandler.OpenMEPackage(fauxExport.FileRef.FilePath);
                        var convExport = convFile.GetUExport(fauxExport.UIndex);
                        if (convExport != null)
                        {
                            AllowWindowRefocus =
                                false; //prevents flicker effect when windows try to focus and then package editor activates
                            new DialogueEditor.DialogueEditorWindow(convExport).Show();
                            return;
                        }
                    }
                }
            }

            MessageBox.Show(this, "Cannot find Conversation!", "Sorry!", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OpenGestureAnimationImporter_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj && obj.Export.ClassName == "SFXSeqAct_SetAmbientPerformance")
            {
                var dialog = new GestureAnimationImporterDialog(obj.Export, this);
                dialog.ShowDialog();
            }
        }

        private void GlobalSeqRefViewSavesMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects.Any())
            {
                SetupJSON(SelectedSequence);
            }
        }

        private void SequenceEditorWPF_Loaded(object sender, RoutedEventArgs e)
        {
            if (FileQueuedForLoad != null || PackageQueuedForLoad != null || ExportQueuedForFocusing != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    //Wait for all children to finish loading
                    if (FileQueuedForLoad != null)
                    {
                        LoadFile(FileQueuedForLoad);
                        FileQueuedForLoad = null;
                    }
                    else if (PackageQueuedForLoad != null)
                    {
                        LoadFile(PackageQueuedForLoad.FilePath, () => RegisterPackage(PackageQueuedForLoad));
                        PackageQueuedForLoad = null;
                    }

                    if (ExportQueuedForFocusing != null)
                    {
                        GoToExport(ExportQueuedForFocusing);
                        ExportQueuedForFocusing = null;
                    }

                    Activate();
                }));
            }
        }

        private void GoToExport(int UIndex)
        {
            if (Pcc != null)
            {
                ExportEntry exp = Pcc.GetUExport(UIndex);
                if (exp != null)
                {
                    if (!IsLoaded && !(isEmbedded && isEmbeddedContentLoaded))
                    {
                        ExportQueuedForFocusing = exp;
                    }
                    else
                    {
                        GoToExport(exp);
                    }
                }
            }
        }

        private void GoToExport(ExportEntry expToNavigateTo, bool goIntoSequences = true)
        {
            if (!IsLoaded && !(isEmbedded && isEmbeddedContentLoaded))
            {
                // Do not try to navigate if UI has not finished loading
                ExportQueuedForFocusing = expToNavigateTo;
                return;
            }

            if (goIntoSequences && (expToNavigateTo.ClassName is "SequenceReference" or "Sequence"
                                    || SequenceExports.Contains(expToNavigateTo)))
            {
                if (expToNavigateTo.ClassName == "SequenceReference")
                {
                    var sequenceprop = expToNavigateTo.GetProperty<ObjectProperty>("oSequenceReference");
                    if (sequenceprop != null)
                    {
                        expToNavigateTo = Pcc?.GetUExport(sequenceprop.Value);
                    }
                    else
                    {
                        return;
                    }
                }

                SelectedItem = TreeViewRootNodes.SelectMany(node => node.FlattenTree())
                    .FirstOrDefault(node => node.UIndex == expToNavigateTo.UIndex);
                return;
            }

            if (CurrentObjects.FirstOrDefault(obj => obj.Export == expToNavigateTo) is SObj currentObject)
            {
                CurrentObjects_ListBox.SelectedItem = currentObject;
                return;
            }

            else
            {
                // Find which sequence contains this object
                foreach (ExportEntry exp in SequenceExports)
                {

                    // Get the export for the sequence we will look for objects in
                    ExportEntry sequence = exp;
                    if (sequence.ClassName == "SequenceReference")
                    {
                        var sequenceprop = sequence.GetProperty<ObjectProperty>("oSequenceReference");
                        if (sequenceprop != null)
                        {
                            sequence = Pcc.GetUExport(sequenceprop.Value);
                        }
                        else
                        {
                            return;
                        }
                    }

                    // Enumerate the objects in the sequence to see if what we are looking for is in this sequence
                    var seqObjs = sequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects");

                    // SFXSceneShopGameData stores children in m_aNodes instead of SequenceObjects
                    if (seqObjs == null && sequence.IsA("SFXSceneShopGameData"))
                    {
                        seqObjs = sequence.GetProperty<ArrayProperty<ObjectProperty>>("m_aNodes");
                    }

                    if (seqObjs != null && seqObjs.Any(objProp => objProp.Value == expToNavigateTo.UIndex))
                    {
                        //This is our sequence
                        var nodes = TreeViewRootNodes.SelectMany(node => node.FlattenTree())
                            .ToList(); // This is to debug selection failures
                        SelectedItem = nodes.First(node => node.UIndex == sequence.UIndex);
                        CurrentObjects_ListBox.SelectedItem =
                            CurrentObjects.FirstOrDefault(x => x.Export == expToNavigateTo);
                        break;
                    }
                }
            }
        }

        private void PlotEditorMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SAction sAction &&
                sAction.Export.ClassName == "BioSeqAct_PMExecuteTransition" &&
                sAction.Export.GetProperty<IntProperty>("m_nIndex")?.Value is int m_nIndex)
            {
                IEnumerable<string> plotFiles = new List<string>();
                int stateEventKey = m_nIndex;

                if (Pcc.Game is MEGame.ME3 or MEGame.LE3)
                {
                    plotFiles = MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                        .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                        .Select(dir => Path.Combine(dir, Pcc.Game.CookedDirName(),
                            $"Startup_{MELoadedDLC.GetDLCNameFromDir(dir)}_INT.pcc"))
                        .Append(Path.Combine(MEDirectories.GetCookedPath(Pcc.Game), "SFXGameInfoSP_SF.pcc"))
                        .Where(File.Exists);
                }

                if (Pcc.Game is MEGame.ME2 or MEGame.LE2)
                {
                    plotFiles = MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                        .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                        .Select(dir => Path.Combine(dir, Pcc.Game.CookedDirName(),
                            $"Startup_{MELoadedDLC.GetDLCNameFromDir(dir)}_INT.pcc"))
                        .Append(Path.Combine(MEDirectories.GetCookedPath(Pcc.Game), "Startup_INT.pcc"))
                        .Where(File.Exists);
                }

                if (Pcc.Game is MEGame.LE1)
                {
                    plotFiles = MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                        .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                        //.Select(dir => Path.Combine(dir, "CookedPCConsole", $"Startup_{MELoadedDLC.GetDLCNameFromDir(dir)}_INT.pcc")) // TODO: implement once ME1 DLC folders work
                        .Append(Path.Combine(MEDirectories.GetCookedPath(Pcc.Game), "BIOC_Materials.pcc"))
                        .Where(File.Exists);
                }

                if (Pcc.Game is MEGame.ME1)
                {
                    plotFiles = MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                        .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                        .Select(dir => Path.Combine(dir, Pcc.Game.CookedDirName(),
                            $@"Packages\PlotManagerAuto{MELoadedDLC.GetDLCNameFromDir(dir)}.upk"))
                        .Append(Path.Combine(MEDirectories.GetCookedPath(Pcc.Game), @"Packages\PlotManagerAuto.upk"))
                        .Where(File.Exists);
                }

                if (stateEventKey != 0 && plotFiles.Any())
                {
                    string filePath = null;
                    foreach (var plotFile in plotFiles)
                    {
                        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(plotFile);
                        if (StateEventMapView.TryFindStateEventMap(pcc, out ExportEntry export))
                        {
                            var stateEventMap = BinaryBioStateEventMap.Load(export);
                            if (stateEventMap.StateEvents.ContainsKey(stateEventKey))
                            {
                                filePath = plotFile;
                            }
                        }
                    }

                    if (filePath != null)
                    {
                        var plotEd = new PlotEditorWindow();
                        plotEd.Show();
                        plotEd.LoadFile(filePath);
                        plotEd.GoToStateEvent(stateEventKey);
                    }
                    else
                    {
                        MessageBox.Show(this, $"Could not find State Event {stateEventKey}");
                    }
                }
            }
        }

        private void ConditionalsEditorMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SAction sAction &&
                sAction.Export.ClassName == "BioSeqAct_PMCheckConditional" &&
                sAction.Export.GetProperty<IntProperty>("m_nIndex")?.Value is int cndId &&
                cndId != 0 && Pcc.Game.IsGame3())
            {
                var cookedDirs = MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                    .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                    .Select(dir => Path.Combine(dir, Pcc.Game.CookedDirName()))
                    .Append(MEDirectories.GetCookedPath(Pcc.Game))
                    .Where(Directory.Exists);

                var cndFiles = cookedDirs.SelectMany(dir => Directory.EnumerateFiles(dir, "*.cnd"));

                string matchedFile = null;
                foreach (var cndFile in cndFiles)
                {
                    var cnd = CNDFile.FromFile(cndFile);
                    if (cnd.ConditionalEntries.Any(c => c.ID == cndId))
                    {
                        matchedFile = cndFile;
                        break;
                    }
                }

                if (matchedFile != null)
                {
                    var cndEd = new ConditionalsEditorWindow();
                    cndEd.Show();
                    cndEd.LoadFile(matchedFile, cndId);
                }
                else
                {
                    MessageBox.Show(this, $"Could not find conditional {cndId} in any mounted .cnd file.");
                }
            }
        }

        private void RepointIncomingReferences_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SVar sVar)
            {
                if (EntrySelector.GetEntry<ExportEntry>(this, Pcc) is ExportEntry export)
                {
                    if (CurrentObjects.All(x => x.Export != export))
                    {
                        MessageBox.Show(
                            $"#{export.UIndex} {export.ObjectName.Instanced}  is not part of this sequence, and can't be repointed to.");
                        return;
                    }

                    var sequence =
                        sVar.Export.FileRef.GetUExport(sVar.Export.GetProperty<ObjectProperty>("ParentSequence").Value);
                    var sequenceObjects = sequence.GetProperty<ArrayProperty<ObjectProperty>>("SequenceObjects");
                    foreach (var seqObjRef in sequenceObjects)
                    {
                        var saveProps = false;
                        var seqObj = sVar.Export.FileRef.GetUExport(seqObjRef.Value);
                        var props = seqObj.GetProperties();
                        var variableLinks = props.GetProp<ArrayProperty<StructProperty>>("VariableLinks");
                        if (variableLinks != null)
                        {
                            foreach (var variableLink in variableLinks)
                            {
                                var linkedVars = variableLink.GetProp<ArrayProperty<ObjectProperty>>("LinkedVariables");
                                if (linkedVars != null)
                                {
                                    foreach (var linkedVar in linkedVars)
                                    {
                                        if (linkedVar.Value == sVar.Export.UIndex)
                                        {
                                            linkedVar.Value = export.UIndex; //repoint
                                            saveProps = true;
                                        }
                                    }
                                }
                            }
                        }

                        if (saveProps)
                        {
                            seqObj.WriteProperties(props);
                        }
                    }

                    RefreshView();
                }
            }
        }

        private void ShowAdditionalInfoInCommentTextMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Settings.Save();
        }

        private void IntegerUpDown_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (CurrentObjects.Any())
            {
                RefreshView();
            }
        }

        private void EditComment_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj sObj)
            {
                var comments = sObj.Export.GetProperty<ArrayProperty<StrProperty>>("m_aObjComment") ??
                               new ArrayProperty<StrProperty>("m_aObjComment");

                string commentText = string.Join("\n", comments.Select(prop => prop.Value));

                string resultText = PromptDialog.Prompt(this, "", "Edit Comment", commentText, true,
                    inputType: PromptDialog.InputType.Multiline);

                if (resultText == null)
                {
                    return;
                }

                comments = new ArrayProperty<StrProperty>(
                    resultText.SplitLines(StringSplitOptions.RemoveEmptyEntries).Select(s => new StrProperty(s)),
                    "m_aObjComment");

                sObj.Export.WriteProperty(comments);
            }
        }

        public void PropogateRecentsChange(string propogationSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        private void GotoSequenceReference_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SAction sAction &&
                (sAction.Export.ClassName is "SequenceReference" or "Sequence"
                 || SequenceExports.Contains(sAction.Export)))
            {
                GoToExport(sAction.Export);
            }
        }

        private void AddToLogString_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SAction sAction &&
                sAction.Export.ClassName == "SeqAct_Log")
            {
                var result = PromptDialog.Prompt(this, "Enter the string to log", "Enter string");
                if (!string.IsNullOrWhiteSpace(result))
                {
                    var newSeqObj = LEXSequenceObjectCreator.CreateSequenceObject(Pcc, "SeqVar_String");
                    newSeqObj.WriteProperty(new StrProperty(result, "StrValue"));
                    KismetHelper.AddObjectToSequence(newSeqObj, SelectedSequence);
                    var varLinks = KismetHelper.GetVariableLinksOfNode(sAction.Export);
                    var stringVarLink = varLinks.First(x => x.LinkDesc == "String");
                    stringVarLink.LinkedNodes.Add(newSeqObj);
                    KismetHelper.WriteVariableLinksToNode(sAction.Export, varLinks);
                }
            }
        }

        private void CreateSeqLogForObject_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SVar sVar)
            {
                var result = PromptDialog.Prompt(this, "Enter the string to log alongside this", "Enter string");
                if (!string.IsNullOrWhiteSpace(result))
                {
                    // Create the log object and add it to the sequence
                    var seqLogObj = LEXSequenceObjectCreator.CreateSequenceObject(Pcc, "SeqAct_Log");
                    KismetHelper.AddObjectToSequence(seqLogObj, SelectedSequence);

                    // Create user string SeqVar
                    var newSeqObj = LEXSequenceObjectCreator.CreateSequenceObject(Pcc, "SeqVar_String");
                    newSeqObj.WriteProperty(new StrProperty(result, "StrValue"));
                    KismetHelper.AddObjectToSequence(newSeqObj, SelectedSequence);

                    // Attach the user string SeqVar and the selected item to the log.

                    // String
                    var varLinks = KismetHelper.GetVariableLinksOfNode(seqLogObj);
                    var stringVarLink = varLinks.First(x => x.LinkDesc == "String");
                    stringVarLink.LinkedNodes.Add(newSeqObj);

                    VarLinkInfo linkToAttachTo = null;
                    var typeName = sVar.Export.ClassName;
                    var game = sVar.Export.Game;

                    // Use expected type
                    if (typeName is "SeqVar_External" or "SeqVar_ScopedNamed")
                    {
                        // Just default to object if we can't find the type
                        typeName = sVar.Export.GetProperty<ObjectProperty>("ExpectedType")?.ResolveToEntry(sVar.Export.FileRef)?.ObjectName.Name ?? "SeqVar_Object";
                    }


                    if (GlobalUnrealObjectInfo.IsA(typeName,"SeqVar_String", game))
                    {
                        linkToAttachTo = varLinks.First(x => x.LinkDesc == "String");
                    }
                    else if (GlobalUnrealObjectInfo.IsA(typeName,"SeqVar_Float", game))
                    {
                        linkToAttachTo = varLinks.First(x => x.LinkDesc == "Float");
                    }
                    else if (GlobalUnrealObjectInfo.IsA(typeName,"SeqVar_Bool", game))
                    {
                        linkToAttachTo = varLinks.First(x => x.LinkDesc == "Bool");
                    }
                    else if (GlobalUnrealObjectInfo.IsA(typeName,"SeqVar_Object", game))
                    {
                        linkToAttachTo = varLinks.First(x => x.LinkDesc == "Object");
                    }
                    else if (GlobalUnrealObjectInfo.IsA(typeName,"SeqVar_Int", game))
                    {
                        linkToAttachTo = varLinks.First(x => x.LinkDesc == "Int");
                    }
                    else if (GlobalUnrealObjectInfo.IsA(typeName,"SeqVar_Name", game))
                    {
                        linkToAttachTo = varLinks.First(x => x.LinkDesc == "Name");
                    }
                    else if (GlobalUnrealObjectInfo.IsA(typeName,"SeqVar_Vector", game))
                    {
                        linkToAttachTo = varLinks.First(x => x.LinkDesc == "Vector");
                    }
                    else if (GlobalUnrealObjectInfo.IsA(typeName,"SeqVar_ObjectList", game))
                    {
                        linkToAttachTo = varLinks.First(x => x.LinkDesc == "Obj List");
                    }

                    if (linkToAttachTo == null)
                    {
                        Debugger.Break();
                    }
                    else
                    {
                        linkToAttachTo.LinkedNodes.Add(sVar.Export);
                    }

                    // Write the links
                    KismetHelper.WriteVariableLinksToNode(seqLogObj, varLinks);
                }
            }
        }

        private void SeqLogLogOutlink(SBox sourceAction, string outLinkName)
        {
            var result = PromptDialog.Prompt(this,
                $"Enter the string to log when the outlink '{outLinkName}' is fired.", "Enter string",
                $"Outlink {outLinkName} fired from {sourceAction.Export.UIndex} {sourceAction.Export.ObjectName.Instanced}",
                true);
            if (!string.IsNullOrWhiteSpace(result))
            {
                // Create the log object and add it to the sequence
                var seqLogObj = LEXSequenceObjectCreator.CreateSequenceObject(Pcc, "SeqAct_Log");
                KismetHelper.AddObjectToSequence(seqLogObj, SelectedSequence);

                // Create user string SeqVar
                var newSeqObj = LEXSequenceObjectCreator.CreateSequenceObject(Pcc, "SeqVar_String");
                newSeqObj.WriteProperty(new StrProperty(result, "StrValue"));
                KismetHelper.AddObjectToSequence(newSeqObj, SelectedSequence);

                // Attach the user string SeqVar and the selected item to the log.
                KismetHelper.CreateVariableLink(seqLogObj, "String", newSeqObj);

                // Add an outlink to the new object
                KismetHelper.CreateOutputLink(sourceAction.Export, outLinkName, seqLogObj);
            }
        }

        private void OpenClassDefinitionInPackageEditor_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj && obj.Export != null)
            {
                // Get class of the object
                var objClass = obj.Export.Class;
                string className = objClass.ClassName;
                if (objClass is ImportEntry imp)
                {
                    objClass = EntryImporter.ResolveImport(imp, new PackageCache());
                }

                if (objClass != null)
                {
                    AllowWindowRefocus =
                        false; //prevents flicker effect when windows try to focus and then package editor activates
                    var p = new PackageEditor.PackageEditorWindow();
                    p.Show();
                    p.LoadFile(objClass.FileRef.FilePath, objClass.UIndex);
                    p.Activate(); //bring to front
                }
                else
                {
                    MessageBox.Show($"Could not determine where class '{className}' is defined.",
                        "Cannot locate class");
                }
            }
        }

        private void OpenOtherVersion()
        {
            var result = CrossGenHelpers.FetchOppositeGenPackage(Pcc, out var otherGen);
            if (result != null)
            {
                MessageBox.Show(result);
            }
            else
            {
                var nodeEntry = SelectedObjects.FirstOrDefault();
                SequenceEditorWPF seqEd = new SequenceEditorWPF(otherGen);
                if (nodeEntry != null && nodeEntry.Export != null)
                {
                    seqEd.ExportQueuedForFocusing = otherGen.FindExport(nodeEntry.Export.InstancedFullPath);
                }

                seqEd.Show();
            }
        }

        private void LoadCustomClasses_Clicked(object sender, RoutedEventArgs e)
        {
            ShowRememberedCustomSequenceObjectSourcesDialog();
        }

        private void LoadCustomClassesFromCurentPackage_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsM.LoadCustomClassesFromCurrentPackage(this);
        }

        private void ShowRememberedCustomSequenceObjectSourcesDialog()
        {
            var sourcesListBox = new ListBox
            {
                MinWidth = 520,
                MinHeight = 220,
                ItemsSource = CustomSequenceObjectSourceFiles
            };
            sourcesListBox.ItemTemplate = new DataTemplate
            {
                VisualTree = new FrameworkElementFactory(typeof(TextBlock))
            };
            sourcesListBox.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            sourcesListBox.ItemTemplate.VisualTree.SetBinding(ToolTipProperty, new System.Windows.Data.Binding());
            sourcesListBox.ItemTemplate.VisualTree.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);

            var loadButton = new Button
            {
                Content = "Load package...",
                MinWidth = 100,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var forgetButton = new Button
            {
                Content = "Forget selected",
                MinWidth = 100,
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = false
            };
            var closeButton = new Button
            {
                Content = "Close",
                MinWidth = 100,
                IsCancel = true
            };

            var dialog = new System.Windows.Window
            {
                Title = "Custom sequence object sources",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(12),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Remembered package files",
                            FontWeight = FontWeights.Bold,
                            Margin = new Thickness(0, 0, 0, 8)
                        },
                        sourcesListBox,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Margin = new Thickness(0, 12, 0, 0),
                            Children =
                            {
                                loadButton,
                                forgetButton,
                                closeButton
                            }
                        }
                    }
                }
            };

            CustomWindowChrome.ApplyCustomChrome(dialog);

            void UpdateForgetState()
            {
                forgetButton.IsEnabled = sourcesListBox.SelectedItem is string;
            }

            sourcesListBox.SelectionChanged += (_, _) => UpdateForgetState();
            loadButton.Click += (_, _) => SequenceEditorExperimentsM.LoadCustomClassesFromFile(this);
            forgetButton.Click += (_, _) =>
            {
                if (sourcesListBox.SelectedItem is not string filePath)
                {
                    return;
                }

                CustomSequenceObjectSourceFiles.Remove(filePath);
                SaveRememberedCustomSequenceObjectSources();
                StatusText = $"Removed {Path.GetFileName(filePath)} from remembered custom sequence object sources. Restart Sequence Editor to stop auto-loading it.";
                UpdateForgetState();
            };
            closeButton.Click += (_, _) => dialog.Close();

            dialog.Loaded += (_, _) => UpdateForgetState();
            dialog.ShowDialog();
        }

        private void CommitObjectPositions_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsM.CommitSequenceObjectPositions(this);
        }

        private void UpdateSelVarLinks_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsE.UpdateSequenceVarLinks(GetSEWindow(), true);
        }

        private void UpdateSequenceVarLinks_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsE.UpdateSequenceVarLinks(GetSEWindow());
        }

        private void AddDialogueWheelCam_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsE.AddDialogueWheelTemplate(GetSEWindow());
        }

        private void AddDialogueWheelDir_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsE.AddDialogueWheelTemplate(GetSEWindow(), true);
        }

        private void AddAnchorToInterps_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsK.UpdateAllInterpAnchorsVarLinks(GetSEWindow());
        }

        private void ConvertToFindByTag_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsK.convertSeqVarObjToObjByTag(GetSEWindow());
        }

        public SequenceEditorWPF GetSEWindow()
        {
            if (GetWindow(this) is SequenceEditorWPF sew)
            {
                return sew;
            }

            return null;
        }

        private void ImportSequenceFromAnotherPackage_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsM.InstallSequencePrefab(GetSEWindow());
        }

        private void CopyInstancedFullPath_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj obj)
            {
                Clipboard.SetText(obj.Export.InstancedFullPath);
            }
        }

        private void ExtractSequence_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SAction sAction &&
                (sAction.Export.ClassName is "SequenceReference" or "Sequence"))
            {
                var seqExp = sAction.Export;

                // We're going to have to modify the package to get this to work, unfortunately...

                // Remove object reference
                var props = seqExp.GetProperties();
                seqExp.RemoveProperty("ParentSequence");
                KismetHelper.RemoveAllLinks(seqExp);
                var originalIdxLink = seqExp.idxLink;

                // Set to root
                seqExp.idxLink = 0;

                SharedPackageTools.ExtractEntryToNewPackage(seqExp, x =>
                {
                    if (x)
                    {
                        SetBusy();
                    }
                    else
                    {
                        // Restore
                        seqExp.WriteProperties(props);
                        seqExp.idxLink = originalIdxLink;
                        EndBusy();
                    }
                }, x => BusyText = x, entryDoubleClick, this);
            }
        }

        private void TrimVariableLinks_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj sAction && sAction.Export != null)
            {
                KismetHelper.TrimVariableLinks(sAction.Export);
            }
        }

        public string Toolname => "SequenceEditor";

        private void AddSwitchOutlinksMenuItem_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is SObj sAction && sAction.Export != null)
            {
                if (PromptForPositiveCount("How many outlinks would you like to add?",
                        "Add switch outlinks", "1") is int howManyToAdd)
                {

                    var sw = sAction.Export;
                    var currentIdx = KismetHelper.GetOutputLinksOfNode(sw).Count;
                    for (int i = 0; i < howManyToAdd; i++)
                    {
                        KismetHelper.CreateNewOutputLink(sw, $"Link {++currentIdx}", null);
                    }

                    sw.WriteProperty(new IntProperty(currentIdx, "LinkCount"));
                }
            }
        }

        private void ColorPicker_SelectedColorChanged(object sender, RoutedPropertyChangedEventArgs<System.Windows.Media.Color?> e)
        {
            var source = (Xceed.Wpf.Toolkit.ColorPicker)sender;
            if (e.NewValue is not null)
            {
                var newColor = e.NewValue.Value.ToWinformsColor();
                switch (source.Name)
                {
                    case "ClrPcker_Background":
                        GraphEditorBackColor = newColor;
                        Settings.SequenceEditor_BackgroundColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_BoxFill":
                        BoxFillColor = newColor;
                        Settings.SequenceEditor_BoxFillColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_TitleBox":
                        TitleBoxColor = newColor;
                        Settings.SequenceEditor_TitleBoxColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_CommentText":
                        CommentTextColor = newColor;
                        Settings.SequenceEditor_CommentTextColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_BoxText":
                        BoxTextColor = newColor;
                        Settings.SequenceEditor_BoxTextColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_Connection":
                        ConnectionColor = newColor;
                        Settings.SequenceEditor_ConnectionColor = newColor.ToArgb();
                        break;
                    case "ClrPcker_VarLink":
                        VarLinkColor = newColor;
                        Settings.SequenceEditor_VarLinkColor = newColor.ToArgb();
                        break;
                }
                Settings.Save();
            }
        }

        #region InterpData Tree

        /// <summary>
        /// Resolves the InterpData export for the currently selected graph object, if applicable.
        /// Handles both InterpData directly and SeqAct_Interp (which links to InterpData via its first var link).
        /// </summary>
        private ExportEntry GetInterpDataForSelectedObject(SObj obj)
        {
            if (obj?.Export == null) return null;
            if (obj.Export.ClassName == "InterpData") return obj.Export;
            if (obj.Export.ClassName == "SeqAct_Interp" && obj is SAction action
                && action.Varlinks.Any() && action.Varlinks[0].Links.Any()
                && Pcc.IsUExport(action.Varlinks[0].Links[0]))
            {
                var linked = Pcc.GetUExport(action.Varlinks[0].Links[0]);
                if (linked.ClassName == "InterpData") return linked;
            }
            return null;
        }

        /// <summary>
        /// Builds the InterpData tree view for the given InterpData export.
        /// </summary>
        private void BuildInterpDataTree(ExportEntry interpDataExport, bool selectRoot = true)
        {
            ClearInterpDataTree();

            if (interpDataExport == null || Pcc == null)
            {
                InterpData_InterpreterWPF.UnloadExport();
                InterpData_MetadataEditor.UnloadExport();
                return;
            }

            InterpData_MetadataEditor.LoadPccData(Pcc);

            var childrenByParent = Pcc.Exports
                .GroupBy(x => x.idxLink)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.UIndex).ToList());

            var root = BuildInterpDataTreeNode(interpDataExport, null, new HashSet<int>(), childrenByParent);
            if (root == null)
            {
                InterpData_InterpreterWPF.UnloadExport();
                InterpData_MetadataEditor.UnloadExport();
                return;
            }

            root.IsExpanded = true;
            InterpDataTreeNodes.Add(root);
            if (selectRoot)
            {
                root.IsSelected = true;
                InterpData_InterpreterWPF.LoadExport(interpDataExport);
                InterpData_MetadataEditor.LoadExport(interpDataExport);
            }
        }

        private TreeViewEntry BuildInterpDataTreeNode(ExportEntry exportEntry, TreeViewEntry parent, HashSet<int> visitedUIndexes, IReadOnlyDictionary<int, List<ExportEntry>> childrenByParent)
        {
            if (!visitedUIndexes.Add(exportEntry.UIndex))
            {
                return null;
            }

            var node = new TreeViewEntry(exportEntry) { Parent = parent };
            if (!childrenByParent.TryGetValue(exportEntry.UIndex, out var children))
            {
                return node;
            }

            foreach (var child in children)
            {
                var childNode = BuildInterpDataTreeNode(child, node, visitedUIndexes, childrenByParent);
                if (childNode != null)
                {
                    node.Sublinks.Add(childNode);
                }
            }

            return node;
        }

        private void ClearInterpDataTree()
        {
            foreach (var root in InterpDataTreeNodes.ToList())
            {
                foreach (var node in root.FlattenTree())
                {
                    node.Dispose();
                }
            }

            InterpDataTreeNodes.ClearEx();
            InterpData_MetadataEditor.UnloadExport();
        }

        private ExportEntry GetSelectedInterpDataTreeExport()
        {
            return InterpDataTreeView?.SelectedItem is TreeViewEntry { Entry: ExportEntry export } ? export : null;
        }

        private ExportEntry _interpDataContextExport;

        private void InterpDataTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewEntry { Entry: ExportEntry export })
            {
                InterpData_InterpreterWPF.LoadExport(export);
                InterpData_MetadataEditor.LoadExport(export);
            }
            else if (suppressInterpDataInterpreterUnloadDepth == 0)
            {
                InterpData_InterpreterWPF.UnloadExport();
                InterpData_MetadataEditor.UnloadExport();
            }
        }

        private void QueueInterpDataEditorsReload(int exportUIndex)
        {
            pendingInterpDataEditorsReload?.Abort();
            pendingInterpDataEditorsReload = Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                pendingInterpDataEditorsReload = null;
                if (GetSelectedInterpDataTreeExport() is ExportEntry selectedExport && selectedExport.UIndex == exportUIndex)
                {
                    InterpData_InterpreterWPF.LoadExport(selectedExport);
                    InterpData_MetadataEditor.LoadExport(selectedExport);
                }
            }));
        }

        private void RefreshInterpDataTreePreserveState(int? preferredSelectedUIndex = null)
        {
            var expanded = InterpDataTreeNodes
                .SelectMany(root => root.FlattenTree())
                .Where(node => node.IsExpanded && node.Entry is ExportEntry)
                .Select(node => node.UIndex)
                .ToHashSet();

            int? selectedUIndex = preferredSelectedUIndex;
            if (!selectedUIndex.HasValue && InterpDataTreeView?.SelectedItem is TreeViewEntry selected && selected.Entry is ExportEntry)
            {
                selectedUIndex = selected.UIndex;
            }

            // Find current InterpData root to rebuild
            ExportEntry interpDataRoot = null;
            if (InterpDataTreeNodes.Count > 0 && InterpDataTreeNodes[0].Entry is ExportEntry rootExport)
            {
                interpDataRoot = rootExport;
            }

            suppressInterpDataInterpreterUnloadDepth++;
            try
            {
                BuildInterpDataTree(interpDataRoot, selectRoot: false);

                TreeViewEntry selectedNode = null;
                foreach (var node in InterpDataTreeNodes.SelectMany(root => root.FlattenTree()))
                {
                    if (expanded.Contains(node.UIndex))
                    {
                        node.IsExpanded = true;
                    }

                    if (selectedUIndex.HasValue && node.UIndex == selectedUIndex.Value)
                    {
                        selectedNode = node;
                    }
                }

                selectedNode ??= InterpDataTreeNodes.FirstOrDefault();
                if (selectedNode != null)
                {
                    selectedNode.IsSelected = true;
                    selectedNode.ExpandParents();
                }
            }
            finally
            {
                suppressInterpDataInterpreterUnloadDepth--;
            }
        }

        private void InterpDataTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            while (source is not TreeViewItem && source != null)
            {
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }

            if (source is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
                _interpDataContextExport = item.DataContext is TreeViewEntry { Entry: ExportEntry export } ? export : null;
            }
        }

        private void Sequences_TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            while (source is not TreeViewItem && source != null)
            {
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }

            if (source is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }

        private void CurrentObjects_ListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            while (source is not ListBoxItem && source != null)
            {
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }

            if (source is ListBoxItem item)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }

        private void InterpDataTreeContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu)
            {
                return;
            }

            var selectedExport = _interpDataContextExport ?? GetSelectedInterpDataTreeExport();
            bool isInterpData = selectedExport?.ClassName == "InterpData";
            bool isInterpTrackMove = selectedExport?.ClassName == "InterpTrackMove";
            bool isInterpTrackDirector = selectedExport?.ClassName == "InterpTrackDirector";
            bool isGestureTrack = selectedExport?.ClassName == "BioEvtSysTrackGesture";

            SetInterpDataContextMenuItemVisibility(menu, "ShiftInterpTrackMovesInInterpData", isInterpData ? Visibility.Visible : Visibility.Collapsed);
            SetInterpDataContextMenuItemVisibility(menu, "ShiftSelectedInterpTrackMove", isInterpTrackMove ? Visibility.Visible : Visibility.Collapsed);
            SetInterpDataContextMenuItemVisibility(menu, "GenerateCameraPresets", isInterpTrackMove || isInterpTrackDirector ? Visibility.Visible : Visibility.Collapsed);
            SetInterpDataContextMenuItemVisibility(menu, "OpenGestureAnimationImporter", isGestureTrack ? Visibility.Visible : Visibility.Collapsed);
        }

        private static void SetInterpDataContextMenuItemVisibility(ItemsControl parent, string tag, Visibility visibility)
        {
            foreach (var item in parent.Items)
            {
                if (item is MenuItem menuItem)
                {
                    if ((menuItem.Tag as string) == tag)
                    {
                        menuItem.Visibility = visibility;
                    }

                    if (menuItem.Items.Count > 0)
                    {
                        SetInterpDataContextMenuItemVisibility(menuItem, tag, visibility);
                    }
                }
            }
        }

        private void InterpDataTree_OpenInPackageEditor_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedInterpDataTreeExport() is ExportEntry export)
            {
                AllowWindowRefocus = false;
                var p = new PackageEditorWindow();
                p.Show();
                p.LoadFile(export.FileRef.FilePath, export.UIndex);
                p.Activate();
            }
        }

        private void InterpDataTree_OpenInInterpEditor_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedInterpDataTreeExport() is ExportEntry export)
            {
                // Find the InterpData root for this export
                ExportEntry interpDataExport = export;
                while (interpDataExport != null && interpDataExport.ClassName != "InterpData")
                {
                    interpDataExport = interpDataExport.Parent as ExportEntry;
                }

                if (interpDataExport != null)
                {
                    AllowWindowRefocus = false;
                    var p = new InterpEditor.InterpEditorWindow();
                    p.Show();
                    p.LoadFile(Pcc.FilePath);
                    p.SelectedInterpData = interpDataExport;
                }
            }
        }

        private void InterpDataTree_CopyUIndex_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedInterpDataTreeExport() is ExportEntry export)
            {
                Clipboard.SetText(export.UIndex.ToString());
            }
        }

        private void InterpDataTree_PackageEditorAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string actionName })
            {
                return;
            }

            if ((_interpDataContextExport ?? GetSelectedInterpDataTreeExport()) is not ExportEntry export)
            {
                return;
            }

            _interpDataContextExport = null;
            ExecuteInterpDataTreeAction(actionName, export);
        }

        private void ExecuteInterpDataTreeAction(string actionName, ExportEntry export)
        {
            switch (actionName)
            {
                case "ViewReferenceGraph":
                    new ObjectReferenceViewerWindow(export, null).Show();
                    break;
                case "AddInterpTrack":
                    if (!export.IsA("InterpGroup"))
                    {
                        MessageBox.Show(this, "Select an InterpGroup to add a track.");
                        return;
                    }

                    if (Dialogs.ClassPickerDlg.GetClass(this, MatineeHelper.GetInterpTracks(export.Game), "Choose Track to Add", "Add") is ClassInfo info)
                    {
                        ExportEntry trackExport = MatineeHelper.AddNewTrackToGroup(export, info.ClassName);
                        MatineeHelper.AddDefaultPropertiesToTrack(trackExport);
                        RefreshInterpDataTreePreserveState(trackExport.UIndex);
                    }
                    break;
                case "BulkEditInterpGroups":
                    if (export.ClassName == "InterpData")
                    {
                        var dialog = new BulkInterpEditorDialog(this, export);
                        dialog.ShowDialog();
                    }
                    break;
                case "ShiftInterpTrackMovesInInterpData":
                    if (export.ClassName == "InterpData")
                    {
                        var dialog = new Dialogs.ShiftInterpTrackDialog();
                        if (dialog.ShowDialog() == true)
                        {
                            var interpTrackMoves = export.FileRef.Exports.Where(x => x.ClassName == "InterpTrackMove" && x.IsDescendantOf(export));
                            foreach (var trackMove in interpTrackMoves)
                            {
                                if (!dialog.Parameters.IncludeAnchorObjectMoves)
                                {
                                    var moveFrame = trackMove.GetProperty<EnumProperty>("MoveFrame");
                                    if (moveFrame != null && moveFrame.Value == "IMF_AnchorObject")
                                    {
                                        continue;
                                    }
                                }

                                PackageEditorExperimentsM.ShiftInterpTrackMove(trackMove, dialog.Parameters);
                            }

                            RefreshInterpDataTreePreserveState(export.UIndex);
                        }
                    }
                    break;
                case "ShiftSelectedInterpTrackMove":
                    if (export.ClassName == "InterpTrackMove")
                    {
                        var dialog = new Dialogs.ShiftInterpTrackDialog();
                        if (dialog.ShowDialog() == true)
                        {
                            PackageEditorExperimentsM.ShiftInterpTrackMove(export, dialog.Parameters);
                            RefreshInterpDataTreePreserveState(export.UIndex);
                        }
                    }
                    break;
                case "GenerateCameraPresets":
                    bool cameraPresetApplied = export.ClassName switch
                    {
                        "InterpTrackMove" => CameraPresetDialog.GenerateForTrack(this, export),
                        "InterpTrackDirector" => CameraPresetDialog.GenerateForDirector(this, export),
                        _ => false
                    };
                    if (cameraPresetApplied)
                    {
                        RefreshInterpDataTreePreserveState(export.UIndex);
                    }
                    break;
                case "OpenGestureAnimationImporter":
                    if (export.ClassName is "BioEvtSysTrackGesture" or "SFXModule_Gestures" or "SFXSkeletalMeshActor" or "SFXSeqAct_SetAmbientPerformance")
                    {
                        var dialog = new GestureAnimationImporterDialog(export, this);
                        dialog.ShowDialog();
                    }
                    break;
                case "FindReferences":
                    SetBusy("Finding references...");
                    Task.Run(() => export.GetEntriesThatReferenceThisOne()).ContinueWith(prevTask =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            EndBusy();
                            var dlg = new ListDialog(
                                prevTask.Result.SelectMany(kvp => kvp.Value.Select(refName =>
                                    new EntryStringPair(kvp.Key, $"#{kvp.Key.UIndex} {kvp.Key.ObjectName.Instanced}: {refName}"))).ToList(),
                                $"{prevTask.Result.Count} Objects that reference #{export.UIndex} {export.InstancedFullPath}",
                                "There may be additional references to this object in the unparsed binary of some objects",
                                this);
                            dlg.Show();
                        });
                    });
                    break;
                case "Reindex":
                    if (export.FullPath.StartsWith(UnrealPackageFile.TrashPackageName))
                    {
                        MessageBox.Show("Cannot reindex exports that are part of trash package.");
                        return;
                    }

                    string prefixToReindex = export.ParentInstancedFullPath;
                    string objectName = export.ObjectName.Name;
                    if (MessageBox.Show(
                            $"Confirm reindexing of all exports named {objectName} within:\n{(string.IsNullOrEmpty(prefixToReindex) ? "Package file root" : prefixToReindex)}",
                            "Confirm Reindexing", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    {
                        int index = 1;
                        foreach (ExportEntry e in export.FileRef.Exports)
                        {
                            if (objectName == e.ObjectName.Name && e.ParentInstancedFullPath == prefixToReindex && !e.IsClass)
                            {
                                e.indexValue = index++;
                            }
                        }
                        RefreshInterpDataTreePreserveState(export.UIndex);
                    }
                    break;
                case "ExtractToFile":
                    SharedPackageTools.ExtractEntryToNewPackage(export, x => IsBusy = x, x => BusyText = x, null, this);
                    break;
                case "Clone":
                {
                    var newEntry = EntryCloner.CloneEntry(export);
                    if (ShouldAddToInterpList(export))
                    {
                        AddToInterpList(newEntry);
                    }
                    RefreshInterpDataTreePreserveState(newEntry?.UIndex);
                    break;
                }
                case "CloneTree":
                {
                    var newTreeRoot = EntryCloner.CloneTree(export);
                    if (ShouldAddToInterpList(export))
                    {
                        AddToInterpList(newTreeRoot);
                    }
                    RefreshInterpDataTreePreserveState(newTreeRoot?.UIndex);
                    break;
                }
                case "MultiClone":
                {
                    var result = PromptDialog.Prompt(this, "How many times do you want to clone this entry?", "Multiple entry cloning", "2", true);
                    if (int.TryParse(result, out var count) && count > 0)
                    {
                        int lastUIndex = export.UIndex;
                        bool addToInterpList = ShouldAddToInterpList(export);
                        for (int i = 0; i < count; i++)
                        {
                            var newEntry = EntryCloner.CloneEntry(export);
                            if (addToInterpList)
                            {
                                AddToInterpList(newEntry);
                            }
                            lastUIndex = newEntry.UIndex;
                        }
                        RefreshInterpDataTreePreserveState(lastUIndex);
                    }
                    break;
                }
                case "MultiCloneTree":
                {
                    var result = PromptDialog.Prompt(this, "How many times do you want to clone this tree?", "Multiple tree cloning", "2", true);
                    if (int.TryParse(result, out var count) && count > 0)
                    {
                        int lastUIndex = export.UIndex;
                        bool addToInterpList = ShouldAddToInterpList(export);
                        for (int i = 0; i < count; i++)
                        {
                            var newTreeRoot = EntryCloner.CloneTree(export);
                            if (addToInterpList)
                            {
                                AddToInterpList(newTreeRoot);
                            }
                            lastUIndex = newTreeRoot.UIndex;
                        }
                        RefreshInterpDataTreePreserveState(lastUIndex);
                    }
                    break;
                }
                case "RestoreExport":
                    Task.Run(() => SharedPackageTools.GetUnmoddedCandidatesForPackage(this)).ContinueWithOnUIThread(foundCandidates =>
                    {
                        if (!foundCandidates.Result.Any())
                        {
                            MessageBox.Show(this, "Cannot find any candidates for this file!");
                            return;
                        }

                        var choices = foundCandidates.Result.DiskFiles.ToList();
                        choices.AddRange(foundCandidates.Result.SFARPackageStreams.Select(x => x.Key));
                        var choice = InputComboBoxDialog.GetValue(this, "Choose file to compare to:", "Unmodified file comparison", choices, choices.Last());
                        if (string.IsNullOrEmpty(choice))
                        {
                            return;
                        }

                        using var restorePackage = MEPackageHandler.OpenMEPackage(choice, forceLoadFromDisk: true);
                        export.Data = restorePackage.GetUExport(export.UIndex).Data;
                        RefreshInterpDataTreePreserveState(export.UIndex);
                    });
                    break;
                case "Trash":
                    if (InterpDataTreeView?.SelectedItem is TreeViewEntry selected)
                    {
                        int fallbackUIndex = selected.Parent?.UIndex ?? export.UIndex;
                        var itemsToTrash = selected.FlattenTree().Select(tv => tv.Entry).Where(x => x is not null).ToList();
                        EntryPruner.TrashEntries(export.FileRef, itemsToTrash);
                        RefreshInterpDataTreePreserveState(fallbackUIndex);
                    }
                    break;
                case "SetIndicesInTreeToZero":
                    if (InterpDataTreeView?.SelectedItem is TreeViewEntry selectedForZero)
                    {
                        foreach (var entry in selectedForZero.FlattenTree().Select(tv => tv.Entry).Where(x => x is not null))
                        {
                            entry.indexValue = 0;
                        }
                        RefreshInterpDataTreePreserveState(selectedForZero.UIndex);
                    }
                    break;
                case "ExportAllData":
                case "ExportBinaryData":
                {
                    bool binaryOnly = actionName == "ExportBinaryData";
                    var d = new SaveFileDialog { Filter = "*.bin|*.bin", FileName = export.ObjectName.Instanced + ".bin" };
                    if (DirectoryMemory.ShowDialog(d) == true)
                    {
                        File.WriteAllBytes(d.FileName, binaryOnly ? export.GetBinaryData() : export.Data);
                        MessageBox.Show("Done.");
                    }
                    break;
                }
                case "ExportEmbeddedFile":
                case "ImportEmbeddedFile":
                    MessageBox.Show(this, "Embedded file import/export is not available directly in Sequence Editor for this context.");
                    break;
                case "ImportAllData":
                case "ImportBinaryData":
                {
                    bool binaryOnly = actionName == "ImportBinaryData";
                    var d = new OpenFileDialog { Filter = "*.bin|*.bin", FileName = export.ObjectName.Instanced + ".bin" };
                    if (DirectoryMemory.ShowDialog(d) == true)
                    {
                        byte[] data = File.ReadAllBytes(d.FileName);
                        if (binaryOnly)
                        {
                            export.WriteBinary(data);
                        }
                        else
                        {
                            export.Data = data;
                        }
                        MessageBox.Show("Done.");
                        RefreshInterpDataTreePreserveState(export.UIndex);
                    }
                    break;
                }
                case "GenerateExportMd5":
                {
                    var hash = MD5.HashData(export.Data);
                    var result = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        result.Append(hash[i].ToString("x2"));
                    }
                    Clipboard.SetText(result.ToString());
                    break;
                }
            }
        }

        private bool ShouldAddToInterpList(IEntry originalEntry)
        {
            if (originalEntry.Parent is not ExportEntry parentExport)
            {
                return false;
            }

            if (parentExport.IsA("InterpGroup"))
            {
                return MessageBox.Show(this,
                    "The cloned object is under an InterpGroup. Would you like to add it to the InterpTracks list?",
                    "Add to InterpTracks",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes;
            }

            if (parentExport.IsA("InterpData"))
            {
                return MessageBox.Show(this,
                    "The cloned object is under an InterpData. Would you like to add it to the InterpGroups list?",
                    "Add to InterpGroups",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes;
            }

            return false;
        }

        private static void AddToInterpList(IEntry newEntry)
        {
            MatineeHelper.AddToParentInterpList(newEntry);
        }

        #endregion
    }

    static class SequenceEditorExtensions
        {
            public static bool IsSequence(this IEntry entry) => entry.IsA("Sequence");
        }
    }
