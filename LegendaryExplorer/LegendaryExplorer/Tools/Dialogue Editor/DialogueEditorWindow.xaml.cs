using LegendaryExplorer.Dialogs;
using LegendaryExplorer.DialogueEditor.DialogueEditorExperiments;
using LegendaryExplorer.Tools.Dialogue_Editor.DialogueEditorExperiments;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.SharedUI.PeregrineTreeView;
using LegendaryExplorer.Tools.ConditionalsEditor;
using LegendaryExplorer.Tools.FaceFXEditor;
using LegendaryExplorer.Tools.AssetDatabase;
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
using GongSolutions.Wpf.DragDrop;
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
    public partial class DialogueEditorWindow : WPFBase, IRecents, IDropTarget
    {
        private static readonly System.Windows.Data.IValueConverter ReplyCategoryBrushConverter = new ReplyCategoryToBrushConverter();
        private static readonly System.Windows.Data.IValueConverter ReplyCategoryDisplayConverter = new ReplyCategoryToDisplayConverter();
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

        internal enum CloneInsertionPosition
        {
            Top,
            SecondTop,
            ThirdTop,
            ThirdBottom,
            SecondBottom,
            Bottom
        }

        internal enum SpeakerNodeCloneInsertionPosition
        {
            AboveClone,
            BelowClone,
            TopOfList,
            BottomOfList
        }

        internal sealed class CloneDialogueNodeOptions
        {
            internal bool CloneLinks { get; init; }
            internal int LinkInsertionIndex { get; init; }
            internal bool CloneStartNode { get; init; }
            internal int StartInsertionIndex { get; init; }
            internal List<DiagEdEdge> InputEdges { get; init; } = [];
            internal int? NodeInsertionIndex { get; init; }
            internal SpeakerExtended ReplacementSpeaker { get; init; }
        }

        private sealed class BulkCloneIncomingLinkSnapshot
        {
            internal DialogueNodeExtended SourceNode { get; init; }
            internal int MatchingTargetOrdinal { get; init; }
            internal Property SourceLinkProperty { get; init; }
        }

        private sealed class BulkCloneStartLinkSnapshot
        {
            internal int MatchingTargetOrdinal { get; init; }
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
            set
            {
                if (SetProperty(ref _SelectedSpeaker, value))
                {
                    OnPropertyChanged(nameof(CanEditSelectedSpeakerFaceFX));
                    NotifySelectedSpeakerFaceFXChanged();
                }
            }
        }
        public bool CanEditSelectedSpeakerFaceFX => SelectedSpeaker?.SpeakerID >= -2;
        public string SelectedSpeakerMaleFaceFXDisplay => GetSpeakerFaceFXDisplayText(SelectedSpeaker?.FaceFX_Male);
        public string SelectedSpeakerFemaleFaceFXDisplay => GetSpeakerFaceFXDisplayText(SelectedSpeaker?.FaceFX_Female);
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
        private int suppressedPackageUpdateDepth;
        private int suppressInterpDataInterpreterUnloadDepth;
        private bool NoUIRefresh; //stops graph refresh on update.
        // FOR GRAPHING
        public ObservableCollectionExtended<DObj> CurrentObjects { get; } = new();
        public ObservableCollectionExtended<DObj> SelectedObjects { get; } = new();
        private readonly List<SaveData> extraSaveData = new();
        private bool panToSelection = true;
        private DiagNode inlineLinkEditorNode;
        private bool inlineLinkEditorIsReply;
        private bool inlineLinkEditorNeedsSave;
        private readonly Dictionary<string, DataGridLength> inlineLinkEditorColumnWidths = new();
        private readonly Dictionary<int, List<DObj>> conversationGraphCache = new();
        private readonly Dictionary<int, (float X, float Y, float ViewScale)> conversationGraphViewStates = new();
        private int? speakerNodeFilterSpeakerId;
        private bool hideUnrelatedConnectionsOnSelection = true;
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
        public ICommand CloneSpeakerNodesCommand { get; set; }
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
        public ICommand LocalizeSpeakerFaceFXCommand { get; set; }
        public ICommand ImportSpeakerFaceFXAudioCommand { get; set; }
        public ICommand BulkEditInterpGroupsCommand { get; set; }
        private bool copiedOutgoingConnectionsAreReplyNode;
        private List<ReplyChoiceNode> copiedOutgoingConnections;
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
            SelectBottomViewportTab("Speaker Details", ConversationDetailsTab);
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
            HideUnrelatedConnectionsOnSelection_MenuItem.IsChecked = hideUnrelatedConnectionsOnSelection;
            RebuildSpeakerNodeFilterMenu();
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
                if (options.ContainsKey("HideUnrelatedConnectionsOnSelection"))
                    HideUnrelatedConnectionsOnSelection_MenuItem.IsChecked = (bool)options["HideUnrelatedConnectionsOnSelection"];
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

        private void SelectBottomViewportTab(string header, TabItem fallbackTab = null)
        {
            if (BottomViewportTabControl == null)
            {
                return;
            }

            BottomViewportTabControl.SelectedItem = BottomViewportTabControl.Items
                .OfType<TabItem>()
                .FirstOrDefault(t => string.Equals(t.Header?.ToString(), header, StringComparison.CurrentCulture))
                ?? fallbackTab;
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
            CloneSpeakerNodesCommand = new RelayCommand(CloneSpeakerNodes, CanCloneSpeakerNodes);
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
            LocalizeSpeakerFaceFXCommand = new GenericCommand(() => DialogueEditorExperimentsS.LocalizeSpeakerFaceFX(this), () => SelectedSpeaker != null && SelectedConv != null);
            ImportSpeakerFaceFXAudioCommand = new RelayCommand(ImportSpeakerFaceFXAudio, CanImportSpeakerFaceFXAudio);
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
                {"HideUnrelatedConnectionsOnSelection", hideUnrelatedConnectionsOnSelection},
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
                speakerNodeFilterSpeakerId = null;
                Conversations.ClearEx();
                SelectedSpeakerList.ClearEx();
                SelectedObjects.ClearEx();
                SelectedDialogueNode = null;
                SelectedConv = null;

                LoadMEPackage(fileName);
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
            speakerNodeFilterSpeakerId = null;
            SelectedConv = null;
            CurrentLoadedExport = null;
            Conversations.ClearEx();
            SelectedSpeakerList.ClearEx();
            RebuildSpeakerNodeFilterMenu();
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
                ApplyAssetDatabaseOwnerFriendlyName(SelectedConv);
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
                ApplyAssetDatabaseOwnerFriendlyName(conv);
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
        }

        public static string GetReplyCategoryDisplayText(EReplyCategory category)
        {
            return category switch
            {
                EReplyCategory.REPLY_CATEGORY_DEFAULT => "Default (Right Middle)",
                EReplyCategory.REPLY_CATEGORY_AGREE => "Agree (Right Top)",
                EReplyCategory.REPLY_CATEGORY_DISAGREE => "Disagree (Right Bottom)",
                EReplyCategory.REPLY_CATEGORY_FRIENDLY => "Friendly (Left Top)",
                EReplyCategory.REPLY_CATEGORY_HOSTILE => "Hostile (Left Bottom)",
                EReplyCategory.REPLY_CATEGORY_INVESTIGATE => "Investigate (Left Middle)",
                EReplyCategory.REPLY_CATEGORY_PARAGON_INTERRUPT => "Paragon Interrupt",
                EReplyCategory.REPLY_CATEGORY_RENEGADE_INTERRUPT => "Renegade Interrupt",
                _ => category.ToString()
            };
        }

        public static string GetReplyCategoryDisplayText(string category)
        {
            return Enum.TryParse(category, out EReplyCategory replyCategory)
                ? GetReplyCategoryDisplayText(replyCategory)
                : category;
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
            return GetDisplayTlkText(id, package);
        }

        private static void ApplyAssetDatabaseOwnerFriendlyName(ConversationExtended conversation)
        {
            if (conversation?.Export?.FileRef == null || conversation.Speakers == null)
            {
                return;
            }

            var ownerSpeaker = conversation.Speakers.FirstOrDefault(speaker => speaker.SpeakerID == -1);
            if (ownerSpeaker == null)
            {
                return;
            }

            var ownerFriendlyName = ConversationOwnerFriendlyNameResolver.GetConversationOwnerFriendlyName(conversation.Export.FileRef.Game, conversation.ConvName);
            if (!string.IsNullOrWhiteSpace(ownerFriendlyName))
            {
                ownerSpeaker.FriendlyName = ownerFriendlyName;
            }
        }

        private static string GetDisplayTlkText(int id, IMEPackage package)
        {
            return RemoveWrappingQuotes(GlobalFindStrRefbyID(id, package));
        }

        private void GenerateSpeakerList()
        {
            SelectedSpeakerList.ClearEx();

            foreach (var spkr in SelectedConv.Speakers)
            {
                SelectedSpeakerList.Add(spkr);
            }

            RebuildListenersList();
            RebuildSpeakerNodeFilterMenu();
        }

        private void RebuildListenersList()
        {
            ListenersList.ClearEx();
            ListenersList.Add(new SpeakerExtended(-3, "None"));
            foreach (var spkr in SelectedSpeakerList)
            {
                ListenersList.Add(spkr);
            }
        }

        private bool NodeMatchesSpeakerNodeFilter(DiagNode node)
        {
            if (!speakerNodeFilterSpeakerId.HasValue || node?.Node == null)
            {
                return false;
            }

            int speakerId = speakerNodeFilterSpeakerId.Value;
            return node.Node.SpeakerIndex == speakerId || node.Node.SpeakerTag?.SpeakerID == speakerId;
        }

        private HashSet<DiagNode> GetSpeakerNodeFilterMatchedNodes()
        {
            return speakerNodeFilterSpeakerId.HasValue
                ? CurrentObjects.OfType<DiagNode>().Where(NodeMatchesSpeakerNodeFilter).ToHashSet()
                : null;
        }

        private HashSet<DObj> GetSpeakerNodeFilterVisibleObjects(HashSet<DiagNode> matchedNodes)
        {
            if (matchedNodes == null)
            {
                return null;
            }

            HashSet<DObj> visibleObjects = matchedNodes.Cast<DObj>().ToHashSet();

            if (graphEditor?.edgeLayer == null)
            {
                return visibleObjects;
            }

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                var originNode = edge.originator as DiagNode;
                DObj endOwner = edge.GetEndOwner();
                var endNode = endOwner as DiagNode;

                if ((originNode != null && matchedNodes.Contains(originNode))
                    || (endNode != null && matchedNodes.Contains(endNode)))
                {
                    if (edge.originator != null)
                    {
                        visibleObjects.Add(edge.originator);
                    }

                    if (endOwner != null)
                    {
                        visibleObjects.Add(endOwner);
                    }
                }
            }

            return visibleObjects;
        }

        private void RebuildSpeakerNodeFilterMenu()
        {
            if (FindName("SpeakerNodeFilter_MenuItem") is not MenuItem speakerNodeFilterMenuItem)
            {
                return;
            }

            speakerNodeFilterMenuItem.Items.Clear();
            speakerNodeFilterMenuItem.IsEnabled = SelectedConv != null && SelectedSpeakerList.Count > 0;

            var offItem = new MenuItem
            {
                Header = "Off",
                IsCheckable = true,
                IsChecked = !speakerNodeFilterSpeakerId.HasValue
            };
            offItem.Click += SpeakerNodeFilterOff_Click;
            speakerNodeFilterMenuItem.Items.Add(offItem);

            if (SelectedSpeakerList.Count == 0)
            {
                return;
            }

            speakerNodeFilterMenuItem.Items.Add(new Separator());
            foreach (SpeakerExtended speaker in SelectedSpeakerList)
            {
                var item = new MenuItem
                {
                    Header = $"{speaker.SpeakerID}: {speaker.DisplayName}",
                    IsCheckable = true,
                    IsChecked = speakerNodeFilterSpeakerId == speaker.SpeakerID,
                    Tag = speaker.SpeakerID
                };
                item.Click += SpeakerNodeFilterSpeaker_Click;
                speakerNodeFilterMenuItem.Items.Add(item);
            }
        }

        private void SpeakerNodeFilterOff_Click(object sender, RoutedEventArgs e)
        {
            speakerNodeFilterSpeakerId = null;
            RebuildSpeakerNodeFilterMenu();
            UpdateSelectedConnectionHighlighting();
            graphEditor?.Refresh();
        }

        private void SpeakerNodeFilterSpeaker_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not int speakerId)
            {
                return;
            }

            speakerNodeFilterSpeakerId = speakerNodeFilterSpeakerId == speakerId ? null : speakerId;
            RebuildSpeakerNodeFilterMenu();
            UpdateSelectedConnectionHighlighting();
            graphEditor?.Refresh();
        }

        private void ApplySpeakerNodeHighlighting()
        {
            int selectedSpeakerId = SelectedSpeaker?.SpeakerID ?? -3;
            bool shouldHighlight = SelectedConv != null && selectedSpeakerId >= -2;

            foreach (var diagNode in CurrentObjects.OfType<DiagNode>())
            {
                diagNode.IsSpeakerHighlighted = shouldHighlight
                    && (diagNode.Node?.SpeakerIndex == selectedSpeakerId
                        || diagNode.Node?.SpeakerTag?.SpeakerID == selectedSpeakerId);
            }

            UpdateSelectedConnectionHighlighting();
        }

        private void UpdateSelectedConnectionHighlighting()
        {
            if (graphEditor?.edgeLayer == null)
            {
                return;
            }

            HashSet<DiagNode> matchedSpeakerNodes = GetSpeakerNodeFilterMatchedNodes();
            HashSet<DObj> speakerVisibleObjects = GetSpeakerNodeFilterVisibleObjects(matchedSpeakerNodes);
            if (speakerVisibleObjects != null && SelectedObjects.Any(obj => !speakerVisibleObjects.Contains(obj)))
            {
                ClearGraphSelection();
                return;
            }

            foreach (DObj graphObject in CurrentObjects)
            {
                bool isVisible = speakerVisibleObjects?.Contains(graphObject) ?? true;
                graphObject.Visible = isVisible;
                graphObject.Pickable = isVisible;
            }

            HashSet<DObj> selectedGraphObjects = SelectedObjects.OfType<DObj>().ToHashSet();
            bool hasSelection = selectedGraphObjects.Count > 0;

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                bool isStartConnection = edge.originator is DStart;
                bool matchesSpeakerFilter = speakerVisibleObjects == null
                    || ((edge.originator is DiagNode originNode && matchedSpeakerNodes.Contains(originNode))
                        || (edge.GetEndOwner() is DiagNode endNode && matchedSpeakerNodes.Contains(endNode)));
                bool isConnectedToSelection = hasSelection
                    && (selectedGraphObjects.Contains(edge.originator)
                        || selectedGraphObjects.Contains(edge.GetEndOwner()));

                edge.Visible = matchesSpeakerFilter
                    && (isStartConnection || !hideUnrelatedConnectionsOnSelection || !hasSelection || isConnectedToSelection);
                edge.Pickable = edge.Visible;
                edge.ApplyVisualState(isConnectedToSelection, matchesSpeakerFilter && !isStartConnection && hasSelection && !isConnectedToSelection);
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
                node.ExportID = nodeprop.GetProp<IntProperty>("nExportID");
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
        public void PushLocalGraphChanges(DiagNode obj, bool persistConversation = true)
        {
            IsLocalUpdate = true;
            if (persistConversation)
            {
                RecreateNodesToProperties(SelectedConv);
            }

            float newX = obj.X + obj.OffsetX;
            float newY = obj.Y + obj.OffsetY;
            obj.SyncIdentityFromNode();
            obj.RemoveAllChildren();
            obj.RemoveConnections();
            obj.GetOutputLinks(obj.Node);
            obj.Layout(newX, newY);
            obj.RecreateConnections(CurrentObjects);

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }

            UpdateSelectedConnectionHighlighting();
            graphEditor.Refresh();
        }

        private void RemoveGraphObject(DObj obj)
        {
            switch (obj)
            {
                case DiagNode diagNode:
                    diagNode.RemoveConnections();
                    diagNode.InputEdges.Clear();
                    break;
                case DStart startNode:
                    startNode.RemoveConnections();
                    break;
            }

            graphEditor.nodeLayer.RemoveChild(obj);
            CurrentObjects.Remove(obj);
            obj.Dispose();
        }

        private void RebuildStartNodesInPlace(IReadOnlyDictionary<int, PointF> preferredPositions = null)
        {
            var existingStarts = CurrentObjects.OfType<DStart>().OrderBy(s => s.Order).ToList();
            var startPositions = existingStarts.ToDictionary(s => s.Order, s => new PointF(s.X + s.OffsetX, s.Y + s.OffsetY));
            var rebuiltStarts = new List<DStart>();

            if (preferredPositions != null)
            {
                foreach (var preferredPosition in preferredPositions)
                {
                    startPositions[preferredPosition.Key] = preferredPosition.Value;
                }
            }

            foreach (var start in existingStarts)
            {
                RemoveGraphObject(start);
            }

            foreach (var startLink in SelectedConv.StartingList.OrderBy(kvp => kvp.Key))
            {
                PointF position = startPositions.TryGetValue(startLink.Key, out var savedPosition)
                    ? savedPosition
                    : new PointF(0, startLink.Key * 127);

                var startNode = new DStart(this, startLink.Key, startLink.Value, position.X, position.Y, graphEditor);
                CurrentObjects.Add(startNode);
                graphEditor.addNode(startNode);
                startNode.MouseDown += node_MouseDown;
                startNode.Click += node_Click;
                rebuiltStarts.Add(startNode);
            }

            foreach (var startNode in rebuiltStarts)
            {
                startNode.RecreateConnections(CurrentObjects);
            }

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }
        }

        private PointF GetNewDialogueNodePosition(bool isReply, DiagNode anchorNode = null)
        {
            if (graphEditor?.Camera != null)
            {
                RectangleF viewBounds = graphEditor.Camera.ViewBounds;
                const float defaultNodeWidth = 220f;
                const float defaultNodeHeight = 140f;
                return new PointF(
                    viewBounds.X + ((viewBounds.Width - defaultNodeWidth) / 2f),
                    viewBounds.Y + ((viewBounds.Height - defaultNodeHeight) / 2f));
            }

            if (anchorNode != null)
            {
                return new PointF(anchorNode.X + anchorNode.OffsetX + 100, anchorNode.Y + anchorNode.OffsetY + 150);
            }

            var existingNodes = CurrentObjects
                .OfType<DiagNode>()
                .Where(node => node.Node.IsReply == isReply)
                .OrderBy(node => node.Y + node.OffsetY)
                .ToList();

            if (existingNodes.Count > 0)
            {
                var columnNode = existingNodes[0];
                return new PointF(
                    columnNode.X + columnNode.OffsetX,
                    existingNodes.Max(node => node.GlobalFullBounds.Bottom) + 25);
            }

            return isReply ? new PointF(500, 20) : new PointF(250, 0);
        }

        private PointF GetNewStartNodePosition(int startOrder, int targetEntryIndex)
        {
            var targetNode = CurrentObjects
                .OfType<DiagNode>()
                .FirstOrDefault(node => !node.Node.IsReply && node.Node.NodeCount == targetEntryIndex);

            if (targetNode != null)
            {
                RectangleF targetBounds = targetNode.GlobalFullBounds;
                const float horizontalPadding = 90f;
                return new PointF(targetBounds.Left - targetBounds.Width - horizontalPadding, targetBounds.Top);
            }

            if (graphEditor?.Camera != null)
            {
                RectangleF viewBounds = graphEditor.Camera.ViewBounds;
                return new PointF(viewBounds.X, viewBounds.Y + (viewBounds.Height / 2f));
            }

            return new PointF(0, startOrder * 127);
        }

        private void AddDialogueNodeToGraphInPlace(DiagNode node, PointF position, bool centerView = true)
        {
            if (node == null)
            {
                return;
            }

            CurrentObjects.Add(node);
            node.Layout(position.X, position.Y);
            graphEditor.addNode(node);
            node.SetOffset(position.X, position.Y);
            node.MouseDown += node_MouseDown;
            node.Click += node_Click;
            node.RecreateConnections(CurrentObjects);

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }

            if (centerView)
            {
                graphEditor.Camera.AnimateViewToCenterBounds(node.GlobalFullBounds, false, 500);
            }

            ApplySpeakerNodeHighlighting();
            UpdateSelectedConnectionHighlighting();
            graphEditor.Refresh();
        }

        private void ApplyStartMutationInPlace(IReadOnlyDictionary<int, PointF> preferredPositions = null)
        {
            IsLocalUpdate = true;
            RecreateNodesToProperties(SelectedConv);
            RebuildStartNodesInPlace(preferredPositions);
            Start_ListBoxUpdate();

            if (inlineLinkEditorNode != null)
            {
                LoadInlineLinkEditor(inlineLinkEditorNode);
            }

            UpdateSelectedConnectionHighlighting();
            graphEditor.Refresh();
        }

        private void AddDialogueNodeInPlace(bool isReply)
        {
            if (SelectedConv == null)
            {
                return;
            }

            var anchorNode = SelectedObjects.OfType<DiagNode>().FirstOrDefault();
            PointF position = GetNewDialogueNodePosition(isReply, anchorNode);
            int newIndex;
            DiagNode graphNode;

            if (isReply)
            {
                PropertyCollection newprop = GlobalUnrealObjectInfo.getDefaultStructValue(Pcc.Game, "BioDialogReplyNode", true, Pcc);
                var props = SelectedConv.BioConvo.GetProp<ArrayProperty<StructProperty>>("m_ReplyList") ??
                            new ArrayProperty<StructProperty>("m_ReplyList");
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

                newIndex = SelectedConv.ReplyList.Count;
                var nodeExtended = SelectedConv.ParseSingleLine(props[newIndex], newIndex, true, TLKLookup);
                InitializeDialogueNodeDerivedData(nodeExtended);
                SelectedConv.ReplyList.Add(nodeExtended);

                IsLocalUpdate = true;
                RecreateNodesToProperties(SelectedConv);
                graphNode = new DiagNodeReply(this, nodeExtended, position.X, position.Y, graphEditor);
            }
            else
            {
                PropertyCollection newprop = GlobalUnrealObjectInfo.getDefaultStructValue(Pcc.Game, "BioDialogEntryNode", true, Pcc);
                var props = SelectedConv.BioConvo.GetProp<ArrayProperty<StructProperty>>("m_EntryList") ??
                            new ArrayProperty<StructProperty>("m_EntryList");
                newprop.AddOrReplaceProp(new EnumProperty("GUI_STYLE_NONE", "EConvGUIStyles", Pcc.Game, "eGUIStyle"));
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

                newIndex = SelectedConv.EntryList.Count;
                var nodeExtended = SelectedConv.ParseSingleLine(props[newIndex], newIndex, false, TLKLookup);
                InitializeDialogueNodeDerivedData(nodeExtended);
                SelectedConv.EntryList.Add(nodeExtended);

                IsLocalUpdate = true;
                RecreateNodesToProperties(SelectedConv);
                graphNode = new DiagNodeEntry(this, nodeExtended, position.X, position.Y, graphEditor);
            }

            AddDialogueNodeToGraphInPlace(graphNode, position, centerView: false);
            DialogueNode_SelectByIndex(newIndex, isReply);
        }

        private void RebuildGraphInPlace(bool rebuildStarts = false)
        {
            foreach (var graphObject in CurrentObjects.OfType<DBox>().ToList())
            {
                graphObject.RemoveConnections();
            }

            graphEditor.edgeLayer.RemoveAllChildren();

            foreach (var diagNode in CurrentObjects.OfType<DiagNode>())
            {
                diagNode.InputEdges.Clear();
            }

            if (rebuildStarts)
            {
                RebuildStartNodesInPlace();
            }

            foreach (var diagNode in CurrentObjects.OfType<DiagNode>().ToList())
            {
                float x = diagNode.X + diagNode.OffsetX;
                float y = diagNode.Y + diagNode.OffsetY;
                bool wasSelected = diagNode.IsSelected;

                diagNode.SyncIdentityFromNode();
                diagNode.RemoveAllChildren();
                diagNode.GetOutputLinks(diagNode.Node);
                diagNode.Layout(x, y);
                diagNode.IsSelected = wasSelected;
            }

            foreach (var graphObject in CurrentObjects.OfType<DBox>())
            {
                graphObject.RecreateConnections(CurrentObjects);
            }

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }

            UpdateSelectedConnectionHighlighting();
            graphEditor.Refresh();
        }

        private void ReindexConversationNodeCounts()
        {
            for (int i = 0; i < SelectedConv.EntryList.Count; i++)
            {
                SelectedConv.EntryList[i].NodeCount = i;
            }

            for (int i = 0; i < SelectedConv.ReplyList.Count; i++)
            {
                SelectedConv.ReplyList[i].NodeCount = i;
            }
        }

        private void ShiftConversationLinksForInsertedNode(bool insertedNodeIsReply, int insertionIndex)
        {
            if (SelectedConv == null)
            {
                return;
            }

            if (insertedNodeIsReply)
            {
                foreach (var entry in SelectedConv.EntryList)
                {
                    var replyLinksProp = entry.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");
                    if (replyLinksProp == null)
                    {
                        continue;
                    }

                    foreach (var link in replyLinksProp)
                    {
                        var linkIndex = link.GetProp<IntProperty>("nIndex");
                        if (linkIndex != null && linkIndex.Value >= insertionIndex)
                        {
                            linkIndex.Value++;
                        }
                    }
                }
            }
            else
            {
                foreach (var reply in SelectedConv.ReplyList)
                {
                    var entryLinksProp = reply.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
                    if (entryLinksProp == null)
                    {
                        continue;
                    }

                    foreach (var link in entryLinksProp)
                    {
                        if (link.Value >= insertionIndex)
                        {
                            link.Value++;
                        }
                    }
                }

                foreach ((int key, int val) in SelectedConv.StartingList.ToList())
                {
                    if (val >= insertionIndex)
                    {
                        SelectedConv.StartingList[key] = val + 1;
                    }
                }
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

        private void ReindexSelectedSpeakerIds()
        {
            for (int i = 0; i < SelectedSpeakerList.Count; i++)
            {
                SelectedSpeakerList[i].SpeakerID = i - 2;
            }
        }

        private void RebindConversationNodeSpeakers()
        {
            if (SelectedConv == null)
            {
                return;
            }

            foreach (var node in SelectedConv.EntryList)
            {
                node.SpeakerTag = SelectedConv.Speakers.FirstOrDefault(s => s.SpeakerID == node.SpeakerIndex);
            }

            foreach (var node in SelectedConv.ReplyList)
            {
                node.SpeakerTag = SelectedConv.Speakers.FirstOrDefault(s => s.SpeakerID == node.SpeakerIndex);
            }
        }

        private void SyncSelectedConversationSpeakerCache()
        {
            if (SelectedConv == null)
            {
                return;
            }

            var cachedConversation = Conversations.FirstOrDefault(c => c.UIndex == SelectedConv.UIndex);
            if (cachedConversation == null || ReferenceEquals(cachedConversation, SelectedConv))
            {
                return;
            }

            cachedConversation.Speakers = SelectedConv.Speakers;
        }

        private void RefreshSpeakerStateInPlace(bool rebuildGraphInPlace = false, bool refreshSelectedNodeAssets = true)
        {
            if (SelectedConv == null)
            {
                return;
            }

            SelectedConv.Speakers = new ObservableCollectionExtended<SpeakerExtended>(SelectedSpeakerList);
            SyncSelectedConversationSpeakerCache();
            RebuildListenersList();
            RebuildSpeakerNodeFilterMenu();
            RebindConversationNodeSpeakers();

            Speakers_ListBox.Items.Refresh();
            Node_Combo_Spkr.Items.Refresh();
            Node_Combo_Lstnr.Items.Refresh();

            if (SelectedDialogueNode != null)
            {
                SelectedDialogueNode.SpeakerTag = SelectedConv.Speakers.FirstOrDefault(s => s.SpeakerID == SelectedDialogueNode.SpeakerIndex);
                if (refreshSelectedNodeAssets)
                {
                    RefreshExportLoaders();
                }
            }

            if (rebuildGraphInPlace)
            {
                RebuildGraphInPlace();
            }

            NotifySelectedSpeakerFaceFXChanged();
            ApplySpeakerNodeHighlighting();
            UpdateSelectedConnectionHighlighting();
            graphEditor?.Refresh();
        }

        private void SaveSpeakerChangesInPlace(bool rebuildGraphInPlace = false, bool refreshSelectedNodeAssets = true, bool reindexSpeakerIds = false)
        {
            if (SelectedConv == null)
            {
                return;
            }

            if (reindexSpeakerIds)
            {
                ReindexSelectedSpeakerIds();
            }

            SelectedConv.Speakers = new ObservableCollectionExtended<SpeakerExtended>(SelectedSpeakerList);
            IsLocalUpdate = true;
            SaveSpeakersToProperties(SelectedSpeakerList);
            RefreshSpeakerStateInPlace(rebuildGraphInPlace, refreshSelectedNodeAssets);
        }

        public void ApplyLocalizedSpeakerFaceFXInPlace(ExportEntry newMaleFaceFx, ExportEntry newFemaleFaceFx)
        {
            if (SelectedConv == null || SelectedSpeaker == null)
            {
                return;
            }

            int speakerIndex = SelectedSpeaker.SpeakerID + 2;
            if (speakerIndex < 0 || speakerIndex >= SelectedSpeakerList.Count)
            {
                return;
            }

            if (newMaleFaceFx != null)
            {
                SelectedSpeaker.FaceFX_Male = newMaleFaceFx;
                SelectedSpeakerList[speakerIndex].FaceFX_Male = newMaleFaceFx;
                if (!FFXAnimsets.OfType<IEntry>().Any(x => x.UIndex == newMaleFaceFx.UIndex))
                {
                    FFXAnimsets.Add(newMaleFaceFx);
                }
            }

            if (newFemaleFaceFx != null)
            {
                SelectedSpeaker.FaceFX_Female = newFemaleFaceFx;
                SelectedSpeakerList[speakerIndex].FaceFX_Female = newFemaleFaceFx;
                if (!FFXAnimsets.OfType<IEntry>().Any(x => x.UIndex == newFemaleFaceFx.UIndex))
                {
                    FFXAnimsets.Add(newFemaleFaceFx);
                }
            }

            RefreshSpeakerStateInPlace(refreshSelectedNodeAssets: true);
        }

        private void NotifySelectedSpeakerFaceFXChanged()
        {
            OnPropertyChanged(nameof(SelectedSpeakerMaleFaceFXDisplay));
            OnPropertyChanged(nameof(SelectedSpeakerFemaleFaceFXDisplay));
        }

        private static string GetSpeakerFaceFXDisplayText(IEntry faceFx)
        {
            return faceFx == null ? "None" : $"#{faceFx.UIndex} {faceFx.ObjectName.Instanced}";
        }
        #endregion RecreateToFile

        #region Handling-updates
        public override void HandleUpdate(List<PackageUpdate> updates)
        {
            if (Pcc == null || IsLocalUpdate || suppressedPackageUpdateDepth > 0)
            {
                if (IsLocalUpdate || suppressedPackageUpdateDepth > 0) //If local load just refresh interpreter
                {
                    Properties_InterpreterWPF.LoadExport(SelectedConv.Export);
                    if (suppressedPackageUpdateDepth == 0)
                    {
                        IsLocalUpdate = false;
                    }
                }
                return; //nothing is loaded
            }

            InterpData_MetadataEditor.LoadPccData(Pcc);

            List<PackageUpdate> relevantUpdates = updates.Where(x => x.Change.HasFlag(PackageChange.Export)).ToList();
            HashSet<int> updatedExportIndexes = relevantUpdates.Select(x => x.Index).ToHashSet();

            if (InterpDataTreeNodes.Count > 0)
            {
                var interpTreeIndexes = InterpDataTreeNodes
                    .SelectMany(root => root.FlattenTree())
                    .Select(node => node.UIndex)
                    .ToHashSet();

                if (updatedExportIndexes.Overlaps(interpTreeIndexes))
                {
                    RefreshInterpDataTreePreserveState();
                }
            }

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
                InterpData_MetadataEditor.UnloadExport();
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

            RemoveConversationGraphCache(updatedConvos);

            if (SelectedDialogueNode != null) //Update any changes to live dialogue node
            {
                if (SelectedDialogueNode.InterpData != null && updatedExportIndexes.Contains(SelectedDialogueNode.InterpData.UIndex))
                {
                    if (SelectedDialogueNode.InterpData.ClassName == "InterpData")
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

            var needsRefresh = false; //Controls if refresh chart (auto happens on full parse)
            var needsNodeRefresh = false;
            var needsPlotSectionRefresh = false;

            switch (e.PropertyName)         // Props in both replies and entries. All Games.
            {
                case "Listener":
                    var nListenerIndex = new IntProperty(node.Listener, "nListenerIndex");
                    prop.Properties.AddOrReplaceProp(nListenerIndex);
                    needsNodeRefresh = true;
                    break;
                case "SpeakerIndex":
                    node.SpeakerTag = SelectedSpeakerList.FirstOrDefault(s => s.SpeakerID == node.SpeakerIndex);
                    needsNodeRefresh = true;
                    break;
                case "LineStrRef":
                    var srText = new StringRefProperty(node.LineStrRef, "srText");
                    prop.Properties.AddOrReplaceProp(srText);
                    ApplyLineStrRefChange(node);
                    return;
                case "ExportID":
                    var nExportID = new IntProperty(node.ExportID, "nExportID");
                    prop.Properties.AddOrReplaceProp(nExportID);
                    ApplyExportIdChange(node);
                    return;
                case "ConditionalOrBool":
                    var nConditionalFunc = new IntProperty(node.ConditionalOrBool, "nConditionalFunc");
                    prop.Properties.AddOrReplaceProp(nConditionalFunc);
                    node.ConditionalPlotPath = node.FiresConditional
                        ? PlotDatabases.FindPlotConditionalByID(node.ConditionalOrBool, Pcc.Game)?.Path
                        : PlotDatabases.FindPlotBoolByID(node.ConditionalOrBool, Pcc.Game)?.Path;
                    needsPlotSectionRefresh = true;
                    break;
                case "ConditionalParam":
                    var nConditionalParam = new IntProperty(node.ConditionalParam, "nConditionalParam");
                    prop.Properties.AddOrReplaceProp(nConditionalParam);
                    needsPlotSectionRefresh = true;
                    break;
                case "Transition":
                    var nStateTransition = new IntProperty(node.Transition, "nStateTransition");
                    prop.Properties.AddOrReplaceProp(nStateTransition);
                    node.TransitionPlotPath = PlotDatabases.FindPlotTransitionByID(node.Transition, Pcc.Game)?.Path;
                    needsPlotSectionRefresh = true;
                    break;
                case "TransitionParam":
                    var nStateTransitionParam = new IntProperty(node.TransitionParam, "nStateTransitionParam");
                    prop.Properties.AddOrReplaceProp(nStateTransitionParam);
                    needsPlotSectionRefresh = true;
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
                    node.ConditionalPlotPath = node.FiresConditional
                        ? PlotDatabases.FindPlotConditionalByID(node.ConditionalOrBool, Pcc.Game)?.Path
                        : PlotDatabases.FindPlotBoolByID(node.ConditionalOrBool, Pcc.Game)?.Path;
                    needsPlotSectionRefresh = true;
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

            if (needsPlotSectionRefresh)
            {
                RefreshNodePlotSectionsInGraph(node);
            }
            else if (needsNodeRefresh)
            {
                RefreshNodeInGraph(node, persistConversation: false);
            }
            else if (needsRefresh)
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

            ApplySpeakerNodeHighlighting();

            graphEditor.Camera.X = 0;
            graphEditor.Camera.Y = 0;
            if (!regenerate && (SavedPositions.IsEmpty() || SaveViewMode == ESaveViewMode.AutoGenerate))
            {
                AutoLayout();
            }

            UpdateSelectedConnectionHighlighting();
            CacheCurrentConversationGraphState();
        }

        private void CacheCurrentConversationGraphState()
        {
            if (SelectedConv == null || CurrentObjects.Count == 0 || graphEditor == null)
            {
                return;
            }

            conversationGraphCache[SelectedConv.UIndex] = CurrentObjects.ToList();
            conversationGraphViewStates[SelectedConv.UIndex] = (graphEditor.Camera.X, graphEditor.Camera.Y, graphEditor.Camera.ViewScale);
        }

        private void ClearConversationGraphCache()
        {
            conversationGraphCache.Clear();
            conversationGraphViewStates.Clear();
        }

        private void RemoveConversationGraphCache(IEnumerable<int> conversationUIndexes)
        {
            foreach (int conversationUIndex in conversationUIndexes.Distinct())
            {
                conversationGraphCache.Remove(conversationUIndex);
                conversationGraphViewStates.Remove(conversationUIndex);
            }
        }

        private bool TryRestoreConversationGraphFromCache()
        {
            if (SelectedConv == null || !conversationGraphCache.TryGetValue(SelectedConv.UIndex, out var cachedObjects) || cachedObjects.Count == 0)
            {
                return false;
            }

            foreach (var selectedObject in SelectedObjects)
            {
                selectedObject.IsSelected = false;
            }

            SelectedObjects.ClearEx();
            CurrentObjects.ClearEx();
            graphEditor.nodeLayer.RemoveAllChildren();
            graphEditor.edgeLayer.RemoveAllChildren();

            foreach (var graphObject in cachedObjects)
            {
                graphObject.IsSelected = false;
                CurrentObjects.Add(graphObject);
                graphEditor.addNode(graphObject);
            }

            foreach (var graphObject in CurrentObjects.OfType<DBox>())
            {
                graphObject.RemoveConnections();
            }

            foreach (var diagNode in CurrentObjects.OfType<DiagNode>())
            {
                diagNode.InputEdges.Clear();
            }

            foreach (var graphObject in CurrentObjects.OfType<DBox>())
            {
                graphObject.RecreateConnections(CurrentObjects);
            }

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }

            if (conversationGraphViewStates.TryGetValue(SelectedConv.UIndex, out var viewState))
            {
                graphEditor.Camera.ViewScale = viewState.ViewScale;
                graphEditor.Camera.X = viewState.X;
                graphEditor.Camera.Y = viewState.Y;
            }

            graphEditor.Enabled = true;
            graphEditor.UseWaitCursor = false;
            ApplySpeakerNodeHighlighting();
            UpdateSelectedConnectionHighlighting();
            graphEditor.Refresh();
            return true;
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
        public void Layout(bool useTransientSavedPositions = false)
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
                    if (SavedPositions.Any() && (SaveViewMode != ESaveViewMode.AutoGenerate || useTransientSavedPositions))
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

        public IDisposable SuppressPackageUpdates()
        {
            suppressedPackageUpdateDepth++;
            return new SuppressPackageUpdatesScope(this);
        }

        public IDisposable SuppressPackageUpdatesAndDeferLocalUpdateReset()
        {
            IsLocalUpdate = true;
            return SuppressPackageUpdates();
        }

        private void ReleaseSuppressedPackageUpdates()
        {
            if (suppressedPackageUpdateDepth > 0)
            {
                suppressedPackageUpdateDepth--;
            }
        }

        private sealed class SuppressPackageUpdatesScope : IDisposable
        {
            private DialogueEditorWindow owner;

            public SuppressPackageUpdatesScope(DialogueEditorWindow owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                owner?.ReleaseSuppressedPackageUpdates();
                owner = null;
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

                EnsureLayoutHasNoOverlaps();

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
            var queuedBranchNodeIds = new HashSet<int>();
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
                                    for (int i = 0; i < thisNode.Links.Count; i++)
                                    {
                                        var targetNode = allNodes.FirstOrDefault(x => x.NodeUID == thisNode.Links[i].Index + r);
                                        if (targetNode == null || visitedNodes.Contains(targetNode.NodeUID) || queuedBranchNodeIds.Contains(targetNode.NodeUID))
                                        {
                                            continue;
                                        }

                                        if (nextNode == null)
                                        {
                                            nextNode = targetNode;
                                            continue;
                                        }

                                        BranchQueue.Enqueue(targetNode);
                                        queuedBranchNodeIds.Add(targetNode.NodeUID);
                                    }
                                }
                            }
                            else if (!BranchQueue.IsEmpty())//REACHED END OF BRANCH PULL nextNode from STACK
                            {
                                nextNode = BranchQueue.Dequeue();
                                queuedBranchNodeIds.Remove(nextNode.NodeUID);
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

            EnsureLayoutHasNoOverlaps();

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

            const float COLUMN_GAP = 24f;
            const float ROW_GAP = 12f;

            var visitedNodes = new HashSet<int>();
            List<DStart> startNodes = CurrentObjects.OfType<DStart>().OrderBy(n => n.Order).ToList();
            var allNodes = CurrentObjects.OfType<DiagNode>().OrderBy(n => n.NodeUID).ToDictionary(n => n.NodeUID);
            var queuedBranchNodeIds = new HashSet<int>();

            // Pass 1: Assign logical (column, row) to every node via graph traversal.
            // Primary outgoing link continues on the same row; secondary links get new rows.
            var nodeLogicalPos = new Dictionary<int, (int col, int row)>();
            var startLogicalPos = new Dictionary<int, (int col, int row)>();
            int maxUsedRow = -1;
            int nextFreeRow = 0;

            static int GetTargetUid(DiagNode src, ReplyChoiceNode link) =>
                src.Node.IsReply ? link.Index : link.Index + 1000;

            foreach (var startNode in startNodes)
            {
                int startRow = Math.Max(nextFreeRow, maxUsedRow + 1);
                startLogicalPos[startNode.Order] = (0, startRow);
                maxUsedRow = Math.Max(maxUsedRow, startRow);
                visitedNodes.Add(startNode.NodeUID);

                if (!allNodes.TryGetValue(startNode.StartNumber, out var nextNode)
                    || visitedNodes.Contains(nextNode.NodeUID))
                {
                    nextFreeRow = maxUsedRow + 1;
                    continue;
                }

                var branchStack = new Stack<(DiagNode node, int col, int row)>();
                int nextBranchRow = startRow;
                int colAt = 1;
                int rowAt = startRow;

                while (nextNode != null || branchStack.Count > 0)
                {
                    if (nextNode == null)
                    {
                        while (branchStack.Count > 0)
                        {
                            var b = branchStack.Pop();
                            queuedBranchNodeIds.Remove(b.node.NodeUID);
                            if (!visitedNodes.Contains(b.node.NodeUID))
                            {
                                nextNode = b.node;
                                colAt = b.col;
                                rowAt = b.row;
                                break;
                            }
                        }
                        if (nextNode == null) break;
                    }

                    if (visitedNodes.Contains(nextNode.NodeUID))
                    {
                        nextNode = null;
                        continue;
                    }

                    var current = nextNode;
                    nodeLogicalPos[current.NodeUID] = (colAt, rowAt);
                    maxUsedRow = Math.Max(maxUsedRow, rowAt);
                    visitedNodes.Add(current.NodeUID);

                    int nextCol = colAt + 1;
                    nextNode = null;

                    var deferredBranches = new List<(DiagNode node, int col, int row)>();
                    foreach (var link in current.Links)
                    {
                        int uid = GetTargetUid(current, link);
                        if (!allNodes.TryGetValue(uid, out var target)
                            || visitedNodes.Contains(target.NodeUID)
                            || queuedBranchNodeIds.Contains(target.NodeUID))
                        {
                            continue;
                        }

                        if (nextNode == null)
                        {
                            nextNode = target;
                            continue;
                        }

                        nextBranchRow = Math.Max(nextBranchRow + 1, maxUsedRow + 1);
                        deferredBranches.Add((target, nextCol, nextBranchRow));
                    }

                    for (int i = deferredBranches.Count - 1; i >= 0; i--)
                    {
                        var deferredBranch = deferredBranches[i];
                        if (queuedBranchNodeIds.Add(deferredBranch.node.NodeUID))
                        {
                            branchStack.Push(deferredBranch);
                        }
                    }

                    colAt = nextCol;
                }

                nextFreeRow = maxUsedRow + 1;
            }

            // Orphan nodes
            int orphanRow = Math.Max(nextFreeRow, maxUsedRow + 1);
            foreach (var node in allNodes.Values
                         .Where(n => !visitedNodes.Contains(n.NodeUID))
                         .OrderBy(n => n.NodeUID))
            {
                nodeLogicalPos[node.NodeUID] = (node.Node.IsReply ? 2 : 1, orphanRow);
                maxUsedRow = Math.Max(maxUsedRow, orphanRow);
                orphanRow++;
            }

            // Pass 2: Apply compact measured spacing so rows stay horizontal without overlap.
            var columnWidths = new Dictionary<int, float>();
            var rowHeights = new Dictionary<int, float>();

            foreach (var startNode in startNodes)
            {
                if (!startLogicalPos.TryGetValue(startNode.Order, out var sp))
                    continue;

                float width = startNode.Bounds.Width;
                float height = startNode.Bounds.Height;
                if (!columnWidths.TryGetValue(sp.col, out float currentColumnWidth) || width > currentColumnWidth)
                    columnWidths[sp.col] = width;
                if (!rowHeights.TryGetValue(sp.row, out float currentRowHeight) || height > currentRowHeight)
                    rowHeights[sp.row] = height;
            }

            foreach (var (uid, (col, row)) in nodeLogicalPos)
            {
                if (!allNodes.TryGetValue(uid, out var node))
                    continue;

                float width = node.Bounds.Width;
                float height = node.Bounds.Height;
                if (!columnWidths.TryGetValue(col, out float currentColumnWidth) || width > currentColumnWidth)
                    columnWidths[col] = width;
                if (!rowHeights.TryGetValue(row, out float currentRowHeight) || height > currentRowHeight)
                    rowHeights[row] = height;
            }

            int maxCol = Math.Max(columnWidths.Keys.DefaultIfEmpty(0).Max(), startLogicalPos.Values.DefaultIfEmpty().Max(p => p.col));
            int maxRow = Math.Max(rowHeights.Keys.DefaultIfEmpty(0).Max(), Math.Max(startLogicalPos.Values.DefaultIfEmpty().Max(p => p.row), nodeLogicalPos.Values.DefaultIfEmpty().Max(p => p.row)));

            var columnX = new float[maxCol + 1];
            for (int col = 1; col <= maxCol; col++)
            {
                float previousWidth = columnWidths.TryGetValue(col - 1, out float w) ? w : 160f;
                columnX[col] = columnX[col - 1] + previousWidth + COLUMN_GAP;
            }

            var rowY = new float[maxRow + 1];
            for (int row = 1; row <= maxRow; row++)
            {
                float previousHeight = rowHeights.TryGetValue(row - 1, out float h) ? h : 100f;
                rowY[row] = rowY[row - 1] + previousHeight + ROW_GAP;
            }

            foreach (var startNode in startNodes)
            {
                if (startLogicalPos.TryGetValue(startNode.Order, out var sp))
                    startNode.SetOffset(columnX[sp.col], rowY[sp.row]);
            }

            foreach (var (uid, (col, row)) in nodeLogicalPos)
            {
                if (!allNodes.TryGetValue(uid, out var node)) continue;
                node.SetOffset(columnX[col], rowY[row]);
            }

            // Do NOT call EnsureLayoutHasNoOverlaps here: that function only pushes nodes
            // downward and would collapse the intentional horizontal waterfall rows.

            foreach (DiagEdEdge edge in graphEditor.edgeLayer)
            {
                ConvGraphEditor.UpdateEdge(edge);
            }
        }

        private void EnsureLayoutHasNoOverlaps()
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

        private void RefreshViewCore(bool preserveLayout)
        {
            if (SelectedConv != null)
            {
                Properties_InterpreterWPF.LoadExport(CurrentLoadedExport);
                if (SelectedDialogueNode != null)
                {
                    RefreshExportLoaders();
                }

                GenerateGraph(preserveLayout);
                if (SaveViewMode != ESaveViewMode.AutoGenerate)
                {
                    saveView(false);
                }
            }
        }

        public void RefreshView()
        {
            RefreshViewCore(false);
        }

        private void RefreshViewPreserveLayout()
        {
            RefreshViewCore(true);
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

        private void ClearGraphSelection()
        {
            foreach (var oldselection in SelectedObjects)
            {
                oldselection.IsSelected = false;
            }

            SelectedObjects.ClearEx();
            Start_ListBox.SelectedIndex = -1;

            if (SelectedDialogueNode != null)
            {
                SelectedDialogueNode.PropertyChanged -= NodePropertyChanged;
            }

            SelectedDialogueNode = null;
            MirrorDialogueNode = null;

            EndInlineLineStrRefEdit(false);
            EndInlinePlotFieldEdit(false);
            ClearInlineLinkEditor();
            ClearInterpDataTree();
            InterpData_InterpreterWPF.UnloadExport();
            SoundpanelWPF_F.UnloadExport();
            SoundpanelWPF_M.UnloadExport();
            FaceFXAnimSetEditorControl_F.UnloadExport();
            FaceFXAnimSetEditorControl_M.UnloadExport();
            SoundpanelFemaleControl.Visibility = Visibility.Hidden;
            SoundpanelMaleControl.Visibility = Visibility.Hidden;

            SetUIMode(0, true);
            UpdateSelectedConnectionHighlighting();
            graphEditor?.Refresh();
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

        public void RefreshSelectedNodeAfterInterpMutation(int? preferredInterpSelectionUIndex = null)
        {
            if (SelectedDialogueNode == null)
            {
                return;
            }

            if (SelectedDialogueNode.InterpData?.GetProperty<FloatProperty>("InterpLength") is FloatProperty lengthprop)
            {
                SelectedDialogueNode.InterpLength = lengthprop.Value;
            }

            if (CurrentLoadedExport != null)
            {
                Properties_InterpreterWPF.LoadExport(CurrentLoadedExport);
            }

            RefreshInterpDataTreePreserveState(preferredInterpSelectionUIndex ?? GetSelectedInterpDataTreeExport()?.UIndex ?? SelectedDialogueNode.InterpData?.UIndex);
            LoadInlineLinkEditor(SelectedObjects.FirstOrDefault() as DiagNode);
        }

        private void BuildInterpDataTree(bool selectRoot = true)
        {
            ClearInterpDataTree();

            if (SelectedDialogueNode?.InterpData is not ExportEntry interpDataExport || Pcc == null)
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

            suppressInterpDataInterpreterUnloadDepth++;
            try
            {
                BuildInterpDataTree(selectRoot: false);

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
            bool isGestureTrack = selectedExport?.ClassName is "BioEvtSysTrackGesture" or "SFXModule_Gestures" or "SFXSkeletalMeshActor";

            SetContextMenuItemVisibility(menu, "ShiftInterpTrackMovesInInterpData", isInterpData ? Visibility.Visible : Visibility.Collapsed);
            SetContextMenuItemVisibility(menu, "ShiftSelectedInterpTrackMove", isInterpTrackMove ? Visibility.Visible : Visibility.Collapsed);
            SetContextMenuItemVisibility(menu, "OpenGestureAnimationImporter", isGestureTrack ? Visibility.Visible : Visibility.Collapsed);
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

        private static IEntry GetTopLevelEntry(IEntry entry)
        {
            if (entry == null)
            {
                return null;
            }

            while (entry.HasParent)
            {
                entry = entry.Parent;
            }

            return entry;
        }

        private IEntry GetTopLevelConversationEntry(ConversationExtended conversation)
        {
            return GetTopLevelEntry(conversation?.Export);
        }

        private static bool IsEntryDescendantOrSame(IEntry entry, IEntry ancestor)
        {
            if (entry == null || ancestor == null)
            {
                return false;
            }

            IEntry current = entry;
            while (current != null)
            {
                if (current == ancestor)
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private List<ExportEntry> GetBioConversationsUnderEntry(IEntry rootEntry)
        {
            return Pcc?.Exports
                .Where(exp => exp.ClassName == "BioConversation" && IsEntryDescendantOrSame(exp, rootEntry))
                .ToList() ?? [];
        }

        private PortingOptions GetConversationPortingOptions(IEntry sourceEntry, IEntry targetEntry)
        {
            var treeMergeDialog = new TreeMergeDialog(sourceEntry, targetEntry, Pcc.Game)
            {
                Owner = this
            };

            treeMergeDialog.ShowDialog();
            treeMergeDialog.PortingOption.PortUsingDonors = treeMergeDialog.PortUsingDonors;
            treeMergeDialog.PortingOption.PortGlobalsAsImports = treeMergeDialog.PortGlobalsAsImports;
            treeMergeDialog.PortingOption.PortExportsAsImportsWhenPossible = treeMergeDialog.PortExportsAsImportsWhenPossible;
            treeMergeDialog.PortingOption.PortExportsMemorySafe = treeMergeDialog.PortExportsMemorySafe;
            return treeMergeDialog.PortingOption;
        }

        private int? GetPreferredConversationSelectionUIndex(IEntry rootEntry, int? fallbackConversationUIndex = null)
        {
            var importedConversation = GetBioConversationsUnderEntry(rootEntry).FirstOrDefault();
            return importedConversation?.UIndex ?? fallbackConversationUIndex;
        }

        private static List<IEntry> GetEntryTree(IEntry rootEntry)
        {
            if (rootEntry == null)
            {
                return [];
            }

            return [rootEntry, .. rootEntry.GetAllDescendants()];
        }

        private static string GetRelativeEntryPath(IEntry rootEntry, IEntry entry)
        {
            if (rootEntry == null || entry == null)
            {
                return null;
            }

            if (ReferenceEquals(rootEntry, entry))
            {
                return string.Empty;
            }

            string rootPath = rootEntry.InstancedFullPath;
            string entryPath = entry.InstancedFullPath;
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(entryPath))
            {
                return entryPath;
            }

            return entryPath.StartsWith(rootPath + ".", StringComparison.OrdinalIgnoreCase)
                ? entryPath[(rootPath.Length + 1)..]
                : entryPath;
        }

        private static Dictionary<string, IEntry> BuildRelativeEntryPathMap(IEntry rootEntry)
        {
            return GetEntryTree(rootEntry)
                .GroupBy(entry => GetRelativeEntryPath(rootEntry, entry), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static List<(IEntry Entry, string RelativePath, string LookupKey)> BuildReplacementSnapshot(IEntry rootEntry)
        {
            return GetEntryTree(rootEntry)
                .Select(entry =>
                    (
                        Entry: entry,
                        RelativePath: GetRelativeEntryPath(rootEntry, entry),
                        LookupKey: GetEntryReplacementLookupKey(entry)
                    ))
                .ToList();
        }

        private static int? TryGetWwiseEventStrRef(IEntry entry)
        {
            if (entry?.ClassName != "WwiseEvent")
            {
                return null;
            }

            string objectName = entry.ObjectName.Name;
            if (!objectName.StartsWith("VO_", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string parsing = objectName[3..];
            int nextUnderscore = parsing.IndexOf('_');
            if (nextUnderscore <= 0)
            {
                return null;
            }

            return int.TryParse(parsing[..nextUnderscore], out int parsedInt) ? parsedInt : null;
        }

        private static int? TryGetWwiseStreamStrRef(IEntry entry)
        {
            if (entry?.ClassName != "WwiseStream")
            {
                return null;
            }

            string[] splits = entry.ObjectName.Name.Split('_', ',');
            for (int i = splits.Length - 1; i >= 0; i--)
            {
                if (int.TryParse(splits[i], out int parsedInt))
                {
                    return parsedInt;
                }
            }

            return null;
        }

        private static string GetAudioGenderToken(IEntry entry)
        {
            string objectName = entry?.ObjectName.Name ?? string.Empty;
            if (objectName.Contains("_f_", StringComparison.OrdinalIgnoreCase))
            {
                return "f";
            }

            if (objectName.Contains("_m_", StringComparison.OrdinalIgnoreCase))
            {
                return "m";
            }

            return string.Empty;
        }

        private static string NormalizeReplacementLookupText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            text = RemoveWrappingQuotes(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string GetEntryReplacementLookupKey(IEntry entry)
        {
            if (entry?.FileRef == null)
            {
                return null;
            }

            int? strRef = entry.ClassName switch
            {
                "WwiseEvent" => TryGetWwiseEventStrRef(entry),
                "WwiseStream" => TryGetWwiseStreamStrRef(entry),
                _ => null
            };

            if (!strRef.HasValue)
            {
                return null;
            }

            string tlkText = NormalizeReplacementLookupText(GlobalFindStrRefbyID(strRef.Value, entry.FileRef));
            if (string.IsNullOrWhiteSpace(tlkText) || tlkText == "No Data")
            {
                tlkText = strRef.Value.ToString();
            }

            return $"{entry.ClassName}|{GetAudioGenderToken(entry)}|{tlkText}";
        }

        private static Dictionary<string, IEntry> BuildReplacementLookupKeyMap(IEntry rootEntry)
        {
            return GetEntryTree(rootEntry)
                .Select(entry => (entry, key: GetEntryReplacementLookupKey(entry)))
                .Where(item => !string.IsNullOrWhiteSpace(item.key))
                .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().entry, StringComparer.OrdinalIgnoreCase);
        }

        private static ListenableDictionary<IEntry, IEntry> BuildReplacementRelinkMap(List<(IEntry Entry, string RelativePath, string LookupKey)> oldEntrySnapshot, IEntry sourceRootEntry, RelinkerOptionsPackage rop)
        {
            var relinkMap = new ListenableDictionary<IEntry, IEntry>();
            if (oldEntrySnapshot == null || sourceRootEntry == null || rop?.CrossPackageMap == null)
            {
                return relinkMap;
            }

            var sourceEntriesByRelativePath = BuildRelativeEntryPathMap(sourceRootEntry);
            var sourceEntriesByLookupKey = BuildReplacementLookupKeyMap(sourceRootEntry);

            foreach (var snapshot in oldEntrySnapshot)
            {
                IEntry oldEntry = snapshot.Entry;
                IEntry sourceEntry = null;

                if (!string.IsNullOrWhiteSpace(snapshot.RelativePath))
                {
                    sourceEntriesByRelativePath.TryGetValue(snapshot.RelativePath, out sourceEntry);
                }

                if (sourceEntry == null)
                {
                    string lookupKey = snapshot.LookupKey;
                    if (!string.IsNullOrWhiteSpace(lookupKey))
                    {
                        sourceEntriesByLookupKey.TryGetValue(lookupKey, out sourceEntry);
                    }
                }

                if (sourceEntry == null
                    || !rop.CrossPackageMap.TryGetValue(sourceEntry, out IEntry newEntry)
                    || newEntry == null
                    || ReferenceEquals(oldEntry, newEntry))
                {
                    continue;
                }

                relinkMap[oldEntry] = newEntry;
            }

            return relinkMap;
        }

        private void RefreshConversationsAfterStructureChange(int? preferredConversationUIndex = null)
        {
            int? targetConversationUIndex = preferredConversationUIndex ?? SelectedConv?.UIndex;

            LoadConversations();
            FirstParse();

            if (targetConversationUIndex.HasValue)
            {
                var conversation = Conversations.FirstOrDefault(c => c.UIndex == targetConversationUIndex.Value);
                if (conversation != null)
                {
                    Conversations_ListBox.SelectedItem = conversation;
                    return;
                }
            }

            if (Conversations.Count > 0)
            {
                Conversations_ListBox.SelectedIndex = 0;
            }
            else
            {
                UnloadFile();
            }
        }

        private void ImportConversationTopLevelPackage(ConversationExtended sourceConversation, ConversationExtended targetConversation)
        {
            if (sourceConversation?.Export?.FileRef == null || Pcc == null)
            {
                return;
            }

            IEntry sourceEntry = GetTopLevelConversationEntry(sourceConversation);
            IEntry targetEntry = GetTopLevelConversationEntry(targetConversation);
            if (sourceEntry == null)
            {
                return;
            }

            if (sourceEntry.FileRef == Pcc)
            {
                return;
            }

            if (targetEntry == sourceEntry)
            {
                return;
            }

            if (sourceEntry.Game.IsLEGame() != Pcc.Game.IsLEGame() && !App.IsDebug && sourceEntry.Game != MEGame.UDK)
            {
                MessageBox.Show(
                    "Cannot port assets between Original Trilogy (OT) games and Legendary Edition (LE) games in release builds of Legendary Explorer.",
                    "Cannot port asset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var portingOption = GetConversationPortingOptions(sourceEntry, targetEntry);
            if (portingOption.PortingOptionChosen == EntryImporter.PortingOption.Cancel)
            {
                return;
            }

            int originalIndex = -1;
            bool hadChanges = false;
            bool hadHeaderChanges = false;
            if (portingOption.PortingOptionChosen != EntryImporter.PortingOption.ReplaceSingular
                && portingOption.PortingOptionChosen != EntryImporter.PortingOption.ReplaceSingularWithRelink
                && Pcc.FindEntry(sourceEntry.InstancedFullPath) != null)
            {
                originalIndex = sourceEntry.indexValue;
                hadChanges = sourceEntry.EntryHasPendingChanges;
                hadHeaderChanges = sourceEntry.HeaderChanged;
                sourceEntry.indexValue = Pcc.GetNextIndexedName(sourceEntry.ObjectName).Number;
            }

            string objectDBPath = AppDirectories.GetObjectDatabasePath(Pcc.Game);
            bool shouldUseDonors = portingOption.PortUsingDonors && sourceEntry.Game != Pcc.Game && sourceEntry.Game != MEGame.UDK;
            ObjectInstanceDB objectDB = null;
            if (shouldUseDonors)
            {
                if (File.Exists(objectDBPath))
                {
                    using FileStream fs = File.OpenRead(objectDBPath);
                    objectDB = ObjectInstanceDB.Deserialize(Pcc.Game, fs);
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
                IsCrossGame = sourceEntry.Game != Pcc.Game && sourceEntry.Game != MEGame.UDK,
                Cache = new PackageCache(),
                TargetGameDonorDB = objectDB,
                ImportExportDependencies = portingOption.PortingOptionChosen is EntryImporter.PortingOption.CloneAllDependencies
                    or EntryImporter.PortingOption.ReplaceSingularWithRelink,
                GenerateImportsForGlobalFiles = portingOption.PortGlobalsAsImports,
                PortImportsMemorySafe = portingOption.PortExportsMemorySafe,
                PortExportsAsImportsWhenPossible = portingOption.PortExportsAsImportsWhenPossible,
            };

            var relinkResults = EntryImporter.ImportAndRelinkEntries(portingOption.PortingOptionChosen, sourceEntry, Pcc,
                targetEntry, true, rop, out IEntry newEntry);

            if (originalIndex >= 0)
            {
                sourceEntry.indexValue = originalIndex;
                sourceEntry.HeaderChanged = hadHeaderChanges;
                sourceEntry.EntryHasPendingChanges = hadChanges;
            }

            RefreshConversationsAfterStructureChange(GetPreferredConversationSelectionUIndex(newEntry, targetConversation?.UIndex));

            if ((relinkResults?.Count ?? 0) > 0)
            {
                new ListDialog(relinkResults, "Relink report",
                    "The following items reported relinking issues.", this).Show();
            }
        }

        private bool TopLevelEntryHasChildren(IEntry entry)
        {
            return entry != null
                   && (Pcc.Exports.Any(x => x.idxLink == entry.UIndex)
                       || Pcc.Imports.Any(x => x.idxLink == entry.UIndex));
        }

        private ExportEntry CloneTopLevelConversationPackage(ConversationExtended conversation)
        {
            if (GetTopLevelConversationEntry(conversation) is not ExportEntry export)
            {
                return null;
            }

            return TopLevelEntryHasChildren(export)
                ? EntryCloner.CloneTree(export)
                : EntryCloner.CloneEntry(export);
        }

        private sealed record ConversationReplacementCandidate(string FilePath, int UIndex, string TopLevelEntryPath)
        {
            public override string ToString()
            {
                return $"{Path.GetFileName(FilePath)} - #{UIndex} {TopLevelEntryPath}";
            }
        }

        private List<ConversationReplacementCandidate> GetReplacementConversationCandidates(ConversationExtended conversation)
        {
            if (conversation?.Export == null || Pcc == null)
            {
                return [];
            }

            string currentDirectory = Path.GetDirectoryName(Pcc.FilePath);
            if (string.IsNullOrWhiteSpace(currentDirectory) || !Directory.Exists(currentDirectory))
            {
                return [];
            }

            string selectedConversationName = conversation.Export.ObjectName.Instanced;
            var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".pcc",
                ".upk",
                ".u",
                ".sfm"
            };

            var candidates = new List<ConversationReplacementCandidate>();
            foreach (string filePath in Directory.EnumerateFiles(currentDirectory)
                         .Where(path => !path.Equals(Pcc.FilePath, StringComparison.OrdinalIgnoreCase)
                                        && supportedExtensions.Contains(Path.GetExtension(path)))
                         .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using IMEPackage package = MEPackageHandler.OpenMEPackage(filePath, forceLoadFromDisk: true);
                    if (package.Game != Pcc.Game)
                    {
                        continue;
                    }

                    foreach (var export in package.Exports.Where(exp => exp.ClassName == "BioConversation"
                                                                         && string.Equals(exp.ObjectName.Instanced, selectedConversationName, StringComparison.OrdinalIgnoreCase)))
                    {
                        IEntry topLevelEntry = GetTopLevelEntry(export);
                        candidates.Add(new ConversationReplacementCandidate(filePath, export.UIndex, topLevelEntry?.InstancedFullPath ?? export.InstancedFullPath));
                    }
                }
                catch (Exception e) when (!App.IsDebug)
                {
                    Debug.WriteLine($"Failed to scan package '{filePath}' for replacement conversations: {e.Message}");
                }
            }

            return candidates;
        }

        private ConversationReplacementCandidate PickReplacementConversationCandidate(ConversationExtended conversation, List<ConversationReplacementCandidate> candidates)
        {
            if (conversation?.Export == null || Pcc == null || candidates == null || candidates.Count == 0)
            {
                return null;
            }

            return EntrySelector.GetItem(
                this,
                candidates
                    .OrderBy(candidate => Path.GetFileName(candidate.FilePath), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.TopLevelEntryPath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                "Choose the same-named BioConversation to import.",
                candidates[0],
                searchHelpText: "Search by package file name or top-level path");
        }

        private static HashSet<int> GetReferencedObjectUIndexes(IEntry entry, bool includeStructuralReferences = true)
        {
            var references = new HashSet<int>();
            if (entry?.FileRef == null)
            {
                return references;
            }

            IMEPackage package = entry.FileRef;
            MEGame game = package.Game;

            void AddReference(int uIndex)
            {
                if (uIndex != 0 && uIndex != entry.UIndex && package.IsEntry(uIndex))
                {
                    references.Add(uIndex);
                }
            }

            static void AddPropertyReferences(PropertyCollection props, ExportEntry exp, Action<int> addReference)
            {
                if (props == null)
                {
                    return;
                }

                foreach (Property prop in props)
                {
                    switch (prop)
                    {
                        case ObjectProperty objectProperty:
                            addReference(objectProperty.Value);
                            break;
                        case DelegateProperty delegateProperty:
                            addReference(delegateProperty.Value.ContainingObjectUIndex);
                            break;
                        case StructProperty structProperty:
                            AddPropertyReferences(structProperty.Properties, exp, addReference);
                            break;
                        case ArrayProperty<ObjectProperty> objectArray:
                            foreach (ObjectProperty objectProp in objectArray)
                            {
                                addReference(objectProp.Value);
                            }
                            break;
                        case ArrayProperty<StructProperty> structArray:
                            foreach (StructProperty structProp in structArray)
                            {
                                AddPropertyReferences(structProp.Properties, exp, addReference);
                            }
                            break;
                    }
                }
            }

            switch (entry)
            {
                case ImportEntry:
                    if (includeStructuralReferences)
                    {
                        AddReference(entry.idxLink);
                    }
                    break;
                case ExportEntry exp:
                    try
                    {
                        if (includeStructuralReferences)
                        {
                            AddReference(exp.idxLink);
                            AddReference(exp.idxArchetype);
                            AddReference(exp.idxClass);
                            AddReference(exp.idxSuperClass);
                        }

                        if (exp.HasComponentMap)
                        {
                            foreach ((_, int value) in exp.ComponentMap)
                            {
                                AddReference(value);
                            }
                        }

                        if (includeStructuralReferences && !exp.HasStack && exp.TemplateOwnerClassIdx is >= 0)
                        {
                            AddReference(exp.TemplateOwnerClassIdx);
                        }

                        AddPropertyReferences(exp.GetProperties(), exp, AddReference);

                        if (!exp.IsDefaultObject
                            && exp.ClassName != "AnimSequence"
                            && ObjectBinary.From(exp) is ObjectBinary objBin)
                        {
                            objBin.ForEachUIndex(game, new ReferencedObjectCollector(exp, references));
                        }
                    }
                    catch
                    {
                    }
                    break;
            }

            return references;
        }

        private readonly struct ReferencedObjectCollector(IEntry entry, HashSet<int> references) : IUIndexAction
        {
            public void Invoke(ref int uIndex, string propName)
            {
                if (uIndex != 0 && uIndex != entry.UIndex && entry.FileRef.IsEntry(uIndex))
                {
                    references.Add(uIndex);
                }
            }
        }

        private List<IEntry> GetExternalReferencedEntries(IEntry topLevelEntry)
        {
            if (topLevelEntry?.FileRef == null)
            {
                return [];
            }

            IMEPackage package = topLevelEntry.FileRef;
            HashSet<int> topLevelTreeUIndexes = [topLevelEntry.UIndex, .. topLevelEntry.GetAllDescendants().Select(x => x.UIndex).Where(x => x > 0)];
            HashSet<int> visitedUIndexes = [];
            HashSet<IEntry> externalEntries = [];
            Stack<IEntry> entriesToProcess = new([topLevelEntry, .. topLevelEntry.GetAllDescendants()]);

            while (entriesToProcess.Count > 0)
            {
                IEntry currentEntry = entriesToProcess.Pop();
                if (currentEntry == null || !visitedUIndexes.Add(currentEntry.UIndex))
                {
                    continue;
                }

                foreach (int referencedUIndex in GetReferencedObjectUIndexes(currentEntry, includeStructuralReferences: false))
                {
                    if (!package.TryGetEntry(referencedUIndex, out IEntry referencedEntry)
                        || topLevelTreeUIndexes.Contains(referencedEntry.UIndex)
                        || referencedEntry.IsTrash())
                    {
                        continue;
                    }

                    if (externalEntries.Add(referencedEntry))
                    {
                        entriesToProcess.Push(referencedEntry);
                    }
                }
            }

            return externalEntries
                .OrderBy(entry => entry.InstancedFullPath.Count(c => c == '.'))
                .ThenBy(entry => entry.InstancedFullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<IEntry> GetConversationEntriesToTrash(ConversationExtended conversation, bool includeExternalReferencedPackages = false)
        {
            IEntry topLevelEntry = GetTopLevelConversationEntry(conversation);
            if (topLevelEntry == null)
            {
                return [];
            }

            HashSet<IEntry> itemsToTrash = [];

            void AddEntryAndDescendants(IEntry entry)
            {
                if (entry == null || !ReferenceEquals(entry.FileRef, Pcc))
                {
                    return;
                }

                itemsToTrash.Add(entry);
                foreach (var descendant in entry.GetAllDescendants())
                {
                    itemsToTrash.Add(descendant);
                }
            }

            AddEntryAndDescendants(topLevelEntry);

            if (!includeExternalReferencedPackages)
            {
                return itemsToTrash.ToList();
            }

            foreach (IEntry referencedEntry in GetExternalReferencedEntries(topLevelEntry))
            {
                itemsToTrash.Add(referencedEntry);
            }

            return itemsToTrash.ToList();
        }

        private bool TrashTopLevelConversationPackage(ConversationExtended conversation, bool confirm = true, bool refreshAfter = true, bool includeExternalReferencedPackages = false)
        {
            IEntry topLevelEntry = GetTopLevelConversationEntry(conversation);
            if (topLevelEntry == null)
            {
                return false;
            }

            if (confirm && MessageBox.Show(
                    includeExternalReferencedPackages
                        ? $"Trash top-level package '{topLevelEntry.InstancedFullPath}', all of its children, and all externally referenced packages used by this conversation?"
                        : $"Trash top-level package '{topLevelEntry.InstancedFullPath}' and all of its children?",
                    "Confirm trash",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) is not MessageBoxResult.Yes)
            {
                return false;
            }

            var itemsToTrash = GetConversationEntriesToTrash(conversation, includeExternalReferencedPackages);
            if (itemsToTrash.Count == 0)
            {
                return false;
            }

            EntryPruner.TrashEntries(Pcc, itemsToTrash);
            if (refreshAfter)
            {
                RefreshConversationsAfterStructureChange();
            }

            return true;
        }

        private IEntry CloneTopLevelConversationPackageFromSource(IEntry sourceEntry)
        {
            if (sourceEntry?.FileRef == null || Pcc == null)
            {
                return null;
            }

            if (sourceEntry.Game.IsLEGame() != Pcc.Game.IsLEGame() && !App.IsDebug && sourceEntry.Game != MEGame.UDK)
            {
                MessageBox.Show(
                    "Cannot port assets between Original Trilogy (OT) games and Legendary Edition (LE) games in release builds of Legendary Explorer.",
                    "Cannot port asset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return null;
            }

            var rop = new RelinkerOptionsPackage
            {
                IsCrossGame = sourceEntry.Game != Pcc.Game && sourceEntry.Game != MEGame.UDK,
                Cache = new PackageCache(),
                ImportExportDependencies = true,
                GenerateImportsForGlobalFiles = true,
                PortImportsMemorySafe = true,
                PortExportsAsImportsWhenPossible = true,
            };

            var relinkResults = EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, sourceEntry, Pcc,
                null, true, rop, out IEntry newEntry);

            if ((relinkResults?.Count ?? 0) > 0)
            {
                new ListDialog(relinkResults, "Relink report",
                    "The following items reported relinking issues.", this).Show();
            }

            return newEntry;
        }

        private void ReplaceTopLevelConversationPackage(ConversationExtended targetConversation, ConversationReplacementCandidate replacementCandidate)
        {
            if (targetConversation?.Export == null || replacementCandidate == null || Pcc == null)
            {
                return;
            }

            IEntry newEntry = null;
            IEntry targetTopLevelEntry = GetTopLevelConversationEntry(targetConversation);
            var targetReplacementSnapshot = BuildReplacementSnapshot(targetTopLevelEntry);
            try
            {
                using IMEPackage sourcePackage = MEPackageHandler.OpenMEPackage(replacementCandidate.FilePath, forceLoadFromDisk: true);
                if (sourcePackage.GetEntry(replacementCandidate.UIndex) is not ExportEntry sourceConversationExport
                    || sourceConversationExport.ClassName != "BioConversation")
                {
                    MessageBox.Show("The selected replacement conversation could not be loaded.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                IEntry sourceTopLevelEntry = GetTopLevelEntry(sourceConversationExport);
                if (sourceTopLevelEntry == null)
                {
                    return;
                }

                if (targetTopLevelEntry == null)
                {
                    return;
                }

                var relinkOptions = new RelinkerOptionsPackage
                {
                    IsCrossGame = sourceTopLevelEntry.Game != Pcc.Game && sourceTopLevelEntry.Game != MEGame.UDK,
                    Cache = new PackageCache(),
                    ImportExportDependencies = true,
                    GenerateImportsForGlobalFiles = true,
                    PortImportsMemorySafe = true,
                    PortExportsAsImportsWhenPossible = true,
                };

                if (!TrashTopLevelConversationPackage(targetConversation, confirm: false, refreshAfter: false))
                {
                    return;
                }

                EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, sourceTopLevelEntry, Pcc,
                    null, true, relinkOptions, out newEntry);

                var replacementRelinkMap = BuildReplacementRelinkMap(targetReplacementSnapshot, sourceTopLevelEntry, relinkOptions);
                if (replacementRelinkMap.Count > 0)
                {
                    Relinker.RelinkSamePackage(Pcc, replacementRelinkMap);
                }

                if ((relinkOptions.RelinkReport?.Count ?? 0) > 0)
                {
                    new ListDialog(relinkOptions.RelinkReport, "Relink report",
                        "The following items reported relinking issues.", this).Show();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to replace conversation:\n{ex.Message}", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RefreshConversationsAfterStructureChange(GetPreferredConversationSelectionUIndex(newEntry));
            }
        }

        private void Conversations_ListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
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

        private void Conversations_CloneTopLevelPackage_Click(object sender, RoutedEventArgs e)
        {
            if (Conversations_ListBox.SelectedItem is not ConversationExtended conversation)
            {
                return;
            }

            var clonedEntry = CloneTopLevelConversationPackage(conversation);
            if (clonedEntry != null)
            {
                RefreshConversationsAfterStructureChange(GetPreferredConversationSelectionUIndex(clonedEntry));
            }
        }

        private void Conversations_MultiCloneTopLevelPackage_Click(object sender, RoutedEventArgs e)
        {
            if (Conversations_ListBox.SelectedItem is not ConversationExtended conversation)
            {
                return;
            }

            var result = PromptDialog.Prompt(this, "How many times do you want to clone this top-level package?", "Multiple package cloning", "2", true);
            if (!int.TryParse(result, out int count) || count <= 0)
            {
                return;
            }

            ExportEntry lastClone = null;
            for (int i = 0; i < count; i++)
            {
                lastClone = CloneTopLevelConversationPackage(conversation);
            }

            if (lastClone != null)
            {
                RefreshConversationsAfterStructureChange(GetPreferredConversationSelectionUIndex(lastClone));
            }
        }

        private void Conversations_ReplaceTopLevelPackage_Click(object sender, RoutedEventArgs e)
        {
            if (Conversations_ListBox.SelectedItem is not ConversationExtended conversation)
            {
                return;
            }

            var candidates = GetReplacementConversationCandidates(conversation);
            if (candidates.Count == 0)
            {
                MessageBox.Show($"No same-named BioConversation named '{conversation.Export.ObjectName.Instanced}' was found in another file in this folder.", "Dialogue Editor");
                return;
            }

            ConversationReplacementCandidate replacementCandidate = PickReplacementConversationCandidate(conversation, candidates);

            if (replacementCandidate == null)
            {
                return;
            }

            IEntry topLevelEntry = GetTopLevelConversationEntry(conversation);
            if (topLevelEntry == null)
            {
                return;
            }

            if (MessageBox.Show(
                    $"Replace top-level package '{topLevelEntry.InstancedFullPath}' with '{replacementCandidate.TopLevelEntryPath}' from '{Path.GetFileName(replacementCandidate.FilePath)}'?",
                    "Confirm replacement",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) is not MessageBoxResult.Yes)
            {
                return;
            }

            ReplaceTopLevelConversationPackage(conversation, replacementCandidate);
        }

        private void Conversations_TrashTopLevelPackage_Click(object sender, RoutedEventArgs e)
        {
            if (Conversations_ListBox.SelectedItem is not ConversationExtended conversation)
            {
                return;
            }

            TrashTopLevelConversationPackage(conversation);
        }

        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.Data is ConversationExtended sourceConversation)
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                dropInfo.Effects = sourceConversation.Export?.FileRef != null && sourceConversation.Export.FileRef != Pcc
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
                return;
            }

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
            if (dropInfo.Data is ConversationExtended sourceConversation)
            {
                ImportConversationTopLevelPackage(sourceConversation, dropInfo.TargetItem as ConversationExtended);
                return;
            }

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
                        AddToInterpList(sourceEntry);
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
                {
                    var referenceViewer = new ObjectReferenceViewerWindow(export, null);
                    ShowWindowAtFront(referenceViewer);
                    break;
                }
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
                case "OpenGestureAnimationImporter":
                    if (export.ClassName is "BioEvtSysTrackGesture" or "SFXModule_Gestures" or "SFXSkeletalMeshActor")
                    {
                        var dialog = new GestureAnimationImporterDialog(export, this);
                        dialog.ShowDialog();
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
            MatineeHelper.AddToParentInterpList(newEntry);
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

            StageDirections_Tab.Visibility = Pcc?.Game.IsGame3() == true ? Visibility.Visible : Visibility.Collapsed;

            Node_Panel.Visibility = Visibility.Collapsed;
            switch (CurrentUIMode)
            {
                case 1:
                    SelectBottomViewportTab("Speaker Details", ConversationDetailsTab);
                    break;
                case 2:
                    Node_Panel.Visibility = Visibility.Visible;
                    break;
                case 3:
                    BottomViewportTabControl.SelectedItem = StartingNodesTab;
                    break;
                default:
                    SelectBottomViewportTab("Speaker Details", ConversationDetailsTab);
                    break;

            }
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

            CacheCurrentConversationGraphState();

            bool shouldPanGraph = true;

            if (Conversations_ListBox.SelectedIndex < 0)
            {
                speakerNodeFilterSpeakerId = null;
                SelectedConv = null;
                SelectedDialogueNode = null;
                SelectedSpeakerList.ClearEx();
                RebuildSpeakerNodeFilterMenu();
                Properties_InterpreterWPF.UnloadExport();
                InterpData_InterpreterWPF.UnloadExport();
                InterpData_MetadataEditor.UnloadExport();
                SoundpanelWPF_F.UnloadExport();
                SoundpanelWPF_M.UnloadExport();
                FaceFXAnimSetEditorControl_F.UnloadExport();
                FaceFXAnimSetEditorControl_M.UnloadExport();
                ClearInterpDataTree();
                ClearInlineLinkEditor();
            }
            else
            {
                speakerNodeFilterSpeakerId = null;
                SelectedDialogueNode = null; //Before convos change make sure no properties fire.
                graphEditor.Enabled = false;
                graphEditor.UseWaitCursor = true;
                var nconv = Conversations[Conversations_ListBox.SelectedIndex];
                SelectedConv = new ConversationExtended(nconv);
                ApplyAssetDatabaseOwnerFriendlyName(SelectedConv);

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
                if (TryRestoreConversationGraphFromCache())
                {
                    Properties_InterpreterWPF.LoadExport(CurrentLoadedExport);
                    shouldPanGraph = false;
                }
                else
                {
                    RefreshView();
                }

                Start_ListBoxUpdate();

            }
            if (shouldPanGraph)
            {
                graphEditor_PanTo();
            }
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
                    if (SelectedSpeaker.StrRefID <= 0)
                    {
                        SelectedSpeaker.StrRefID = LookupTagRef(SelectedSpeaker.SpeakerName);
                        SelectedSpeaker.FriendlyName = GlobalFindStrRefbyID(SelectedSpeaker.StrRefID, Pcc);
                    }

                    TextBox_Speaker_Name.IsEnabled = SelectedSpeaker.SpeakerID >= 0;
                }
                else
                {
                    SelectedSpeaker = SelectedSpeakerList[0];
                }

                ApplySpeakerNodeHighlighting();
                graphEditor?.Refresh();
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
            SaveSpeakerChangesInPlace(rebuildGraphInPlace: true, reindexSpeakerIds: true);
        }
        private void PickSpeakerFaceFX(bool isMale)
        {
            if (!CanEditSelectedSpeakerFaceFX || SelectedConv == null || Pcc == null || SelectedSpeaker == null)
            {
                return;
            }

            var selectedFaceFx = EntrySelector.GetEntry<ExportEntry>(
                this,
                Pcc,
                $"Select the {(isMale ? "male" : "female")} FaceFX animation set for speaker '{SelectedSpeaker.SpeakerName}'.",
                exp => exp.ClassName == "FaceFXAnimSet",
                selectLastItemByDefault: true);

            var currentFaceFx = isMale ? SelectedSpeaker.FaceFX_Male : SelectedSpeaker.FaceFX_Female;
            if (selectedFaceFx == null || selectedFaceFx == currentFaceFx)
            {
                return;
            }

            int speakerIndex = SelectedSpeaker.SpeakerID + 2;
            if (speakerIndex < 0 || speakerIndex >= SelectedSpeakerList.Count)
            {
                return;
            }

            if (isMale)
            {
                SelectedSpeaker.FaceFX_Male = selectedFaceFx;
                SelectedSpeakerList[speakerIndex].FaceFX_Male = selectedFaceFx;
            }
            else
            {
                SelectedSpeaker.FaceFX_Female = selectedFaceFx;
                SelectedSpeakerList[speakerIndex].FaceFX_Female = selectedFaceFx;
            }

            SaveSpeakerChangesInPlace();
        }

        private void PickSpeakerFaceFXMale_Click(object sender, RoutedEventArgs e)
        {
            PickSpeakerFaceFX(true);
        }

        private void PickSpeakerFaceFXFemale_Click(object sender, RoutedEventArgs e)
        {
            PickSpeakerFaceFX(false);
        }

        private void SelectedSpeakerMaleFaceFXDisplay_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanEditSelectedSpeakerFaceFX)
            {
                PickSpeakerFaceFX(true);
            }
        }

        private void SelectedSpeakerFemaleFaceFXDisplay_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanEditSelectedSpeakerFaceFX)
            {
                PickSpeakerFaceFX(false);
            }
        }

        private bool CanImportSpeakerFaceFXAudio(object obj)
        {
            return SelectedSpeaker != null
                && Pcc != null
                && SelectedSpeaker.FaceFX_Male is ExportEntry faceFx
                && faceFx.ClassName == "FaceFXAnimSet";
        }

        private void ImportSpeakerFaceFXAudio(object obj)
        {
            if (!CanImportSpeakerFaceFXAudio(obj))
            {
                return;
            }

            if (SelectedSpeaker.FaceFX_Male is not ExportEntry faceFx)
            {
                return;
            }

            FaceFXAnimSetEditorControl_M.LoadExport(faceFx);
            FaceFXAnimSetEditorControl_M.ImportAudioIntoMirroredFaceFXAssets();
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

                    SaveSpeakerChangesInPlace(rebuildGraphInPlace: true);
                }
            }
        }

        private (NameReference SpeakerName, ExportEntry MaleFaceFX, ExportEntry FemaleFaceFX)? PromptForNewSpeaker()
        {
            if (Pcc == null)
            {
                return null;
            }

            var dialog = new Window
            {
                Title = "Add a speaker",
                Width = 640,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            dialog.SetResourceReference(Window.BackgroundProperty, System.Windows.SystemColors.WindowBrushKey);
            dialog.SetResourceReference(Window.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(dialog);

            ExportEntry selectedMaleFaceFx = null;
            ExportEntry selectedFemaleFaceFx = null;

            var actorTagTextBox = new TextBox
            {
                Margin = new Thickness(0, 4, 0, 0),
                MinWidth = 360
            };

            var maleFaceFxTextBox = new TextBox
            {
                Margin = new Thickness(0, 4, 8, 0),
                IsReadOnly = true,
                Text = "None"
            };

            var femaleFaceFxTextBox = new TextBox
            {
                Margin = new Thickness(0, 4, 8, 0),
                IsReadOnly = true,
                Text = "None"
            };

            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                IsEnabled = false,
                MinWidth = 90,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4)
            };

            void UpdateOkState()
            {
                okButton.IsEnabled = !string.IsNullOrWhiteSpace(actorTagTextBox.Text)
                    && selectedMaleFaceFx != null
                    && selectedFemaleFaceFx != null;
            }

            Button CreateFaceFxSelectButton(bool isMale, TextBox targetTextBox)
            {
                var button = new Button
                {
                    Content = "Select...",
                    MinWidth = 90,
                    Margin = new Thickness(0, 4, 0, 0),
                    Padding = new Thickness(10, 4, 10, 4)
                };

                button.Click += (_, _) =>
                {
                    var currentSelection = isMale ? selectedMaleFaceFx : selectedFemaleFaceFx;
                    var selectedFaceFx = EntrySelector.GetEntry<ExportEntry>(
                        dialog,
                        Pcc,
                        $"Select the {(isMale ? "male" : "female")} FaceFX animation set for the new speaker.",
                        exp => exp.ClassName == "FaceFXAnimSet",
                        currentSelection,
                        selectLastItemByDefault: true);

                    if (selectedFaceFx == null)
                    {
                        return;
                    }

                    if (isMale)
                    {
                        selectedMaleFaceFx = selectedFaceFx;
                    }
                    else
                    {
                        selectedFemaleFaceFx = selectedFaceFx;
                    }

                    targetTextBox.Text = GetSpeakerFaceFXDisplayText(selectedFaceFx);
                    UpdateOkState();
                };

                return button;
            }

            actorTagTextBox.TextChanged += (_, _) => UpdateOkState();
            okButton.Click += (_, _) => dialog.DialogResult = true;

            var rootPanel = new StackPanel
            {
                Margin = new Thickness(18)
            };

            var actorTagPanel = new StackPanel();
            actorTagPanel.Children.Add(new TextBlock
            {
                Text = "Actor's tag"
            });
            actorTagPanel.Children.Add(actorTagTextBox);
            rootPanel.Children.Add(actorTagPanel);

            Grid CreateFaceFxRow(string label, TextBox displayTextBox, bool isMale)
            {
                var grid = new Grid
                {
                    Margin = new Thickness(0, 12, 0, 0)
                };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var labelBlock = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                Grid.SetColumn(labelBlock, 0);
                grid.Children.Add(labelBlock);

                Grid.SetColumn(displayTextBox, 1);
                grid.Children.Add(displayTextBox);

                var selectButton = CreateFaceFxSelectButton(isMale, displayTextBox);
                Grid.SetColumn(selectButton, 2);
                grid.Children.Add(selectButton);

                return grid;
            }

            rootPanel.Children.Add(CreateFaceFxRow("Male FaceFX", maleFaceFxTextBox, isMale: true));
            rootPanel.Children.Add(CreateFaceFxRow("Female FaceFX", femaleFaceFxTextBox, isMale: false));
            rootPanel.Children.Add(new TextBlock
            {
                Text = "Both FaceFX selections are required before the speaker can be created.",
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 90,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4)
            });

            rootPanel.Children.Add(buttonPanel);
            dialog.Content = rootPanel;

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            return (
                NameReference.FromInstancedString(actorTagTextBox.Text.Trim()),
                selectedMaleFaceFx,
                selectedFemaleFaceFx);
        }

        private (SpeakerExtended ReplacementSpeaker, Dictionary<DialogueNodeExtended, int> LineStrRefs, bool? UpdateInterpLengthsByFxa, Dictionary<string, string> InterpNameReplacements)? PromptForBulkCloneSpeakerOptions(SpeakerExtended sourceSpeaker, List<DialogueNodeExtended> sourceNodes)
        {
            var replacementSpeakers = SelectedSpeakerList
                .Where(speaker => speaker.SpeakerID != sourceSpeaker.SpeakerID)
                .ToList();
            if (replacementSpeakers.Count == 0)
            {
                MessageBox.Show("There are no other speaker tags available to use as the replacement.", "Clone Speaker Nodes", MessageBoxButton.OK);
                return null;
            }

            var dialog = new Window
            {
                Title = "Clone speaker nodes",
                Width = 900,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            dialog.SetResourceReference(Window.BackgroundProperty, System.Windows.SystemColors.WindowBrushKey);
            dialog.SetResourceReference(Window.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(dialog);

            var speakerComboBox = new ComboBox
            {
                ItemsSource = replacementSpeakers,
                SelectedIndex = 0,
                Margin = new Thickness(0, 4, 0, 0),
                MinWidth = 300,
                DisplayMemberPath = nameof(SpeakerExtended.DisplayName)
            };

            var tlkRows = new List<(DialogueNodeExtended Node, TextBox TextBox, TextBlock Preview)>();

            var rootPanel = new StackPanel
            {
                Margin = new Thickness(18)
            };
            rootPanel.Children.Add(new TextBlock
            {
                Text = $"Clone nodes using '{sourceSpeaker.DisplayName}' and change cloned nodes to:",
                TextWrapping = TextWrapping.Wrap
            });
            rootPanel.Children.Add(speakerComboBox);

            rootPanel.Children.Add(new TextBlock
            {
                Text = "TLK string mapping for cloned nodes:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 6)
            });

            var mappingGrid = new Grid();
            mappingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mappingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mappingGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var originalHeader = new TextBlock
            {
                Text = "Original TLK String",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 4)
            };
            var clonedHeader = new TextBlock
            {
                Text = "Cloned TLK String",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 0, 4)
            };
            Grid.SetColumn(originalHeader, 0);
            Grid.SetColumn(clonedHeader, 1);
            mappingGrid.Children.Add(originalHeader);
            mappingGrid.Children.Add(clonedHeader);

            for (int i = 0; i < sourceNodes.Count; i++)
            {
                DialogueNodeExtended sourceNode = sourceNodes[i];
                int rowIndex = i + 1;
                mappingGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var originalText = new TextBlock
                {
                    Text = $"E{sourceNode.NodeCount}: {sourceNode.LineStrRef}\n{sourceNode.Line}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 8, 8)
                };
                Grid.SetColumn(originalText, 0);
                Grid.SetRow(originalText, rowIndex);
                mappingGrid.Children.Add(originalText);

                var clonedPanel = new StackPanel
                {
                    Margin = new Thickness(8, 4, 0, 8)
                };
                var clonedTextBox = new TextBox
                {
                    Text = sourceNode.LineStrRef.ToString(CultureInfo.InvariantCulture),
                    MinWidth = 180
                };
                var clonedPreview = new TextBlock
                {
                    Text = sourceNode.Line,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                clonedTextBox.TextChanged += (_, _) =>
                {
                    clonedPreview.Text = int.TryParse(clonedTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineStrRef)
                        ? GetDisplayTlkText(lineStrRef, Pcc)
                        : "Invalid TLK string ref";
                };
                clonedPanel.Children.Add(clonedTextBox);
                clonedPanel.Children.Add(clonedPreview);
                Grid.SetColumn(clonedPanel, 1);
                Grid.SetRow(clonedPanel, rowIndex);
                mappingGrid.Children.Add(clonedPanel);
                tlkRows.Add((sourceNode, clonedTextBox, clonedPreview));
            }

            rootPanel.Children.Add(new ScrollViewer
            {
                Content = mappingGrid,
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            var updateInterpLengthsCheckBox = new CheckBox
            {
                Content = "Update cloned InterpLengths after cloning",
                IsChecked = true,
                Margin = new Thickness(0, 18, 0, 6)
            };
            var interpLengthOptionsPanel = new StackPanel
            {
                Margin = new Thickness(18, 0, 0, 0)
            };
            var byFxaRadioButton = new RadioButton
            {
                Content = "Calculate by FXA length",
                IsChecked = true,
                GroupName = "BulkCloneInterpLengthMode"
            };
            var byAudioRadioButton = new RadioButton
            {
                Content = "Calculate by audio length",
                GroupName = "BulkCloneInterpLengthMode",
                Margin = new Thickness(0, 4, 0, 0)
            };
            updateInterpLengthsCheckBox.Checked += (_, _) => interpLengthOptionsPanel.IsEnabled = true;
            updateInterpLengthsCheckBox.Unchecked += (_, _) => interpLengthOptionsPanel.IsEnabled = false;
            interpLengthOptionsPanel.Children.Add(byFxaRadioButton);
            interpLengthOptionsPanel.Children.Add(byAudioRadioButton);
            rootPanel.Children.Add(updateInterpLengthsCheckBox);
            rootPanel.Children.Add(interpLengthOptionsPanel);

            rootPanel.Children.Add(new TextBlock
            {
                Text = "Interp name replacements:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 6)
            });
            rootPanel.Children.Add(new TextBlock
            {
                Text = "Enter original and new names to replace in cloned Interp group names, m_nmSFXFindActor, and track m_nmFindActor values.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var interpNameReplacementRows = new List<(TextBox OriginalTextBox, TextBox NewTextBox)>();
            var interpNameReplacementGrid = new Grid();
            interpNameReplacementGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            interpNameReplacementGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            interpNameReplacementGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            interpNameReplacementGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var originalInterpNameHeader = new TextBlock
            {
                Text = "Original",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 4)
            };
            var newInterpNameHeader = new TextBlock
            {
                Text = "New",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 8, 4)
            };
            Grid.SetColumn(originalInterpNameHeader, 0);
            Grid.SetColumn(newInterpNameHeader, 1);
            interpNameReplacementGrid.Children.Add(originalInterpNameHeader);
            interpNameReplacementGrid.Children.Add(newInterpNameHeader);

            void AddInterpNameReplacementRow(string originalName = "", string newName = "")
            {
                int rowIndex = interpNameReplacementGrid.RowDefinitions.Count;
                interpNameReplacementGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var originalTextBox = new TextBox
                {
                    Text = originalName,
                    Margin = new Thickness(0, 2, 8, 2),
                    MinWidth = 220
                };
                var newTextBox = new TextBox
                {
                    Text = newName,
                    Margin = new Thickness(8, 2, 8, 2),
                    MinWidth = 220
                };
                var removeButton = new Button
                {
                    Content = "Remove",
                    Margin = new Thickness(0, 2, 0, 2),
                    Padding = new Thickness(8, 2, 8, 2)
                };
                removeButton.Click += (_, _) =>
                {
                    originalTextBox.Text = string.Empty;
                    newTextBox.Text = string.Empty;
                    originalTextBox.Visibility = Visibility.Collapsed;
                    newTextBox.Visibility = Visibility.Collapsed;
                    removeButton.Visibility = Visibility.Collapsed;
                };
                Grid.SetColumn(originalTextBox, 0);
                Grid.SetRow(originalTextBox, rowIndex);
                Grid.SetColumn(newTextBox, 1);
                Grid.SetRow(newTextBox, rowIndex);
                Grid.SetColumn(removeButton, 2);
                Grid.SetRow(removeButton, rowIndex);
                interpNameReplacementGrid.Children.Add(originalTextBox);
                interpNameReplacementGrid.Children.Add(newTextBox);
                interpNameReplacementGrid.Children.Add(removeButton);
                interpNameReplacementRows.Add((originalTextBox, newTextBox));
            }

            var rememberedInterpNameReplacements = DecodeRememberedBulkCloneInterpReplacements();
            if (Settings.DialogueEditor_RememberBulkCloneInterpReplacements && rememberedInterpNameReplacements.Count > 0)
            {
                foreach (var rememberedReplacement in rememberedInterpNameReplacements)
                {
                    AddInterpNameReplacementRow(rememberedReplacement.Key, rememberedReplacement.Value);
                }
            }
            else
            {
                AddInterpNameReplacementRow();
            }

            rootPanel.Children.Add(interpNameReplacementGrid);
            var addInterpReplacementButton = new Button
            {
                Content = "Add interp name replacement",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(10, 4, 10, 4)
            };
            addInterpReplacementButton.Click += (_, _) => AddInterpNameReplacementRow();
            rootPanel.Children.Add(addInterpReplacementButton);

            var rememberInterpReplacementsCheckBox = new CheckBox
            {
                Content = "Remember interp name replacements across sessions",
                IsChecked = Settings.DialogueEditor_RememberBulkCloneInterpReplacements,
                Margin = new Thickness(0, 8, 0, 0)
            };
            rootPanel.Children.Add(rememberInterpReplacementsCheckBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                MinWidth = 90,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4)
            };
            Dictionary<DialogueNodeExtended, int> selectedLineStrRefs = null;
            bool? selectedUpdateInterpLengthsByFxa = null;
            Dictionary<string, string> selectedInterpNameReplacements = null;
            Dictionary<string, string> GetInterpNameReplacementsFromRows()
            {
                var replacements = new Dictionary<string, string>();
                foreach (var row in interpNameReplacementRows)
                {
                    string originalName = row.OriginalTextBox.Text;
                    if (!string.IsNullOrEmpty(originalName))
                    {
                        replacements[originalName] = row.NewTextBox.Text ?? string.Empty;
                    }
                }

                return replacements;
            }

            void PersistRememberedInterpNameReplacements()
            {
                if (rememberInterpReplacementsCheckBox.IsChecked == true)
                {
                    Settings.DialogueEditor_RememberBulkCloneInterpReplacements = true;
                    Settings.DialogueEditor_BulkCloneInterpReplacements = EncodeRememberedBulkCloneInterpReplacements(GetInterpNameReplacementsFromRows());
                }
                else
                {
                    Settings.DialogueEditor_RememberBulkCloneInterpReplacements = false;
                    Settings.DialogueEditor_BulkCloneInterpReplacements = [];
                }
            }

            dialog.Closing += (_, _) => PersistRememberedInterpNameReplacements();

            okButton.Click += (_, _) =>
            {
                var lineStrRefs = new Dictionary<DialogueNodeExtended, int>();
                foreach (var row in tlkRows)
                {
                    if (!int.TryParse(row.TextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lineStrRef))
                    {
                        MessageBox.Show($"'{row.TextBox.Text}' is not a valid TLK string ref.", "Clone Speaker Nodes", MessageBoxButton.OK);
                        return;
                    }

                    lineStrRefs[row.Node] = lineStrRef;
                }

                selectedLineStrRefs = lineStrRefs;
                selectedUpdateInterpLengthsByFxa = updateInterpLengthsCheckBox.IsChecked == true
                    ? byFxaRadioButton.IsChecked == true
                    : null;
                selectedInterpNameReplacements = GetInterpNameReplacementsFromRows();

                dialog.DialogResult = true;
            };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 90,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4)
            });

            rootPanel.Children.Add(buttonPanel);
            dialog.Content = rootPanel;

            return dialog.ShowDialog() == true && speakerComboBox.SelectedItem is SpeakerExtended replacementSpeaker
                ? (replacementSpeaker, selectedLineStrRefs ?? [], selectedUpdateInterpLengthsByFxa, selectedInterpNameReplacements ?? [])
                : null;
        }

        private void SpeakerAdd()
        {
            int maxID = SelectedSpeakerList.Max(x => x.SpeakerID);
            var newSpeaker = PromptForNewSpeaker();
            if (!newSpeaker.HasValue)
                return;

            var (speakerName, maleFaceFx, femaleFaceFx) = newSpeaker.Value;
            Pcc.FindNameOrAdd(speakerName.Name);
            int strRefId = LookupTagRef(speakerName.Instanced);
            string friendlyName = strRefId > 0 ? GlobalFindStrRefbyID(strRefId, Pcc) : "No Data";
            SelectedSpeakerList.Add(new SpeakerExtended(maxID + 1, speakerName, maleFaceFx, femaleFaceFx, strRefId, friendlyName));
            SaveSpeakerChangesInPlace(reindexSpeakerIds: true);
            Speakers_ListBox.SelectedIndex = SelectedSpeakerList.Count - 1;
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
            SaveSpeakerChangesInPlace(rebuildGraphInPlace: true, reindexSpeakerIds: true);
            Speakers_ListBox.SelectedIndex = Math.Min(deleteTarget, SelectedSpeakerList.Count - 1);
        }
        private void SpeakerGoToName()
        {
            TextBox_Speaker_Name.Focus();
            TextBox_Speaker_Name.CaretIndex = TextBox_Speaker_Name.Text.Length;
        }

        private const char BulkCloneInterpReplacementSeparator = '\u001F';

        private static Dictionary<string, string> DecodeRememberedBulkCloneInterpReplacements()
        {
            var replacements = new Dictionary<string, string>();
            foreach (string encodedReplacement in Settings.DialogueEditor_BulkCloneInterpReplacements ?? [])
            {
                if (string.IsNullOrEmpty(encodedReplacement))
                {
                    continue;
                }

                int separatorIndex = encodedReplacement.IndexOf(BulkCloneInterpReplacementSeparator);
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string originalName = encodedReplacement[..separatorIndex];
                string newName = encodedReplacement[(separatorIndex + 1)..];
                replacements[originalName] = newName;
            }

            return replacements;
        }

        private static List<string> EncodeRememberedBulkCloneInterpReplacements(Dictionary<string, string> replacements)
        {
            return replacements
                .Where(kvp => !string.IsNullOrEmpty(kvp.Key))
                .Select(kvp => $"{kvp.Key}{BulkCloneInterpReplacementSeparator}{kvp.Value ?? string.Empty}")
                .ToList();
        }

        private bool CanCloneSpeakerNodes(object obj)
        {
            return Pcc != null
                && SelectedConv != null
                && SelectedSpeaker != null
                && SelectedSpeaker.SpeakerID >= -2
                && SelectedConv.EntryList.Any(node => node.SpeakerIndex == SelectedSpeaker.SpeakerID && node.InterpData != null);
        }

        private void CloneSpeakerNodes(object obj)
        {
            if (SelectedConv == null || SelectedSpeaker == null)
            {
                return;
            }

            if (!Enum.TryParse(obj as string, out SpeakerNodeCloneInsertionPosition insertionPosition))
            {
                insertionPosition = SpeakerNodeCloneInsertionPosition.BelowClone;
            }

            var sourceSpeaker = SelectedSpeaker;
            var sourceNodes = SelectedConv.EntryList
                .Where(node => node.SpeakerIndex == sourceSpeaker.SpeakerID)
                .ToList();
            if (sourceNodes.Count == 0)
            {
                MessageBox.Show($"No entry nodes use the speaker tag '{sourceSpeaker.DisplayName}'.", "Clone Speaker Nodes", MessageBoxButton.OK);
                return;
            }

            var cloneOptionsResult = PromptForBulkCloneSpeakerOptions(sourceSpeaker, sourceNodes);
            if (!cloneOptionsResult.HasValue)
            {
                return;
            }

            var (replacementSpeaker, lineStrRefs, updateInterpLengthsByFxa, interpNameReplacements) = cloneOptionsResult.Value;

            var orderedSourceNodes = insertionPosition == SpeakerNodeCloneInsertionPosition.TopOfList
                ? sourceNodes.AsEnumerable().Reverse().ToList()
                : sourceNodes;
            int clonedCount = 0;
            int skippedCount = 0;
            var faceOnlyVoNodes = new List<DialogueNodeExtended>();
            var incomingLinkSnapshots = CreateBulkCloneIncomingLinkSnapshots(sourceNodes);
            var startLinkSnapshots = CreateBulkCloneStartLinkSnapshots(sourceNodes);

            using var _ = SuppressPackageUpdates();
            foreach (DialogueNodeExtended sourceNode in orderedSourceNodes)
            {
                int sourceIndex = SelectedConv.EntryList.IndexOf(sourceNode);
                if (sourceIndex < 0)
                {
                    skippedCount++;
                    continue;
                }

                if (sourceNode.InterpData == null)
                {
                    skippedCount++;
                    continue;
                }

                int insertionIndex = insertionPosition switch
                {
                    SpeakerNodeCloneInsertionPosition.AboveClone => sourceIndex,
                    SpeakerNodeCloneInsertionPosition.BelowClone => sourceIndex + 1,
                    SpeakerNodeCloneInsertionPosition.TopOfList => 0,
                    SpeakerNodeCloneInsertionPosition.BottomOfList => SelectedConv.EntryList.Count,
                    _ => sourceIndex + 1
                };

                DiagNode sourceGraphNode = CurrentObjects.OfType<DiagNode>().FirstOrDefault(node => ReferenceEquals(node.Node, sourceNode));
                if (sourceGraphNode != null)
                {
                    DialogueNode_Selected(sourceGraphNode);
                }
                else
                {
                    SelectDialogueNodeByIndex(sourceNode.NodeCount, sourceNode.IsReply);
                    sourceGraphNode = SelectedObjects.OfType<DiagNode>().FirstOrDefault();
                }

                var cloneOptions = new CloneDialogueNodeOptions
                {
                    CloneLinks = true,
                    InputEdges = [],
                    NodeInsertionIndex = insertionIndex,
                    ReplacementSpeaker = replacementSpeaker
                };

                DialogueNodeExtended clonedNode = DialogueEditorExperimentsE.CloneNodeAndSequence(this, cloneOptions, showSuccessMessage: false);
                if (clonedNode == null)
                {
                    skippedCount++;
                    continue;
                }

                CloneBulkIncomingLinks(sourceNode, clonedNode, incomingLinkSnapshots, insertionPosition);
                CloneBulkStartLinks(sourceNode, clonedNode, startLinkSnapshots, insertionPosition);

                bool tlkChanged = false;
                if (lineStrRefs.TryGetValue(sourceNode, out int clonedLineStrRef))
                {
                    tlkChanged = clonedLineStrRef != sourceNode.LineStrRef;
                    clonedNode.LineStrRef = clonedLineStrRef;
                    clonedNode.NodeProp.Properties.AddOrReplaceProp(new StringRefProperty(clonedLineStrRef, "srText"));
                    UpdateNodeLineDerivedData(clonedNode);
                    DialogueEditorExperimentsE.UpdateVOAndComment(clonedNode);
                }

                if (interpNameReplacements.Count > 0)
                {
                    BulkInterpEditorDialog.ApplyNameReplacementsToInterpData(clonedNode.InterpData, interpNameReplacements);
                }

                if (tlkChanged && updateInterpLengthsByFxa.HasValue)
                {
                    DialogueEditorExperimentsE.UpdateInterpLength(clonedNode, updateInterpLengthsByFxa.Value, FaceFXAnimSetEditorControl_F, FaceFXAnimSetEditorControl_M);
                    clonedNode.InterpLength = clonedNode.InterpData?.GetProperty<FloatProperty>("InterpLength")?.Value ?? clonedNode.InterpLength;
                }

                if (InterpDataContainsFaceOnlyVoTrack(clonedNode.InterpData))
                {
                    faceOnlyVoNodes.Add(clonedNode);
                }

                clonedCount++;
            }

            RecreateNodesToProperties(SelectedConv);
            RebuildGraphInPlace(rebuildStarts: true);
            ApplySpeakerNodeHighlighting();

            string skippedText = skippedCount > 0 ? $" {skippedCount} node(s) were skipped." : string.Empty;
            MessageBox.Show($"Cloned {clonedCount} node(s) from '{sourceSpeaker.DisplayName}' to '{replacementSpeaker.DisplayName}'.{skippedText}", "Clone Speaker Nodes", MessageBoxButton.OK);
            ShowFaceOnlyVoBulkCloneWarning(faceOnlyVoNodes);
        }

        private Dictionary<DialogueNodeExtended, List<BulkCloneIncomingLinkSnapshot>> CreateBulkCloneIncomingLinkSnapshots(IEnumerable<DialogueNodeExtended> sourceNodes)
        {
            var snapshots = new Dictionary<DialogueNodeExtended, List<BulkCloneIncomingLinkSnapshot>>();
            foreach (var sourceNode in sourceNodes)
            {
                var sourceGraphNode = CurrentObjects.OfType<DiagNode>().FirstOrDefault(node => ReferenceEquals(node.Node, sourceNode));
                if (sourceGraphNode == null)
                {
                    continue;
                }

                foreach (var edge in sourceGraphNode.InputEdges)
                {
                    if (edge.originator is not DiagNode inputNode)
                    {
                        continue;
                    }

                    int sourceLinkIndex = inputNode.Outlinks.FindIndex(outlink => outlink.Edges.Contains(edge));
                    Property sourceLinkProperty = GetLinkPropertyAt(inputNode.Node, sourceLinkIndex);
                    if (sourceLinkIndex < 0 || sourceLinkProperty == null)
                    {
                        continue;
                    }

                    if (!snapshots.TryGetValue(sourceNode, out var nodeSnapshots))
                    {
                        nodeSnapshots = [];
                        snapshots[sourceNode] = nodeSnapshots;
                    }

                    nodeSnapshots.Add(new BulkCloneIncomingLinkSnapshot
                    {
                        SourceNode = inputNode.Node,
                        MatchingTargetOrdinal = GetMatchingLinkOrdinal(inputNode.Node, sourceNode, sourceLinkIndex),
                        SourceLinkProperty = sourceLinkProperty.DeepClone()
                    });
                }
            }

            return snapshots;
        }

        private Dictionary<DialogueNodeExtended, List<BulkCloneStartLinkSnapshot>> CreateBulkCloneStartLinkSnapshots(IEnumerable<DialogueNodeExtended> sourceNodes)
        {
            var snapshots = new Dictionary<DialogueNodeExtended, List<BulkCloneStartLinkSnapshot>>();
            foreach (var sourceNode in sourceNodes)
            {
                int matchingTargetOrdinal = 0;
                foreach (var startLink in SelectedConv.StartingList.OrderBy(kvp => kvp.Key))
                {
                    if (startLink.Value != sourceNode.NodeCount)
                    {
                        continue;
                    }

                    if (!snapshots.TryGetValue(sourceNode, out var nodeSnapshots))
                    {
                        nodeSnapshots = [];
                        snapshots[sourceNode] = nodeSnapshots;
                    }

                    nodeSnapshots.Add(new BulkCloneStartLinkSnapshot
                    {
                        MatchingTargetOrdinal = matchingTargetOrdinal
                    });
                    matchingTargetOrdinal++;
                }
            }

            return snapshots;
        }

        private void CloneBulkIncomingLinks(
            DialogueNodeExtended sourceNode,
            DialogueNodeExtended clonedNode,
            Dictionary<DialogueNodeExtended, List<BulkCloneIncomingLinkSnapshot>> incomingLinkSnapshots,
            SpeakerNodeCloneInsertionPosition insertionPosition)
        {
            if (!incomingLinkSnapshots.TryGetValue(sourceNode, out var nodeSnapshots))
            {
                return;
            }

            foreach (var snapshot in nodeSnapshots)
            {
                int sourceLinkIndex = FindCurrentLinkIndex(snapshot.SourceNode, sourceNode, snapshot.MatchingTargetOrdinal);
                int insertionIndex = GetBulkCloneRelativeInsertionIndex(sourceLinkIndex, insertionPosition);
                CloneIncomingLinkToNode(snapshot.SourceNode, snapshot.SourceLinkProperty, clonedNode, insertionIndex);
            }
        }

        private void CloneBulkStartLinks(
            DialogueNodeExtended sourceNode,
            DialogueNodeExtended clonedNode,
            Dictionary<DialogueNodeExtended, List<BulkCloneStartLinkSnapshot>> startLinkSnapshots,
            SpeakerNodeCloneInsertionPosition insertionPosition)
        {
            if (!startLinkSnapshots.TryGetValue(sourceNode, out var nodeSnapshots))
            {
                return;
            }

            foreach (var snapshot in nodeSnapshots)
            {
                int sourceStartIndex = FindCurrentStartIndex(sourceNode, snapshot.MatchingTargetOrdinal);
                int insertionIndex = GetBulkCloneRelativeInsertionIndex(sourceStartIndex, insertionPosition);
                AddStartNodeForEntry(clonedNode.NodeCount, insertionIndex);
            }
        }

        private static Property GetLinkPropertyAt(DialogueNodeExtended sourceNode, int linkIndex)
        {
            if (sourceNode == null || linkIndex < 0)
            {
                return null;
            }

            if (sourceNode.IsReply)
            {
                var entryList = sourceNode.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
                return linkIndex < entryList?.Count ? entryList[linkIndex] : null;
            }

            var replyList = sourceNode.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");
            return linkIndex < replyList?.Count ? replyList[linkIndex] : null;
        }

        private static int GetMatchingLinkOrdinal(DialogueNodeExtended linkSourceNode, DialogueNodeExtended targetNode, int sourceLinkIndex)
        {
            int matchingTargetOrdinal = 0;
            for (int i = 0; i < sourceLinkIndex; i++)
            {
                if (GetLinkTargetIndex(linkSourceNode, i) == targetNode.NodeCount)
                {
                    matchingTargetOrdinal++;
                }
            }

            return matchingTargetOrdinal;
        }

        private static int FindCurrentLinkIndex(DialogueNodeExtended linkSourceNode, DialogueNodeExtended targetNode, int matchingTargetOrdinal)
        {
            int currentOrdinal = 0;
            int linkCount = GetLinkCount(linkSourceNode);
            for (int i = 0; i < linkCount; i++)
            {
                if (GetLinkTargetIndex(linkSourceNode, i) != targetNode.NodeCount)
                {
                    continue;
                }

                if (currentOrdinal == matchingTargetOrdinal)
                {
                    return i;
                }

                currentOrdinal++;
            }

            return -1;
        }

        private int FindCurrentStartIndex(DialogueNodeExtended targetNode, int matchingTargetOrdinal)
        {
            int currentOrdinal = 0;
            foreach (var startLink in SelectedConv.StartingList.OrderBy(kvp => kvp.Key))
            {
                if (startLink.Value != targetNode.NodeCount)
                {
                    continue;
                }

                if (currentOrdinal == matchingTargetOrdinal)
                {
                    return startLink.Key;
                }

                currentOrdinal++;
            }

            return -1;
        }

        private static int GetBulkCloneRelativeInsertionIndex(int sourceIndex, SpeakerNodeCloneInsertionPosition insertionPosition)
        {
            return insertionPosition switch
            {
                SpeakerNodeCloneInsertionPosition.TopOfList => 0,
                SpeakerNodeCloneInsertionPosition.BottomOfList => int.MaxValue,
                SpeakerNodeCloneInsertionPosition.AboveClone => sourceIndex >= 0 ? sourceIndex : 0,
                _ => sourceIndex >= 0 ? sourceIndex + 1 : int.MaxValue
            };
        }

        private static int GetLinkCount(DialogueNodeExtended sourceNode)
        {
            if (sourceNode == null)
            {
                return 0;
            }

            return sourceNode.IsReply
                ? sourceNode.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList")?.Count ?? 0
                : sourceNode.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew")?.Count ?? 0;
        }

        private static int GetLinkTargetIndex(DialogueNodeExtended sourceNode, int linkIndex)
        {
            if (sourceNode == null || linkIndex < 0)
            {
                return -1;
            }

            if (sourceNode.IsReply)
            {
                var entryList = sourceNode.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
                return linkIndex < entryList?.Count ? entryList[linkIndex].Value : -1;
            }

            var replyList = sourceNode.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");
            return linkIndex < replyList?.Count ? replyList[linkIndex].GetProp<IntProperty>("nIndex")?.Value ?? -1 : -1;
        }

        private void CloneIncomingLinkToNode(DialogueNodeExtended sourceNode, Property sourceLinkProperty, DialogueNodeExtended targetNode, int insertionIndex)
        {
            if (sourceNode == null || sourceLinkProperty == null || targetNode == null)
            {
                return;
            }

            if (sourceNode.IsReply)
            {
                var entryList = sourceNode.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList")
                                ?? new ArrayProperty<IntProperty>("EntryList");
                var clonedLink = sourceLinkProperty is IntProperty intProperty
                    ? (IntProperty)intProperty.DeepClone()
                    : new IntProperty(targetNode.NodeCount);
                clonedLink.Value = targetNode.NodeCount;
                entryList.Insert(GetCloneInsertionIndex(entryList.Count, insertionIndex), clonedLink);
                sourceNode.NodeProp.Properties.AddOrReplaceProp(entryList);
            }
            else
            {
                var replyList = sourceNode.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew")
                                ?? new ArrayProperty<StructProperty>("ReplyListNew");
                StructProperty clonedLink;
                if (sourceLinkProperty is StructProperty structProperty)
                {
                    clonedLink = (StructProperty)structProperty.DeepClone();
                    clonedLink.Properties.AddOrReplaceProp(new IntProperty(targetNode.NodeCount, "nIndex"));
                }
                else
                {
                    clonedLink = new StructProperty("BioDialogReplyListDetails", new PropertyCollection
                    {
                        new IntProperty(targetNode.NodeCount, "nIndex"),
                        new StringRefProperty(663399, "srParaphrase"),
                        new StrProperty(string.Empty, "sParaphrase"),
                        new EnumProperty("REPLY_CATEGORY_DEFAULT", "EReplyCategory", Pcc.Game, "Category"),
                        new NoneProperty()
                    });
                }

                replyList.Insert(GetCloneInsertionIndex(replyList.Count, insertionIndex), clonedLink);
                sourceNode.NodeProp.Properties.AddOrReplaceProp(replyList);
            }
        }

        private static int GetBulkCloneLinkInsertionIndex(DiagNode sourceGraphNode, List<DiagEdEdge> inputEdges, SpeakerNodeCloneInsertionPosition insertionPosition)
        {
            return insertionPosition switch
            {
                SpeakerNodeCloneInsertionPosition.TopOfList => 0,
                SpeakerNodeCloneInsertionPosition.BottomOfList => int.MaxValue,
                _ => GetBulkCloneRelativeLinkInsertionIndex(sourceGraphNode, inputEdges, insertionPosition)
            };
        }

        private static int GetBulkCloneRelativeLinkInsertionIndex(DiagNode sourceGraphNode, List<DiagEdEdge> inputEdges, SpeakerNodeCloneInsertionPosition insertionPosition)
        {
            foreach (var inputEdge in inputEdges)
            {
                if (inputEdge.originator is not DiagNode inputNode)
                {
                    continue;
                }

                int sourceLinkIndex = inputNode.Outlinks.FindIndex(outlink => outlink.Edges.Contains(inputEdge));
                if (sourceLinkIndex >= 0)
                {
                    return insertionPosition == SpeakerNodeCloneInsertionPosition.AboveClone
                        ? sourceLinkIndex
                        : sourceLinkIndex + 1;
                }
            }

            return insertionPosition == SpeakerNodeCloneInsertionPosition.AboveClone
                ? 0
                : sourceGraphNode?.InputEdges.Count ?? int.MaxValue;
        }

        private int GetBulkCloneStartInsertionIndex(DiagNode sourceGraphNode, SpeakerNodeCloneInsertionPosition insertionPosition)
        {
            return insertionPosition switch
            {
                SpeakerNodeCloneInsertionPosition.TopOfList => 0,
                SpeakerNodeCloneInsertionPosition.BottomOfList => SelectedConv?.StartingList.Count ?? int.MaxValue,
                _ => GetBulkCloneRelativeStartInsertionIndex(sourceGraphNode, insertionPosition)
            };
        }

        private int GetBulkCloneRelativeStartInsertionIndex(DiagNode sourceGraphNode, SpeakerNodeCloneInsertionPosition insertionPosition)
        {
            var sourceStart = sourceGraphNode?.InputEdges
                .Select(edge => edge.originator)
                .OfType<DStart>()
                .OrderBy(start => start.Order)
                .FirstOrDefault();

            if (sourceStart == null)
            {
                return insertionPosition == SpeakerNodeCloneInsertionPosition.AboveClone
                    ? 0
                    : SelectedConv?.StartingList.Count ?? int.MaxValue;
            }

            return insertionPosition == SpeakerNodeCloneInsertionPosition.AboveClone
                ? sourceStart.Order
                : sourceStart.Order + 1;
        }

        private bool InterpDataContainsFaceOnlyVoTrack(ExportEntry interpData)
        {
            if (interpData == null || Pcc == null)
            {
                return false;
            }

            var interpGroups = interpData.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
            if (interpGroups == null)
            {
                return false;
            }

            foreach (var groupRef in interpGroups)
            {
                if (!Pcc.TryGetUExport(groupRef.Value, out ExportEntry interpGroup))
                {
                    continue;
                }

                var interpTracks = interpGroup.GetProperty<ArrayProperty<ObjectProperty>>("InterpTracks");
                if (interpTracks == null)
                {
                    continue;
                }

                foreach (var trackRef in interpTracks)
                {
                    if (Pcc.TryGetUExport(trackRef.Value, out ExportEntry track)
                        && track.IsA("SFXInterpTrackPlayFaceOnlyVO"))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void ShowFaceOnlyVoBulkCloneWarning(List<DialogueNodeExtended> faceOnlyVoNodes)
        {
            if (faceOnlyVoNodes.Count == 0)
            {
                return;
            }

            var dialog = new Window
            {
                Title = "FaceOnlyVO tracks detected",
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            dialog.SetResourceReference(Window.BackgroundProperty, System.Windows.SystemColors.WindowBrushKey);
            dialog.SetResourceReference(Window.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(dialog);

            var rootPanel = new StackPanel
            {
                Margin = new Thickness(18)
            };
            rootPanel.Children.Add(new TextBlock
            {
                Text = $"{faceOnlyVoNodes.Count} node(s) contain FaceOnlyVO tracks—verify timing manually.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            rootPanel.Children.Add(new TextBlock
            {
                Text = "Click a node below to select it in the graph:",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var nodePanel = new StackPanel();
            foreach (DialogueNodeExtended node in faceOnlyVoNodes)
            {
                var nodeButton = new Button
                {
                    Content = $"E{node.NodeCount}: {node.LineStrRef} {node.Line}",
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 2, 0, 2),
                    Padding = new Thickness(8, 4, 8, 4),
                    Tag = node
                };
                nodeButton.Click += (_, _) =>
                {
                    if (nodeButton.Tag is DialogueNodeExtended targetNode)
                    {
                        SelectDialogueNodeByIndex(targetNode.NodeCount, targetNode.IsReply, centerView: true);
                    }
                };
                nodePanel.Children.Add(nodeButton);
            }

            rootPanel.Children.Add(new ScrollViewer
            {
                Content = nodePanel,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            var closeButton = new Button
            {
                Content = "Close",
                IsDefault = true,
                IsCancel = true,
                MinWidth = 90,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(10, 4, 10, 4)
            };
            closeButton.Click += (_, _) => dialog.Close();
            rootPanel.Children.Add(closeButton);

            dialog.Content = rootPanel;
            dialog.Show();
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
        private void EditBox_CommitAndLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }

            EditBox_LostKeyboardFocus(sender, e);
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

        private void Start_ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Start_ListBox.SelectedIndex < 0)
            {
                return;
            }

            var start = CurrentObjects.OfType<DStart>().FirstOrDefault(s => s.Order == Start_ListBox.SelectedIndex);
            if (start == null || graphEditor?.Camera == null)
            {
                return;
            }

            SelectGraphObjectByUid(start.NodeUID);
            graphEditor.Camera.AnimateViewToCenterBounds(start.GlobalFullBounds, false, 100);
            graphEditor.Refresh();
        }

        private void AddStartNodeForEntry(int entryIndex, bool insertAtTop = false)
        {
            AddStartNodeForEntry(entryIndex, insertAtTop ? 0 : int.MaxValue);
        }

        private void AddStartNodeForEntry(int entryIndex, int insertionIndex)
        {
            if (SelectedConv == null)
            {
                return;
            }

            var existingStartPositions = CurrentObjects
                .OfType<DStart>()
                .OrderBy(start => start.Order)
                .Select(start => new PointF(start.X + start.OffsetX, start.Y + start.OffsetY))
                .ToList();

            var orderedStarts = SelectedConv.StartingList
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value)
                .ToList();

            insertionIndex = GetCloneInsertionIndex(orderedStarts.Count, insertionIndex);
            orderedStarts.Insert(insertionIndex, entryIndex);
            SelectedConv.StartingList.Clear();
            for (int i = 0; i < orderedStarts.Count; i++)
            {
                SelectedConv.StartingList.Add(i, orderedStarts[i]);
            }

            forcedSelectStart = insertionIndex;

            var preferredPositions = new Dictionary<int, PointF>
            {
                [insertionIndex] = GetNewStartNodePosition(insertionIndex, entryIndex)
            };

            int existingPositionIndex = 0;
            for (int i = 0; i < orderedStarts.Count; i++)
            {
                if (i == insertionIndex)
                {
                    continue;
                }

                preferredPositions[i] = existingStartPositions[existingPositionIndex++];
            }

            ApplyStartMutationInPlace(preferredPositions);
        }

        private void EntryNode_CreateStart_Click(object sender, RoutedEventArgs e)
        {
            var selectedEntryNode = SelectedObjects
                .OfType<DiagNode>()
                .FirstOrDefault(node => !node.Node.IsReply)
                ?? CurrentObjects
                    .OfType<DiagNode>()
                    .FirstOrDefault(node => node.IsSelected && !node.Node.IsReply);

            if (selectedEntryNode == null)
            {
                return;
            }

            int? insertionIndex = PromptForCloneInsertionIndex(
                "Choose where the new start node should be inserted in the start node list.",
                SelectedConv?.StartingList.Count ?? 0);
            if (!insertionIndex.HasValue)
            {
                return;
            }

            AddStartNodeForEntry(selectedEntryNode.Node.NodeCount, insertionIndex.Value);
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
                AddStartNodeForEntry(newVal);
                return;
            }

            forcedSelectStart = newKey;
            ApplyStartMutationInPlace();
        }
        private void StartDelete()
        {
            SelectedConv.StartingList.Remove(Start_ListBox.SelectedIndex);
            ApplyStartMutationInPlace();
        }
        private void StartMoveAction(object obj)
        {
            StartTopButton.IsEnabled = false;
            StartUpButton.IsEnabled = false;
            StartDownButton.IsEnabled = false;
            StartBottomButton.IsEnabled = false;

            try
            {
                string direction = obj as string;
                int selectedIndex = Start_ListBox.SelectedIndex;
                if (SelectedConv == null || selectedIndex < 0)
                {
                    return;
                }

                var orderedStarts = SelectedConv.StartingList
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => kvp.Value)
                    .ToList();

                int targetIndex = direction switch
                {
                    "Top" => 0,
                    "Up" => selectedIndex - 1,
                    "Bottom" => orderedStarts.Count - 1,
                    _ => selectedIndex + 1
                };

                if (targetIndex < 0 || targetIndex >= orderedStarts.Count || targetIndex == selectedIndex)
                {
                    return;
                }

                Start_ListBox.SelectedIndex = -1;

                int selectedValue = orderedStarts[selectedIndex];
                orderedStarts.RemoveAt(selectedIndex);
                orderedStarts.Insert(targetIndex, selectedValue);

                SelectedConv.StartingList.Clear();
                for (int i = 0; i < orderedStarts.Count; i++)
                {
                    SelectedConv.StartingList.Add(i, orderedStarts[i]);
                }

                forcedSelectStart = targetIndex;
                ApplyStartMutationInPlace();
            }
            finally
            {
                StartTopButton.IsEnabled = true;
                StartUpButton.IsEnabled = true;
                StartDownButton.IsEnabled = true;
                StartBottomButton.IsEnabled = true;
            }
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

        public DObj SelectGraphObjectByUid(int nodeUid, bool centerView = false)
        {
            var targetObject = CurrentObjects.FirstOrDefault(o => o.NodeUID == nodeUid);
            switch (targetObject)
            {
                case DiagNode diagNode:
                    DialogueNode_Selected(diagNode);
                    break;
                case DStart startNode:
                    foreach (var oldselection in SelectedObjects)
                    {
                        oldselection.IsSelected = false;
                    }

                    SelectedObjects.ClearEx();
                    startNode.IsSelected = true;
                    SelectedObjects.Add(startNode);
                    Start_ListBox.SelectedIndex = startNode.Order;
                    SetUIMode(3, false);
                    break;
                default:
                    return null;
            }

            if (centerView && graphEditor != null)
            {
                graphEditor.Camera.AnimateViewToCenterBounds(targetObject.GlobalFullBounds, false, 100);
                graphEditor.Refresh();
            }

            return targetObject;
        }

        public DiagNode SelectDialogueNodeByIndex(int index, bool isReply = false, bool centerView = false)
        {
            var node = index == -1
                ? DialogueNode_SelectByIndex(index, isReply)
                : SelectGraphObjectByUid(index + (isReply ? 1000 : 0), centerView) as DiagNode;

            if (index == -1 && centerView && node != null && graphEditor != null)
            {
                graphEditor.Camera.AnimateViewToCenterBounds(node.GlobalFullBounds, false, 100);
                graphEditor.Refresh();
            }

            return node;
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

            UpdateSelectedConnectionHighlighting();
            graphEditor?.Refresh();

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
            RefreshNodeInGraph(node, persistConversation: false);
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
            RefreshNodeInGraph(node, persistConversation: false);
        }

        public void UpdateNodeLineStrRefFromGraph(DialogueNodeExtended node, int lineStrRef)
        {
            if (node == null || SelectedConv == null || node.LineStrRef == lineStrRef)
            {
                return;
            }

            node.LineStrRef = lineStrRef;
            node.NodeProp.Properties.AddOrReplaceProp(new StringRefProperty(lineStrRef, "srText"));

            ApplyLineStrRefChange(node);
        }

        public void UpdateNodeExportIdFromGraph(DialogueNodeExtended node, int exportId)
        {
            if (node == null || SelectedConv == null || node.ExportID == exportId)
            {
                return;
            }

            node.ExportID = exportId;
            node.NodeProp.Properties.AddOrReplaceProp(new IntProperty(exportId, "nExportID"));

            ApplyExportIdChange(node);
        }

        public void ToggleNodeFiresConditionalFromGraph(DialogueNodeExtended node)
        {
            if (node == null || SelectedConv == null)
            {
                return;
            }

            node.FiresConditional = !node.FiresConditional;
            node.NodeProp.Properties.AddOrReplaceProp(new BoolProperty(node.FiresConditional, "bFireConditional"));
            node.ConditionalPlotPath = node.FiresConditional
                ? PlotDatabases.FindPlotConditionalByID(node.ConditionalOrBool, Pcc.Game)?.Path
                : PlotDatabases.FindPlotBoolByID(node.ConditionalOrBool, Pcc.Game)?.Path;

            IsLocalUpdate = true;
            RecreateNodesToProperties(SelectedConv);
            RefreshNodePlotSectionsInGraph(node);
        }

        private void ApplyLineStrRefChange(DialogueNodeExtended node)
        {
            UpdateNodeLineDerivedData(node);

            var graphNode = CurrentObjects
                .OfType<DiagNode>()
                .FirstOrDefault(o => o.Node.NodeCount == node.NodeCount && o.Node.IsReply == node.IsReply);

            if (graphNode != null)
            {
                PushLocalGraphChanges(graphNode);
            }
            else
            {
                IsLocalUpdate = true;
                RecreateNodesToProperties(SelectedConv);
            }

            DialogueNode_SelectByIndex(node.NodeCount, node.IsReply);
            ApplySpeakerNodeHighlighting();
        }

        private void ApplyExportIdChange(DialogueNodeExtended node)
        {
            if (node == null || SelectedConv == null)
            {
                return;
            }

            node.InterpData = SelectedConv.ParseSingleNodeInterpData(node);
            node.InterpLength = node.InterpData?.GetProperty<FloatProperty>("InterpLength")?.Value ?? 0f;

            var graphNode = CurrentObjects
                .OfType<DiagNode>()
                .FirstOrDefault(o => o.Node.NodeCount == node.NodeCount && o.Node.IsReply == node.IsReply);

            if (graphNode != null)
            {
                PushLocalGraphChanges(graphNode);
            }
            else
            {
                IsLocalUpdate = true;
                RecreateNodesToProperties(SelectedConv);
            }

            DialogueNode_SelectByIndex(node.NodeCount, node.IsReply);
            ApplySpeakerNodeHighlighting();
        }

        private void RefreshNodePlotSectionsInGraph(DialogueNodeExtended node)
        {
            if (node == null)
            {
                return;
            }

            var graphNode = CurrentObjects
                .OfType<DiagNode>()
                .FirstOrDefault(o => o.Node.NodeCount == node.NodeCount && o.Node.IsReply == node.IsReply);

            if (graphNode != null)
            {
                graphNode.RefreshPlotSectionsInPlace();
                graphEditor?.Refresh();
            }

            DialogueNode_SelectByIndex(node.NodeCount, node.IsReply);
            ApplySpeakerNodeHighlighting();
        }

        private void UpdateNodeLineDerivedData(DialogueNodeExtended node)
        {
            if (node == null || Pcc == null)
            {
                return;
            }

            node.Line = GetDisplayTlkText(node.LineStrRef, Pcc);

            if (node.Line != "No data" && !string.IsNullOrWhiteSpace(node.Line))
            {
                node.FaceFX_Female = $"FXA_{node.LineStrRef}_F";
                node.FaceFX_Male = $"FXA_{node.LineStrRef}_M";
            }
            else
            {
                node.FaceFX_Female = "None";
                node.FaceFX_Male = "None";
            }

            if (Pcc.Game is MEGame.LE1 or MEGame.ME1)
            {
                node.WwiseStream_Female = null;
                node.WwiseStream_Male = null;
                return;
            }

            string femaleSearch = $"{node.LineStrRef}_f";
            string maleSearch = $"{node.LineStrRef}_m";
            node.WwiseStream_Female = Pcc.Exports.FirstOrDefault(x => x.ClassName == "WwiseStream"
                && x.ObjectName.Name.Contains(femaleSearch, StringComparison.OrdinalIgnoreCase));
            node.WwiseStream_Male = Pcc.Exports.FirstOrDefault(x => x.ClassName == "WwiseStream"
                && x.ObjectName.Name.Contains(maleSearch, StringComparison.OrdinalIgnoreCase));
        }

        private void InitializeDialogueNodeDerivedData(DialogueNodeExtended node)
        {
            if (node == null || SelectedConv == null)
            {
                return;
            }

            node.SpeakerTag = SelectedConv.Speakers.FirstOrDefault(s => s.SpeakerID == node.SpeakerIndex);
            UpdateNodeLineDerivedData(node);

            int scriptIndex = node.NodeProp.GetProp<IntProperty>("nScriptIndex")?.Value ?? -1;
            int resolvedScriptIndex = scriptIndex + 1;
            if (resolvedScriptIndex >= 0 && resolvedScriptIndex < SelectedConv.ScriptList.Count)
            {
                node.Script = SelectedConv.ScriptList[resolvedScriptIndex];
            }
        }

        private void RefreshNodeInGraph(DialogueNodeExtended node, bool persistConversation = true)
        {
            if (node == null)
            {
                return;
            }

            var graphNode = CurrentObjects
                .OfType<DiagNode>()
                .FirstOrDefault(o => o.Node.NodeCount == node.NodeCount && o.Node.IsReply == node.IsReply);

            if (graphNode != null)
            {
                PushLocalGraphChanges(graphNode, persistConversation);
            }
            else if (persistConversation)
            {
                IsLocalUpdate = true;
                RecreateNodesToProperties(SelectedConv);
            }

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
                ShortcutsEnabled = true
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
                    graphEditor?.Refresh();
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
                    graphEditor?.Refresh();
                    DialogueNode_SelectByIndex(node.Node.NodeCount, node.Node.IsReply);
                }
            }
            else
            {
                graphEditor?.Refresh();
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
                    "InterpLength" => node.Node.InterpLength.ToString("0.###", CultureInfo.InvariantCulture),
                    "ExportID" => node.Node.ExportID.ToString(),
                    "CameraIntimacy" => node.Node.CameraIntimacy.ToString(),
                    _ => ""
                };

                var textBox = new System.Windows.Forms.TextBox
                {
                    Text = currentValue,
                    BorderStyle = System.Windows.Forms.BorderStyle.None,
                    Multiline = false,
                    ShortcutsEnabled = true
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
                    graphEditor?.Refresh();
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
            else if (fieldInfo.FieldTag == "InterpLength")
            {
                bool parsedFloat = float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out float newFloat)
                                   || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out newFloat);
                if (parsedFloat && node.Node.InterpData != null && Math.Abs(node.Node.InterpLength - newFloat) > 0.0001f)
                {
                    node.Node.InterpLength = newFloat;
                    node.Node.InterpData.WriteProperty(new FloatProperty(newFloat, "InterpLength"));
                    changed = true;
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
                    case "CameraIntimacy":
                        if (node.Node.CameraIntimacy != newValue)
                        {
                            node.Node.CameraIntimacy = newValue;
                            node.Node.NodeProp.Properties.AddOrReplaceProp(new IntProperty(newValue, "nCameraIntimacy"));
                            changed = true;
                        }
                        break;
                    case "ExportID":
                        if (node.Node.ExportID != newValue)
                        {
                            UpdateNodeExportIdFromGraph(node.Node, newValue);
                            inlinePlotFieldEditClosing = false;
                            return;
                        }
                        break;
                }
            }

            if (changed)
            {
                IsLocalUpdate = true;
                RecreateNodesToProperties(SelectedConv);
                RefreshNodePlotSectionsInGraph(node.Node);
            }
            else
            {
                graphEditor?.Refresh();
                DialogueNode_SelectByIndex(node.Node.NodeCount, node.Node.IsReply);
            }
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
            RebuildInlineLinkEditorRows(node.Links.OrderBy(link => link.Order));
        }

        private void BuildInlineLinkEditorColumns()
        {
            CacheInlineLinkEditorColumnWidths();

            while (InlineLinkEditor_DataGrid.Columns.Count > 5)
            {
                InlineLinkEditor_DataGrid.Columns.RemoveAt(5);
            }

            var readOnlyBrush = (System.Windows.Media.Brush)FindResource("ReadOnlyColumnTextBrush");

            var ordinalColumn = new DataGridTextColumn
            {
                Header = "#",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.Ordinal)),
                Width = 30,
                IsReadOnly = true,
                Foreground = readOnlyBrush
            };
            ApplyInlineLinkEditorColumnWidth(ordinalColumn);
            InlineLinkEditor_DataGrid.Columns.Add(ordinalColumn);

            var linkColumn = new DataGridTextColumn
            {
                Header = "Link",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.NodeIDLink)),
                IsReadOnly = true,
                Width = 40,
                FontWeight = FontWeights.Heavy
            };
            ApplyInlineLinkEditorColumnWidth(linkColumn);
            InlineLinkEditor_DataGrid.Columns.Add(linkColumn);

            if (!inlineLinkEditorIsReply)
            {
                var guiStrRefColumn = new DataGridTextColumn
                {
                    Header = "GUI StrRef",
                    Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.ReplyStrRef)),
                    IsReadOnly = false,
                    Width = 70,
                    FontWeight = FontWeights.Bold
                };
                ApplyInlineLinkEditorColumnWidth(guiStrRefColumn);
                InlineLinkEditor_DataGrid.Columns.Add(guiStrRefColumn);

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
                ApplyInlineLinkEditorColumnWidth(choiceLineColumn);
                InlineLinkEditor_DataGrid.Columns.Add(choiceLineColumn);

                var categoryValues = GetInlineReplyCategoryValues();
                var categoryColumn = new DataGridTemplateColumn
                {
                    Header = "GUI Category",
                    Width = 150
                };

                var cellTemplate = new DataTemplate();
                var cellTextBlock = new FrameworkElementFactory(typeof(TextBlock));
                cellTextBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ReplyChoiceNode.RCategory)) { Converter = ReplyCategoryDisplayConverter });
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

                var comboItemTemplate = new DataTemplate();
                var comboItemTextBlock = new FrameworkElementFactory(typeof(TextBlock));
                comboItemTextBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = ReplyCategoryDisplayConverter });
                comboItemTextBlock.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding { Converter = ReplyCategoryBrushConverter });
                comboItemTemplate.VisualTree = comboItemTextBlock;
                comboBoxFactory.SetValue(ComboBox.ItemTemplateProperty, comboItemTemplate);

                comboBoxFactory.SetValue(ComboBox.IsDropDownOpenProperty, true);
                editTemplate.VisualTree = comboBoxFactory;
                categoryColumn.CellEditingTemplate = editTemplate;

                ApplyInlineLinkEditorColumnWidth(categoryColumn);
                InlineLinkEditor_DataGrid.Columns.Add(categoryColumn);
            }

            var targetCheckColumn = new DataGridTextColumn
            {
                Header = "Target Check",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.TgtFireCnd)),
                IsReadOnly = true,
                Width = 80,
                Foreground = readOnlyBrush
            };
            ApplyInlineLinkEditorColumnWidth(targetCheckColumn);
            InlineLinkEditor_DataGrid.Columns.Add(targetCheckColumn);

            var plotCheckColumn = new DataGridTextColumn
            {
                Header = "Plot Check",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.TgtCondition)),
                IsReadOnly = true,
                Width = 65,
                Foreground = readOnlyBrush
            };
            ApplyInlineLinkEditorColumnWidth(plotCheckColumn);
            InlineLinkEditor_DataGrid.Columns.Add(plotCheckColumn);

            var speakerColumn = new DataGridTextColumn
            {
                Header = "Speaker",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.TgtSpeaker)),
                IsReadOnly = true,
                Width = inlineLinkEditorIsReply ? 100 : 60,
                Foreground = readOnlyBrush
            };
            ApplyInlineLinkEditorColumnWidth(speakerColumn);
            InlineLinkEditor_DataGrid.Columns.Add(speakerColumn);

            var targetLineColumn = new DataGridTextColumn
            {
                Header = "Target Line",
                Binding = new System.Windows.Data.Binding(nameof(ReplyChoiceNode.TgtLine)),
                IsReadOnly = true,
                Foreground = readOnlyBrush
            };
            ApplyInlineLinkEditorColumnWidth(targetLineColumn);
            InlineLinkEditor_DataGrid.Columns.Add(targetLineColumn);

            var goToTargetColumn = new DataGridTemplateColumn
            {
                Header = "Go",
                Width = 50
            };

            var goToTargetTemplate = new DataTemplate();
            var goToTargetButton = new FrameworkElementFactory(typeof(Button));
            goToTargetButton.SetValue(Button.ContentProperty, "Go");
            goToTargetButton.SetValue(Button.MarginProperty, new Thickness(2));
            goToTargetButton.AddHandler(Button.ClickEvent, new RoutedEventHandler(InlineLinkEditor_GoToTarget_Click));
            goToTargetTemplate.VisualTree = goToTargetButton;
            goToTargetColumn.CellTemplate = goToTargetTemplate;
            ApplyInlineLinkEditorColumnWidth(goToTargetColumn);
            InlineLinkEditor_DataGrid.Columns.Add(goToTargetColumn);
        }

        private void CacheInlineLinkEditorColumnWidths()
        {
            foreach (var column in InlineLinkEditor_DataGrid.Columns.Skip(5))
            {
                if (column.Header is string header)
                {
                    inlineLinkEditorColumnWidths[GetInlineLinkEditorColumnWidthKey(header)] = column.Width;
                }
            }
        }

        private void ApplyInlineLinkEditorColumnWidth(DataGridColumn column)
        {
            if (column.Header is string header && inlineLinkEditorColumnWidths.TryGetValue(GetInlineLinkEditorColumnWidthKey(header), out DataGridLength width))
            {
                column.Width = width;
            }
        }

        private string GetInlineLinkEditorColumnWidthKey(string header)
        {
            return $"{(inlineLinkEditorIsReply ? "Reply" : "Entry")}::{header}";
        }

        private static ReplyChoiceNode CreateLinkSectionDivider(string label)
        {
            return new ReplyChoiceNode
            {
                IsDividerRow = true,
                NodeIDLink = label,
                Ordinal = string.Empty,
                TgtFireCnd = string.Empty,
                TgtLine = string.Empty,
                TgtSpeaker = string.Empty,
                ReplyLine = string.Empty
            };
        }

        private static string GetDialogueNodeLinkLabel(DiagNode node)
        {
            return $"{(node.Node.IsReply ? "R" : "E")}{node.Node.NodeCount}";
        }

        private static int GetLinkedTargetNodeUid(DiagNode sourceNode, ReplyChoiceNode link)
        {
            return sourceNode.Node.IsReply ? link.Index : link.Index + 1000;
        }

        private ReplyChoiceNode CreateIncomingLinkRow(DiagNode sourceNode, ReplyChoiceNode sourceLink)
        {
            return new ReplyChoiceNode(sourceLink)
            {
                IsIncomingConnection = true,
                NavigationNodeUid = sourceNode.NodeUID,
                Ordinal = "In",
                NodeIDLink = GetDialogueNodeLinkLabel(sourceNode),
                TgtFireCnd = sourceNode.Node.FiresConditional ? "Conditional" : "Bool",
                TgtCondition = sourceNode.Node.ConditionalOrBool,
                TgtLine = sourceNode.Node.Line,
                TgtSpeaker = sourceNode.Node.SpeakerTag?.SpeakerName ?? "Unknown"
            };
        }

        private static ReplyChoiceNode CreateIncomingStartRow(int startOrder, int targetNodeIndex)
        {
            return new ReplyChoiceNode
            {
                IsIncomingConnection = true,
                NavigationNodeUid = 2000 + targetNodeIndex,
                Ordinal = "In",
                NodeIDLink = $"{AddOrdinal(startOrder + 1)} Start",
                TgtLine = $"Start node -> E{targetNodeIndex}",
                TgtFireCnd = string.Empty,
                TgtSpeaker = string.Empty,
                ReplyLine = string.Empty
            };
        }

        private List<ReplyChoiceNode> GetInlineEditableLinks()
        {
            return InlineLinkEditorLinks.Where(link => link.IsEditableLink).OrderBy(link => link.Order).ToList();
        }

        private List<ReplyChoiceNode> GetIncomingInlineLinkRows(DiagNode node)
        {
            List<ReplyChoiceNode> incomingRows = [];

            if (!node.Node.IsReply)
            {
                foreach (var startLink in SelectedConv.StartingList.Where(kvp => kvp.Value == node.Node.NodeCount).OrderBy(kvp => kvp.Key))
                {
                    incomingRows.Add(CreateIncomingStartRow(startLink.Key, startLink.Value));
                }
            }

            foreach (var sourceNode in CurrentObjects.OfType<DiagNode>().OrderBy(o => o.NodeUID))
            {
                foreach (var sourceLink in sourceNode.Links.OrderBy(link => link.Order))
                {
                    if (GetLinkedTargetNodeUid(sourceNode, sourceLink) == node.NodeUID)
                    {
                        incomingRows.Add(CreateIncomingLinkRow(sourceNode, sourceLink));
                    }
                }
            }

            return incomingRows;
        }

        private static int GetCloneInsertionIndex(int itemCount, CloneInsertionPosition insertionPosition)
        {
            return insertionPosition switch
            {
                CloneInsertionPosition.Top => 0,
                CloneInsertionPosition.SecondTop => Math.Min(1, itemCount),
                CloneInsertionPosition.ThirdTop => Math.Min(2, itemCount),
                CloneInsertionPosition.ThirdBottom => Math.Max(itemCount - 2, 0),
                CloneInsertionPosition.SecondBottom => Math.Max(itemCount - 1, 0),
                _ => itemCount
            };
        }

        private static int GetCloneInsertionIndex(int itemCount, int insertionIndex)
        {
            return Math.Clamp(insertionIndex, 0, itemCount);
        }

        private static string GetCloneInsertionIndexDisplayText(int insertionIndex, int itemCount)
        {
            if (insertionIndex <= 0)
            {
                return "Top";
            }

            if (insertionIndex >= itemCount)
            {
                return "Bottom";
            }

            return $"{AddOrdinal(insertionIndex + 1)} from top";
        }

        private int? PromptForCloneInsertionIndex(string promptText, int itemCount)
        {
            var dialog = new Window
            {
                Title = "Clone Node",
                Width = 520,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            dialog.SetResourceReference(Window.BackgroundProperty, System.Windows.SystemColors.WindowBrushKey);
            dialog.SetResourceReference(Window.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(dialog);

            int? selectedInsertionIndex = null;

            Button CreateChoiceButton(int insertionIndex)
            {
                var button = new Button
                {
                    Content = GetCloneInsertionIndexDisplayText(insertionIndex, itemCount),
                    MinWidth = 120,
                    Margin = new Thickness(6),
                    Padding = new Thickness(10, 6, 10, 6)
                };
                button.Click += (_, _) =>
                {
                    selectedInsertionIndex = insertionIndex;
                    dialog.DialogResult = true;
                };
                return button;
            }

            var promptBlock = new TextBlock
            {
                Text = promptText,
                Margin = new Thickness(18, 18, 18, 12),
                TextWrapping = TextWrapping.Wrap
            };
            promptBlock.SetResourceReference(TextBlock.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);

            var optionPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 12)
            };
            for (int insertionIndex = 0; insertionIndex <= itemCount; insertionIndex++)
            {
                optionPanel.Children.Add(CreateChoiceButton(insertionIndex));
            }

            var cancelButton = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 120,
                Margin = new Thickness(6),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var rootPanel = new StackPanel();
            rootPanel.Children.Add(promptBlock);
            rootPanel.Children.Add(optionPanel);
            rootPanel.Children.Add(cancelButton);

            dialog.Content = rootPanel;
            return dialog.ShowDialog() == true ? selectedInsertionIndex : null;
        }

        internal int? PromptForLinkInsertionIndex(string promptText, int itemCount)
        {
            return PromptForCloneInsertionIndex(promptText, itemCount);
        }

        private (bool CloneLinks, int InsertionIndex)? PromptForCloneLinksOptions(int existingOutgoingLinkCount)
        {
            var dialog = new Window
            {
                Title = "Clone Node",
                Width = 520,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            dialog.SetResourceReference(Window.BackgroundProperty, System.Windows.SystemColors.WindowBrushKey);
            dialog.SetResourceReference(Window.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(dialog);

            bool cloneLinks = false;
            int selectedInsertionIndex = 0;

            var promptBlock = new TextBlock
            {
                Text = "Clone links as well?",
                Margin = new Thickness(18, 18, 18, 12),
                TextWrapping = TextWrapping.Wrap
            };
            promptBlock.SetResourceReference(TextBlock.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);

            var optionHeader = new TextBlock
            {
                Text = "Insert cloned links at:",
                Margin = new Thickness(18, 0, 18, 8),
                FontWeight = FontWeights.SemiBold
            };
            optionHeader.SetResourceReference(TextBlock.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);

            var checkBoxes = new List<CheckBox>();

            CheckBox CreateOptionCheckBox(int insertionIndex)
            {
                var checkBox = new CheckBox
                {
                    Content = GetCloneInsertionIndexDisplayText(insertionIndex, existingOutgoingLinkCount),
                    Margin = new Thickness(6),
                    IsChecked = insertionIndex == 0
                };
                checkBox.Checked += (_, _) =>
                {
                    selectedInsertionIndex = insertionIndex;
                    foreach (var otherCheckBox in checkBoxes)
                    {
                        if (!ReferenceEquals(otherCheckBox, checkBox))
                        {
                            otherCheckBox.IsChecked = false;
                        }
                    }
                };
                checkBox.Unchecked += (_, _) =>
                {
                    if (checkBoxes.All(cb => cb.IsChecked != true))
                    {
                        checkBox.IsChecked = true;
                    }
                };
                checkBoxes.Add(checkBox);
                return checkBox;
            }

            var optionPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 12)
            };
            for (int insertionIndex = 0; insertionIndex <= existingOutgoingLinkCount; insertionIndex++)
            {
                optionPanel.Children.Add(CreateOptionCheckBox(insertionIndex));
            }

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 18)
            };

            var yesButton = new Button
            {
                Content = "Yes",
                MinWidth = 120,
                Margin = new Thickness(6),
                Padding = new Thickness(10, 6, 10, 6)
            };
            yesButton.Click += (_, _) =>
            {
                cloneLinks = true;
                dialog.DialogResult = true;
            };

            var noButton = new Button
            {
                Content = "No",
                MinWidth = 120,
                Margin = new Thickness(6),
                Padding = new Thickness(10, 6, 10, 6)
            };
            noButton.Click += (_, _) =>
            {
                cloneLinks = false;
                dialog.DialogResult = true;
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 120,
                Margin = new Thickness(6),
                Padding = new Thickness(10, 6, 10, 6)
            };
            cancelButton.Click += (_, _) => dialog.DialogResult = false;

            buttonPanel.Children.Add(yesButton);
            buttonPanel.Children.Add(noButton);
            buttonPanel.Children.Add(cancelButton);

            var rootPanel = new StackPanel();
            rootPanel.Children.Add(promptBlock);
            rootPanel.Children.Add(optionHeader);
            rootPanel.Children.Add(optionPanel);
            rootPanel.Children.Add(buttonPanel);

            dialog.Content = rootPanel;
            return dialog.ShowDialog() == true
                ? (cloneLinks, selectedInsertionIndex)
                : null;
        }

        internal CloneDialogueNodeOptions PromptForCloneDialogueNodeOptions(string command, DiagNode diagNode)
        {
            if (command is not "CloneReply" and not "CloneEntry" || diagNode == null)
            {
                return new CloneDialogueNodeOptions();
            }

            int maxSourceOutgoingLinkCount = diagNode.InputEdges
                .Select(edge => edge.originator)
                .OfType<DiagNode>()
                .Select(node => node.Links.Count)
                .DefaultIfEmpty(0)
                .Max();

            var cloneLinkOptions = PromptForCloneLinksOptions(maxSourceOutgoingLinkCount);
            if (!cloneLinkOptions.HasValue)
            {
                return null;
            }

            var cloneLinkOptionsValue = cloneLinkOptions.Value;
            var inputEdges = new List<DiagEdEdge>();
            bool cloneStartNode = false;
            int clonedStartInsertionIndex = 0;

            if (cloneLinkOptionsValue.Item1)
            {
                foreach (var edge in diagNode.InputEdges)
                {
                    if (edge.originator is DiagNode)
                    {
                        inputEdges.Add(edge);
                    }
                    else if (command == "CloneEntry" && edge.originator is DStart)
                    {
                        cloneStartNode = true;
                    }
                }

                if (cloneStartNode)
                {
                    int? insertionPosition = PromptForCloneInsertionIndex(
                        "Choose where the cloned start node should be inserted in the start node list.",
                        SelectedConv?.StartingList.Count ?? 0);
                    if (!insertionPosition.HasValue)
                    {
                        return null;
                    }

                    clonedStartInsertionIndex = insertionPosition.Value;
                }
            }

            return new CloneDialogueNodeOptions
            {
                CloneLinks = cloneLinkOptionsValue.Item1,
                LinkInsertionIndex = cloneLinkOptionsValue.Item2,
                CloneStartNode = cloneStartNode,
                StartInsertionIndex = clonedStartInsertionIndex,
                InputEdges = inputEdges
            };
        }

        internal DialogueLinkEditDialogResult PromptForNewEntryLinkOptions(DiagNode startNode, DiagNode endNode)
        {
            if (startNode == null || endNode == null)
            {
                return null;
            }

            string selectedTarget = $"{endNode.Node.NodeCount}: {endNode.Node.LineStrRef} {endNode.Node.Line}";
            var existingLinks = startNode.Links.OrderBy(link => link.Order).Select(link => new ReplyChoiceNode(link)).ToList();
            var tempLinks = new List<ReplyChoiceNode>(existingLinks)
            {
                new ReplyChoiceNode(endNode.Node.NodeCount, string.Empty, 663399, EReplyCategory.REPLY_CATEGORY_DEFAULT, GlobalFindStrRefbyID(663399, Pcc))
                {
                    Order = existingLinks.Count
                }
            };

            var dialog = new Window
            {
                Title = "Create Link",
                Width = 760,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };
            dialog.SetResourceReference(Window.BackgroundProperty, System.Windows.SystemColors.WindowBrushKey);
            dialog.SetResourceReference(Window.ForegroundProperty, System.Windows.SystemColors.WindowTextBrushKey);
            CustomWindowChrome.ApplyCustomChrome(dialog);

            int selectedInsertionIndex = 0;
            int replyStrRef = 663399;
            string selectedCategory = EReplyCategory.REPLY_CATEGORY_DEFAULT.ToString();

            var rootPanel = new StackPanel { Margin = new Thickness(18) };

            var promptBlock = new TextBlock
            {
                Text = $"Choose where the new link to R{endNode.Node.NodeCount} should be inserted.",
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            };
            rootPanel.Children.Add(promptBlock);

            var optionHeader = new TextBlock
            {
                Text = "Insert new link at:",
                Margin = new Thickness(0, 0, 0, 8),
                FontWeight = FontWeights.SemiBold
            };
            rootPanel.Children.Add(optionHeader);

            var checkBoxes = new List<CheckBox>();
            CheckBox CreateOptionCheckBox(int insertionIndex)
            {
                var checkBox = new CheckBox
                {
                    Content = GetCloneInsertionIndexDisplayText(insertionIndex, existingLinks.Count),
                    Margin = new Thickness(6),
                    IsChecked = insertionIndex == 0
                };
                checkBox.Checked += (_, _) =>
                {
                    selectedInsertionIndex = insertionIndex;
                    foreach (var otherCheckBox in checkBoxes)
                    {
                        if (!ReferenceEquals(otherCheckBox, checkBox))
                        {
                            otherCheckBox.IsChecked = false;
                        }
                    }
                };
                checkBox.Unchecked += (_, _) =>
                {
                    if (checkBoxes.All(cb => cb.IsChecked != true))
                    {
                        checkBox.IsChecked = true;
                    }
                };
                checkBoxes.Add(checkBox);
                return checkBox;
            }

            var optionPanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            for (int insertionIndex = 0; insertionIndex <= existingLinks.Count; insertionIndex++)
            {
                optionPanel.Children.Add(CreateOptionCheckBox(insertionIndex));
            }
            rootPanel.Children.Add(optionPanel);

            var tlkPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            tlkPanel.Children.Add(new TextBlock
            {
                Text = "Dialogue wheel TLK string reference",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var replyStrRefTextBox = new TextBox { Text = replyStrRef.ToString(CultureInfo.InvariantCulture) };
            var replyPreviewTextBlock = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            void UpdateReplyPreview()
            {
                if (int.TryParse(replyStrRefTextBox.Text, out int parsedStrRef) && parsedStrRef > 0)
                {
                    replyPreviewTextBlock.Text = RemoveWrappingQuotes(GlobalFindStrRefbyID(parsedStrRef, Pcc));
                }
                else
                {
                    replyPreviewTextBlock.Text = "No Data";
                }
            }
            replyStrRefTextBox.TextChanged += (_, _) => UpdateReplyPreview();
            UpdateReplyPreview();
            tlkPanel.Children.Add(replyStrRefTextBox);
            tlkPanel.Children.Add(replyPreviewTextBlock);
            rootPanel.Children.Add(tlkPanel);

            var categoryPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            categoryPanel.Children.Add(new TextBlock
            {
                Text = "Dialogue wheel category",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var categoryComboBox = new ComboBox();
            foreach (var category in GetInlineReplyCategoryValues())
            {
                var categoryBrush = ReplyCategoryBrushConverter.Convert(category, typeof(System.Windows.Media.Brush), null, CultureInfo.CurrentCulture) as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Black;
                categoryComboBox.Items.Add(new ComboBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = GetReplyCategoryDisplayText(category),
                        Foreground = categoryBrush
                    },
                    Tag = category.ToString(),
                    Foreground = categoryBrush
                });
            }
            categoryComboBox.SelectedItem = categoryComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectedCategory, StringComparison.Ordinal));
            categoryPanel.Children.Add(categoryComboBox);
            rootPanel.Children.Add(categoryPanel);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button
            {
                Content = "OK",
                IsDefault = true,
                MinWidth = 120,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            okButton.Click += (_, _) =>
            {
                if (!int.TryParse(replyStrRefTextBox.Text, out replyStrRef) || replyStrRef <= 0)
                {
                    System.Windows.MessageBox.Show(dialog, "The string reference must be a positive whole number.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                selectedCategory = (categoryComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
                if (string.IsNullOrWhiteSpace(selectedCategory))
                {
                    System.Windows.MessageBox.Show(dialog, "Select a dialogue wheel category.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                dialog.DialogResult = true;
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 120,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 6, 10, 6)
            };
            cancelButton.Click += (_, _) => dialog.DialogResult = false;

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            rootPanel.Children.Add(buttonPanel);

            dialog.Content = rootPanel;
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            return new DialogueLinkEditDialogResult(selectedTarget, replyStrRef, selectedCategory, selectedInsertionIndex);
        }

        private void CloneIncomingLinkToNode(DiagEdEdge sourceEdge, DiagNode targetNode, bool insertAtTop)
        {
            CloneIncomingLinkToNode(sourceEdge, targetNode, insertAtTop ? 0 : int.MaxValue);
        }

        private void CloneIncomingLinkToNode(DiagEdEdge sourceEdge, DiagNode targetNode, int insertionIndex)
        {
            if (sourceEdge?.originator is not DiagNode sourceNode || targetNode == null)
            {
                return;
            }

            int sourceLinkIndex = sourceNode.Outlinks.FindIndex(outlink => outlink.Edges.Contains(sourceEdge));
            if (sourceLinkIndex < 0)
            {
                return;
            }

            if (sourceNode.Node.IsReply)
            {
                var entryList = sourceNode.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList")
                                ?? new ArrayProperty<IntProperty>("EntryList");
                var clonedLink = sourceLinkIndex < entryList.Count
                    ? (IntProperty)entryList[sourceLinkIndex].DeepClone()
                    : new IntProperty(targetNode.NodeID);
                clonedLink.Value = targetNode.NodeID;
                entryList.Insert(GetCloneInsertionIndex(entryList.Count, insertionIndex), clonedLink);
                sourceNode.NodeProp.Properties.AddOrReplaceProp(entryList);
            }
            else
            {
                var replyList = sourceNode.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew")
                                ?? new ArrayProperty<StructProperty>("ReplyListNew");
                StructProperty clonedLink;
                if (sourceLinkIndex < replyList.Count)
                {
                    clonedLink = (StructProperty)replyList[sourceLinkIndex].DeepClone();
                    clonedLink.Properties.AddOrReplaceProp(new IntProperty(targetNode.NodeID - 1000, "nIndex"));
                }
                else
                {
                    clonedLink = new StructProperty("BioDialogReplyListDetails", new PropertyCollection
                    {
                        new IntProperty(targetNode.NodeID - 1000, "nIndex"),
                        new StringRefProperty(663399, "srParaphrase"),
                        new StrProperty(string.Empty, "sParaphrase"),
                        new EnumProperty("REPLY_CATEGORY_DEFAULT", "EReplyCategory", Pcc.Game, "Category"),
                        new NoneProperty()
                    });
                }

                replyList.Insert(GetCloneInsertionIndex(replyList.Count, insertionIndex), clonedLink);
                sourceNode.NodeProp.Properties.AddOrReplaceProp(replyList);
            }
        }

        private void RebuildInlineLinkEditorRows(IEnumerable<ReplyChoiceNode> editableLinks = null, ReplyChoiceNode selectedLink = null)
        {
            if (inlineLinkEditorNode == null)
            {
                InlineLinkEditorLinks.ClearEx();
                return;
            }

            var outgoingLinks = (editableLinks ?? GetInlineEditableLinks()).OrderBy(link => link.Order).ToList();
            var incomingRows = GetIncomingInlineLinkRows(inlineLinkEditorNode);

            InlineLinkEditorLinks.ClearEx();

            if (incomingRows.Count > 0)
            {
                InlineLinkEditorLinks.Add(CreateLinkSectionDivider("Incoming"));
                foreach (var incomingRow in incomingRows)
                {
                    InlineLinkEditorLinks.Add(incomingRow);
                }
            }

            InlineLinkEditorLinks.Add(CreateLinkSectionDivider("Outgoing"));
            foreach (var outgoingLink in outgoingLinks)
            {
                ParseInlineLink(outgoingLink);
                InlineLinkEditorLinks.Add(outgoingLink);
            }

            if (selectedLink != null)
            {
                InlineLinkEditor_DataGrid.SelectedItem = InlineLinkEditorLinks.FirstOrDefault(link => ReferenceEquals(link, selectedLink));
            }
        }

        private void ParseInlineLink(ReplyChoiceNode link)
        {
            string nodePrefix = inlineLinkEditorIsReply ? "E" : "R";
            int targetUID = inlineLinkEditorIsReply ? link.Index : link.Index + 1000;
            link.IsIncomingConnection = false;
            link.IsDividerRow = false;
            link.NavigationNodeUid = targetUID;
            link.NodeIDLink = $"{nodePrefix}{link.Index}";
            link.ReplyLine = GetDisplayTlkText(link.ReplyStrRef, Pcc);

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
            link.TgtSpeaker = targetNode.Node.SpeakerTag?.SpeakerName ?? "Unknown";
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

            var editableLinks = GetInlineEditableLinks();
            WriteEditableLinksToNodeProperties(inlineLinkEditorNode, editableLinks);

            int nodeCount = inlineLinkEditorNode.Node.NodeCount;
            bool isReply = inlineLinkEditorNode.Node.IsReply;
            inlineLinkEditorNeedsSave = false;
            PushLocalGraphChanges(inlineLinkEditorNode);

            if (focusEditedNode)
            {
                var refreshedNode = SelectDialogueNodeByIndex(nodeCount, isReply);
                LoadInlineLinkEditor(refreshedNode);
                BottomViewportTabControl.SelectedItem = LinkEditorTab;
            }
        }

        private void InlineLinkEditor_Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ReplyChoiceNode { IsEditableLink: true } link)
            {
                InlineLinkEditor_DataGrid.SelectedItem = link;
                var editableLinks = GetInlineEditableLinks();
                editableLinks.Remove(link);
                ReOrderInlineLinkEditorLinks();
                inlineLinkEditorNeedsSave = true;
            }
        }

        private void InlineLinkEditor_Clone_Click(object sender, RoutedEventArgs e)
        {
            if (InlineLinkEditor_DataGrid.SelectedItem is not ReplyChoiceNode { IsEditableLink: true } selectedLink)
            {
                return;
            }

            var editableLinks = GetInlineEditableLinks();
            editableLinks.Add(new ReplyChoiceNode(selectedLink) { Order = editableLinks.Count + 1 });
            RebuildInlineLinkEditorRows(editableLinks, selectedLink);
            inlineLinkEditorNeedsSave = true;
        }

        private void InlineLinkEditor_Move_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ReplyChoiceNode { IsEditableLink: true } selectedLink)
            {
                InlineLinkEditor_DataGrid.SelectedItem = selectedLink;
            }

            if (InlineLinkEditor_DataGrid.SelectedIndex < 0 || sender is not Button button || button.CommandParameter is not string direction)
            {
                return;
            }

            if (InlineLinkEditor_DataGrid.SelectedItem is not ReplyChoiceNode { IsEditableLink: true } moveLink)
            {
                return;
            }

            var editableLinks = GetInlineEditableLinks();
            int moveLinkID = editableLinks.IndexOf(moveLink);
            if ((moveLinkID == 0 && direction is "Up" or "Top") || (moveLinkID >= editableLinks.Count - 1 && direction is "Down" or "Bottom"))
            {
                return;
            }

            int numSwaps = direction switch
            {
                "Top" => moveLinkID,
                "Bottom" => editableLinks.Count - 1 - moveLinkID,
                _ => 1
            };

            int swapDir = direction is "Up" or "Top" ? -1 : 1;
            for (int i = 0; Math.Abs(i) < numSwaps; i += swapDir)
            {
                ReplyChoiceNode moveNode = editableLinks[moveLinkID];
                ReplyChoiceNode swapNode = editableLinks[moveLinkID + i + swapDir];
                (moveNode.Order, swapNode.Order) = (swapNode.Order, moveNode.Order);
            }

            ReOrderInlineLinkEditorLinks();
            inlineLinkEditorNeedsSave = true;
        }

        private void InlineLinkEditor_Edit_Click(object sender, RoutedEventArgs e)
        {
            EditInlineSelectedLink();
        }

        private void InlineLinkEditor_GoToTarget_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ReplyChoiceNode { HasNavigationTarget: true } targetLink)
            {
                InlineLinkEditor_DataGrid.SelectedItem = targetLink;
                SelectGraphObjectByUid(targetLink.NavigationNodeUid, centerView: true);
                BottomViewportTabControl.SelectedItem = LinkEditorTab;
            }
        }

        private void InlineLinkEditor_DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            EditInlineSelectedLink();
        }

        private void EditInlineSelectedLink()
        {
            if (InlineLinkEditor_DataGrid.SelectedItem is not ReplyChoiceNode { IsEditableLink: true } editLink)
            {
                return;
            }

            var editableLinks = GetInlineEditableLinks();
            if (!TryEditDialogueLink(editLink, inlineLinkEditorIsReply, editableLinks))
            {
                return;
            }

            ParseInlineLink(editLink);
            ReOrderInlineLinkEditorLinks(editLink);
            inlineLinkEditorNeedsSave = true;
            SaveInlineLinkEditorChanges(focusEditedNode: false);
        }

        public void EditGraphOutgoingLink(DiagNode node, int linkIndex)
        {
            if (node == null || linkIndex < 0 || linkIndex >= node.Links.Count)
            {
                return;
            }

            DialogueNode_Selected(node);

            var editLink = node.Links[linkIndex];
            var editableLinks = node.Links.OrderBy(link => link.Order).ToList();
            if (!TryEditDialogueLink(editLink, node.Node.IsReply, editableLinks))
            {
                return;
            }

            WriteEditableLinksToNodeProperties(node, editableLinks);
            PushLocalGraphChanges(node);

            if (inlineLinkEditorNode?.NodeUID == node.NodeUID)
            {
                LoadInlineLinkEditor(node);
                BottomViewportTabControl.SelectedItem = LinkEditorTab;
            }
        }

        private bool TryEditDialogueLink(ReplyChoiceNode editLink, bool sourceNodeIsReply, IList<ReplyChoiceNode> editableLinks)
        {
            if (editLink is not { IsEditableLink: true })
            {
                return false;
            }

            if (editableLinks == null || !editableLinks.Contains(editLink))
            {
                editableLinks = [editLink];
            }

            foreach (var link in editableLinks)
            {
                ParseInlineLink(link);
            }

            var links = new List<string>();
            int currentTarget = editLink.Index;
            if (sourceNodeIsReply)
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
                return false;
            }

            if (!DialogueLinkEditDialog.TryEditLink(
                    this,
                    links,
                    currentSelection,
                    editableLinks.Select(link => DialogueLinkEditDialog.CreateOrderDisplayItem(link, sourceNodeIsReply)),
                    editableLinks.IndexOf(editLink),
                    !sourceNodeIsReply,
                    editLink.ReplyStrRef,
                    id => GlobalFindStrRefbyID(id, Pcc),
                    GetInlineReplyCategoryValues().Select(v => v.ToString()),
                    editLink.RCategory.ToString(),
                    out var dialogResult))
            {
                return false;
            }

            editLink.Index = links.FindIndex(dialogResult.SelectedTarget.Equals);
            if (!sourceNodeIsReply)
            {
                editLink.ReplyStrRef = dialogResult.ReplyStrRef;
                editLink.RCategory = Enums.Parse<EReplyCategory>(dialogResult.SelectedCategory);
            }

            MoveEditableLinkToOrder(editableLinks, editLink, dialogResult.SelectedOrder);

            return true;
        }

        internal static void MoveEditableLinkToOrder(IList<ReplyChoiceNode> editableLinks, ReplyChoiceNode editedLink, int targetOrder)
        {
            if (editableLinks == null || editedLink == null || editableLinks.Count == 0)
            {
                return;
            }

            var orderedLinks = editableLinks.OrderBy(link => link.Order).ToList();
            int currentIndex = orderedLinks.IndexOf(editedLink);
            if (currentIndex < 0)
            {
                return;
            }

            targetOrder = Math.Clamp(targetOrder, 0, orderedLinks.Count - 1);
            if (currentIndex != targetOrder)
            {
                orderedLinks.RemoveAt(currentIndex);
                orderedLinks.Insert(targetOrder, editedLink);
            }

            for (int i = 0; i < orderedLinks.Count; i++)
            {
                orderedLinks[i].Order = i;
                orderedLinks[i].Ordinal = AddOrdinal(i + 1);
            }
        }

        internal static string BuildLinkOrderDisplayText(ReplyChoiceNode link, bool sourceNodeIsReply)
        {
            if (link == null)
            {
                return string.Empty;
            }

            string summary = sourceNodeIsReply
                ? link.TgtLine
                : $"{link.ReplyStrRef} {link.ReplyLine}";

            string categoryLabel = null;
            if (!sourceNodeIsReply)
            {
                categoryLabel = $"{GetReplyCategoryAcronym(link.RCategory)} - {GetReplyCategoryDisplayText(link.RCategory)}";
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = link.TgtLine;
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = link.NodeIDLink;
            }

            summary = Regex.Replace(summary ?? string.Empty, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(categoryLabel)
                ? $"{AddOrdinal(link.Order + 1)} - {link.NodeIDLink}: {summary}"
                : $"{AddOrdinal(link.Order + 1)} - {link.NodeIDLink} [{categoryLabel}]: {summary}";
        }

        public static string GetReplyCategoryAcronym(EReplyCategory category)
        {
            return category switch
            {
                EReplyCategory.REPLY_CATEGORY_DEFAULT => "D",
                EReplyCategory.REPLY_CATEGORY_AGREE => "A",
                EReplyCategory.REPLY_CATEGORY_DISAGREE => "DI",
                EReplyCategory.REPLY_CATEGORY_FRIENDLY => "F",
                EReplyCategory.REPLY_CATEGORY_HOSTILE => "H",
                EReplyCategory.REPLY_CATEGORY_INVESTIGATE => "I",
                EReplyCategory.REPLY_CATEGORY_RENEGADE_INTERRUPT => "RI",
                EReplyCategory.REPLY_CATEGORY_PARAGON_INTERRUPT => "PI",
                _ => "D"
            };
        }

        private void WriteEditableLinksToNodeProperties(DiagNode node, IEnumerable<ReplyChoiceNode> editableLinks)
        {
            if (node == null)
            {
                return;
            }

            var orderedLinks = editableLinks?.OrderBy(link => link.Order).ToList() ?? [];
            if (node.Node.IsReply)
            {
                node.NodeProp.Properties.AddOrReplaceProp(new ArrayProperty<IntProperty>(orderedLinks.Select(link => new IntProperty(link.Index)), "EntryList"));
            }
            else
            {
                node.NodeProp.Properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(orderedLinks.Select(link =>
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
        }

        private void ReOrderInlineLinkEditorLinks(ReplyChoiceNode selectedLink = null)
        {
            var editableLinks = GetInlineEditableLinks().OrderBy(link => link.Order).ToList();
            int order = 0;
            foreach (var link in editableLinks)
            {
                link.Order = order;
                link.Ordinal = AddOrdinal(link.Order + 1);
                order++;
            }

            RebuildInlineLinkEditorRows(editableLinks, selectedLink);
            InlineLinkEditor_DataGrid.Items.Refresh();
        }

        private void InlineLinkEditor_DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e.Row.Item is ReplyChoiceNode link)
                {
                    if (!link.IsEditableLink)
                    {
                        return;
                    }

                    ParseInlineLink(link);
                    inlineLinkEditorNeedsSave = true;
                    ReOrderInlineLinkEditorLinks(link);
                    SaveInlineLinkEditorChanges(focusEditedNode: false);
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

        private sealed class ReplyCategoryToDisplayConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                var category = value is EReplyCategory eReplyCategory ? eReplyCategory : EReplyCategory.REPLY_CATEGORY_DEFAULT;
                return GetReplyCategoryDisplayText(category);
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }

        internal DialogueNodeExtended CloneDialogueNodeInPlace(string command, CloneDialogueNodeOptions cloneOptions = null)
        {
            if (command == "AddReply")
            {
                AddDialogueNodeInPlace(isReply: true);
                return SelectedDialogueNode;
            }

            if (command == "AddEntry")
            {
                AddDialogueNodeInPlace(isReply: false);
                return SelectedDialogueNode;
            }

            if (SelectedObjects.Count is 0 || SelectedObjects[0] is not DiagNode diagNode)
            {
                return null;
            }

            float newX = diagNode.OffsetX + 100;
            float newY = diagNode.OffsetY + 150;
            int newIndex = 0;

            DiagNode node = null;
            bool isReply = false;
            bool cloneLinks = false;
            int clonedLinkInsertionIndex = 0;
            bool cloneStartNode = false;
            int clonedStartInsertionIndex = 0;
            List<DiagEdEdge> inputEdges = [];
            SpeakerExtended replacementSpeaker = null;
            int? nodeInsertionIndex = null;
            if (command is "CloneReply" or "CloneEntry")
            {
                cloneOptions ??= PromptForCloneDialogueNodeOptions(command, diagNode);
                if (cloneOptions == null)
                {
                    return null;
                }

                if (cloneOptions.CloneLinks)
                {
                    cloneLinks = true;
                    clonedLinkInsertionIndex = cloneOptions.LinkInsertionIndex;
                    cloneStartNode = cloneOptions.CloneStartNode;
                    clonedStartInsertionIndex = cloneOptions.StartInsertionIndex;
                    inputEdges = cloneOptions.InputEdges ?? [];
                }

                replacementSpeaker = cloneOptions.ReplacementSpeaker;
                nodeInsertionIndex = cloneOptions.NodeInsertionIndex;
            }

            IsLocalUpdate = true;
            panToSelection = false;
            graphEditor.Enabled = false;
            graphEditor.UseWaitCursor = true;

            if (command == "CloneReply")
            {
                isReply = true;
                int originalReplyCount = SelectedConv.ReplyList.Count;
                newIndex = GetCloneInsertionIndex(originalReplyCount, nodeInsertionIndex ?? originalReplyCount);
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
                    props.AddOrReplaceProp(op.DeepClone());
                }
                props.AddOrReplaceProp(new NoneProperty());
                replyprop.Insert(newIndex, new StructProperty(typeName, props));
                var nodeExtended = SelectedConv.ParseSingleLine(replyprop[newIndex], newIndex, isReply, TLKLookup);
                nodeExtended.InterpData = SelectedDialogueNode.InterpData;
                nodeExtended.InterpLength = SelectedDialogueNode.InterpLength;
                nodeExtended.Line = SelectedDialogueNode.Line;
                nodeExtended.SpeakerIndex = replacementSpeaker?.SpeakerID ?? SelectedDialogueNode.SpeakerIndex;
                nodeExtended.SpeakerTag = replacementSpeaker ?? SelectedDialogueNode.SpeakerTag;
                nodeExtended.WwiseStream_Female = SelectedDialogueNode.WwiseStream_Female;
                nodeExtended.WwiseStream_Male = SelectedDialogueNode.WwiseStream_Male;
                nodeExtended.FaceFX_Female = SelectedDialogueNode.FaceFX_Female;
                nodeExtended.FaceFX_Male = SelectedDialogueNode.FaceFX_Male;
                SelectedConv.ReplyList.Insert(newIndex, nodeExtended);
                if (newIndex < originalReplyCount)
                {
                    ShiftConversationLinksForInsertedNode(insertedNodeIsReply: true, newIndex);
                }
                ReindexConversationNodeCounts();
                RecreateNodesToProperties(SelectedConv);
                node = new DiagNodeReply(this, SelectedConv.ReplyList[newIndex], newX, newY, graphEditor);
            }
            else if (command == "CloneEntry")
            {
                int originalEntryCount = SelectedConv.EntryList.Count;
                newIndex = GetCloneInsertionIndex(originalEntryCount, nodeInsertionIndex ?? originalEntryCount);
                var entryprop = SelectedConv.BioConvo.GetProp<ArrayProperty<StructProperty>>("m_EntryList");
                string typeName = "BioDialogEntryNode";
                PropertyCollection props = new();
                foreach (var op in SelectedDialogueNode.NodeProp.Properties)
                {
                    if (!cloneLinks && op.Name.Name == "ReplyListNew")
                    {
                        props.AddOrReplaceProp(new ArrayProperty<StructProperty>(op.Name));
                        continue;
                    }
                    props.AddOrReplaceProp(op.DeepClone());
                }
                if (replacementSpeaker != null)
                {
                    props.AddOrReplaceProp(new IntProperty(replacementSpeaker.SpeakerID, "nSpeakerIndex"));
                }
                props.AddOrReplaceProp(new NoneProperty());
                entryprop.Insert(newIndex, new StructProperty(typeName, props));
                var nodeExtended = SelectedConv.ParseSingleLine(entryprop[newIndex], newIndex, isReply, TLKLookup);
                nodeExtended.InterpData = SelectedDialogueNode.InterpData;
                nodeExtended.InterpLength = SelectedDialogueNode.InterpLength;
                nodeExtended.Line = SelectedDialogueNode.Line;
                nodeExtended.SpeakerIndex = replacementSpeaker?.SpeakerID ?? SelectedDialogueNode.SpeakerIndex;
                nodeExtended.SpeakerTag = replacementSpeaker ?? SelectedDialogueNode.SpeakerTag;
                nodeExtended.WwiseStream_Female = SelectedDialogueNode.WwiseStream_Female;
                nodeExtended.WwiseStream_Male = SelectedDialogueNode.WwiseStream_Male;
                nodeExtended.FaceFX_Female = SelectedDialogueNode.FaceFX_Female;
                nodeExtended.FaceFX_Male = SelectedDialogueNode.FaceFX_Male;
                SelectedConv.EntryList.Insert(newIndex, nodeExtended);
                if (newIndex < originalEntryCount)
                {
                    ShiftConversationLinksForInsertedNode(insertedNodeIsReply: false, newIndex);
                }
                ReindexConversationNodeCounts();
                RecreateNodesToProperties(SelectedConv);
                node = new DiagNodeEntry(this, SelectedConv.EntryList[newIndex], newX, newY, graphEditor);
            }
            AddDialogueNodeToGraphInPlace(node, new PointF(newX, newY), centerView: false);
            DialogueNode_SelectByIndex(newIndex, isReply);
            graphEditor.Enabled = true;
            graphEditor.UseWaitCursor = false;
            graphEditor.Camera.AnimateViewToCenterBounds(node.GlobalFullBounds, false, 500);
            if (cloneLinks)
            {
                using var suppressedPackageUpdates = SuppressPackageUpdates();
                foreach (var inputEdge in inputEdges)
                {
                    CloneIncomingLinkToNode(inputEdge, node, clonedLinkInsertionIndex);
                    if (inputEdge.originator is DiagNode inputNode)
                    {
                        PushLocalGraphChanges(inputNode, persistConversation: false);
                    }
                }

                RecreateNodesToProperties(SelectedConv);
                PushConvoToFile(SelectedConv);

                if (cloneStartNode)
                {
                    AddStartNodeForEntry(newIndex, clonedStartInsertionIndex);
                    DialogueNode_SelectByIndex(newIndex, isReply);
                    graphEditor.Camera.AnimateViewToCenterBounds(node.GlobalFullBounds, false, 500);
                }
            }
            else
            {
                PushConvoToFile(SelectedConv);
            }

            return node?.Node;
        }

        private void DialogueNode_Add(object obj)
        {
            if (obj is string command)
            {
                CloneDialogueNodeInPlace(command);
            }
        }
        private void DialogueNode_DeleteLinks(object obj)
        {
            if (SelectedDialogueNode == null || SelectedConv == null)
            {
                return;
            }

            bool linksCleared = false;
            if (SelectedDialogueNode.IsReply)
            {
                var entrylinklist = SelectedDialogueNode.NodeProp.GetProp<ArrayProperty<IntProperty>>("EntryList");
                if (entrylinklist != null && entrylinklist.Count > 0)
                {
                    entrylinklist.Clear();
                    linksCleared = true;
                }
            }
            else
            {
                var replylinklist = SelectedDialogueNode.NodeProp.GetProp<ArrayProperty<StructProperty>>("ReplyListNew");
                if (replylinklist != null && replylinklist.Count > 0)
                {
                    replylinklist.Clear();
                    linksCleared = true;
                }
            }

            if (!linksCleared)
            {
                return;
            }

            IsLocalUpdate = true;
            RecreateNodesToProperties(SelectedConv);
            RefreshNodeInGraph(SelectedDialogueNode, persistConversation: false);

            if (SelectedObjects.FirstOrDefault() is DiagNode selectedNode && inlineLinkEditorNode?.NodeUID == selectedNode.NodeUID)
            {
                LoadInlineLinkEditor(selectedNode);
            }
        }

        private void CopyOutgoingConnections_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedObjects.FirstOrDefault() is not DiagNode selectedNode)
            {
                return;
            }

            copiedOutgoingConnectionsAreReplyNode = selectedNode.Node.IsReply;
            copiedOutgoingConnections = selectedNode.Links
                .OrderBy(link => link.Order)
                .Select(link => new ReplyChoiceNode(link) { Order = link.Order })
                .ToList();

            StatusBar_OtherText.Text = $"Copied {copiedOutgoingConnections.Count} outgoing connection(s).";
        }

        private void PasteOutgoingConnections_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedObjects.FirstOrDefault() is not DiagNode selectedNode)
            {
                return;
            }

            if (copiedOutgoingConnections == null)
            {
                MessageBox.Show("No outgoing connections have been copied yet.", "Dialogue Editor");
                return;
            }

            if (copiedOutgoingConnectionsAreReplyNode != selectedNode.Node.IsReply)
            {
                MessageBox.Show("Outgoing connections can only be pasted onto the same node type.", "Dialogue Editor");
                return;
            }

            if (selectedNode.Node.IsReply)
            {
                selectedNode.NodeProp.Properties.AddOrReplaceProp(new ArrayProperty<IntProperty>(copiedOutgoingConnections.Select(link => new IntProperty(link.Index)), "EntryList"));
            }
            else
            {
                selectedNode.NodeProp.Properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(copiedOutgoingConnections.Select(link =>
                    new StructProperty("BioDialogReplyListDetails", new PropertyCollection
                    {
                        new IntProperty(link.Index, "nIndex"),
                        new StringRefProperty(link.ReplyStrRef, "srParaphrase"),
                        new StrProperty(link.Paraphrase ?? string.Empty, "sParaphrase"),
                        new EnumProperty(link.RCategory.ToString(), "EReplyCategory", Pcc.Game, "Category"),
                        new NoneProperty()
                    })
                ), "ReplyListNew"));
            }

            PushLocalGraphChanges(selectedNode);
            LoadInlineLinkEditor(selectedNode);
            BottomViewportTabControl.SelectedItem = LinkEditorTab;
            StatusBar_OtherText.Text = $"Pasted {copiedOutgoingConnections.Count} outgoing connection(s).";
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
            var deleteGraphNode = CurrentObjects.OfType<DiagNode>().FirstOrDefault(n => n.Node.NodeCount == deleteNode.NodeCount && n.Node.IsReply == deleteNode.IsReply);
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

            ReindexConversationNodeCounts();
            IsLocalUpdate = true;
            RecreateNodesToProperties(SelectedConv);

            if (deleteGraphNode != null)
            {
                RemoveGraphObject(deleteGraphNode);
            }

            RebuildGraphInPlace(rebuildStarts: !deleteNode.IsReply);
            Start_ListBoxUpdate();
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

                SelectedConv.StageDirections.Add(new StageDirection(strRef, GetDisplayTlkText(strRef, Pcc), "Add direction"));
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
                UpdateSelectedConnectionHighlighting();
                graphEditor?.Refresh();
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
                UpdateSelectedConnectionHighlighting();
                graphEditor?.Refresh();
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

            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                ClearGraphSelection();
            }
            else if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                if (FindResource("graphBackgroundContextMenu") is ContextMenu contextMenu)
                {
                    contextMenu.DataContext = this;
                    contextMenu.IsOpen = true;
                    graphEditor.DisableDragging();
                    e.Handled = true;
                }
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
                    if (contextMenu.GetChild("replyPasteOutgoingConnectionsMenuItem") is MenuItem replyPasteMenuItem)
                    {
                        replyPasteMenuItem.IsEnabled = copiedOutgoingConnections != null && copiedOutgoingConnectionsAreReplyNode;
                    }
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
                    if (contextMenu.GetChild("entryPasteOutgoingConnectionsMenuItem") is MenuItem entryPasteMenuItem)
                    {
                        entryPasteMenuItem.IsEnabled = copiedOutgoingConnections != null && !copiedOutgoingConnectionsAreReplyNode;
                    }
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
                case "Toggle_HideUnrelatedConnectionsOnSelection":
                    hideUnrelatedConnectionsOnSelection = HideUnrelatedConnectionsOnSelection_MenuItem.IsChecked;
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
            hideUnrelatedConnectionsOnSelection = HideUnrelatedConnectionsOnSelection_MenuItem.IsChecked;

            if (CurrentObjects.Any() && ((needsRegen && SaveViewMode == ESaveViewMode.AutoGenerate) || forceRegen))
            {
                RefreshView();
            }

            UpdateSelectedConnectionHighlighting();
            graphEditor?.Refresh();
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
            ShowWindowAtFront(p);
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
                    if (TryGetBaseConversationReferenceTarget(Level, out string basePackagePath, out int basePackageTargetUIndex))
                    {
                        OpenInToolkit("PackageEditor", basePackageTargetUIndex, basePackagePath);
                    }
                    else
                    {
                        OpenInToolkit("PackageEditor", 0, Level);
                    }
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
                case "PackEdFaceFXSpkrM":
                    if (SelectedSpeaker.FaceFX_Male != null)
                    {
                        OpenInToolkit("PackageEditor", SelectedSpeaker.FaceFX_Male.UIndex);
                    }
                    break;
                case "PackEdFaceFXSpkrF":
                    if (SelectedSpeaker.FaceFX_Female != null)
                    {
                        OpenInToolkit("PackageEditor", SelectedSpeaker.FaceFX_Female.UIndex);
                    }
                    break;
                case "SeqEdLvl":
                    if (TryGetBaseConversationReferenceTarget(Level, out string baseSequencePath, out int baseSequenceTargetUIndex))
                    {
                        OpenInToolkit("SequenceEditor", baseSequenceTargetUIndex, baseSequencePath);
                    }
                    else
                    {
                        OpenInToolkit("SequenceEditor", 0, Level);
                    }
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
                    if (SelectedDialogueNode.SpeakerTag?.FaceFX_Male != null)
                    {
                        OpenInToolkit("FaceFXEditor", SelectedDialogueNode.SpeakerTag.FaceFX_Male.UIndex, null, SelectedDialogueNode.FaceFX_Male);
                    }
                    break;
                case "FaceFXLineF":
                    if (SelectedDialogueNode.SpeakerTag?.FaceFX_Female != null)
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
                "FaceFXLineM" => SelectedDialogueNode?.SpeakerTag?.FaceFX_Male != null,
                "FaceFXLineF" => SelectedDialogueNode?.SpeakerTag?.FaceFX_Female != null,
                "SoundP_Bank" => SelectedConv?.WwiseBank != null,
                "SoundP_StreamM" => SelectedDialogueNode?.WwiseStream_Male != null,
                "SoundP_StreamF" => SelectedDialogueNode?.WwiseStream_Female != null,
                "InterpEdLine" => SelectedDialogueNode?.InterpData != null,
                "PlotDbCnd" => SelectedDialogueNode != null && SelectedDialogueNode.ConditionalOrBool != 0,
                "PlotDbTrans" => SelectedDialogueNode != null && SelectedDialogueNode.Transition != 0,
                _ => true
            };
        }

        private bool TryGetBaseConversationReferenceTarget(string filename, out string filePath, out int targetUIndex)
        {
            filePath = null;
            targetUIndex = 0;

            if (SelectedConv?.Export == null || string.IsNullOrWhiteSpace(filename) || !TryResolveToolkitFilePath(filename, out filePath))
            {
                return false;
            }

            try
            {
                using IMEPackage basePackage = MEPackageHandler.OpenMEPackage(filePath, forceLoadFromDisk: true);
                IEntry conversationEntry = basePackage.Imports.FirstOrDefault(imp => imp.ClassName == "BioConversation"
                                                                                      && string.Equals(imp.InstancedFullPath, SelectedConv.Export.InstancedFullPath, StringComparison.OrdinalIgnoreCase));
                conversationEntry ??= basePackage.Imports.FirstOrDefault(imp => imp.ClassName == "BioConversation"
                                                                                && string.Equals(imp.ObjectName.Instanced, SelectedConv.Export.ObjectName.Instanced, StringComparison.OrdinalIgnoreCase));
                conversationEntry ??= basePackage.Exports.FirstOrDefault(exp => exp.ClassName == "BioConversation"
                                                                                && string.Equals(exp.InstancedFullPath, SelectedConv.Export.InstancedFullPath, StringComparison.OrdinalIgnoreCase));
                conversationEntry ??= basePackage.Exports.FirstOrDefault(exp => exp.ClassName == "BioConversation"
                                                                                && string.Equals(exp.ObjectName.Instanced, SelectedConv.Export.ObjectName.Instanced, StringComparison.OrdinalIgnoreCase));

                if (conversationEntry == null)
                {
                    return false;
                }

                targetUIndex = conversationEntry.GetEntriesThatReferenceThisOne()
                    .Keys
                    .OfType<ExportEntry>()
                    .OrderBy(exp => exp.UIndex)
                    .Select(exp => exp.UIndex)
                    .FirstOrDefault();

                return targetUIndex > 0;
            }
            catch (Exception ex) when (!App.IsDebug)
            {
                Debug.WriteLine($"Failed to resolve base conversation reference target in '{filePath}': {ex.Message}");
                return false;
            }
        }

        private static void ShowWindowAtFront(System.Windows.Window targetWindow)
        {
            if (targetWindow == null)
            {
                return;
            }

            if (!targetWindow.IsVisible)
            {
                targetWindow.Show();
            }

            targetWindow.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (!targetWindow.IsVisible)
                {
                    return;
                }

                if (targetWindow.WindowState == System.Windows.WindowState.Minimized)
                {
                    targetWindow.WindowState = System.Windows.WindowState.Normal;
                }

                bool wasTopmost = targetWindow.Topmost;
                targetWindow.Topmost = true;
                targetWindow.Activate();
                targetWindow.Focus();
                targetWindow.Topmost = wasTopmost;
            }));
        }

        private void OpenInToolkit(string tool, int uIndex = 0, string filename = null, string param = null)
        {
            if (!TryResolveToolkitFilePath(filename, out string filePath))
            {
                return;
            }

            switch (tool)
            {

                case "FaceFXEditor":
                    if (Pcc.IsUExport(uIndex) && param != null)
                    {
                        var faceFxEditor = new FaceFXEditorWindow(Pcc.GetUExport(uIndex), param);
                        ShowWindowAtFront(faceFxEditor);
                    }
                    else if (Pcc.IsUExport(uIndex))
                    {
                        var faceFxEditor = new FaceFXEditorWindow(Pcc.GetUExport(uIndex));
                        ShowWindowAtFront(faceFxEditor);
                    }
                    else
                    {
                        var facefxEditor = new FaceFXEditorWindow();
                        facefxEditor.LoadFile(filePath);
                        ShowWindowAtFront(facefxEditor);
                    }
                    break;
                case "PackageEditor":
                    var packEditor = new PackageEditorWindow();
                    if (Pcc.IsUExport(uIndex) && filePath == Pcc.FilePath)
                    {
                        packEditor.LoadFile(Pcc.FilePath, uIndex);
                    }
                    else
                    {
                        packEditor.LoadFile(filePath, uIndex);
                    }
                    ShowWindowAtFront(packEditor);
                    break;
                case "SoundplorerWPF":
                    if (Pcc.TryGetUExport(uIndex, out ExportEntry soundplorerExp))
                    {
                        var soundplorer = new SoundplorerWPF(soundplorerExp);
                        ShowWindowAtFront(soundplorer);
                    }
                    else
                    {
                        var soundplorerWPF = new SoundplorerWPF();
                        soundplorerWPF.LoadFile(Pcc.FilePath);
                        ShowWindowAtFront(soundplorerWPF);
                    }
                    break;
                case "SequenceEditor":
                    if (Pcc.IsUExport(uIndex) && filePath == Pcc.FilePath)
                    {
                        var sequenceEditor = new SequenceEditorWPF(Pcc.GetUExport(uIndex));
                        ShowWindowAtFront(sequenceEditor);
                    }
                    else
                    {
                        var seqEditor = new SequenceEditorWPF();
                        if (uIndex != 0)
                        {
                            seqEditor.LoadFileAndGoTo(filePath, uIndex);
                        }
                        else seqEditor.LoadFile(filePath);
                        ShowWindowAtFront(seqEditor);
                    }
                    break;
            }
        }

        private bool TryResolveToolkitFilePath(string filename, out string filePath)
        {
            filePath = null;
            if (filename == null)
            {
                filePath = Pcc.FilePath;
                return true;
            }

            if (File.Exists(filename))
            {
                filePath = filename;
                return true;
            }

            if (MELoadedFiles.TryGetHighestMountedFile(Pcc.Game, filename, out filePath))
            {
                return true;
            }

            filePath = Path.Combine(Path.GetDirectoryName(Pcc.FilePath), filename);
            if (File.Exists(filePath))
            {
                return true;
            }

            string rootPath = Pcc.Game switch
            {
                MEGame.ME1 => ME1Directory.DefaultGamePath,
                MEGame.ME2 => ME2Directory.DefaultGamePath,
                MEGame.ME3 => ME3Directory.DefaultGamePath,
                MEGame.LE1 => LE1Directory.DefaultGamePath,
                MEGame.LE2 => LE2Directory.DefaultGamePath,
                MEGame.LE3 => LE3Directory.DefaultGamePath,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(rootPath) && Directory.Exists(rootPath))
            {
                filePath = Directory.GetFiles(rootPath, filename, SearchOption.AllDirectories).FirstOrDefault();
                if (filePath != null)
                {
                    var dlg = MessageBox.Show($"Opening level at {filePath}", "Dialogue Editor", MessageBoxButton.OKCancel);
                    if (dlg != MessageBoxResult.Cancel)
                    {
                        return true;
                    }
                }
            }

            MessageBox.Show($"File {filename} not found.");
            filePath = null;
            return false;
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
                ShowWindowAtFront(m);
            }
            else
            {
                ShowWindowAtFront(Application.Current.Windows.OfType<TLKManagerWPF>().First());
            }
        }

        private void PopulateAddAllToReferencerItems(ItemCollection items)
        {
            items.Clear();

            if (Pcc == null)
            {
                return;
            }

            if (!Pcc.Exports.Any(exp => !exp.IsTrash() && exp.ClassName != "ObjectReferencer"))
            {
                items.Add(new MenuItem
                {
                    Header = "No eligible exports in package",
                    IsEnabled = false
                });
                return;
            }

            var defaultReferencerItem = new MenuItem
            {
                Header = "Default ObjectReferencer",
                Tag = "default"
            };
            defaultReferencerItem.Click += AddAllToReferencer_Target_Click;
            items.Add(defaultReferencerItem);

            if (!Pcc.Game.IsGame2())
            {
                var startupReferencerItem = new MenuItem
                {
                    Header = "CombinedStartupReferencer",
                    Tag = "startup"
                };
                startupReferencerItem.Click += AddAllToReferencer_Target_Click;
                items.Add(startupReferencerItem);
            }

            var customReferencerItem = new MenuItem
            {
                Header = "Custom named ObjectReferencer...",
                Tag = "custom"
            };
            customReferencerItem.Click += AddAllToReferencer_Target_Click;
            items.Add(customReferencerItem);

            var existingReferencers = Pcc.Exports
                .Where(exp => exp.ClassName == "ObjectReferencer" && exp.Parent == null && !exp.IsTrash())
                .OrderBy(exp => exp.ObjectName.Instanced, StringComparer.OrdinalIgnoreCase)
                .ThenBy(exp => exp.UIndex)
                .ToList();

            if (existingReferencers.Count > 0)
            {
                items.Add(new Separator());
                foreach (var referencer in existingReferencers)
                {
                    var referencerItem = new MenuItem
                    {
                        Header = $"{referencer.ObjectName.Instanced} (#{referencer.UIndex})",
                        Tag = referencer
                    };
                    referencerItem.Click += AddAllToReferencer_Target_Click;
                    items.Add(referencerItem);
                }
            }
        }

        private void AddAllToReferencer_MenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem)
            {
                return;
            }

            if (menuItem.ContextMenu is not ContextMenu contextMenu)
            {
                return;
            }

            PopulateAddAllToReferencerItems(contextMenu.Items);
            contextMenu.PlacementTarget = menuItem;
            contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            contextMenu.IsOpen = true;
            e.Handled = true;
        }

        private void AddAllToReferencer_Target_Click(object sender, RoutedEventArgs e)
        {
            if (Pcc == null || sender is not MenuItem menuItem)
            {
                return;
            }

            ExportEntry referencer = menuItem.Tag switch
            {
                ExportEntry existingReferencer => existingReferencer,
                "default" => Pcc.CreateObjectReferencer(),
                "startup" => Pcc.CreateObjectReferencer(isStartupPackage: true),
                "custom" => CreateNamedObjectReferencer(),
                _ => null
            };

            if (referencer == null)
            {
                return;
            }

            var exportsToReference = GetExportsForObjectReferencer(referencer);
            if (exportsToReference.Count == 0)
            {
                MessageBox.Show("No eligible exports were found to add to the selected referencer.", "Dialogue Editor");
                return;
            }

            var referencedObjects = referencer.GetProperties()?.GetProp<ArrayProperty<ObjectProperty>>("ReferencedObjects");
            if (referencedObjects != null)
            {
                referencedObjects.Clear();
                referencedObjects.AddRange(exportsToReference.Select(x => new ObjectProperty(x)));
                referencer.WriteProperty(referencedObjects);
            }
        }

        private ExportEntry CreateNamedObjectReferencer()
        {
            var customName = PromptDialog.Prompt(this, "Enter a custom suffix/name for the ObjectReferencer.", "Create named ObjectReferencer", "", true);
            if (string.IsNullOrWhiteSpace(customName))
            {
                return null;
            }

            return Pcc.CreateObjectReferencer(objectReferencerName: customName.Trim());
        }

        private List<ExportEntry> GetExportsForObjectReferencer(ExportEntry referencer)
        {
            if (Pcc == null)
            {
                return [];
            }

            var seekfreeClasses = new List<string>
            {
                "BioConversation",
                "FaceFXAnimSet",
                "Material",
                "MaterialInstanceConstant",
                "ObjectReferencer",
                "Sequence",
                "SkeletalMesh",
                "SkeletalMeshSocket",
                "Texture2D",
                "WwiseBank",
                "WwiseStream",
                "WwiseEvent"
            };

            var exportsToReference = new List<ExportEntry>();
            foreach (var export in Pcc.Exports)
            {
                foreach (var className in seekfreeClasses)
                {
                    if (export.ClassName == className)
                    {
                        exportsToReference.Add(export);
                        break;
                    }
                }
            }

            return exportsToReference;
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
            ClearConversationGraphCache();
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
                case "LineNoQuotes":
                    copytext = RemoveWrappingQuotes(SelectedDialogueNode.Line);
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

        private static string RemoveWrappingQuotes(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
            {
                return text;
            }

            return text[0] switch
            {
                '"' when text[^1] == '"' => text[1..^1],
                '“' when text[^1] == '”' => text[1..^1],
                _ => text
            };
        }

        private void ForceRefreshCore(bool preserveLayout)
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

            if (preserveLayout)
            {
                RefreshViewPreserveLayout();
            }
            else
            {
                RefreshView();
            }

            if (reselectedNodeID >= 0)
            {
                DialogueNode_SelectByIndex(reselectedNodeID, reselectedNodeReply);
            }
        }

        private void ForceRefresh(object obj)
        {
            ForceRefreshCore(false);
        }

        private void ForceRefreshPreserveLayout()
        {
            ForceRefreshCore(true);
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

            int? selectedInterpUIndex = GetSelectedInterpDataTreeExport()?.UIndex ?? SelectedDialogueNode.InterpData.UIndex;
            using var _ = SuppressPackageUpdates();
            var dialog = new BulkInterpEditorDialog(this, SelectedDialogueNode, SelectedConv);
            dialog.ShowDialog();
            if (dialog.ChangesApplied)
            {
                RefreshSelectedNodeAfterInterpMutation(selectedInterpUIndex);
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