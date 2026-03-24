using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Threading;
using LegendaryExplorerCore;
using LegendaryExplorerCore.Packages;
using Newtonsoft.Json.Linq;
using LegendaryExplorer.UserControls.ExportLoaderControls.ScriptEditor;

namespace LegendaryExplorer.Misc.AppSettings
{
    /// <summary>
    /// Auto-generated settings: DO NOT MANUALLY EDIT THIS .CS FILE, RUN SETTINGSBUILDER.TT, DEFINE IN SETTINGSDEFINTIIONS.XML!
    /// </summary>
    public static partial class Settings
    {
        private static readonly Lock settingsSyncObj = new();
        private static bool _mainwindow_disabletransparencyandanimations = false;
        public static bool MainWindow_DisableTransparencyAndAnimations {
            get => _mainwindow_disabletransparencyandanimations;
            set => SetProperty(ref _mainwindow_disabletransparencyandanimations, value);
        }
        private static string _mainwindow_favorites = "";
        public static string MainWindow_Favorites {
            get => _mainwindow_favorites;
            set => SetProperty(ref _mainwindow_favorites, value);
        }
        private static bool _mainwindow_completedinitialsetup = false;
        public static bool MainWindow_CompletedInitialSetup {
            get => _mainwindow_completedinitialsetup;
            set => SetProperty(ref _mainwindow_completedinitialsetup, value);
        }
        private static bool _packageeditor_hideinterpreterhexbox = true;
        public static bool PackageEditor_HideInterpreterHexBox {
            get => _packageeditor_hideinterpreterhexbox;
            set => SetProperty(ref _packageeditor_hideinterpreterhexbox, value);
        }
        private static bool _packageeditor_touchcomfymode = false;
        public static bool PackageEditor_TouchComfyMode {
            get => _packageeditor_touchcomfymode;
            set => SetProperty(ref _packageeditor_touchcomfymode, value);
        }
        private static bool _packageeditor_showimpexpprefix = true;
        public static bool PackageEditor_ShowImpExpPrefix {
            get => _packageeditor_showimpexpprefix;
            set => SetProperty(ref _packageeditor_showimpexpprefix, value);
        }
        private static bool _packageeditor_showexporttypeicons = true;
        public static bool PackageEditor_ShowExportTypeIcons {
            get => _packageeditor_showexporttypeicons;
            set => SetProperty(ref _packageeditor_showexporttypeicons, value);
        }
        private static bool _packageeditor_showtreeentrysubtext = true;
        public static bool PackageEditor_ShowTreeEntrySubText {
            get => _packageeditor_showtreeentrysubtext;
            set => SetProperty(ref _packageeditor_showtreeentrysubtext, value);
        }
        private static bool _packageeditor_showexperiments = false;
        public static bool PackageEditor_ShowExperiments {
            get => _packageeditor_showexperiments;
            set => SetProperty(ref _packageeditor_showexperiments, value);
        }
        private static bool _packageeditor_defaultmemorysafeimportporting = false;
        public static bool PackageEditor_DefaultMemorySafeImportPorting {
            get => _packageeditor_defaultmemorysafeimportporting;
            set => SetProperty(ref _packageeditor_defaultmemorysafeimportporting, value);
        }
        private static int _sequenceeditor_maxvarstringlength = 40;
        public static int SequenceEditor_MaxVarStringLength {
            get => _sequenceeditor_maxvarstringlength;
            set => SetProperty(ref _sequenceeditor_maxvarstringlength, value);
        }
        private static bool _sequenceeditor_showparsedinfo = true;
        public static bool SequenceEditor_ShowParsedInfo {
            get => _sequenceeditor_showparsedinfo;
            set => SetProperty(ref _sequenceeditor_showparsedinfo, value);
        }
        private static bool _sequenceeditor_autosaveviewv2 = true;
        public static bool SequenceEditor_AutoSaveViewV2 {
            get => _sequenceeditor_autosaveviewv2;
            set => SetProperty(ref _sequenceeditor_autosaveviewv2, value);
        }
        private static bool _sequenceeditor_showoutputnumbers = false;
        public static bool SequenceEditor_ShowOutputNumbers {
            get => _sequenceeditor_showoutputnumbers;
            set => SetProperty(ref _sequenceeditor_showoutputnumbers, value);
        }
        private static int _sequenceeditor_backgroundcolor = -8355840;
        public static int SequenceEditor_BackgroundColor {
            get => _sequenceeditor_backgroundcolor;
            set => SetProperty(ref _sequenceeditor_backgroundcolor, value);
        }
        private static int _sequenceeditor_boxfillcolor = -7892495;
        public static int SequenceEditor_BoxFillColor {
            get => _sequenceeditor_boxfillcolor;
            set => SetProperty(ref _sequenceeditor_boxfillcolor, value);
        }
        private static int _sequenceeditor_titleboxcolor = -8553424;
        public static int SequenceEditor_TitleBoxColor {
            get => _sequenceeditor_titleboxcolor;
            set => SetProperty(ref _sequenceeditor_titleboxcolor, value);
        }
        private static int _sequenceeditor_commenttextcolor = -5978694;
        public static int SequenceEditor_CommentTextColor {
            get => _sequenceeditor_commenttextcolor;
            set => SetProperty(ref _sequenceeditor_commenttextcolor, value);
        }
        private static int _sequenceeditor_boxtextcolor = -1;
        public static int SequenceEditor_BoxTextColor {
            get => _sequenceeditor_boxtextcolor;
            set => SetProperty(ref _sequenceeditor_boxtextcolor, value);
        }
        private static int _sequenceeditor_connectioncolor = -16777216; // Black (0xFF000000)
        public static int SequenceEditor_ConnectionColor {
            get => _sequenceeditor_connectioncolor;
            set => SetProperty(ref _sequenceeditor_connectioncolor, value);
        }
        private static int _sequenceeditor_varlinkcolor = -16777216; // Black (0xFF000000)
        public static int SequenceEditor_VarLinkColor {
            get => _sequenceeditor_varlinkcolor;
            set => SetProperty(ref _sequenceeditor_varlinkcolor, value);
        }
        private static string _sequenceeditor_favorites_me1 = "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent";
        public static string SequenceEditor_Favorites_ME1 {
            get => _sequenceeditor_favorites_me1;
            set => SetProperty(ref _sequenceeditor_favorites_me1, value);
        }
        private static string _sequenceeditor_favorites_me2 = "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent";
        public static string SequenceEditor_Favorites_ME2 {
            get => _sequenceeditor_favorites_me2;
            set => SetProperty(ref _sequenceeditor_favorites_me2, value);
        }
        private static string _sequenceeditor_favorites_me3 = "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent";
        public static string SequenceEditor_Favorites_ME3 {
            get => _sequenceeditor_favorites_me3;
            set => SetProperty(ref _sequenceeditor_favorites_me3, value);
        }
        private static string _sequenceeditor_favorites_le1 = "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent";
        public static string SequenceEditor_Favorites_LE1 {
            get => _sequenceeditor_favorites_le1;
            set => SetProperty(ref _sequenceeditor_favorites_le1, value);
        }
        private static string _sequenceeditor_favorites_le2 = "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent";
        public static string SequenceEditor_Favorites_LE2 {
            get => _sequenceeditor_favorites_le2;
            set => SetProperty(ref _sequenceeditor_favorites_le2, value);
        }
        private static string _sequenceeditor_favorites_le3 = "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent";
        public static string SequenceEditor_Favorites_LE3 {
            get => _sequenceeditor_favorites_le3;
            set => SetProperty(ref _sequenceeditor_favorites_le3, value);
        }
        private static string _sequenceeditor_favorites_udk = "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent";
        public static string SequenceEditor_Favorites_UDK {
            get => _sequenceeditor_favorites_udk;
            set => SetProperty(ref _sequenceeditor_favorites_udk, value);
        }
        private static bool _soundplorer_reverseiddisplayendianness = false;
        public static bool Soundplorer_ReverseIDDisplayEndianness {
            get => _soundplorer_reverseiddisplayendianness;
            set => SetProperty(ref _soundplorer_reverseiddisplayendianness, value);
        }
        private static bool _soundplorer_autoplayentriesonselection = false;
        public static bool Soundplorer_AutoplayEntriesOnSelection {
            get => _soundplorer_autoplayentriesonselection;
            set => SetProperty(ref _soundplorer_autoplayentriesonselection, value);
        }
        private static string _meshplorer_backgroundcolor = "#999999";
        public static string Meshplorer_BackgroundColor {
            get => _meshplorer_backgroundcolor;
            set => SetProperty(ref _meshplorer_backgroundcolor, value);
        }
        private static string _actorpreview_backgroundcolor = "#999999";
        public static string ActorPreview_BackgroundColor {
            get => _actorpreview_backgroundcolor;
            set => SetProperty(ref _actorpreview_backgroundcolor, value);
        }
        private static bool _meshplorer_viewfirstperson = false;
        public static bool Meshplorer_ViewFirstPerson {
            get => _meshplorer_viewfirstperson;
            set => SetProperty(ref _meshplorer_viewfirstperson, value);
        }
        private static bool _meshplorer_viewrotating = false;
        public static bool Meshplorer_ViewRotating {
            get => _meshplorer_viewrotating;
            set => SetProperty(ref _meshplorer_viewrotating, value);
        }
        private static bool _meshplorer_view_solidenabled = true;
        public static bool Meshplorer_View_SolidEnabled {
            get => _meshplorer_view_solidenabled;
            set => SetProperty(ref _meshplorer_view_solidenabled, value);
        }
        private static bool _meshplorer_viewwireframeenabled = false;
        public static bool Meshplorer_ViewWireframeEnabled {
            get => _meshplorer_viewwireframeenabled;
            set => SetProperty(ref _meshplorer_viewwireframeenabled, value);
        }
        private static bool _pathfindingeditor_shownodesizes = false;
        public static bool PathfindingEditor_ShowNodeSizes {
            get => _pathfindingeditor_shownodesizes;
            set => SetProperty(ref _pathfindingeditor_shownodesizes, value);
        }
        private static bool _pathfindingeditor_showpathfindingnodeslayer = true;
        public static bool PathfindingEditor_ShowPathfindingNodesLayer {
            get => _pathfindingeditor_showpathfindingnodeslayer;
            set => SetProperty(ref _pathfindingeditor_showpathfindingnodeslayer, value);
        }
        private static bool _pathfindingeditor_showactorslayer = false;
        public static bool PathfindingEditor_ShowActorsLayer {
            get => _pathfindingeditor_showactorslayer;
            set => SetProperty(ref _pathfindingeditor_showactorslayer, value);
        }
        private static bool _pathfindingeditor_showartlayer = false;
        public static bool PathfindingEditor_ShowArtLayer {
            get => _pathfindingeditor_showartlayer;
            set => SetProperty(ref _pathfindingeditor_showartlayer, value);
        }
        private static bool _pathfindingeditor_showsplineslayer = false;
        public static bool PathfindingEditor_ShowSplinesLayer {
            get => _pathfindingeditor_showsplineslayer;
            set => SetProperty(ref _pathfindingeditor_showsplineslayer, value);
        }
        private static bool _pathfindingeditor_showeverythingelselayer = false;
        public static bool PathfindingEditor_ShowEverythingElseLayer {
            get => _pathfindingeditor_showeverythingelselayer;
            set => SetProperty(ref _pathfindingeditor_showeverythingelselayer, value);
        }
        private static int _pathfindingeditor_backgroundcolor = -8224126;
        public static int PathfindingEditor_BackgroundColor {
            get => _pathfindingeditor_backgroundcolor;
            set => SetProperty(ref _pathfindingeditor_backgroundcolor, value);
        }
        private static bool _leveleditor_showcollision = false;
        public static bool LevelEditor_ShowCollision {
            get => _leveleditor_showcollision;
            set => SetProperty(ref _leveleditor_showcollision, value);
        }
        private static bool _leveleditor_showlighticons = true;
        public static bool LevelEditor_ShowLightIcons {
            get => _leveleditor_showlighticons;
            set => SetProperty(ref _leveleditor_showlighticons, value);
        }
        private static bool _leveleditor_showvolumes = false;
        public static bool LevelEditor_ShowVolumes {
            get => _leveleditor_showvolumes;
            set => SetProperty(ref _leveleditor_showvolumes, value);
        }
        private static bool _leveleditor_showvolumetrics = false;
        public static bool LevelEditor_ShowVolumetrics {
            get => _leveleditor_showvolumetrics;
            set => SetProperty(ref _leveleditor_showvolumetrics, value);
        }
        private static bool _leveleditor_unlit = false;
        public static bool LevelEditor_Unlit {
            get => _leveleditor_unlit;
            set => SetProperty(ref _leveleditor_unlit, value);
        }
        private static string _assetdb_defaultgame = "";
        public static string AssetDB_DefaultGame {
            get => _assetdb_defaultgame;
            set => SetProperty(ref _assetdb_defaultgame, value);
        }
        private static string _assetdbgame = "LE3";
        public static string AssetDBGame {
            get => _assetdbgame;
            set => SetProperty(ref _assetdbgame, value);
        }
        private static string _assetdbpath = "";
        public static string AssetDBPath {
            get => _assetdbpath;
            set => SetProperty(ref _assetdbpath, value);
        }
        private static bool _assetdb_hidemics = false;
        public static bool AssetDB_HideMICs {
            get => _assetdb_hidemics;
            set => SetProperty(ref _assetdb_hidemics, value);
        }
        private static string _assetdb_ambperfmasterpccpath = "";
        public static string AssetDB_AmbPerfMasterPccPath {
            get => _assetdb_ambperfmasterpccpath;
            set => SetProperty(ref _assetdb_ambperfmasterpccpath, value);
        }
        private static string _coalescededitor_sourcepath = "";
        public static string CoalescedEditor_SourcePath {
            get => _coalescededitor_sourcepath;
            set => SetProperty(ref _coalescededitor_sourcepath, value);
        }
        private static string _coalescededitor_destinationpath = "";
        public static string CoalescedEditor_DestinationPath {
            get => _coalescededitor_destinationpath;
            set => SetProperty(ref _coalescededitor_destinationpath, value);
        }
        private static bool _wwisegrapheditor_autosaveview = false;
        public static bool WwiseGraphEditor_AutoSaveView {
            get => _wwisegrapheditor_autosaveview;
            set => SetProperty(ref _wwisegrapheditor_autosaveview, value);
        }
        private static int _wwisegrapheditor_backgroundcolor = -5789785;
        public static int WwiseGraphEditor_BackgroundColor {
            get => _wwisegrapheditor_backgroundcolor;
            set => SetProperty(ref _wwisegrapheditor_backgroundcolor, value);
        }
        private static int _wwisegrapheditor_boxfillcolor = -7566196;
        public static int WwiseGraphEditor_BoxFillColor {
            get => _wwisegrapheditor_boxfillcolor;
            set => SetProperty(ref _wwisegrapheditor_boxfillcolor, value);
        }
        private static int _wwisegrapheditor_titleboxcolor = -9408400;
        public static int WwiseGraphEditor_TitleBoxColor {
            get => _wwisegrapheditor_titleboxcolor;
            set => SetProperty(ref _wwisegrapheditor_titleboxcolor, value);
        }
        private static int _wwisegrapheditor_commenttextcolor = -11911234;
        public static int WwiseGraphEditor_CommentTextColor {
            get => _wwisegrapheditor_commenttextcolor;
            set => SetProperty(ref _wwisegrapheditor_commenttextcolor, value);
        }
        private static int _wwisegrapheditor_boxtextcolor = -128;
        public static int WwiseGraphEditor_BoxTextColor {
            get => _wwisegrapheditor_boxtextcolor;
            set => SetProperty(ref _wwisegrapheditor_boxtextcolor, value);
        }
        private static int _wwisegrapheditor_boxoutlinecolor = -16777216;
        public static int WwiseGraphEditor_BoxOutlineColor {
            get => _wwisegrapheditor_boxoutlinecolor;
            set => SetProperty(ref _wwisegrapheditor_boxoutlinecolor, value);
        }
        private static int _wwisegrapheditor_connectioncolor = -16777216;
        public static int WwiseGraphEditor_ConnectionColor {
            get => _wwisegrapheditor_connectioncolor;
            set => SetProperty(ref _wwisegrapheditor_connectioncolor, value);
        }
        private static bool _binaryinterpreter_skipautoparsesizecheck = false;
        public static bool BinaryInterpreter_SkipAutoParseSizeCheck {
            get => _binaryinterpreter_skipautoparsesizecheck;
            set => SetProperty(ref _binaryinterpreter_skipautoparsesizecheck, value);
        }
        private static bool _textureviewer_autoloadmip = true;
        public static bool TextureViewer_AutoLoadMip {
            get => _textureviewer_autoloadmip;
            set => SetProperty(ref _textureviewer_autoloadmip, value);
        }
        private static string _textureviewer_backgroundcolor = "#808080";
        public static string TextureViewer_BackgroundColor {
            get => _textureviewer_backgroundcolor;
            set => SetProperty(ref _textureviewer_backgroundcolor, value);
        }
        private static bool _interpreter_limitarraypropertysize = true;
        public static bool Interpreter_LimitArrayPropertySize {
            get => _interpreter_limitarraypropertysize;
            set => SetProperty(ref _interpreter_limitarraypropertysize, value);
        }
        private static bool _interpreter_advanceddisplay = true;
        public static bool Interpreter_AdvancedDisplay {
            get => _interpreter_advanceddisplay;
            set => SetProperty(ref _interpreter_advanceddisplay, value);
        }
        private static bool _interpreter_colorize = true;
        public static bool Interpreter_Colorize {
            get => _interpreter_colorize;
            set => SetProperty(ref _interpreter_colorize, value);
        }
        private static bool _interpreter_showlinearcolorwheel = false;
        public static bool Interpreter_ShowLinearColorWheel {
            get => _interpreter_showlinearcolorwheel;
            set => SetProperty(ref _interpreter_showlinearcolorwheel, value);
        }
        private static bool _soundpanel_loopaudio = false;
        public static bool Soundpanel_LoopAudio {
            get => _soundpanel_loopaudio;
            set => SetProperty(ref _soundpanel_loopaudio, value);
        }
        private static string _wwise_3773path = "";
        public static string Wwise_3773Path {
            get => _wwise_3773path;
            set => SetProperty(ref _wwise_3773path, value);
        }
        private static string _wwise_7110path = "";
        public static string Wwise_7110Path {
            get => _wwise_7110path;
            set => SetProperty(ref _wwise_7110path, value);
        }
        private static string _tfccompactor_laststagingpath = "";
        public static string TFCCompactor_LastStagingPath {
            get => _tfccompactor_laststagingpath;
            set => SetProperty(ref _tfccompactor_laststagingpath, value);
        }
        private static bool _global_propertyparsing_parseunknownarraytypeasobject = false;
        public static bool Global_PropertyParsing_ParseUnknownArrayTypeAsObject {
            get => _global_propertyparsing_parseunknownarraytypeasobject;
            set => SetProperty(ref _global_propertyparsing_parseunknownarraytypeasobject, value);
        }
        private static bool _global_analytics_enabled = true;
        public static bool Global_Analytics_Enabled {
            get => _global_analytics_enabled;
            set => SetProperty(ref _global_analytics_enabled, value);
        }
        private static bool _global_darkmode_enabled = false;
        public static bool Global_DarkMode_Enabled {
            get => _global_darkmode_enabled;
            set => SetProperty(ref _global_darkmode_enabled, value);
        }
        private static string _global_me1directory = "";
        public static string Global_ME1Directory {
            get => _global_me1directory;
            set => SetProperty(ref _global_me1directory, value);
        }
        private static string _global_me2directory = "";
        public static string Global_ME2Directory {
            get => _global_me2directory;
            set => SetProperty(ref _global_me2directory, value);
        }
        private static string _global_me3directory = "";
        public static string Global_ME3Directory {
            get => _global_me3directory;
            set => SetProperty(ref _global_me3directory, value);
        }
        private static string _global_ledirectory = "";
        public static string Global_LEDirectory {
            get => _global_ledirectory;
            set => SetProperty(ref _global_ledirectory, value);
        }
        private static string _global_udkcustomdirectory = "";
        public static string Global_UDKCustomDirectory {
            get => _global_udkcustomdirectory;
            set => SetProperty(ref _global_udkcustomdirectory, value);
        }
        private static string _global_tlk_language = "INT";
        public static string Global_TLK_Language {
            get => _global_tlk_language;
            set => SetProperty(ref _global_tlk_language, value);
        }
        private static bool _conditionalseditor_defaultgraphview = false;
        public static bool ConditionalsEditor_DefaultGraphView {
            get => _conditionalseditor_defaultgraphview;
            set => SetProperty(ref _conditionalseditor_defaultgraphview, value);
        }
        private static bool _global_tlk_ismale = true;
        public static bool Global_TLK_IsMale {
            get => _global_tlk_ismale;
            set => SetProperty(ref _global_tlk_ismale, value);
        }
        private static List<string> _customstartupfiles = new List<string>();
        public static List<string> CustomStartupFiles {
            get => _customstartupfiles;
            set => SetProperty(ref _customstartupfiles, value);
        }
        private static List<string> _customassetdirectories = new List<string>();
        public static List<string> CustomAssetDirectories {
            get => _customassetdirectories;
            set => SetProperty(ref _customassetdirectories, value);
        }
        private static string _scriptide_activetheme = "";
        public static string ScriptIDE_ActiveTheme {
            get => _scriptide_activetheme;
            set => SetProperty(ref _scriptide_activetheme, value);
        }
        private static Dictionary<string, ThemeData> _scriptide_savedthemes = new Dictionary<string, ThemeData>();
        public static Dictionary<string, ThemeData> ScriptIDE_SavedThemes {
            get => _scriptide_savedthemes;
            set => SetProperty(ref _scriptide_savedthemes, value);
        }
        private static List<string> _tlkeditor_opentabs = new List<string>();
        public static List<string> TLKEditor_OpenTabs {
            get => _tlkeditor_opentabs;
            set => SetProperty(ref _tlkeditor_opentabs, value);
        }
        private static string _tlkeditor_selectedtab = "";
        public static string TLKEditor_SelectedTab {
            get => _tlkeditor_selectedtab;
            set => SetProperty(ref _tlkeditor_selectedtab, value);
        }

