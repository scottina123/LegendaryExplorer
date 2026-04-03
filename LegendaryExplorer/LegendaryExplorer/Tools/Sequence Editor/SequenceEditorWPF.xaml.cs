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
using System.Windows.Threading;
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorer.Tools.ObjectReferenceViewer;
using LegendaryExplorer.DialogueEditor;
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
        public ObservableCollectionExtended<SObj> CurrentObjects { get; } = new();
        public ObservableCollectionExtended<SObj> SelectedObjects { get; } = new();
        public ObservableCollectionExtended<ExportEntry> SequenceExports { get; } = new();
        public ObservableCollectionExtended<TreeViewEntry> TreeViewRootNodes { get; } = new();
        public ObservableCollectionExtended<TreeViewEntry> InterpDataTreeNodes { get; } = new();
        public string CurrentFile;
        public string JSONpath;

        private bool _useSavedViews = true; // Should probably be a global setting
        private int suppressInterpreterUnloadDepth;
        private int suppressInterpDataInterpreterUnloadDepth;

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
            private set => SetProperty(ref _isSceneShopSequenceSelected, value);
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

        private SavedViewData SavedView;
        private bool forceAutoLayoutOnInitialPackageLoad = true;
        private bool forceAutoLayoutForCurrentPackage;
        private readonly HashSet<int> autoLaidOutSequencesForCurrentPackage = [];
        private List<CopiedInputConnection> copiedInputConnections;
        private string copiedInputConnectionsSourceFilePath;
        private List<CopiedOutputConnection> copiedOutputConnections;
        private string copiedOutputConnectionsSourceFilePath;
        private List<CopiedVariableConnection> copiedVariableConnections;
        private string copiedVariableConnectionsSourceFilePath;
        private CopiedConnectionSet copiedAllConnections;

        public static readonly string SequenceEditorDataFolder =
            Path.Combine(AppDirectories.AppDataFolder, @"SequenceEditor\");

        public static readonly string
            OptionsPath = Path.Combine(SequenceEditorDataFolder, "SequenceEditorOptions.JSON");

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

        public ICommand OpenCommand { get; set; }
        public ICommand SaveCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand SaveImageCommand { get; set; }
        public ICommand SaveViewCommand { get; set; }
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
            if (dlg.ShowDialog(this) == CommonFileDialogResult.Ok)
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

            addObject(newSeqObj);
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
            if (d.ShowDialog() == true)
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
            if (d.ShowDialog() == true)
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
            }
        }

        private void RemoveFavorite(ClassInfo classInfo)
        {
            favoritesToolBox.Classes.Remove(classInfo);
            SaveFavorites();
        }

        private void ResetFavorites()
        {
            favoritesToolBox.Classes.Clear();
            favoritesToolBox.Classes.AddRange(SequenceObjectCreator.GetCommonObjects(Pcc.Game)
                .OrderBy(info => info.ClassName));
            SaveFavorites();
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
            bool forceAutoLayout = fromFile
                                   && forceAutoLayoutForCurrentPackage
                                   && autoLaidOutSequencesForCurrentPackage.Add(seqExport.UIndex);
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
                    .Where(prop => Pcc.IsUExport(prop.Value))
                    .Select(prop => Pcc.GetUExport(prop.Value))
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

            if (export.IsA("SequenceEvent"))
            {
                return new SEvent(export, graphEditor);
            }
            else if (export.IsA("SequenceVariable"))
            {
                return new SVar(export, graphEditor);
            }
            else if (export.ClassName == "SequenceFrame" &&
                     (Pcc.Game == MEGame.ME1 || Pcc.Game == MEGame.UDK || Pcc.Game.IsLEGame()))
            {
                return new SFrame(export, graphEditor);
            }
            else //if (s.StartsWith("BioSeqAct_") || s.StartsWith("SeqAct_") || s.StartsWith("SFXSeqAct_") || s.StartsWith("SeqCond_") || pcc.getExport(index).ClassName == "Sequence" || pcc.getExport(index).ClassName == "SequenceReference")
            {
                return new SAction(export, graphEditor);
            }
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
                if (FindResource("backContextMenu") is ContextMenu contextMenu)
                {
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

        private void SequenceEditor_DragEnter(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop))
                e.Effect = System.Windows.Forms.DragDropEffects.All;
            else
                e.Effect = System.Windows.Forms.DragDropEffects.None;
        }

        private void SequenceEditor_DragDrop(object sender, System.Windows.Forms.DragEventArgs e)
        {
            if (e.Data.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] DroppedFiles)
            {
                if (DroppedFiles.Any())
                {
                    LoadFile(DroppedFiles[0]);
                }
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

            InterpData_MetadataEditor.LoadPccData(Pcc);

            IEnumerable<PackageUpdate> relevantUpdates = updates.Where(x => x.Change.Has(PackageChange.Export));
            List<int> updatedExports = relevantUpdates.Select(x => x.Index).ToList();

            if (InterpDataTreeNodes.Count > 0)
            {
                var interpTreeIndexes = InterpDataTreeNodes
                    .SelectMany(root => root.FlattenTree())
                    .Select(node => node.UIndex)
                    .ToHashSet();

                if (updatedExports.Any(interpTreeIndexes.Contains))
                {
                    RefreshInterpDataTreePreserveState();
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
            PromptAndAddNamedLinkEntry("InputLinks", "input", "In");
        }

        private void AddOutputEntry_Clicked(object sender, RoutedEventArgs e)
        {
            PromptAndAddNamedLinkEntry("OutputLinks", "output", "Out");
        }

        private void AddVariableEntry_Clicked(object sender, RoutedEventArgs e)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SObj { Export: { } export })
            {
                return;
            }

            if (!CanAddNamedLinkEntry(export, "VariableLinks"))
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

        private record VariableLinkEntryDialogResult(string EntryName, string ExpectedTypeName);

        private VariableLinkEntryDialogResult PromptForVariableLinkEntry()
        {
            var classOptions = GlobalUnrealObjectInfo.GetClasses(Pcc.Game).Values
                .Where(x => x.IsA("SequenceVariable", Pcc.Game))
                .Select(x => x.ClassName)
                .OrderBy(x => x)
                .ToList();

            var nameTextBox = new TextBox
            {
                MinWidth = 260,
                Text = "Variable"
            };
            var typeComboBox = new ComboBox
            {
                MinWidth = 260,
                ItemsSource = classOptions,
                SelectedItem = "SeqVar_Object",
                IsEditable = false
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
                        typeComboBox,
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
                okButton.IsEnabled = !string.IsNullOrWhiteSpace(nameTextBox.Text) && typeComboBox.SelectedItem != null;
            }

            nameTextBox.TextChanged += (_, _) => UpdateOkState();
            typeComboBox.SelectionChanged += (_, _) => UpdateOkState();
            dialog.Loaded += (_, _) =>
            {
                nameTextBox.Focus();
                nameTextBox.SelectAll();
                UpdateOkState();
            };
            okButton.Click += (_, _) => dialog.DialogResult = true;

            return dialog.ShowDialog() == true
                ? new VariableLinkEntryDialogResult(nameTextBox.Text.Trim(), typeComboBox.SelectedItem as string)
                : null;
        }

        private void PromptAndAddNamedLinkEntry(string propertyName, string entryType, string defaultName)
        {
            if (CurrentObjects_ListBox.SelectedItem is not SObj { Export: { } export })
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

        private void SequenceEditorWPF_Closing(object sender, CancelEventArgs e)
        {
            if (e.Cancel)
                return;

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
            if (d.ShowDialog() == true)
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

        private void addObject(ExportEntry exportToAdd, bool removeLinks = true)
        {
            customSaveData[exportToAdd.UIndex] =
                new PointF(graphEditor.Camera.ViewCenterX, graphEditor.Camera.ViewCenterY);
            if (SelectedSequence.IsA("SFXSceneShopGameData"))
            {
                exportToAdd.Parent = SelectedSequence;
                AddObjectToSFXSceneShopGameData(exportToAdd, SelectedSequence);
            }
            else
            {
                KismetHelper.AddObjectToSequence(exportToAdd, SelectedSequence, removeLinks);
            }
        }

        private void AddObject_Clicked(object sender, RoutedEventArgs e)
        {
            if (EntrySelector.GetEntry<ExportEntry>(this, Pcc) is ExportEntry exportToAdd)
            {
                if (!exportToAdd.IsA("SequenceObject"))
                {
                    MessageBox.Show(this,
                        $"#{exportToAdd.UIndex}: {exportToAdd.ObjectName.Instanced} is not a sequence object.");
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
                    if (!IsLoaded)
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
            if (!IsLoaded)
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
            SequenceEditorExperimentsM.LoadCustomClassesFromFile(this);
        }

        private void LoadCustomClassesFromCurentPackage_Clicked(object sender, RoutedEventArgs e)
        {
            SequenceEditorExperimentsM.LoadCustomClassesFromCurrentPackage(this);
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
                var result = PromptDialog.Prompt(this, "How many outlinks would you like to add?",
                    "Add switch outlinks", "1", true);
                if (int.TryParse(result, out var howManyToAdd) && howManyToAdd > 0)
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

            var selectedExport = GetSelectedInterpDataTreeExport();
            bool isInterpData = selectedExport?.ClassName == "InterpData";
            bool isInterpTrackMove = selectedExport?.ClassName == "InterpTrackMove";
            bool isGestureTrack = selectedExport?.ClassName == "BioEvtSysTrackGesture";

            SetInterpDataContextMenuItemVisibility(menu, "ShiftInterpTrackMovesInInterpData", isInterpData ? Visibility.Visible : Visibility.Collapsed);
            SetInterpDataContextMenuItemVisibility(menu, "ShiftSelectedInterpTrackMove", isInterpTrackMove ? Visibility.Visible : Visibility.Collapsed);
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
                    if (d.ShowDialog() == true)
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