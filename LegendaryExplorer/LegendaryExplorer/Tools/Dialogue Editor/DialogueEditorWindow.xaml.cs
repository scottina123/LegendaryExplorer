using LegendaryExplorer.Dialogs;
using LegendaryExplorer.DialogueEditor.DialogueEditorExperiments;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.Tools.ConditionalsEditor;
using LegendaryExplorer.Tools.FaceFXEditor;
using LegendaryExplorer.Tools.InterpEditor;
using LegendaryExplorer.Tools.ObjectReferenceViewer;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorer.Tools.PlotEditor;
using LegendaryExplorer.Tools.Sequence_Editor;
using LegendaryExplorer.Tools.Soundplorer;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UnrealExtensions;
using LegendaryExplorer.UnrealExtensions.Classes;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorer.Packages;
using LegendaryExplorerCore.Dialogue;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.Extensions.Collections.Generic;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Matinee;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.PlotDatabase.PlotElements;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Gammtek.Conduit.MassEffect3.SFXGame.StateEventMap;
using Newtonsoft.Json;
using Piccolo;
using Piccolo.Event;
using Piccolo.Nodes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using static LegendaryExplorer.Tools.TlkManagerNS.TLKManagerWPF;
using Key = System.Windows.Input.Key;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Xceed.Wpf.Toolkit;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using WindowStartupLocation = System.Windows.WindowStartupLocation;

namespace LegendaryExplorer.DialogueEditor
{
    /// <summary>
    /// Interaction logic for DialogueEditorWindow.xaml
    /// </summary>
    public partial class DialogueEditorWindow : WPFBase, IRecents
    {
        private static readonly System.Windows.Data.IValueConverter ReplyCategoryBrushConverter = new ReplyCategoryToBrushConverter();
        #region Declarations
        private struct SaveData
        {
            public int index;
            public float X;
            public float Y;

            public SaveData(int i) : this()
            {
                index = i;
            }
        }

        private enum ESaveViewMode
        {
            AutoSave,
            ManualSave,
            AutoGenerate
        }

        private enum ELayoutMode
        {
            Column,
            Waterfall,
            AdvancedColumn
        }

        private readonly ConvGraphEditor graphEditor;
        public ObservableCollectionExtended<IEntry> FFXAnimsets { get; } = new();
        public ObservableCollectionExtended<ConversationExtended> Conversations { get; } = new();
        public ExportEntry CurrentLoadedExport;
        public ObservableCollectionExtended<SpeakerExtended> SelectedSpeakerList { get; } = new();
        public ObservableCollectionExtended<SpeakerExtended> ListenersList { get; } = new();
        public ObservableCollectionExtended<TreeViewEntry> InterpDataTreeNodes { get; } = new();
        public ObservableCollectionExtended<ReplyChoiceNode> InlineLinkEditorLinks { get; } = new();
        private DialogueNodeExtended _SelectedDialogueNode;
        public DialogueNodeExtended SelectedDialogueNode
        {
            get => _SelectedDialogueNode;
            set => SetProperty(ref _SelectedDialogueNode, value);
        }
        private DialogueNodeExtended MirrorDialogueNode;
        private bool IsLocalUpdate; //Used to prevent uneccessary UI updates.
        //SPEAKERS
        private SpeakerExtended _SelectedSpeaker;
        public SpeakerExtended SelectedSpeaker
        {
            get => _SelectedSpeaker;
            set => SetProperty(ref _SelectedSpeaker, value);
        }
        private readonly Dictionary<string, int> SelectedStarts = new();

        private int forcedSelectStart = -1;
        private string _SelectedScript = "None";
        public string SelectedScript
        {
            get => _SelectedScript;
            set => SetProperty(ref _SelectedScript, value);
        }
        #region ConvoBox //Conversation Box Links
        private ConversationExtended _SelectedConv;
        public ConversationExtended SelectedConv
        {
            get => _SelectedConv;
            set => SetProperty(ref _SelectedConv, value);
        }
        private string _level;
        public string Level
        {
            get => _level;
            set => SetProperty(ref _level, value);
        }
        private int CurrentUIMode = -1; //Sets which panel is up.
        #endregion ConvoBox//Conversation Box Links

        private BlockingCollection<ConversationExtended> BackQueue = new();
        private BackgroundWorker BackParser = new();
        private readonly ConcurrentDictionary<int, byte> ownerResolutionInProgress = new();
        private bool NoUIRefresh; //stops graph refresh on update.
        // FOR GRAPHING
        public ObservableCollectionExtended<DObj> CurrentObjects { get; } = new();
        public ObservableCollectionExtended<DObj> SelectedObjects { get; } = new();
        private readonly List<SaveData> extraSaveData = new();
        private bool panToSelection = true;
        private DiagNode inlineLinkEditorNode;
        private bool inlineLinkEditorIsReply;
        private bool inlineLinkEditorNeedsSave;
        private System.Windows.Forms.TextBox inlineLineStrRefEditor;
        private DiagNode inlineLineStrRefNode;
        private bool inlineLineStrRefEditClosing;
        private System.Windows.Forms.Control inlinePlotFieldEditor;
        private DiagNode inlinePlotFieldNode;
        private PlotFieldEditorInfo inlinePlotFieldInfo;
        private bool inlinePlotFieldEditClosing;
        private string FileQueuedForLoad;
        private ExportEntry ExportQueuedForFocusing;
        public string CurrentFile;
        public string JSONpath;
        private List<SaveData> SavedPositions;