        public static string Get_SequenceEditor_Favorites (MEGame game) => game switch
        {
            MEGame.ME1 => SequenceEditor_Favorites_ME1,
            MEGame.ME2 => SequenceEditor_Favorites_ME2,
            MEGame.ME3 => SequenceEditor_Favorites_ME3,
            MEGame.LE1 => SequenceEditor_Favorites_LE1,
            MEGame.LE2 => SequenceEditor_Favorites_LE2,
            MEGame.LE3 => SequenceEditor_Favorites_LE3,
            MEGame.UDK => SequenceEditor_Favorites_UDK,
            _ => default
        };

        public static void Set_SequenceEditor_Favorites (MEGame game, string value)
        {
            switch (game)
            {
                case MEGame.ME1:
                    SequenceEditor_Favorites_ME1 = value;
                    break;
                case MEGame.ME2:
                    SequenceEditor_Favorites_ME2 = value;
                    break;
                case MEGame.ME3:
                    SequenceEditor_Favorites_ME3 = value;
                    break;
                case MEGame.LE1:
                    SequenceEditor_Favorites_LE1 = value;
                    break;
                case MEGame.LE2:
                    SequenceEditor_Favorites_LE2 = value;
                    break;
                case MEGame.LE3:
                    SequenceEditor_Favorites_LE3 = value;
                    break;
                case MEGame.UDK:
                    SequenceEditor_Favorites_UDK = value;
                    break;
            }
        }

        // Settings converters
        public static int TryGetSetting(Dictionary<string, object> settings, string key, int defaultValue) => settings.TryGetValue(key, out var value) && value is string svalue && int.TryParse(svalue, out var ivalue) ? ivalue : defaultValue;
        public static bool TryGetSetting(Dictionary<string, object> settings, string key, bool defaultValue) => settings.TryGetValue(key, out var value) && value is string svalue && bool.TryParse(svalue, out var bvalue) ? bvalue : defaultValue;
        public static string TryGetSetting(Dictionary<string, object> settings, string key, string defaultValue) => settings.TryGetValue(key, out var value) && value is string svalue ? svalue : defaultValue;
        public static List<string> TryGetSetting(Dictionary<string, object> settings, string key, List<string> defaultValue) => settings.TryGetValue(key, out var value) && value is JArray listValue ? listValue.ToObject<List<string>>() : defaultValue;
        public static Dictionary<string, ThemeData> TryGetSetting(Dictionary<string, object> settings, string key, Dictionary<string, ThemeData> defaultValue)
        {
            if (settings.TryGetValue(key, out var value) && value is JObject jObj)
            {
                try { return jObj.ToObject<Dictionary<string, ThemeData>>() ?? defaultValue; }
                catch { return defaultValue; }
            }
            return defaultValue;
        }