        public static readonly string DialogueEditorDataFolder = Path.Combine(AppDirectories.AppDataFolder, @"DialogueEditor\");
        public static readonly string OptionsPath = Path.Combine(DialogueEditorDataFolder, "DialogueEditorOptions.JSON");
        public static readonly string ME3ViewsPath = Path.Combine(DialogueEditorDataFolder, @"ME3DialogueViews\");
        public static readonly string ME2ViewsPath = Path.Combine(DialogueEditorDataFolder, @"ME2DialogueViews\");
        public static readonly string ME1ViewsPath = Path.Combine(DialogueEditorDataFolder, @"ME1DialogueViews\");
        internal static string ActorDatabasePath = Path.Combine(AppDirectories.ExecFolder, "ActorTagdb.json");
        private static bool TagDBLoaded;
        private static Dictionary<string, int> ActorStrRefs;

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, $"{CurrentFile} {value}");
        }
        private ELayoutMode LayoutMode; //0 = column, 1 = waterfall.
        private ESaveViewMode SaveViewMode; //0 = auto save, 1 = manual save, 2 = autogenerate.
        public float StartPoDStarts;
        public float StartPoDiagNodes;
        public float StartPoDReplyNodes;
        private int _RowSpace = 200;
        public int RowSpace { get => _RowSpace; set => SetProperty(ref _RowSpace, value); }
        private int _ColumnSpacee = 200;
        public int ColumnSpace { get => _ColumnSpacee; set => SetProperty(ref _ColumnSpacee, value); }
        private int _WaterfallSpace = 40;
        public int WaterfallSpace { get => _WaterfallSpace; set => SetProperty(ref _WaterfallSpace, value); }

        private Color _graphBackgroundColor = Color.FromArgb(64, 64, 64);
        public Color GraphBackgroundColor
        {
            get => _graphBackgroundColor;
            set
            {
                if (_graphBackgroundColor != value)
                {
                    _graphBackgroundColor = value;
                    DObj.graphBackgroundColor = value;
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

        private Color _boxColor = Color.FromArgb(140, 140, 140);
        public Color BoxColor
        {
            get => _boxColor;
            set
            {
                if (_boxColor != value)
                {
                    _boxColor = value;
                    DObj.boxColor = value;
                    UpdateNodeBrush();
                    if (CurrentObjects.Any())
                    {
                        RefreshView();
                    }
                }
            }
        }

        public ICommand OpenCommand { get; set; }
        public ICommand SaveCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand SaveImageCommand { get; set; }
        public ICommand SaveViewCommand { get; set; }
        public ICommand GoToCommand { get; set; }
        public ICommand AutoLayoutCommand { get; set; }
        public ICommand LoadTLKManagerCommand { get; set; }
        public ICommand OpenInCommand { get; set; }
        public ICommand SpeakerMoveUpCommand { get; set; }
        public ICommand SpeakerMoveDownCommand { get; set; }
        public ICommand AddSpeakerCommand { get; set; }
        public ICommand DeleteSpeakerCommand { get; set; }
        public ICommand ChangeNameCommand { get; set; }
        public ICommand ChangeLineSizeCommand { get; set; }
        public ICommand StartUpCommand { get; set; }
        public ICommand StartDownCommand { get; set; }
        public ICommand StartAddCommand { get; set; }
        public ICommand StartDeleteCommand { get; set; }
        public ICommand StartEditCommand { get; set; }
        public ICommand ScriptAddCommand { get; set; }
        public ICommand ScriptDeleteCommand { get; set; }
        public ICommand NodeEditCommand { get; set; }
        public ICommand NodeAddCommand { get; set; }
        public ICommand CloneNodeAndSequenceCommand { get; set; }
        public ICommand UpdateInterpLengthCommand { get; set; }
        public ICommand UpdateVOElementsAndInterpCommentCommand { get; set; }
        public ICommand NodeRemoveCommand { get; set; }
        public ICommand NodeDeleteAllLinksCommand { get; set; }
        public ICommand TestPathsCommand { get; set; }
        public ICommand DefaultColorsCommand { get; set; }
        public ICommand StageDirectionsModCommand { get; set; }
        public ICommand RecenterCommand { get; set; }
        public ICommand UpdateLayoutDefaultsCommand { get; set; }
        public ICommand SearchCommand { get; set; }
        public ICommand CopyToClipboardCommand { get; set; }
        public ICommand ForceRefreshCommand { get; set; }
        public ICommand ExtractSpeakerAudioCommand { get; set; }
        public ICommand BulkEditInterpGroupsCommand { get; set; }
        private bool HasWwbank(object param)
        {
            return SelectedConv?.WwiseBank != null;
        }
        private bool HasFFXNS(object param)
        {
            return SelectedConv?.NonSpkrFFX != null;
        }
        private bool SpkrCanMoveUp(object param)
        {
            return SelectedSpeaker != null && SelectedSpeaker.SpeakerID > 0;
        }
        private bool SpkrCanMoveDown(object param)
        {
            return SelectedSpeaker != null && SelectedSpeaker.SpeakerID >= 0 && SelectedSpeaker.SpeakerID + 3 < SelectedSpeakerList.Count;
        }
        private bool HasActiveSpkr()
        {
            return Speakers_ListBox.SelectedIndex >= 2;
        }
        private bool LineHasInterpData()
        {
            return SelectedDialogueNode?.InterpData != null;
        }
        private bool StartCanMoveUp(object param)
        {
            return SelectedConv != null && Start_ListBox.SelectedIndex > 0;
        }
        private bool StartCanMoveDown(object param)
        {
            return SelectedConv != null && Start_ListBox.SelectedIndex >= 0 && Start_ListBox.SelectedIndex < Start_ListBox.Items.Count - 1;
        }
        private bool StartCanDelete()
        {
            return SelectedConv != null && Start_ListBox.SelectedIndex >= 0 && Start_ListBox.Items.Count > 1;
        }
        private bool ScriptCanDelete()
        {
            return SelectedConv != null && Script_ListBox.SelectedIndex > 0;
        }
        #endregion Declarations

        #region Startup/Exit
        public DialogueEditorWindow() : base("Dialogue Editor")
        {
            LoadCommands();
            StatusText = "Select package file to load";
            SelectedSpeaker = new SpeakerExtended(-3, "None");

            InitializeComponent();
            SortBottomViewportTabsAlphabetically();
            InlineLinkEditor_DataGrid.ItemsSource = InlineLinkEditorLinks;
            InlineLinkEditor_DataGrid.MouseDoubleClick += InlineLinkEditor_DataGrid_MouseDoubleClick;
            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, LoadFile);

            // Apply theme-appropriate colors based on current dark mode setting
            ApplyThemeDefaults();

            // Subscribe to theme changes to update graph colors dynamically
            ThemeManager.ThemeChanged += OnThemeChanged;

            graphEditor = (ConvGraphEditor)GraphHost.Child;
            graphEditor.BackColor = GraphBackgroundColor;
            graphEditor.Camera.MouseDown += backMouseDown_Handler;
            graphEditor.Camera.MouseUp += back_MouseUp;
            graphEditor.Camera.ViewTransformChanged += graphEditor_ViewTransformChanged;

            this.graphEditor.Click += graphEditor_Click;
            this.graphEditor.DragDrop += DialogueEditor_DragDrop;
            this.graphEditor.DragEnter += DialogueEditor_DragEnter;

            Node_Combo_GUIStyle.ItemsSource = Enums.GetValues<EConvGUIStyles>();
            Node_Combo_ReplyType.ItemsSource = Enums.GetValues<EReplyTypes>();
            // Detect if theme changed while editor was closed so we skip stale saved colors
            bool themeChangedWhileEditorClosed = false;
            if (File.Exists(OptionsPath)) //Handle options
            {
                var options = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(OptionsPath));
                if (options.TryGetValue("LastThemeDarkMode", out var lastThemeValue)
                    && bool.TryParse(lastThemeValue?.ToString(), out var lastThemeDarkMode))
                {
                    themeChangedWhileEditorClosed = lastThemeDarkMode != Settings.Global_DarkMode_Enabled;
                }
                if (options.ContainsKey("LineTextSize"))
                {
                    ChangeLineSize(null);
                }
                // Only load saved color settings if the theme hasn't changed since last save.
                // If theme changed, the correct colors were already set by ThemeManager.ApplyGraphEditorTheme
                // and ApplyThemeDefaults above — loading stale colors would override them.
                if (!themeChangedWhileEditorClosed)
                {
                    if (options.ContainsKey("LineTextColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["LineTextColor"]);
                        DBox.lineColor = c;
                        ClrPcker_Line.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("LinkTextColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["LinkTextColor"]);
                        DObj.linkTextColor = c;
                        ClrPcker_LinkText.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("ParaIntRColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["ParaIntRColor"]);
                        DObj.paraintColor = c;
                        ClrPcker_ParaInt.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("RenIntRColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["RenIntRColor"]);
                        DObj.renintColor = c;
                        ClrPcker_RenInt.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("AgreeRColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["AgreeRColor"]);
                        DObj.agreeColor = c;
                        ClrPcker_Agree.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("DisagreeRColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["DisagreeRColor"]);
                        DObj.disagreeColor = c;
                        ClrPcker_Disagree.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("FriendlyRColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["FriendlyRColor"]);
                        DObj.friendlyColor = c;
                        ClrPcker_Friendly.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("HostileRColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["HostileRColor"]);
                        DObj.hostileColor = c;
                        ClrPcker_Hostile.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("ConnectionColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["ConnectionColor"]);
                        DObj.connectionColor = c;
                        ClrPcker_Connection.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("EntryPenColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["EntryPenColor"]);
                        DObj.entryPenColor = c;
                        ClrPcker_EntryPen.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("EntryColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["EntryColor"]);
                        DObj.entryColor = c;
                        ClrPcker_Entry.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("ReplyColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["ReplyColor"]);
                        DObj.replyColor = c;
                        ClrPcker_Reply.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("ReplyPenColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["ReplyPenColor"]);
                        DObj.replyPenColor = c;
                        ClrPcker_ReplyPen.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("GraphBackgroundColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["GraphBackgroundColor"]);
                        GraphBackgroundColor = c;
                        ClrPcker_GraphBackground.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("BoxColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["BoxColor"]);
                        BoxColor = c;
                        ClrPcker_BoxColor.SelectedColor = c.ToWPFColor();
                    }
                    if (options.ContainsKey("BoxTextColor"))
                    {
                        var c = ColorTranslator.FromHtml((string)options["BoxTextColor"]);
                        DObj.boxTextColor = c;
                        ClrPcker_BoxText.SelectedColor = c.ToWPFColor();
                    }
                }
                if (options.ContainsKey("AutoSaveMode"))
                {
                    int.TryParse(options["AutoSaveMode"].ToString(), out int a);
                    SaveViewMode = (ESaveViewMode)a;
                }
                if (options.ContainsKey("LayoutMode"))
                {
                    int.TryParse(options["LayoutMode"].ToString(), out int l);
                    LayoutMode = (ELayoutMode)l;
                }
                if (options.ContainsKey("RowSpace"))
                {
                    int.TryParse(options["RowSpace"].ToString(), out int rs);
                    RowSpace = rs;
                }
                if (options.ContainsKey("ColumnSpace"))
                {
                    int.TryParse(options["ColumnSpace"].ToString(), out int cs);
                    ColumnSpace = cs;
                }
                if (options.ContainsKey("WaterfallSpace"))
                {
                    int.TryParse(options["WaterfallSpace"].ToString(), out int ws);
                    WaterfallSpace = ws;
                }
                if (options.ContainsKey("LinesAtTop"))
                    ShowLinesOnTop_MenuItem.IsChecked = (bool)options["LinesAtTop"];
                if (options.ContainsKey("OutputNumbers"))
                    HideEntryOutput_MenuItem.IsChecked = (bool)options["OutputNumbers"];
            }

            // If theme changed while editor was closed, or no options file exists,
            // sync color pickers to the current (correct) static color values.
            // ApplyThemeDefaults() + ThemeManager already set the right static colors.
            if (themeChangedWhileEditorClosed || !File.Exists(OptionsPath))
            {
                Menu_LineSize_10.IsChecked = true;
                ClrPcker_Line.SelectedColor = DBox.lineColor.ToWPFColor();
                ClrPcker_LinkText.SelectedColor = DObj.linkTextColor.ToWPFColor();
                ClrPcker_ParaInt.SelectedColor = DObj.paraintColor.ToWPFColor();
                ClrPcker_RenInt.SelectedColor = DObj.renintColor.ToWPFColor();
                ClrPcker_Agree.SelectedColor = DObj.agreeColor.ToWPFColor();
                ClrPcker_Disagree.SelectedColor = DObj.disagreeColor.ToWPFColor();
                ClrPcker_Friendly.SelectedColor = DObj.friendlyColor.ToWPFColor();
                ClrPcker_Hostile.SelectedColor = DObj.hostileColor.ToWPFColor();
                ClrPcker_Connection.SelectedColor = DObj.connectionColor.ToWPFColor();
                ClrPcker_Entry.SelectedColor = DObj.entryColor.ToWPFColor();
                ClrPcker_EntryPen.SelectedColor = DObj.entryPenColor.ToWPFColor();
                ClrPcker_Reply.SelectedColor = DObj.replyColor.ToWPFColor();
                ClrPcker_ReplyPen.SelectedColor = DObj.replyPenColor.ToWPFColor();
                ClrPcker_GraphBackground.SelectedColor = GraphBackgroundColor.ToWPFColor();
                ClrPcker_BoxColor.SelectedColor = BoxColor.ToWPFColor();
                ClrPcker_BoxText.SelectedColor = DObj.boxTextColor.ToWPFColor();
                // Also update instance fields to match theme-set static fields
                _graphBackgroundColor = DObj.graphBackgroundColor;
                if (graphEditor != null) graphEditor.BackColor = _graphBackgroundColor;
                _boxColor = DObj.boxColor;
                UpdateNodeBrush();
            }

            UpdateLayoutDefaults("startup");
        }

        private void SortBottomViewportTabsAlphabetically()
        {
            var selectedTab = BottomViewportTabControl?.SelectedItem as TabItem;
            var orderedTabs = BottomViewportTabControl?.Items
                .OfType<TabItem>()
                .OrderBy(t => t.Header?.ToString(), StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (orderedTabs == null || orderedTabs.Count == 0)
            {
                return;
            }

            BottomViewportTabControl.Items.Clear();
            foreach (var tab in orderedTabs)
            {
                BottomViewportTabControl.Items.Add(tab);
            }

            if (selectedTab != null && orderedTabs.Contains(selectedTab))
            {
                BottomViewportTabControl.SelectedItem = selectedTab;
            }
        }

        public DialogueEditorWindow(ExportEntry export) : this()
        {
            FileQueuedForLoad = export.FileRef.FilePath;
            ExportQueuedForFocusing = export;
        }
        private void LoadCommands()
        {
            OpenCommand = new GenericCommand(OpenPackage);
            SaveCommand = new GenericCommand(SavePackage, PackageIsLoaded);
            SaveAsCommand = new GenericCommand(SavePackageAs, PackageIsLoaded);
            SaveViewCommand = new GenericCommand(() => saveView(), () => CurrentObjects.Any);
            SaveImageCommand = new GenericCommand(SaveImage, () => CurrentObjects.Any);
            AutoLayoutCommand = new GenericCommand(AutoLayout, () => CurrentObjects.Any);
            GoToCommand = new GenericCommand(GoToBoxOpen);
            LoadTLKManagerCommand = new GenericCommand(LoadTLKManager);
            OpenInCommand = new RelayCommand(OpenInAction, CanOpenIn);
            SpeakerMoveUpCommand = new RelayCommand(SpeakerMoveAction, SpkrCanMoveUp);
            SpeakerMoveDownCommand = new RelayCommand(SpeakerMoveAction, SpkrCanMoveDown);
            AddSpeakerCommand = new GenericCommand(SpeakerAdd);
            DeleteSpeakerCommand = new GenericCommand(SpeakerDelete, HasActiveSpkr);
            ChangeNameCommand = new GenericCommand(SpeakerGoToName, HasActiveSpkr);
            ChangeLineSizeCommand = new RelayCommand(ChangeLineSize);
            StartUpCommand = new RelayCommand(StartMoveAction, StartCanMoveUp);
            StartDownCommand = new RelayCommand(StartMoveAction, StartCanMoveDown);
            StartAddCommand = new RelayCommand(StartAddEdit);
            StartDeleteCommand = new GenericCommand(StartDelete, StartCanDelete);
            StartEditCommand = new RelayCommand(StartAddEdit);
            ScriptAddCommand = new GenericCommand(Script_Add);
            ScriptDeleteCommand = new GenericCommand(Script_Delete, ScriptCanDelete);
            NodeEditCommand = new RelayCommand(DialogueNode_OpenLinkEditor);
            NodeAddCommand = new RelayCommand(DialogueNode_Add);
            CloneNodeAndSequenceCommand = new GenericCommand(() => DialogueEditorExperimentsE.CloneNodeAndSequence(this), LineHasInterpData);
            UpdateInterpLengthCommand = new GenericCommand(() => DialogueEditorExperimentsE.UpdateInterpLengthExperiment(this), LineHasInterpData);
            UpdateVOElementsAndInterpCommentCommand = new GenericCommand(() => DialogueEditorExperimentsE.UpdateVOAndCommentExperiment(this), LineHasInterpData);
            NodeRemoveCommand = new RelayCommand(DialogueNode_Delete);
            NodeDeleteAllLinksCommand = new RelayCommand(DialogueNode_DeleteLinks);
            StageDirectionsModCommand = new RelayCommand(StageDirections_Modify);
            TestPathsCommand = new GenericCommand(TestPaths);
            DefaultColorsCommand = new GenericCommand(ResetColorsToDefault);
            RecenterCommand = new GenericCommand(graphEditor_PanTo);
            UpdateLayoutDefaultsCommand = new RelayCommand(UpdateLayoutDefaults);
            SearchCommand = new GenericCommand(SearchDialogue, () => CurrentObjects.Any);
            CopyToClipboardCommand = new RelayCommand(CopyStringToClipboard);
            ForceRefreshCommand = new RelayCommand(ForceRefresh);
            ExtractSpeakerAudioCommand = new GenericCommand(ExtractSpeakerAudio, () => SelectedSpeaker != null && SelectedSpeaker.SpeakerID >= -2);
            BulkEditInterpGroupsCommand = new GenericCommand(OpenBulkInterpEditor, LineHasInterpData);
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
                DBox.lineColor = Color.FromArgb(130, 180, 255);  // Light blue for better visibility
                DObj.linkTextColor = Color.White;  // White for link text in dark mode
                DObj.paraintColor = Color.FromArgb(100, 149, 237);  // Cornflower blue
                DObj.renintColor = Color.FromArgb(255, 99, 71);  // Tomato
                DObj.agreeColor = Color.FromArgb(135, 206, 250);  // Light sky blue
                DObj.disagreeColor = Color.FromArgb(255, 160, 122);  // Light salmon
                DObj.friendlyColor = Color.FromArgb(100, 149, 237);  // Cornflower blue
                DObj.hostileColor = Color.FromArgb(205, 92, 92);  // Indian red
                DObj.connectionColor = Color.White;  // White connection lines for dark mode
                DObj.entryColor = Color.FromArgb(75, 0, 130);  // Indigo
                DObj.entryPenColor = Color.FromArgb(80, 80, 80);  // Dark grey
                DObj.replyColor = Color.FromArgb(85, 107, 47);  // Dark olive green
                DObj.replyPenColor = Color.FromArgb(80, 80, 80);  // Dark grey
                GraphBackgroundColor = Color.FromArgb(30, 30, 30);  // Dark background
                BoxColor = Color.FromArgb(45, 45, 48);  // VS dark box color
                DObj.boxTextColor = Color.FromArgb(220, 220, 220);  // Light text
            }
            else
            {
                // Light theme defaults - matching the classic Dialogue Editor look
                DBox.lineColor = Color.White;  // White for spoken line text
                DObj.linkTextColor = Color.Black;  // Black for link text in light mode
                DObj.paraintColor = Color.Blue;
                DObj.renintColor = Color.Red;
                DObj.agreeColor = Color.DodgerBlue;
                DObj.disagreeColor = Color.Tomato;
                DObj.friendlyColor = Color.FromArgb(3, 3, 116);  // Dark blue
                DObj.hostileColor = Color.FromArgb(116, 3, 3);  // Dark red
                DObj.connectionColor = Color.Black;  // Black connection lines for light mode
                DObj.entryColor = Color.FromArgb(218, 165, 32);  // Goldenrod for entry headers
                DObj.entryPenColor = Color.Black;
                DObj.replyColor = Color.FromArgb(64, 224, 208);  // Turquoise for reply headers
                DObj.replyPenColor = Color.Black;
                GraphBackgroundColor = Color.FromArgb(115, 115, 115);  // Gray background
                BoxColor = Color.FromArgb(80, 80, 80);  // Dark gray node boxes
                DObj.boxTextColor = Color.White;
            }
        }

        /// <summary>
        /// Handles theme changes from the ThemeManager.
        /// Resets graph colors to theme defaults when user switches between light/dark mode.
        /// </summary>
        private void OnThemeChanged(object sender, bool isDarkMode)
        {
            // Ensure we're on the UI thread
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnThemeChanged(sender, isDarkMode)));
                return;
            }

            // Apply theme defaults (overrides any user customizations)
            ApplyThemeDefaults();

            // Update the graph editor background
            if (graphEditor != null)
            {
                graphEditor.BackColor = GraphBackgroundColor;
            }

            // Update node brush
            UpdateNodeBrush();

            // Update color pickers to reflect the new theme colors
            ClrPcker_Line.SelectedColor = DBox.lineColor.ToWPFColor();
            ClrPcker_LinkText.SelectedColor = DObj.linkTextColor.ToWPFColor();
            ClrPcker_ParaInt.SelectedColor = DObj.paraintColor.ToWPFColor();
            ClrPcker_RenInt.SelectedColor = DObj.renintColor.ToWPFColor();
            ClrPcker_Agree.SelectedColor = DObj.agreeColor.ToWPFColor();
            ClrPcker_Disagree.SelectedColor = DObj.disagreeColor.ToWPFColor();
            ClrPcker_Friendly.SelectedColor = DObj.friendlyColor.ToWPFColor();
            ClrPcker_Hostile.SelectedColor = DObj.hostileColor.ToWPFColor();
            ClrPcker_Connection.SelectedColor = DObj.connectionColor.ToWPFColor();
            ClrPcker_Entry.SelectedColor = DObj.entryColor.ToWPFColor();
            ClrPcker_EntryPen.SelectedColor = DObj.entryPenColor.ToWPFColor();
            ClrPcker_Reply.SelectedColor = DObj.replyColor.ToWPFColor();
            ClrPcker_ReplyPen.SelectedColor = DObj.replyPenColor.ToWPFColor();
            ClrPcker_GraphBackground.SelectedColor = GraphBackgroundColor.ToWPFColor();
            ClrPcker_BoxColor.SelectedColor = BoxColor.ToWPFColor();
            ClrPcker_BoxText.SelectedColor = DObj.boxTextColor.ToWPFColor();

            // Refresh the view if there are objects loaded
            if (CurrentObjects.Any())
            {
                RefreshView();
            }
        }

        private void DialogueEditorWPF_Loaded(object sender, RoutedEventArgs e)
        {
            if (FileQueuedForLoad != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    //Wait for all children to finish loading
                    LoadFile(FileQueuedForLoad);
                    FileQueuedForLoad = null;

                    if (ExportQueuedForFocusing != null && ExportQueuedForFocusing.ClassName == "BioConversation")
                    {
                        Conversations_ListBox.SelectedItem = Conversations.FirstOrDefault(x => x.Export.UIndex == ExportQueuedForFocusing.UIndex);
                        SetUIMode(0, true);
                        ExportQueuedForFocusing = null;
                    }

                    Activate();
                }));
            }
        }
        private async void SavePackageAs()
        {
            string extension = Path.GetExtension(Pcc.FilePath);
            SaveFileDialog d = new() { Filter = $"*{extension}|*{extension}" };
            if (d.ShowDialog() == true)
            {
                await Pcc.SaveAsync(d.FileName);
                MessageBox.Show("Done.");
            }
        }
        private async void SavePackage()
        {
            await Pcc.SaveAsync();
        }
        private async void DialogueEditorWPF_Closing(object sender, CancelEventArgs e)
        {
            if (e.Cancel)
                return;

            if (AutoSaveView_MenuItem.IsChecked)
                saveView();

            var options = new Dictionary<string, object>
            {
                {"LineTextSize", DBox.LineScaleOption},
                {"LineTextColor", ColorTranslator.ToHtml(DBox.lineColor)},
                {"LinkTextColor", ColorTranslator.ToHtml(DObj.linkTextColor)},
                {"ParaIntRColor", ColorTranslator.ToHtml(DObj.paraintColor)},
                {"RenIntRColor", ColorTranslator.ToHtml(DObj.renintColor)},
                {"AgreeRColor", ColorTranslator.ToHtml(DObj.agreeColor)},
                {"DisagreeRColor", ColorTranslator.ToHtml(DObj.disagreeColor)},
                {"FriendlyRColor", ColorTranslator.ToHtml(DObj.friendlyColor)},
                {"HostileRColor", ColorTranslator.ToHtml(DObj.hostileColor)},
                {"ConnectionColor", ColorTranslator.ToHtml(DObj.connectionColor)},
                {"EntryColor", ColorTranslator.ToHtml(DObj.entryColor)},
                {"ReplyColor", ColorTranslator.ToHtml(DObj.replyColor)},
                {"EntryPenColor", ColorTranslator.ToHtml(DObj.entryPenColor)},
                {"ReplyPenColor", ColorTranslator.ToHtml(DObj.replyPenColor)},
                {"GraphBackgroundColor", ColorTranslator.ToHtml(DObj.graphBackgroundColor)},
                {"BoxColor", ColorTranslator.ToHtml(DObj.boxColor)},
                {"BoxTextColor", ColorTranslator.ToHtml(DObj.boxTextColor)},
                {"LinesAtTop", DBox.LinesAtTop},
                {"OutputNumbers", DObj.OutputNumbers},
                {"AutoSaveMode", (int)SaveViewMode},
                {"LayoutMode", (int)LayoutMode},
                {"RowSpace", RowSpace},
                {"ColumnSpace", ColumnSpace},
                {"WaterfallSpace", WaterfallSpace},
                {"LastThemeDarkMode", Settings.Global_DarkMode_Enabled},
            };
            await Task.Run(() =>
            {
                if (!Directory.Exists(DialogueEditorDataFolder))
                    Directory.CreateDirectory(DialogueEditorDataFolder);
                File.WriteAllText(OptionsPath, JsonConvert.SerializeObject(options));
            }
            );

            // Unsubscribe from theme changes to prevent memory leaks
            ThemeManager.ThemeChanged -= OnThemeChanged;

            //Code here remove these objects from leaking the window memory
            graphEditor.Camera.MouseDown -= backMouseDown_Handler;
            graphEditor.Camera.MouseUp -= back_MouseUp;
            graphEditor.Camera.ViewTransformChanged -= graphEditor_ViewTransformChanged;
            graphEditor.Click -= graphEditor_Click;
            graphEditor.DragDrop -= DialogueEditor_DragDrop;
            graphEditor.DragEnter -= DialogueEditor_DragEnter;
            CurrentObjects.ForEach(x =>
            {
                x.MouseDown -= node_MouseDown;
                x.Click -= node_Click;
                x.Dispose();
            });
            CurrentObjects.Clear();
            graphEditor.Dispose();
            Properties_InterpreterWPF.Dispose();
            InterpData_InterpreterWPF.Dispose();
            SoundpanelWPF_F.Dispose();
            SoundpanelWPF_M.Dispose();
            FaceFXAnimSetEditorControl_F.Dispose();
            FaceFXAnimSetEditorControl_M.Dispose();
            ClearInterpDataTree();
            GraphHost.Child = null; //This seems to be required to clear OnChildGotFocus handler from WinFormsHost
            GraphHost.Dispose();
            DataContext = null;
            DispatcherHelper.EmptyQueue();
            RecentsController?.Dispose();
        }

        private void graphEditor_ViewTransformChanged(object sender, PPropertyEventArgs e)
        {
            if (inlineLineStrRefNode != null)
            {
                UpdateInlineLineStrRefEditorPosition(inlineLineStrRefNode);
            }
            if (inlinePlotFieldNode != null)
            {
                UpdateInlinePlotFieldEditorPosition(inlinePlotFieldNode);
            }
        }

        private void OpenPackage()
        {
            OpenFileDialog d = AppDirectories.GetOpenPackageDialog();
            if (d.ShowDialog() == true)
            {
                try
                {
                    LoadFile(d.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to open file:\n" + ex.Message);
                }
            }
        }
        private bool PackageIsLoaded()
        {
            //System.Diagnostics.Debug.WriteLine("Package Is Loaded.");
            return Pcc != null;
        }

        public void LoadFile(string fileName, int uIndex)
        {
            LoadFile(fileName);
            var convo = Conversations.FirstOrDefault(c => c.UIndex == uIndex);
            if (convo != null)
            {
                Conversations_ListBox.SelectedItem = convo;
            }
        }
        public void LoadFile(string fileName)
        {
            try
            {
                Conversations.ClearEx();
                SelectedSpeakerList.ClearEx();
                SelectedObjects.ClearEx();
                SelectedDialogueNode = null;
                SelectedConv = null;

                LoadMEPackage(fileName);
                var loadedPackage = Pcc;
                if (loadedPackage != null)
                {
                    Task.Run(() => ConversationExtended.WarmOwnerTagCacheForPackage(loadedPackage));
                }
                CurrentFile = Path.GetFileName(fileName);
                LoadConversations();
                if (Conversations.IsEmpty())
                {
                    UnloadFile();
                    MessageBox.Show("This file does not contain any Conversations!");
                    return;
                }
                FirstParse();
                RightBarColumn.Width = new GridLength(0);
                graphEditor.nodeLayer.RemoveAllChildren();
                graphEditor.edgeLayer.RemoveAllChildren();

                RecentsController.AddRecent(fileName, false, Pcc?.Game);
                RecentsController.SaveRecentList(true);

                Title = $"Dialogue Editor - {fileName}";
                StatusText = null;

                Level = Path.GetFileName(Pcc.FilePath);
                if (Pcc.Game.IsGame1())
                {
                    if (Pcc.Localization == MELocalization.None)
                    {
                        Level = $"{Level.Remove(Level.Length - 4)}_LOC_INT{Path.GetExtension(Pcc.FilePath)}";
                    }
                }
                else
                {
                    Level = $"{Level.Remove(Level.Length - 12)}.pcc";
                }

                //Build Animset list
                FFXAnimsets.ClearEx();
                foreach (var exp in Pcc.Exports.Where(exp => exp.ClassName == "FaceFXAnimSet"))
                {
                    FFXAnimsets.Add(exp);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error:\n" + ex.Message);
                Title = "Dialogue Editor";
                UnloadFile();
            }
        }
        private void UnloadFile()
        {
            RightBarColumn.Width = new GridLength(0);
            SelectedConv = null;
            CurrentLoadedExport = null;
            Conversations.ClearEx();
            SelectedSpeakerList.ClearEx();
            Properties_InterpreterWPF.UnloadExport();
            InterpData_InterpreterWPF.UnloadExport();
            SoundpanelWPF_F.UnloadExport();
            SoundpanelWPF_M.UnloadExport();
            FaceFXAnimSetEditorControl_F.UnloadExport();
            FaceFXAnimSetEditorControl_M.UnloadExport();
            ClearInterpDataTree();
            CurrentObjects.Clear();
            graphEditor.nodeLayer.RemoveAllChildren();
            graphEditor.edgeLayer.RemoveAllChildren();
            CurrentFile = null;
            UnLoadMEPackage();
            StatusText = "Select a package file to load";
        }
        #endregion Startup/Exit

        #region Parsing
        private void LoadConversations()
        {
            Conversations.ClearEx();
            foreach (var exp in Pcc.Exports.Where(exp => exp.ClassName.Equals("BioConversation")))
            {
                Conversations.Add(new ConversationExtended(exp));
            }
        }
        private async void FirstParse()
        {
            BackQueue = new BlockingCollection<ConversationExtended>();
            BackParser = new BackgroundWorker
            {
                WorkerReportsProgress = true,
                WorkerSupportsCancellation = true
            };
            BackParser.DoWork += BackParse;
            BackParser.RunWorkerCompleted += BackParser_RunWorkerCompleted;
            BackParser.RunWorkerAsync();

            if (!TLKLoader.TlkFirstLoadDone)
            {
                bool waitingfortlks = true;
                while (waitingfortlks)
                {
                    waitingfortlks = await CheckProcess(100, TLKLoader.TlkFirstLoadDone, true);
                }
            }

            if (SelectedConv != null && SelectedConv.IsFirstParsed == false) //Get Active setup pronto.
            {
                SelectedConv.ParseStartingList();
                SelectedConv.ParseSpeakers();
                GenerateSpeakerList();
                SelectedConv.ParseEntryList(TLKLookup);
                SelectedConv.ParseReplyList(TLKLookup);
                SelectedConv.ParseScripts();
                SelectedConv.ParseNSFFX();
                SelectedConv.ParseSequence();
                SelectedConv.ParseWwiseBank();
                SelectedConv.ParseStageDirections(TLKLookup);

                SelectedConv.IsFirstParsed = true;
                SelectedConv.DetailedParse();
            }

            foreach (var conv in Conversations.Where(conv => conv.IsFirstParsed == false)) //Get Speakers entry and replies plus convo data first
            {
                conv.ParseStartingList();
                conv.ParseSpeakers();
                conv.ParseEntryList(TLKLookup);
                conv.ParseReplyList(TLKLookup);
                conv.ParseScripts();
                conv.ParseNSFFX();
                conv.ParseSequence();
                conv.ParseWwiseBank();
                conv.ParseStageDirections(TLKLookup);
                conv.IsFirstParsed = true;

                if (!conv.IsParsed)
                    BackQueue.Add(conv);
            }
#if DEBUG
            Debug.WriteLine("FirstParse Done");
#endif
            BackQueue.CompleteAdding();

            foreach (var conv in Conversations)
            {
                QueueOwnerFriendlyNameResolution(conv);
            }
        }

        private static bool NeedsOwnerFriendlyName(SpeakerExtended speaker)
        {
            if (speaker == null)
                return false;

            var friendlyName = speaker.FriendlyName;
            return string.IsNullOrWhiteSpace(friendlyName)
                   || friendlyName == "No data"
                   || friendlyName.Contains("no_owner", StringComparison.OrdinalIgnoreCase);
        }

        private async void QueueOwnerFriendlyNameResolution(ConversationExtended sourceConv)
        {
            if (sourceConv == null)
                return;

            var ownerSpeaker = sourceConv.Speakers.FirstOrDefault(s => s.SpeakerID == -1);
            if (!NeedsOwnerFriendlyName(ownerSpeaker))
                return;

            if (!ownerResolutionInProgress.TryAdd(sourceConv.UIndex, 0))
                return;

            try
            {
                await Task.Run(() => sourceConv.ResolveOwnerTag());

                if (SelectedConv != null && SelectedConv.UIndex == sourceConv.UIndex)
                {
                    var sourceOwner = sourceConv.Speakers.FirstOrDefault(s => s.SpeakerID == -1);
                    var selectedOwner = SelectedConv.Speakers.FirstOrDefault(s => s.SpeakerID == -1);
                    if (sourceOwner != null && selectedOwner != null && !NeedsOwnerFriendlyName(sourceOwner))
                    {
                        selectedOwner.FriendlyName = sourceOwner.FriendlyName;
                        GenerateSpeakerList();
                        Speakers_ListBox.Items.Refresh();
                        Node_Combo_Spkr.Items.Refresh();
                        Node_Combo_Lstnr.Items.Refresh();
                    }
                }
            }
            finally
            {
                ownerResolutionInProgress.TryRemove(sourceConv.UIndex, out _);
            }
        }

        private void BackParse(object sender, DoWorkEventArgs e)
        {
#if DEBUG
            Debug.WriteLine("BackParse Starting");
#endif
            //Do minor stuff
            foreach (var conv in BackQueue.GetConsumingEnumerable(CancellationToken.None))
            {
                conv.DetailedParse();
            }
        }
        private void BackParser_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            BackParser.CancelAsync();
#if DEBUG
            Debug.WriteLine("BackParse Done");
#endif
        }

        private string TLKLookup(int id, IMEPackage package)
        {
            return GlobalFindStrRefbyID(id, package);
        }

        private void GenerateSpeakerList()
        {
            SelectedSpeakerList.ClearEx();

            foreach (var spkr in SelectedConv.Speakers)
            {
                SelectedSpeakerList.Add(spkr);
            }
        }

        private void ParseNodeData(DialogueNodeExtended node)
        {
            try
            {
                var nodeprop = node.NodeProp;
                node.Listener = nodeprop.GetProp<IntProperty>("nListenerIndex");  //ME3//ME2//ME1
                if (node.IsReply)
                {
                    node.IsSkippable = false; //ME3/
                    node.IsUnskippable = nodeprop.GetProp<BoolProperty>("bUnskippable");
                }
                else
                {
                    node.IsSkippable = nodeprop.GetProp<BoolProperty>("bSkippable"); //ME3/
                    node.IsUnskippable = false;
                }
                node.ConditionalParam = nodeprop.GetProp<IntProperty>("nConditionalParam");
                node.TransitionParam = nodeprop.GetProp<IntProperty>("nStateTransitionParam");
                node.CameraIntimacy = nodeprop.GetProp<IntProperty>("nCameraIntimacy");
                node.IsAmbient = nodeprop.GetProp<BoolProperty>("bAmbient");
                node.IsNonTextLine = nodeprop.GetProp<BoolProperty>("bNonTextLine");
                node.IgnoreBodyGesture = nodeprop.GetProp<BoolProperty>("bIgnoreBodyGestures");
                node.GUIStyle = Enums.Parse<EConvGUIStyles>(nodeprop.GetProp<EnumProperty>("eGUIStyle").Value.Name);
                bool isNotGame3Reply = true;
                if (Pcc.Game.IsGame3())
                {
                    node.HideSubtitle = nodeprop.GetProp<BoolProperty>("bAlwaysHideSubtitle");
                    if (node.IsReply)
                    {
                        isNotGame3Reply = false;
                        node.IsDefaultAction = nodeprop.GetProp<BoolProperty>("bIsDefaultAction");
                        node.IsMajorDecision = nodeprop.GetProp<BoolProperty>("bIsMajorDecision");
                    }
                }
                if (isNotGame3Reply)
                {
                    //cannot set these unconditionally earlier, since the propertychanged event will alter the nodeProp, overwriting the real value!
                    node.IsDefaultAction = false;
                    node.IsMajorDecision = false;
                }

                var lengthprop = node.InterpData?.GetProperty<FloatProperty>("InterpLength");
                if (lengthprop != null)
                {
                    node.InterpLength = lengthprop.Value;
                }

                if (node.FiresConditional)
                {
                    node.ConditionalPlotPath = PlotDatabases.FindPlotConditionalByID(node.ConditionalOrBool, Pcc.Game)?.Path;
                }
                else
                {
                    node.ConditionalPlotPath = PlotDatabases.FindPlotBoolByID(node.ConditionalOrBool, Pcc.Game)?.Path;
                }
                node.TransitionPlotPath = PlotDatabases.FindPlotTransitionByID(node.Transition, Pcc.Game)?.Path;
            }
            catch (Exception e) when (App.IsDebug)
            {
                throw new Exception("DiagNodeParse Failed.", e);
            }
        }

        public int ParseActorsNames(ConversationExtended conv, string tag)
        {
            if (Pcc.Game.IsGame1())
            {
                try
                {
                    var actors = Pcc.Exports.Where(xp => xp.ClassName == "BioPawn");
                    ExportEntry actor = actors.First(a => a.GetProperty<NameProperty>("Tag").ToString() == tag);
                    var behav = actor.GetProperty<ObjectProperty>("m_oBehavior");
                    var set = Pcc.GetUExport(behav.Value).GetProperty<ObjectProperty>("m_oActorType");
                    var strrefprop = Pcc.GetUExport(set.Value).GetProperty<StringRefProperty>("ActorGameNameStrRef");
                    if (strrefprop != null)
                    {
                        return strrefprop.Value;
                    }
                }
                catch
                {
                    return -2;
                }
            }

            // ME2/ME3 need to load non-LOC file.  Or parse a JSON.

            return 0;
        }
        #endregion Parsing

        #region RecreateToFile
        public static void PushConvoToFile(ConversationExtended convo)
        {
            convo.Export.WriteProperties(convo.BioConvo);
        }
        public void PushLocalGraphChanges(DiagNode obj)
        {
            IsLocalUpdate = true;
            RecreateNodesToProperties(SelectedConv);

            float newX = obj.X + obj.OffsetX;
            float newY = obj.Y + obj.OffsetY;
            obj.RemoveAllChildren();
            obj.RemoveConnections();
            obj.GetOutputLinks(obj.Node);
            obj.Layout(newX, newY);
            obj.RecreateConnections(CurrentObjects);

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }
        }

        private void SaveSpeakersToProperties(IEnumerable<SpeakerExtended> speakerCollection)
        {
            try
            {
                var m_aSpeakerList = new ArrayProperty<NameProperty>("m_aSpeakerList");
                var m_SpeakerList = new ArrayProperty<StructProperty>("m_SpeakerList");
                var m_aMaleFaceSets = new ArrayProperty<ObjectProperty>("m_aMaleFaceSets");
                var m_aFemaleFaceSets = new ArrayProperty<ObjectProperty>("m_aFemaleFaceSets");

                foreach (SpeakerExtended spkr in speakerCollection)
                {
                    if (spkr.SpeakerID >= 0)
                    {
                        if (Pcc.Game.IsGame3())
                        {
                            m_aSpeakerList.Add(new NameProperty(spkr.SpeakerNameRef, "m_aSpeakerList"));
                        }
                        else
                        {
                            m_SpeakerList.Add(new StructProperty("BioDialogSpeaker", new PropertyCollection
                            {
                                new NameProperty(spkr.SpeakerNameRef, "sSpeakerTag"),
                                new NoneProperty()
                            }));
                        }
                    }

                    m_aMaleFaceSets.Add(new ObjectProperty(spkr.FaceFX_Male));
                    m_aFemaleFaceSets.Add(new ObjectProperty(spkr.FaceFX_Female));
                }

                if (m_aSpeakerList.Count > 0 && Pcc.Game.IsGame3())
                {
                    SelectedConv.BioConvo.AddOrReplaceProp(m_aSpeakerList);
                }
                else if (m_SpeakerList.Count > 0)
                {
                    SelectedConv.BioConvo.AddOrReplaceProp(m_SpeakerList);
                }
                if (m_aMaleFaceSets.Count > 0)
                {
                    SelectedConv.BioConvo.AddOrReplaceProp(m_aMaleFaceSets);
                }
                if (m_aFemaleFaceSets.Count > 0)
                {
                    SelectedConv.BioConvo.AddOrReplaceProp(m_aFemaleFaceSets);
                }
                PushConvoToFile(SelectedConv);
            }
            catch (Exception e)
            {
                MessageBox.Show($"Speaksave FAILED. {e}");
            }
        }
        public void RecreateNodesToProperties(ConversationExtended conv, bool pushtofile = true)
        {
            conv.SerializeNodes(pushtofile);
        }
        private void SaveScriptsToProperties(ConversationExtended conv, bool pushtofile = true)
        {
            if (Pcc.Game.IsGame3())
            {
                var newscriptList = new ArrayProperty<NameProperty>("m_aScriptList");
                foreach (var script in conv.ScriptList)
                {
                    if (script.Name != "None")
                    {
                        newscriptList.Add(new NameProperty(script, "m_aScriptList"));
                    }
                }
                if (newscriptList.Count > 0)
                {
                    conv.BioConvo.AddOrReplaceProp(newscriptList);
                }
                else
                {
                    conv.BioConvo.TryReplaceProp(newscriptList);
                }
            }
            else
            {
                var newscriptList = new ArrayProperty<StructProperty>("m_ScriptList");
                foreach (var script in conv.ScriptList)
                {
                    if (script.Name != "None")
                    {
                        newscriptList.Add(new StructProperty("BioDialogScript", new PropertyCollection
                        {
                            new NameProperty(script, "sScriptTag"),
                            new NoneProperty()
                        }));
                    }
                }
                if (newscriptList.Count > 0)
                {
                    conv.BioConvo.AddOrReplaceProp(newscriptList);
                }
                else
                {
                    conv.BioConvo.TryReplaceProp(newscriptList);
                }
            }
            if (pushtofile)
            {
                PushConvoToFile(conv);
            }
        }
        private static void SaveStageDirectionsToProperties(ConversationExtended conv)
        {
            var aStageDirs = new ArrayProperty<StructProperty>("m_aStageDirections");
            foreach (var stageD in conv.StageDirections)
            {
                var p = new PropertyCollection();
                p.AddOrReplaceProp(new StrProperty(stageD.Direction, "sText"));
                p.AddOrReplaceProp(new StringRefProperty(stageD.StageStrRef, "srStrRef"));
                p.AddOrReplaceProp(new NoneProperty());
                aStageDirs.Add(new StructProperty("BioStageDirection", p));
            }
            conv.BioConvo.AddOrReplaceProp(aStageDirs);
            PushConvoToFile(conv);
        }
        #endregion RecreateToFile

        #region Handling-updates
        public override void HandleUpdate(List<PackageUpdate> updates)
        {
            if (Pcc == null || IsLocalUpdate)
            {
                if (IsLocalUpdate) //If local load just refresh interpreter
                {
                    Properties_InterpreterWPF.LoadExport(SelectedConv.Export);
                    IsLocalUpdate = false;
                }
                return; //nothing is loaded
            }
            List<PackageUpdate> relevantUpdates = updates.Where(x => x.Change.HasFlag(PackageChange.Export)).ToList();

            if (SelectedConv != null && CurrentLoadedExport.ClassName != "BioConversation")
            {
                //loaded convo is no longer a convo
                SelectedConv = null;
                SelectedSpeakerList.ClearEx();
                Conversations_ListBox.SelectedIndex = -1;
                graphEditor.nodeLayer.RemoveAllChildren();
                graphEditor.edgeLayer.RemoveAllChildren();
                Properties_InterpreterWPF.UnloadExport();
                InterpData_InterpreterWPF.UnloadExport();
                SoundpanelWPF_F.UnloadExport();
                SoundpanelWPF_M.UnloadExport();
                FaceFXAnimSetEditorControl_F.UnloadExport();
                FaceFXAnimSetEditorControl_M.UnloadExport();
                ClearInterpDataTree();
                LoadConversations();
                return;
            }

            List<int> updatedConvos = relevantUpdates.Select(x => x.Index).Where(update => Pcc.GetEntry(update)?.ClassName == "BioConversation").ToList();

            if (relevantUpdates.Select(x => x.Index).Any(update => Pcc.GetEntry(update)?.ClassName == "FaceFXAnimSet"))
            {
                FFXAnimsets.Clear(); //REBUILD ANIMSET LIST IF NEW ONES and Rerun parsing of speakers.
                foreach (var exp in Pcc.Exports.Where(exp => exp.ClassName == "FaceFXAnimSet"))
                {
                    FFXAnimsets.Add(exp);
                }

                if (SelectedConv != null) updatedConvos.Add(SelectedConv.Export.UIndex);
            }

            if (SelectedDialogueNode != null) //Update any changes to live dialogue node
            {
                if (relevantUpdates.Select(x => x.Index).Any(update => Pcc.GetEntry(update) == SelectedDialogueNode.InterpData))
                {
                    if (SelectedDialogueNode.InterpData.ClassName == "Interpdata") //If changed??
                    {
                        var lengthprop = SelectedDialogueNode.InterpData.GetProperty<FloatProperty>("InterpLength");
                        if (lengthprop != null)
                        {
                            SelectedDialogueNode.InterpLength = lengthprop.Value;
                        }
                    }
                }
            }

            if (updatedConvos.IsEmpty())
                return;

            int cSelectedIdx = Conversations_ListBox.SelectedIndex;
            int sSelectedIdx = Speakers_ListBox.SelectedIndex;
            foreach (var uxp in updatedConvos)
            {
                int index = Conversations.FindIndex(i => i.UIndex == uxp);
                Conversations.RemoveAt(index);
                if (Pcc.GetEntry(uxp) is ExportEntry exp)
                {
                    Conversations.Insert(index, new ConversationExtended(exp));
                }
            }

            FirstParse();
            Conversations_ListBox.SelectedIndex = cSelectedIdx;
            Speakers_ListBox.SelectedIndex = sSelectedIdx;
            if (!NoUIRefresh)
            {
                RefreshView();
                SetUIMode(CurrentUIMode, true);
                DialogueNode_SelectByIndex(-1);
            }
            NoUIRefresh = false;
        }

        public void NodePropertyChanged(object sender, PropertyChangedEventArgs e) //update handler for selecteddiagnode.
        {
            if (sender == null || SelectedConv == null || SelectedDialogueNode == null)
                return;

            var diagnode = (DialogueNodeExtended)sender;  //THIS IS A GATE TO CHECK IF VALUES HAVE CHANGED
            var newvalue = diagnode.GetType().GetProperty(e.PropertyName).GetValue(diagnode, null);
            var oldvalue = MirrorDialogueNode.GetType().GetProperty(e.PropertyName).GetValue(MirrorDialogueNode, null);
            if (oldvalue == null || newvalue == null || newvalue.ToString() == oldvalue.ToString())
            {
                return;
            }
            MirrorDialogueNode.GetType().GetProperty(e.PropertyName).SetValue(MirrorDialogueNode, newvalue);
            //IF PASS THEN RECREATE NODE
            var node = SelectedDialogueNode;
            var prop = node.NodeProp;
            IsLocalUpdate = true;  //Full reparse of changed convo not needed.

            if (e.PropertyName == "LineStrRef" || e.PropertyName == "SpeakerIndex")
            {
                IsLocalUpdate = false;  //StrRef/Speaker change requires full reparse due to FaceFX/Rechart.
            }
            var needsRefresh = false; //Controls if refresh chart (auto happens on full parse)

            switch (e.PropertyName)         // Props in both replies and entries. All Games.
            {
                case "Listener":
                    var nListenerIndex = new IntProperty(node.Listener, "nListenerIndex");
                    prop.Properties.AddOrReplaceProp(nListenerIndex);
                    break;
                case "LineStrRef":
                    var srText = new StringRefProperty(node.LineStrRef, "srText");
                    prop.Properties.AddOrReplaceProp(srText);
                    break;
                case "ConditionalOrBool":
                    var nConditionalFunc = new IntProperty(node.ConditionalOrBool, "nConditionalFunc");
                    prop.Properties.AddOrReplaceProp(nConditionalFunc);
                    needsRefresh = true;
                    break;
                case "ConditionalParam":
                    var nConditionalParam = new IntProperty(node.ConditionalParam, "nConditionalParam");
                    prop.Properties.AddOrReplaceProp(nConditionalParam);
                    break;
                case "Transition":
                    var nStateTransition = new IntProperty(node.Transition, "nStateTransition");
                    prop.Properties.AddOrReplaceProp(nStateTransition);
                    needsRefresh = true;
                    break;
                case "TransitionParam":
                    var nStateTransitionParam = new IntProperty(node.TransitionParam, "nStateTransitionParam");
                    prop.Properties.AddOrReplaceProp(nStateTransitionParam);
                    break;
                case "ExportID":
                    var nExportID = new IntProperty(node.ExportID, "nExportID");
                    prop.Properties.AddOrReplaceProp(nExportID);
                    node.InterpData = SelectedConv.ParseSingleNodeInterpData(node);
                    var lengthprop = node.InterpData?.GetProperty<FloatProperty>("InterpLength");
                    node.InterpLength = lengthprop?.Value ?? -1;
                    needsRefresh = true;
                    break;
                case "InterpLength":
                    if (node.InterpData != null)
                    {
                        node.InterpData.WriteProperty(new FloatProperty(node.InterpLength, "InterpLength"));
                    }
                    break;
                case "CameraIntimacy":
                    var CameraIntimacy = new IntProperty(node.CameraIntimacy, "nCameraIntimacy");
                    prop.Properties.AddOrReplaceProp(CameraIntimacy);
                    break;
                case "FiresConditional":
                    var bFireConditional = new BoolProperty(node.FiresConditional, "bFireConditional");
                    prop.Properties.AddOrReplaceProp(bFireConditional);
                    needsRefresh = true;
                    break;
                case "IsAmbient":
                    var bAmbient = new BoolProperty(node.IsAmbient, "bAmbient");
                    prop.Properties.AddOrReplaceProp(bAmbient);
                    break;
                case "IsNonTextLine":
                    var bNonTextLine = new BoolProperty(node.IsNonTextLine, "bNonTextLine");
                    prop.Properties.AddOrReplaceProp(bNonTextLine);
                    break;
                case "IgnoreBodyGesture":
                    var bIgnoreBodyGestures = new BoolProperty(node.IgnoreBodyGesture, "bIgnoreBodyGestures");
                    prop.Properties.AddOrReplaceProp(bIgnoreBodyGestures);
                    break;
                case "Script":
                    var scriptidx = SelectedConv.ScriptList.FindIndex(s => s == node.Script) - 1;
                    var nScriptIndex = new IntProperty(scriptidx, "nScriptIndex");
                    prop.Properties.AddOrReplaceProp(nScriptIndex);
                    break;
                case "GUIStyle":
                    var EGUIStyles = new EnumProperty(node.GUIStyle.ToString(), "EConvGUIStyles", Pcc.Game, "eGUIStyle");
                    prop.Properties.AddOrReplaceProp(EGUIStyles);
                    break;
                default:
                    break;
            }
            //Skip SText
            if (Pcc.Game.IsGame3() && e.PropertyName == "HideSubtitle")
            {
                var bAlwaysHideSubtitle = new BoolProperty(node.HideSubtitle, "bAlwaysHideSubtitle");
                prop.Properties.AddOrReplaceProp(bAlwaysHideSubtitle);
            }

            if (!SelectedDialogueNode.IsReply)
            {
                //Ignore replylist for now
                //Ignore aSpeakerList  <-- autorecreated
                var nSpeakerIndex = new IntProperty(node.SpeakerIndex, "nSpeakerIndex");
                prop.Properties.AddOrReplaceProp(nSpeakerIndex);
                var bSkippable = new BoolProperty(node.IsSkippable, "bSkippable");
                prop.Properties.AddOrReplaceProp(bSkippable);
            }
            else
            {
                //Ignore Entry List
                var bUnskippable = new BoolProperty(node.IsUnskippable, "bUnskippable");
                prop.Properties.AddOrReplaceProp(bUnskippable);
                if (e.PropertyName == "ReplyType")
                {
                    var ReplyType = new EnumProperty(node.ReplyType.ToString(), "EReplyTypes", Pcc.Game, "ReplyType");
                    prop.Properties.AddOrReplaceProp(ReplyType);
                    needsRefresh = true;
                }

                if (Pcc.Game.IsGame3() && (e.PropertyName == "IsDefaultAction" || e.PropertyName == "IsMajorDecision"))
                {
                    var bIsDefaultAction = new BoolProperty(node.IsDefaultAction, "bIsDefaultAction");
                    prop.Properties.AddOrReplaceProp(bIsDefaultAction);
                    var bIsMajorDecision = new BoolProperty(node.IsMajorDecision, "bIsMajorDecision");
                    prop.Properties.AddOrReplaceProp(bIsMajorDecision);
                }
            }

            RecreateNodesToProperties(SelectedConv);

            if (needsRefresh)
                RefreshView();
        }

        #endregion Handling-updates

        #region CreateGraph

        public void GenerateGraph(bool regenerate = false)
        {
            if (regenerate)
            {
                saveView(false);
            }
            else if (File.Exists(JSONpath) && SaveViewMode != ESaveViewMode.AutoGenerate)
            {
                SavedPositions = JsonConvert.DeserializeObject<List<SaveData>>(File.ReadAllText(JSONpath));
            }
            else
            {
                SavedPositions = [];
            }
            extraSaveData.Clear();

            CurrentObjects.ClearEx();
            graphEditor.nodeLayer.RemoveAllChildren();
            graphEditor.edgeLayer.RemoveAllChildren();
            StartPoDStarts = 0;
            StartPoDiagNodes = 0;
            StartPoDReplyNodes = 20;
            if (SelectedConv == null)
                return;

            LoadDialogueObjects();
            Layout();
            graphEditor.Enabled = true;
            graphEditor.UseWaitCursor = false;
            foreach (DObj o in CurrentObjects)
            {
                o.MouseDown += node_MouseDown;
                o.Click += node_Click;
            }

            graphEditor.Camera.X = 0;
            graphEditor.Camera.Y = 0;
            if (!regenerate && (SavedPositions.IsEmpty() || SaveViewMode == ESaveViewMode.AutoGenerate))
            {
                AutoLayout();
            }
        }
        public bool LoadDialogueObjects()
        {
            float x = 0;
            float y = 0;
            int ecnt = SelectedConv.EntryList.Count;
            int rcnt = SelectedConv.ReplyList.Count;
            int max = Math.Max(ecnt, rcnt);
            var startlist = new Dictionary<int, int>(SelectedConv.StartingList); //Dictionary (Key = position on list, value = outlink)
            for (int n = 0; n < max; n++)
            {
                bool isInList = startlist.Values.IndexOf(n) != -1;
                if (isInList)
                {
                    var startOrder = startlist.FirstOrDefault(k => k.Value == n).Key;
                    var newstart = new DStart(this, startOrder, n, x, y, graphEditor);
                    CurrentObjects.Add(newstart);
                }
                if (n < ecnt)
                {
                    CurrentObjects.Add(new DiagNodeEntry(this, SelectedConv.EntryList[n], x, y, graphEditor));
                }

                if (n < rcnt)
                {
                    CurrentObjects.Add(new DiagNodeReply(this, SelectedConv.ReplyList[n], x, y, graphEditor));
                }
            }

            return true;
        }
        public void Layout()
        {
            if (CurrentObjects != null && CurrentObjects.Any())
            {
                foreach (DObj obj in CurrentObjects)
                {
                    graphEditor.addNode(obj);
                }

                foreach (DObj obj in CurrentObjects)
                {
                    obj.CreateConnections(CurrentObjects);
                }

                foreach (DObj obj in CurrentObjects)
                {
                    //SAVED DATA
                    SaveData savedInfo = new(-1);
                    if (SavedPositions.Any() && SaveViewMode != ESaveViewMode.AutoGenerate)
                    {
                        DObj obj1 = obj;
                        savedInfo = SavedPositions.FirstOrDefault(p => obj1.NodeUID == p.index);
                    }

                    bool hasSavedPosition = savedInfo.index == obj.NodeUID;
                    if (hasSavedPosition)
                    {
                        obj.Layout(savedInfo.X, savedInfo.Y);
                    }
                    else
                    {
                        switch (obj)
                        {
                            case DStart dStart:
                                float ystart = dStart.StartNumber * 127;
                                obj.Layout(0, ystart);
                                //StartPoDStarts += obj.Height + 20;
                                break;
                            case DiagNodeReply _:
                                obj.Layout(500, StartPoDReplyNodes);
                                StartPoDReplyNodes += obj.Height + 25;
                                break;
                            case DiagNode _:
                                obj.Layout(250, StartPoDiagNodes);
                                StartPoDiagNodes += obj.Height + 25;
                                break;

                        }
                    }
                }

                foreach (DiagEdEdge edge in graphEditor.edgeLayer)
                {
                    ConvGraphEditor.UpdateEdge(edge);
                }
            }
        }
        private void AutoLayout()
        {
            switch (LayoutMode)
            {
                case ELayoutMode.Waterfall:
                    AutoLayout_Waterfall();
                    break;
                case ELayoutMode.AdvancedColumn:
                    AutoLayout_AdvancedColumn();
                    break;
                default:
                    AutoLayout_SimpleColumn();
                    break;
            }
        }
        private void AutoLayout_SimpleColumn()
        {
            if (CurrentObjects != null && CurrentObjects.Any())
            {
                foreach (DObj obj in CurrentObjects)
                {
                    obj.SetOffset(0, 0); //remove existing positioning
                }

                const float HORIZONTAL_SPACING = 400;
                float VERTICAL_SPACING = 30;
                if (ShowLinesOnTop_MenuItem.IsChecked)
                    VERTICAL_SPACING = 45;

                var layoutEntries = new Queue<DiagNodeEntry>();
                var layoutReplies = new Queue<DiagNodeReply>();
                var layoutStarts = new ObservableCollectionExtended<DStart>();
                foreach (var obj in CurrentObjects)
                {
                    switch (obj)
                    {
                        case DStart dStart:
                            layoutStarts.Add(dStart);
                            break;
                        case DiagNodeReply diagNodeReply:
                            layoutReplies.Enqueue(diagNodeReply);
                            break;
                        case DiagNodeEntry diagNodeEntry:
                            layoutEntries.Enqueue(diagNodeEntry);
                            break;
                    }
                }

                StartPoDStarts = 0;
                float addheight = 0;
                int currentrow = 0;
                while (layoutEntries.Count > 0 || layoutReplies.Count > 0 || layoutStarts.Count > 0)
                {
                    DStart start = layoutStarts.FirstOrDefault(n => n.StartNumber == currentrow);
                    if (start != null)
                    {
                        start.SetOffset(0, StartPoDStarts);
                        if (start.Height > addheight)
                        {
                            addheight = start.Height;
                        }
                        layoutStarts.Remove(start);
                    }

                    if (layoutEntries.Count > 0)
                    {
                        DiagNodeEntry entry = layoutEntries.Dequeue();
                        entry.SetOffset(HORIZONTAL_SPACING, StartPoDStarts);
                        if (entry.Height > addheight)
                        {
                            addheight = entry.Height;
                        }
                    }

                    if (layoutReplies.Count > 0)
                    {
                        DiagNodeReply reply = layoutReplies.Dequeue();
                        reply.SetOffset(HORIZONTAL_SPACING * 2, StartPoDStarts + 30);
                        if (reply.Height > addheight)
                        {
                            addheight = reply.Height;
                        }
                    }

                    //Adjust height of next start
                    StartPoDStarts += addheight + VERTICAL_SPACING;
                    addheight = 0;
                    currentrow++;
                }

                foreach (DiagEdEdge edge in graphEditor.edgeLayer)
                {
                    ConvGraphEditor.UpdateEdge(edge);
                }
            }
        }
        private void AutoLayout_AdvancedColumn()
        {
            foreach (DObj obj in CurrentObjects)
            {
                obj.SetOffset(0, 0); //remove existing positioning
            }
            int rowAt = 0;
            int maxEntryRow = -1;
            int maxReplyRow = -1;
            int maxStartrow = -1;
            float maxobjHeight = 0;
            float rowShift = 0;
            float COLUMN_SPACING = float.TryParse(ColumnSpace.ToString(), out float clmSp) ? clmSp + 150 : 350;
            float WATERFALL_SPACING = float.TryParse(WaterfallSpace.ToString(), out float wSp) ? wSp : 40;
            float ROW_SPACING = float.TryParse(RowSpace.ToString(), out float rowSp) ? rowSp : 200;
            var visitedNodes = new HashSet<int>();
            List<DStart> startNodes = CurrentObjects.OfType<DStart>().ToList();
            List<DiagNode> allNodes = CurrentObjects.OfType<DiagNode>().OrderBy(n => n.NodeUID).ToList();
            var BranchQueue = new Queue<DiagNode>();

            while (allNodes.Count > 0)
            {
                DStart firstNode = startNodes.FirstOrDefault();
                if (firstNode != null)
                {
                    if (maxEntryRow <= maxReplyRow) // means finished on reply
                    {
                        maxStartrow = maxReplyRow + 1; //start next row.
                    }
                    else
                    {
                        maxStartrow = maxEntryRow + 1;
                    }

                    firstNode.SetOffset(0, maxStartrow * ROW_SPACING + rowShift);
                    startNodes.Remove(firstNode);
                    visitedNodes.Add(firstNode.NodeUID);
                    DiagNode nextNode = allNodes.FirstOrDefault(x => x.NodeUID == firstNode.StartNumber);
                    if (nextNode != null && !visitedNodes.Contains(nextNode.NodeUID))
                    {
                        while (!(nextNode == null && BranchQueue.IsEmpty()))
                        {
                            var thisNode = nextNode;
                            nextNode = null;
                            if (thisNode != null && !visitedNodes.Contains(thisNode.NodeUID))
                            {
                                int r = 0;
                                if (!thisNode.Node.IsReply)
                                {
                                    if (maxobjHeight > ROW_SPACING) //On entry set spacing for this row
                                    {
                                        rowShift += maxobjHeight + 30 - ROW_SPACING;
                                    }

                                    if (maxEntryRow >= rowAt)
                                        rowAt = maxEntryRow + 1;

                                    r = 1000; //Conversion factor from nIndex to NodeUID to link to reply
                                    thisNode.SetOffset(COLUMN_SPACING, rowAt * ROW_SPACING + rowShift);
                                    maxEntryRow = rowAt;
                                    maxobjHeight = thisNode.Height;
                                }
                                else
                                {
                                    if (maxReplyRow >= rowAt)
                                        rowAt = maxReplyRow + 1;

                                    thisNode.SetOffset(2 * COLUMN_SPACING, rowAt * ROW_SPACING + rowShift + WATERFALL_SPACING);
                                    maxReplyRow = rowAt;
                                    rowAt++;  //After reply go to next row.
                                    if (thisNode.Height > maxobjHeight)
                                    {
                                        maxobjHeight = thisNode.Height;
                                    }
                                }
                                visitedNodes.Add(thisNode.NodeUID);
                                allNodes.Remove(thisNode);
                                if (thisNode.Links.Count != 0)
                                {
                                    for (int i = 0; i < thisNode.Links.Count; i++) //DO IN REVERSE SO STACK IS CORRECTLY DONE
                                    {
                                        if (i == 0)
                                        {
                                            nextNode = allNodes.FirstOrDefault(x => x.NodeUID == (thisNode.Links[0].Index + r));
                                        }
                                        else
                                        {
                                            var pushqueue = allNodes.FirstOrDefault(x => x.NodeUID == thisNode.Links[i].Index + r);
                                            if (pushqueue != null) //means link to visited node.  Don't add to Branchstack.
                                            {
                                                BranchQueue.Enqueue(pushqueue);
                                            }
                                        }
                                    }
                                }
                            }
                            else if (!BranchQueue.IsEmpty())//REACHED END OF BRANCH PULL nextNode from STACK
                            {
                                nextNode = BranchQueue.Dequeue();
                                if (visitedNodes.Contains(nextNode.NodeUID)) //if nextnode is already up, make sure stack is pulled again without moving down.
                                {
                                    nextNode = null;
                                }
                            }
                            else
                            {
                                rowAt++;
                            }
                        }
                    }
                }
                else //everything else is orphan.
                {
                    int orphanrowEntry = maxStartrow;
                    int orphanrowReply = maxStartrow;
                    foreach (var obj in allNodes)
                    {
                        if (obj.Node.IsReply)
                        {
                            obj.SetOffset(2 * COLUMN_SPACING, orphanrowReply * ROW_SPACING + WATERFALL_SPACING);
                            orphanrowReply++;
                        }
                        else
                        {
                            obj.SetOffset(1 * COLUMN_SPACING, orphanrowEntry * ROW_SPACING);
                            orphanrowEntry++;
                        }
                    }
                    break;
                }
            }

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }
        }
        private void AutoLayout_Waterfall()
        {
            foreach (DObj obj in CurrentObjects)
            {
                obj.SetOffset(0, 0); //remove existing positioning
            }
            int rowAt = 0;
            int columnAt = 0;
            int maxrow = 0;
            Dictionary<int, int> columnlevels = new Dictionary<int, int>(); // records the max row in each column
            columnlevels.Add(0, -1);
            float COLUMN_SPACING = float.TryParse(ColumnSpace.ToString(), out float clmSp) ? clmSp : 200;
            float WATERFALL_SPACING = float.TryParse(WaterfallSpace.ToString(), out float wSp) ? wSp : 40;
            float ROW_SPACING = float.TryParse(RowSpace.ToString(), out float rowSp) ? rowSp : 200;
            var visitedNodes = new HashSet<int>();
            List<DStart> startNodes = CurrentObjects.OfType<DStart>().ToList();
            List<DiagNode> allNodes = CurrentObjects.OfType<DiagNode>().OrderBy(n => n.NodeUID).ToList();
            var BranchStack = new Stack<(DiagNode node, int column)>();
            var thisBranch = new Dictionary<int, DiagNode>(); //list of items in this branch column, diagnode

            //TAKE First Start - use to get first layer of waterfall.
            //add to first layer until end. any other branches add to branch stack LIFO to create second etc layer.
            // If no branches in stack then go back to new start.

            while (allNodes.Count > 0)
            {
                maxrow = columnlevels.Values.Max(); //Reset rows on new start node
                for (int i = 0; i < columnlevels.Count; i++)
                {
                    columnlevels[i] = maxrow;
                }
                DStart firstNode = startNodes.FirstOrDefault();
                if (firstNode != null)
                {
                    rowAt = maxrow + 1;
                    columnAt = 0;
                    firstNode.SetOffset(columnAt, rowAt * ROW_SPACING);
                    columnlevels[columnAt] = rowAt;
                    startNodes.Remove(firstNode);
                    visitedNodes.Add(firstNode.NodeUID);
                    DiagNode nextNode = allNodes.FirstOrDefault(x => x.NodeUID == firstNode.StartNumber);
                    if (nextNode != null && !visitedNodes.Contains(nextNode.NodeUID))
                    {
                        while (!(nextNode == null && thisBranch.IsEmpty() && BranchStack.IsEmpty()))
                        {
                            var thisNode = nextNode;
                            nextNode = null;
                            if (thisNode != null && !visitedNodes.Contains(thisNode.NodeUID))
                            {
                                columnAt++;
                                visitedNodes.Add(thisNode.NodeUID);
                                allNodes.Remove(thisNode);
                                thisBranch.Add(columnAt, thisNode);
                                if (columnlevels.TryGetValue(columnAt, out int cmax))
                                {
                                    cmax = rowAt;
                                }
                                else
                                {
                                    columnlevels.Add(columnAt, rowAt);
                                }
                                if (thisNode.Links.Count != 0)
                                {
                                    int r = 0;
                                    if (!thisNode.Node.IsReply) //Conversion factor from nIndex to NodeUID
                                    {
                                        r = 1000;
                                    }
                                    for (int i = thisNode.Links.Count - 1; i >= 0; i--) //DO IN REVERSE SO STACK IS CORRECTLY DONE
                                    {
                                        if (i == 0)
                                        {
                                            nextNode = allNodes.FirstOrDefault(x => x.NodeUID == (thisNode.Links[0].Index + r));
                                        }
                                        else
                                        {
                                            var pushstack = allNodes.FirstOrDefault(x => x.NodeUID == thisNode.Links[i].Index + r);
                                            if (pushstack != null) //means link to visited node.  Don't add to Branchstack.
                                            {
                                                BranchStack.Push((pushstack, columnAt));
                                            }
                                        }
                                    }
                                }
                            }
                            else if (!thisBranch.IsEmpty()) //REACHED END OF BRANCH Set the positions of the previous branch
                            {
                                int tgtRowAt = rowAt;
                                if (thisBranch.First().Key != 1) //Test if this is inside branch
                                {
                                    foreach (var dn in thisBranch)
                                    {
                                        var col = dn.Key;
                                        var priormax = columnlevels[col];
                                        if (priormax >= tgtRowAt)
                                        {
                                            tgtRowAt = priormax + 1;
                                        }
                                    }
                                }

                                foreach (var dn in thisBranch)
                                {
                                    dn.Value.SetOffset(dn.Key * COLUMN_SPACING, tgtRowAt * ROW_SPACING + dn.Key * WATERFALL_SPACING);
                                    columnlevels[dn.Key] = tgtRowAt;
                                }

                                thisBranch.Clear();
                            }
                            else if (!BranchStack.IsEmpty())//PRIOR BRANCH IS DONE PULL nextNode from STACK of sub-branches
                            {
                                (nextNode, columnAt) = BranchStack.Pop();

                                if (visitedNodes.Contains(nextNode.NodeUID)) //if nextnode is already up, make sure stack is pulled again without moving down.
                                {
                                    nextNode = null;
                                }
                            }
                        }
                    }
                }
                else //everything else is orphan.
                {
                    int orphanrowEntry = maxrow + 1;
                    int orphanrowReply = maxrow + 1;
                    foreach (var obj in allNodes)
                    {
                        if (obj.Node.IsReply)
                        {
                            obj.SetOffset(2 * COLUMN_SPACING, orphanrowReply * ROW_SPACING + WATERFALL_SPACING);
                            orphanrowReply++;
                        }
                        else
                        {
                            obj.SetOffset(1 * COLUMN_SPACING, orphanrowEntry * ROW_SPACING);
                            orphanrowEntry++;
                        }
                    }
                    break;
                }
            }

            EnsureWaterfallLayoutHasNoOverlaps();

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }
        }

        private void EnsureWaterfallLayoutHasNoOverlaps()
        {
            const float overlapPadding = 8f;
            var positionedNodes = CurrentObjects
                .Where(o => o is DiagNode or DStart)
                .OrderBy(o => o.X)
                .ThenBy(o => o.Y)
                .ToList();

            var settledNodes = new List<DObj>(positionedNodes.Count);
            foreach (var node in positionedNodes)
            {
                bool moved;
                int safetyCounter = 0;
                do
                {
                    moved = false;
                    float shiftDown = 0f;
                    var nodeBounds = node.GlobalFullBounds;

                    foreach (var settledNode in settledNodes)
                    {
                        var settledBounds = settledNode.GlobalFullBounds;
                        bool horizontalOverlap = nodeBounds.Left < settledBounds.Right + overlapPadding
                                                 && nodeBounds.Right + overlapPadding > settledBounds.Left;
                        if (!horizontalOverlap)
                        {
                            continue;
                        }

                        bool verticalOverlap = nodeBounds.Top < settledBounds.Bottom + overlapPadding
                                               && nodeBounds.Bottom + overlapPadding > settledBounds.Top;
                        if (!verticalOverlap)
                        {
                            continue;
                        }

                        float thisShift = settledBounds.Bottom + overlapPadding - nodeBounds.Top;
                        if (thisShift > shiftDown)
                        {
                            shiftDown = thisShift;
                        }
                    }

                    if (shiftDown > 0)
                    {
                        node.OffsetBy(0, shiftDown);
                        moved = true;
                    }
                } while (moved && ++safetyCounter < positionedNodes.Count + 4);

                settledNodes.Add(node);
            }
        }

        public void RefreshView()
        {
            if (SelectedConv != null)
            {
                Properties_InterpreterWPF.LoadExport(CurrentLoadedExport);
                if (SelectedDialogueNode != null)
                {
                    RefreshExportLoaders();
                }

                GenerateGraph();
                if (SaveViewMode != ESaveViewMode.AutoGenerate)
                {
                    saveView(false);
                }
            }
        }

        /// <summary>
        /// Deferred graph refresh safe to call from Piccolo event handlers.
        /// Rebuilds the graph on the next message pump cycle to avoid re-entrancy.
        /// </summary>
        public void ForceRefreshFromGraph()
        {
            graphEditor?.BeginInvoke(RefreshView);
        }

        public void OpenPlotToolFromGraph(DiagNode node, bool isTransition)
        {
            if (node == null)
            {
                return;
            }

            if (DialogueNode_SelectByIndex(node.Node.NodeCount, node.Node.IsReply) != null)
            {
                OpenInAction(isTransition ? "PlotDbTrans" : "PlotDbCnd");
            }
        }

        private void RefreshExportLoaders()
        {
            BuildInterpDataTree();
            LoadInlineLinkEditor(SelectedObjects.FirstOrDefault() as DiagNode);

            if (SelectedDialogueNode.WwiseStream_Female == null)
            {
                SoundpanelFemaleControl.Visibility = Visibility.Hidden;
                SoundpanelWPF_F.UnloadExport();
            }
            else
            {
                SoundpanelFemaleControl.Visibility = Visibility.Visible;
                SoundpanelWPF_F.LoadExport(SelectedDialogueNode.WwiseStream_Female);
            }

            if (SelectedDialogueNode.WwiseStream_Male == null)
            {
                SoundpanelMaleControl.Visibility = Visibility.Hidden;
                SoundpanelWPF_M.UnloadExport();
            }
            else
            {
                SoundpanelMaleControl.Visibility = Visibility.Visible;
                SoundpanelWPF_M.LoadExport(SelectedDialogueNode.WwiseStream_Male);
            }

            if (SelectedDialogueNode.SpeakerTag?.FaceFX_Female is ExportEntry faceFX_f)
            {
                FaceFXAnimSetEditorControl_F.LoadExport(faceFX_f);
                FaceFXAnimSetEditorControl_F.SelectLineByName(SelectedDialogueNode.FaceFX_Female);
            }
            else
            {
                FaceFXAnimSetEditorControl_F.UnloadExport();
            }

            if (SelectedDialogueNode.SpeakerTag?.FaceFX_Male is ExportEntry faceFX_m)
            {
                FaceFXAnimSetEditorControl_M.LoadExport(faceFX_m);
                FaceFXAnimSetEditorControl_M.SelectLineByName(SelectedDialogueNode.FaceFX_Male);
            }
            else
            {
                FaceFXAnimSetEditorControl_M.UnloadExport();
            }
        }

        private void BuildInterpDataTree()
        {
            ClearInterpDataTree();

            if (SelectedDialogueNode?.InterpData is not ExportEntry interpDataExport || Pcc == null)
            {
                InterpData_InterpreterWPF.UnloadExport();
                return;
            }

            var childrenByParent = Pcc.Exports
                .GroupBy(x => x.idxLink)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.UIndex).ToList());

            var root = BuildInterpDataTreeNode(interpDataExport, null, new HashSet<int>(), childrenByParent);
            if (root == null)
            {
                InterpData_InterpreterWPF.UnloadExport();
                return;
            }

            root.IsExpanded = true;
            InterpDataTreeNodes.Add(root);
            root.IsSelected = true;
            InterpData_InterpreterWPF.LoadExport(interpDataExport);
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
        }

        private ExportEntry GetSelectedInterpDataTreeExport()
        {
            return InterpDataTreeView?.SelectedItem is TreeViewEntry { Entry: ExportEntry export } ? export : null;
        }

        private void InterpDataTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewEntry { Entry: ExportEntry export })
            {
                InterpData_InterpreterWPF.LoadExport(export);
            }
            else
            {
                InterpData_InterpreterWPF.UnloadExport();
            }
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

            BuildInterpDataTree();

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

            if (selectedNode != null)
            {
                selectedNode.IsSelected = true;
                selectedNode.ExpandParents();
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
            }
        }

        private void InterpDataTreeContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu)
            {
                return;
            }

            var selectedExport = GetSelectedInterpDataTreeExport();
            bool isInterpData = selectedExport?.ClassName == "InterpData";
            bool isInterpTrackMove = selectedExport?.ClassName == "InterpTrackMove";

            SetContextMenuItemVisibility(menu, "ShiftInterpTrackMovesInInterpData", isInterpData ? Visibility.Visible : Visibility.Collapsed);
            SetContextMenuItemVisibility(menu, "ShiftSelectedInterpTrackMove", isInterpTrackMove ? Visibility.Visible : Visibility.Collapsed);
        }

        private static void SetContextMenuItemVisibility(ItemsControl parent, string tag, Visibility visibility)
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
                        SetContextMenuItemVisibility(menuItem, tag, visibility);
                    }
                }
            }
        }