        private static string AppSettingsFile => Path.Combine(AppDirectories.AppDataFolder, "appsettings.json");
        /// <summary>
        /// Loads settings from disk.
        /// </summary>
        public static void LoadSettings()
        {
            if (Loaded)
                return;
            
            var settingsJson = File.Exists(AppSettingsFile)
                ? JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(AppSettingsFile))
                : new Dictionary<string, object>();

            //if the settings file has been corrupted somehow, the JSON deserializer will return null.
            settingsJson ??= new();

            MainWindow_DisableTransparencyAndAnimations = TryGetSetting(settingsJson, "mainwindow_disabletransparencyandanimations", false);
            MainWindow_Favorites = TryGetSetting(settingsJson, "mainwindow_favorites", "");
            MainWindow_CompletedInitialSetup = TryGetSetting(settingsJson, "mainwindow_completedinitialsetup", false);
            PackageEditor_HideInterpreterHexBox = TryGetSetting(settingsJson, "packageeditor_hideinterpreterhexbox", true);
            PackageEditor_TouchComfyMode = TryGetSetting(settingsJson, "packageeditor_touchcomfymode", false);
            PackageEditor_ShowImpExpPrefix = TryGetSetting(settingsJson, "packageeditor_showimpexpprefix", true);
            PackageEditor_ShowExportTypeIcons = TryGetSetting(settingsJson, "packageeditor_showexporttypeicons", true);
            PackageEditor_ShowTreeEntrySubText = TryGetSetting(settingsJson, "packageeditor_showtreeentrysubtext", true);
            PackageEditor_ShowExperiments = TryGetSetting(settingsJson, "packageeditor_showexperiments", false);
            PackageEditor_DefaultMemorySafeImportPorting = TryGetSetting(settingsJson, "packageeditor_defaultmemorysafeimportporting", false);
            SequenceEditor_MaxVarStringLength = TryGetSetting(settingsJson, "sequenceeditor_maxvarstringlength", 40);
            SequenceEditor_ShowParsedInfo = TryGetSetting(settingsJson, "sequenceeditor_showparsedinfo", true);
            SequenceEditor_AutoSaveViewV2 = TryGetSetting(settingsJson, "sequenceeditor_autosaveviewv2", true);
            SequenceEditor_ShowOutputNumbers = TryGetSetting(settingsJson, "sequenceeditor_showoutputnumbers", false);
            SequenceEditor_BackgroundColor = TryGetSetting(settingsJson, "sequenceeditor_backgroundcolor", -8355840);
            SequenceEditor_BoxFillColor = TryGetSetting(settingsJson, "sequenceeditor_boxfillcolor", -7892495);
            SequenceEditor_TitleBoxColor = TryGetSetting(settingsJson, "sequenceeditor_titleboxcolor", -8553424);
            SequenceEditor_CommentTextColor = TryGetSetting(settingsJson, "sequenceeditor_commenttextcolor", -5978694);
            SequenceEditor_BoxTextColor = TryGetSetting(settingsJson, "sequenceeditor_boxtextcolor", -1);
            SequenceEditor_ConnectionColor = TryGetSetting(settingsJson, "sequenceeditor_connectioncolor", -16777216);
            SequenceEditor_VarLinkColor = TryGetSetting(settingsJson, "sequenceeditor_varlinkcolor", -16777216);
            SequenceEditor_Favorites_ME1 = TryGetSetting(settingsJson, "sequenceeditor_favorites_me1", "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent");
            SequenceEditor_Favorites_ME2 = TryGetSetting(settingsJson, "sequenceeditor_favorites_me2", "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent");
            SequenceEditor_Favorites_ME3 = TryGetSetting(settingsJson, "sequenceeditor_favorites_me3", "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent");
            SequenceEditor_Favorites_LE1 = TryGetSetting(settingsJson, "sequenceeditor_favorites_le1", "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent");
            SequenceEditor_Favorites_LE2 = TryGetSetting(settingsJson, "sequenceeditor_favorites_le2", "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent");
            SequenceEditor_Favorites_LE3 = TryGetSetting(settingsJson, "sequenceeditor_favorites_le3", "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent");
            SequenceEditor_Favorites_UDK = TryGetSetting(settingsJson, "sequenceeditor_favorites_udk", "Sequence;SeqAct_Interp;InterpData;BioSeqAct_EndCurrentConvNode;BioSeqEvt_ConvNode;BioSeqVar_ObjectFindByTag;SeqVar_Object;SeqAct_ActivateRemoteEvent;SeqEvent_SequenceActivated;SeqAct_Delay;SeqAct_Gate;BioSeqAct_PMCheckState;BioSeqAct_PMExecuteTransition;SeqAct_FinishSequence;SeqEvent_RemoteEvent");
            Soundplorer_ReverseIDDisplayEndianness = TryGetSetting(settingsJson, "soundplorer_reverseiddisplayendianness", false);
            Soundplorer_AutoplayEntriesOnSelection = TryGetSetting(settingsJson, "soundplorer_autoplayentriesonselection", false);
            Meshplorer_BackgroundColor = TryGetSetting(settingsJson, "meshplorer_backgroundcolor", "#999999");
            ActorPreview_BackgroundColor = TryGetSetting(settingsJson, "actorpreview_backgroundcolor", "#999999");
            Meshplorer_ViewFirstPerson = TryGetSetting(settingsJson, "meshplorer_viewfirstperson", false);
            Meshplorer_ViewRotating = TryGetSetting(settingsJson, "meshplorer_viewrotating", false);
            Meshplorer_View_SolidEnabled = TryGetSetting(settingsJson, "meshplorer_view_solidenabled", true);
            Meshplorer_ViewWireframeEnabled = TryGetSetting(settingsJson, "meshplorer_viewwireframeenabled", false);
            PathfindingEditor_ShowNodeSizes = TryGetSetting(settingsJson, "pathfindingeditor_shownodesizes", false);
            PathfindingEditor_ShowPathfindingNodesLayer = TryGetSetting(settingsJson, "pathfindingeditor_showpathfindingnodeslayer", true);
            PathfindingEditor_ShowActorsLayer = TryGetSetting(settingsJson, "pathfindingeditor_showactorslayer", false);
            PathfindingEditor_ShowArtLayer = TryGetSetting(settingsJson, "pathfindingeditor_showartlayer", false);
            PathfindingEditor_ShowSplinesLayer = TryGetSetting(settingsJson, "pathfindingeditor_showsplineslayer", false);
            PathfindingEditor_ShowEverythingElseLayer = TryGetSetting(settingsJson, "pathfindingeditor_showeverythingelselayer", false);
            PathfindingEditor_BackgroundColor = TryGetSetting(settingsJson, "pathfindingeditor_backgroundcolor", -8224126);
            LevelEditor_ShowCollision = TryGetSetting(settingsJson, "leveleditor_showcollision", false);
            LevelEditor_ShowLightIcons = TryGetSetting(settingsJson, "leveleditor_showlighticons", true);
            LevelEditor_ShowVolumes = TryGetSetting(settingsJson, "leveleditor_showvolumes", false);
            LevelEditor_ShowVolumetrics = TryGetSetting(settingsJson, "leveleditor_showvolumetrics", false);
            AssetDB_DefaultGame = TryGetSetting(settingsJson, "assetdb_defaultgame", "");
            AssetDBGame = TryGetSetting(settingsJson, "assetdbgame", "LE3");
            AssetDBPath = TryGetSetting(settingsJson, "assetdbpath", "");
            AssetDB_HideMICs = TryGetSetting(settingsJson, "assetdb_hidemics", false);
            AssetDB_AmbPerfMasterPccPath = TryGetSetting(settingsJson, "assetdb_ambperfmasterpccpath", "");
            CoalescedEditor_SourcePath = TryGetSetting(settingsJson, "coalescededitor_sourcepath", "");
            CoalescedEditor_DestinationPath = TryGetSetting(settingsJson, "coalescededitor_destinationpath", "");
            WwiseGraphEditor_AutoSaveView = TryGetSetting(settingsJson, "wwisegrapheditor_autosaveview", false);
            WwiseGraphEditor_BackgroundColor = TryGetSetting(settingsJson, "wwisegrapheditor_backgroundcolor", -5789785);
            WwiseGraphEditor_BoxFillColor = TryGetSetting(settingsJson, "wwisegrapheditor_boxfillcolor", -7566196);
            WwiseGraphEditor_TitleBoxColor = TryGetSetting(settingsJson, "wwisegrapheditor_titleboxcolor", -9408400);
            WwiseGraphEditor_CommentTextColor = TryGetSetting(settingsJson, "wwisegrapheditor_commenttextcolor", -11911234);
            WwiseGraphEditor_BoxTextColor = TryGetSetting(settingsJson, "wwisegrapheditor_boxtextcolor", -128);
            WwiseGraphEditor_BoxOutlineColor = TryGetSetting(settingsJson, "wwisegrapheditor_boxoutlinecolor", -16777216);
            WwiseGraphEditor_ConnectionColor = TryGetSetting(settingsJson, "wwisegrapheditor_connectioncolor", -16777216);
            BinaryInterpreter_SkipAutoParseSizeCheck = TryGetSetting(settingsJson, "binaryinterpreter_skipautoparsesizecheck", false);
            TextureViewer_AutoLoadMip = TryGetSetting(settingsJson, "textureviewer_autoloadmip", true);
            TextureViewer_BackgroundColor = TryGetSetting(settingsJson, "textureviewer_backgroundcolor", "#808080");
            Interpreter_LimitArrayPropertySize = TryGetSetting(settingsJson, "interpreter_limitarraypropertysize", true);
            Interpreter_AdvancedDisplay = TryGetSetting(settingsJson, "interpreter_advanceddisplay", true);
            Interpreter_Colorize = TryGetSetting(settingsJson, "interpreter_colorize", true);
            Interpreter_ShowLinearColorWheel = TryGetSetting(settingsJson, "interpreter_showlinearcolorwheel", false);
            Soundpanel_LoopAudio = TryGetSetting(settingsJson, "soundpanel_loopaudio", false);
            Wwise_3773Path = TryGetSetting(settingsJson, "wwise_3773path", "");
            Wwise_7110Path = TryGetSetting(settingsJson, "wwise_7110path", "");
            TFCCompactor_LastStagingPath = TryGetSetting(settingsJson, "tfccompactor_laststagingpath", "");
            Global_PropertyParsing_ParseUnknownArrayTypeAsObject = TryGetSetting(settingsJson, "global_propertyparsing_parseunknownarraytypeasobject", false);
            Global_Analytics_Enabled = TryGetSetting(settingsJson, "global_analytics_enabled", true);
            Global_DarkMode_Enabled = TryGetSetting(settingsJson, "global_darkmode_enabled", false);
            Global_ME1Directory = TryGetSetting(settingsJson, "global_me1directory", "");
            Global_ME2Directory = TryGetSetting(settingsJson, "global_me2directory", "");
            Global_ME3Directory = TryGetSetting(settingsJson, "global_me3directory", "");
            Global_LEDirectory = TryGetSetting(settingsJson, "global_ledirectory", "");
            Global_UDKCustomDirectory = TryGetSetting(settingsJson, "global_udkcustomdirectory", "");
            Global_TLK_Language = TryGetSetting(settingsJson, "global_tlk_language", "INT");
            Global_TLK_IsMale = TryGetSetting(settingsJson, "global_tlk_ismale", true);
            CustomStartupFiles = TryGetSetting(settingsJson, "customstartupfiles", new List<string>());
            CustomAssetDirectories = TryGetSetting(settingsJson, "customassetdirectories", new List<string>());
            ScriptIDE_ActiveTheme = TryGetSetting(settingsJson, "scriptide_activetheme", "");
            ScriptIDE_SavedThemes = TryGetSetting(settingsJson, "scriptide_savedthemes", new Dictionary<string, ThemeData>());
            TLKEditor_OpenTabs = TryGetSetting(settingsJson, "tlkeditor_opentabs", new List<string>());
            TLKEditor_SelectedTab = TryGetSetting(settingsJson, "tlkeditor_selectedtab", "");

            ConditionalsEditor_DefaultGraphView = TryGetSetting(settingsJson, "conditionalseditor_defaultgraphview", false);

            // Settings Bridge Init
            LegendaryExplorerCoreLibSettings.Instance.ParseUnknownArrayTypesAsObject = Global_PropertyParsing_ParseUnknownArrayTypeAsObject;
            LegendaryExplorerCoreLibSettings.Instance.ME1Directory = Global_ME1Directory;
            LegendaryExplorerCoreLibSettings.Instance.ME2Directory = Global_ME2Directory;
            LegendaryExplorerCoreLibSettings.Instance.ME3Directory = Global_ME3Directory;
            LegendaryExplorerCoreLibSettings.Instance.LEDirectory = Global_LEDirectory;
            LegendaryExplorerCoreLibSettings.Instance.UDKCustomDirectory = Global_UDKCustomDirectory;
            LegendaryExplorerCoreLibSettings.Instance.TLKDefaultLanguage = Global_TLK_Language;
            LegendaryExplorerCoreLibSettings.Instance.TLKGenderIsMale = Global_TLK_IsMale;

            Loaded = true;
        }

        /// <summary>
        /// Commits settings to disk.
        /// </summary>
        public static void Save()
        {
            var settingsJson = new Dictionary<string,object>();
            settingsJson["mainwindow_disabletransparencyandanimations"] = MainWindow_DisableTransparencyAndAnimations.ToString();
            settingsJson["mainwindow_favorites"] = MainWindow_Favorites.ToString();
            settingsJson["mainwindow_completedinitialsetup"] = MainWindow_CompletedInitialSetup.ToString();
            settingsJson["packageeditor_hideinterpreterhexbox"] = PackageEditor_HideInterpreterHexBox.ToString();
            settingsJson["packageeditor_touchcomfymode"] = PackageEditor_TouchComfyMode.ToString();
            settingsJson["packageeditor_showimpexpprefix"] = PackageEditor_ShowImpExpPrefix.ToString();
            settingsJson["packageeditor_showexporttypeicons"] = PackageEditor_ShowExportTypeIcons.ToString();
            settingsJson["packageeditor_showtreeentrysubtext"] = PackageEditor_ShowTreeEntrySubText.ToString();
            settingsJson["packageeditor_showexperiments"] = PackageEditor_ShowExperiments.ToString();
            settingsJson["packageeditor_defaultmemorysafeimportporting"] = PackageEditor_DefaultMemorySafeImportPorting.ToString();
            settingsJson["sequenceeditor_maxvarstringlength"] = SequenceEditor_MaxVarStringLength.ToString();
            settingsJson["sequenceeditor_showparsedinfo"] = SequenceEditor_ShowParsedInfo.ToString();
            settingsJson["sequenceeditor_autosaveviewv2"] = SequenceEditor_AutoSaveViewV2.ToString();
            settingsJson["sequenceeditor_showoutputnumbers"] = SequenceEditor_ShowOutputNumbers.ToString();
            settingsJson["sequenceeditor_backgroundcolor"] = SequenceEditor_BackgroundColor.ToString();
            settingsJson["sequenceeditor_boxfillcolor"] = SequenceEditor_BoxFillColor.ToString();
            settingsJson["sequenceeditor_titleboxcolor"] = SequenceEditor_TitleBoxColor.ToString();
            settingsJson["sequenceeditor_commenttextcolor"] = SequenceEditor_CommentTextColor.ToString();
            settingsJson["sequenceeditor_boxtextcolor"] = SequenceEditor_BoxTextColor.ToString();
            settingsJson["sequenceeditor_connectioncolor"] = SequenceEditor_ConnectionColor.ToString();
            settingsJson["sequenceeditor_varlinkcolor"] = SequenceEditor_VarLinkColor.ToString();
            settingsJson["sequenceeditor_favorites_me1"] = SequenceEditor_Favorites_ME1.ToString();
            settingsJson["sequenceeditor_favorites_me2"] = SequenceEditor_Favorites_ME2.ToString();
            settingsJson["sequenceeditor_favorites_me3"] = SequenceEditor_Favorites_ME3.ToString();
            settingsJson["sequenceeditor_favorites_le1"] = SequenceEditor_Favorites_LE1.ToString();
            settingsJson["sequenceeditor_favorites_le2"] = SequenceEditor_Favorites_LE2.ToString();
            settingsJson["sequenceeditor_favorites_le3"] = SequenceEditor_Favorites_LE3.ToString();
            settingsJson["sequenceeditor_favorites_udk"] = SequenceEditor_Favorites_UDK.ToString();
            settingsJson["soundplorer_reverseiddisplayendianness"] = Soundplorer_ReverseIDDisplayEndianness.ToString();
            settingsJson["soundplorer_autoplayentriesonselection"] = Soundplorer_AutoplayEntriesOnSelection.ToString();
            settingsJson["meshplorer_backgroundcolor"] = Meshplorer_BackgroundColor.ToString();
            settingsJson["meshplorer_viewfirstperson"] = Meshplorer_ViewFirstPerson.ToString();
            settingsJson["meshplorer_viewrotating"] = Meshplorer_ViewRotating.ToString();
            settingsJson["meshplorer_view_solidenabled"] = Meshplorer_View_SolidEnabled.ToString();
            settingsJson["meshplorer_viewwireframeenabled"] = Meshplorer_ViewWireframeEnabled.ToString();
            settingsJson["pathfindingeditor_shownodesizes"] = PathfindingEditor_ShowNodeSizes.ToString();
            settingsJson["pathfindingeditor_showpathfindingnodeslayer"] = PathfindingEditor_ShowPathfindingNodesLayer.ToString();
            settingsJson["pathfindingeditor_showactorslayer"] = PathfindingEditor_ShowActorsLayer.ToString();
            settingsJson["pathfindingeditor_showartlayer"] = PathfindingEditor_ShowArtLayer.ToString();
            settingsJson["pathfindingeditor_showsplineslayer"] = PathfindingEditor_ShowSplinesLayer.ToString();
            settingsJson["pathfindingeditor_showeverythingelselayer"] = PathfindingEditor_ShowEverythingElseLayer.ToString();
            settingsJson["pathfindingeditor_backgroundcolor"] = PathfindingEditor_BackgroundColor.ToString();
            settingsJson["leveleditor_showcollision"] = LevelEditor_ShowCollision.ToString();
            settingsJson["leveleditor_showlighticons"] = LevelEditor_ShowLightIcons.ToString();
            settingsJson["leveleditor_showvolumes"] = LevelEditor_ShowVolumes.ToString();
            settingsJson["leveleditor_showvolumetrics"] = LevelEditor_ShowVolumetrics.ToString();
            settingsJson["assetdb_defaultgame"] = AssetDB_DefaultGame.ToString();
            settingsJson["assetdbgame"] = AssetDBGame.ToString();
            settingsJson["assetdbpath"] = AssetDBPath.ToString();
            settingsJson["assetdb_hidemics"] = AssetDB_HideMICs.ToString();
            settingsJson["assetdb_ambperfmasterpccpath"] = AssetDB_AmbPerfMasterPccPath.ToString();
            settingsJson["coalescededitor_sourcepath"] = CoalescedEditor_SourcePath.ToString();
            settingsJson["coalescededitor_destinationpath"] = CoalescedEditor_DestinationPath.ToString();
            settingsJson["wwisegrapheditor_autosaveview"] = WwiseGraphEditor_AutoSaveView.ToString();
            settingsJson["wwisegrapheditor_backgroundcolor"] = WwiseGraphEditor_BackgroundColor.ToString();
            settingsJson["wwisegrapheditor_boxfillcolor"] = WwiseGraphEditor_BoxFillColor.ToString();
            settingsJson["wwisegrapheditor_titleboxcolor"] = WwiseGraphEditor_TitleBoxColor.ToString();
            settingsJson["wwisegrapheditor_commenttextcolor"] = WwiseGraphEditor_CommentTextColor.ToString();
            settingsJson["wwisegrapheditor_boxtextcolor"] = WwiseGraphEditor_BoxTextColor.ToString();
            settingsJson["wwisegrapheditor_connectioncolor"] = WwiseGraphEditor_ConnectionColor.ToString();
            settingsJson["binaryinterpreter_skipautoparsesizecheck"] = BinaryInterpreter_SkipAutoParseSizeCheck.ToString();
            settingsJson["textureviewer_autoloadmip"] = TextureViewer_AutoLoadMip.ToString();
            settingsJson["textureviewer_backgroundcolor"] = TextureViewer_BackgroundColor.ToString();
            settingsJson["interpreter_limitarraypropertysize"] = Interpreter_LimitArrayPropertySize.ToString();
            settingsJson["interpreter_advanceddisplay"] = Interpreter_AdvancedDisplay.ToString();
            settingsJson["interpreter_colorize"] = Interpreter_Colorize.ToString();
            settingsJson["interpreter_showlinearcolorwheel"] = Interpreter_ShowLinearColorWheel.ToString();
            settingsJson["soundpanel_loopaudio"] = Soundpanel_LoopAudio.ToString();
            settingsJson["wwise_3773path"] = Wwise_3773Path.ToString();
            settingsJson["wwise_7110path"] = Wwise_7110Path.ToString();
            settingsJson["tfccompactor_laststagingpath"] = TFCCompactor_LastStagingPath.ToString();
            settingsJson["global_propertyparsing_parseunknownarraytypeasobject"] = Global_PropertyParsing_ParseUnknownArrayTypeAsObject.ToString();
            settingsJson["global_analytics_enabled"] = Global_Analytics_Enabled.ToString();
            settingsJson["global_darkmode_enabled"] = Global_DarkMode_Enabled.ToString();
            settingsJson["global_me1directory"] = Global_ME1Directory.ToString();
            settingsJson["global_me2directory"] = Global_ME2Directory.ToString();
            settingsJson["global_me3directory"] = Global_ME3Directory.ToString();
            settingsJson["global_ledirectory"] = Global_LEDirectory.ToString();
            settingsJson["global_udkcustomdirectory"] = Global_UDKCustomDirectory.ToString();
            settingsJson["global_tlk_language"] = Global_TLK_Language.ToString();
            settingsJson["global_tlk_ismale"] = Global_TLK_IsMale.ToString();
            settingsJson["customstartupfiles"] = CustomStartupFiles;
            settingsJson["customassetdirectories"] = CustomAssetDirectories;
            settingsJson["scriptide_activetheme"] = ScriptIDE_ActiveTheme.ToString();
            settingsJson["scriptide_savedthemes"] = ScriptIDE_SavedThemes;
            settingsJson["tlkeditor_opentabs"] = TLKEditor_OpenTabs;
            settingsJson["tlkeditor_selectedtab"] = TLKEditor_SelectedTab.ToString();

            settingsJson["conditionalseditor_defaultgraphview"] = ConditionalsEditor_DefaultGraphView.ToString();

            var settingsText = JsonConvert.SerializeObject(settingsJson, Formatting.Indented);
            try
            {
                lock (settingsSyncObj) {
                    File.WriteAllText(AppSettingsFile, settingsText);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Could not save settings: {e.Message}");
            }
        }
    }
}