        private void InterpDataTree_OpenInPackageEditor_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedInterpDataTreeExport() is ExportEntry export)
            {
                OpenPackageEditorForInterpDataExport(export);
            }
        }

        private void InterpDataTree_OpenInSequenceEditor_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedInterpDataTreeExport() is ExportEntry export)
            {
                OpenInToolkit("SequenceEditor", export.UIndex, Path.GetFileName(export.FileRef.FilePath));
            }
        }

        private void InterpDataTree_OpenInInterpEditor_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedInterpDataTreeExport() is ExportEntry export)
            {
                OpenInInterpViewer_Clicked(export);
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

            if (GetSelectedInterpDataTreeExport() is not ExportEntry export)
            {
                return;
            }

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

                    if (ClassPickerDlg.GetClass(this, MatineeHelper.GetInterpTracks(export.Game), "Choose Track to Add", "Add") is ClassInfo info)
                    {
                        ExportEntry trackExport = MatineeHelper.AddNewTrackToGroup(export, info.ClassName);
                        MatineeHelper.AddDefaultPropertiesToTrack(trackExport);
                        RefreshInterpDataTreePreserveState(trackExport.UIndex);
                    }
                    break;
                case "ShiftInterpTrackMovesInInterpData":
                    if (export.ClassName == "InterpData")
                    {
                        var dialog = new ShiftInterpTrackDialog();
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
                        var dialog = new ShiftInterpTrackDialog();
                        if (dialog.ShowDialog() == true)
                        {
                            PackageEditorExperimentsM.ShiftInterpTrackMove(export, dialog.Parameters);
                            RefreshInterpDataTreePreserveState(export.UIndex);
                        }
                    }
                    break;
                case "FindReferences":
                    BusyText = "Finding references...";
                    IsBusy = true;
                    Task.Run(() => export.GetEntriesThatReferenceThisOne()).ContinueWithOnUIThread(prevTask =>
                    {
                        IsBusy = false;
                        var dlg = new ListDialog(
                            prevTask.Result.SelectMany(kvp => kvp.Value.Select(refName =>
                                new EntryStringPair(kvp.Key, $"#{kvp.Key.UIndex} {kvp.Key.ObjectName.Instanced}: {refName}"))).ToList(),
                            $"{prevTask.Result.Count} Objects that reference #{export.UIndex} {export.InstancedFullPath}",
                            "There may be additional references to this object in the unparsed binary of some objects",
                            this);
                        dlg.Show();
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
                    if (d.ShowDialog() == true)
                    {
                        File.WriteAllBytes(d.FileName, binaryOnly ? export.GetBinaryData() : export.Data);
                        MessageBox.Show("Done.");
                    }
                    break;
                }
                case "ImportAllData":
                case "ImportBinaryData":
                {
                    bool binaryOnly = actionName == "ImportBinaryData";
                    var d = new OpenFileDialog { Filter = "*.bin|*.bin", FileName = export.ObjectName.Instanced + ".bin", CustomPlaces = AppDirectories.GameCustomPlaces };
                    if (d.ShowDialog() == true)
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
                case "ExportEmbeddedFile":
                case "ImportEmbeddedFile":
                    MessageBox.Show(this, "Embedded file import/export is not available directly in Dialogue Editor for this context.");
                    break;
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
            if (newEntry.Parent is not ExportEntry parentExport)
            {
                return;
            }

            if (parentExport.IsA("InterpGroup"))
            {
                var props = parentExport.GetProperties();
                var tracksProp = props.GetProp<ArrayProperty<ObjectProperty>>("InterpTracks") ?? new ArrayProperty<ObjectProperty>("InterpTracks");
                tracksProp.Add(new ObjectProperty(newEntry));
                props.AddOrReplaceProp(tracksProp);
                parentExport.WriteProperties(props);
            }
            else if (parentExport.IsA("InterpData"))
            {
                var props = parentExport.GetProperties();
                var groupsProp = props.GetProp<ArrayProperty<ObjectProperty>>("InterpGroups") ?? new ArrayProperty<ObjectProperty>("InterpGroups");
                groupsProp.Add(new ObjectProperty(newEntry));
                props.AddOrReplaceProp(groupsProp);
                parentExport.WriteProperties(props);
            }
        }

        private void OpenPackageEditorForInterpDataExport(ExportEntry export, string packageEditorAction = null)
        {
            var packageEditor = new PackageEditorWindow(false)
            {
                Owner = this
            };

            packageEditor.Show();
            packageEditor.LoadFile(export.FileRef.FilePath, export.UIndex);

            if (string.IsNullOrWhiteSpace(packageEditorAction))
            {
                return;
            }

            packageEditor.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (packageEditor.Pcc?.GetEntry(export.UIndex) is IEntry selectedEntry && packageEditor.NavigateToEntryCommand?.CanExecute(selectedEntry) == true)
                {
                    packageEditor.NavigateToEntryCommand.Execute(selectedEntry);
                }

                ICommand actionCommand = packageEditorAction switch
                {
                    "ViewReferenceGraph" => packageEditor.ViewReferenceGraphCommand,
                    "AddInterpTrack" => packageEditor.AddInterpTrackCommand,
                    "ShiftInterpTrackMovesInInterpData" => packageEditor.ShiftInterpTrackMovesInInterpDataCommand,
                    "FindReferences" => packageEditor.FindReferencesCommand,
                    "Reindex" => packageEditor.ReindexCommand,
                    "ExtractToFile" => packageEditor.ExtractToPackageCommand,
                    "Clone" => packageEditor.CloneCommand,
                    "CloneTree" => packageEditor.CloneTreeCommand,
                    "MultiClone" => packageEditor.MultiCloneCommand,
                    "MultiCloneTree" => packageEditor.MultiCloneTreeCommand,
                    "RestoreExport" => packageEditor.RestoreExportCommand,
                    "Trash" => packageEditor.TrashCommand,
                    "SetIndicesInTreeToZero" => packageEditor.SetIndicesInTreeToZeroCommand,
                    "ExportEmbeddedFile" => packageEditor.ExportEmbeddedFileCommand,
                    "ExportAllData" => packageEditor.ExportAllDataCommand,
                    "ExportBinaryData" => packageEditor.ExportBinaryDataCommand,
                    "ImportEmbeddedFile" => packageEditor.ImportEmbeddedFileCommand,
                    "ImportAllData" => packageEditor.ImportAllDataCommand,
                    "ImportBinaryData" => packageEditor.ImportBinaryDataCommand,
                    "GenerateExportMd5" => packageEditor.CalculateExportMD5Command,
                    _ => null
                };

                if (actionCommand?.CanExecute(null) == true)
                {
                    actionCommand.Execute(null);
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        #endregion CreateGraph  

        #region UIHandling-items
        /// <summary>
        /// Sets UI to 0 = Convo (default), 1=Speakers, 2=Node.
        /// </summary>
        private void SetUIMode(int mode, bool force = false)
        {
            if (mode == CurrentUIMode && !force)
            {
                return;
            }
            CurrentUIMode = mode;

            Node_Panel.Visibility = Visibility.Collapsed;
            switch (CurrentUIMode)
            {
                case 1:
                    BottomViewportTabControl.SelectedItem = BottomViewportTabControl.Items
                        .OfType<TabItem>()
                        .FirstOrDefault(t => t.Header?.ToString() == "Speaker Details");
                    break;
                case 2:
                    Node_Panel.Visibility = Visibility.Visible;
                    break;
                case 3:
                    BottomViewportTabControl.SelectedItem = StartingNodesTab;
                    break;
                default:
                    BottomViewportTabControl.SelectedItem = ConversationDetailsTab;
                    break;

            }

            StageDirections_Tab.Visibility = Pcc?.Game.IsGame3() == true ? Visibility.Visible : Visibility.Collapsed;
        }
        private void ListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox box)
            {
                switch (box.Name)
                {
                    case "Speakers_ListBox":
                        SetUIMode(1, true);
                        break;
                    default:
                        SetUIMode(0, true);
                        break;
                }
            }
        }

        private void ConversationList_SelectedItemChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AutoSaveView_MenuItem.IsChecked)
            {
                saveView();
            }

            if (Conversations_ListBox.SelectedIndex < 0)
            {
                SelectedConv = null;
                SelectedDialogueNode = null;
                SelectedSpeakerList.ClearEx();
                Properties_InterpreterWPF.UnloadExport();
                InterpData_InterpreterWPF.UnloadExport();
                SoundpanelWPF_F.UnloadExport();
                SoundpanelWPF_M.UnloadExport();
                FaceFXAnimSetEditorControl_F.UnloadExport();
                FaceFXAnimSetEditorControl_M.UnloadExport();
                ClearInterpDataTree();
                ClearInlineLinkEditor();
            }
            else
            {
                SelectedDialogueNode = null; //Before convos change make sure no properties fire.
                graphEditor.Enabled = false;
                graphEditor.UseWaitCursor = true;
                var nconv = Conversations[Conversations_ListBox.SelectedIndex];
                SelectedConv = new ConversationExtended(nconv);

                var sourceOwner = nconv.Speakers.FirstOrDefault(s => s.SpeakerID == -1);
                var selectedOwner = SelectedConv.Speakers.FirstOrDefault(s => s.SpeakerID == -1);
                if (sourceOwner != null && selectedOwner != null)
                {
                    if (!NeedsOwnerFriendlyName(sourceOwner))
                    {
                        selectedOwner.FriendlyName = sourceOwner.FriendlyName;
                    }
                    else
                    {
                        QueueOwnerFriendlyNameResolution(nconv);
                    }
                }

                CurrentLoadedExport = SelectedConv.Export;
                SetupConvJSON(CurrentLoadedExport);
                if (Pcc.Game == MEGame.ME1)
                {
                    LevelHeader.Text = "Audio/Matinee File:";
                    LevelHeader.ToolTip = "File that contains the audio and cutscene data for the conversation";
                    Level_Textbox.ToolTip = "File that contains the audio and cutscene data for the conversation";
                    OpenLevelPackEd_Button.ToolTip = "Open Audio/Matinee File in Package Editor";
                    OpenLevelSeqEd_Button.ToolTip = "Open Audio/Matinee File in Sequence Editor";
                }
                else
                {
                    LevelHeader.Text = "Level:";
                    LevelHeader.ToolTip = "File with the level and sequence that uses the conversation.";
                    Level_Textbox.ToolTip = "File with the level and sequence that uses the conversation.";
                    OpenLevelPackEd_Button.ToolTip = "Open level in Package Editor";
                    OpenLevelSeqEd_Button.ToolTip = "Open level in Sequence Editor";
                }

                GenerateSpeakerList();
                RefreshView();
                Start_ListBoxUpdate();

                ListenersList.ClearEx();
                ListenersList.Add(new SpeakerExtended(-3, "None"));
                foreach (var spkr in SelectedSpeakerList)
                {
                    ListenersList.Add(spkr);
                }
            }
            graphEditor_PanTo();
        }
        private void Convo_NSFFX_DropDownClosed(object sender, EventArgs e)
        {
            if (FFXAnimsets.Count < 1 || Conversations_ListBox.SelectedIndex is -1 || Conversations[Conversations_ListBox.SelectedIndex].NonSpkrFFX == null)
                return;

            if (Conversations[Conversations_ListBox.SelectedIndex].NonSpkrFFX.UIndex != FFXAnimsets[ComboBox_Conv_NSFFX.SelectedIndex].UIndex)
            {
                SelectedConv.BioConvo.AddOrReplaceProp(new ObjectProperty(SelectedConv.NonSpkrFFX, "m_pNonSpeakerFaceFXSet"));
                PushConvoToFile(SelectedConv);
            }
        }
        private void SetupConvJSON(ExportEntry export)
        {
            string objectName = Regex.Replace(export.ObjectName.Name, @"[<>:""/\\|?*]", "");
            string viewsPath = ME3ViewsPath;
            switch (Pcc.Game)
            {
                case MEGame.ME2:
                    viewsPath = ME2ViewsPath;
                    break;
                case MEGame.ME1:
                    viewsPath = ME1ViewsPath;
                    break;
                case MEGame.LE2:
                    viewsPath = ME2ViewsPath;
                    break;
                case MEGame.LE1:
                    viewsPath = ME1ViewsPath;
                    break;
            }

            JSONpath = Path.Combine(viewsPath, $"{CurrentFile}.#{export.UIndex - 1}{objectName}.JSON");
        }

        private void Speakers_ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!SelectedSpeakerList.IsEmpty())
            {
                if (Speakers_ListBox.SelectedIndex >= 0)
                {
                    if (SelectedSpeaker.SpeakerID == -1 && (string.IsNullOrEmpty(SelectedSpeaker.FriendlyName) || SelectedSpeaker.FriendlyName == "No data"))
                    {
                        QueueOwnerFriendlyNameResolution(SelectedConv);
                    }

                    if (SelectedSpeaker.StrRefID <= 0)
                    {
                        SelectedSpeaker.StrRefID = LookupTagRef(SelectedSpeaker.SpeakerName);
                        // Don't overwrite FriendlyName for owner if it was already resolved from the sequence
                        if (SelectedSpeaker.SpeakerID != -1 || string.IsNullOrEmpty(SelectedSpeaker.FriendlyName) || SelectedSpeaker.FriendlyName == "No data")
                        {
                            SelectedSpeaker.FriendlyName = GlobalFindStrRefbyID(SelectedSpeaker.StrRefID, Pcc);
                        }
                    }

                    TextBox_Speaker_Name.IsEnabled = SelectedSpeaker.SpeakerID >= 0;
                }
                else
                {
                    SelectedSpeaker = SelectedSpeakerList[0];
                }
            }
        }
        private void SpeakerMoveAction(object obj)
        {
            SpkrUpButton.IsEnabled = false;
            SpkrDownButton.IsEnabled = false;
            string direction = obj as string;
            int n = 1; //Movement default is down the list (higher n)
            if (direction == "Up")
            {
                n = -1;
            }

            int selectedIndex = Speakers_ListBox.SelectedIndex;
            Speakers_ListBox.SelectedIndex = -1;

            var OldSpkrList = new ObservableCollectionExtended<SpeakerExtended>(SelectedSpeakerList);
            SelectedSpeakerList.ClearEx();

            var itemToMove = OldSpkrList[selectedIndex];
            itemToMove.SpeakerID = selectedIndex + n - 2;
            OldSpkrList[selectedIndex + n].SpeakerID = selectedIndex - 2;
            OldSpkrList.RemoveAt(selectedIndex);
            OldSpkrList.Insert(selectedIndex + n, itemToMove);

            foreach (var spkr in OldSpkrList)
            {
                SelectedSpeakerList.Add(spkr);
            }
            SelectedConv.Speakers = new ObservableCollectionExtended<SpeakerExtended>(SelectedSpeakerList);
            Speakers_ListBox.SelectedIndex = selectedIndex + n;
            SpkrUpButton.IsEnabled = true;
            SpkrDownButton.IsEnabled = true;
            SaveSpeakersToProperties(SelectedSpeakerList);
        }
        private void ComboBox_Speaker_FFX_DropDownClosed(object sender, EventArgs e)
        {
            var ffxMaleNew = SelectedSpeaker.FaceFX_Male;
            var ffxMaleOld = SelectedConv.GetFaceFX(SelectedSpeaker.SpeakerID, true);
            var ffxFemaleNew = SelectedSpeaker.FaceFX_Female;
            var ffxFemaleOld = SelectedConv.GetFaceFX(SelectedSpeaker.SpeakerID, false);
            if (ffxMaleNew == ffxMaleOld && ffxFemaleNew == ffxFemaleOld)
                return;

            System.Media.SystemSounds.Question.Play();
            var dlg = MessageBox.Show("Are you sure you want to change this speaker's facial animation set? (Not recommended)", "WARNING", MessageBoxButton.OKCancel);
            if (dlg == MessageBoxResult.Cancel)
            {
                SelectedSpeaker.FaceFX_Male = ffxMaleOld;
                SelectedSpeaker.FaceFX_Female = ffxFemaleOld;
                return;
            }

            SelectedSpeakerList[SelectedSpeaker.SpeakerID + 2].FaceFX_Male = ffxMaleNew;
            SelectedSpeakerList[SelectedSpeaker.SpeakerID + 2].FaceFX_Female = ffxFemaleNew;

            SaveSpeakersToProperties(SelectedSpeakerList);
        }
        private void EnterName_Speaker_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var dlg = MessageBox.Show("Do you want to change this actor's tag?", "Confirm", MessageBoxButton.YesNo);
                if (dlg != MessageBoxResult.No)
                {
                    Keyboard.ClearFocus();
                    SelectedSpeakerList[Speakers_ListBox.SelectedIndex].SpeakerNameRef = SelectedSpeaker.SpeakerNameRef;
                    SelectedSpeaker.StrRefID = LookupTagRef(SelectedSpeaker.SpeakerName);
                    SelectedSpeaker.FriendlyName = GlobalFindStrRefbyID(SelectedSpeakerList[Speakers_ListBox.SelectedIndex].StrRefID, Pcc);

                    SaveSpeakersToProperties(SelectedSpeakerList);
                }
            }
        }
        private void SpeakerAdd()
        {
            int maxID = SelectedSpeakerList.Max(x => x.SpeakerID);
            var ndlg = new PromptDialog("Enter the new actors tag", "Add a speaker", "Actor_Tag");
            ndlg.ShowDialog();
            if (ndlg.ResponseText is null or "Actor_Tag")
                return;
            var speakerName = NameReference.FromInstancedString(ndlg.ResponseText);
            Pcc.FindNameOrAdd(speakerName.Name);
            SelectedSpeakerList.Add(new SpeakerExtended(maxID + 1, speakerName, null, null, 0, "No Data"));
            SaveSpeakersToProperties(SelectedSpeakerList);
        }
        private void SpeakerDelete()
        {
            var deleteTarget = Speakers_ListBox.SelectedIndex;
            if (deleteTarget < 2)
            {
                MessageBox.Show("Owner and Player speakers cannot be deleted.", "Dialogue Editor");
                return;
            }

            string delName = SelectedSpeakerList[deleteTarget].SpeakerName;
            int delID = SelectedSpeakerList[deleteTarget].SpeakerID;
            var dlg = MessageBox.Show($"Are you sure you want to delete {delID} : {delName}? ", "Warning: Speaker Deletion", MessageBoxButton.OKCancel);

            if (dlg == MessageBoxResult.Cancel)
                return;

            foreach (var node in SelectedConv.EntryList)
            {
                if (node.SpeakerIndex == delID)
                {
                    MessageBox.Show("Deletion Aborted.\r\nSpeakers with active dialogue nodes cannot be deleted.", "Dialogue Editor", MessageBoxButton.OK);
                    return;
                }
            }

            SelectedConv.Speakers.RemoveAt(deleteTarget);
            SelectedSpeakerList.RemoveAt(deleteTarget);
            SaveSpeakersToProperties(SelectedSpeakerList);
        }
        private void SpeakerGoToName()
        {
            TextBox_Speaker_Name.Focus();
            TextBox_Speaker_Name.CaretIndex = TextBox_Speaker_Name.Text.Length;
        }
        private static int LookupTagRef(string actortag)
        {
            if (!TagDBLoaded)
            {
                if (File.Exists(ActorDatabasePath))
                {
                    ActorStrRefs = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(ActorDatabasePath));
                    TagDBLoaded = true;
                }
            }
            var strref = ActorStrRefs.FirstOrDefault(a => string.Equals(a.Key, actortag, StringComparison.CurrentCultureIgnoreCase));
            if (strref.Key != null)
            {
                return strref.Value;
            }

            return 0;
        }

        private void EditBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var editbox = (TextBox)sender;
            editbox.BorderThickness = new Thickness(2, 2, 2, 2);
            editbox.SetResourceReference(TextBox.BackgroundProperty, System.Windows.SystemColors.HighlightBrushKey);
            editbox.SetResourceReference(TextBox.ForegroundProperty, System.Windows.SystemColors.HighlightTextBrushKey);
        }
        private void EditBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var editbox = (TextBox)sender;
            editbox.BorderThickness = new Thickness(0, 0, 0, 0);
            editbox.SetResourceReference(TextBox.BackgroundProperty, System.Windows.SystemColors.ControlBrushKey);
            editbox.SetResourceReference(TextBox.ForegroundProperty, System.Windows.SystemColors.ControlTextBrushKey);
        }
        private void EditBox_Node_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                var tbox = (TextBox)sender;
                Keyboard.ClearFocus();
                var be = tbox.GetBindingExpression(TextBox.TextProperty);
                switch (e.Key)
                {
                    case Key.Enter:
                        be?.UpdateSource();
                        break;
                    case Key.Escape:
                        be?.UpdateTarget();
                        break;
                }
            }
        }
        private void NumberValidationEditBox(object sender, TextCompositionEventArgs e)
        {
            var regex = new Regex("[^-]+[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void Start_ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var start = CurrentObjects.OfType<DStart>().FirstOrDefault(s => s.Order == Start_ListBox.SelectedIndex);
            if (start == null)
                return;

            foreach (var oldselection in SelectedObjects)
            {
                oldselection.IsSelected = false;
            }
            SelectedObjects.ClearEx();
            start.IsSelected = true;
            SelectedObjects.Add(start);
            panToSelection = false;
        }
        private void StartAddEdit(object param)
        {
            var p = param as string;
            int newKey = SelectedConv.StartingList.Count;
            int f = 0;
            if (p == "Edit")
            {
                newKey = Start_ListBox.SelectedIndex;
                f = SelectedConv.StartingList[newKey];
            }

            var links = new List<string>();
            foreach (var entry in SelectedConv.EntryList)
            {
                links.Add($"{entry.NodeCount}: {entry.LineStrRef} {entry.Line}");
            }
            var sdlg = InputComboBoxDialog.GetValue(this, "Pick an entry node to link to", "Entry selector", links, links[f], false);

            if (sdlg == "")
                return;

            var newVal = links.FindIndex(sdlg.Equals);

            if (p == "Edit")
            {
                SelectedConv.StartingList[newKey] = newVal;
            }
            else
            {
                SelectedConv.StartingList.Add(newKey, newVal);
            }

            RecreateNodesToProperties(SelectedConv);
            forcedSelectStart = newKey;
        }
        private void StartDelete()
        {
            SelectedConv.StartingList.Remove(Start_ListBox.SelectedIndex);
            RecreateNodesToProperties(SelectedConv);
        }
        private void StartMoveAction(object obj)
        {
            StartUpButton.IsEnabled = false;
            StartDownButton.IsEnabled = false;
            string direction = obj as string;
            int n = 1; //Movement default is down the list (higher n)
            if (direction == "Up")
            {
                n = -1;
            }

            int selectedIndex = Start_ListBox.SelectedIndex;
            Start_ListBox.SelectedIndex = -1;

            var selectedval = SelectedConv.StartingList[selectedIndex];
            var shiftval = SelectedConv.StartingList[selectedIndex + n];

            SelectedConv.StartingList.Remove(selectedIndex);
            SelectedConv.StartingList.Remove(selectedIndex + n);
            SelectedConv.StartingList.Add(selectedIndex + n, selectedval);
            SelectedConv.StartingList.Add(selectedIndex, shiftval);

            RecreateNodesToProperties(SelectedConv);
            forcedSelectStart = selectedIndex + n;
            StartUpButton.IsEnabled = true;
            StartDownButton.IsEnabled = true;
        }
        private void Start_ListBoxUpdate()
        {
            var i = Start_ListBox.SelectedIndex;
            Start_ListBox.SelectedIndex = -1;
            Start_ListBox.ItemsSource = null;
            SelectedStarts.Clear();
            foreach (var s in SelectedConv.StartingList)
            {
                SelectedStarts.Add(AddOrdinal(s.Key + 1), s.Value);
            }
            Start_ListBox.ItemsSource = SelectedStarts;
            if (forcedSelectStart > -1)
            {
                Start_ListBox.SelectedIndex = forcedSelectStart;
                forcedSelectStart = -1;
                Start_ListBox.Focus();
            }
            else
            {
                Start_ListBox.SelectedIndex = i;
            }
            panToSelection = false;
        }

        private void Script_Add()
        {
            if (SelectOrAddNamePromptDialog.Prompt(this, "Enter the new script name", "Add a script", Pcc, out NameReference result))
            {
                SelectedConv.ScriptList.Add(result);
                SaveScriptsToProperties(SelectedConv);
            }
        }
        private void Script_Delete()
        {
            var cdlg = MessageBox.Show("Are you sure you want to delete this script reference?", "Confirm", MessageBoxButton.OKCancel);
            if (cdlg == MessageBoxResult.Cancel)
                return;
            var script2remove = (NameReference)Script_ListBox.SelectedItem;
            //CHECK IF ANY LINES REFERENCE THIS SCRIPT.
            bool hasreferences = SelectedConv.EntryList.Any(e => e.Script == script2remove);
            if (!hasreferences)
            {
                hasreferences = SelectedConv.ReplyList.Any(r => r.Script == script2remove);
            }

            if (hasreferences)
            {
                MessageBox.Show("There are lines that reference this script.\r\nPlease remove all references before deleting", "Warning", MessageBoxButton.OK);
                return;
            }

            SelectedConv.ScriptList.Remove(script2remove);

            SaveScriptsToProperties(SelectedConv, false);
            RecreateNodesToProperties(SelectedConv);
        }

        private DiagNode DialogueNode_SelectByIndex(int index, bool isreply = false)
        {
            if (SelectedObjects.Count > 0 && index == -1) //In this case pull up first selected object on list.
            {
                if (SelectedObjects[0] is DiagNode d)
                {
                    //Get redrawn node to keep in focus
                    var dnode = CurrentObjects.OfType<DiagNode>().FirstOrDefault(o => o.Node.NodeCount == d.Node.NodeCount && o.Node.IsReply == d.Node.IsReply);

                    if (dnode != null)
                    {
                        DialogueNode_Selected(dnode);
                        return dnode;
                    }
                }
            }
            else if (index >= 0)
            {
                var dnode = CurrentObjects.OfType<DiagNode>().FirstOrDefault(o => o.Node.NodeCount == index && o.Node.IsReply == isreply);

                if (dnode != null)
                {
                    DialogueNode_Selected(dnode);
                    return dnode;
                }
            }

            return null;
        }
        private void DialogueNode_Selected(DiagNode obj)
        {
            SetUIMode(2);
            foreach (var oldselection in SelectedObjects)
            {
                oldselection.IsSelected = false;
            }
            SelectedObjects.ClearEx();
            obj.IsSelected = true;
            SelectedObjects.Add(obj);

            ParseNodeData(obj.Node);
            SelectedDialogueNode = obj.Node;
            SelectedDialogueNode.PropertyChanged += NodePropertyChanged;
            MirrorDialogueNode = new DialogueNodeExtended(SelectedDialogueNode);  //Setup gate

            Node_Combo_Spkr.SelectedIndex = SelectedDialogueNode.SpeakerIndex + 2;
            Node_Combo_Lstnr.SelectedIndex = SelectedDialogueNode.Listener + 3;

            Node_Combo_Spkr.IsEnabled = true; //Enable/disable boxes

            Node_CB_HideSubs.IsEnabled = false;
            Node_CB_ESkippable.IsEnabled = false;
            Node_CB_RMajor.IsEnabled = false;
            Node_CB_RDefault.IsEnabled = false;
            Node_CB_RUnskippable.IsEnabled = false;
            Node_Combo_ReplyType.IsEnabled = false;

            if (Pcc.Game.IsGame3())
            {
                Node_CB_HideSubs.IsEnabled = true;
            }

            if (SelectedDialogueNode.IsReply)
            {
                Node_Text_Type.Text = "Reply Node";
                Node_Combo_Spkr.IsEnabled = false;
                Node_CB_RUnskippable.IsEnabled = true;
                Node_Combo_ReplyType.IsEnabled = true;
                if (Pcc.Game.IsGame3())
                {
                    Node_CB_RMajor.IsEnabled = true;
                    Node_CB_RDefault.IsEnabled = true;
                }
            }
            else
            {
                Node_Text_Type.Text = "Entry Node";
                Node_CB_ESkippable.IsEnabled = true;
            }

            RefreshExportLoaders();

            if (SelectedDialogueNode.FiresConditional)
                Node_Text_Cnd.Text = "Conditional: ";
            else
                Node_Text_Cnd.Text = "Bool: ";

        }

        public void UpdateNodeSpeakerFromGraph(DialogueNodeExtended node, int speakerId)
        {
            if (node == null || SelectedConv == null || node.SpeakerIndex == speakerId)
            {
                return;
            }

            node.SpeakerIndex = speakerId;
            node.SpeakerTag = SelectedSpeakerList.FirstOrDefault(s => s.SpeakerID == speakerId);
            node.NodeProp.Properties.AddOrReplaceProp(new IntProperty(speakerId, "nSpeakerIndex"));

            RecreateNodesToProperties(SelectedConv);
            RefreshView();
            DialogueNode_SelectByIndex(node.NodeCount, node.IsReply);
        }

        public void UpdateNodeListenerFromGraph(DialogueNodeExtended node, int listenerId)
        {
            if (node == null || SelectedConv == null || node.Listener == listenerId)
            {
                return;
            }

            node.Listener = listenerId;
            node.NodeProp.Properties.AddOrReplaceProp(new IntProperty(listenerId, "nListenerIndex"));

            RecreateNodesToProperties(SelectedConv);
            RefreshView();
            DialogueNode_SelectByIndex(node.NodeCount, node.IsReply);
        }

        public void UpdateNodeLineStrRefFromGraph(DialogueNodeExtended node, int lineStrRef)
        {
            if (node == null || SelectedConv == null || node.LineStrRef == lineStrRef)
            {
                return;
            }

            node.LineStrRef = lineStrRef;
            node.NodeProp.Properties.AddOrReplaceProp(new StringRefProperty(lineStrRef, "srText"));

            RecreateNodesToProperties(SelectedConv);
            ForceRefresh(null);
            DialogueNode_SelectByIndex(node.NodeCount, node.IsReply);
        }

        public void BeginInlineLineStrRefEdit(DiagNode node, System.Drawing.Point editorScreenPoint, float editorWidth, float editorHeight, PointF clickOffsetInEditor)
        {
            if (node == null || graphEditor == null)
            {
                return;
            }

            EndInlineLineStrRefEdit(false);

            inlineLineStrRefNode = node;
            var editor = new System.Windows.Forms.TextBox
            {
                Text = node.Node.LineStrRef.ToString(),
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                Multiline = false,
                ShortcutsEnabled = false
            };
            inlineLineStrRefEditor = editor;

            if (Settings.Global_DarkMode_Enabled)
            {
                editor.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
                editor.ForeColor = System.Drawing.Color.FromArgb(224, 224, 224);
            }

            editor.KeyDown += (_, e) =>
            {
                if (e.KeyCode == System.Windows.Forms.Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    EndInlineLineStrRefEdit(true);
                }
                else if (e.KeyCode == System.Windows.Forms.Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    EndInlineLineStrRefEdit(false);
                }
            };

            editor.LostFocus += (_, _) => EndInlineLineStrRefEdit(true);

            graphEditor.Controls.Add(editor);
            if (editor.IsDisposed || inlineLineStrRefEditor != editor)
            {
                return;
            }

            UpdateInlineLineStrRefEditorPosition(node);

            editor.BringToFront();
            editor.Focus();
            editor.SelectAll();
        }

        public void UpdateInlineLineStrRefEditorPosition(DiagNode node)
        {
            if (node == null || inlineLineStrRefEditor == null || inlineLineStrRefNode != node)
            {
                return;
            }

            Rectangle bounds = node.GetLineStrRefEditorViewBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                inlineLineStrRefEditor.Visible = false;
                return;
            }

            inlineLineStrRefEditor.Visible = true;
            inlineLineStrRefEditor.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);

            float viewScale = graphEditor.Camera.ViewScale;
            float fontSize = Math.Max(12f * viewScale, 1f);
            if (inlineLineStrRefEditor.Font.SizeInPoints != fontSize)
            {
                inlineLineStrRefEditor.Font = new System.Drawing.Font("Arial", fontSize, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            }
        }

        private void EndInlineLineStrRefEdit(bool commit)
        {
            if (inlineLineStrRefEditClosing)
            {
                return;
            }

            var editor = inlineLineStrRefEditor;
            if (editor == null)
            {
                return;
            }

            inlineLineStrRefEditClosing = true;

            string text = editor.Text;
            var node = inlineLineStrRefNode;

            if (graphEditor?.Controls.Contains(editor) == true)
            {
                graphEditor.Controls.Remove(editor);
            }
            editor.Dispose();
            inlineLineStrRefEditor = null;
            inlineLineStrRefNode = null;
            inlineLineStrRefEditClosing = false;

            if (!commit || node == null)
            {
                if (node != null)
                {
                    RefreshView();
                    DialogueNode_SelectByIndex(node.Node.NodeCount, node.Node.IsReply);
                }
                else
                {
                    graphEditor?.Refresh();
                }
                return;
            }

            if (int.TryParse(text, out int newRef))
            {
                bool valueChanged = node.Node.LineStrRef != newRef;
                UpdateNodeLineStrRefFromGraph(node.Node, newRef);
                if (!valueChanged)
                {
                    RefreshView();
                    DialogueNode_SelectByIndex(node.Node.NodeCount, node.Node.IsReply);
                }
            }
            else
            {
                RefreshView();
                DialogueNode_SelectByIndex(node.Node.NodeCount, node.Node.IsReply);
            }
        }

        /// <summary>
        /// Opens a single inline control over a specific plot field in the graph node,
        /// exactly like the TLK string ref inline editor. For FiresConditional, spawns a ComboBox dropdown.
        /// For all other fields, spawns a TextBox.
        /// </summary>
        public void BeginInlinePlotFieldEdit(DiagNode node, PlotFieldEditorInfo fieldInfo)
        {
            if (node == null || graphEditor == null || fieldInfo == null)
                return;

            EndInlinePlotFieldEdit(false);

            inlinePlotFieldNode = node;
            inlinePlotFieldInfo = fieldInfo;

            System.Windows.Forms.Control editor;

            if (fieldInfo.FieldTag == "FiresConditional")
            {
                var combo = new System.Windows.Forms.ComboBox
                {
                    DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                    FlatStyle = System.Windows.Forms.FlatStyle.Flat
                };
                combo.Items.AddRange(["Bool", "Conditional"]);
                combo.SelectedIndex = node.Node.FiresConditional ? 1 : 0;

                if (Settings.Global_DarkMode_Enabled)
                {
                    combo.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
                    combo.ForeColor = System.Drawing.Color.FromArgb(224, 224, 224);
                }

                // Commit on selection change — deferred to avoid re-entrancy during ComboBox event handling
                combo.SelectedIndexChanged += (_, _) => combo.BeginInvoke(() => EndInlinePlotFieldEdit(true));
                combo.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == System.Windows.Forms.Keys.Escape)
                    {
                        e.SuppressKeyPress = true;
                        EndInlinePlotFieldEdit(false);
                    }
                };
                combo.LostFocus += (_, _) =>
                {
                    if (!combo.IsDisposed)
                        combo.BeginInvoke(() => EndInlinePlotFieldEdit(true));
                };
                editor = combo;
            }
            else
            {
                string currentValue = fieldInfo.FieldTag switch
                {
                    "ConditionalOrBool" => node.Node.ConditionalOrBool.ToString(),
                    "ConditionalParam" => node.Node.ConditionalParam.ToString(),
                    "Transition" => node.Node.Transition.ToString(),
                    "TransitionParam" => node.Node.TransitionParam.ToString(),
                    _ => ""
                };

                var textBox = new System.Windows.Forms.TextBox
                {
                    Text = currentValue,
                    BorderStyle = System.Windows.Forms.BorderStyle.None,
                    Multiline = false,
                    ShortcutsEnabled = false
                };

                if (Settings.Global_DarkMode_Enabled)
                {
                    textBox.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
                    textBox.ForeColor = System.Drawing.Color.FromArgb(224, 224, 224);
                }

                textBox.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == System.Windows.Forms.Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        EndInlinePlotFieldEdit(true);
                    }
                    else if (e.KeyCode == System.Windows.Forms.Keys.Escape)
                    {
                        e.SuppressKeyPress = true;
                        EndInlinePlotFieldEdit(false);
                    }
                };
                textBox.LostFocus += (_, _) => EndInlinePlotFieldEdit(true);
                editor = textBox;
            }

            inlinePlotFieldEditor = editor;

            graphEditor.Controls.Add(editor);
            if (editor.IsDisposed || inlinePlotFieldEditor != editor)
                return;

            UpdateInlinePlotFieldEditorPosition(node);

            editor.BringToFront();
            editor.Focus();
            if (editor is System.Windows.Forms.TextBox tb)
                tb.SelectAll();
            if (editor is System.Windows.Forms.ComboBox cb)
                cb.DroppedDown = true;
        }

        public void UpdateInlinePlotFieldEditorPosition(DiagNode node)
        {
            if (node == null || inlinePlotFieldEditor == null || inlinePlotFieldNode != node || inlinePlotFieldInfo == null)
                return;

            Rectangle bounds = node.GetPlotFieldEditorViewBounds(inlinePlotFieldInfo);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                inlinePlotFieldEditor.Visible = false;
                return;
            }

            inlinePlotFieldEditor.Visible = true;
            inlinePlotFieldEditor.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);

            float viewScale = graphEditor.Camera.ViewScale;
            float fontSize = Math.Max(12f * viewScale, 1f);
            if (inlinePlotFieldEditor.Font.SizeInPoints != fontSize)
            {
                inlinePlotFieldEditor.Font = new System.Drawing.Font("Arial", fontSize, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            }
        }

        private void EndInlinePlotFieldEdit(bool commit)
        {
            if (inlinePlotFieldEditClosing)
                return;

            var editor = inlinePlotFieldEditor;
            if (editor == null)
                return;

            inlinePlotFieldEditClosing = true;

            var node = inlinePlotFieldNode;
            var fieldInfo = inlinePlotFieldInfo;

            // Read value before disposing
            string text = null;
            int comboIndex = -1;
            if (editor is System.Windows.Forms.TextBox tb)
                text = tb.Text;
            else if (editor is System.Windows.Forms.ComboBox cb)
                comboIndex = cb.SelectedIndex;

            if (graphEditor?.Controls.Contains(editor) == true)
                graphEditor.Controls.Remove(editor);
            editor.Dispose();
            inlinePlotFieldEditor = null;
            inlinePlotFieldNode = null;
            inlinePlotFieldInfo = null;

            if (!commit || node == null || fieldInfo == null)
            {
                inlinePlotFieldEditClosing = false;
                if (node != null)
                {
                    RefreshView();
                    DialogueNode_SelectByIndex(node.Node.NodeCount, node.Node.IsReply);
                }
                return;
            }

            bool changed = false;

            if (fieldInfo.FieldTag == "FiresConditional")
            {
                if (comboIndex >= 0)
                {
                    bool newFiresConditional = comboIndex == 1;
                    if (node.Node.FiresConditional != newFiresConditional)
                    {
                        node.Node.FiresConditional = newFiresConditional;
                        node.Node.NodeProp.Properties.AddOrReplaceProp(new BoolProperty(newFiresConditional, "bFireConditional"));
                        // Refresh the plot path with the new type
                        if (newFiresConditional)
                            node.Node.ConditionalPlotPath = PlotDatabases.FindPlotConditionalByID(node.Node.ConditionalOrBool, Pcc.Game)?.Path;
                        else
                            node.Node.ConditionalPlotPath = PlotDatabases.FindPlotBoolByID(node.Node.ConditionalOrBool, Pcc.Game)?.Path;
                        changed = true;
                    }
                }
            }
            else if (int.TryParse(text, out int newValue))
            {
                switch (fieldInfo.FieldTag)
                {
                    case "ConditionalOrBool":
                        if (node.Node.ConditionalOrBool != newValue)
                        {
                            node.Node.ConditionalOrBool = newValue;
                            node.Node.NodeProp.Properties.AddOrReplaceProp(new IntProperty(newValue, "nConditionalFunc"));
                            if (node.Node.FiresConditional)
                                node.Node.ConditionalPlotPath = PlotDatabases.FindPlotConditionalByID(newValue, Pcc.Game)?.Path;
                            else
                                node.Node.ConditionalPlotPath = PlotDatabases.FindPlotBoolByID(newValue, Pcc.Game)?.Path;
                            changed = true;
                        }
                        break;
                    case "ConditionalParam":
                        if (node.Node.ConditionalParam != newValue)
                        {
                            node.Node.ConditionalParam = newValue;
                            node.Node.NodeProp.Properties.AddOrReplaceProp(new IntProperty(newValue, "nConditionalParam"));
                            changed = true;
                        }
                        break;
                    case "Transition":
                        if (node.Node.Transition != newValue)
                        {
                            node.Node.Transition = newValue;
                            node.Node.NodeProp.Properties.AddOrReplaceProp(new IntProperty(newValue, "nStateTransition"));
                            node.Node.TransitionPlotPath = PlotDatabases.FindPlotTransitionByID(newValue, Pcc.Game)?.Path;
                            changed = true;
                        }
                        break;
                    case "TransitionParam":
                        if (node.Node.TransitionParam != newValue)
                        {
                            node.Node.TransitionParam = newValue;
                            node.Node.NodeProp.Properties.AddOrReplaceProp(new IntProperty(newValue, "nStateTransitionParam"));
                            changed = true;
                        }
                        break;
                }
            }

            if (changed)
            {
                RecreateNodesToProperties(SelectedConv);
                ForceRefresh(null);
            }
            else
            {
                RefreshView();
            }
            DialogueNode_SelectByIndex(node.Node.NodeCount, node.Node.IsReply);
            inlinePlotFieldEditClosing = false;
        }

        private void DialogueNode_OpenLinkEditor(object obj)
        {
            if (SelectedObjects.FirstOrDefault() is not DiagNode node)
            {
                return;
            }

            LoadInlineLinkEditor(node);
            BottomViewportTabControl.SelectedItem = LinkEditorTab;
        }

        private void ClearInlineLinkEditor()
        {
            inlineLinkEditorNode = null;
            inlineLinkEditorNeedsSave = false;
            InlineLinkEditorLinks.ClearEx();
            InlineLinkEditor_LineText.Text = string.Empty;
        }

        private void LoadInlineLinkEditor(DiagNode node)
        {
            if (node == null)
            {
                ClearInlineLinkEditor();
                return;
            }

            if (inlineLinkEditorNeedsSave && inlineLinkEditorNode != null && inlineLinkEditorNode != node)
            {
                SaveInlineLinkEditorChanges(false);
            }

            inlineLinkEditorNode = node;
            inlineLinkEditorIsReply = node.Node.IsReply;
            inlineLinkEditorNeedsSave = false;

            InlineLinkEditor_LineText.Text = node.Node.Line;
            BuildInlineLinkEditorColumns();
            InlineLinkEditorLinks.ClearEx();

            int order = 0;
            foreach (var link in node.Links)
            {
                link.Order = order;
                ParseInlineLink(link);
                InlineLinkEditorLinks.Add(link);
                order++;
            }
        }

        private void BuildInlineLinkEditorColumns()
        {
            while (InlineLinkEditor_DataGrid.Columns.Count > 5)
            {
                InlineLinkEditor_DataGrid.Columns.RemoveAt(5);
            }

            var readOnlyBrush = (System.Windows.Media.Brush)FindResource("ReadOnlyColumnTextBrush");

            InlineLinkEditor_DataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "#",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.Ordinal)),
                Width = 30,
                IsReadOnly = true,
                Foreground = readOnlyBrush
            });

            InlineLinkEditor_DataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Link",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.NodeIDLink)),
                IsReadOnly = true,
                Width = 40,
                FontWeight = FontWeights.Heavy
            });

            if (!inlineLinkEditorIsReply)
            {
                InlineLinkEditor_DataGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = "GUI StrRef",
                    Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.ReplyStrRef)),
                    IsReadOnly = false,
                    Width = 70,
                    FontWeight = FontWeights.Bold
                });

                var choiceLineColumn = new DataGridTemplateColumn
                {
                    Header = "GUI Choice Line",
                    Width = 120
                };

                var choiceLineTemplate = new DataTemplate();
                var choiceLineTextBlock = new FrameworkElementFactory(typeof(TextBlock));
                choiceLineTextBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReplyChoiceNode.ReplyLine)));
                choiceLineTextBlock.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(ReplyChoiceNode.RCategory)) { Converter = ReplyCategoryBrushConverter });
                choiceLineTextBlock.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                choiceLineTextBlock.SetValue(TextBlock.MarginProperty, new Thickness(2));
                choiceLineTemplate.VisualTree = choiceLineTextBlock;
                choiceLineColumn.CellTemplate = choiceLineTemplate;
                InlineLinkEditor_DataGrid.Columns.Add(choiceLineColumn);

                var categoryValues = GetInlineReplyCategoryValues();
                var categoryColumn = new DataGridTemplateColumn
                {
                    Header = "GUI Category",
                    Width = 150
                };

                var cellTemplate = new DataTemplate();
                var cellTextBlock = new FrameworkElementFactory(typeof(TextBlock));
                cellTextBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReplyChoiceNode.RCategory)));
                cellTextBlock.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(ReplyChoiceNode.RCategory)) { Converter = ReplyCategoryBrushConverter });
                cellTextBlock.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                cellTextBlock.SetValue(TextBlock.MarginProperty, new Thickness(2));
                cellTemplate.VisualTree = cellTextBlock;
                categoryColumn.CellTemplate = cellTemplate;

                var editTemplate = new DataTemplate();
                var comboBoxFactory = new FrameworkElementFactory(typeof(ComboBox));
                comboBoxFactory.SetValue(ComboBox.ItemsSourceProperty, categoryValues);
                comboBoxFactory.SetBinding(ComboBox.SelectedItemProperty, new System.Windows.Data.Binding(nameof(ReplyChoiceNode.RCategory))
                {
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                });
                comboBoxFactory.SetValue(ComboBox.IsDropDownOpenProperty, true);
                editTemplate.VisualTree = comboBoxFactory;
                categoryColumn.CellEditingTemplate = editTemplate;

                InlineLinkEditor_DataGrid.Columns.Add(categoryColumn);
            }

            InlineLinkEditor_DataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Target Check",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.TgtFireCnd)),
                IsReadOnly = true,
                Width = 80,
                Foreground = readOnlyBrush
            });

            InlineLinkEditor_DataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Plot Check",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.TgtCondition)),
                IsReadOnly = true,
                Width = 65,
                Foreground = readOnlyBrush
            });

            var speakerColumn = new DataGridTextColumn
            {
                Header = "Speaker",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.TgtSpeaker)),
                IsReadOnly = true,
                Width = inlineLinkEditorIsReply ? 100 : 60,
                Foreground = readOnlyBrush
            };
            InlineLinkEditor_DataGrid.Columns.Add(speakerColumn);

            InlineLinkEditor_DataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Target Line",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.TgtLine)),
                IsReadOnly = true,
                Foreground = readOnlyBrush
            });
        }

        private void ParseInlineLink(ReplyChoiceNode link)
        {
            string nodePrefix = inlineLinkEditorIsReply ? "E" : "R";
            int targetUID = inlineLinkEditorIsReply ? link.Index : link.Index + 1000;
            link.NodeIDLink = $"{nodePrefix}{link.Index}";
            link.ReplyLine = GlobalFindStrRefbyID(link.ReplyStrRef, Pcc);

            var targetNode = CurrentObjects.OfType<DiagNode>().FirstOrDefault(t => t.NodeUID == targetUID);
            if (targetNode is null)
            {
                link.TgtFireCnd = "Unknown";
                link.TgtCondition = 0;
                link.TgtLine = "<target node not found>";
                link.TgtSpeaker = "Unknown";
                link.Ordinal = AddOrdinal(link.Order + 1);
                return;
            }

            link.TgtFireCnd = targetNode.Node.FiresConditional ? "Conditional" : "Bool";
            link.TgtCondition = targetNode.Node.ConditionalOrBool;
            link.TgtLine = targetNode.Node.Line;
            link.Ordinal = AddOrdinal(link.Order + 1);
            link.TgtSpeaker = targetNode.Node.SpeakerTag.SpeakerName;
        }

        private void InlineLinkEditor_Apply_Click(object sender, RoutedEventArgs e)
        {
            SaveInlineLinkEditorChanges();
        }

        private void SaveInlineLinkEditorChanges(bool focusEditedNode = true)
        {
            if (!inlineLinkEditorNeedsSave || inlineLinkEditorNode is null)
            {
                return;
            }

            if (inlineLinkEditorIsReply)
            {
                inlineLinkEditorNode.NodeProp.Properties.AddOrReplaceProp(new ArrayProperty<IntProperty>(InlineLinkEditorLinks.Select(link => new IntProperty(link.Index)), "EntryList"));
            }
            else
            {
                inlineLinkEditorNode.NodeProp.Properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(InlineLinkEditorLinks.Select(link =>
                    new StructProperty("BioDialogReplyListDetails", new PropertyCollection
                    {
                        new IntProperty(link.Index, "nIndex"),
                        new StringRefProperty(link.ReplyStrRef, "srParaphrase"),
                        new StrProperty("", "sParaphrase"),
                        new EnumProperty(link.RCategory.ToString(), "EReplyCategory", Pcc.Game, "Category"),
                        new NoneProperty()
                    })
                ), "ReplyListNew"));
            }

            int nodeCount = inlineLinkEditorNode.Node.NodeCount;
            bool isReply = inlineLinkEditorNode.Node.IsReply;
            inlineLinkEditorNeedsSave = false;
            RecreateNodesToProperties(SelectedConv);

            if (focusEditedNode)
            {
                DialogueNode_SelectByIndex(nodeCount, isReply);
                BottomViewportTabControl.SelectedItem = LinkEditorTab;
            }
        }

        private void InlineLinkEditor_Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ReplyChoiceNode link)
            {
                InlineLinkEditor_DataGrid.SelectedItem = link;
                InlineLinkEditorLinks.Remove(link);
                ReOrderInlineLinkEditorLinks();
                inlineLinkEditorNeedsSave = true;
            }
        }

        private void InlineLinkEditor_Clone_Click(object sender, RoutedEventArgs e)
        {
            if (InlineLinkEditor_DataGrid.SelectedItem is not ReplyChoiceNode selectedLink)
            {
                return;
            }

            InlineLinkEditorLinks.Add(new ReplyChoiceNode(selectedLink) { Order = InlineLinkEditorLinks.Count + 1 });
            ReOrderInlineLinkEditorLinks();
            inlineLinkEditorNeedsSave = true;
        }

        private void InlineLinkEditor_Move_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ReplyChoiceNode selectedLink)
            {
                InlineLinkEditor_DataGrid.SelectedItem = selectedLink;
            }

            if (InlineLinkEditor_DataGrid.SelectedIndex < 0 || sender is not Button button || button.CommandParameter is not string direction)
            {
                return;
            }

            int moveLinkID = InlineLinkEditor_DataGrid.SelectedIndex;
            if ((moveLinkID == 0 && direction is "Up" or "Top") || (moveLinkID >= InlineLinkEditorLinks.Count - 1 && direction is "Down" or "Bottom"))
            {
                return;
            }

            int numSwaps = direction switch
            {
                "Top" => moveLinkID,
                "Bottom" => InlineLinkEditorLinks.Count - 1 - moveLinkID,
                _ => 1
            };

            int swapDir = direction is "Up" or "Top" ? -1 : 1;
            for (int i = 0; Math.Abs(i) < numSwaps; i += swapDir)
            {
                ReplyChoiceNode moveNode = InlineLinkEditorLinks[moveLinkID];
                ReplyChoiceNode swapNode = InlineLinkEditorLinks[moveLinkID + i + swapDir];
                (moveNode.Order, swapNode.Order) = (swapNode.Order, moveNode.Order);
            }

            ReOrderInlineLinkEditorLinks();
            inlineLinkEditorNeedsSave = true;
        }

        private void InlineLinkEditor_Edit_Click(object sender, RoutedEventArgs e)
        {
            EditInlineSelectedLink();
        }

        private void InlineLinkEditor_DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            EditInlineSelectedLink();
        }

        private void EditInlineSelectedLink()
        {
            if (InlineLinkEditor_DataGrid.SelectedItem is not ReplyChoiceNode editLink)
            {
                return;
            }

            var links = new List<string>();
            int currentTarget = editLink.Index;
            if (inlineLinkEditorIsReply)
            {
                foreach (var entry in SelectedConv.EntryList)
                {
                    links.Add($"{entry.NodeCount}: {entry.LineStrRef} {entry.Line}");
                }
            }
            else
            {
                foreach (var entry in SelectedConv.ReplyList)
                {
                    links.Add($"{entry.NodeCount}: {entry.LineStrRef} {entry.Line}");
                }
            }

            string currentSelection = currentTarget >= 0 && currentTarget < links.Count ? links[currentTarget] : links.FirstOrDefault();
            if (currentSelection is null)
            {
                return;
            }

            string selectedLink = InputComboBoxDialog.GetValue(this, "Pick the next dialogue node to link to", "Dialogue Editor", links, currentSelection);
            if (string.IsNullOrEmpty(selectedLink))
            {
                return;
            }

            editLink.Index = links.FindIndex(selectedLink.Equals);

            if (!inlineLinkEditorIsReply)
            {
                int strRef;
                while (true)
                {
                    var response = PromptDialog.Prompt(this, "Enter the TLK String Reference for the dialogue wheel:", "Link Editor", editLink.ReplyStrRef.ToString());
                    if (response == null || response == "0")
                    {
                        return;
                    }

                    if (int.TryParse(response, out strRef) && strRef > 0)
                    {
                        break;
                    }

                    var warning = MessageBox.Show("The string reference must be a positive whole number.", "Dialogue Editor", MessageBoxButton.OKCancel);
                    if (warning == MessageBoxResult.Cancel)
                    {
                        return;
                    }
                }

                editLink.ReplyStrRef = strRef;

                string selectedCategory = InputComboBoxDialog.GetValue(this,
                    "Pick the wheel position or interrupt:",
                    "Dialogue Editor",
                    GetInlineReplyCategoryValues().Select(v => v.ToString()),
                    editLink.RCategory.ToString());

                if (string.IsNullOrEmpty(selectedCategory))
                {
                    return;
                }

                editLink.RCategory = Enums.Parse<EReplyCategory>(selectedCategory);
            }

            ParseInlineLink(editLink);
            ReOrderInlineLinkEditorLinks();
            InlineLinkEditor_DataGrid.Items.Refresh();
            inlineLinkEditorNeedsSave = true;
        }

        private void ReOrderInlineLinkEditorLinks()
        {
            InlineLinkEditorLinks.Sort(l => l.Order);
            int order = 0;
            foreach (var link in InlineLinkEditorLinks)
            {
                link.Order = order;
                link.Ordinal = AddOrdinal(link.Order + 1);
                order++;
            }
            InlineLinkEditor_DataGrid.Items.Refresh();
        }

        private void InlineLinkEditor_DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Row.Item is ReplyChoiceNode link)
                {
                    ParseInlineLink(link);
                    inlineLinkEditorNeedsSave = true;
                    InlineLinkEditor_DataGrid.Items.Refresh();
                }
            }), DispatcherPriority.Background);
        }

        private EReplyCategory[] GetInlineReplyCategoryValues()
        {
            if (Pcc.Game.IsGame1())
            {
                return new[]
                {
                    EReplyCategory.REPLY_CATEGORY_DEFAULT,
                    EReplyCategory.REPLY_CATEGORY_AGREE,
                    EReplyCategory.REPLY_CATEGORY_DISAGREE,
                    EReplyCategory.REPLY_CATEGORY_FRIENDLY,
                    EReplyCategory.REPLY_CATEGORY_HOSTILE,
                    EReplyCategory.REPLY_CATEGORY_INVESTIGATE,
                };
            }

            return Enums.GetValues<EReplyCategory>();
        }

        private sealed class ReplyCategoryToBrushConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                var category = value is EReplyCategory eReplyCategory ? eReplyCategory : EReplyCategory.REPLY_CATEGORY_DEFAULT;
                var color = category switch
                {
                    EReplyCategory.REPLY_CATEGORY_PARAGON_INTERRUPT => DObj.paraintColor,
                    EReplyCategory.REPLY_CATEGORY_RENEGADE_INTERRUPT => DObj.renintColor,
                    EReplyCategory.REPLY_CATEGORY_AGREE => DObj.agreeColor,
                    EReplyCategory.REPLY_CATEGORY_DISAGREE => DObj.disagreeColor,
                    EReplyCategory.REPLY_CATEGORY_FRIENDLY => DObj.friendlyColor,
                    EReplyCategory.REPLY_CATEGORY_HOSTILE => DObj.hostileColor,
                    _ => DObj.connectionColor
                };

                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }

        private void DialogueNode_Add(object obj)
        {
            string command = obj as string;

            if (command == "AddReply")
            {
                PropertyCollection newprop = GlobalUnrealObjectInfo.getDefaultStructValue(Pcc.Game, "BioDialogReplyNode", true, Pcc);
                var props = SelectedConv.BioConvo.GetProp<ArrayProperty<StructProperty>>("m_ReplyList") ??
                            new ArrayProperty<StructProperty>("m_ReplyList");
                //Set to needed defaults.
                newprop.AddOrReplaceProp(new EnumProperty("GUI_STYLE_NONE", "EConvGUIStyles", Pcc.Game, "eGUIStyle"));
                newprop.AddOrReplaceProp(new EnumProperty("REPLY_STANDARD", "EReplyTypes", Pcc.Game, "ReplyType"));
                newprop.GetProp<IntProperty>("nScriptIndex").Value = -1;
                newprop.GetProp<BoolProperty>("bFireConditional").Value = true;
                newprop.GetProp<IntProperty>("nConditionalFunc").Value = -1;
                newprop.GetProp<IntProperty>("nConditionalParam").Value = -1;
                newprop.GetProp<IntProperty>("nStateTransition").Value = -1;
                newprop.GetProp<IntProperty>("nStateTransitionParam").Value = -1;
                newprop.GetProp<IntProperty>("nCameraIntimacy").Value = 1;
                props.Add(new StructProperty("BioDialogReplyNode", newprop));
                SelectedConv.BioConvo.AddOrReplaceProp(props);
                PushConvoToFile(SelectedConv);
                return;
            }

            if (command == "AddEntry")
            {
                PropertyCollection newprop = GlobalUnrealObjectInfo.getDefaultStructValue(Pcc.Game, "BioDialogEntryNode", true, Pcc);
                var props = SelectedConv.BioConvo.GetProp<ArrayProperty<StructProperty>>("m_EntryList") ??
                            new ArrayProperty<StructProperty>("m_EntryList");
                var EGUIStyles = new EnumProperty("GUI_STYLE_NONE", "EConvGUIStyles", Pcc.Game, "eGUIStyle");
                newprop.AddOrReplaceProp(EGUIStyles);
                newprop.GetProp<IntProperty>("nSpeakerIndex").Value = -1;
                newprop.GetProp<IntProperty>("nScriptIndex").Value = -1;
                newprop.GetProp<BoolProperty>("bFireConditional").Value = true;
                newprop.GetProp<IntProperty>("nConditionalFunc").Value = -1;
                newprop.GetProp<IntProperty>("nConditionalParam").Value = -1;
                newprop.GetProp<IntProperty>("nStateTransition").Value = -1;
                newprop.GetProp<IntProperty>("nStateTransitionParam").Value = -1;
                newprop.GetProp<IntProperty>("nCameraIntimacy").Value = 1;
                props.Add(new StructProperty("BioDialogEntryNode", newprop));
                SelectedConv.BioConvo.AddOrReplaceProp(props);
                PushConvoToFile(SelectedConv);
                return;
            }

            if (SelectedObjects.Count is 0 || SelectedObjects[0] is not DiagNode diagNode) return;

            float newX = diagNode.OffsetX + 100;
            float newY = diagNode.OffsetY + 150;
            int newIndex = 0;

            DiagNode node = null;
            bool isReply = false;
            IsLocalUpdate = true;
            panToSelection = false;
            graphEditor.Enabled = false;
            graphEditor.UseWaitCursor = true;
            bool cloneLinks = false;
            List<DiagNode> inputNodes = [];
            if (command is "CloneReply" or "CloneEntry")
            {
                if (MessageBox.Show("Clone links as well?", "Dialogue Editor", MessageBoxButton.YesNo) is MessageBoxResult.Yes)
                {
                    cloneLinks = true;
                    foreach (var edge in diagNode.InputEdges)
                    {
                        if (edge.originator is DiagNode inputNode)
                        {
                            inputNodes.Add(inputNode);
                        }
                    }
                }
            }

            if (command == "CloneReply")
            {
                isReply = true;
                newIndex = SelectedConv.ReplyList.Count;
                var replyprop = SelectedConv.BioConvo.GetProp<ArrayProperty<StructProperty>>("m_ReplyList");
                string typeName = "BioDialogReplyNode";
                PropertyCollection props = new();
                foreach (var op in SelectedDialogueNode.NodeProp.Properties)
                {
                    if (!cloneLinks && op.Name.Name == "EntryList")
                    {
                        props.AddOrReplaceProp(new ArrayProperty<IntProperty>(op.Name));
                        continue;
                    }
                    props.AddOrReplaceProp(op);
                }
                props.AddOrReplaceProp(new NoneProperty());
                replyprop.Add(new StructProperty(typeName, props));
                var nodeExtended = SelectedConv.ParseSingleLine(replyprop[newIndex], newIndex, isReply, TLKLookup);
                nodeExtended.InterpData = SelectedDialogueNode.InterpData;
                nodeExtended.InterpLength = SelectedDialogueNode.InterpLength;
                nodeExtended.Line = SelectedDialogueNode.Line;
                nodeExtended.SpeakerTag = SelectedDialogueNode.SpeakerTag;
                nodeExtended.WwiseStream_Female = SelectedDialogueNode.WwiseStream_Female;
                nodeExtended.WwiseStream_Male = SelectedDialogueNode.WwiseStream_Male;
                nodeExtended.FaceFX_Female = SelectedDialogueNode.FaceFX_Female;
                nodeExtended.FaceFX_Male = SelectedDialogueNode.FaceFX_Male;
                SelectedConv.ReplyList.Add(nodeExtended);
                RecreateNodesToProperties(SelectedConv);
                node = new DiagNodeReply(this, nodeExtended, newX, newY, graphEditor);
            }
            else if (command == "CloneEntry")
            {
                newIndex = SelectedConv.EntryList.Count;
                var entryprop = SelectedConv.BioConvo.GetProp<ArrayProperty<StructProperty>>("m_EntryList");
                string typeName = "BioDialogEntryNode";
                PropertyCollection props = new();
                foreach (var op in SelectedDialogueNode.NodeProp.Properties)
                {
                    if (!cloneLinks && op.Name.Name == "ReplyListNew")
                    {
                        props.AddOrReplaceProp(new ArrayProperty<IntProperty>(op.Name));
                        continue;
                    }
                    props.AddOrReplaceProp(op);
                }
                props.AddOrReplaceProp(new NoneProperty());
                entryprop.Add(new StructProperty(typeName, props));
                var nodeExtended = SelectedConv.ParseSingleLine(entryprop[newIndex], newIndex, isReply, TLKLookup);
                nodeExtended.InterpData = SelectedDialogueNode.InterpData;
                nodeExtended.InterpLength = SelectedDialogueNode.InterpLength;
                nodeExtended.Line = SelectedDialogueNode.Line;
                nodeExtended.SpeakerTag = SelectedDialogueNode.SpeakerTag;
                nodeExtended.WwiseStream_Female = SelectedDialogueNode.WwiseStream_Female;
                nodeExtended.WwiseStream_Male = SelectedDialogueNode.WwiseStream_Male;
                nodeExtended.FaceFX_Female = SelectedDialogueNode.FaceFX_Female;
                nodeExtended.FaceFX_Male = SelectedDialogueNode.FaceFX_Male;
                SelectedConv.EntryList.Add(nodeExtended);
                RecreateNodesToProperties(SelectedConv);
                node = new DiagNodeEntry(this, SelectedConv.EntryList[newIndex], newX, newY, graphEditor);
            }
            CurrentObjects.Add(node);
            node.Layout(newX, newY);
            graphEditor.addNode(node);
            node.SetOffset(newX, newY);
            node.MouseDown += node_MouseDown;
            node.Click += node_Click;
            DialogueNode_SelectByIndex(newIndex, isReply);
            graphEditor.Enabled = true;
            graphEditor.UseWaitCursor = false;
            graphEditor.Camera.AnimateViewToCenterBounds(node.GlobalFullBounds, false, 500);
            graphEditor.Refresh();
            PushConvoToFile(SelectedConv);
            if (cloneLinks)
            {
                foreach (var inputNode in inputNodes)
                {
                    inputNode.CreateLink(inputNode, node);
                }
                GenerateGraph(true);
            }
        }
        private void DialogueNode_DeleteLinks(object obj)
        {
            if (SelectedDialogueNode.IsReply)
            {
                var entrylinklist = SelectedDialogueNode.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
                if (entrylinklist != null)
                {
                    entrylinklist.Clear();
                    RecreateNodesToProperties(SelectedConv);
                }
            }
            else
            {
                var replylinklist = SelectedDialogueNode.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");
                if (replylinklist != null)
                {
                    replylinklist.Clear();
                    RecreateNodesToProperties(SelectedConv);
                }
            }
        }
        private void DialogueNode_Delete(object obj)
        {
            //Warn
            var wdlg = MessageBox.Show("Do you want to remove this dialogue node?", "Warning", MessageBoxButton.OKCancel);
            if (wdlg == MessageBoxResult.Cancel)
                return;

            if (SelectedDialogueNode.IsReply == false && SelectedConv.EntryList.Count <= 1)
            {
                MessageBox.Show("Each conversation must have a minimum of one entry node.", "Warning", MessageBoxButton.OK);
                return;
            }

            var deleteNode = SelectedDialogueNode;
            SelectedDialogueNode = null;
            SelectedObjects.ClearEx();
            int deleteID = deleteNode.NodeCount;
            if (deleteNode.IsReply)
            {
                foreach (var entry in SelectedConv.EntryList)
                {
                    var newReplyLinksProp = new ArrayProperty<StructProperty>("ReplyListNew");
                    var oldReplyLinksProp = entry.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");
                    if (oldReplyLinksProp != null)
                    {
                        foreach (var link in oldReplyLinksProp)
                        {
                            var linkval = link.GetProp<IntProperty>("nIndex").Value;
                            if (linkval != deleteID)
                            {
                                if (linkval > deleteID)
                                {
                                    linkval -= 1;
                                }
                                var newip = new IntProperty(linkval, "nIndex");
                                link.Properties.AddOrReplaceProp(newip);
                                newReplyLinksProp.Add(link);
                            }
                        }
                    }

                    entry.NodeProp.Properties.AddOrReplaceProp(newReplyLinksProp);
                }
                SelectedConv.ReplyList.RemoveAt(deleteID);
            }
            else
            {
                foreach (var reply in SelectedConv.ReplyList)
                {
                    var oldEntryLinksProp = reply.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
                    var newEntryLinksProp = new ArrayProperty<IntProperty>("EntryList");
                    if (oldEntryLinksProp != null)
                    {
                        foreach (var r in oldEntryLinksProp)
                        {
                            if (r.Value != deleteID)
                            {
                                if (r.Value > deleteID)
                                {
                                    r.Value -= 1;
                                }
                                newEntryLinksProp.Add(r);
                            }
                        }
                    }
                    reply.NodeProp.Properties.AddOrReplaceProp(newEntryLinksProp);
                }

                var newStartList = new SortedDictionary<int, int>();
                foreach ((int key, int val) in SelectedConv.StartingList)
                {
                    if (val > deleteID)
                    {
                        newStartList.Add(key, val - 1);
                    }
                    else if (val < deleteID)
                    {
                        newStartList.Add(key, val);
                    }
                }
                SelectedConv.StartingList.Clear();
                foreach (var ns in newStartList)
                {
                    SelectedConv.StartingList.Add(ns.Key, ns.Value);
                }

                SelectedConv.EntryList.RemoveAt(deleteID);
            }
            RecreateNodesToProperties(SelectedConv);
        }

        private void StageDirections_TextChanged(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                var tbox = (TextBox)sender;
                Keyboard.ClearFocus();
                var be = tbox.GetBindingExpression(TextBox.TextProperty);
                switch (e.Key)
                {
                    case Key.Enter:
                        be?.UpdateSource();
                        SaveStageDirectionsToProperties(SelectedConv);
                        break;
                    case Key.Escape:
                        be?.UpdateTarget();
                        break;
                }
            }
        }
        private void StageDirections_Modify(object obj)
        {
            string command = obj as string;
            if (command == "Add")
            {
                int strRef = 0;
                bool isNumber = false;
                while (!isNumber)
                {
                    var sdlg = new PromptDialog("Enter the TLK String Reference for the direction:", "Add a Stage Direction", "0");
                    sdlg.ShowDialog();
                    if (sdlg.ResponseText == null || sdlg.ResponseText == "0")
                        return;
                    isNumber = int.TryParse(sdlg.ResponseText, out strRef);
                    if (!isNumber || strRef <= 0)
                    {
                        var wdlg = MessageBox.Show("The string reference must be a positive whole number.", "Dialogue Editor", MessageBoxButton.OKCancel);
                        if (wdlg == MessageBoxResult.Cancel)
                            return;
                    }
                }

                SelectedConv.StageDirections.Add(new StageDirection(strRef, GlobalFindStrRefbyID(strRef, Pcc), "Add direction"));
                SaveStageDirectionsToProperties(SelectedConv);
            }
            else if (command == "Delete" && StageDirs_ListBox.SelectedIndex >= 0)
            {
                SelectedConv.StageDirections.RemoveAt(StageDirs_ListBox.SelectedIndex);
                SaveStageDirectionsToProperties(SelectedConv);
            }
            else if (command == "Goto" && StageDirs_ListBox.SelectedIndex >= 0)
            {
                GoToSelectedStageDirection();
            }
        }

        private void StageDirs_ListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            GoToSelectedStageDirection();
        }

        private void GoToSelectedStageDirection()
        {
            var selectedIndex = StageDirs_ListBox.SelectedIndex;
            if (selectedIndex >= -0)
            {
                var selectedDirection = SelectedConv.StageDirections[StageDirs_ListBox.SelectedIndex];
                TrySelectStrRef(selectedDirection.StageStrRef, suppressErrorMessageBox: true);
            }
        }

        #endregion

        #region UIHandling-graph

        private void node_Click(object sender, PInputEventArgs e)
        {
            if (e.Shift && e.PickedNode is DObj dObj)
            {
                dObj.IsSelected = true;
                SelectedObjects.Add(dObj);
            }
            else if (sender is DiagNode obj)
            {
                SetUIMode(2, false);
                if (e.Button != System.Windows.Forms.MouseButtons.Left && obj.GlobalFullBounds == obj.posAtDragStart)
                {
                    if (!e.Shift && !e.Control)
                    {
                        if (SelectedObjects.Count == 1 && obj.IsSelected) return;
                        panToSelection = false;
                        if (SelectedObjects.Count > 1)
                        {
                            panToSelection = false;
                        }
                    }
                }
                else
                {
                    DialogueNode_Selected(obj);
                }
            }
            else if (sender is DStart start)
            {
                foreach (var oldselection in SelectedObjects)
                {
                    oldselection.IsSelected = false;
                }
                SelectedObjects.ClearEx();
                start.IsSelected = true;
                SelectedObjects.Add(start);

                Start_ListBox.SelectedIndex = start.Order;
                SetUIMode(3, false);
            }
        }
        private void graphEditor_Click(object sender, EventArgs e)
        {
            graphEditor.Focus();
        }
        private void graphEditor_PanTo()
        {
            var PanObjects = new ObservableCollectionExtended<DObj>();
            PanObjects.AddRange(CurrentObjects.Take(5));

            if (PanObjects.Any())
            {
                if (panToSelection)
                {
                    if (PanObjects.Count == 1)
                    {
                        graphEditor.Camera.AnimateViewToCenterBounds(PanObjects[0].GlobalFullBounds, false, 100);
                    }
                    else
                    {
                        RectangleF boundingBox = PanObjects.Select(obj => obj.GlobalFullBounds).BoundingRect();
                        graphEditor.Camera.AnimateViewToCenterBounds(boundingBox, true, 200);
                    }
                }
            }

            panToSelection = true;
            graphEditor.Refresh();
        }
        private void DialogueEditor_DragEnter(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop))
                e.Effect = System.Windows.Forms.DragDropEffects.All;
            else
                e.Effect = System.Windows.Forms.DragDropEffects.None;
        }
        private void DialogueEditor_DragDrop(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (e.Data.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] DroppedFiles)
            {
                if (DroppedFiles.Any())
                {
                    LoadFile(DroppedFiles[0]);
                }
            }
        }
        private void saveView(bool toFile = true)
        {
            if (CurrentObjects.Count == 0)
                return;
            SavedPositions = new List<SaveData>();
            foreach (DObj obj in CurrentObjects)
            {
                if (obj.Pickable)
                {
                    SavedPositions.Add(new SaveData
                    {
                        index = obj.NodeUID,
                        X = obj.X + obj.Offset.X,
                        Y = obj.Y + obj.Offset.Y
                    });
                }
            }

            SavedPositions.AddRange(extraSaveData);
            extraSaveData.Clear();

            if (toFile)
            {
                string outputFile = JsonConvert.SerializeObject(SavedPositions);
                if (!Directory.Exists(Path.GetDirectoryName(JSONpath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(JSONpath));
                File.WriteAllText(JSONpath, outputFile);
                SavedPositions.Clear();
            }
        }
        protected void node_MouseDown(object sender, PInputEventArgs e)
        {
            if (sender is DObj obj)
            {
                obj.posAtDragStart = obj.GlobalFullBounds;
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                {
                    panToSelection = false;
                    OpenNodeContextMenu(obj);
                }
                else if (e.Shift || e.Control)
                {
                    panToSelection = false;
                }
                else if (!obj.IsSelected)
                {
                    foreach (var oldselection in SelectedObjects)
                    {
                        oldselection.IsSelected = false;
                    }
                    SelectedObjects.ClearEx();
                    panToSelection = false;
                }
            }
        }

        private void backMouseDown_Handler(object sender, PInputEventArgs e)
        {
            if (!(e.PickedNode is PCamera) || SelectedConv == null) return;

            if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
            }
        }
        private void back_MouseUp(object sender, PInputEventArgs e)
        {
            //var nodesToSelect = graphEditor.OfType<DObj>();
            //foreach (DObj DObj in nodesToSelect)
            //{
            //    panToSelection = false;
            //    .SelectedItems.Add(DObj);
            //}
        }

        #endregion UIHandling-graph

        #region UIHandling-menus
        public void OpenNodeContextMenu(DObj obj)
        {
            if (obj is DStart dStart)
            {
                if (FindResource("startnodeContextMenu") is ContextMenu contextMenu)
                {
                    foreach (var oldselection in SelectedObjects)
                    {
                        oldselection.IsSelected = false;
                    }
                    SelectedObjects.ClearEx();
                    dStart.IsSelected = true;
                    SelectedObjects.Add(dStart);

                    Start_ListBox.SelectedIndex = dStart.Order;
                    SetUIMode(3, false);
                    contextMenu.DataContext = this;
                    contextMenu.IsOpen = true;
                    graphEditor.DisableDragging();
                }
            }
            else if (obj is DiagNodeReply dreply)
            {
                if (FindResource("replynodeContextMenu") is ContextMenu contextMenu)
                {
                    if (contextMenu.GetChild("replyLinkEditContextMenu") is MenuItem editHeader)
                    {
                        editHeader.Background = new System.Windows.Media.SolidColorBrush(DObj.replyColor.ToWPFColor());
                    }
                    if (dreply.Outlinks.Any()
                     && contextMenu.GetChild("breakLinksMenuItem") is MenuItem breakLinksMenuItem)
                    {
                        bool hasLinks = false;
                        if (breakLinksMenuItem.GetChild("outputLinksMenuItem") is MenuItem outputLinksMenuItem)
                        {
                            outputLinksMenuItem.Visibility = Visibility.Collapsed;
                            outputLinksMenuItem.Items.Clear();
                            for (int i = 0; i < dreply.Outlinks.Count; i++)
                            {
                                for (int j = 0; j < dreply.Outlinks[i].Links.Count; j++)
                                {
                                    outputLinksMenuItem.Visibility = Visibility.Visible;
                                    hasLinks = true;
                                    var temp = new MenuItem
                                    {
                                        Header = $"Break link from R{dreply.NodeID - 1000} to E{dreply.Outlinks[i].Links[j]}"
                                    };
                                    int linkConnection = i;
                                    int linkIndex = j;
                                    temp.Click += (o, args) => { dreply.RemoveOutlink(linkConnection, linkIndex); };
                                    outputLinksMenuItem.Items.Add(temp);
                                }
                            }
                        }

                        if (breakLinksMenuItem.GetChild("breakAllLinksMenuItem") is MenuItem breakAllLinksMenuItem)
                        {
                            if (hasLinks)
                            {
                                breakLinksMenuItem.Visibility = Visibility.Visible;
                                breakAllLinksMenuItem.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                breakLinksMenuItem.Visibility = Visibility.Collapsed;
                                breakAllLinksMenuItem.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                    DialogueNode_Selected(dreply);
                    contextMenu.DataContext = this;
                    contextMenu.IsOpen = true;
                    graphEditor.DisableDragging();
                }
            }
            else if (obj is DiagNodeEntry dentry)
            {
                if (FindResource("entrynodeContextMenu") is ContextMenu contextMenu)
                {
                    if (contextMenu.GetChild("entryLinkEditContextMenu") is MenuItem editHeader)
                    {
                        editHeader.Background = new System.Windows.Media.SolidColorBrush(DObj.entryColor.ToWPFColor());
                    }

                    if (dentry.Outlinks.Any()
                     && contextMenu.GetChild("ebreakLinksMenuItem") is MenuItem breakLinksMenuItem)
                    {
                        bool hasLinks = false;
                        if (breakLinksMenuItem.GetChild("eoutputLinksMenuItem") is MenuItem outputLinksMenuItem)
                        {
                            outputLinksMenuItem.Visibility = Visibility.Collapsed;
                            outputLinksMenuItem.Items.Clear();
                            for (int i = 0; i < dentry.Outlinks.Count; i++)
                            {
                                for (int j = 0; j < dentry.Outlinks[i].Links.Count; j++)
                                {
                                    outputLinksMenuItem.Visibility = Visibility.Visible;
                                    hasLinks = true;
                                    var temp = new MenuItem
                                    {
                                        Header = $"Break link from E{dentry.NodeID} to R{dentry.Outlinks[i].Links[j] - 1000}"
                                    };
                                    int linkConnection = i;
                                    int linkIndex = j;
                                    temp.Click += (o, args) => { dentry.RemoveOutlink(linkConnection, linkIndex); };
                                    outputLinksMenuItem.Items.Add(temp);
                                }
                            }
                        }

                        if (breakLinksMenuItem.GetChild("ebreakAllLinksMenuItem") is MenuItem breakAllLinksMenuItem)
                        {
                            if (hasLinks)
                            {
                                breakLinksMenuItem.Visibility = Visibility.Visible;
                                breakAllLinksMenuItem.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                breakLinksMenuItem.Visibility = Visibility.Collapsed;
                                breakAllLinksMenuItem.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                    DialogueNode_Selected(dentry);
                    contextMenu.DataContext = this;
                    contextMenu.IsOpen = true;
                    graphEditor.DisableDragging();
                }
            }
        }
        private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            graphEditor.AllowDragging();
            Focus(); //this will make window bindings work, as context menu is not part of the visual tree, and focus will be on there if the user clicked it.
        }
        private void GotoBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems != e.RemovedItems)
            {
                if (GotoBox.SelectedItem is DiagNode dnode)
                {
                    DialogueNode_Selected(dnode);
                }
                if (GotoBox.SelectedItem is DObj o)
                {
                    graphEditor.Camera.AnimateViewToCenterBounds(o.GlobalFullBounds, false, 100);
                    graphEditor.Refresh();
                }
            }
        }
        private void UpdateLayoutDefaults(object obj)
        {
            string command = obj as string;
            bool needsRegen = false;
            bool forceRegen = false;
            switch (command)
            {
                case "Lay_Manual":
                    SaveViewMode = ESaveViewMode.ManualSave;
                    break;
                case "Lay_AutoSave":
                    SaveViewMode = 0;
                    if (CurrentObjects.Any())
                    {
                        SetupConvJSON(SelectedConv.Export);
                    }
                    break;
                case "Lay_AutoGen":
                    SaveViewMode = ESaveViewMode.AutoGenerate;
                    break;
                case "Auto_Column":
                    LayoutMode = 0;
                    needsRegen = true;
                    break;
                case "Auto_Waterfall":
                    LayoutMode = ELayoutMode.Waterfall;
                    needsRegen = true;
                    break;
                case "Auto_AdvColumn":
                    LayoutMode = ELayoutMode.AdvancedColumn;
                    needsRegen = true;
                    break;
                case "Toggle_Output":
                    DObj.OutputNumbers = HideEntryOutput_MenuItem.IsChecked;
                    forceRegen = true;
                    break;
                case "Toggle_LineAtTop":
                    DBox.LinesAtTop = ShowLinesOnTop_MenuItem.IsChecked;
                    forceRegen = true;
                    break;
                default:
                    break;
            }
            ManualSaveView_MenuItem.IsChecked = false;
            AutoGenView_MenuItem.IsChecked = false;
            AutoSaveView_MenuItem.IsChecked = false;
            Waterfall_MenuItem.IsChecked = false;
            Column_MenuItem.IsChecked = false;
            AdvColumn_MenuItem.IsChecked = false;
            switch (SaveViewMode)
            {
                case ESaveViewMode.ManualSave:
                    ManualSaveView_MenuItem.IsChecked = true;
                    break;
                case ESaveViewMode.AutoGenerate:
                    AutoGenView_MenuItem.IsChecked = true;
                    break;
                default: //in case non valid reset
                    AutoSaveView_MenuItem.IsChecked = true;
                    SaveViewMode = 0;
                    break;
            }
            switch (LayoutMode)
            {
                case ELayoutMode.Waterfall:
                    Waterfall_MenuItem.IsChecked = true;
                    break;
                case ELayoutMode.AdvancedColumn:
                    AdvColumn_MenuItem.IsChecked = true;
                    break;
                default: //in case non valid reset
                    Column_MenuItem.IsChecked = true;
                    LayoutMode = 0;
                    break;
            }
            DBox.LinesAtTop = ShowLinesOnTop_MenuItem.IsChecked;
            DObj.OutputNumbers = HideEntryOutput_MenuItem.IsChecked;

            if (CurrentObjects.Any() && ((needsRegen && SaveViewMode == ESaveViewMode.AutoGenerate) || forceRegen))
            {
                RefreshView();
            }
        }
        private void GenderTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl tabctrl)
            {
                faceFXEditorTabControl.SelectedIndex = tabctrl.SelectedIndex;
            }
        }
        private void TestPaths()
        {
            if (SelectedConv.AutoGenerateSpeakerArrays())
            {
                MessageBox.Show("There are possible looping pathways to this conversation.\r\nThis can be a problem unless the player has control of the loop via choices.", "Dialogue Editor");
            }
            else
            {
                MessageBox.Show("No looping paths in the conversation.", "Dialogue Editor");
            }
        }

        //TEMPORARY UNTIL NEW BUILD
        private void OpenInInterpViewer_Clicked(ExportEntry exportEntry)
        {
            var p = new InterpEditorWindow();
            p.Show();
            p.LoadFile(exportEntry.FileRef.FilePath);
            if (exportEntry.ObjectName == "InterpData")
            {
                p.SelectedInterpData = exportEntry;
            }
        }

        private void OpenInAction(object obj)
        {
            string tool = obj as string;
            switch (tool)
            {
                case "PackEdLvl":
                    OpenInToolkit("PackageEditor", 0, Level);
                    break;
                case "PackEdConv":
                    OpenInToolkit("PackageEditor", SelectedConv.UIndex);
                    break;
                case "PackEdLine":
                    OpenInToolkit("PackageEditor", SelectedDialogueNode.InterpData.UIndex, Path.GetFileName(SelectedDialogueNode.InterpData.FileRef.FilePath));
                    break;
                case "PackEd_StreamM":
                    if (SelectedDialogueNode.WwiseStream_Male != null)
                    {
                        OpenInToolkit("PackageEditor", SelectedDialogueNode.WwiseStream_Male.UIndex);
                    }
                    break;
                case "PackEd_StreamF":
                    if (SelectedDialogueNode.WwiseStream_Female != null)
                    {
                        OpenInToolkit("PackageEditor", SelectedDialogueNode.WwiseStream_Female.UIndex);
                    }
                    break;
                case "SeqEdLvl":
                    OpenInToolkit("SequenceEditor", 0, Level);
                    break;
                case "SeqEdNode":
                    if (SelectedConv.Sequence.UIndex < 0)
                    {
                        OpenInToolkit("SequenceEditor", 0, Level);
                    }
                    else
                    {
                        OpenInToolkit("SequenceEditor", SelectedConv.Sequence.UIndex);
                    }
                    break;
                case "SeqEdLine":
                    OpenInToolkit("SequenceEditor", SelectedDialogueNode.InterpData.UIndex, Path.GetFileName(SelectedDialogueNode.InterpData.FileRef.FilePath));
                    break;
                case "FaceFXNS":
                    OpenInToolkit("FaceFXEditor", SelectedConv.NonSpkrFFX.UIndex);
                    break;
                case "FaceFXSpkrM":
                    if (SelectedSpeaker.FaceFX_Male != null)
                    {
                        if (Pcc.IsImport(SelectedSpeaker.FaceFX_Male.UIndex))
                        {
                            OpenInToolkit("FaceFXEditor", 0,
                                Level); //CAN SEND TO THE CORRECT EXPORT IN THE NEW FILE LOAD?
                        }
                        else
                        {
                            OpenInToolkit("FaceFXEditor", SelectedSpeaker.FaceFX_Male.UIndex);
                        }
                    }
                    break;
                case "FaceFXSpkrF":
                    if (SelectedSpeaker.FaceFX_Female != null)
                    {
                        if (Pcc.IsImport(SelectedSpeaker.FaceFX_Female.UIndex))
                        {
                            OpenInToolkit("FaceFXEditor", 0, Level);
                        }
                        else
                        {
                            OpenInToolkit("FaceFXEditor", SelectedSpeaker.FaceFX_Female.UIndex);
                        }
                    }
                    break;
                case "FaceFXLineM":
                    if (SelectedDialogueNode.SpeakerTag.FaceFX_Male != null)
                    {
                        OpenInToolkit("FaceFXEditor", SelectedDialogueNode.SpeakerTag.FaceFX_Male.UIndex, null, SelectedDialogueNode.FaceFX_Male);
                    }
                    break;
                case "FaceFXLineF":
                    if (SelectedDialogueNode.SpeakerTag.FaceFX_Female != null)
                    {
                        OpenInToolkit("FaceFXEditor", SelectedDialogueNode.SpeakerTag.FaceFX_Female.UIndex, null,
                            SelectedDialogueNode.FaceFX_Female);
                    }
                    break;
                case "SoundP_Bank":
                    if (SelectedConv.WwiseBank != null)
                    {
                        OpenInToolkit("SoundplorerWPF", SelectedConv.WwiseBank.UIndex);
                    }
                    break;
                case "SoundP_StreamM":
                    if (SelectedDialogueNode.WwiseStream_Male != null)
                    {
                        OpenInToolkit("SoundplorerWPF", SelectedDialogueNode.WwiseStream_Male.UIndex);
                    }
                    break;
                case "SoundP_StreamF":
                    if (SelectedDialogueNode.WwiseStream_Female != null)
                    {
                        OpenInToolkit("SoundplorerWPF", SelectedDialogueNode.WwiseStream_Female.UIndex);
                    }
                    break;
                case "InterpEdLine":
                    if (SelectedDialogueNode.InterpData != null)
                    {
                        OpenInInterpViewer_Clicked(SelectedDialogueNode.InterpData);
                    }
                    break;
                case "PlotDbCnd":
                    {
                        int cndId = SelectedDialogueNode.ConditionalOrBool;
                        if (cndId != 0)
                        {
                            if (SelectedDialogueNode.FiresConditional && Pcc.Game.IsGame3())
                            {
                                // Search .cnd files from highest-mounted DLC to basegame
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
                                    MessageBox.Show($"Could not find conditional {cndId} in any mounted .cnd file.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }
                            else
                            {
                                // Bool or non-Game3 conditional: open in Plot Database
                                PlotElement element = SelectedDialogueNode.FiresConditional
                                    ? PlotDatabases.FindPlotConditionalByID(cndId, Pcc.Game)
                                    : PlotDatabases.FindPlotBoolByID(cndId, Pcc.Game);
                                if (element != null)
                                {
                                    var plotDb = new Tools.PlotDatabase.PlotManagerWindow(Pcc.Game, element);
                                    plotDb.Show();
                                }
                                else
                                {
                                    MessageBox.Show($"Could not find {(SelectedDialogueNode.FiresConditional ? "conditional" : "bool")} {cndId} in the plot database.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }
                        }
                    }
                    break;
                case "PlotDbTrans":
                    {
                        int transId = SelectedDialogueNode.Transition;
                        if (transId != 0)
                        {
                            IEnumerable<string> plotFiles = Pcc.Game switch
                            {
                                MEGame.ME3 or MEGame.LE3 => MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                                    .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                                    .Select(dir => Path.Combine(dir, Pcc.Game.CookedDirName(), $"Startup_{MELoadedDLC.GetDLCNameFromDir(dir)}_INT.pcc"))
                                    .Append(Path.Combine(MEDirectories.GetCookedPath(Pcc.Game), "SFXGameInfoSP_SF.pcc"))
                                    .Where(File.Exists),
                                MEGame.ME2 or MEGame.LE2 => MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                                    .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                                    .Select(dir => Path.Combine(dir, Pcc.Game.CookedDirName(), $"Startup_{MELoadedDLC.GetDLCNameFromDir(dir)}_INT.pcc"))
                                    .Append(Path.Combine(MEDirectories.GetCookedPath(Pcc.Game), "Startup_INT.pcc"))
                                    .Where(File.Exists),
                                MEGame.LE1 => MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                                    .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                                    .Append(Path.Combine(MEDirectories.GetCookedPath(Pcc.Game), "BIOC_Materials.pcc"))
                                    .Where(File.Exists),
                                MEGame.ME1 => MELoadedDLC.GetEnabledDLCFolders(Pcc.Game)
                                    .OrderByDescending(dir => MELoadedDLC.GetMountPriority(dir, Pcc.Game))
                                    .Select(dir => Path.Combine(dir, Pcc.Game.CookedDirName(), $@"Packages\PlotManagerAuto{MELoadedDLC.GetDLCNameFromDir(dir)}.upk"))
                                    .Append(Path.Combine(MEDirectories.GetCookedPath(Pcc.Game), @"Packages\PlotManagerAuto.upk"))
                                    .Where(File.Exists),
                                _ => Enumerable.Empty<string>()
                            };

                            string matchedFile = null;
                            foreach (var plotFile in plotFiles)
                            {
                                using IMEPackage pcc = MEPackageHandler.OpenMEPackage(plotFile);
                                if (StateEventMapView.TryFindStateEventMap(pcc, out ExportEntry export))
                                {
                                    var stateEventMap = BinaryBioStateEventMap.Load(export);
                                    if (stateEventMap.StateEvents.ContainsKey(transId))
                                    {
                                        matchedFile = plotFile;
                                    }
                                }
                            }

                            if (matchedFile != null)
                            {
                                var plotEd = new PlotEditorWindow();
                                plotEd.Show();
                                plotEd.LoadFile(matchedFile);
                                plotEd.GoToStateEvent(transId);
                            }
                            else
                            {
                                MessageBox.Show($"Could not find transition {transId} in any mounted state event map.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                    }
                    break;
                default:
                    OpenInToolkit(tool);
                    break;
            }
        }

        private bool CanOpenIn(object obj)
        {
            string tool = obj as string;
            return tool switch
            {
                "PackEdLine" => SelectedDialogueNode?.InterpData != null,
                "PackEd_StreamM" => SelectedDialogueNode?.WwiseStream_Male != null,
                "PackEd_StreamF" => SelectedDialogueNode?.WwiseStream_Female != null,
                "SeqEdLine" => SelectedDialogueNode?.InterpData != null,
                "FaceFXNS" => SelectedConv?.NonSpkrFFX != null,
                "FaceFXSpkrM" => SelectedSpeaker?.FaceFX_Male != null,
                "FaceFXSpkrF" => SelectedSpeaker?.FaceFX_Female != null,
                "FaceFXLineM" => SelectedDialogueNode?.SpeakerTag.FaceFX_Male != null,
                "FaceFXLineF" => SelectedDialogueNode?.SpeakerTag.FaceFX_Female != null,
                "SoundP_Bank" => SelectedConv?.WwiseBank != null,
                "SoundP_StreamM" => SelectedDialogueNode?.WwiseStream_Male != null,
                "SoundP_StreamF" => SelectedDialogueNode?.WwiseStream_Female != null,
                "InterpEdLine" => SelectedDialogueNode?.InterpData != null,
                "PlotDbCnd" => SelectedDialogueNode != null && SelectedDialogueNode.ConditionalOrBool != 0,
                "PlotDbTrans" => SelectedDialogueNode != null && SelectedDialogueNode.Transition != 0,
                _ => true
            };
        }
        private void OpenInToolkit(string tool, int uIndex = 0, string filename = null, string param = null)
        {
            string filePath = null;
            if (filename != null)  //If file is a new loaded file need to find path.
            {
                filePath = Path.Combine(Path.GetDirectoryName(Pcc.FilePath), filename);

                if (!File.Exists(filePath))
                {
                    string rootPath = null;
                    switch (Pcc.Game)
                    {
                        case MEGame.ME1:
                            rootPath = ME1Directory.DefaultGamePath;
                            break;
                        case MEGame.ME2:
                            rootPath = ME2Directory.DefaultGamePath;
                            break;
                        case MEGame.ME3:
                            rootPath = ME3Directory.DefaultGamePath;
                            break;
                        case MEGame.LE1:
                            rootPath = LE1Directory.DefaultGamePath;
                            break;
                        case MEGame.LE2:
                            rootPath = LE2Directory.DefaultGamePath;
                            break;
                        case MEGame.LE3:
                            rootPath = LE3Directory.DefaultGamePath;
                            break;
                    }
                    filePath = Directory.GetFiles(rootPath, Level, SearchOption.AllDirectories).FirstOrDefault();
                    if (filePath == null)
                    {
                        MessageBox.Show($"File {filename} not found.");
                        return;
                    }
                    var dlg = MessageBox.Show($"Opening level at {filePath}", "Dialogue Editor", MessageBoxButton.OKCancel);
                    if (dlg == MessageBoxResult.Cancel)
                    {
                        return;
                    }
                }
            }

            filePath ??= Pcc.FilePath;

            switch (tool)
            {

                case "FaceFXEditor":
                    if (Pcc.IsUExport(uIndex) && param != null)
                    {
                        new FaceFXEditorWindow(Pcc.GetUExport(uIndex), param).Show();
                    }
                    else if (Pcc.IsUExport(uIndex))
                    {
                        new FaceFXEditorWindow(Pcc.GetUExport(uIndex)).Show();
                    }
                    else
                    {
                        var facefxEditor = new FaceFXEditorWindow();
                        facefxEditor.LoadFile(filePath);
                        facefxEditor.Show();
                    }
                    break;
                case "PackageEditor":
                    var packEditor = new PackageEditorWindow();
                    packEditor.Show();
                    if (Pcc.IsUExport(uIndex) && filePath == Pcc.FilePath)
                    {
                        packEditor.LoadFile(Pcc.FilePath, uIndex);
                    }
                    else
                    {
                        packEditor.LoadFile(filePath, uIndex);
                    }
                    break;
                case "SoundplorerWPF":
                    if (Pcc.TryGetUExport(uIndex, out ExportEntry soundplorerExp))
                    {
                        new SoundplorerWPF(soundplorerExp).Show();
                    }
                    else
                    {
                        var soundplorerWPF = new SoundplorerWPF();
                        soundplorerWPF.LoadFile(Pcc.FilePath);
                        soundplorerWPF.Show();
                    }
                    break;
                case "SequenceEditor":
                    if (Pcc.IsUExport(uIndex) && filePath == Pcc.FilePath)
                    {
                        new SequenceEditorWPF(Pcc.GetUExport(uIndex)).Show();
                    }
                    else
                    {
                        var seqEditor = new SequenceEditorWPF();
                        seqEditor.Show();
                        if (uIndex != 0)
                        {
                            seqEditor.LoadFileAndGoTo(filePath, uIndex);
                        }
                        else seqEditor.LoadFile(filePath);
                    }
                    break;
            }
        }

        public void TrySelectStrRef(int strRef, bool suppressErrorMessageBox = false)
        {
            var selectedObj = SelectedObjects.FirstOrDefault();
            DiagNode tgt = CurrentObjects.AfterThenBefore(selectedObj).OfType<DiagNode>().FirstOrDefault(d => d.Node.LineStrRef == strRef);
            if (tgt != null)
            {
                DialogueNode_Selected(tgt);
                graphEditor.Camera.AnimateViewToCenterBounds(tgt.GlobalFullBounds, false, 100);
                graphEditor.Refresh();
            }
            else if (suppressErrorMessageBox == false)
            {
                MessageBox.Show($"\"{searchtext}\" not found");
            }
        }

        private string searchtext = "";
        private void SearchDialogue()
        {
            const string input = "Enter a TLK StringRef or the part of a line.";
            searchtext = PromptDialog.Prompt(this, input, "Search Dialogue", searchtext, true);

            if (!string.IsNullOrEmpty(searchtext))
            {
                var selectedObj = SelectedObjects.FirstOrDefault();
                DiagNode tgt = CurrentObjects.AfterThenBefore(selectedObj).OfType<DiagNode>().FirstOrDefault(d => d.Node.LineStrRef.ToString().Contains(searchtext)
                                                                                                               || d.Node.Line.Contains(searchtext, StringComparison.InvariantCultureIgnoreCase));
                if (tgt != null)
                {
                    DialogueNode_Selected(tgt);
                    graphEditor.Camera.AnimateViewToCenterBounds(tgt.GlobalFullBounds, false, 100);
                    graphEditor.Refresh();
                }
                else
                {
                    MessageBox.Show($"\"{searchtext}\" not found");
                }
            }
        }
        private void GoToBoxOpen()
        {
            if (!GotoBox.IsDropDownOpen)
            {
                GotoBox.IsDropDownOpen = true;
                Keyboard.Focus(GotoBox);
            }
            else
            {
                GotoBox.IsDropDownOpen = false;
            }
        }
        private static void LoadTLKManager()
        {
            if (!Application.Current.Windows.OfType<TLKManagerWPF>().Any())
            {
                var m = new TLKManagerWPF();
                m.Show();
            }
            else
            {
                Application.Current.Windows.OfType<TLKManagerWPF>().First().Focus();
            }
        }
        private void SaveImage()
        {
            if (CurrentObjects.Count == 0)
                return;
            string objectName = Regex.Replace(CurrentLoadedExport.ObjectName.Name, @"[<>:""/\\|?*]", "");
            var d = new SaveFileDialog
            {
                Filter = "PNG Files (*.png)|*.png",
                FileName = $"{CurrentFile}.{objectName}"
            };
            if (d.ShowDialog() == true)
            {
                PNode r = graphEditor.Root;
                RectangleF rr = r.GlobalFullBounds;
                PNode p = PPath.CreateRectangle(rr.X, rr.Y, rr.Width, rr.Height);
                p.Brush = Brushes.White;
                graphEditor.addBack(p);
                graphEditor.Camera.Visible = false;
                System.Drawing.Image image = graphEditor.Root.ToImage();
                graphEditor.Camera.Visible = true;
                image.Save(d.FileName, ImageFormat.Png);
                graphEditor.backLayer.RemoveAllChildren();
                MessageBox.Show("Done.");
            }
        }
        private void ChangeLineSize(object obj)
        {
            if (!(obj is string cmd))
            {
                var options = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(OptionsPath));
                cmd = ((double)options["LineTextSize"] * 10).ToString();
            }
            Menu_LineSize_00.IsChecked = false;
            Menu_LineSize_15.IsChecked = false;
            Menu_LineSize_20.IsChecked = false;
            Menu_LineSize_10.IsChecked = false;
            switch (cmd)
            {
                case "00":
                    Menu_LineSize_00.IsChecked = true;
                    DBox.LineScaleOption = 0f;
                    break;
                case "15":
                    Menu_LineSize_15.IsChecked = true;
                    DBox.LineScaleOption = 1.5f;
                    break;
                case "20":
                    Menu_LineSize_20.IsChecked = true;
                    DBox.LineScaleOption = 2.0f;
                    break;
                default:
                    DBox.LineScaleOption = 1.0f;
                    Menu_LineSize_10.IsChecked = true;
                    break;
            }
            RefreshView();
        }
        private void ChangeLineColor(object sender, RoutedPropertyChangedEventArgs<System.Windows.Media.Color?> e)
        {
            var source = (Xceed.Wpf.Toolkit.ColorPicker)sender;
            if (e.NewValue is not null)
            {
                var newcolor = e.NewValue.Value;
                switch (source.Name)
                {
                    case "ClrPcker_Line":
                        DBox.lineColor = newcolor.ToWinformsColor();
                        break;
                    case "ClrPcker_LinkText":
                        DObj.linkTextColor = newcolor.ToWinformsColor();
                        break;
                    case "ClrPcker_ParaInt":
                        DObj.paraintColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_RenInt":
                        DObj.renintColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_Agree":
                        DObj.agreeColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_Disagree":
                        DObj.disagreeColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_Friendly":
                        DObj.friendlyColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_Hostile":
                        DObj.hostileColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_Connection":
                        DObj.connectionColor = newcolor.ToWinformsColor();
                        break;
                    case "ClrPcker_EntryPen":
                        DObj.entryPenColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_ReplyPen":
                        DObj.replyPenColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_Entry":
                        DObj.entryColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_Reply":
                        DObj.replyColor = newcolor.ToWinformsColor(); ;
                        break;
                    case "ClrPcker_GraphBackground":
                        GraphBackgroundColor = newcolor.ToWinformsColor();
                        break;
                    case "ClrPcker_BoxColor":
                        BoxColor = newcolor.ToWinformsColor();
                        break;
                    case "ClrPcker_BoxText":
                        DObj.boxTextColor = newcolor.ToWinformsColor(); ;
                        break;
                }
                RefreshView();
            }
        }
        private void UpdateNodeBrush()
        {
            DObj._nodeBrush?.Dispose();
            DObj._nodeBrush = new System.Drawing.SolidBrush(DObj.boxColor);
            DObj._titleBoxBrush = new System.Drawing.SolidBrush(DObj.boxColor);
        }
        private void ResetColorsToDefault()
        {
            var cdlg = MessageBox.Show("Do you wish to reset the color scheme?", "Dialogue Editor", MessageBoxButton.OKCancel);
            if (cdlg == MessageBoxResult.Cancel)
                return;

            // Apply theme-appropriate defaults
            ApplyThemeDefaults();

            // Update node brush
            UpdateNodeBrush();

            // Update color pickers to reflect the new colors
            ClrPcker_Line.SelectedColor = DBox.lineColor.ToWPFColor();
            ClrPcker_LinkText.SelectedColor = DObj.linkTextColor.ToWPFColor();
            ClrPcker_ParaInt.SelectedColor = DObj.paraintColor.ToWPFColor();
            ClrPcker_RenInt.SelectedColor = DObj.renintColor.ToWPFColor();
            ClrPcker_Agree.SelectedColor = DObj.agreeColor.ToWPFColor();
            ClrPcker_Disagree.SelectedColor = DObj.disagreeColor.ToWPFColor();
            ClrPcker_Friendly.SelectedColor = DObj.friendlyColor.ToWPFColor();
            ClrPcker_Hostile.SelectedColor = DObj.hostileColor.ToWPFColor();
            ClrPcker_Connection.SelectedColor = DObj.connectionColor.ToWPFColor();
            ClrPcker_Entry.SelectedColor = DObj.entryColor.ToWPFColor();
            ClrPcker_EntryPen.SelectedColor = DObj.entryPenColor.ToWPFColor();
            ClrPcker_Reply.SelectedColor = DObj.replyColor.ToWPFColor();
            ClrPcker_ReplyPen.SelectedColor = DObj.replyPenColor.ToWPFColor();
            ClrPcker_GraphBackground.SelectedColor = GraphBackgroundColor.ToWPFColor();
            ClrPcker_BoxColor.SelectedColor = BoxColor.ToWPFColor();
            ClrPcker_BoxText.SelectedColor = DObj.boxTextColor.ToWPFColor();
            RefreshView();
        }
        private void Spacing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue == e.OldValue)
            {
                return;
            }
            RefreshView();
        }
        private void CopyStringToClipboard_Click(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(sender, Node_Text_LineString))
            {
                CopyStringToClipboard("Line");
            }
            else if (ReferenceEquals(sender, Interpdata_TxtBx))
            {
                CopyStringToClipboard("ItpDta");
            }
        }
        private async void CopyStringToClipboard(object obj)
        {
            if (!(obj is string cmd))
                return;
            Clipboard.Clear();
            string copytext = null;
            switch (cmd)
            {
                case "Line":
                    copytext = SelectedDialogueNode.Line;
                    break;
                case "ItpDta":
                    copytext = SelectedDialogueNode.InterpData.UIndex.ToString();
                    break;
            }

            if (copytext == null)
                return;

            Clipboard.SetText(copytext);
            var otext = StatusBar_OtherText.Text;
            StatusBar_OtherText.Text = "Copied to Clipboard.";
            await Task.Delay(4000);
            StatusBar_OtherText.Text = otext;
        }

        private void ForceRefresh(object obj)
        {
            SelectedSpeakerList.ClearEx();
            SelectedObjects.ClearEx();
            var reselectedNodeID = SelectedDialogueNode?.NodeCount ?? -1;
            var reselectedNodeReply = SelectedDialogueNode?.IsReply ?? false;
            SelectedDialogueNode = null;
            if (SelectedConv is not null)
            {
                SelectedConv.IsFirstParsed = false;
                SelectedConv.IsParsed = false;
            }
            FirstParse();
            FFXAnimsets.ClearEx();
            foreach (var exp in Pcc.Exports.Where(exp => exp.ClassName == "FaceFXAnimSet"))
            {
                FFXAnimsets.Add(exp);
            }

            RefreshView();

            if (reselectedNodeID >= 0)
            {
                DialogueNode_SelectByIndex(reselectedNodeID, reselectedNodeReply);
            }
        }

        #endregion

        private void ExtractSpeakerAudio()
        {
            if (SelectedSpeaker == null || SelectedSpeaker.SpeakerID < -2 || SelectedConv == null)
                return;

            // Get all entry nodes for this speaker
            List<DialogueNodeExtended> speakerEntries;
            if (SelectedSpeaker.SpeakerID == -2)
            {
                speakerEntries = SelectedConv.ReplyList.Where(e => e.SpeakerIndex == SelectedSpeaker.SpeakerID).ToList();
            }
            else
            {
                speakerEntries = SelectedConv.EntryList.Where(e => e.SpeakerIndex == SelectedSpeaker.SpeakerID).ToList();
            }

            if (!speakerEntries.Any())
            {
                MessageBox.Show($"No dialogue lines found for speaker '{SelectedSpeaker.SpeakerName}'.", "Dialogue Editor");
                return;
            }

            // Count available audio files
            var maleAudioCount = speakerEntries.Count(e => e.WwiseStream_Male != null);
            var femaleAudioCount = speakerEntries.Count(e => e.WwiseStream_Female != null);

            if (maleAudioCount == 0 && femaleAudioCount == 0)
            {
                MessageBox.Show($"No audio files found for speaker '{SelectedSpeaker.SpeakerName}'.", "Dialogue Editor");
                return;
            }

            // Ask if user wants to include dialogue text in filenames
            var includeDialogueResult = MessageBox.Show(
                "Include dialogue text in the filenames?\n\n" +
                "Yes - Filenames will include a shortened version of the spoken line\n" +
                "No - Filenames will only include entry number and string reference",
                "Include Dialogue Text",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            bool includeDialogueText = includeDialogueResult == MessageBoxResult.Yes;

            // Ask which genders to extract
            var genderDialog = new System.Windows.Window
            {
                Title = "Select Genders",
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };
            genderDialog.SetResourceReference(System.Windows.Window.BackgroundProperty, System.Windows.SystemColors.WindowBrushKey);
            genderDialog.SetResourceReference(System.Windows.Window.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(genderDialog);

            string genderChoice = null;

            var textBlock = new System.Windows.Controls.TextBlock
            {
                Text = $"Which audio files would you like to extract?\n\n" +
                       $"Male files available: {maleAudioCount}\n" +
                       $"Female files available: {femaleAudioCount}",
                Margin = new Thickness(10, 15, 10, 10),
                TextWrapping = TextWrapping.Wrap
            };
            textBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10)
            };

            var bothBtn = new System.Windows.Controls.Button { Content = "Both", Width = 70, Margin = new Thickness(5) };
            var maleBtn = new System.Windows.Controls.Button { Content = "Male", Width = 70, Margin = new Thickness(5) };
            var femaleBtn = new System.Windows.Controls.Button { Content = "Female", Width = 70, Margin = new Thickness(5) };
            var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 70, Margin = new Thickness(5), IsCancel = true };

            bothBtn.Click += (_, _) => { genderChoice = "both"; genderDialog.DialogResult = true; };
            maleBtn.Click += (_, _) => { genderChoice = "male"; genderDialog.DialogResult = true; };
            femaleBtn.Click += (_, _) => { genderChoice = "female"; genderDialog.DialogResult = true; };

            buttonPanel.Children.Add(bothBtn);
            buttonPanel.Children.Add(maleBtn);
            buttonPanel.Children.Add(femaleBtn);
            buttonPanel.Children.Add(cancelBtn);

            var mainPanel = new System.Windows.Controls.StackPanel();
            mainPanel.Children.Add(textBlock);
            mainPanel.Children.Add(buttonPanel);
            genderDialog.Content = mainPanel;

            if (genderDialog.ShowDialog() != true)
                return;

            bool extractMale = genderChoice is "both" or "male";
            bool extractFemale = genderChoice is "both" or "female";

            // Ask user to select folder
            using var folderDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = $"Select folder to extract audio for '{SelectedSpeaker.SpeakerName}'",
                ShowNewFolderButton = true
            };

            if (folderDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            try
            {
                var extractedCount = ExtractAudioFilesForSpeaker(speakerEntries, SelectedSpeaker.SpeakerName, includeDialogueText, extractMale, extractFemale, folderDialog.SelectedPath);

                MessageBox.Show($"Successfully extracted {extractedCount} audio file(s) for speaker '{SelectedSpeaker.SpeakerName}'.", "Dialogue Editor");

                // Open the folder in File Explorer
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderDialog.SelectedPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting audio: {ex.Message}", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Extracts audio files for a given speaker's dialogue entries and saves them to the specified output folder. 
        /// Filenames are generated based on the speaker name, entry number, string reference, and optionally 
        /// include a shortened version of the dialogue text.
        /// </summary>
        /// <param name="speakerEntries"></param>
        /// <param name="tag"></param>
        /// <param name="includeDialogueText"></param>
        /// <param name="extractMale">Whether to extract male audio files</param>
        /// <param name="extractFemale">Whether to extract female audio files</param>
        /// <param name="outputFolder"></param>
        /// <returns></returns>
        public static int ExtractAudioFilesForSpeaker(List<DialogueNodeExtended> speakerEntries, string tag, bool includeDialogueText, bool extractMale, bool extractFemale, string outputFolder)
        {
            int extractedCount = 0;
            string speakerName = Regex.Replace(tag, @"[<>:""/\\|?*]", "_");

            foreach (var entry in speakerEntries)
            {
                string baseFileName = $"{speakerName}_E{entry.NodeCount}_SR{entry.LineStrRef}";

                // Add dialogue text if requested
                if (includeDialogueText && !string.IsNullOrWhiteSpace(entry.Line))
                {
                    // Truncate to 40 characters and sanitize for filename
                    string dialogueText = entry.Line.Length > 40 ? entry.Line.Substring(0, 40) : entry.Line;
                    dialogueText = Regex.Replace(dialogueText, @"[<>:""/\\|?*]", "_");
                    dialogueText = dialogueText.Replace('\n', ' ').Replace('\r', ' ').Trim();
                    baseFileName += $"_{dialogueText}";
                }

                // Extract male audio
                if (extractMale && entry.WwiseStream_Male != null)
                {
                    string maleFileName = Path.Combine(outputFolder, $"{baseFileName}_M.wav");
                    if (ExtractWwiseAudio(entry.WwiseStream_Male, maleFileName))
                    {
                        extractedCount++;
                    }
                }

                // Extract female audio
                if (extractFemale && entry.WwiseStream_Female != null)
                {
                    string femaleFileName = Path.Combine(outputFolder, $"{baseFileName}_F.wav");
                    if (ExtractWwiseAudio(entry.WwiseStream_Female, femaleFileName))
                    {
                        extractedCount++;
                    }
                }
            }

            return extractedCount;
        }

        private static bool ExtractWwiseAudio(ExportEntry wwiseStream, string outputPath)
        {
            try
            {
                // Get the WwiseStream binary data and use CreateWave() to generate WAV file
                var wwiseStreamData = wwiseStream.GetBinaryData<WwiseStream>();
                string tempWavPath = wwiseStreamData.CreateWave();

                if (tempWavPath != null && File.Exists(tempWavPath))
                {
                    File.Copy(tempWavPath, outputPath, true);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to extract audio from {wwiseStream.InstancedFullPath}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Opens the bulk InterpGroup editor dialog for the currently selected dialogue node.
        /// </summary>
        private void OpenBulkInterpEditor()
        {
            if (SelectedDialogueNode?.InterpData == null || SelectedConv == null)
            {
                MessageBox.Show("No InterpData available for this node.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new BulkInterpEditorDialog(this, SelectedDialogueNode, SelectedConv);
            if (dialog.ShowDialog() == true)
            {
                // Refresh the view after changes
                ForceRefresh(null);
            }
        }

        #region Helpers
        public static string AddOrdinal(int num)
        {
            if (num <= 0) return num.ToString();
            switch (num % 100)
            {
                case 11:
                case 12:
                case 13:
                    return num + "th";
            }
            switch (num % 10)
            {
                case 1:
                    return num + "st";
                case 2:
                    return num + "nd";
                case 3:
                    return num + "rd";
                default:
                    return num + "th";
            }
        }
        /// <summary>
        /// Wait for bool condition to switch to false. Used for async delay. Await until awaitforfalse and awaitfortrue are synchronised or straight delay.
        /// </summary>
        /// <param name="waitforfalse">condition</param>
        /// <param name="waitfortrue">condition</param>
        /// <param name="delay">Delay in milliseconds.</param>
        /// <returns></returns>
        public async Task<bool> CheckProcess(int delay, bool waitforfalse = false, bool waitfortrue = true)
        {
            if (waitforfalse == waitfortrue)
            {
                return false;
            }

            await Task.Delay(new TimeSpan(0, 0, 0, 0, delay));
            return true;
        }

        #endregion Helpers

        #region Assets Tab Audio
        private void PlayAudioM_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDialogueNode?.WwiseStream_Male != null)
            {
                SoundpanelWPF_M.StartOrPausePlaying();
            }
        }

        private void PlayAudioF_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDialogueNode?.WwiseStream_Female != null)
            {
                SoundpanelWPF_F.StartOrPausePlaying();
            }
        }

        private async void ReplaceAudioM_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDialogueNode?.WwiseStream_Male != null)
            {
                await SoundpanelWPF_M.ReplaceAudioFromWave();
            }
        }

        private async void ReplaceAudioF_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDialogueNode?.WwiseStream_Female != null)
            {
                await SoundpanelWPF_F.ReplaceAudioFromWave();
            }
        }
        #endregion

        #region IRecents interface
        public void PropogateRecentsChange(string propogationSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        public string Toolname => "DialogueEditor";
        #endregion
    }
}