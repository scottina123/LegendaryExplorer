using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using Key = System.Windows.Input.Key;
using GongSolutions.Wpf.DragDrop;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Libraries;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorerCore.Misc.ME3Tweaks;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorer.Tools.TlkManagerNS;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.Extensions.Collections.Generic;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Matinee;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Sound.ISACT;
using LegendaryExplorerCore.TLK.ME1;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorerCore.UnrealScript;
using LegendaryExplorerCore.UnrealScript.Compiling.Errors;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using LegendaryExplorerCore.Audio;
using LegendaryExplorer.Packages;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Pathing;
using LegendaryExplorerCore.UnrealScript.Language.Tree;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.AssetViewer;
using LegendaryExplorer.Tools.PlotEditor;
using LegendaryExplorer.GameInterop;
using Xceed.Wpf.Toolkit;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using LegendaryExplorer.Tools.ObjectReferenceViewer;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorer.Tools.Dialogue_Editor.DialogueEditorExperiments;
using LegendaryExplorerCore.Matinee;
using System.Windows.Media;
using TextureImage = LegendaryExplorerCore.Textures.Image;
using Texture2D = LegendaryExplorerCore.Unreal.Classes.Texture2D;

namespace LegendaryExplorer.Tools.PackageEditor
{
    /// <summary>
    /// Interaction logic for PackageEditorWPF.xaml
    /// </summary>
    public partial class PackageEditorWindow : WPFBase, IDropTarget, IBusyUIHost, IRecents
    {
        private readonly record struct NameArrayPathSegment(NameReference ArrayName, int StructIndex);

        private readonly record struct NameUsagePropertyPathSegment(string PropertyName, int? ArrayIndex);

        private readonly record struct TreeViewScrollState(double HorizontalOffset, double VerticalOffset);

        private readonly record struct TextureTfcMoveResult(int MovedCount, int FailedCount, List<EntryStringPair> Messages);

        private const int SearchBatchSize = 2048;

        private CancellationTokenSource _entrySearchCancellationTokenSource;

        private static readonly HashSet<string> CommonStringRefPropertyNames =
        [
            "m_nStrRefID",
            "nLineStrRef",
            "nStrRefID",
            "m_iStringRef",
            "m_iDescriptionStringRef",
            "m_srStringID"
        ];

        private sealed record NameArrayUsageMatch(
            ExportEntry Entry,
            string DisplayPath,
            NameReference ArrayName,
            int SourceElementIndex,
            NameReference SourceName,
            IReadOnlyList<NameArrayPathSegment> PathSegments)
        {
            public string PathKey => string.Join("/", PathSegments.Select(segment => $"{segment.ArrayName.Instanced}[{segment.StructIndex}]").Append(ArrayName.Instanced));

            public bool TryApply(PropertyCollection rootProps, NameReference targetName)
            {
                PropertyCollection currentProps = rootProps;
                foreach (var segment in PathSegments)
                {
                    if (currentProps.GetProp<ArrayProperty<StructProperty>>(segment.ArrayName) is not { } structArray
                        || segment.StructIndex < 0
                        || segment.StructIndex >= structArray.Count)
                    {
                        return false;
                    }

                    currentProps = structArray[segment.StructIndex].Properties;
                }

                if (currentProps.GetProp<ArrayProperty<NameProperty>>(ArrayName) is not { } nameArray)
                {
                    return false;
                }

                if (nameArray.Any(prop => prop.Value == targetName))
                {
                    return false;
                }

                int insertIndex = Math.Min(SourceElementIndex + 1, nameArray.Count);
                nameArray.Insert(insertIndex, new NameProperty(targetName));
                return true;
            }

            public bool TryRemove(PropertyCollection rootProps, NameReference targetName)
            {
                PropertyCollection currentProps = rootProps;
                foreach (var segment in PathSegments)
                {
                    if (currentProps.GetProp<ArrayProperty<StructProperty>>(segment.ArrayName) is not { } structArray
                        || segment.StructIndex < 0
                        || segment.StructIndex >= structArray.Count)
                    {
                        return false;
                    }

                    currentProps = structArray[segment.StructIndex].Properties;
                }

                if (currentProps.GetProp<ArrayProperty<NameProperty>>(ArrayName) is not { } nameArray
                    || SourceElementIndex < 0
                    || SourceElementIndex >= nameArray.Count
                    || nameArray[SourceElementIndex].Value != targetName)
                {
                    return false;
                }

                nameArray.RemoveAt(SourceElementIndex);
                return true;
            }
        }

        public enum CurrentViewMode
        {
            Names,
            Imports,
            Exports,
            Tree
        }

        public static readonly string[] ExportFileTypes =
            ["GFxMovieInfo", "BioSWF", "Texture2D", "WwiseStream", "BioTlkFile"];

        public static readonly string[] ExportIconTypes =
        [
            "GFxMovieInfo", "BioSWF", "Texture2D", "WwiseStream", "BioTlkFile",
            "World", "Package", "StaticMesh", "SkeletalMesh", "Sequence", "Material", "Function", "Class", "State",
            "TextureCube", "Bio2DA", "Bio2DANumberedRows", "DecalMaterial", "MaterialInstanceConstant"
        ];

        //Objects in this collection are displayed on the left list view (names, imports, exports)

        readonly Dictionary<ExportLoaderControl, TabItem> ExportLoaders = [];

        private CurrentViewMode _currentView;

        public CurrentViewMode CurrentView
        {
            get => _currentView;
            set
            {
                if (SetProperty(ref _currentView, value))
                {
                    switch (value)
                    {
                        case CurrentViewMode.Names:
                            TextSearch.SetTextPath(LeftSide_ListView, "Name");
                            ClearPreviewPane();
                            break;
                        case CurrentViewMode.Imports:
                            TextSearch.SetTextPath(LeftSide_ListView, "ObjectName");
                            break;
                        case CurrentViewMode.Exports:
                            TextSearch.SetTextPath(LeftSide_ListView, "ObjectName");
                            break;
                    }

                    RefreshView();
                }
            }
        }

        public ObservableCollectionExtended<object> LeftSideList_ItemsSource { get; } = [];

        //referenced by EntryMetaDataExportLoader's xaml, do not make private
        public ObservableCollectionExtended<IndexedName> NamesList { get; } = [];

        public ObservableCollectionExtended<TreeViewEntry> AllTreeViewNodesX { get; } = [];
        public ObservableCollectionExtended<IEntry> BackwardsEntries { get; } = new();
        public ObservableCollectionExtended<IEntry> ForwardsEntries { get; } = new();
        private readonly HashSet<TreeViewEntry> _selectedTreeItems = [];
        private TreeViewEntry _treeSelectionAnchor;
        private bool _updatingTreeMultiSelection;

        private TreeViewEntry _selectedItem;
        public TreeViewEntry SelectedItem
        {
            get => _selectedItem;
            set
            {
                var oldIndex = _selectedItem?.UIndex;
                // Some weird oddity exists in TreeView WPF where it selects the node twice when expanding stuff
                // and it makes first selection sometimes reset to nothing.
                // This is hack to make it not do that.

                // only allow selecting a null tree entry if there is no package loaded
                bool allowSelection = Pcc != null && value != null;
                if (!allowSelection && Pcc == null) allowSelection = true;

                if (allowSelection && SetProperty(ref _selectedItem, value) && !SuppressSelectionEvent)
                {
                    SyncTreeMultiSelectionWithPrimary(value);
                    OnPropertyChanged(nameof(CanRebuildBioWorldStreamingLevels));
                    //_lastSelectionEvent = now;
                    if (oldIndex.HasValue && oldIndex.Value != 0 && !IsBackForwardsNavigationEvent)
                    {
                        // 0 = tree root
                        //Debug.WriteLine("Push onto backwards: " + oldIndex);
                        BackwardsEntries.Insert(0, Pcc.GetEntry(oldIndex.Value));
                        ForwardsEntries.Clear(); //forward list is no longer valid
                    }

                    ApplySelectionPreview();
                }
            }
        }

        private void SyncTreeMultiSelectionWithPrimary(TreeViewEntry primaryNode)
        {
            if (_updatingTreeMultiSelection || CurrentView != CurrentViewMode.Tree)
            {
                return;
            }

            if (primaryNode is null)
            {
                ClearTreeMultiSelection();
                return;
            }

            SetTreeMultiSelection([primaryNode], primaryNode, updatePrimarySelection: false, updateAnchor: false);
        }

        private void ClearTreeMultiSelection()
        {
            foreach (TreeViewEntry node in _selectedTreeItems)
            {
                node.IsMultiSelected = false;
            }

            _selectedTreeItems.Clear();
        }

        private void SetTreeMultiSelection(IEnumerable<TreeViewEntry> nodes, TreeViewEntry primaryNode, bool updatePrimarySelection, bool updateAnchor = true)
        {
            _updatingTreeMultiSelection = true;
            try
            {
                ClearTreeMultiSelection();

                foreach (TreeViewEntry node in nodes.Where(node => node is not null).Distinct())
                {
                    node.IsMultiSelected = true;
                    _selectedTreeItems.Add(node);
                }

                if (updateAnchor)
                {
                    _treeSelectionAnchor = primaryNode;
                }

                if (updatePrimarySelection && primaryNode is not null)
                {
                    primaryNode.IsProgramaticallySelecting = true;
                    SelectedItem = primaryNode;
                }
            }
            finally
            {
                _updatingTreeMultiSelection = false;
            }
        }

        private static bool IsTreeNodeVisibleForSelection(TreeViewEntry node)
        {
            if (node is null || !node.IsVisibleInTree)
            {
                return false;
            }

            for (TreeViewEntry current = node.Parent; current is not null; current = current.Parent)
            {
                if (!current.IsVisibleInTree)
                {
                    return false;
                }

                if (current.Parent is not null && !current.IsExpanded)
                {
                    return false;
                }
            }

            return true;
        }

        private List<TreeViewEntry> GetVisibleTreeNodes()
        {
            return AllTreeViewNodesX.Count == 0
                ? []
                : AllTreeViewNodesX[0].FlattenTree().Where(IsTreeNodeVisibleForSelection).ToList();
        }

        private int QueuedGotoNumber;
        private bool IsLoadingFile;
        private bool _delaySelectionPreview;
        private DispatcherOperation _pendingPreviewOperation;
        private bool _pendingPreviewIsRefresh;
        /// <summary>
        /// Caches FaceFXAnimSet export UIndex -> ObjectName so we can detect renames in HandleUpdate.
        /// </summary>
        private Dictionary<int, string> _faceFXAnimSetNameCache = [];

        private string _searchHintText = "Object name";

        public string SearchHintText
        {
            get => _searchHintText;
            set => SetProperty(ref _searchHintText, value);
        }

        private string _gotoHintText = "UIndex";
        private string _stringRefSearchText;
        private bool SuppressSelectionEvent;

        private bool _showOnlyEditedTreeViewItems;
        private readonly HashSet<int> _comparedChangedEntryIndices = [];
        private string _selectedClassSearch;
        public string SelectedClassSearch
        {
            get => _selectedClassSearch;
            set => SetProperty(ref _selectedClassSearch, value);
        }

        public bool ShowOnlyEditedTreeViewItems
        {
            get => _showOnlyEditedTreeViewItems;
            set
            {
                if (SetProperty(ref _showOnlyEditedTreeViewItems, value))
                {
                    ApplyTreeViewEditedFilter();
                }
            }
        }

        public string GotoHintText
        {
            get => _gotoHintText;
            set => SetProperty(ref _gotoHintText, value);
        }

        public string StringRefSearchText
        {
            get => _stringRefSearchText;
            set => SetProperty(ref _stringRefSearchText, value);
        }

        public double StringRefSearchBoxWidth
        {
            get
            {
                double width = ActualWidth;
                if (width < 1250)
                {
                    return 40;
                }

                if (width < 1450)
                {
                    return 70;
                }

                if (width < 1650)
                {
                    return 100;
                }

                return 150;
            }
        }

        private bool _showExperiments = App.IsDebug || Settings.PackageEditor_ShowExperiments;
        public bool ShowExperiments
        {
            get => _showExperiments;
            set
            {
                SetProperty(ref _showExperiments, value);
                Settings.PackageEditor_ShowExperiments = value;
            }
        }

        #region Commands
        public ICommand NavigateBackCommand { get; set; }
        public ICommand NavigateForwardCommand { get; set; }
        public ICommand ForceReloadPackageCommand { get; set; }
        public ICommand ComparePackagesCommand { get; set; }
        public ICommand StructuralComparePackagesCommand { get; set; }
        public ICommand OpenOtherVersionCommand { get; set; }
        public ICommand OpenHighestMountedCommand { get; set; }
        public ICommand OpenHighestMountedLinkedFileCommand { get; set; }
        public ICommand CompareToUnmoddedCommand { get; set; }
        public ICommand StructuralCompareToUnmoddedCommand { get; set; }
        public ICommand ExportAllDataCommand { get; set; }
        public ICommand ExportBinaryDataCommand { get; set; }
        public ICommand ImportAllDataCommand { get; set; }
        public ICommand ImportBinaryDataCommand { get; set; }
        public ICommand CloneCommand { get; set; }
        public ICommand CloneTreeCommand { get; set; }
        public ICommand MultiCloneCommand { get; set; }
        public ICommand MultiCloneTreeCommand { get; set; }
        public ICommand FindEntryViaOffsetCommand { get; set; }
        public ICommand FindEntryViaBadIndexCommand { get; set; }
        public ICommand ResolveImportsTreeViewCommand { get; set; }
        public ICommand CheckForDuplicateIndexesCommand { get; set; }
        public ICommand CheckForInvalidObjectPropertiesCommand { get; set; }
        public ICommand CheckForBrokenMaterialsCommand { get; set; }
        public ICommand CheckForScriptErrorsCommand { get; set; }
        public ICommand CheckForInvalidPropertiesCommand { get; set; }
        public ICommand EditNameCommand { get; set; }
        public ICommand AddNameCommand { get; set; }
        public ICommand CopyNameCommand { get; set; }
        public ICommand FindNameUsagesCommand { get; set; }
        public ICommand ViewInAssetViewerCommand { get; set; }
        public ICommand RebuildStreamingLevelsCommand { get; set; }
        public ICommand ExportEmbeddedFileCommand { get; set; }
        public ICommand ImportEmbeddedFileCommand { get; set; }
        public ICommand ReindexCommand { get; set; }
        public ICommand TrashCommand { get; set; }
        public ICommand TrashChildrenCommand { get; set; }
        public ICommand SetIndicesInTreeToZeroCommand { get; set; }
        public ICommand PackageHeaderViewerCommand { get; set; }
        public ICommand LECLEditorCommand { get; set; }
        public ICommand CreateNewPackageGUIDCommand { get; set; }
        public ICommand RestoreExportCommand { get; set; }
        public ICommand RestoreExportTreeCommand { get; set; }
        public ICommand SetPackageAsFilenamePackageCommand { get; set; }
        public ICommand FindEntryViaTagCommand { get; set; }
        public ICommand PopoutCurrentViewCommand { get; set; }
        public ICommand BulkExportSWFCommand { get; set; }
        public ICommand BulkImportSWFCommand { get; set; }
        public ICommand OpenFileCommand { get; set; }
        public ICommand NewFileCommand { get; set; }
        public ICommand NewLevelFileCommand { get; set; }
        public ICommand SaveFileCommand { get; set; }
        public ICommand SaveAsCommand { get; set; }
        public ICommand FindCommand { get; set; }
        public ICommand SearchStringRefsCommand { get; set; }
        public ICommand FindAllClassInstancesCommand { get; set; }
        public ICommand GotoCommand { get; set; }
        public ICommand TabRightCommand { get; set; }
        public ICommand TabLeftCommand { get; set; }
        public ICommand FindReferencesCommand { get; set; }
        public ICommand OpenExportInCommand { get; set; }
        public ICommand CompactShaderCacheCommand { get; set; }
        public ICommand GoToArchetypeCommand { get; set; }
        public ICommand ReplaceNamesCommand { get; set; }
        public ICommand NavigateToEntryCommand { get; set; }
        public ICommand ResolveImportCommand { get; set; }
        public ICommand ExtractToPackageCommand { get; set; }
        public ICommand PackageExportIsSelectedCommand { get; set; }
        public ICommand ReindexDuplicateIndexesCommand { get; set; }
        public ICommand ReplaceReferenceLinksCommand { get; set; }
        public ICommand CalculateExportMD5Command { get; set; }
        public ICommand CreateClassCommand { get; set; }
        public ICommand CreatePackageExportCommand { get; set; }
        public ICommand CreateObjectRedirectorCommand { get; set; }
        public ICommand CreateObjectReferencerCommand { get; set; }
        public ICommand CreateTextureCommand { get; set; }
        public ICommand DeleteEntryCommand { get; set; }
        public ICommand ExportAllPropsCommand { get; set; }
        public ICommand ApplyBulkPropEditsCommand { get; set; }
        public ICommand ViewReferenceGraphCommand { get; set; }
        public ICommand AddInterpGroupCommand { get; set; }
        public ICommand AddInterpTrackCommand { get; set; }
        public ICommand BulkEditInterpGroupsCommand { get; set; }
        public ICommand OpenGestureImporterCommand { get; set; }
        public ICommand ShiftInterpTrackMoveCommand { get; set; }
        public ICommand ShiftInterpTrackMovesInPackageCommand { get; set; }
        public ICommand ShiftInterpTrackMovesInInterpDataCommand { get; set; }
        public ICommand ShiftLevelActorsCommand { get; set; }
        public ICommand AdjustInterpTimeOffsetsCommand { get; set; }
        public ICommand AddAllAssetsToReferencerCommand { get; set; }
        public ICommand CloneTreeToFolderCommand { get; set; }
        public ICommand MatchMaterialsToSkeletalMeshCommand { get; set; }
        public ICommand OpenAssetImporterCommand { get; set; }

        private void LoadCommands()
        {
            CalculateExportMD5Command = new GenericCommand(CalculateExportMD5, ExportIsSelected);
            CompareToUnmoddedCommand = new GenericCommand(() => SharedPackageTools.ComparePackageToUnmodded(this, entryDoubleClickToTreeview), () => SharedPackageTools.CanCompareToUnmodded(this));
            StructuralCompareToUnmoddedCommand = new GenericCommand(() => SharedPackageTools.ComparePackageToUnmodded(this, entryDoubleClickToTreeview, true), () => SharedPackageTools.CanCompareToUnmodded(this));
            ComparePackagesCommand = new GenericCommand(() => SharedPackageTools.ComparePackageToAnother(this, entryDoubleClickToTreeview), PackageIsLoaded);
            StructuralComparePackagesCommand = new GenericCommand(() => SharedPackageTools.ComparePackageToAnother(this, entryDoubleClickToTreeview, true), PackageIsLoaded);
            ExportAllDataCommand = new GenericCommand(ExportAllData, ExportIsSelected);
            ExportBinaryDataCommand = new GenericCommand(ExportBinaryData, ExportIsSelected);
            ImportAllDataCommand = new GenericCommand(ImportAllData, ExportIsSelected);
            ImportBinaryDataCommand = new GenericCommand(ImportBinaryData, ExportIsSelected);
            CloneCommand = new GenericCommand(() => CloneEntry(1), EntryIsSelected);
            CloneTreeCommand = new GenericCommand(() => CloneTree(1), TreeEntryIsSelected);
            MultiCloneCommand = new GenericCommand(CloneEntryMultiple, EntryIsSelected);
            MultiCloneTreeCommand = new GenericCommand(CloneTreeMultiple, TreeEntryIsSelected);
            FindEntryViaOffsetCommand = new GenericCommand(FindEntryViaOffset, PackageIsLoaded);
            FindEntryViaBadIndexCommand = new GenericCommand(FindEntryViaBadIndex, PackageIsLoaded);
            CheckForDuplicateIndexesCommand = new GenericCommand(CheckForDuplicateIndexes, PackageIsLoaded);
            CheckForInvalidObjectPropertiesCommand = new GenericCommand(CheckForBadObjectPropertyReferences, PackageIsLoaded);
            CheckForBrokenMaterialsCommand = new GenericCommand(CheckForBrokenMaterials, IsLoadedPackageME);
            CheckForScriptErrorsCommand = new GenericCommand(CheckForScriptErrors, IsLoadedPackageME);
            CheckForInvalidPropertiesCommand = new GenericCommand(CheckForInvalidProperties, IsLoadedPackageME);
            EditNameCommand = new GenericCommand(EditName, NameIsSelected);
            AddNameCommand = new RelayCommand(AddName, CanAddName);
            CopyNameCommand = new GenericCommand(CopyName, NameIsSelected);
            FindNameUsagesCommand = new GenericCommand(FindNameUsages, NameIsSelected);
            ViewInAssetViewerCommand = new GenericCommand(ViewInAssetViewer, CanViewInAssetViewer);
            RebuildStreamingLevelsCommand = new GenericCommand(RebuildStreamingLevels, () => CanRebuildBioWorldStreamingLevels);
            ExportEmbeddedFileCommand = new GenericCommand(ExportEmbeddedFilePrompt, DoesSelectedItemHaveEmbeddedFile);
            ImportEmbeddedFileCommand = new GenericCommand(ImportEmbeddedFile, DoesSelectedItemHaveEmbeddedFile);
            FindReferencesCommand = new GenericCommand(FindReferencesToObject, EntryIsSelected);
            ReindexCommand = new GenericCommand(ReindexObjectByName, ExportIsSelected);
            SetIndicesInTreeToZeroCommand = new GenericCommand(SetIndicesInTreeToZero, TreeEntryIsSelected);
            TrashCommand = new GenericCommand(() => TrashEntryAndChildren(true), () => TreeEntryIsSelected());
            TrashChildrenCommand = new GenericCommand(() => TrashEntryAndChildren(false), () => TreeEntryHasChildren());
            PackageHeaderViewerCommand = new GenericCommand(ViewPackageInfo, PackageIsLoaded);
            LECLEditorCommand = new GenericCommand(EditLECLData, CanEditLECLData);
            PackageExportIsSelectedCommand = new EnableCommand(PackageExportIsSelected);
            CreateNewPackageGUIDCommand = new GenericCommand(GenerateNewGUIDForSelected, PackageExportIsSelected);
            SetPackageAsFilenamePackageCommand = new GenericCommand(SetSelectedAsFilenamePackage, PackageExportIsSelected);
            FindEntryViaTagCommand = new GenericCommand(FindEntryViaTag, PackageIsLoaded);
            PopoutCurrentViewCommand = new GenericCommand(PopoutCurrentView, ExportIsSelected);
            CompactShaderCacheCommand = new GenericCommand(CompactShaderCache, HasShaderCache);
            GoToArchetypeCommand = new GenericCommand(GoToArchetype, CanGoToArchetype);
            ReplaceNamesCommand = new GenericCommand(SearchReplaceNames, PackageIsLoaded);
            ReindexDuplicateIndexesCommand = new GenericCommand(ReindexDuplicateIndexes, PackageIsLoaded);
            ReplaceReferenceLinksCommand = new GenericCommand(ReplaceReferenceLinks, EntryIsSelected);
            OpenFileCommand = new GenericCommand(OpenFile);
            NewFileCommand = new GenericCommand(NewFile);
            NewLevelFileCommand = new GenericCommand(NewLevelFile);
            SaveFileCommand = new GenericCommand(SaveFile, PackageIsLoaded);
            SaveAsCommand = new GenericCommand(SaveFileAs, PackageIsLoaded);
            FindCommand = new GenericCommand(FocusSearch, PackageIsLoaded);
            SearchStringRefsCommand = new GenericCommand(SearchStringRefs, PackageIsLoaded);
            GotoCommand = new GenericCommand(FocusGoto, PackageIsLoaded);
            TabRightCommand = new GenericCommand(TabRight, PackageIsLoaded);
            TabLeftCommand = new GenericCommand(TabLeft, PackageIsLoaded);

            BulkExportSWFCommand = new GenericCommand(BulkExportSWFs, PackageIsLoaded);
            BulkImportSWFCommand = new GenericCommand(BulkImportSWFs, PackageIsLoaded);
            OpenExportInCommand = new RelayCommand(OpenExportIn, CanOpenExportIn);
            AddInterpGroupCommand = new RelayCommand(AddInterpGroup, CanAddInterpGroup);
            AddInterpTrackCommand = new GenericCommand(AddInterpTrack, CanAddInterpTrack);
            BulkEditInterpGroupsCommand = new GenericCommand(BulkEditInterpGroups, CanBulkEditInterpGroups);
            OpenGestureImporterCommand = new GenericCommand(OpenGestureImporter, CanOpenGestureImporter);
            ShiftInterpTrackMoveCommand = new GenericCommand(ShiftSelectedInterpTrackMove, CanShiftInterpTrackMove);
            ShiftInterpTrackMovesInPackageCommand = new GenericCommand(ShiftInterpTrackMovesInSelectedPackage, PackageExportIsSelected);
            ShiftInterpTrackMovesInInterpDataCommand = new GenericCommand(ShiftInterpTrackMovesInSelectedInterpData, CanBulkEditInterpGroups);
            ShiftLevelActorsCommand = new GenericCommand(ShiftSelectedLevelActors, CanShiftSelectedLevelActors);
            AdjustInterpTimeOffsetsCommand = new GenericCommand(AdjustSelectedInterpTimeOffsets, CanAdjustSelectedInterpTimeOffsets);
            AddAllAssetsToReferencerCommand = new GenericCommand(AddAllAssetsToReferencer, ObjectReferencerIsSelected);
            CloneTreeToFolderCommand = new GenericCommand(CloneTreeToFolder, ExportIsSelected);
            MatchMaterialsToSkeletalMeshCommand = new GenericCommand(MatchMaterialsToSkeletalMesh);

            NavigateToEntryCommand = new RelayCommand(NavigateToEntry, CanNavigateToEntry);

            ResolveImportCommand = new GenericCommand(OpenImportDefinition, ImportIsSelected);
            ResolveImportsTreeViewCommand = new GenericCommand(ResolveImportsTreeView, PackageIsLoaded);
            FindAllClassInstancesCommand = new GenericCommand(FindAllInstancesofClass, PackageIsLoaded);
            ExtractToPackageCommand = new GenericCommand(ExtractEntryToNewPackage, ExportIsSelected);

            RestoreExportCommand = new GenericCommand(RestoreExportData, ExportIsSelected);
            RestoreExportTreeCommand = new GenericCommand(RestoreExportDataForWholeTree, ExportIsSelected);
            OpenOtherVersionCommand = new GenericCommand(OpenOtherVersion, IsLoadedPackageME);
            OpenHighestMountedCommand = new GenericCommand(OpenHighestMountedVersion, IsLoadedPackageME);
            OpenHighestMountedLinkedFileCommand = new GenericCommand(OpenHighestMountedLinkedFile, IsLoadedPackageME);

            //do not change lambda to method group here! causes runtime error
            ForceReloadPackageCommand = new GenericCommand(() => ExperimentsMenu.ForceReloadPackageWithoutSharing(), () => ShowExperiments && ExperimentsMenu.CanForceReload());

            NavigateForwardCommand = new GenericCommand(NavigateToNextEntry, () => CurrentView == CurrentViewMode.Tree && ForwardsEntries != null && ForwardsEntries.Any());
            NavigateBackCommand = new GenericCommand(NavigateToPreviousEntry, () => CurrentView == CurrentViewMode.Tree && BackwardsEntries.Any());

            CreateClassCommand = new GenericCommand(CreateClass, IsLoadedPackageME);
            CreatePackageExportCommand = new GenericCommand(CreatePackageExport, IsLoadedPackageME);
            CreateObjectRedirectorCommand = new GenericCommand(CreateObjectRedirector, ExportIsSelected);
            CreateObjectReferencerCommand = new GenericCommand(CreateObjectReferencer, IsLoadedPackageME);
            CreateTextureCommand = new GenericCommand(CreateTexture, IsLoadedPackageME);
            DeleteEntryCommand = new GenericCommand(DeleteEntry, EntryIsSelected);

            ExportAllPropsCommand = new GenericCommand(ExportAllProps, PackageIsLoaded);
            ApplyBulkPropEditsCommand = new GenericCommand(ApplyBulkPropEdits, PackageIsLoaded);
            ViewReferenceGraphCommand = new GenericCommand(ViewReferenceGraph, EntryIsSelected);
            OpenAssetImporterCommand = new GenericCommand(OpenAssetImporter, PackageIsLoaded);
        }

        private void OpenAssetImporter()
        {
            if (Pcc == null) return;
            var targetPcc = Pcc;
            AssetDatabaseWindow.OpenForImport(this, targetPcc.Game, importItems =>
            {
                IsBusy = true;
                BusyText = "Importing assets...";
                Task.Run(() =>
                {
                    var allRelinkIssues = new List<EntryStringPair>();
                    using var cache = new PackageCache();
                    foreach (var item in importItems)
                    {
                        if (item.ResolvedFilePath is null) continue;
                        try
                        {
                            using var sourcePcc = MEPackageHandler.OpenMEPackage(item.ResolvedFilePath);
                            if (!sourcePcc.IsUExport(item.UIndex)) continue;
                            var sourceExport = sourcePcc.GetUExport(item.UIndex);
                            var rop = new RelinkerOptionsPackage(cache)
                            {
                                ImportExportDependencies = true,
                                PortImportsMemorySafe = true,
                            };
                            IEntry targetLink = null;
                            if (!string.IsNullOrEmpty(sourceExport.ParentFullPath))
                            {
                                targetLink = EntryImporter.GetOrAddCrossImportOrPackage(
                                    sourceExport.ParentFullPath, sourcePcc, targetPcc, rop);
                            }
                            var results = EntryImporter.ImportAndRelinkEntries(
                                EntryImporter.PortingOption.CloneAllDependencies,
                                sourceExport, targetPcc, targetLink, true, rop, out IEntry importedEntry);
                            SynchronizeImportedSequenceObjects(sourceExport, targetLink, importedEntry, rop);
                            if (results is not null)
                                allRelinkIssues.AddRange(results);
                        }
                        catch (Exception ex)
                        {
                            allRelinkIssues.Add(new EntryStringPair(
                                $"Error importing '{item.DisplayName}': {ex.Message}"));
                        }
                    }
                    return allRelinkIssues;
                }).ContinueWithOnUIThread(prevTask =>
                {
                    IsBusy = false;
                    EntryImporterExtended.ShowRelinkResultsIfAny(prevTask.Result);
                });
            });
        }

        private void FindEntryViaBadIndex()
        {
            if (Pcc == null)
            {
                return;
            }

            string input = "Enter the bad export/import index that is listed in the output of Debug Logger.";
            string result = PromptDialog.Prompt(this, input, "Enter bad index");
            if (result != null)
            {
                try
                {
                    int badIndex = int.Parse(result);

                    var decomp = Pcc.SaveToStream(false);
                    bool found = false;
                    while (decomp.Position <= decomp.Length - 4)
                    {
                        var readVal = decomp.ReadInt32();
                        decomp.Position -= 3;
                        if (readVal == badIndex)
                        {
                            found = true;
                            decomp.Position--; // Go back one more
                            break;
                        }
                    }

                    if (found)
                    {
                        GotoEntryViaOffset((int)decomp.Position);
                    }
                    else
                    {
                        MessageBox.Show($"Did not find any instance of the number {badIndex} in the uncompressed package file.");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error: " + ex.Message);
                }
            }
        }

        private void GotoEntryViaOffset(int offset)
        {
            //TODO: Fix offset selection code, it seems off by a bit, not sure why yet
            for (int i = 0; i < Pcc.ImportCount; i++)
            {
                ImportEntry imp = Pcc.Imports[i];
                if (offset >= imp.HeaderOffset && offset < imp.HeaderOffset + ImportEntry.HeaderLength)
                {
                    GoToNumber(imp.UIndex);
                    Metadata_Tab.IsSelected = true;
                    MetadataTab_MetadataEditor.SetHexboxSelectedOffset(imp.HeaderOffset + ImportEntry.HeaderLength - offset);
                    return;
                }
            }

            foreach (ExportEntry exp in Pcc.Exports)
            {
                //header
                if (offset >= exp.HeaderOffset && offset < exp.HeaderOffset + exp.HeaderLength)
                {
                    GoToNumber(exp.UIndex);
                    Metadata_Tab.IsSelected = true;
                    MetadataTab_MetadataEditor.SetHexboxSelectedOffset(exp.HeaderOffset + exp.HeaderLength - offset);
                    return;
                }

                //data
                if (offset >= exp.DataOffset && offset < exp.DataOffset + exp.DataSize)
                {
                    GoToNumber(exp.UIndex);
                    int inExportDataOffset = exp.DataOffset + exp.DataSize - offset;
                    int propsEnd = exp.propsEnd();

                    if (inExportDataOffset > propsEnd && exp.DataSize > propsEnd &&
                        BinaryInterpreterTab_BinaryInterpreter.CanParse(exp))
                    {
                        BinaryInterpreterTab_BinaryInterpreter.SetHexboxSelectedOffset(inExportDataOffset);
                        BinaryInterpreter_Tab.IsSelected = true;
                    }
                    else
                    {
                        InterpreterTab_Interpreter.SetHexboxSelectedOffset(inExportDataOffset);
                        Interpreter_Tab.IsSelected = true;
                    }

                    return;
                }
            }

            MessageBox.Show($"No entry or header containing offset 0x{offset:X8} was found.");
        }

        private void ViewReferenceGraph()
        {
            if (TryGetSelectedEntry(out var entry))
            {
                var orv = new ObjectReferenceViewerWindow(entry, GetEntryDoubleClickAction());
                orv.Show();
            }
        }

        private void ViewInAssetViewer()
        {
            if (TryGetSelectedExport(out var currentExport) && AssetViewerWindow.SupportsAsset(currentExport))
            {
                AssetViewerWindow.PreviewAsset(currentExport);
            }
        }

        private void MatchMaterialsToSkeletalMesh()
        {
            if (TryGetSelectedExport(out var currentExport) && InterpreterExportLoader.CanMatchMaterialsToSkeletalMesh(currentExport))
            {
                InterpreterExportLoader.MatchMaterialsToSkeletalMesh(this, currentExport);
                Preview(true);
                return;
            }

            MessageBox.Show(this,
                "This action only works on SkeletalMeshComponent exports.",
                "Match MaterialInstanceConstants to SkeletalMesh",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private bool CanMatchMaterialsToSkeletalMesh()
        {
            return TryGetSelectedExport(out var currentExport) && InterpreterExportLoader.CanMatchMaterialsToSkeletalMesh(currentExport);
        }

        private static bool CanRestoreMaterialFromAssetDatabase(ExportEntry export)
        {
            return export is not null && (export.ClassName == "Material" || export.IsA("MaterialInstanceConstant"));
        }

        private static bool CanAddMissingTexturesToInstancesMap(ExportEntry export)
        {
            return export is { ClassName: "Level", InstancedFullPath: "TheWorld.PersistentLevel" };
        }

        private static bool CanStripLightmap(ExportEntry export)
        {
            return export is { ClassName: "StaticMeshComponent", IsDefaultObject: false };
        }

        private static bool CanStripShadowmap(ExportEntry export)
        {
            return export is { ClassName: "StaticMeshComponent", IsDefaultObject: false };
        }

        private bool CanViewInAssetViewer()
        {
            if (Pcc != null && Pcc.Game.IsLEGame() && TryGetSelectedExport(out var currentExport) && GameController.TryGetMEProcess(currentExport.Game, out _) && AssetViewerWindow.SupportsAsset(currentExport))
            {
                return true;
            }

            return false;
        }

        private void CreateTexture()
        {
            var tc = new TextureCreatorDialog(this, Pcc, SelectedItem?.Entry);

            tc.ShowDialog();

            if (tc.GeneratedExport != null)
            {
                GoToEntry(tc.GeneratedExport.InstancedFullPath);
            }

        }

        private void ApplyBulkPropEdits()
        {
            var d = new OpenFileDialog
            {
                Title = "Select properties file",
                Filter = "unrealscript file|*.uc",
                FileName = $"{Pcc.FileNameNoExtension}_Props.uc",
                CheckFileExists = true
            };
            if (DirectoryMemory.ShowDialog(d) is not true) return;
            var fileName = d.FileName;
            SetBusy("Applying property edits");
            Task.Run(() =>
            {
                string src = File.ReadAllText(fileName);
                return UnrealScriptCompiler.CompileBulkPropertiesFile(src, Pcc, new UnrealScriptOptionsPackage());

            }).ContinueWithOnUIThread(prevTask =>
            {
                EndBusy();
                MessageLog log = prevTask.Result;
                if (log.HasErrors || log.HasLexErrors)
                {
                    new ListDialog(log.AllErrors.Select(msg => msg.ToString()), "Errors", "Errors occured while applying property edits!", this).Show();
                }
                else
                {
                    // if (App.IsDebug)
                    // {
                    //     MessageBox.Show(this, $"Property edits successfully applied! {Pcc.Exports.FirstOrDefault(exp => exp.DataChanged)?.UIndex}");
                    // }
                    // else
                    {
                        MessageBox.Show(this, "Property edits successfully applied!");
                    }
                }
            });
        }

        private void ExportAllProps()
        {
            SetBusy("Decompiling all properties");
            Task.Run(() =>
            {
                string src = UnrealScriptCompiler.DecompileBulkProps(Pcc, out MessageLog log, new UnrealScriptOptionsPackage());
                if (src is null || log.HasErrors)
                {
                    return log;
                }
                return (object)src;
            }).ContinueWithOnUIThread(prevTask =>
            {
                EndBusy();
                switch (prevTask.Result)
                {
                    case string src:
                        {
                            var d = new SaveFileDialog
                            {
                                Title = "Save properties file",
                                Filter = "unrealscript file|*.uc",
                                FileName = $"{Pcc.FileNameNoExtension}_Props.uc"
                            };
                            if (DirectoryMemory.ShowDialog(d) == true)
                            {
                                File.WriteAllText(d.FileName, src);
                            }
                            break;
                        }
                    case MessageLog log:
                        {
                            new ListDialog(log.AllErrors.Select(msg => msg.ToString()), "Errors", "Error(s) occured while decompiling properties", this).Show();
                            break;
                        }
                }
            });
        }

        private void CreateObjectReferencer()
        {
            if (Pcc.Flags.HasFlag(UnrealFlags.EPackageFlags.Map))
            {
                MessageBox.Show(@"Map packages do not use ObjectReferencer; to keep objects in memory, add root objects to ExtraReferencedObjects in TheWorld's binary.");
                return;
            }

            var objRef = Pcc.Exports.FirstOrDefault(x => x.ClassName == "ObjectReferencer" && !x.IsDefaultObject);
            if (objRef != null)
            {
                GoToEntry(objRef.InstancedFullPath);
                return;
            }

            // This part ported from Mass Effect 2 Randomizer
            var rop = new RelinkerOptionsPackage() { Cache = new PackageCache() };
            var referencer = new ExportEntry(Pcc, 0, Pcc.GetNextIndexedName("ObjectReferencer"), properties: [new ArrayProperty<ObjectProperty>("ReferencedObjects")])
            {
                Class = EntryImporter.EnsureClassIsInFile(Pcc, "ObjectReferencer", rop)
            };
            Pcc.AddExport(referencer);
            GoToEntry(referencer.InstancedFullPath);
        }

        private void DeleteEntry()
        {
            TrashEntryAndChildren();
        }

        private void CheckForScriptErrors()
        {
            if (Pcc is null)
            {
                return;
            }
            BusyText = "Checking for Script errors...";
            IsBusy = true;
            Task.Run(() =>
            {
                var errors = new List<EntryStringPair>();

                var fileLib = new FileLib(Pcc);
                UnrealScriptOptionsPackage usop = new UnrealScriptOptionsPackage() { Cache = new PackageCache() };
                using var packageCache = new PackageCache();
                if (fileLib.Initialize(usop))
                {
                    foreach (ExportEntry export in Pcc.Exports)
                    {
                        BusyText = $"{export.UIndex}/{Pcc.ExportCount}";
                        try
                        {
                            if (export.IsClass)
                            {
                                (_, string source) = UnrealScriptCompiler.DecompileExport(export, fileLib, usop);
                                var log = new MessageLog();

                                var (ast, _) = UnrealScriptCompiler.CompileOutlineAST(source, "Class", log, Pcc.Game);
                                if (!log.HasErrors)
                                {
                                    UnrealScriptCompiler.CompileNewClassAST(Pcc, (Class)ast, log, fileLib, out bool vfTableChanged, usop);
                                    if (vfTableChanged)
                                    {
                                        log.LogError("Virtual function table needs to be updated!");
                                    }
                                }
                                if (log.HasErrors)
                                {
                                    errors.Add(new EntryStringPair(export, $"#{export.UIndex,-9}\t{export.InstancedFullPath}:\n{string.Join('\n', log.AllErrors)}"));
                                }
                            }
                            else if (export.ClassName.CaseInsensitiveEquals("Function"))
                            {
                                var funcBin = export.GetBinaryData<UFunction>();
                                if (funcBin.SuperClass != 0 && (Pcc.GetEntry(funcBin.SuperClass) is not IEntry super || super.ObjectName != export.ObjectName))
                                {
                                    errors.Add(new EntryStringPair(export, $"#{export.UIndex,-9}\t{export.InstancedFullPath}:\n SuperClass field in binary refers to an invalid entry!"));
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            errors.Add(new EntryStringPair(export, $"{export.UIndex,-9}\t{export.InstancedFullPath}: EXCEPTION while checking for errors\n{e.FlattenException()}"));
                        }
                    }
                }
                else
                {
                    errors.Add(new EntryStringPair($"FileLib failed to initialize! Errors: \n{string.Join('\n', fileLib.InitializationLog.AllErrors)}"));
                }
                return errors;
            }).ContinueWithOnUIThread(prevTask =>
            {
                IsBusy = false;
                if (prevTask.Result.IsEmpty())
                {
                    MessageBox.Show(this, "No Script Errors found!");
                }
                else
                {
                    new ListDialog(prevTask.Result, "Script errors", "", this)
                    {
                        DoubleClickEntryHandler = entryDoubleClick
                    }.Show();
                }
            });
        }

        private void CheckForInvalidProperties()
        {
            if (Pcc is null)
            {
                return;
            }
            BusyText = "Checking for Property errors...";
            IsBusy = true;
            Task.Run(() =>
            {
                var errors = new List<EntryStringPair>();

                var fileLib = new FileLib(Pcc);
                UnrealScriptOptionsPackage usop = new UnrealScriptOptionsPackage() { Cache = new PackageCache() };
                using var packageCache = new PackageCache();
                if (fileLib.Initialize(usop))
                {
                    var exports = Pcc.Exports.Where(exp => !exp.IsScriptExport() && !exp.IsInDefaultsTree() && !exp.IsTrash()).ToList();
                    int sixteens = 0;
                    for (int i = 0; i < exports.Count; i++)
                    {
                        ExportEntry export = exports[i];
                        int t = i / 16;
                        if (t > sixteens)
                        {
                            sixteens = t;
                            BusyText = $"{i}/{exports.Count}";
                        }
                        try
                        {
                            (var node, string text) = UnrealScriptCompiler.DecompileExport(export, fileLib, usop);
                            if (node is null)
                            {
                                errors.Add(EntryStringPair.FormatMessage(export, ""));
                                continue;
                            }
                            (node, var log) = UnrealScriptCompiler.CompileDefaultProperties(export, text, fileLib, usop, true);
                            if (log.HasErrors || log.HasLexErrors)
                            {
                                errors.Add(EntryStringPair.FormatMessage(export, ""));
                            }
                        }
                        catch (Exception e)
                        {
                            errors.Add(EntryStringPair.FormatMessage(export, ""));
                        }
                    }
                }
                else
                {
                    errors.Add(new EntryStringPair($"FileLib failed to initialize! Errors: \n{string.Join('\n', fileLib.InitializationLog.AllErrors)}"));
                }
                return errors;
            }).ContinueWithOnUIThread(prevTask =>
            {
                IsBusy = false;
                if (prevTask.Result.IsEmpty())
                {
                    MessageBox.Show(this, "No Property Errors found!");
                }
                else
                {
                    new ListDialog(prevTask.Result, "Property errors", "Check the Script Editor tab to see the exact error", this)
                    {
                        DoubleClickEntryHandler = entryDoubleClick
                    }.Show();
                }
            });
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
                TryGetSelectedEntry(out var entry);
                PackageEditorWindow pe = new PackageEditorWindow();
                pe.LoadPackage(otherGen, goToEntry: entry?.InstancedFullPath);
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
            string currentExtension = Path.GetExtension(currentFileName);
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

            TryGetSelectedEntry(out var entry);
            var pe = new PackageEditorWindow();
            pe.LoadFile(counterpartFilePath, goToEntry: entry?.InstancedFullPath);
            pe.Show();
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
                TryGetSelectedEntry(out var entry);
                var pe = new PackageEditorWindow();
                pe.LoadFile(filePath, goToEntry: entry?.InstancedFullPath);
                pe.Show();
            }
        }

        // LECLData is only available on LE game files
        private bool CanEditLECLData() => Pcc != null && Pcc.Game.IsLEGame();

        private void EditLECLData()
        {
            new LECLDataEditorWindow(this, Pcc).ShowDialog();
        }

        private void CreatePackageExport()
        {
            var packName = PromptDialog.Prompt(this, "Enter a package name to create at the root.", "Enter package export name");
            if (string.IsNullOrWhiteSpace(packName))
                return;
            var package = ExportCreator.CreatePackageExport(Pcc, packName);
            GoToNumber(package.UIndex);
        }

        private void CreateObjectRedirector()
        {
#if DEBUG
            if (TryGetSelectedExport(out var exp))
            {
                var objRe = ExportCreator.CreateExport(exp.FileRef, exp.ObjectName, "ObjectRedirector", indexed: false);
                var objReBin = ObjectRedirector.Create();
                objReBin.DestinationObject = exp.UIndex;
                objRe.WriteBinary(objReBin);
                GoToEntry(objRe.InstancedFullPath);
            }
#endif
        }

        private void CreateClass()
        {
            IEntry parent = null;
            string fileName = Path.GetFileName(Pcc.FilePath);
            if (fileName.CaseInsensitiveEquals("Startup_INT.pcc") || !FileLib.PackagesWithTopLevelClasses(Pcc.Game).Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                //not a base file, so classes must be within a package.

                var existingPackages = new List<ExportEntry>();
                foreach (TreeNode<IEntry, int> root in Pcc.Tree.Roots)
                {
                    if (root.Data is ExportEntry exp && exp.ClassName.CaseInsensitiveEquals("Package"))
                    {
                        existingPackages.Add(exp);
                    }
                }

                if (existingPackages.Count is 0)
                {
                    MessageBox.Show(this, "Classes must be child of a Package export. Add one to the file first.");
                    return;
                }

                IEntry defaultParent = null;
                if (TryGetSelectedExport(out var currentExport) && (currentExport.Parent is null && currentExport.ClassName == "Package" || currentExport.Parent is { ClassName: "Package" }))
                {
                    // This will match both cases given the if statement.
                    defaultParent = currentExport.Parent ?? currentExport;
                }
                else
                {
                    defaultParent = Pcc.Exports.FirstOrDefault(exp => exp.IsClass)?.Parent;
                }

                parent = EntrySelector.GetEntry<ExportEntry>(this, Pcc, "Pick a Package export your class should be a child of.",
                    exp => existingPackages.Contains(exp), defaultParent);
                if (parent is null)
                {
                    return;
                }
            }
            var className = PromptDialog.Prompt(this, "Enter the name of your class:", "Class Name", "MyClass", true);
            if (string.IsNullOrWhiteSpace(className))
            {
                return;
            }
            string fullPath = parent is null ? className : $"{parent.InstancedFullPath}.{className}";
            if (Pcc.FindEntry(fullPath) is not null)
            {
                MessageBox.Show(this, $"'{fullPath}' already exists in this file!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UnrealScriptOptionsPackage usop = new UnrealScriptOptionsPackage();
            var fileLib = new FileLib(Pcc);
            if (!fileLib.Initialize(usop))
            {
                var dlg = new ListDialog(fileLib.InitializationLog.AllErrors.Select(msg => msg.ToString()), "Script Error", "Could not build script database for this file!", this);
                dlg.Show();
                return;
            }
            (_, MessageLog log) = UnrealScriptCompiler.CompileClass(Pcc, $"class {className};", fileLib, usop, parent: parent);
            if (log.HasErrors)
            {
                var dlg = new ListDialog(log.AllErrors.Select(msg => msg.ToString()), "Script Error", "Could not create class!", this);
                dlg.Show();
                return;
            }
            CurrentView = CurrentViewMode.Tree;
            GoToNumber(Pcc.FindEntry(fullPath)?.UIndex ?? 0);
        }

        private void CalculateExportMD5()
        {
            if (TryGetSelectedExport(out var ee))
            {
                var hash = MD5.HashData(ee.Data);
                StringBuilder result = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    result.Append(hash[i].ToString("x2"));
                Clipboard.SetText(result.ToString());
            }
        }

        private void ResolveImportsTreeView()
        {
            if (Enumerable.Any(AllTreeViewNodesX))
            {
                Task.Run(() =>
                {
                    var unresolvableImports = new List<EntryStringPair>();
                    BusyText = "Resolving imports";
                    IsBusy = true;

                    var treeNodes = AllTreeViewNodesX[0].FlattenTree().Where(x => x.Entry is ImportEntry);

                    var cache = new PackageCache();
                    foreach (var impTV in treeNodes)
                    {
                        if (impTV.Entry.IsAKnownNativeClass())
                        {
                            impTV.SubText = $"{impTV.Entry.InstancedFullPath.Substring(0, impTV.Entry.InstancedFullPath.IndexOf('.'))}.{(impTV.Game == MEGame.ME1 ? "u" : "pcc")} (Native)";
                        }
                        else
                        {
                            var resolvedExp = EntryImporter.ResolveImport(impTV.Entry as ImportEntry, cache);
                            if (resolvedExp == null)
                            {
                                unresolvableImports.Add(new EntryStringPair(impTV.Entry, $"Unresolvable import: {impTV.Entry.InstancedFullPath}"));
                            }
                            else if (resolvedExp.FileRef.FilePath != null)
                            {
                                var fname = Path.GetFileName(resolvedExp.FileRef.FilePath);
                                impTV.SubText = fname;
                            }
                        }
                    }

                    return unresolvableImports;
                }).ContinueWithOnUIThread(unresolvableImports =>
                {
                    IsBusy = false;
                    if (unresolvableImports.Exception == null)
                    {
                        if (unresolvableImports.Result.Count == 0)
                        {
                            MessageBox.Show("All imports resolved using Legendary Explorer's import resolution algorithm. This does not match how it works in the game and may not be accurate.");
                        }
                        else
                        {
                            ListDialog ld = new ListDialog(unresolvableImports.Result, "Found unresolved imports",
                                "The following imports failed to resolve. This may be due to improperly named files (an issue in LEX, not in the game), or they may be incorrectly named.",
                                this) { DoubleClickEntryHandler = GetEntryDoubleClickAction() };
                        ld.Show();
                    }
                    }
                });
            }
        }

        private void RestoreExportData()
        {
            RestoreExportData(restoreWholeTree: false);
        }

        private void RestoreExportDataForWholeTree()
        {
            RestoreExportData(restoreWholeTree: true);
        }

        private void RestoreExportData(bool restoreWholeTree)
        {
            if (!TryGetSelectedExport(out var selectedExport))
            {
                return;
            }

            var exportsToRestore = restoreWholeTree
                ? selectedExport.GetAllDescendants().OfType<ExportEntry>().Prepend(selectedExport).ToList()
                : [selectedExport];

            if (!Pcc.Game.IsLEGame() && !Pcc.Game.IsOTGame())
            {
                MessageBox.Show(this, "Not a supported file for restoring export data. Only LE/OT files are supported.");
                return;
            }

            Task.Run(() =>
            {
                BusyText = "Finding unmodded candidates...";
                IsBusy = true;
                return SharedPackageTools.GetUnmoddedCandidatesForPackage(this);
            }).ContinueWithOnUIThread(foundCandidates =>
            {
                IsBusy = false;
                if (!foundCandidates.Result.Any())
                {
                    MessageBox.Show(this, "Cannot find any candidates for this file!");
                    return;
                }

                var choices = foundCandidates.Result.DiskFiles.ToList(); //make new list
                choices.AddRange(foundCandidates.Result.SFARPackageStreams.Select(x => x.Key));

                var choice = SharedPackageTools.SelectUnmodifiedComparisonCandidate(this, choices);
                if (string.IsNullOrEmpty(choice))
                {
                    return;
                }

                using var restorePackage = OpenRestoreCandidatePackage(foundCandidates.Result, choice);
                if (restorePackage == null)
                {
                    MessageBox.Show(this, "Could not open the selected unmodded package.");
                    return;
                }

                int restoredCount = 0;
                int skippedCount = 0;
                foreach (var exportToRestore in exportsToRestore)
                {
                    if (!restorePackage.IsUExport(exportToRestore.UIndex))
                    {
                        skippedCount++;
                        continue;
                    }

                    var sourceExport = restorePackage.GetUExport(exportToRestore.UIndex);
                    if (!string.Equals(sourceExport.ClassName, exportToRestore.ClassName, StringComparison.Ordinal))
                    {
                        skippedCount++;
                        continue;
                    }

                    exportToRestore.Data = sourceExport.Data;
                    restoredCount++;
                }

                Preview(true);
                MessageBox.Show(this,
                    restoreWholeTree
                        ? $"Restored export data for {restoredCount} export(s). Skipped {skippedCount}."
                        : restoredCount == 1
                            ? "Restored export data."
                            : $"No export data was restored. Skipped {skippedCount}.");
            });
        }

        private IMEPackage OpenRestoreCandidatePackage(SharedPackageTools.UnmoddedCandidatesLookup candidates, string choice)
        {
            if (candidates.DiskFiles.Contains(choice))
            {
                return MEPackageHandler.OpenMEPackage(choice, forceLoadFromDisk: true);
            }

            if (candidates.SFARPackageStreams.TryGetValue(choice, out Stream packageStream))
            {
                if (packageStream.CanSeek)
                {
                    packageStream.Position = 0;
                }

                return MEPackageHandler.OpenMEPackageFromStream(packageStream, Pcc.FilePath);
            }

            return null;
        }

        private void ExtractEntryToNewPackage()
        {
            if (SelectedItem.Entry is ExportEntry exp)
            {
                SharedPackageTools.ExtractEntryToNewPackage(exp, x => IsBusy = x, x => BusyText = x, GetEntryDoubleClickAction(), this);
            }
        }

        private void FindAllInstancesofClass()
        {
            var classes = Pcc.Exports.Select(x => x.ClassName).NonNull().Distinct().ToList().OrderBy(p => p).ToList();
            var chosenClass = StringSelectorDialog.GetValue(this, "Select a class to list all instances of.", "Class selector", classes, classes.FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(chosenClass))
            {
                var foundExports = Pcc.Exports.Where(x => x.ClassName == chosenClass).ToList();
                // Have to make new EntryStringPair as Entry can be casted into String
                ListDialog ld = new ListDialog(foundExports.Select(x => new EntryStringPair(x, x.InstancedFullPath)),
                    $"Instances of {chosenClass}", $"These are all the exports in this package file that have a class of type {chosenClass}.", this)
                {
                    DoubleClickEntryHandler = entryDoubleClick
                };
                ld.Show();
            }
        }

        private void SetIndicesInTreeToZero()
        {
            if (TreeEntryIsSelected() &&
                MessageBoxResult.Yes ==
                MessageBox.Show(
                    "Are you sure you want to do this? Removing the Indexes from objects can break things if you don't know what you're doing.",
                    "", MessageBoxButton.YesNo, MessageBoxImage.Warning))
            {
                TreeViewEntry selected = (TreeViewEntry)LeftSide_TreeView.SelectedItem;

                IEnumerable<IEntry> itemsTosetIndexTo0 = selected.FlattenTree().Select(tvEntry => tvEntry.Entry);

                foreach (IEntry entry in itemsTosetIndexTo0)
                {
                    entry.indexValue = 0;
                }
            }
        }

        private void OpenImportDefinition()
        {
            if (TryGetSelectedEntry(out IEntry entry) && entry is ImportEntry curImport)
            {
                BusyText = "Attempting to find source of import...";
                IsBusy = true;
                Task.Run(() => EntryImporter.ResolveImport(curImport, new PackageCache())).ContinueWithOnUIThread(prevTask =>
                {
                    IsBusy = false;
                    if (prevTask.Result is ExportEntry res)
                    {
                        var pwpf = new PackageEditorWindow();
                        pwpf.Show();
                        pwpf.LoadEntry(res);
                        pwpf.RestoreAndBringToFront();
                    }
                    else
                    {
                        MessageBox.Show(
                            "Could not find the export that this import references.\nHas the link or name (including parents) of this import been changed?\nDo the filenames match the BioWare naming scheme if it's a BioX file?");
                    }
                });
            }
        }

        public void LoadEntry(IEntry entry)
        {
            LoadFile(entry.FileRef.FilePath, entry.UIndex);
        }

        private void NavigateToEntry(object obj)
        {
            IEntry e = (IEntry)obj;
            GoToNumber(e.UIndex);
        }

        private bool CanNavigateToEntry(object o) => o is IEntry entry && entry.FileRef == Pcc;

        private void GoToArchetype()
        {
            if (TryGetSelectedExport(out ExportEntry export) && export.HasArchetype)
            {
                GoToNumber(export.Archetype.UIndex);
            }
        }

        private bool CanGoToArchetype()
        {
            return TryGetSelectedExport(out ExportEntry exp) && exp.HasArchetype;
        }

        private void OpenExportIn(object obj)
        {
            if (obj is string toolName && TryGetSelectedExport(out ExportEntry exp))
            {
                switch (toolName)
                {
                    case "SequenceEditor":
                        if (TryGetSequenceEditorTargetExport(exp, out var sequenceTarget))
                        {
                            new Sequence_Editor.SequenceEditorWPF(sequenceTarget).Show();
                        }
                        break;
                    case "InterpViewer":
                        if (Timeline.CanParseStatic(exp))
                        {
                            var p = new InterpEditor.InterpEditorWindow();
                            p.Show();
                            p.LoadFile(Pcc.FilePath);
                            if (exp.ObjectName == "InterpData")
                            {
                                p.SelectedInterpData = exp;
                            }
                        }
                        break;
                    case "Soundplorer":
                        if (Soundpanel.CanParseStatic(exp))
                        {
                            new Soundplorer.SoundplorerWPF(exp).Show();
                        }
                        break;
                    case "FaceFXEditor":
                        if (exp.ClassName == "FaceFXAnimSet")
                        {
                            new FaceFXEditor.FaceFXEditorWindow(exp).Show();
                        }
                        break;
                    case "DialogueEditor":
                        if (exp.ClassName == "BioConversation")
                        {
                            new DialogueEditorWindow(exp).Show();
                        }
                        break;
                    case "PathfindingEditor":
                        if (PathfindingEditor.PathfindingEditorWindow.CanParseStatic(exp))
                        {
                            var pf = new PathfindingEditor.PathfindingEditorWindow(exp);
                            pf.Show();
                        }
                        break;
                    case "Meshplorer":
                        if (MeshRenderer.CanParseStatic(exp))
                        {
                            new Meshplorer.MeshplorerWindow(exp).Show();
                        }
                        break;
                    case "PlotEditor":
                        PlotEditorWindow.OpenExportInPlotEditor(exp);
                        break;
                    case "WwiseEditor":
                        if (exp.ClassName == "WwiseBank")
                        {
                            var w = new WwiseEditor.WwiseEditorWindow(exp);
                            w.Show();
                        }
                        break;
                    case "LevelEditor":
                        new LevelEditor.LevelEditor(exp).Show();
                        break;
                    case "GalaxyMapEditor":
                        if (TryGetGalaxyMapEditorTargetExport(exp, out var galaxyMapTarget))
                        {
                            var galaxyMapEditor = new LegendaryExplorer.Tools.GalaxyMapEditor.GalaxyMapEditor();
                            galaxyMapEditor.Show();
                            _ = galaxyMapEditor.LoadFileAndSelectObjectAsync(Pcc.FilePath, galaxyMapTarget.UIndex);
                        }
                        break;
                }
            }
        }

        private static bool TryGetGalaxyMapEditorTargetExport(ExportEntry export, [NotNullWhen(true)] out ExportEntry? galaxyMapTarget)
        {
            for (ExportEntry current = export; current is not null; current = current.Parent as ExportEntry)
            {
                if (LegendaryExplorer.Tools.GalaxyMapEditor.GalaxyMapObjectProxy.IsGalaxyMapClass(current))
                {
                    galaxyMapTarget = current;
                    return true;
                }
            }

            galaxyMapTarget = null;
            return false;
        }

        private static bool CanOpenInSequenceEditor(ExportEntry export)
        {
            return TryGetSequenceEditorTargetExport(export, out _);
        }

        private static bool TryGetSequenceEditorTargetExport(ExportEntry export, [NotNullWhen(true)] out ExportEntry? sequenceTarget)
        {
            if (export.IsA("Sequence"))
            {
                if (export.Parent is ExportEntry parent && parent.IsA("SequenceReference"))
                {
                    sequenceTarget = parent;
                    return true;
                }

                sequenceTarget = export;
                return true;
            }

            if (export.IsA("SequenceObject") || export.IsA("SFXSceneShopGameData"))
            {
                sequenceTarget = export;
                return true;
            }

            for (ExportEntry current = export.Parent as ExportEntry; current is not null; current = current.Parent as ExportEntry)
            {
                if (current.IsA("SFXSceneShopGameData"))
                {
                    sequenceTarget = export;
                    return true;
                }
            }

            sequenceTarget = null;
            return false;
        }

        private bool CanAddInterpGroup(object obj)
        {
            if (!TryGetSelectedExport(out ExportEntry exp) || exp.ClassName != "InterpData")
            {
                return false;
            }

            if (obj is not "Director")
            {
                return true;
            }

            var interpGroups = exp.GetProperty<ArrayProperty<ObjectProperty>>("InterpGroups");
            return interpGroups?.All(groupRef => !Pcc.TryGetUExport(groupRef.Value, out ExportEntry group)
                                                 || group.ClassName is not "InterpGroupDirector" and not "InterpDirector") ?? true;
        }

        private bool CanAddInterpTrack() => TryGetSelectedExport(out ExportEntry exp) && exp.IsA("InterpGroup");

        private bool CanBulkEditInterpGroups() => TryGetSelectedExport(out ExportEntry exp) && exp.ClassName == "InterpData";

        private bool CanShiftInterpTrackMove() => TryGetSelectedExport(out ExportEntry exp) && exp.ClassName == "InterpTrackMove";

        private bool CanOpenGestureImporter() => TryGetSelectedExport(out ExportEntry exp) && exp.ClassName is "BioEvtSysTrackGesture" or "SFXModule_Gestures" or "SFXSkeletalMeshActor" or "SFXSeqAct_SetAmbientPerformance";

        private bool CanShiftSelectedLevelActors() => TryGetSelectedExport(out ExportEntry exp) && exp.ClassName == "Level";

        private void OpenGestureImporter()
        {
            if (TryGetSelectedExport(out ExportEntry exp) && exp.ClassName is "BioEvtSysTrackGesture" or "SFXModule_Gestures" or "SFXSkeletalMeshActor" or "SFXSeqAct_SetAmbientPerformance")
            {
                var dialog = new Dialogs.GestureAnimationImporterDialog(exp, this);
                dialog.ShowDialog();
            }
        }

        private void ShiftSelectedInterpTrackMove()
        {
            if (TryGetSelectedExport(out ExportEntry exp) && exp.ClassName == "InterpTrackMove")
            {
                var dialog = new ShiftInterpTrackDialog();
                if (dialog.ShowDialog() == true)
                {
                    PackageEditorExperimentsM.ShiftInterpTrackMove(exp, dialog.Parameters);
                }
            }
        }

        private void ShiftInterpTrackMovesInSelectedPackage()
        {
            if (TryGetSelectedExport(out ExportEntry packageExp) && packageExp.ClassName == "Package")
            {
                var dialog = new ShiftInterpTrackDialog();
                if (dialog.ShowDialog() == true)
                {
                    var interpTrackMoves = Pcc.Exports.Where(x =>
                        x.ClassName == "InterpTrackMove" && x.IsDescendantOf(packageExp));

                    foreach (var trackMove in interpTrackMoves)
                    {
                        if (!dialog.Parameters.IncludeAnchorObjectMoves)
                        {
                            var moveFrame = trackMove.GetProperty<EnumProperty>("MoveFrame");
                            if (moveFrame != null && moveFrame.Value == "IMF_AnchorObject")
                                continue;
                        }

                        PackageEditorExperimentsM.ShiftInterpTrackMove(trackMove, dialog.Parameters);
                    }
                }
            }
        }

        private void ShiftSelectedLevelActors()
        {
            if (TryGetSelectedExport(out ExportEntry levelExp) && levelExp.ClassName == "Level")
            {
                var dialog = new ShiftInterpTrackDialog(false, false, "Shift Level Actors");
                if (dialog.ShowDialog() == true)
                {
                    PackageEditorExperimentsM.ShiftLevelActors(levelExp, dialog.Parameters);
                }
            }
        }

        private void ShiftInterpTrackMovesInSelectedInterpData()
        {
            if (TryGetSelectedExport(out ExportEntry interpDataExp) && interpDataExp.ClassName == "InterpData")
            {
                var dialog = new ShiftInterpTrackDialog();
                if (dialog.ShowDialog() == true)
                {
                    var interpTrackMoves = Pcc.Exports.Where(x =>
                        x.ClassName == "InterpTrackMove" && x.IsDescendantOf(interpDataExp));

                    foreach (var trackMove in interpTrackMoves)
                    {
                        if (!dialog.Parameters.IncludeAnchorObjectMoves)
                        {
                            var moveFrame = trackMove.GetProperty<EnumProperty>("MoveFrame");
                            if (moveFrame != null && moveFrame.Value == "IMF_AnchorObject")
                                continue;
                        }

                        PackageEditorExperimentsM.ShiftInterpTrackMove(trackMove, dialog.Parameters);
                    }
                }
            }
        }

        private void BulkEditInterpGroups()
        {
            if (TryGetSelectedExport(out ExportEntry exp) && exp.ClassName == "InterpData")
            {
                var dialog = new BulkInterpEditorDialog(this, exp);
                dialog.ShowDialog();
            }
        }

        private void AdjustSelectedInterpTimeOffsets()
        {
            if (!TryGetSelectedExport(out ExportEntry exp))
            {
                return;
            }

            if (exp.ClassName == "InterpData")
            {
                AdjustInterpTimeOffsets(timeOffset => Tools.InterpEditor.InterpTrack.ShiftTimePropertiesUnderExport(exp, timeOffset), "Adjust InterpData Time Offsets");
                return;
            }

            if (exp.IsA("InterpGroup"))
            {
                AdjustInterpTimeOffsets(timeOffset => Tools.InterpEditor.InterpTrack.ShiftTimePropertiesUnderExport(exp, timeOffset), "Adjust InterpGroup Time Offsets");
                return;
            }

            if (Tools.InterpEditor.InterpTrack.IsTimeShiftableTrack(exp))
            {
                AdjustInterpTimeOffsets(timeOffset => Tools.InterpEditor.InterpTrack.ShiftTimeProperties(exp, timeOffset), "Adjust InterpTrack Time Offsets");
            }
        }

        private void AdjustInterpTimeOffsets(Func<float, int> shiftAction, string title)
        {
            var dialog = new ShiftInterpTrackDialog(includeTimeOffset: true, includeAnchorObjectMoves: false, title, includeSpatialOffsets: false)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                shiftAction(dialog.Parameters.TimeOffset);
                Preview();
            }
        }

        private void AddInterpTrack()
        {
            if (TryGetSelectedExport(out ExportEntry exp) && exp.IsA("InterpGroup"))
            {
                if (ClassPickerDlg.GetClass(this, MatineeHelper.GetInterpTracks(Pcc.Game), "Choose Track to Add", "Add") is ClassInfo info)
                {
                    ExportEntry trackExport = MatineeHelper.AddNewTrackToGroup(exp, info.ClassName);
                    MatineeHelper.AddDefaultPropertiesToTrack(trackExport);
                }
            }
        }

        private void AddInterpGroup(object obj)
        {
            if (!CanAddInterpGroup(obj) || !TryGetSelectedExport(out ExportEntry exp))
            {
                return;
            }

            if (obj is "Director")
            {
                MatineeHelper.AddNewGroupDirectorToInterpData(exp);
                return;
            }

            if (PromptDialog.Prompt(this, "Name of InterpGroup:") is string groupName)
            {
                MatineeHelper.AddNewGroupToInterpData(exp, groupName);
            }
        }

        private bool CanAdjustSelectedInterpTimeOffsets()
        {
            if (!TryGetSelectedExport(out ExportEntry exp))
            {
                return false;
            }

            if (exp.ClassName == "InterpData" || exp.IsA("InterpGroup"))
            {
                return true;
            }

            return Tools.InterpEditor.InterpTrack.IsTimeShiftableTrack(exp);
        }

        private bool CanOpenExportIn(object obj)
        {
            if (obj is string toolName && TryGetSelectedExport(out ExportEntry exp) && !exp.IsDefaultObject)
            {
                switch (toolName)
                {
                    case "DialogueEditor":
                        return exp.ClassName == "BioConversation";
                    case "FaceFXEditor":
                        return exp.ClassName == "FaceFXAnimSet";
                    case "Meshplorer":
                        return MeshRenderer.CanParseStatic(exp);
                    case "PlotEditor":
                        return PlotEditorWindow.CanOpenExport(exp);
                    case "PathfindingEditor":
                        return PathfindingEditor.PathfindingEditorWindow.CanParseStatic(exp);
                    case "Soundplorer":
                        return Soundpanel.CanParseStatic(exp);
                    case "SequenceEditor":
                        return CanOpenInSequenceEditor(exp);
                    case "InterpViewer":
                        return exp.ClassName == "InterpData";
                    case "WwiseEditor":
                        return exp.ClassName == "WwiseBank";
                    case "LevelEditor":
                        return exp.ClassName is "Level" or "World" || exp.IsA("Actor") || (exp.ClassName is "StaticMeshComponent" && exp.Parent?.ClassName is "StaticMeshCollectionActor");
                    case "GalaxyMapEditor":
                        return TryGetGalaxyMapEditorTargetExport(exp, out _);
                }
            }

            return false;
        }

        private void ExportEmbeddedFilePrompt()
        {
            ExportEmbeddedFile();
        }

        private void ImportEmbeddedFile()
        {
            MessageBox.Show("Import embedded file is not currently available from Package Editor.");
        }

        private void BulkExportSWFs()
        {
            var swfsInFile = Pcc.Exports.Where(x =>
                x.ClassName == (Pcc.Game == MEGame.ME1 ? "BioSWF" : "GFxMovieInfo") && !x.IsDefaultObject).ToList();
            if (swfsInFile.Count > 0)
            {
                var m = new CommonOpenFileDialog
                {
                    IsFolderPicker = true,
                    EnsurePathExists = true,
                    Title = "Select output folder"
                };
                if (DirectoryMemory.ShowDialog(m, this) == CommonFileDialogResult.Ok)
                {
                    string dir = m.FileName;
                    Stopwatch stopwatch = Stopwatch.StartNew(); //creates and start the instance of Stopwatch
                                                                //your sample code                    
                    foreach (var export in swfsInFile)
                    {
                        string exportFilename = $"{export.FullPath}.swf";
                        string outputPath = Path.Combine(dir, exportFilename);
                        ExportEmbeddedFile(export, outputPath);
                    }

                    stopwatch.Stop();
                    Console.WriteLine(stopwatch.ElapsedMilliseconds);
                }
            }
            else
            {
                MessageBox.Show("This file contains no scaleform exports.");
            }
        }

        private void BulkImportSWFs()
        {
            var swfsInFile = Pcc.Exports.Where(x =>
                x.ClassName == (Pcc.Game == MEGame.ME1 ? "BioSWF" : "GFxMovieInfo") && !x.IsDefaultObject).ToList();
            if (swfsInFile.Count > 0)
            {
                var m = new CommonOpenFileDialog
                {
                    IsFolderPicker = true,
                    EnsurePathExists = true,
                    Title = "Select folder of GFX/SWF files to import"
                };
                if (DirectoryMemory.ShowDialog(m, this) == CommonFileDialogResult.Ok)
                {
                    var bw = new BackgroundWorker();
                    bw.RunWorkerAsync(m.FileName);
                    bw.RunWorkerCompleted += (x, y) =>
                    {
                        IsBusy = false;
                        var ld = new ListDialog((List<EntryStringPair>)y.Result, "Imported Files", "The following files were imported.", this)
                        {
                            DoubleClickEntryHandler = entryDoubleClick
                        };
                        ld.Show();
                    };
                    bw.DoWork += (param, eventArgs) =>
                    {
                        BusyText = "Importing SWFs";
                        IsBusy = true;
                        string dir = (string)eventArgs.Argument;
                        var allfiles = new List<string>();
                        allfiles.AddRange(Directory.GetFiles(dir, "*.swf"));
                        allfiles.AddRange(Directory.GetFiles(dir, "*.gfx"));
                        var importedFiles = new List<EntryStringPair>();
                        foreach (var file in allfiles)
                        {
                            var fullpath = Path.GetFileNameWithoutExtension(file);
                            var matchingExport = swfsInFile.Find(x =>
                                x.FullPath.Equals(fullpath, StringComparison.InvariantCultureIgnoreCase));
                            if (matchingExport != null)
                            {
                                //Import and replace file
                                BusyText = $"Importing {fullpath}";

                                var bytes = File.ReadAllBytes(file);
                                var props = matchingExport.GetProperties();

                                string dataPropName = matchingExport.ClassName == "GFxMovieInfo" ? "RawData" : "Data";
                                var rawData = props.GetProp<ImmutableByteArrayProperty>(dataPropName);
                                //Write SWF data
                                rawData.Bytes = bytes;

                                //Write SWF metadata
                                if (matchingExport.FileRef.Game == MEGame.ME1 ||
                                    matchingExport.FileRef.Game == MEGame.ME2)
                                {
                                    string sourceFilePropName = matchingExport.FileRef.Game != MEGame.ME1
                                        ? "SourceFile"
                                        : "SourceFilePath";
                                    StrProperty sourceFilePath = props.GetProp<StrProperty>(sourceFilePropName);
                                    if (sourceFilePath == null)
                                    {
                                        sourceFilePath = new StrProperty(file, sourceFilePropName);
                                        props.Add(sourceFilePath);
                                    }

                                    sourceFilePath.Value = file;
                                }

                                if (matchingExport.FileRef.Game == MEGame.ME1)
                                {
                                    StrProperty sourceFileTimestamp = props.GetProp<StrProperty>("SourceFileTimestamp");
                                    sourceFileTimestamp = File.GetLastWriteTime(file)
                                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                                }

                                importedFiles.Add(new EntryStringPair(matchingExport,
                                    $"{matchingExport.UIndex} {fullpath}"));
                                matchingExport.WriteProperties(props);
                            }
                        }

                        if (importedFiles.Count == 0)
                        {
                            importedFiles.Add(new EntryStringPair((IEntry)null, "No matching filenames were found."));
                        }

                        eventArgs.Result = importedFiles;
                    };
                }
            }
            else
            {
                MessageBox.Show("This file contains no scaleform exports.");
            }
        }

        private void TabRight()
        {
            int index = EditorTabs.SelectedIndex + 1;
            while (index < EditorTabs.Items.Count)
            {
                TabItem ti = (TabItem)EditorTabs.Items[index];
                if (ti.IsEnabled && ti.IsVisible)
                {
                    EditorTabs.SelectedIndex = index;
                    break;
                }

                index++;
            }
        }

        private void TabLeft()
        {
            int index = EditorTabs.SelectedIndex - 1;
            while (index >= 0)
            {
                TabItem ti = (TabItem)EditorTabs.Items[index];
                if (ti.IsEnabled && ti.IsVisible)
                {
                    EditorTabs.SelectedIndex = index;
                    break;
                }

                index--;
            }
        }

        private void FocusSearch()
        {
            Search_TextBox.Focus();
            Search_TextBox.SelectAll();
        }

        private CancellationTokenSource BeginEntrySearch()
        {
            _entrySearchCancellationTokenSource?.Cancel();
            _entrySearchCancellationTokenSource?.Dispose();
            _entrySearchCancellationTokenSource = new CancellationTokenSource();
            return _entrySearchCancellationTokenSource;
        }

        private async Task<bool> ContinueEntrySearchAsync(
            int numSearched,
            IMEPackage package,
            CurrentViewMode view,
            CancellationToken cancellationToken)
        {
            bool canContinue = !cancellationToken.IsCancellationRequested
                               && ReferenceEquals(Pcc, package)
                               && CurrentView == view;
            if (!canContinue || numSearched == 0 || numSearched % SearchBatchSize != 0)
            {
                return canContinue;
            }

            await Dispatcher.Yield(DispatcherPriority.Background);
            return !cancellationToken.IsCancellationRequested
                   && ReferenceEquals(Pcc, package)
                   && CurrentView == view;
        }

        private void EndEntrySearch(CancellationTokenSource searchCancellation)
        {
            if (!ReferenceEquals(_entrySearchCancellationTokenSource, searchCancellation))
            {
                return;
            }

            _entrySearchCancellationTokenSource = null;
            searchCancellation.Dispose();
        }

        private void FocusGoto()
        {
            Goto_TextBox.Focus();
            Goto_TextBox.SelectAll();
        }

        internal async void SaveFileAs()
        {
            string fileFilter;
            switch (Pcc.Game)
            {
                case MEGame.ME1:
                    fileFilter = GameFileFilters.ME1SaveFileFilter;
                    break;
                case MEGame.ME2:
                case MEGame.ME3:
                    fileFilter = GameFileFilters.ME3ME2SaveFileFilter;
                    break;
                default:
                    string extension = Path.GetExtension(Pcc.FilePath);
                    fileFilter = $"*{extension}|*{extension}";
                    break;
            }

            var d = new SaveFileDialog { Filter = fileFilter, CustomPlaces = AppDirectories.GameCustomPlaces };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                await Pcc.SaveAsync(d.FileName);
                MessageBox.Show("Done");
            }
        }

        private async void SaveFile()
        {
            await Pcc.SaveAsync();
            if (GetSelected(out _))
            {
                Preview(true);
            }
        }

        private void StripShadowmap_Click(object sender, RoutedEventArgs e)
        {
            ExportEntry export = null;
            if (sender is MenuItem { Parent: ContextMenu contextMenu } && TryGetContextMenuExport(contextMenu, out var contextExport))
            {
                export = contextExport;
            }
            else
            {
                TryGetSelectedExport(out export);
            }

            if (!CanStripShadowmap(export))
            {
                MessageBox.Show(this,
                    "This action only works on StaticMeshComponent exports.",
                    "Strip ShadowMap",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PackageEditorExperimentsM.StripShadowmap(export);
            if (ReferenceEquals(export, SelectedItem?.Entry) || GetSelected(out int selectedIndex) && selectedIndex == export.UIndex)
            {
                Preview(true);
            }
        }

        private void OpenFile()
        {
            var d = AppDirectories.GetOpenPackageDialog();
            if (DirectoryMemory.ShowDialog(d) == true)
            {
#if !DEBUG
                try
                {
#endif
                LoadFile(d.FileName);
                //AddRecent(d.FileName, false);
                //SaveRecentList();
                //RefreshRecent(true, RFiles);
#if !DEBUG
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Unable to open file:\n" + ex.Message);
                }
#endif
            }
        }

        private void NewFile()
        {
            var gameDialog = new NewPackageGameDialog(this, "Create new package file", false);
            if (gameDialog.ShowDialog() == true)
            {
                MEGame game = gameDialog.SelectedGame;
                var dlg = new SaveFileDialog
                {
                    Filter = game switch
                    {
                        MEGame.ME1 => GameFileFilters.ME1SaveFileFilter,
                        MEGame.ME2 => GameFileFilters.ME3ME2SaveFileFilter,
                        MEGame.ME3 => GameFileFilters.ME3ME2SaveFileFilter,
                        _ => GameFileFilters.LESaveFileFilter
                    },
                    CustomPlaces = AppDirectories.GameCustomPlaces,
                };
                if (DirectoryMemory.ShowDialog(dlg) == true)
                {
                    MEPackageHandler.CreateAndSavePackage(dlg.FileName, game);
                    LoadFile(dlg.FileName);
                }
            }
        }

        private void NewLevelFile()
        {
            var gameDialog = new NewPackageGameDialog(this, "Create new level file", true);
            if (gameDialog.ShowDialog() == true)
            {
                MEGame game = gameDialog.SelectedGame;
                var dlg = new SaveFileDialog
                {
                    Filter = GameFileFilters.ME3ME2SaveFileFilter,
                    OverwritePrompt = true
                };
                if (game.IsLEGame())
                    dlg.Filter = GameFileFilters.LESaveFileFilter;
                if (game == MEGame.ME1)
                    dlg.Filter = GameFileFilters.ME1SaveFileFilter;

                if (DirectoryMemory.ShowDialog(dlg) == true)
                {
                    (string TopPackageName, string ConversationName)? blankConversationNames = null;
                    if (gameDialog.CreateBlankConversation)
                    {
                        string defaultConversationName = Path.GetFileNameWithoutExtension(dlg.FileName);
                        blankConversationNames = PackageEditorExperimentsScottina.PromptForBlankBioConversationNames(
                            this, defaultConversationName, defaultConversationName);
                        if (blankConversationNames == null)
                        {
                            return;
                        }
                    }

                    string locFilePath = Path.Combine(
                        Path.GetDirectoryName(dlg.FileName)!,
                        $"{Path.GetFileNameWithoutExtension(dlg.FileName)}_LOC_INT{Path.GetExtension(dlg.FileName)}");
                    if (gameDialog.CreateLocFile
                        && File.Exists(locFilePath)
                        && MessageBox.Show($"{Path.GetFileName(locFilePath)} already exists. Overwrite it?",
                            "Create localization file", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        return;
                    }

                    if (File.Exists(dlg.FileName))
                    {
                        File.Delete(dlg.FileName);
                    }

                    MEPackageHandler.CreateEmptyLevel(dlg.FileName, game);

                    if (gameDialog.CreateLocFile)
                    {
                        if (File.Exists(locFilePath))
                        {
                            File.Delete(locFilePath);
                        }

                        using IMEPackage locPackage = MEPackageHandler.CreateAndOpenPackage(locFilePath, game, forceLoadFromDisk: true);
                        locPackage.CreateObjectReferencer();
                        if (gameDialog.CreateBlankConversation)
                        {
                            (ExportEntry bioConversation, List<ExportEntry> referencedExports) = PackageEditorExperimentsScottina.GenerateBlankBioConversationAssets(
                                locPackage, blankConversationNames.Value.TopPackageName, blankConversationNames.Value.ConversationName);
                            locPackage.AddObjectsToReferencer(referencedExports);

                            using IMEPackage levelPackage = MEPackageHandler.OpenMEPackage(
                                dlg.FileName, forceLoadFromDisk: true);
                            var topPackageImport = new ImportEntry(
                                (ExportEntry)bioConversation.Parent, 0, levelPackage);
                            levelPackage.AddImport(topPackageImport);
                            var conversationImport = new ImportEntry(
                                bioConversation, topPackageImport.UIndex, levelPackage);
                            levelPackage.AddImport(conversationImport);
                            levelPackage.Save();
                        }
                        locPackage.Save();
                    }

                    LoadFile(dlg.FileName);
                }
            }
        }

        // This is a coupling hack for splitting the experiments class out. Probably can make this an interface though for more wide-usability
        /// <summary>
        /// Returns a method that can be used in other windows to navigate this instance of Package Editor to a specify entry
        /// </summary>
        /// <returns></returns>
        public Action<EntryStringPair> GetEntryDoubleClickAction() => entryDoubleClick;

        private void entryDoubleClick(EntryStringPair clickedItem)
        {
            if (clickedItem?.Entry != null && clickedItem.Entry.UIndex != 0)
            {
                GoToNumber(clickedItem.Entry.UIndex);
            }
        }

        /// <summary>
        /// Same as <see cref="entryDoubleClick"/>, but navigates to the TreeView first if you're on the names tab
        /// Used in the "Find Usages of Name" list dialog
        /// </summary>
        /// <param name="clickedItem"></param>
        private void entryDoubleClickToTreeview(EntryStringPair clickedItem)
        {
            if (CurrentView is CurrentViewMode.Names)
            {
                SearchHintText = "Object name";
                GotoHintText = "UIndex";
                CurrentView = CurrentViewMode.Tree;
            }
            entryDoubleClick(clickedItem);
        }

        private void nameUsageDoubleClick(EntryStringPair clickedItem)
        {
            if (CurrentView is CurrentViewMode.Names)
            {
                SearchHintText = "Object name";
                GotoHintText = "UIndex";
                CurrentView = CurrentViewMode.Tree;
            }

            entryDoubleClick(clickedItem);

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => SelectNameUsageInRightPanel(clickedItem)));
        }

        private void objectReferenceDoubleClick(EntryStringPair clickedItem)
        {
            if (CurrentView is CurrentViewMode.Names)
            {
                SearchHintText = "Object name";
                GotoHintText = "UIndex";
                CurrentView = CurrentViewMode.Tree;
            }

            entryDoubleClick(clickedItem);

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => SelectObjectReferenceInRightPanel(clickedItem)));
        }

        private void stringRefUsageDoubleClick(EntryStringPair clickedItem)
        {
            if (CurrentView is CurrentViewMode.Names)
            {
                SearchHintText = "Object name";
                GotoHintText = "UIndex";
                CurrentView = CurrentViewMode.Tree;
            }

            entryDoubleClick(clickedItem);

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => SelectStringRefUsageInRightPanel(clickedItem)));
        }

        private void SelectNameUsageInRightPanel(EntryStringPair clickedItem)
        {
            if (clickedItem?.Entry is null || clickedItem.Entry.UIndex == 0)
            {
                return;
            }

            string usageDetail = GetUsageDetail(clickedItem);
            if (string.IsNullOrWhiteSpace(usageDetail))
            {
                return;
            }

            switch (clickedItem.Entry)
            {
                case ExportEntry exportEntry:
                    if (TrySelectPropertyNameUsage(usageDetail))
                    {
                        return;
                    }

                    if (usageDetail == "Component TemplateName (0x4)")
                    {
                        BinaryInterpreter_Tab.IsSelected = true;
                        BinaryInterpreterTab_BinaryInterpreter.SetHexboxSelectedOffset(4);
                        return;
                    }

                    break;
            }
        }

        private void SelectObjectReferenceInRightPanel(EntryStringPair clickedItem)
        {
            if (clickedItem?.Entry is null || clickedItem.Entry.UIndex == 0)
            {
                return;
            }

            string usageDetail = GetUsageDetail(clickedItem);
            if (string.IsNullOrWhiteSpace(usageDetail))
            {
                return;
            }

            if (clickedItem.Entry is not ExportEntry)
            {
                return;
            }

            if (TrySelectPropertyUsage(usageDetail, "Property: "))
            {
                return;
            }

            if (usageDetail == "Stack")
            {
                BinaryInterpreter_Tab.IsSelected = true;
                BinaryInterpreterTab_BinaryInterpreter.SetHexboxSelectedOffset(0);
                return;
            }

            if (TryParseTemplateOwnerClassUsageOffset(usageDetail, out int templateOwnerOffset))
            {
                BinaryInterpreter_Tab.IsSelected = true;
                BinaryInterpreterTab_BinaryInterpreter.SetHexboxSelectedOffset(templateOwnerOffset);
            }
        }

        private void SelectStringRefUsageInRightPanel(EntryStringPair clickedItem)
        {
            if (clickedItem?.Entry is not ExportEntry || clickedItem.Entry.UIndex == 0)
            {
                return;
            }

            string usageDetail = GetUsageDetail(clickedItem);
            if (string.IsNullOrWhiteSpace(usageDetail))
            {
                return;
            }

            string usagePath = ExtractUsagePath(usageDetail, "StringRef: ", "Property: ");
            if (TrySelectExportHeaderNameUsage(usagePath))
            {
                return;
            }

            TrySelectPropertyUsage(usageDetail, "StringRef: ", "Property: ");
        }

        private static string GetUsageDetail(EntryStringPair clickedItem)
        {
            if (clickedItem?.Entry is null || string.IsNullOrEmpty(clickedItem.Message))
            {
                return null;
            }

            string expectedPrefix = $"#{clickedItem.Entry.UIndex} {clickedItem.Entry.ObjectName.Instanced}: ";
            if (clickedItem.Message.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                return clickedItem.Message[expectedPrefix.Length..];
            }

            int separatorIndex = clickedItem.Message.IndexOf(": ", StringComparison.Ordinal);
            return separatorIndex >= 0 ? clickedItem.Message[(separatorIndex + 2)..] : null;
        }

        private bool TrySelectExportHeaderNameUsage(string usageDetail)
        {
            return usageDetail switch
            {
                "Header: Object Name" => SelectMetadataOffset(0xC),
                "Header: ComponentMap" => SelectMetadataOffset(0x28),
                _ => false
            };
        }

        private bool TrySelectExportObjectReferenceHeaderUsage(string usageDetail)
        {
            return usageDetail switch
            {
                "Header: Class" => SelectMetadataOffset(0x0),
                "Header: SuperClass" => SelectMetadataOffset(0x4),
                "Header: Archetype" => SelectMetadataOffset(0x14),
                "Header: ComponentMap" => SelectMetadataOffset(0x28),
                _ => false
            };
        }

        private bool TrySelectImportHeaderNameUsage(string usageDetail)
        {
            return usageDetail switch
            {
                "ObjectName" => SelectMetadataOffset(0x14),
                "PackageFile" => SelectMetadataOffset(0x0),
                "Class" => SelectMetadataOffset(0x8),
                _ => false
            };
        }

        private bool SelectMetadataOffset(long offset)
        {
            Metadata_Tab.IsSelected = true;
            MetadataTab_MetadataEditor.SetHexboxSelectedOffset(offset);
            return true;
        }

        private bool TrySelectPropertyNameUsage(string usageDetail)
        {
            return TrySelectPropertyUsage(usageDetail, "Property: ");
        }

        private bool TrySelectPropertyUsage(string usageDetail, params string[] propertyPrefixes)
        {
            string propertyPath = ExtractUsagePath(usageDetail, propertyPrefixes);

            if (propertyPath is null
                || InterpreterTab_Interpreter.PropertyNodes.Count == 0
                || !TryParsePropertyUsagePath(propertyPath, out var pathSegments))
            {
                return false;
            }

            UPropertyTreeViewEntry current = null;
            foreach (var segment in pathSegments)
            {
                IEnumerable<UPropertyTreeViewEntry> children = current?.ChildrenProperties
                    ?? InterpreterTab_Interpreter.PropertyNodes.SelectMany(node => node.ChildrenProperties);
                current = children.FirstOrDefault(node => node.Property?.Name.Name == segment.PropertyName);
                if (current is null)
                {
                    return false;
                }

                if (segment.ArrayIndex is int arrayIndex)
                {
                    if (arrayIndex < 0 || arrayIndex >= current.ChildrenProperties.Count)
                    {
                        return false;
                    }

                    current.IsExpanded = true;
                    current = current.ChildrenProperties[arrayIndex];
                }
            }

            if (current is null)
            {
                return false;
            }

            current.ExpandParents();
            Interpreter_Tab.IsSelected = true;
            current.IsSelected = true;
            InterpreterTab_Interpreter.SelectedItem = current;
            return true;
        }

        private static string ExtractUsagePath(string usageDetail, params string[] usagePrefixes)
        {
            if (string.IsNullOrWhiteSpace(usageDetail))
            {
                return null;
            }

            foreach (string usagePrefix in usagePrefixes)
            {
                if (!usageDetail.StartsWith(usagePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string usagePath = usageDetail[usagePrefix.Length..];
                int detailSeparatorIndex = usagePath.IndexOf(" | ", StringComparison.Ordinal);
                if (detailSeparatorIndex >= 0)
                {
                    usagePath = usagePath[..detailSeparatorIndex];
                }

                return usagePath.Trim();
            }

            return null;
        }

        private static bool TryParseTemplateOwnerClassUsageOffset(string usageDetail, out int offset)
        {
            const string prefix = "TemplateOwnerClass (Data offset 0x";
            offset = 0;
            if (!usageDetail.StartsWith(prefix, StringComparison.Ordinal) || !usageDetail.EndsWith(')'))
            {
                return false;
            }

            return int.TryParse(usageDetail[prefix.Length..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset);
        }

        private static bool TryParsePropertyUsagePath(string usagePath, out List<NameUsagePropertyPathSegment> pathSegments)
        {
            pathSegments = null;
            if (string.IsNullOrWhiteSpace(usagePath))
            {
                return false;
            }

            string normalizedPath = usagePath.Trim();
            foreach (string suffix in new[] { " function name", " enum type", " enum value", " struct type", " name", " value" })
            {
                if (normalizedPath.EndsWith(suffix, StringComparison.Ordinal))
                {
                    normalizedPath = normalizedPath[..^suffix.Length];
                    break;
                }
            }

            normalizedPath = normalizedPath.Replace(": ", ".", StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return false;
            }

            var parsedSegments = new List<NameUsagePropertyPathSegment>();
            foreach (string rawSegment in normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int bracketIndex = rawSegment.IndexOf('[');
                if (bracketIndex < 0)
                {
                    parsedSegments.Add(new NameUsagePropertyPathSegment(rawSegment, null));
                    continue;
                }

                if (!rawSegment.EndsWith(']')
                    || !int.TryParse(rawSegment[(bracketIndex + 1)..^1], out int arrayIndex))
                {
                    return false;
                }

                parsedSegments.Add(new NameUsagePropertyPathSegment(rawSegment[..bracketIndex], arrayIndex));
            }

            pathSegments = parsedSegments;
            return pathSegments.Count > 0;
        }

        private void PopoutCurrentView()
        {
            if (EditorTabs.SelectedItem is TabItem { Content: ExportLoaderControl exportLoader })
            {
                exportLoader.PopOut();
            }
        }

        private void FindEntryViaTag()
        {
            List<IndexedName> indexedList = Pcc.Names.Select((nr, i) => new IndexedName(i, nr)).ToList();

            const string input = "Select the name of the tag you are trying to find.";
            IndexedName result = NamePromptDialog.Prompt(this, input, "Select tag name", indexedList);

            if (result != null)
            {
                string searchTerm = result.Name;
                bool found = Pcc.Names.Any(x => x.CaseInsensitiveEquals(searchTerm));
                if (found)
                {
                    foreach (ExportEntry exp in Pcc.Exports)
                    {
                        try
                        {
                            var tag = exp.GetProperty<NameProperty>("Tag");
                            if (tag != null &&
                                tag.Value.Name.Equals(searchTerm, StringComparison.InvariantCultureIgnoreCase))
                            {
                                GoToNumber(exp.UIndex);
                                return;
                            }
                        }
                        catch
                        {
                            //skip
                        }
                    }
                }
                else
                {
                    MessageBox.Show(result + " is not a name in the name table.");
                    return;
                }

                MessageBox.Show("Could not find export with Tag property with value: " + result);
            }
        }

        private void SetSelectedAsFilenamePackage()
        {
            if (!TryGetSelectedExport(out ExportEntry export)) return;

            export.PackageGUID = export.FileRef.PackageGuid;

            export.ObjectName = Path.GetFileNameWithoutExtension(export.FileRef.FilePath);
        }

        private void GenerateNewGUIDForSelected()
        {
            if (!TryGetSelectedExport(out ExportEntry export)) return;
            export.PackageGUID = Guid.NewGuid();
        }

        // I think this should be moved to it's own file. Like a PackageInfoWindow class.
        private void ViewPackageInfo()
        {
            var items = new List<string>();
            try
            {
                byte[] header = Pcc.getHeader();
                var ms = new MemoryStream(header);

                uint magicnum = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Magic number: 0x{magicnum:X8}");
                ushort unrealVer = ms.ReadUInt16();
                items.Add($"0x{ms.Position - 2:X2} Unreal version: {unrealVer} (0x{unrealVer:X4})");
                int licenseeVer = ms.ReadUInt16();
                items.Add($"0x{ms.Position - 2:X2} Licensee version:  {licenseeVer} (0x{licenseeVer:X4})");
                uint fullheadersize = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Full header size:  {fullheadersize} (0x{fullheadersize:X8})");
                int foldernameStrLen = ms.ReadInt32();
                items.Add($"0x{ms.Position - 4:X2} Folder name string length: {foldernameStrLen} (0x{foldernameStrLen:X8}) (Negative means Unicode)");
                long currentPosition = ms.Position;
                if (foldernameStrLen > 0)
                {
                    string str = ms.ReadStringLatin1(foldernameStrLen - 1);
                    items.Add($"0x{currentPosition:X2} Folder name:  {str}");
                    ms.ReadByte();
                }
                else
                {
                    string str = ms.ReadStringUnicodeNull(foldernameStrLen * -2);
                    items.Add($"0x{currentPosition:X2} Folder name:  {str}");
                }

                uint flags = ms.ReadUInt32();
                string flagsStr = $"0x{ms.Position - 4:X2} Flags: 0x{flags:X8} ";
                UnrealFlags.EPackageFlags flagEnum = (UnrealFlags.EPackageFlags)flags;
                var setFlags = flagEnum.MaskToList();
                foreach (var setFlag in setFlags)
                {
                    flagsStr += " " + setFlag;
                }

                items.Add(flagsStr);

                if (Pcc.Game is MEGame.ME3 or MEGame.LE3 && Pcc.Flags.HasFlag(UnrealFlags.EPackageFlags.Cooked))
                {
                    uint unknown1 = ms.ReadUInt32();
                    items.Add($"0x{ms.Position - 4:X2} Unknown 1: {unknown1} (0x{unknown1:X8})");
                }

                uint nameCount = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Name Table Count: {nameCount}");

                uint nameOffset = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Name Table Offset: 0x{nameOffset:X8}");

                uint exportCount = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Export Count: {exportCount}");

                uint exportOffset = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Export Metadata Table Offset: 0x{exportOffset:X8}");

                uint importCount = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Import Count: {importCount}");

                uint importOffset = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Import Metadata Table Offset: 0x{importOffset:X8}");

                if (Pcc.Game.IsLEGame() || (Pcc.Game != MEGame.ME1 || Pcc.Platform != MEPackage.GamePlatform.Xenon))
                {
                    uint dependencyTableOffset = ms.ReadUInt32();
                    items.Add($"0x{ms.Position - 4:X2} Dependency Table Offset: 0x{dependencyTableOffset:X8} (Not used in Mass Effect games)");
                }

                if (Pcc.Game >= MEGame.ME3)
                {
                    uint importExportGuidsOffset = ms.ReadUInt32();
                    items.Add($"0x{ms.Position - 4:X2} ImportExportGuidsOffset: 0x{importExportGuidsOffset:X8} (Not used in Mass Effect games)");

                    uint unknown2 = ms.ReadUInt32();
                    items.Add($"0x{ms.Position - 4:X2} ImportGuidsCount: {unknown2} (0x{unknown2:X8}) (Not used in Mass Effect games)");

                    uint unknown3 = ms.ReadUInt32();
                    items.Add($"0x{ms.Position - 4:X2} ExportGuidsCount: {unknown3} (0x{unknown3:X8}) (Not used in Mass Effect games)");
                    uint unknown4 = ms.ReadUInt32();
                    items.Add($"0x{ms.Position - 4:X2} ThumbnailTableOffset: {unknown4} (0x{unknown4:X8}) (Not used in Mass Effect games)");
                }

                var guidBytes = new byte[16];
                ms.Read(guidBytes, 0, 16);
                items.Add($"0x{ms.Position - 16:X2} Package File GUID: {new Guid(guidBytes).ToString()}");

                uint generationsTableCount = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Generations Count: {generationsTableCount}");

                for (int i = 0; i < generationsTableCount; i++)
                {
                    uint generationExportcount = ms.ReadUInt32();
                    items.Add($"0x{ms.Position - 4:X2}   Generation #{i}: Export count: {generationExportcount}");

                    uint generationImportcount = ms.ReadUInt32();
                    items.Add($"0x{ms.Position - 4:X2}   Generation #{i}: Nametable count: {generationImportcount}");

                    uint generationNetcount = ms.ReadUInt32();
                    items.Add(
                        $"0x{ms.Position - 4:X2}   Generation #{i}: Net(worked) object count: {generationNetcount}");
                }

                uint engineVersion = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Engine Version: {engineVersion}");

                uint cookerVersion = ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} CookedContent Version: {cookerVersion}");

                if (Pcc.Game == MEGame.ME2 || Pcc.Game == MEGame.ME1)
                {
                    int unknown2 = ms.ReadInt32();
                    items.Add($"0x{ms.Position - 4:X2} Unknown 2: {unknown2} (0x{unknown2:X8})");

                    int unknown3 = ms.ReadInt32();
                    items.Add($"0x{ms.Position - 4:X2} Static 47699: {unknown3} (0x{unknown3:X8})");

                    if (Pcc.Game == MEGame.ME1)
                    {
                        int static0 = ms.ReadInt32();
                        items.Add($"0x{ms.Position - 4:X2} Static 0: {static0} (0x{static0:X8})");
                        int static1 = ms.ReadInt32();
                        items.Add($"0x{ms.Position - 4:X2} Static 1: {static1} (0x{static1:X8})");
                    }
                    else
                    {
                        int unknown4 = ms.ReadInt32();
                        items.Add($"0x{ms.Position - 4:X2} Unknown 4: {unknown4} (0x{unknown4:X8})");
                        int static1966080 = ms.ReadInt32();
                        items.Add($"0x{ms.Position - 4:X2} Static 1966080: {static1966080} (0x{static1966080:X8})");
                    }
                }

                if (Pcc.Game != MEGame.UDK)
                {
                    int unknown5 = ms.ReadInt32();
                    items.Add($"0x{ms.Position - 4:X2} Unknown 5: {unknown5} (0x{unknown5:X8})");

                    int unknown6 = ms.ReadInt32();
                    items.Add($"0x{ms.Position - 4:X2} Unknown 6: {unknown6} (0x{unknown6:X8})");
                }

                if (Pcc.Game == MEGame.ME1)
                {
                    int unknown7 = ms.ReadInt32();
                    items.Add($"0x{ms.Position - 4:X2} Unknown 7: {unknown7} (0x{unknown7:X8})");
                }

                UnrealPackageFile.CompressionType compressionType = (UnrealPackageFile.CompressionType)ms.ReadUInt32();
                items.Add($"0x{ms.Position - 4:X2} Package Compression Type: {compressionType.ToString()}");

                int numChunks = ms.ReadInt32();
                items.Add($"0x{ms.Position - 4:X2} Number of compressed chunks: {numChunks.ToString()}");

                //read package source
                //var savedPos = ms.Position;
                ms.Skip(numChunks * 16); //skip chunk table so we can find package tag

                var packageSource = ms.ReadUInt32(); //this needs to be read in so it can be properly written back out.
                items.Add($"0x{ms.Position - 4:X4} Package Source: {packageSource:X8}");

                if ((Pcc.Game == MEGame.ME2 || Pcc.Game == MEGame.ME1) && Pcc.Platform != MEPackage.GamePlatform.PS3)
                {
                    var alwaysZero1 =
                        ms.ReadUInt32(); //this needs to be read in so it can be properly written back out.
                    items.Add($"0x{ms.Position - 4:X4} Always zero: {alwaysZero1}");
                }

                if (Pcc.Game is MEGame.ME2 or MEGame.ME3 || Pcc.Game.IsLEGame() || Pcc.Platform == MEPackage.GamePlatform.PS3)
                {
                    int additionalPackagesToCookCount = ms.ReadInt32();
                    items.Add(
                        $"0x{ms.Position - 4:X4} Number of additional packages to cook: {additionalPackagesToCookCount}");
                    //var additionalPackagesToCook = new string[additionalPackagesToCookCount];
                    for (int i = 0; i < additionalPackagesToCookCount; i++)
                    {
                        var pos = ms.Position;
                        var packageStr = ms.ReadUnrealString();
                        items.Add($"0x{pos:X4} Additional package to cook: {packageStr}");
                    }
                }
            }
            catch
            {
            }

            new ListDialog(items, Path.GetFileName(Pcc.FilePath) + " package summary",
                "Below is information about this package from the package summary.", this).Show();
        }

        private void TrashEntryAndChildren(bool includeSelectedEntry = true)
        {
            if (TreeEntryIsSelected())
            {
                var selected = (TreeViewEntry)LeftSide_TreeView.SelectedItem;
                // 06/12/2022 - Change from FullPath.StartsWith() because if somehow trashed object has children (old files, bad experiments, etc) 
                // this prevents removing these items easily
                if (selected.Entry is IEntry ent && ent.ClassName == @"Package" && ent.ObjectName.Name == UnrealPackageFile.TrashPackageName)
                {
                    MessageBox.Show("Cannot trash an already trashed item.");
                    return;
                }

                bool skipReferencesCheck = ShowExperiments &&
                    (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)); // Bypass the check if holding SHIFT

                BusyText = "Performing reference check...";
                IsBusy = true;
                var positionInBranch = includeSelectedEntry ? selected.Parent.Sublinks.IndexOf(selected) : -1;
                var treeViewScrollState = CaptureTreeViewScrollState();
                Task.Run(() =>
                {
                    IEnumerable<TreeViewEntry> treeEntriesToTrash = includeSelectedEntry
                        ? selected.FlattenTree()
                        : selected.Sublinks.SelectMany(child => child.FlattenTree());
                    List<IEntry> itemsToTrash = treeEntriesToTrash.OrderByDescending(x => x.UIndex).Select(tvEntry => tvEntry.Entry).ToList();

                    IEntry entryWithReferences =
                        // Requested by Khaar 05/12/2022
                        // Way to bypass references check as it slows down mass
                        // trashing of objects especially when the dev knows what they're doing
                        // Implemented by Mgamerz 05/14/2022
                        skipReferencesCheck ? null : GetExternallyReferencedEntry(itemsToTrash);
                    return (itemsToTrash, entryWithReferences);
                }).ContinueWithOnUIThread(prevTask =>
                {
                    IsBusy = false;
                    (List<IEntry> itemsToTrash, IEntry entryWithReferences) = prevTask.Result;
                    if (entryWithReferences is not null)
                    {
                        MessageBoxResult messageBoxResult = Xceed.Wpf.Toolkit.MessageBox.Show(this,
                            $"#{entryWithReferences.UIndex} {entryWithReferences.InstancedFullPath} is referenced by other entries! Use the \"{FindReferencesMenuText}\" option in the context menu to see the references. " +
                            "These references will be broken if you trash it! Are you sure you want to proceed?",
                            "Trash warning", MessageBoxButton.YesNo);
                        if (messageBoxResult != MessageBoxResult.Yes)
                        {
                            return;
                        }
                    }

                    if (includeSelectedEntry)
                    {
                        int newSelection = selected.Entry.Parent?.UIndex ?? 0; // The parent
                        if (positionInBranch > 0)
                        {
                            if (selected.Parent.Sublinks.Count > positionInBranch + 1) // Node has not been removed yet from the entry tree so we have to check +1
                            {
                                newSelection = selected.Parent.Sublinks[positionInBranch + 1].UIndex; // Go to the item that will be shifted into our position
                            }
                            else if (positionInBranch > 0) // go to the previous item
                            {
                                newSelection = selected.Parent.Sublinks[positionInBranch - 1].UIndex; // Go to the item that is was before our item
                            }
                        }

                        if (!GoToNumber(newSelection))
                        {
                            AllTreeViewNodesX[0].IsProgramaticallySelecting = true;
                            SelectedItem = AllTreeViewNodesX[0];
                        }
                    }

                    bool removedFromLevel = includeSelectedEntry && selected.Entry is ExportEntry { ParentName: "PersistentLevel" } exp && exp.IsA("Actor") && Pcc.RemoveFromLevelActors(exp);
                    RemoveFromStaticCollectionActors(itemsToTrash);

                    EntryPruner.TrashEntries(Pcc, itemsToTrash);

                    RestoreTreeViewViewport(treeViewScrollState);

                    if (removedFromLevel)
                    {
                        MessageBox.Show(this, "Trashed and removed from level!");
                    }
                });

                static IEntry GetExternallyReferencedEntry(List<IEntry> entriesToTrash)
                {
                    if (entriesToTrash.IsEmpty())
                    {
                        return null;
                    }
                    IMEPackage pcc = entriesToTrash[0].FileRef;
                    MEGame pccGame = pcc.Game;
                    var uIndexes = new HashSet<int>(entriesToTrash.Select(entry => entry.UIndex));

                    foreach (ExportEntry exp in pcc.Exports.Except(entriesToTrash.OfType<ExportEntry>()))
                    {
                        try
                        {
                            //find header references
                            if (uIndexes.Contains(exp.idxArchetype))
                            {
                                return pcc.GetEntry(exp.idxArchetype);
                            }
                            if (uIndexes.Contains(exp.idxClass))
                            {
                                return pcc.GetEntry(exp.idxClass);
                            }
                            if (uIndexes.Contains(exp.idxSuperClass))
                            {
                                return pcc.GetEntry(exp.idxSuperClass);
                            }
                            if (exp.HasComponentMap)
                            {
                                var componentMap = exp.ComponentMap;
                                if (componentMap.Any(kvp => uIndexes.Contains(kvp.Value + 1)))
                                {
                                    return pcc.GetEntry(componentMap.Values.First(idx => uIndexes.Contains(idx + 1)));
                                }
                            }

                            //find stack references
                            if (exp.HasStack)
                            {
                                if (uIndexes.TryGetValue(EndianReader.ToInt32(exp.DataReadOnly, 0, exp.FileRef.Endian), out int stack1))
                                {
                                    return pcc.GetEntry(stack1);
                                }
                                if (uIndexes.TryGetValue(EndianReader.ToInt32(exp.DataReadOnly, 4, exp.FileRef.Endian), out int stack2))
                                {
                                    return pcc.GetEntry(stack2);
                                }
                            }
                            else if (exp.TemplateOwnerClassIdx is var toci and >= 0 &&
                                     uIndexes.TryGetValue(EndianReader.ToInt32(exp.DataReadOnly, toci, exp.FileRef.Endian), out int tocuIdx))
                            {
                                return pcc.GetEntry(tocuIdx);
                            }

                            //find property references
                            if (GetReferencedEntryInProps(exp.GetProperties()) is IEntry entry)
                            {
                                return entry;
                            }

                            //find binary references
                            if (!exp.IsDefaultObject
                                && exp.ClassName != "AnimSequence" //has no UIndexes, and is expensive to deserialize
                                && ObjectBinary.From(exp) is ObjectBinary objBin)
                            {
                                var indices = new List<int>();
                                if (objBin is Level levelBin)
                                {
                                    //trashing a level object will automatically remove it from the Actor list
                                    //so we don't care if it's referenced there
                                    levelBin.ForEachUIndexExceptActorList(pccGame, new UIndexCollector(indices));
                                }
                                else
                                {
                                    objBin.ForEachUIndex(pccGame, new UIndexCollector(indices));
                                }
                                foreach (int uIndex in indices)
                                {
                                    if (uIndexes.Contains(uIndex))
                                    {
                                        return pcc.GetEntry(uIndex);
                                    }
                                }
                            }
                        }
                        catch (Exception e) //when (!App.IsDebug)
                        {
                            MessageBox.Show($"Exception occurred while reading export# {exp.UIndex}: {e.Message}");
                        }
                    }

                    return null;

                    IEntry GetReferencedEntryInProps(PropertyCollection props)
                    {
                        foreach (Property prop in props)
                        {
                            switch (prop)
                            {
                                case ObjectProperty objectProperty:
                                    if (uIndexes.Contains(objectProperty.Value))
                                    {
                                        return pcc.GetEntry(objectProperty.Value);
                                    }
                                    break;
                                case DelegateProperty delegateProperty:
                                    if (uIndexes.Contains(delegateProperty.Value.ContainingObjectUIndex))
                                    {
                                        return pcc.GetEntry(delegateProperty.Value.ContainingObjectUIndex);
                                    }
                                    break;
                                case StructProperty structProperty:
                                    if (GetReferencedEntryInProps(structProperty.Properties) is ExportEntry export1)
                                    {
                                        return export1;
                                    }
                                    break;
                                case ArrayProperty<ObjectProperty> arrayProperty:
                                    foreach (ObjectProperty objProp in arrayProperty)
                                    {
                                        if (uIndexes.Contains(objProp.Value))
                                        {
                                            return pcc.GetEntry(objProp.Value);
                                        }
                                    }
                                    break;
                                case ArrayProperty<StructProperty> arrayProperty:
                                    foreach (StructProperty structProp in arrayProperty)
                                    {
                                        if (GetReferencedEntryInProps(structProp.Properties) is IEntry entry)
                                        {
                                            return entry;
                                        }
                                    }
                                    break;
                            }
                        }
                        return null;
                    }
                }
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global
        public static string FindReferencesMenuText => "Find references";

        private void FindReferencesToObject()
        {
            if (TryGetSelectedEntry(out IEntry entry))
            {
                BusyText = "Finding references...";
                IsBusy = true;
                Task.Run(() => entry.GetEntriesThatReferenceThisOne()).ContinueWithOnUIThread(prevTask =>
                {
                    IsBusy = false;
                    var dlg = new ListDialog(
                            prevTask.Result.SelectMany(kvp => kvp.Value.Select(refName =>
                                new EntryStringPair(kvp.Key,
                                    $"#{kvp.Key.UIndex} {kvp.Key.ObjectName.Instanced}: {refName}"))).ToList(),
                            $"{prevTask.Result.Count} Objects that reference #{entry.UIndex} {entry.InstancedFullPath}",
                            "There may be additional references to this object in the unparsed binary of some objects",
                            this)
                    { DoubleClickEntryHandler = objectReferenceDoubleClick };
                    dlg.Show();
                });
            }
        }

        private void ReindexObjectByName()
        {
            if (!TryGetSelectedExport(out ExportEntry export)) return;
            if (export.FullPath.StartsWith(UnrealPackageFile.TrashPackageName))
            {
                MessageBox.Show(
                    "Cannot reindex exports that are part of ME3ExplorerTrashPackage. All items in this package should have an object index of 0.");
                return;
            }

            ReindexObjectsByName(export, true);
        }

        private void ReindexObjectsByName(ExportEntry exp, bool showUI)
        {
            if (exp != null)
            {
                bool uiConfirm = false;
                string prefixToReindex = exp.ParentInstancedFullPath;
                //if (numItemsInFullPath > 0)
                //{
                //    prefixToReindex = prefixToReindex.Substring(0, prefixToReindex.LastIndexOf('.'));
                //}
                string objectname = exp.ObjectName.Name;
                if (showUI)
                {
                    uiConfirm = MessageBox.Show(
                        $"Confirm reindexing of all exports named {objectname} within the following package path:\n{(string.IsNullOrEmpty(prefixToReindex) ? "Package file root" : prefixToReindex)}\n\n" +
                        $"Only use this reindexing feature for items that are meant to be indexed 1 and above (and not 0) as this tool will force all items to be indexed at 1 or above.\n\n" +
                        $"Ensure this file has a backup, this operation may cause the file to stop working if you use it improperly.",
                        "Confirm Reindexing",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Yes;
                }

                if (!showUI || uiConfirm)
                {
                    // Get list of all exports with that object name.
                    //Filter out duplicates
                    //Get their objectnames from the name list
                    //Order it ascending
                    List<ExportEntry> exports = new List<ExportEntry>();
                    foreach (ExportEntry export in Pcc.Exports)
                    {
                        if (objectname == export.ObjectName.Name && export.ParentInstancedFullPath == prefixToReindex && !export.IsClass)
                        {
                            exports.Add(export);
                        }
                    }

                    // Now we reindex
                    int index = 1; //we'll start at 1.
                    foreach (ExportEntry export in exports)
                    {
                        export.indexValue = index;
                        index++;
                    }

                    //RefreshNames();
                    //RefreshView();
                    //Preview(true);
                }

                if (showUI && uiConfirm)
                {
                    MessageBox.Show($"Objects named \"{objectname}\" under {prefixToReindex} have been reindexed.",
                        "Reindexing completed");
                }
            }
        }

        private void CopyName()
        {
            try
            {
                if (LeftSide_ListView.SelectedItem is IndexedName iName)
                {
                    Clipboard.SetText(iName.Name);
                }
            }
            catch (Exception)
            {
                //don't bother, clippy is not having it today
            }
        }

        private void FindNameUsages()
        {
            if (LeftSide_ListView.SelectedItem is IndexedName iName)
            {
                string name = iName.Name;
                BusyText = $"Finding usages of '{name}'...";
                IsBusy = true;
                Task.Run(() =>
                {
                    var usages = Pcc.FindUsagesOfName(name);
                    var addableUsages = FindAddableNameUsages(name);
                    var movableTextureUsages = FindTextureCacheUsages(name);
                    return (Usages: usages, AddableUsages: addableUsages, MovableTextureUsages: movableTextureUsages);
                }).ContinueWithOnUIThread(prevTask =>
                {
                    IsBusy = false;
                    var dlg = new ListDialog(
                            prevTask.Result.Usages.SelectMany(kvp => kvp.Value.Select(refName =>
                                new EntryStringPair(kvp.Key,
                                    $"#{kvp.Key.UIndex} {kvp.Key.ObjectName.Instanced}: {refName}"))).ToList(),
                            $"{prevTask.Result.Usages.Count} Objects that use '{name}'",
                            "There may be additional usages of this name in the unparsed binary of some objects", this)
                    {
                        DoubleClickEntryHandler = nameUsageDoubleClick,
                        QuinaryActionText = $"Replace editable references to '{name}'",
                        QuinaryActionHandler = () => ReplaceEditableNameReferences(name, prevTask.Result.Usages)
                    };
                    if (prevTask.Result.AddableUsages.Count > 0)
                    {
                        dlg.SecondaryActionText = $"Add another name to {prevTask.Result.AddableUsages.Count} matching array entr{(prevTask.Result.AddableUsages.Count == 1 ? "y" : "ies")}";
                        dlg.SecondaryActionHandler = () => AddNameToMatchingUsages(name, prevTask.Result.AddableUsages);
                        dlg.TertiaryActionText = $"Remove this name from {prevTask.Result.AddableUsages.Count} matching array entr{(prevTask.Result.AddableUsages.Count == 1 ? "y" : "ies")}";
                        dlg.TertiaryActionHandler = () => RemoveNameFromMatchingUsages(name, prevTask.Result.AddableUsages);
                    }
                    if (prevTask.Result.MovableTextureUsages.Count > 0)
                    {
                        dlg.QuaternaryActionText = $"Move {prevTask.Result.MovableTextureUsages.Count} texture{(prevTask.Result.MovableTextureUsages.Count == 1 ? string.Empty : "s")} to another TFC";
                        dlg.QuaternaryActionHandler = () => MoveTexturesToAnotherTfc(name, prevTask.Result.MovableTextureUsages);
                    }
                    dlg.Show();
                });
            }
        }

        private void ReplaceEditableNameReferences(string sourceName, Dictionary<IEntry, List<string>> usages)
        {
            string replacementName = PromptDialog.Prompt(
                this,
                $"Enter the new name for editable references to '{sourceName}'. The original name-table entry will not be renamed.",
                "Replace name references",
                defaultValue: sourceName,
                selectText: true)?.Trim();
            if (string.IsNullOrEmpty(replacementName) || replacementName == sourceName)
            {
                return;
            }

            int usageCount = usages.Sum(usage => usage.Value.Count);
            if (MessageBox.Show(
                    this,
                    $"Replace editable references to '{sourceName}' with '{replacementName}'?\n\nReferences stored only in binary data or read-only type metadata will be left unchanged.",
                    "Replace name references",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            int replacedCount = 0;
            int modifiedEntries = 0;
            foreach (IEntry entry in usages.Keys)
            {
                int entryReplacementCount = 0;
                switch (entry)
                {
                    case ExportEntry export:
                        if (export.ObjectName.Name == sourceName)
                        {
                            export.ObjectName = ReplaceName(export.ObjectName, replacementName);
                            entryReplacementCount++;
                        }

                        PropertyCollection properties = export.GetProperties();
                        entryReplacementCount += ReplacePropertyNameReferences(properties, sourceName, replacementName);
                        if (entryReplacementCount > 0)
                        {
                            export.WriteProperties(properties);
                        }
                        break;
                    case ImportEntry import:
                        if (import.ObjectName.Name == sourceName)
                        {
                            import.ObjectName = ReplaceName(import.ObjectName, replacementName);
                            entryReplacementCount++;
                        }
                        if (import.PackageFile == sourceName)
                        {
                            import.PackageFile = replacementName;
                            entryReplacementCount++;
                        }
                        if (import.ClassName == sourceName)
                        {
                            import.ClassName = replacementName;
                            entryReplacementCount++;
                        }
                        break;
                }

                if (entryReplacementCount > 0)
                {
                    replacedCount += entryReplacementCount;
                    modifiedEntries++;
                }
            }

            if (replacedCount > 0)
            {
                RefreshNames();
                RefreshView();
                Preview(true);
            }

            int unchangedCount = Math.Max(0, usageCount - replacedCount);
            MessageBox.Show(
                this,
                $"Replaced {replacedCount} reference{(replacedCount == 1 ? string.Empty : "s")} across {modifiedEntries} entr{(modifiedEntries == 1 ? "y" : "ies")}.\n" +
                $"Left {unchangedCount} usage{(unchangedCount == 1 ? string.Empty : "s")} unchanged because they are binary-only or use read-only metadata.",
                "Replace name references",
                MessageBoxButton.OK,
                unchangedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            static NameReference ReplaceName(NameReference currentName, string newName) => new(newName, currentName.Number);

            static int ReplacePropertyNameReferences(PropertyCollection properties, string oldName, string newName, bool isInImmutable = false)
            {
                int replacementCount = 0;
                foreach (Property property in properties)
                {
                    if (!isInImmutable && property.Name.Name == oldName)
                    {
                        property.Name = ReplaceName(property.Name, newName);
                        replacementCount++;
                    }

                    switch (property)
                    {
                        case NameProperty nameProperty when nameProperty.Value.Name == oldName:
                            nameProperty.Value = ReplaceName(nameProperty.Value, newName);
                            replacementCount++;
                            break;
                        case DelegateProperty delegateProperty when delegateProperty.Value.FunctionName.Name == oldName:
                            delegateProperty.Value = new ScriptDelegate(
                                delegateProperty.Value.ContainingObjectUIndex,
                                ReplaceName(delegateProperty.Value.FunctionName, newName));
                            replacementCount++;
                            break;
                        case EnumProperty enumProperty when enumProperty.Value.Name == oldName:
                            enumProperty.Value = ReplaceName(enumProperty.Value, newName);
                            replacementCount++;
                            break;
                        case StructProperty structProperty:
                            replacementCount += ReplacePropertyNameReferences(structProperty.Properties, oldName, newName, structProperty.IsImmutable);
                            break;
                        case ArrayProperty<NameProperty> nameArray:
                            foreach (NameProperty arrayElement in nameArray.Where(element => element.Value.Name == oldName))
                            {
                                arrayElement.Value = ReplaceName(arrayElement.Value, newName);
                                replacementCount++;
                            }
                            break;
                        case ArrayProperty<EnumProperty> enumArray:
                            foreach (EnumProperty arrayElement in enumArray.Where(element => element.Value.Name == oldName))
                            {
                                arrayElement.Value = ReplaceName(arrayElement.Value, newName);
                                replacementCount++;
                            }
                            break;
                        case ArrayProperty<StructProperty> structArray:
                            foreach (StructProperty arrayElement in structArray)
                            {
                                replacementCount += ReplacePropertyNameReferences(arrayElement.Properties, oldName, newName, arrayElement.IsImmutable);
                            }
                            break;
                    }
                }

                return replacementCount;
            }
        }

        private List<ExportEntry> FindTextureCacheUsages(string textureCacheName)
        {
            if (Pcc == null || Pcc.Game <= MEGame.ME1 || string.IsNullOrWhiteSpace(textureCacheName))
            {
                return [];
            }

            var matches = new List<ExportEntry>();
            foreach (ExportEntry export in Pcc.Exports.Where(exp => exp.IsTexture()))
            {
                try
                {
                    if (export.GetProperty<NameProperty>("TextureFileCacheName") is { } tfcProp
                        && tfcProp.Value.Name == textureCacheName)
                    {
                        matches.Add(export);
                    }
                }
                catch
                {
                    // Ignore textures that fail to parse so the rest of the package can still be processed.
                }
            }

            return matches;
        }

        private void MoveTexturesToAnotherTfc(string sourceTfcName, List<ExportEntry> textureUsages)
        {
            if (Pcc == null)
            {
                return;
            }

            if (Pcc.Game <= MEGame.ME1)
            {
                MessageBox.Show(this, "Moving textures between TFCs is only supported for ME2/ME3/LE textures.", "Move textures to another TFC");
                return;
            }

            if (textureUsages.Count == 0)
            {
                MessageBox.Show(this, $"No textures referencing '{sourceTfcName}' were found.", "Move textures to another TFC");
                return;
            }

            string preferredTfcName = GetPreferredTextureTfcName() ?? sourceTfcName;
            if (!SelectOrAddNamePromptDialog.Prompt(this,
                    "Select or add the destination TFC name. A new .tfc file will be created automatically if needed.",
                    "Move textures to another TFC",
                    Pcc,
                    out NameReference targetTfcName,
                    new NameReference(preferredTfcName)))
            {
                return;
            }

            string targetTfc = targetTfcName.Name;
            if (!targetTfc.StartsWith("Textures_", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "TFC names must start with 'Textures_'.",
                    "Move textures to another TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (string.Equals(targetTfc, sourceTfcName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "The selected destination TFC matches the current TFC.",
                    "Move textures to another TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (MEDirectories.BasegameTFCs(Pcc.Game).Contains(targetTfc, StringComparer.InvariantCultureIgnoreCase)
                || MEDirectories.OfficialDLC(Pcc.Game).Any(dlc => $"Textures_{dlc}".Equals(targetTfc, StringComparison.InvariantCultureIgnoreCase)))
            {
                MessageBox.Show(this,
                    "Cannot move textures into a BioWare-provided TFC. Choose a different destination TFC.",
                    "Move textures to another TFC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            BusyText = $"Moving textures from '{sourceTfcName}' to '{targetTfc}'...";
            IsBusy = true;
            var texturesToMove = textureUsages
                .Where(export => export?.FileRef == Pcc)
                .Distinct()
                .OrderBy(export => export.UIndex)
                .ToList();

            Task.Run(() => MoveTexturesToAnotherTfcInternal(sourceTfcName, targetTfc, texturesToMove)).ContinueWithOnUIThread(task =>
            {
                IsBusy = false;

                if (task.Exception != null)
                {
                    MessageBox.Show(this,
                        "Error moving textures between TFCs:\n" + task.Exception.FlattenException(),
                        "Move textures to another TFC",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                RefreshNames();
                RefreshView();
                if (texturesToMove.Any(export => export == SelectedItem?.Entry))
                {
                    Preview(true);
                }

                var result = task.Result;
                string summary = $"Moved {result.MovedCount} texture{(result.MovedCount == 1 ? string.Empty : "s")} from '{sourceTfcName}' to '{targetTfc}'.";
                if (result.FailedCount > 0)
                {
                    summary += $" Failed to move {result.FailedCount}.";
                }

                if (result.Messages.Count > 0)
                {
                    new ListDialog(result.Messages,
                        "Move textures to another TFC",
                        summary,
                        this)
                    {
                        DoubleClickEntryHandler = entryDoubleClick
                    }.Show();
                }
                else
                {
                    MessageBox.Show(this,
                        summary,
                        "Move textures to another TFC",
                        MessageBoxButton.OK,
                        result.MovedCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
            });
        }

        private TextureTfcMoveResult MoveTexturesToAnotherTfcInternal(string sourceTfcName, string targetTfcName, List<ExportEntry> texturesToMove)
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "LegendaryExplorer", "MoveTexturesBetweenTfcs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            int movedCount = 0;
            int failedCount = 0;
            var messages = new List<EntryStringPair>();

            try
            {
                foreach (ExportEntry textureExport in texturesToMove)
                {
                    try
                    {
                        var texture = new Texture2D(textureExport);
                        string tempTexturePath = Path.Combine(tempDirectory, $"{textureExport.UIndex:D8}_{SanitizeFileName(textureExport.InstancedFullPath)}.tga");

                        texture.ExportToFile(tempTexturePath);

                        var props = textureExport.GetProperties();
                        TextureImage image = TextureImage.LoadFromFile(tempTexturePath, LegendaryExplorerCore.Textures.PixelFormat.ARGB);
                        List<string> replaceMessages = texture.Replace(image, props, tempTexturePath, forcedTFCName: targetTfcName);

                        movedCount++;
                        messages.Add(new EntryStringPair(textureExport,
                            $"#{textureExport.UIndex} {textureExport.ObjectName.Instanced}: moved from '{sourceTfcName}' to '{targetTfcName}'"));

                        if (replaceMessages.Count > 0)
                        {
                            messages.AddRange(replaceMessages.Select(message =>
                                new EntryStringPair(textureExport,
                                    $"#{textureExport.UIndex} {textureExport.ObjectName.Instanced}: {message}")));
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        messages.Add(new EntryStringPair(textureExport,
                            $"#{textureExport.UIndex} {textureExport.ObjectName.Instanced}: failed to move from '{sourceTfcName}' to '{targetTfcName}' - {ex.Message}"));
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                }
                catch
                {
                    // Temporary files are best-effort cleanup only.
                }
            }

            return new TextureTfcMoveResult(movedCount, failedCount, messages);
        }

        private string GetPreferredTextureTfcName()
        {
            string filePath = Pcc?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath) || Pcc.Game <= MEGame.ME1)
            {
                return null;
            }

            string topLevelFolderName = filePath.DetermineDLCNameFromPath();
            if (string.IsNullOrWhiteSpace(topLevelFolderName))
            {
                for (DirectoryInfo directory = Directory.GetParent(filePath); directory != null; directory = directory.Parent)
                {
                    string normalizedFolderName = directory.Name.NormalizeDLCFolderName();
                    if (!string.IsNullOrWhiteSpace(normalizedFolderName))
                    {
                        topLevelFolderName = normalizedFolderName;
                        break;
                    }
                }
            }

            return string.IsNullOrWhiteSpace(topLevelFolderName)
                ? null
                : $"Textures_{topLevelFolderName}";
        }

        private static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                builder.Append(invalidChars.Contains(c) ? '_' : c);
            }

            return builder.ToString();
        }

        private List<NameArrayUsageMatch> FindAddableNameUsages(string sourceName)
        {
            var matches = new List<NameArrayUsageMatch>();
            foreach (ExportEntry export in Pcc.Exports)
            {
                try
                {
                    CollectAddableNameUsages(export.GetProperties(), export, sourceName, matches, [], "Property: ");
                }
                catch
                {
                    // Ignore exports that fail to parse in the same way the normal usage search does.
                }
            }

            return matches;
        }

        private static void CollectAddableNameUsages(
            PropertyCollection props,
            ExportEntry export,
            string sourceName,
            List<NameArrayUsageMatch> matches,
            List<NameArrayPathSegment> pathSegments,
            string prefix)
        {
            foreach (Property prop in props)
            {
                switch (prop)
                {
                    case StructProperty structProperty:
                        CollectAddableNameUsages(structProperty.Properties, export, sourceName, matches, pathSegments, $"{prefix}{structProperty.Name}: ");
                        break;
                    case ArrayProperty<NameProperty> nameArray:
                        for (int i = 0; i < nameArray.Count; i++)
                        {
                            if (nameArray[i].Value.Name == sourceName)
                            {
                                matches.Add(new NameArrayUsageMatch(export, $"{prefix}{nameArray.Name}[{i}]", nameArray.Name, i, nameArray[i].Value, [.. pathSegments]));
                            }
                        }
                        break;
                    case ArrayProperty<StructProperty> structArray:
                        for (int i = 0; i < structArray.Count; i++)
                        {
                            pathSegments.Add(new NameArrayPathSegment(structArray.Name, i));
                            CollectAddableNameUsages(structArray[i].Properties, export, sourceName, matches, pathSegments, $"{prefix}{structArray.Name}[{i}].");
                            pathSegments.RemoveAt(pathSegments.Count - 1);
                        }
                        break;
                }
            }
        }

        private void AddNameToMatchingUsages(string sourceName, List<NameArrayUsageMatch> addableUsages)
        {
            if (addableUsages.Count == 0)
            {
                MessageBox.Show($"No editable name arrays referencing '{sourceName}' were found.", "No addable usages");
                return;
            }

            if (!NamePromptDialog.Prompt(this,
                    $"Select the specific indexed source name to match. Only locations containing that exact instanced name will be updated.",
                    "Match indexed source name",
                    Pcc,
                    out NameReference sourceIndexedName,
                    Pcc.findName(sourceName)))
            {
                return;
            }

            var filteredUsages = addableUsages.Where(usage => usage.SourceName == sourceIndexedName).ToList();
            if (filteredUsages.Count == 0)
            {
                MessageBox.Show($"No editable name arrays referencing '{sourceIndexedName.Instanced}' were found.", "No matching indexed usages");
                return;
            }

            if (!SelectOrAddNamePromptDialog.Prompt(this,
                    $"Select or add the exact name to append anywhere '{sourceIndexedName.Instanced}' is already present in a name array.",
                    "Add name to matching usages",
                    Pcc,
                    out NameReference targetName,
                    sourceIndexedName))
            {
                return;
            }

            int modifiedExports = 0;
            int addedCount = 0;
            int skippedCount = 0;

            foreach (var usageGroup in filteredUsages.GroupBy(usage => usage.Entry))
            {
                var props = usageGroup.Key.GetProperties();
                bool exportModified = false;

                foreach (var usage in usageGroup.OrderBy(usage => usage.PathKey).ThenByDescending(usage => usage.SourceElementIndex))
                {
                    if (usage.TryApply(props, targetName))
                    {
                        addedCount++;
                        exportModified = true;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }

                if (exportModified)
                {
                    usageGroup.Key.WriteProperties(props);
                    modifiedExports++;
                }
            }

            if (addedCount > 0)
            {
                RefreshNames();
                RefreshView();
                Preview(true);
            }

            MessageBox.Show(
                $"Added '{targetName.Instanced}' to {addedCount} matching array entr{(addedCount == 1 ? "y" : "ies")} that contained '{sourceIndexedName.Instanced}' across {modifiedExports} export{(modifiedExports == 1 ? string.Empty : "s")}.\nSkipped {skippedCount} usage{(skippedCount == 1 ? string.Empty : "s")} that already contained the target name or could not be updated.",
                "Add name to matching usages",
                MessageBoxButton.OK,
                addedCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void RemoveNameFromMatchingUsages(string sourceName, List<NameArrayUsageMatch> removableUsages)
        {
            if (removableUsages.Count == 0)
            {
                MessageBox.Show($"No editable name arrays referencing '{sourceName}' were found.", "No removable usages");
                return;
            }

            if (!NamePromptDialog.Prompt(this,
                    $"Select the specific indexed name to remove. Only exact matching instanced names will be removed from matching arrays.",
                    "Remove name from matching usages",
                    Pcc,
                    out NameReference targetName,
                    Pcc.findName(sourceName)))
            {
                return;
            }

            var filteredUsages = removableUsages.Where(usage => usage.SourceName == targetName).ToList();
            if (filteredUsages.Count == 0)
            {
                MessageBox.Show($"No editable name arrays referencing '{targetName.Instanced}' were found.", "No matching indexed usages");
                return;
            }

            int modifiedExports = 0;
            int removedCount = 0;
            int skippedCount = 0;

            foreach (var usageGroup in filteredUsages.GroupBy(usage => usage.Entry))
            {
                var props = usageGroup.Key.GetProperties();
                bool exportModified = false;

                foreach (var usage in usageGroup.OrderBy(usage => usage.PathKey).ThenByDescending(usage => usage.SourceElementIndex))
                {
                    if (usage.TryRemove(props, targetName))
                    {
                        removedCount++;
                        exportModified = true;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }

                if (exportModified)
                {
                    usageGroup.Key.WriteProperties(props);
                    modifiedExports++;
                }
            }

            if (removedCount > 0)
            {
                RefreshNames();
                RefreshView();
                Preview(true);
            }

            string removedEntrySuffix = removedCount == 1 ? "y" : "ies";
            string modifiedExportSuffix = modifiedExports == 1 ? string.Empty : "s";
            string skippedUsageSuffix = skippedCount == 1 ? string.Empty : "s";
            string resultMessage = "Removed '" + targetName.Instanced + "' from " + removedCount
                + " matching array entr" + removedEntrySuffix
                + " across " + modifiedExports + " export" + modifiedExportSuffix + "."
                + Environment.NewLine
                + "Skipped " + skippedCount + " usage" + skippedUsageSuffix + " that could not be updated.";
            MessageBox.Show(
                resultMessage,
                "Remove name from matching usages",
                MessageBoxButton.OK,
                removedCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private bool DoesSelectedItemHaveEmbeddedFile()
        {
            if (TryGetSelectedExport(out ExportEntry export))
            {
                switch (export.ClassName)
                {
                    case "BioSWF":
                    case "GFxMovieInfo":
                    case "BioTlkFile":
                    case "SoundNodeWave":
                    case "BioSoundNodeWaveStreamingData":
                    case "FaceFXAsset":
                    case "WwiseBank":
                    case "BrushComponent":
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Exports the embedded file in the given export to the given path. If the export given is empty, the one currently selected in the tree is exported.
        /// If the given save path is null, it will prompt the user and say Done when completed in a messagebox.
        /// </summary>
        /// <param name="exp"></param>
        /// <param name="savePath"></param>
        private void ExportEmbeddedFile(ExportEntry exp = null, string savePath = null)
        {
            if (exp == null) TryGetSelectedExport(out exp);
            if (exp != null)
            {
                switch (exp.ClassName)
                {
                    case "BioSWF":
                    case "GFxMovieInfo":
                        {
                            try
                            {
                                var props = exp.GetProperties();
                                string dataPropName = exp.FileRef.Game != MEGame.ME1 ? "RawData" : "Data";
                                var DataProp = props.GetProp<ImmutableByteArrayProperty>(dataPropName);
                                byte[] data = DataProp.Bytes;

                                if (savePath == null)
                                {
                                    //GFX is scaleform extensions for SWF
                                    //SWC is Shockwave Compressed
                                    //SWF is Shockwave Flash (uncompressed)
                                    var d = new SaveFileDialog
                                    {
                                        Title = "Save SWF",
                                        FileName = exp.FullPath + ".swf",
                                        Filter = "*.swf|*.swf"
                                    };
                                    if (DirectoryMemory.ShowDialog(d) == true)
                                    {
                                        File.WriteAllBytes(d.FileName, data);
                                        MessageBox.Show("Done");
                                    }
                                }
                                else
                                {
                                    File.WriteAllBytes(savePath, data);
                                }
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show("Error reading/saving SWF data:\n\n" + ex.FlattenException());
                            }

                            break;
                        }
                    case "BioTlkFile":
                        {
                            string extension = Path.GetExtension(".xml");
                            var d = new SaveFileDialog
                            {
                                Title = "Export TLK as XML",
                                FileName = exp.FullPath + ".xml",
                                Filter = $"*{extension}|*{extension}"
                            };
                            if (DirectoryMemory.ShowDialog(d) == true)
                            {
                                var exportingTalk = new ME1TalkFile(exp);
                                exportingTalk.SaveToXML(d.FileName);
                                MessageBox.Show("Done");
                            }

                            break;
                        }
                    case "SoundNodeWave":
                        {
                            var ob = ObjectBinary.From<SoundNodeWave>(exp);
                            if (ob.RawData == null || !ob.RawData.Any())
                            {
                                MessageBox.Show("This export has no sound data embedded in it.");
                                return;
                            }

                            var d = new CommonOpenFileDialog()
                            {
                                Title = "Select output folder for ICB/ISB",
                                IsFolderPicker = true
                            };

                            if (DirectoryMemory.ShowDialog(d) == CommonFileDialogResult.Ok)
                            {
                                // todo: Change to ISACTBankPair?

                                // ICB
                                var outDir = d.FileName;
                                // todo: Use objectbinary when we implement it
                                var data = new MemoryStream(ob.RawData);
                                // var totalStreamingDataLen = data.ReadInt32();
                                var isbOffset = data.ReadInt32();

                                string icbName = null;

                                // ICB
                                var dataStartPos = data.Position; // RIFF start
                                var riffForDebug = data.ReadStringASCII(0x4); // get riff length
                                var riffLen = data.ReadInt32() + 0x8; // include len and RIFF
                                data.Skip(0x8); // Jump to start of unicode string
                                var strLen = data.ReadInt32();
                                icbName = data.ReadStringUnicodeNull(strLen);

                                data.Position = dataStartPos;
                                using FileStream fs = new FileStream(Path.Combine(outDir, icbName), FileMode.Create);
                                data.CopyToEx(fs, riffLen);

                                // ISB
                                data.Position = isbOffset;

                                var audioName =
                                    exp.ObjectName.Instanced.Substring(exp.ObjectName.Instanced.IndexOf(':') +
                                                                       1); // This is really weak 
                                using FileStream fs2 = new FileStream(
                                    Path.Combine(outDir,
                                        $"{Path.GetFileNameWithoutExtension(icbName)}_{audioName}.isb"),
                                    FileMode.Create);
                                data.Copy(fs2, new byte[2048]);

                                MessageBox.Show("Done");
                            }
                        }
                        break;
                    case "BioSoundNodeWaveStreamingData":
                        {
                            var d = new CommonOpenFileDialog
                            {
                                Title = "Select output folder for ICB/Stripped ISB",
                                IsFolderPicker = true
                            };
                            if (DirectoryMemory.ShowDialog(d) == CommonFileDialogResult.Ok)
                            {
                                // ICB
                                var outDir = d.FileName;

                                var bsnwsd = ObjectBinary.From<BioSoundNodeWaveStreamingData>(exp);
                                var icbBank = bsnwsd.BankPair.ICBBank;
                                var icbName = icbBank.BankChunks.OfType<TitleBankChunk>().FirstOrDefault();

                                using var fs =
                                    new FileStream(
                                        Path.Combine(outDir, Path.GetFileNameWithoutExtension(icbName.Value) + ".icb"),
                                        FileMode.Create);
                                bsnwsd.BankPair.ICBBank.Write(fs);
                                // ISB
                                using var fs2 =
                                    new FileStream(
                                        Path.Combine(outDir, Path.GetFileNameWithoutExtension(icbName.Value) + ".isb"),
                                        FileMode.Create);
                                bsnwsd.BankPair.ISBBank.Write(fs2);

                                MessageBox.Show("Done");
                            }

                            break;
                        }
                    case "FaceFXAsset":
                        {
                            var d = new SaveFileDialog
                            {
                                Title = "Save Face FX Asset",
                                FileName = exp.FullPath + ".fxa",
                                Filter = "*.fxa|*.fxa"
                            };
                            if (DirectoryMemory.ShowDialog(d) == true)
                            {
                                var data = new MemoryStream(exp.GetBinaryData());
                                data.Skip(0x4);
                                using FileStream fs = new FileStream(d.FileName, FileMode.Create);
                                data.CopyToEx(fs, (int)data.Length - 4);
                                MessageBox.Show("Done");
                            }

                            break;
                        }
                    case "WwiseBank":
                        {
                            var wdiag = new SaveFileDialog
                            {
                                Title = "WwiseBank file",
                                FileName = exp.FullPath + ".bnk",
                                Filter = "*.bnk|*.bnk"
                            };
                            if (DirectoryMemory.ShowDialog(wdiag) == true)
                            {
                                var data = new MemoryStream(exp.GetBinaryData());
                                if (exp.Game.IsGame3())
                                {
                                    data.Skip(0x10);
                                }
                                else if (exp.Game.IsGame2())
                                {
                                    data.Skip(0x18);
                                }

                                using FileStream fs = new FileStream(wdiag.FileName, FileMode.Create);
                                data.CopyToEx(fs, (int)data.Length - 0x10);
                                MessageBox.Show("Done");
                            }
                        }
                        break;
                    case "BrushComponent":
                        {
                            var cachedConv = ObjectBinary.From<BrushComponent>(exp);
                            if (cachedConv.CachedPhysBrushData == null ||
                                cachedConv.CachedPhysBrushData.CachedConvexElements == null ||
                                cachedConv.CachedPhysBrushData.CachedConvexElements.Length == 0)
                            {
                                MessageBox.Show("This BrushComponent doesn't have a cached convex hull");
                                break;
                            }

                            var saveDiag = new SaveFileDialog
                            {
                                Title = "Cached Convex Hull Data",
                                FileName = exp.InstancedFullPath + ".phys",
                                Filter = "*.phys|*.phys"
                            };
                            if (DirectoryMemory.ShowDialog(saveDiag) == true)
                            {
                                File.WriteAllBytes(saveDiag.FileName, cachedConv.CachedPhysBrushData.CachedConvexElements[0].ConvexElementData);
                                MessageBox.Show("Done");
                            }
                        }
                        break;
                }
            }
        }

        private void RebuildStreamingLevels()
        {
            try
            {
                var levelStreamingKismets = new List<ExportEntry>();
                ExportEntry bioworldinfo = null;
                foreach (ExportEntry exp in Pcc.Exports)
                {
                    switch (exp.ClassName)
                    {
                        case "BioWorldInfo" when exp.ObjectName == "BioWorldInfo":
                            bioworldinfo = exp;
                            continue;
                        case "LevelStreamingKismet" when exp.ObjectName == "LevelStreamingKismet":
                            levelStreamingKismets.Add(exp);
                            continue;
                    }
                }

                levelStreamingKismets = [.. levelStreamingKismets.OrderBy(o => o.GetProperty<NameProperty>("PackageName").ToString())];
                if (bioworldinfo != null)
                {
                    var streamingLevelsProp =
                        bioworldinfo.GetProperty<ArrayProperty<ObjectProperty>>("StreamingLevels") ??
                        new ArrayProperty<ObjectProperty>("StreamingLevels");

                    streamingLevelsProp.Clear();
                    foreach (ExportEntry exp in levelStreamingKismets)
                    {
                        streamingLevelsProp.Add(new ObjectProperty(exp.UIndex));
                    }

                    bioworldinfo.WriteProperty(streamingLevelsProp);
                    MessageBox.Show("Done.");
                }
                else
                {
                    MessageBox.Show("No BioWorldInfo object found in this file.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error setting streaming levels:\n" + ex.Message);
            }
        }

        private void AddName(object obj)
        {
            const string input = "Enter a new name.";
            string result = PromptDialog.Prompt(this, input, "Enter new name");
            if (!string.IsNullOrEmpty(result))
            {
                if (result.Contains('.'))
                {
                    var sContinue = MessageBox.Show("Names should not contain the '.' unless they are referencing a memory path of an object - these names will break significant amounts of tooling. Do you want to continue to add this name?", ". character breaks LEX", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (sContinue == MessageBoxResult.No)
                    {
                    return;
                }
                }
                int idx = Pcc.FindNameOrAdd(result);
                if (CurrentView == CurrentViewMode.Names)
                {
                    LeftSide_ListView.SelectedIndex = idx;
                }

                if (idx != Pcc.Names.Count - 1)
                {
                    //not the last
                    MessageBox.Show($"{result} already exists in this package file.\nName index: {idx} (0x{idx:X8})",
                        "Name already exists");
                }
                else
                {
                    MessageBox.Show($"{result} has been added as a name.\nName index: {idx} (0x{idx:X8})",
                        "Name added");
                }
            }
        }

        private bool CanAddName(object obj)
        {
            if (obj is string parameter)
            {
                if (parameter == "FromContextMenu")
                {
                    //Ensure we are on names view - used for menu item
                    return PackageIsLoaded() && CurrentView == CurrentViewMode.Names;
                }
            }

            return PackageIsLoaded();
        }

        private bool TreeEntryIsSelected()
        {
            return CurrentView == CurrentViewMode.Tree && EntryIsSelected();
        }

        private bool TreeEntryHasChildren()
        {
            return TreeEntryIsSelected() && LeftSide_TreeView.SelectedItem is TreeViewEntry { Sublinks.Count: > 0 };
        }

        private bool NameIsSelected() =>
            CurrentView == CurrentViewMode.Names && LeftSide_ListView.SelectedItem is IndexedName;

        private void EditName()
        {
            if (LeftSide_ListView.SelectedItem is IndexedName iName)
            {
                string name = iName.Name;
                string input = $"Enter a new name to replace this name ({name}) with.";
                string result =
                    PromptDialog.Prompt(this, input, "Enter new name", defaultValue: name, selectText: true);
                if (!string.IsNullOrEmpty(result))
                {
                    // Before renaming in the name table, find FaceFXAnimSet exports
                    // that use this name, so we can update their internal binary names
                    List<ExportEntry> affectedFxaExports = null;
                    if (Pcc.Game is not MEGame.ME1)
                    {
                        affectedFxaExports = Pcc.Exports
                            .Where(exp => exp.ClassName == "FaceFXAnimSet" && exp.ObjectName.Name == name)
                            .ToList();
                    }

                    Pcc.replaceName(LeftSide_ListView.SelectedIndex, result);

                    // Update FaceFXAnimSet internal binary names to match
                    if (affectedFxaExports is { Count: > 0 })
                    {
                        UpdateFaceFXAnimSetBinaryNames(affectedFxaExports, name, result);
                    }
                }
            }
        }

        /// <summary>
        /// Updates the internal binary names of FaceFXAnimSet exports after a rename.
        /// FaceFXAnimSet stores names internally as strings separate from the name table,
        /// so they must be updated explicitly.
        /// </summary>
        private void UpdateFaceFXAnimSetBinaryNames(List<ExportEntry> fxaExports, string oldName, string newName)
        {
            foreach (var fxaExport in fxaExports)
            {
                try
                {
                    string oldInternalName = oldName;
                    string newInternalName = newName;

                    // FaceFXAnimSet binary stores names without the _M/_F suffix
                    if (oldInternalName.Length >= 2 && oldInternalName[^2..].ToLower() is "_m" or "_f")
                    {
                        oldInternalName = oldInternalName[..^2];
                    }
                    if (newInternalName.Length >= 2 && newInternalName[^2..].ToLower() is "_m" or "_f")
                    {
                        newInternalName = newInternalName[..^2];
                    }

                    var faceFXAnimSet = fxaExport.GetBinaryData<FaceFXAnimSet>();

                    // Update the internal Names list
                    faceFXAnimSet.Names = faceFXAnimSet.Names
                        .Select(n => n == oldInternalName ? newInternalName : n)
                        .ToList();

                    // Update line paths for sound event references
                    var eventRefs = fxaExport.GetProperty<ArrayProperty<ObjectProperty>>("ReferencedSoundCues");
                    if (eventRefs != null)
                    {
                        foreach (var line in faceFXAnimSet.Lines)
                        {
                            if (Pcc.Game.IsGame1())
                            {
                                line.ID = line.ID.Replace(oldName, newName);
                            }

                            ExportEntry soundEvent;
                            if (Pcc.Game is MEGame.ME2)
                            {
                                if (string.IsNullOrEmpty(line.Path)) continue;
                                soundEvent = Pcc.FindExport(line.Path.Replace(oldName, newName, StringComparison.OrdinalIgnoreCase));
                            }
                            else
                            {
                                if (line.Index < 0 || line.Index >= eventRefs.Count || eventRefs[line.Index].Value <= 0) continue;
                                soundEvent = Pcc.GetUExport(eventRefs[line.Index].Value);
                            }

                            if (soundEvent == null) continue;
                            line.Path = soundEvent.FullPath;
                        }
                    }

                    fxaExport.WriteBinary(faceFXAnimSet);
                }
                catch
                {
                    // Skip if binary parsing fails
                }
            }
        }

        private void SearchReplaceNames()
        {
            string searchstr = PromptDialog.Prompt(this, "Input text to be replaced:", "Search and Replace Names",
                defaultValue: "search text", selectText: true);
            if (string.IsNullOrEmpty(searchstr))
                return;

            string replacestr = PromptDialog.Prompt(this, "Input new text:", "Search and Replace Names",
                defaultValue: "replacement text", selectText: true);
            if (string.IsNullOrEmpty(replacestr))
                return;

            var wdlg = MessageBox.Show(
                $"This will replace every name containing the text \"{searchstr}\" with a new name containing \"{replacestr}\".\n" +
                $"This may break any properties, or links containing this string. Please confirm.", "WARNING:",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (wdlg == MessageBoxResult.Cancel)
                return;
            int count = 0;
            for (int i = 0; i < Pcc.Names.Count; i++)
            {
                string name = Pcc.Names[i];
                if (name.Contains(searchstr))
                {
                    var newName = name.Replace(searchstr, replacestr);
                    Pcc.replaceName(i, newName);
                    count++;
                }
            }

            RefreshNames();
            RefreshView();
            Preview(true);
            MessageBox.Show($"{count} names were amended.", "Search and Replace Names", MessageBoxButton.OK);
        }

        private void CheckForBadObjectPropertyReferences()
        {
            if (Pcc == null)
            {
                return;
            }

            ReferenceCheckPackage rcp = new ReferenceCheckPackage();
            EntryChecker.CheckReferences(rcp, Pcc, LECLocalizationShim.NonLocalizedStringConverter);

            var issues = rcp.GetBlockingErrors().Concat(rcp.GetSignificantIssues()).ToList();
            if (issues.Any())
            {
                MessageBox.Show($"{issues.Count} object reference issues were found.", "Reference issues found");
                var lw = new ListDialog(issues.ToList(), $"Reference issues in {Pcc.FilePath}",
                        "The following items have referencing issues. Note that this is a best-effort check and may not be 100% accurate.",
                        this)
                { DoubleClickEntryHandler = objectReferenceDoubleClick };
                lw.Show();
            }
            else
            {
                MessageBox.Show(
                    "No referencing issues were found. Note that this is a best-effort check and may not be 100% accurate and does not account for imports being preloaded in memory before package load.",
                    "Check complete");
            }
        }

        private void CheckForBrokenMaterials()
        {
            if (Pcc == null)
            {
                return;
            }

            var brokenMaterials = ShaderCacheManipulator.GetBrokenMaterials(Pcc);
            if (brokenMaterials.Any())
            {
                var lw = new ListDialog(brokenMaterials.Select(exp => new EntryStringPair(exp)), $"Broken Materials in {Pcc.FilePath}",
                        "The following Materials or MaterialInstances have no corresponding entry in either the local or global shader cache.",
                        this)
                { DoubleClickEntryHandler = entryDoubleClick };
                lw.Show();
            }
            else
            {
                MessageBox.Show("No broken materials were found.",
                    "Check complete");
            }
        }

        private void CheckForDuplicateIndexes()
        {
            if (Pcc == null)
            {
                return;
            }

            var duplicates = EntryChecker.CheckForDuplicateIndices(Pcc);

            if (duplicates.Count > 0)
            {
                string copy = "";
                foreach (var ei in duplicates)
                {
                    copy += ei.Message + "\n";
                }

                //Clipboard.SetText(copy);
                MessageBox.Show(duplicates.Count + " duplicate indexes were found.", "BAD INDEXING");
                ListDialog lw = new ListDialog(duplicates, "Duplicate indexes",
                        "The following items have duplicate indexes. The game may choose to use the first occurrence of the index it finds, or may crash if indexing is checked internally (such as pathfinding). You can reindex an object to force all same named items to be reindexed in the given unique path. You should reindex from the topmost duplicate entry first if one is found, as it may resolve lower item duplicates.",
                        this)
                { DoubleClickEntryHandler = entryDoubleClick };
                lw.Show();
            }
            else
            {
                MessageBox.Show("No duplicate indexes were found.", "Indexing OK");
            }
        }

        private void ReindexDuplicateIndexes()
        {
            if (Pcc == null)
            {
                return;
            }

            if (MessageBox.Show(
                $"This will reindex all objects that have duplicate indexing. Objects this will affect can be seen via `Debugging > Check for duplicate indexes`\n" +
                "If you don't understand what this does, do not do it!\n\n" +
                "Ensure this file has a backup, this operation may cause the file to stop working if you use it improperly.",
                "Confirm Reindexing",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                var duplicatesPackagePathIndexMapping = new Dictionary<string, List<ExportEntry>>();
                foreach (ExportEntry exp in Pcc.Exports)
                {
                    string key = exp.InstancedFullPath;
                    if (key.StartsWith(UnrealPackageFile.TrashPackageName))
                        continue; //Do not report these as requiring re-indexing.
                    if (!duplicatesPackagePathIndexMapping.TryGetValue(key, out List<ExportEntry> indexList))
                    {
                        indexList = [];
                        duplicatesPackagePathIndexMapping[key] = indexList;
                    }

                    indexList.Add(exp);
                }

                foreach (ExportEntry exp in duplicatesPackagePathIndexMapping.Values.Where(list => list.Count > 1)
                    .Select(list => list.First()))
                {
                    ReindexObjectsByName(exp, false);
                }
            }
        }

        private void FindEntryViaOffset()
        {
            if (Pcc == null)
            {
                return;
            }

            string input = "Enter an offset (in hex, e.g. 2FA360) to find what entry contains that offset.";
            string result = PromptDialog.Prompt(this, input, "Enter offset");
            if (result != null)
            {
                try
                {
                    int offsetDec = int.Parse(result, NumberStyles.HexNumber);
                    GotoEntryViaOffset(offsetDec);
                    }
                catch (Exception ex)
                {
                    MessageBox.Show("Error: " + ex.Message);
                }
            }
        }

        private void CloneTree(int numClones)
        {
            if (CurrentView == CurrentViewMode.Tree && TryGetSelectedEntry(out IEntry entry))
            {
                int lastTreeRoot = 0;
                bool? addToInterpList = null;
                var clonedTreeRoots = new List<IEntry>(numClones);
                for (int i = 0; i < numClones; i++)
                {
                    IEntry newTreeRoot = EntryCloner.CloneTree(entry);
                    clonedTreeRoots.Add(newTreeRoot);
                    TryAddToPersistentLevel(newTreeRoot);
                    TryAddToStaticCollectionActor(newTreeRoot, entry);
                    addToInterpList ??= ShouldAddToInterpList(entry);
                    if (addToInterpList == true)
                    {
                        AddToInterpList(newTreeRoot);
                    }
                    lastTreeRoot = newTreeRoot.UIndex;
                }
                TryAddToStreamingLevelsList(clonedTreeRoots);
                GoToNumber(lastTreeRoot);
            }
        }

        private void CloneEntryMultiple()
        {
            var result = PromptDialog.Prompt(this, "How many times do you want to clone this entry?", "Multiple entry cloning", "2", true);
            if (int.TryParse(result, out var howManyTimes) && howManyTimes > 0)
            {
                CloneEntry(howManyTimes);
            }
        }

        private void CloneTreeMultiple()
        {
            var result = PromptDialog.Prompt(this, "How many times do you want to clone this tree?", "Multiple tree cloning", "2", true);
            if (int.TryParse(result, out var howManyTimes) && howManyTimes > 0)
            {
                CloneTree(howManyTimes);
            }
        }

        private void CloneTreeToFolder()
        {
            if (!TryGetSelectedExport(out ExportEntry sourceExport))
                return;

            var dlg = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                EnsurePathExists = true,
                Title = "Select destination folder containing PCC files"
            };

            if (DirectoryMemory.ShowDialog(dlg, this) != CommonFileDialogResult.Ok)
                return;

            string targetFolder = dlg.FileName;

            var filterDlg = new Dialogs.CloneTreeFileFilterDialog(this);
            if (filterDlg.ShowDialog() != true)
                return;

            var filterMode = filterDlg.SelectedMode;

            string extension = Path.GetExtension(Pcc.FilePath);
            var pccFiles = Directory.GetFiles(targetFolder, $"*{extension}", SearchOption.TopDirectoryOnly)
                .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(Pcc.FilePath), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (filterMode == Dialogs.CloneTreeFileFilterDialog.FileFilterMode.LocOnly)
            {
                pccFiles = pccFiles.Where(f => Path.GetFileNameWithoutExtension(f).Contains("_LOC_", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (filterMode == Dialogs.CloneTreeFileFilterDialog.FileFilterMode.BaseOnly)
            {
                pccFiles = pccFiles.Where(f => !Path.GetFileNameWithoutExtension(f).Contains("_LOC_", StringComparison.OrdinalIgnoreCase)).ToList();
            }

            string filterLabel = filterMode switch
            {
                Dialogs.CloneTreeFileFilterDialog.FileFilterMode.LocOnly => "LOC",
                Dialogs.CloneTreeFileFilterDialog.FileFilterMode.BaseOnly => "base (non-LOC)",
                _ => ""
            };

            if (pccFiles.Count == 0)
            {
                MessageBox.Show(this, string.IsNullOrEmpty(filterLabel)
                    ? "No PCC files found in the selected folder."
                    : $"No {filterLabel} PCC files found in the selected folder.",
                    "Clone Tree to Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirmResult = MessageBox.Show(this,
                $"This will clone the export tree rooted at:\n\n{sourceExport.InstancedFullPath}\n\ninto {pccFiles.Count} file(s) in:\n{targetFolder}\n\nProceed?",
                "Confirm Clone Tree to Folder",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes)
                return;

            // Detect if the tree being cloned contains any FaceFXAnimSet exports
            bool treeContainsFxa = false;
            var allEntries = new List<IEntry> { sourceExport };
            allEntries.AddRange(sourceExport.GetAllDescendants());
            var fxaExports = allEntries.OfType<ExportEntry>().Where(e => e.ClassName == "FaceFXAnimSet").ToList();
            treeContainsFxa = fxaExports.Count > 0;

            // If FaceFXAnimSets are being cloned to LOC files, offer to add speakers to conversations
            bool addSpeakersToConvos = false;
            bool? localizeSpeakerFaceFxInConvos = null;
            string speakerTag = null;
            string maleFxaIFP = null;
            string femaleFxaIFP = null;

            if (treeContainsFxa && filterMode != Dialogs.CloneTreeFileFilterDialog.FileFilterMode.BaseOnly)
            {
                var addSpeakerResult = MessageBox.Show(this,
                    "The cloned tree contains FaceFXAnimSet export(s).\n\n" +
                    "Would you like to add a speaker with these shared FXAs to all conversations in each target file?",
                    "Add Speaker to Conversations",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (addSpeakerResult == MessageBoxResult.Yes)
                {
                    var defaultMaleFxa = fxaExports.FirstOrDefault(e => e.InstancedFullPath.EndsWith("_m", StringComparison.OrdinalIgnoreCase));
                    var defaultFemaleFxa = fxaExports.FirstOrDefault(e => e.InstancedFullPath.EndsWith("_f", StringComparison.OrdinalIgnoreCase));
                    var speakerSelections = DialogueEditorExperimentsM.PromptForSharedFxaSelectionAndSpeakerTag(this, Pcc, defaultMaleFxa, defaultFemaleFxa);
                    if (speakerSelections is null)
                    {
                        return;
                    }

                    var (newTag, maleFxa, femaleFxa) = speakerSelections.Value;

                    speakerTag = newTag;
                    maleFxaIFP = maleFxa.InstancedFullPath;
                    femaleFxaIFP = femaleFxa.InstancedFullPath;
                    addSpeakersToConvos = true;
                }
            }

            IsBusy = true;
            BusyText = "Cloning tree to folder...";

            var sourcePackage = Pcc;
            var sourceEntry = sourceExport;

            Task.Run(() =>
            {
                var errors = new List<string>();
                int successCount = 0;

                for (int i = 0; i < pccFiles.Count; i++)
                {
                    string pccFile = pccFiles[i];
                    try
                    {
                        using var destPackage = MEPackageHandler.OpenMEPackage(pccFile, forceLoadFromDisk: true);
                        var rop = new RelinkerOptionsPackage
                        {
                            ImportExportDependencies = true,
                            Cache = new PackageCache()
                        };

                        IEntry parentInDest = null;
                        if (sourceEntry.Parent != null)
                        {
                            parentInDest = destPackage.FindEntry(sourceEntry.Parent.InstancedFullPath);
                            parentInDest ??= EntryExporter.PortParents(sourceEntry, destPackage, cache: rop.Cache, customROP: rop);
                        }

                        rop.CrossPackageMap.Clear();
                        MapParentSequenceToDestination(sourceEntry, parentInDest, rop);
                        var relinkResults = EntryImporter.ImportAndRelinkEntries(
                            EntryImporter.PortingOption.CloneTreeAsChild,
                            sourceEntry,
                            destPackage,
                            parentInDest,
                            true,
                            rop,
                            out IEntry clonedEntry);

                        SynchronizeImportedSequenceObjects(sourceEntry, parentInDest, clonedEntry, rop);

                        if (relinkResults.Any())
                        {
                            errors.Add($"{Path.GetFileName(pccFile)}: {relinkResults.Count} relink issue(s)");
                        }

                        if (addSpeakersToConvos)
                        {
                            int convosModified = DialogueEditorExperimentsM.AddSpeakerWithSharedFXAToAllConvos(
                                destPackage, speakerTag, maleFxaIFP, femaleFxaIFP);
                            if (convosModified < 0)
                            {
                                errors.Add($"{Path.GetFileName(pccFile)}: Could not find FXA export(s) for speaker assignment");
                            }
                            else if (convosModified > 0)
                            {
                                if (!localizeSpeakerFaceFxInConvos.HasValue)
                                {
                                    localizeSpeakerFaceFxInConvos = Dispatcher.Invoke(() =>
                                        MessageBox.Show(this,
                                            "The speaker tag was added to BioConversations. Would you also like to localize that speaker's FaceFX for all BioConversations in each target file?",
                                            "Localize Speaker FaceFX",
                                            MessageBoxButton.YesNo,
                                            MessageBoxImage.Question) == MessageBoxResult.Yes);
                                }

                                if (localizeSpeakerFaceFxInConvos == true)
                                {
                                    DialogueEditorExperimentsS.LocalizeSpeakerFaceFXForAllConversations(destPackage, speakerTag);
                                }
                            }
                        }

                        destPackage.Save();
                        successCount++;

                        Dispatcher.Invoke(() => BusyText = $"Cloning tree to folder... ({i + 1}/{pccFiles.Count})");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Path.GetFileName(pccFile)}: {ex.Message}");
                    }
                }

                return (successCount, errors);
            }).ContinueWithOnUIThread(task =>
            {
                IsBusy = false;
                var (successCount, errors) = task.Result;

                if (errors.Count > 0)
                {
                    var dlg = new ListDialog(
                        errors.Select(e => new EntryStringPair(e)).ToList(),
                        $"Cloned to {successCount}/{pccFiles.Count} files with issues",
                        "The following issues occurred during cloning:",
                        this);
                    dlg.Show();
                }
                else
                {
                    MessageBox.Show(this,
                        $"Successfully cloned export tree into {successCount} file(s).",
                        "Clone Tree to Folder", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
        }

        private void CloneEntry(int numClones)
        {
            if (TryGetSelectedEntry(out IEntry entry))
            {
                int lastClonedUIndex = 0;
                bool? addToInterpList = null;
                var clonedEntries = new List<IEntry>(numClones);
                for (int i = 0; i < numClones; i++)
                {
                    IEntry newEntry = EntryCloner.CloneEntry(entry);
                    clonedEntries.Add(newEntry);
                    if (newEntry is ExportEntry clonedExport)
                    {
                        KismetHelper.SynchronizeSequenceObjectMembership(clonedExport, null, clonedExport.Parent as ExportEntry);
                    }
                    TryAddToPersistentLevel(newEntry);
                    TryAddToStaticCollectionActor(newEntry, entry);
                    addToInterpList ??= ShouldAddToInterpList(entry);
                    if (addToInterpList == true)
                    {
                        AddToInterpList(newEntry);
                    }
                    lastClonedUIndex = newEntry.UIndex;
                }
                TryAddToStreamingLevelsList(clonedEntries);
                GoToNumber(lastClonedUIndex);
            }
        }

        private static void MapParentSequenceToDestination(IEntry sourceEntry, IEntry destinationEntry, RelinkerOptionsPackage rop)
        {
            if (sourceEntry is not ExportEntry sourceExport
                || !sourceExport.IsA("SequenceObject")
                || sourceExport.GetProperty<ObjectProperty>("ParentSequence")?.ResolveToEntry(sourceExport.FileRef) is not ExportEntry sourceParentSequence
                || destinationEntry is not ExportEntry destinationExport)
            {
                return;
            }

            ExportEntry destinationSequence = destinationExport.IsA("Sequence")
                ? destinationExport
                : destinationExport.GetProperty<ObjectProperty>("ParentSequence")?.ResolveToEntry(destinationExport.FileRef) as ExportEntry;
            if (destinationSequence == null)
            {
                return;
            }

            rop.CrossPackageMap[sourceParentSequence] = destinationSequence;
            rop.RelinkMapEntriesToSkip.Add(sourceParentSequence);
        }

        private static void SynchronizeImportedSequenceObjects(IEntry sourceEntry, IEntry destinationEntry, IEntry importedEntry, RelinkerOptionsPackage rop)
        {
            if (sourceEntry is not ExportEntry sourceExport || !sourceExport.IsA("SequenceObject"))
            {
                return;
            }

            ExportEntry sourceSequence = KismetHelper.GetParentSequence(sourceExport);
            if (sourceSequence is null)
            {
                return;
            }

            ExportEntry destinationSequence = destinationEntry as ExportEntry;
            if (destinationSequence is not null && !destinationSequence.IsA("Sequence"))
            {
                destinationSequence = KismetHelper.GetParentSequence(destinationSequence);
            }

            destinationSequence ??= (importedEntry as ExportEntry)?.Parent as ExportEntry;
            if (destinationSequence is null || !destinationSequence.IsA("Sequence"))
            {
                return;
            }

            var importedSequenceObjects = rop.CrossPackageMap
                .Where(pair => pair.Key is ExportEntry mappedSource
                               && mappedSource.IsA("SequenceObject")
                               && KismetHelper.GetParentSequence(mappedSource) == sourceSequence
                               && pair.Value is ExportEntry)
                .Select(pair => (ExportEntry)pair.Value)
                .Append(importedEntry as ExportEntry)
                .Where(export => export?.IsA("SequenceObject") == true)
                .Distinct();

            foreach (ExportEntry importedSequenceObject in importedSequenceObjects)
            {
                importedSequenceObject.idxLink = destinationSequence.UIndex;
                KismetHelper.SynchronizeSequenceObjectMembership(importedSequenceObject, null, destinationSequence);
            }
        }

        /// <summary>
        /// Prompts the user to add a cloned entry to the parent's InterpTracks or InterpGroups list.
        /// Returns true if the user chose Yes, false otherwise.
        /// </summary>
        private bool ShouldAddToInterpList(IEntry originalEntry)
        {
            if (originalEntry.Parent is ExportEntry parentExport)
            {
                if (parentExport.IsA("InterpGroup"))
                {
                    return MessageBox.Show(this,
                        "The cloned object is under an InterpGroup. Would you like to add it to the InterpTracks list?",
                        "Add to InterpTracks",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                }

                if (parentExport.IsA("InterpData"))
                {
                    return MessageBox.Show(this,
                        "The cloned object is under an InterpData. Would you like to add it to the InterpGroups list?",
                        "Add to InterpGroups",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                }
            }

            return false;
        }

        /// <summary>
        /// Adds a cloned entry to its parent's InterpTracks or InterpGroups list.
        /// </summary>
        private void AddToInterpList(IEntry newEntry)
        {
            if (newEntry.Parent is ExportEntry parentExport)
            {
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
        }

        private bool TryAddToPersistentLevel(params IEntry[] newEntries) =>
            TryAddToPersistentLevel((IEnumerable<IEntry>)newEntries);

        private bool TryAddToPersistentLevel(IEnumerable<IEntry> newEntries)
        {
            ExportEntry[] actorsToAdd = newEntries.OfType<ExportEntry>()
                .Where(exp => exp.Parent?.ClassName == "Level" && exp.IsA("Actor")).ToArray();
            int num = actorsToAdd.Length;
            if (num > 0 && Pcc.AddToLevelActorsIfNotThere(actorsToAdd))
            {
                MessageBox.Show(this,
                    $"Added actor{(num > 1 ? "s" : "")} to PersistentLevel's Actor list:\n{actorsToAdd.Select(exp => exp.ObjectName.Instanced).StringJoin("\n")}");
                return true;
            }

            return false;
        }

        private void TryAddToStreamingLevelsList(IEnumerable<IEntry> newEntries)
        {
            if (!newEntries.OfType<ExportEntry>().Any(exp => exp.ClassName == "LevelStreamingKismet" && exp.ObjectName == "LevelStreamingKismet") ||
                !Pcc.Exports.Any(exp => exp.ClassName == "BioWorldInfo" && exp.ObjectName == "BioWorldInfo"))
            {
                return;
            }

            try
            {
                LegendaryExplorer.Misc.ExperimentsTools.SharedMethods.RebuildStreamingLevels(Pcc);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"The LevelStreamingKismet was cloned, but BioWorldInfo.StreamingLevels could not be updated:\n{ex.Message}",
                    "StreamingLevels update failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private bool TryAddToStaticCollectionActor(IEntry newEntry, IEntry originalEntry)
        {
            if (newEntry is ExportEntry
                {
                    Parent: ExportEntry
                    {
                        ClassName: nameof(StaticMeshCollectionActor) or nameof(StaticLightCollectionActor)
                    } scaExp
                } &&
                ObjectBinary.From(scaExp) is StaticCollectionActor scaBin)
            {
                var componentsProp = scaExp.GetProperty<ArrayProperty<ObjectProperty>>(scaBin.ComponentPropName);
                int originalIndex = componentsProp.IndexOf(new ObjectProperty(originalEntry));
                if (originalIndex == -1)
                {
                    return false;
                }
                componentsProp.Add(new ObjectProperty(newEntry));
                scaExp.WriteProperty(componentsProp);

                scaBin.LocalToWorldTransforms.Add(scaBin.LocalToWorldTransforms[originalIndex]);
                scaExp.WriteBinary(scaBin);
                return true;
            }
            return false;
        }

        private void RemoveFromStaticCollectionActors(IEnumerable<IEntry> entriesToTrash)
        {
            var itemsToTrash = entriesToTrash.OfType<ExportEntry>().ToList();
            if (itemsToTrash.Count == 0)
            {
                return;
            }

            var entriesToTrashSet = itemsToTrash.Cast<IEntry>().ToHashSet();
            foreach (var componentGroup in itemsToTrash
                         .Where(exp => exp.Parent is ExportEntry
                         {
                             ClassName: nameof(StaticMeshCollectionActor) or nameof(StaticLightCollectionActor)
                         } parent && !entriesToTrashSet.Contains(parent))
                         .GroupBy(exp => (ExportEntry)exp.Parent))
            {
                ExportEntry scaExp = componentGroup.Key;
                if (ObjectBinary.From(scaExp) is not StaticCollectionActor scaBin)
                {
                    continue;
                }

                var componentsProp = scaExp.GetProperty<ArrayProperty<ObjectProperty>>(scaBin.ComponentPropName);
                if (componentsProp == null || componentsProp.Count == 0)
                {
                    continue;
                }

                HashSet<int> componentUIndexes = [.. componentGroup.Select(exp => exp.UIndex)];
                List<int> indicesToRemove = [];
                for (int i = 0; i < componentsProp.Count; i++)
                {
                    if (componentUIndexes.Contains(componentsProp[i].Value))
                    {
                        indicesToRemove.Add(i);
                    }
                }

                if (indicesToRemove.Count == 0)
                {
                    continue;
                }

                for (int i = indicesToRemove.Count - 1; i >= 0; i--)
                {
                    int indexToRemove = indicesToRemove[i];
                    componentsProp.RemoveAt(indexToRemove);
                    if (scaBin.Components != null && indexToRemove < scaBin.Components.Count)
                    {
                        scaBin.Components.RemoveAt(indexToRemove);
                    }

                    if (scaBin.LocalToWorldTransforms != null && indexToRemove < scaBin.LocalToWorldTransforms.Count)
                    {
                        scaBin.LocalToWorldTransforms.RemoveAt(indexToRemove);
                    }
                }

                scaExp.WriteProperty(componentsProp);
                scaExp.WriteBinary(scaBin);
            }
        }

        private void ImportBinaryData() => ImportExpData(true);

        private void ImportAllData() => ImportExpData(false);

        private void ImportExpData(bool binaryOnly)
        {
            if (!TryGetSelectedExport(out ExportEntry export))
            {
                return;
            }

            OpenFileDialog d = new OpenFileDialog
            {
                Filter = "*.bin|*.bin",
                FileName = export.ObjectName.Instanced + ".bin",
                CustomPlaces = AppDirectories.GameCustomPlaces
            };
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
            }
        }

        private void ExportBinaryData() => ExportExpData(true);

        private void ExportAllData() => ExportExpData(false);

        private void ExportExpData(bool binaryOnly)
        {
            if (!TryGetSelectedExport(out ExportEntry export))
            {
                return;
            }

            SaveFileDialog d = new SaveFileDialog
            {
                Filter = "*.bin|*.bin",
                FileName = export.ObjectName.Instanced + ".bin"
            };
            if (DirectoryMemory.ShowDialog(d) == true)
            {
                File.WriteAllBytes(d.FileName, binaryOnly ? export.GetBinaryData() : export.Data);
                MessageBox.Show("Done.");
            }
        }

        private bool ExportIsSelected() => TryGetSelectedExport(out _);

        private List<IEntry> GetSelectedLinkableEntries()
        {
            if (CurrentView == CurrentViewMode.Tree)
            {
                var treeEntries = _selectedTreeItems
                    .Select(node => node.Entry)
                    .Where(entry => entry?.FileRef == Pcc)
                    .Distinct()
                    .ToList();
                if (treeEntries.Count > 0)
                {
                    return treeEntries;
                }

                return SelectedItem?.Entry is { FileRef: not null } selectedEntry && selectedEntry.FileRef == Pcc
                    ? [selectedEntry]
                    : [];
            }

            if (CurrentView is not (CurrentViewMode.Imports or CurrentViewMode.Exports))
            {
                return [];
            }

            return LeftSide_ListView.SelectedItems
                .OfType<IEntry>()
                .Where(entry => entry.FileRef == Pcc)
                .Distinct()
                .ToList();
        }

        private void ChangeEntryLink(IEntry entry, int newLink)
        {
            if (entry is not ExportEntry exportEntry)
            {
                entry.idxLink = newLink;
                return;
            }

            ExportEntry oldParent = exportEntry.Parent as ExportEntry;
            ExportEntry newParent = newLink == 0 ? null : exportEntry.FileRef.GetEntry(newLink) as ExportEntry;
            if (!ReferenceEquals(oldParent, newParent))
            {
                foreach (ExportEntry movedExport in KismetHelper.MoveConnectedSequenceObjects(exportEntry, oldParent, newParent))
                {
                    MatineeHelper.RemoveFromParentInterpList(movedExport, oldParent);
                    MatineeHelper.AddToParentInterpList(movedExport);
                }
            }
        }

        private void RestoreSelectionAfterLinkChange(List<IEntry> movedEntries)
        {
            if (movedEntries.Count == 0)
            {
                return;
            }

            RunWithDeferredPreview(() =>
            {
                switch (CurrentView)
                {
                    case CurrentViewMode.Tree:
                        if (AllTreeViewNodesX.Count == 0)
                        {
                            return;
                        }

                        int primaryUIndex = movedEntries[0].UIndex;
                        GoToNumber(primaryUIndex);
                        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                        {
                            var movedNodes = AllTreeViewNodesX[0]
                                .FlattenTree()
                                .Where(node => node.Entry is not null && movedEntries.Any(entry => ReferenceEquals(entry, node.Entry)))
                                .ToList();
                            if (movedNodes.Count == 0)
                            {
                                return;
                            }

                            SetTreeMultiSelection(movedNodes, movedNodes[0], updatePrimarySelection: true);
                            EnsureTreeNodeVisible(movedNodes[0]);
                            LeftSide_TreeView.Focus();
                            Keyboard.Focus(LeftSide_TreeView);
                        }));
                        break;
                    case CurrentViewMode.Imports:
                    case CurrentViewMode.Exports:
                        LeftSide_ListView.SelectedItems.Clear();
                        foreach (IEntry entry in movedEntries)
                        {
                            LeftSide_ListView.SelectedItems.Add(entry);
                        }

                        LeftSide_ListView.SelectedItem = movedEntries[0];
                        LeftSide_ListView.Focus();
                        Keyboard.Focus(LeftSide_ListView);
                        LeftSide_ListView.ScrollIntoView(movedEntries[0]);
                        break;
                }
            });
        }

        private void EnsureTreeNodeVisible(TreeViewEntry node)
        {
            if (node is null)
            {
                return;
            }

            node.ExpandParents();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!TryGetTreeViewItem(node, out TreeViewItem item))
                {
                    return;
                }

                Rect targetRect = new(-1000, 0, item.ActualWidth + 1000, item.ActualHeight);
                item.BringIntoView(targetRect);
                item.Focus();
                LeftSide_TreeView.Focus();
                Keyboard.Focus(LeftSide_TreeView);
            }));
        }

        private bool TryGetTreeViewItem(TreeViewEntry node, out TreeViewItem treeViewItem)
        {
            treeViewItem = null;
            if (node is null)
            {
                return false;
            }

            var nodeDynasty = new List<TreeViewEntry> { node };
            for (TreeViewEntry parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                nodeDynasty.Insert(0, parent);
            }

            ItemsControl currentParent = LeftSide_TreeView;
            foreach (TreeViewEntry currentNode in nodeDynasty)
            {
                currentParent.UpdateLayout();
                treeViewItem = currentParent.ItemContainerGenerator.ContainerFromItem(currentNode) as TreeViewItem;
                if (treeViewItem is null)
                {
                    return false;
                }

                currentParent = treeViewItem;
            }

            return treeViewItem is not null;
        }

        private void ChangeLinksForSelectedEntries_Click(object sender, RoutedEventArgs e)
        {
            if (Pcc == null)
            {
                return;
            }

            var selectedEntries = GetSelectedLinkableEntries();
            if (selectedEntries.Count == 0)
            {
                return;
            }

            var selectedUIndexes = selectedEntries.Select(entry => entry.UIndex).ToHashSet();
            var (selectedPackageRoot, selectedEntry) = EntrySelector.GetEntryWithNoOption<IEntry>(
                this,
                Pcc,
                $"Select the new link for {selectedEntries.Count} selected entr{(selectedEntries.Count == 1 ? "y" : "ies")}.",
                entry => !selectedUIndexes.Contains(entry.UIndex));
            if (!selectedPackageRoot && selectedEntry is null)
            {
                return;
            }

            int newLink = selectedPackageRoot ? 0 : selectedEntry.UIndex;
            int updatedCount = 0;
            var failedEntries = new List<EntryStringPair>();

            foreach (IEntry entry in selectedEntries.OrderBy(entry => entry.UIndex))
            {
                try
                {
                    ChangeEntryLink(entry, newLink);
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    failedEntries.Add(new EntryStringPair(entry,
                        $"#{entry.UIndex} {entry.InstancedFullPath}: {ex.Message}"));
                }
            }

            RefreshView();
            LeftSide_ListView.UpdateLayout();
            RestoreSelectionAfterLinkChange(selectedEntries);
            ApplySelectionPreview();

            string targetText = selectedPackageRoot ? "the package root" : $"#{selectedEntry.UIndex} {selectedEntry.InstancedFullPath}";
            string summary = $"Changed the link for {updatedCount} entr{(updatedCount == 1 ? "y" : "ies")} to {targetText}.";
            if (failedEntries.Count > 0)
            {
                summary += $" Failed to update {failedEntries.Count}.";
                new ListDialog(failedEntries,
                    "Change links of selected objects",
                    summary,
                    this)
                {
                    DoubleClickEntryHandler = entryDoubleClick
                }.Show();
                return;
            }

            MessageBox.Show(this,
                summary,
                "Change links of selected objects",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private bool PackageExportIsSelected()
        {
            TryGetSelectedEntry(out IEntry entry);
            return entry?.ClassName == "Package";
        }

        public bool CanRebuildBioWorldStreamingLevels => TryGetSelectedExport(out ExportEntry exp) && exp.ClassName == "BioWorldInfo" && exp.ObjectName == "BioWorldInfo";

        private bool ObjectReferencerIsSelected() => TryGetSelectedExport(out ExportEntry exp) && exp.ClassName == "ObjectReferencer";

        private void AddAllAssetsToReferencer() => PackageEditorExperimentsK.AddAllAssetsToReferencer(this);

        private bool ImportIsSelected() => TryGetSelectedImport(out _);

        private bool EntryIsSelected() => TryGetSelectedEntry(out _);

        private bool PackageIsLoaded() => Pcc != null;

        #endregion

        public PackageEditorWindow() : this(submitTelemetry: true) { }

        public PackageEditorWindow(bool submitTelemetry = true) : base("Package Editor", submitTelemetry)
        {
            CurrentView = CurrentViewMode.Tree;
            LoadCommands();

            InitializeComponent();
            DataContext = this;
            ((FrameworkElement)Resources["EntryContextMenu"]).DataContext = this;

            //map export loaders to their tabs
            ExportLoaders[InterpreterTab_Interpreter] = Interpreter_Tab;
            ExportLoaders[MetadataTab_MetadataEditor] = Metadata_Tab;
            ExportLoaders[SoundTab_Soundpanel] = Sound_Tab;
            ExportLoaders[CurveTab_CurveEditor] = CurveEditor_Tab;
            ExportLoaders[Curve3DTab_CurveEditor] = CurveEditor3D_Tab;
            ExportLoaders[FaceFXTab_Editor] = FaceFXAnimSet_Tab;
            ExportLoaders[Bio2DATab_Bio2DAEditor] = Bio2DAViewer_Tab;
            ExportLoaders[BytecodeTab_BytecodeEditor] = Bytecode_Tab;
            ExportLoaders[BinaryInterpreterTab_BinaryInterpreter] = BinaryInterpreter_Tab;
            ExportLoaders[EmbeddedTextureViewerTab_EmbededTextureViewer] = EmbeddedTextureViewer_Tab;
            ExportLoaders[CollectionActorEditorTab_CollectionActorEditor] = CollectionActorEditor_Tab;
            ExportLoaders[ParticleSystemTab_ParticleSystemLoader] = ParticleSystem_Tab;
            ExportLoaders[ParticleModuleTab_ParticleModuleLoader] = ParticleModule_Tab;
            ExportLoaders[MeshRendererTab_MeshRenderer] = MeshRenderer_Tab;
            ExportLoaders[JPEXLauncherTab_JPEXLauncher] = JPEXLauncher_Tab;
            ExportLoaders[TlkEditorTab_TlkEditor] = TlkEditor_Tab;
            ExportLoaders[MaterialEditorTab_MaterialEditorExportLoader] = MaterialEditor_Tab;
            ExportLoaders[MaterialViewerTab_MaterialExportLoader] = MaterialViewer_Tab;
            ExportLoaders[ScriptTab_UnrealScriptIDE] = Script_Tab;
            ExportLoaders[RADLauncherTab_BIKLauncher] = RADLaunch_Tab;
            ExportLoaders[AnimNodeTab_AnimNodeLoader] = AnimNode_Tab;
            ExportLoaders[ActorPreviewTab_ActorPreviewControl] = ActorPreview_Tab;
            ExportLoaders[GesturePreviewTab_GesturePreview] = GesturePreview_Tab;

            InterpreterTab_Interpreter.SetParentNameList(NamesList); //reference to this control for name editor set

            BinaryInterpreterTab_BinaryInterpreter.SetParentNameList(NamesList); //reference to this control for name editor set
            Bio2DATab_Bio2DAEditor.SetParentNameList(NamesList); //reference to this control for name editor set

            InterpreterTab_Interpreter.UseAssetDatabaseOwnerFriendlyNames = true;
            InterpreterTab_Interpreter.HideHexBox = Settings.PackageEditor_HideInterpreterHexBox;
            InterpreterTab_Interpreter.ToggleHexbox_Button.Visibility = Visibility.Visible;

            RecentsController.InitRecentControl(Toolname, Recents_MenuItem, fileName => LoadFile(fileName));
        }

        /// <summary>
        /// Opens an existing package object, that may have been loaded from somewhere else.
        /// </summary>
        /// <param name="package"></param>
        /// <param name="goToIndex"></param>
        /// <param name="goToEntry"></param>
        public void LoadPackage(IMEPackage package, int goToIndex = 0, string goToEntry = null)
        {
            // Todo: Maybe prompt if there are pending changes to the current package?
            var packageFilePath = package.FilePath;
            try
            {
                preloadPackage(Path.GetFileName(packageFilePath), 0); // Package is already loaded.
                RegisterPackage(package);
                _selectedItem = null; // We change the backing data so we don't fire off a tree event since it checks if Pcc is null.
                if (goToIndex == 0 && !string.IsNullOrWhiteSpace(goToEntry))
                {
                    goToIndex = Pcc.FindEntry(goToEntry)?.UIndex ?? 0;
                }

                postloadPackage(packageFilePath, goToIndex);
                if (File.Exists(packageFilePath))
                {
                    RecentsController.AddRecent(packageFilePath, false, Pcc?.Game);
                    RecentsController.SaveRecentList(true);
                }
            }
            catch (Exception e) when (!App.IsDebug)
            {
                StatusBar_LeftMostText.Text = "Failed to load " + Path.GetFileName(packageFilePath);
                MessageBox.Show($"Error loading {Path.GetFileName(packageFilePath)}:\n{e.Message}");
                IsBusy = false;
                IsBusyTaskbar = false;
                //throw e;
            }
        }

        public void LoadFile(string s, int goToIndex = 0, string goToEntry = null)
        {
            // Todo: Maybe prompt if there are pending changes to the current package?
            try
            {
                preloadPackage(Path.GetFileName(s), new FileInfo(s).Length);
                LoadMEPackage(s);
                _selectedItem = null; // We change the backing data so we don't fire off a tree event since it checks if Pcc is null.
                if (goToIndex == 0 && !string.IsNullOrWhiteSpace(goToEntry))
                {
                    goToIndex = Pcc.FindEntry(goToEntry)?.UIndex ?? 0;
                }
                postloadPackage(s, goToIndex);

                RecentsController.AddRecent(s, false, Pcc?.Game);
                RecentsController.SaveRecentList(true);
            }
            catch (Exception e) when (!App.IsDebug)
            {
                StatusBar_LeftMostText.Text = "Failed to load " + Path.GetFileName(s);
                MessageBox.Show($"Error loading {Path.GetFileName(s)}:\n{e.Message}");
                IsBusy = false;
                IsBusyTaskbar = false;
                //throw e;
            }
        }

        /// <summary>
        /// Call once the MEPackage has been loaded and set
        /// </summary>
        private void postloadPackage(string filePath, int goToIndex = 0)
        {
            _comparedChangedEntryIndices.Clear();
            RefreshView();
            InitStuff();
            StatusBar_LeftMostText.Text = GetStatusBarText();
            Title = $"Package Editor - {filePath}";
            InterpreterTab_Interpreter.UnloadExport();

            QueuedGotoNumber = goToIndex;

            BuildFaceFXNameCache();
            InitializeTreeView();
        }

        /// <summary>
        /// Builds the FaceFXAnimSet name cache for detecting renames.
        /// </summary>
        private void BuildFaceFXNameCache()
        {
            _faceFXAnimSetNameCache.Clear();
            if (Pcc is not null && Pcc.Game is not MEGame.ME1)
            {
                foreach (var exp in Pcc.Exports)
                {
                    if (exp.ClassName == "FaceFXAnimSet")
                    {
                        _faceFXAnimSetNameCache[exp.UIndex] = exp.ObjectName.Name;
                    }
                }
            }
        }

        /// <summary>
        /// Call this before loading an ME Package to clear the UI up and show the loading interface
        /// </summary>
        /// <param name="loadingName"></param>
        /// <param name="loadingSize"></param>
        private void preloadPackage(string loadingName, long loadingSize)
        {
            CancelPendingPreview();
            ClearTreeMultiSelection();
            _treeSelectionAnchor = null;
            BusyText = $"Loading {loadingName}";
            IsBusy = true;
            IsLoadingFile = true;
            foreach (KeyValuePair<ExportLoaderControl, TabItem> entry in ExportLoaders)
            {
                entry.Value.Visibility = Visibility.Collapsed;
            }

            Metadata_Tab.Visibility = Visibility.Collapsed;
            Intro_Tab.Visibility = Visibility.Visible;
            Intro_Tab.IsSelected = true;

            ResetTreeView();
            NamesList.ClearEx();
            SelectedClassSearch = null;
            BackwardsEntries.ClearEx();
            ForwardsEntries.ClearEx();
            StatusBar_LeftMostText.Text = $"Loading {loadingName} ({FileSize.FormatSize(loadingSize)})";
            //Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle, null);
        }

        private void InitializeTreeViewBackground_Completed(Task<List<TreeViewEntry>> prevTask)
        {
            if (prevTask.Exception == null && prevTask.Result != null)
            {
                ResetTreeView();
                AllTreeViewNodesX.AddRange(prevTask.Result);
            }

            IsLoadingFile = false;
            if (QueuedGotoNumber != 0)
            {
                //Wait for UI to render
                Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle, null);
                BusyText = $"Navigating to {QueuedGotoNumber}";

                GoToNumber(QueuedGotoNumber);
                Goto_TextBox.Text = QueuedGotoNumber.ToString();
                if (QueuedGotoNumber > 0)
                {
                    Interpreter_Tab.IsSelected = true;
                }

                QueuedGotoNumber = 0;
                IsBusy = false;
            }
            else
            {
                IsBusy = false;
            }
        }

        private List<TreeViewEntry> InitializeTreeViewBackground()
        {
            BusyText = "Loading " + Path.GetFileName(Pcc.FilePath);
            if (Pcc == null)
            {
                return null;
            }

            IReadOnlyList<ImportEntry> Imports = Pcc.Imports;
            IReadOnlyList<ExportEntry> Exports = Pcc.Exports;

            var rootEntry = new TreeViewEntry(null, Path.GetFileName(Pcc.FilePath)) { IsExpanded = true, PackageRef = Pcc };

            var rootNodes = new List<TreeViewEntry> { rootEntry };
            rootNodes.AddRange(Exports.Select(t => new TreeViewEntry(t)));
            rootNodes.AddRange(Imports.Select(t => new TreeViewEntry(t)));

            //configure links
            //Order: 0 = Root, [Exports], [Imports], <extra, new stuff>
            var itemsToRemove = new List<TreeViewEntry>();
            foreach (TreeViewEntry entry in rootNodes)
            {
                if (entry.Entry != null)
                {
                    int tvLink = entry.Entry.idxLink;
                    if (tvLink < 0)
                    {
                        //import
                        //Debug.WriteLine("import tvlink " + tvLink);

                        tvLink = Exports.Count + Math.Abs(tvLink);
                        //Debug.WriteLine("Linking " + entry.Entry.GetFullPath + " to index " + tvLink);
                    }

                    TreeViewEntry parent = rootNodes[tvLink];
                    parent.Sublinks.Add(entry);
                    entry.Parent = parent;
                    itemsToRemove.Add(entry); //remove from this level as we have added it to another already
                }
            }

            foreach (TreeViewEntry node in rootNodes)
            {
                node.SortChildren();
            }

            return new List<TreeViewEntry>(rootNodes.Except(itemsToRemove));
        }

        private void InitializeTreeView()
        {
            IsBusy = true;
            if (Pcc == null)
            {
                return;
            }

            Task.Run(InitializeTreeViewBackground)
                .ContinueWithOnUIThread(InitializeTreeViewBackground_Completed);
        }

        /// <summary>
        /// Updates the data bindings for tree/list view and chagnes visibility of the tree/list view depending on what the currentview mode is. Also forces refresh of all treeview display names
        /// </summary>
        private void RefreshView()
        {
            if (Pcc == null)
            {
                return;
            }

            if (CurrentView == CurrentViewMode.Names)
            {
                LeftSideList_ItemsSource.ReplaceAll(NamesList);
            }

            if (CurrentView == CurrentViewMode.Imports)
            {
                LeftSideList_ItemsSource.ReplaceAll(Pcc.Imports);
            }

            if (CurrentView == CurrentViewMode.Exports)
            {
                LeftSideList_ItemsSource.ReplaceAll(Pcc.Exports);
            }

            if (CurrentView == CurrentViewMode.Tree)
            {
                if (AllTreeViewNodesX.Count > 0)
                {
                    foreach (TreeViewEntry tv in AllTreeViewNodesX[0].FlattenTree())
                    {
                        tv.RefreshDisplayName();
                    }
                }

                LeftSide_ListView.Visibility = Visibility.Collapsed;
                LeftSide_TreeView.Visibility = Visibility.Visible;
            }
            else
            {
                LeftSide_ListView.Visibility = Visibility.Visible;
                LeftSide_TreeView.Visibility = Visibility.Collapsed;
            }
        }

        public void InitStuff()
        {
            if (Pcc == null)
                return;

            MetadataTab_MetadataEditor.LoadPccData(Pcc);
            RefreshNames();
            if (CurrentView != CurrentViewMode.Tree)
            {
                RefreshView(); //Tree will initialize itself in thread
            }
        }

        private void TreeView_Click(object sender, RoutedEventArgs e)
        {
            SearchHintText = "Object name";
            GotoHintText = "UIndex";
            CurrentView = CurrentViewMode.Tree;
        }

        private void NamesView_Click(object sender, RoutedEventArgs e)
        {
            SearchHintText = "Name";
            GotoHintText = "Index";
            CurrentView = CurrentViewMode.Names;
        }

        private void ImportsView_Click(object sender, RoutedEventArgs e)
        {
            SearchHintText = "Object name";
            GotoHintText = "UIndex";
            CurrentView = CurrentViewMode.Imports;
        }

        private void ExportsView_Click(object sender, RoutedEventArgs e)
        {
            SearchHintText = "Object name";
            GotoHintText = "UIndex";
            CurrentView = CurrentViewMode.Exports;
        }

        /// <summary>
        /// Gets the selected entry uindex in the left side view.
        /// </summary>
        /// <param name="n">int that will be updated to point to the selected entry index. Will return 0 if nothing was selected (check the return value for false).</param>
        /// <returns>True if an item was selected, false if nothing was selected.</returns>
        public bool GetSelected(out int n)
        {
            n = 0;
            if (Pcc is null)
            {
                return false;
            }
            switch (CurrentView)
            {
                case CurrentViewMode.Tree when SelectedItem is TreeViewEntry selected:
                    n = selected.UIndex;
                    return true;
                case CurrentViewMode.Exports when LeftSide_ListView.SelectedItem != null:
                    n = LeftSide_ListView.SelectedIndex + 1; //to unreal indexing
                    return true;
                case CurrentViewMode.Imports when LeftSide_ListView.SelectedItem != null:
                    n = -LeftSide_ListView.SelectedIndex - 1;
                    return true;
                case CurrentViewMode.Names:
                default:
                    return false;
            }
        }

        private bool TryGetSelectedEntry(out IEntry entry)
        {
            if (GetSelected(out int uIndex) && Pcc.IsEntry(uIndex))
            {
                entry = Pcc.GetEntry(uIndex);
                return true;
            }

            entry = null;
            return false;
        }

        internal bool TryGetSelectedExport([NotNullWhen(true)] out ExportEntry? export)
        {
            if (GetSelected(out int uIndex) && Pcc.IsUExport(uIndex))
            {
                export = Pcc.GetUExport(uIndex);
                return true;
            }

            export = null;
            return false;
        }

        private bool TryGetSelectedImport([NotNullWhen(true)] out ImportEntry? import)
        {
            if (GetSelected(out int uIndex) && Pcc.IsImport(uIndex))
            {
                import = Pcc.GetImport(uIndex);
                return true;
            }

            import = null;
            return false;
        }

        private static string GetEntryPreviewText(IEntry entry)
        {
            return ConversationOwnerFriendlyNameResolver.GetEntryDisplayText(entry) ?? string.Empty;
        }

        public override void HandleUpdate(List<PackageUpdate> updates)
        {
            int selectedEditorTabIndex = EditorTabs?.SelectedIndex ?? -1;

            List<PackageChange> changes = updates.ConvertAll(x => x.Change);
            if (changes.Any(x => x.HasFlag(PackageChange.Name)))
            {
                foreach (ExportLoaderControl elc in ExportLoaders.Keys)
                {
                    elc.SignalNamelistAboutToUpdate();
                }

                RefreshNames(updates.Where(x => x.Change.HasFlag(PackageChange.Name)).ToList());
                foreach (ExportLoaderControl elc in ExportLoaders.Keys)
                {
                    elc.SignalNamelistChanged();
                }
            }

            if (updates.Any(x => x.Change is PackageChange.ExportRemove or PackageChange.ImportRemove))
            {
                InitializeTreeView();
                MetadataTab_MetadataEditor.RefreshAllEntriesList(Pcc);
                Preview();
                return;
            }

            bool hasImportChanges = changes.Any(x => x.HasFlag(PackageChange.Import));
            bool hasExportNonDataChanges =
                changes.Any(x => x != PackageChange.ExportData && x.HasFlag(PackageChange.Export));
            bool hasSelection = GetSelected(out int selectedEntryUIndex);

            List<PackageUpdate> addedChanges = [.. updates.Where(x => x.Change.HasFlag(PackageChange.EntryAdd)).OrderBy(x => x.Index)];
            HashSet<int> headerChanges = updates.Where(x => x.Change.HasFlag(PackageChange.EntryHeader)).Select(x => x.Index).ToHashSet();

            // Reduces tree enumeration
            List<TreeViewEntry> treeViewItems = AllTreeViewNodesX[0].FlattenTree();
            var uindexMap = new Dictionary<int, TreeViewEntry>();
            if (addedChanges.Count != 0 || headerChanges.Count != 0)
            {
                foreach (TreeViewEntry tv in treeViewItems)
                {
                    uindexMap[tv.UIndex] = tv;
                }
            }

            if (addedChanges.Count > 0)
            {
                MetadataTab_MetadataEditor.RefreshAllEntriesList(Pcc);

                // Track newly added FaceFXAnimSet exports in the name cache
                if (Pcc.Game is not MEGame.ME1)
                {
                    foreach (var change in addedChanges)
                    {
                        if (change.Index > 0 && change.Index <= Pcc.ExportCount)
                        {
                            var exp = Pcc.GetUExport(change.Index);
                            if (exp.ClassName == "FaceFXAnimSet")
                            {
                                _faceFXAnimSetNameCache[exp.UIndex] = exp.ObjectName.Name;
                            }
                        }
                    }
                }

                //Find nodes that haven't been generated and added yet

                List<IEntry> entriesToAdd = addedChanges.ConvertAll(change => Pcc.GetEntry(change.Index));

                //Generate new nodes
                var nodesToSortChildrenFor = new HashSet<TreeViewEntry>();
                //might have to loop a few times if it contains children before parents

                while (entriesToAdd.Count != 0)
                {
                    var orphans = new List<IEntry>();
                    foreach (IEntry entry in entriesToAdd)
                    {
                        if (uindexMap.TryGetValue(entry.idxLink, out TreeViewEntry parent))
                        {
                            var newEntry = new TreeViewEntry(entry) { Parent = parent };
                            parent.Sublinks.Add(newEntry);
                            treeViewItems.Add(newEntry); //used to find parents
                            nodesToSortChildrenFor.Add(parent);
                            uindexMap[entry.UIndex] = newEntry;
                        }
                        else
                        {
                            orphans.Add(entry);
                        }
                    }

                    if (orphans.Count == entriesToAdd.Count)
                    {
                        //actual orphans
                        Debug.WriteLine("Unable to attach new items to parents.");
                        break;
                    }

                    entriesToAdd = orphans;
                }

                SuppressSelectionEvent = true;
                nodesToSortChildrenFor.ToList().ForEach(x => x.SortChildren());
                SuppressSelectionEvent = false;

                if (CurrentView == CurrentViewMode.Imports)
                {
                    foreach (PackageUpdate update in addedChanges)
                    {
                        if (update.Index < 0)
                        {
                            LeftSideList_ItemsSource.Add(Pcc.GetEntry(update.Index));
                        }
                    }
                }

                if (CurrentView == CurrentViewMode.Exports)
                {
                    foreach (PackageUpdate update in addedChanges)
                    {
                        if (update.Index > 0)
                        {
                            LeftSideList_ItemsSource.Add(Pcc.GetEntry(update.Index));
                        }
                    }
                }
            }

            if (headerChanges.Count > 0)
            {
                // Update FaceFXAnimSet binary names when ObjectName changes via metadata editor
                if (Pcc.Game is not MEGame.ME1)
                {
                    foreach (int uIdx in headerChanges)
                    {
                        if (uIdx > 0 && uIdx <= Pcc.ExportCount)
                        {
                            var exp = Pcc.GetUExport(uIdx);
                            if (exp.ClassName == "FaceFXAnimSet" &&
                                _faceFXAnimSetNameCache.TryGetValue(uIdx, out string oldName) &&
                                oldName != exp.ObjectName.Name)
                            {
                                UpdateFaceFXAnimSetBinaryNames([exp], oldName, exp.ObjectName.Name);
                                _faceFXAnimSetNameCache[uIdx] = exp.ObjectName.Name;
                            }
                        }
                    }
                }

                //List<TreeViewEntry> tree = AllTreeViewNodesX[0].FlattenTree();
                var nodesNeedingResort = new List<TreeViewEntry>();
                List<TreeViewEntry> tviWithChangedHeaders = uindexMap.Values.Where(x => x.UIndex != 0 && headerChanges.Contains(x.Entry.UIndex)).ToList();
                foreach (TreeViewEntry tvi in tviWithChangedHeaders)
                {
                    if (tvi.Parent.UIndex != tvi.Entry.idxLink)
                    {
                        //Debug.WriteLine("Reorder req for " + tvi.UIndex);
                        if (!uindexMap.TryGetValue(tvi.Entry.idxLink, out var newParent))
                        {
                            Debugger.Break();
                        }
                        else
                        {
                            tvi.Parent.Sublinks.Remove(tvi);
                            tvi.Parent = newParent;
                            newParent.Sublinks.Add(tvi);
                            nodesNeedingResort.Add(newParent);
                        }
                    }
                }

                nodesNeedingResort = nodesNeedingResort.Distinct().ToList();
                SuppressSelectionEvent = true;
                nodesNeedingResort.ForEach(x => x.SortChildren());
                SuppressSelectionEvent = false;
            }

            if (CurrentView == CurrentViewMode.Imports && hasImportChanges ||
                CurrentView == CurrentViewMode.Exports && hasExportNonDataChanges ||
                CurrentView == CurrentViewMode.Tree && (hasImportChanges || hasExportNonDataChanges))
            {
                RefreshView();
                if (QueuedGotoNumber != 0 && GoToNumber(QueuedGotoNumber))
                {
                    QueuedGotoNumber = 0;
                }
                else if (hasSelection && this.IsForegroundWindow())
                {
                    GoToNumber(selectedEntryUIndex);
                }
            }

            if (CurrentView is CurrentViewMode.Exports or CurrentViewMode.Tree && hasSelection &&
                updates.Contains(new PackageUpdate(PackageChange.ExportData, selectedEntryUIndex)))
            {
                if (Pcc.GetEntry(selectedEntryUIndex) is not ExportEntry selectedExport
                    || !InterpreterTab_Interpreter.ConsumePendingPropertyWrite(selectedExport))
                {
                    Preview(true);
                }
            }

            if (selectedEditorTabIndex >= 0)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    if (selectedEditorTabIndex < EditorTabs.Items.Count
                        && EditorTabs.Items[selectedEditorTabIndex] is TabItem tab
                        && tab.IsEnabled
                        && tab.IsVisible)
                    {
                        EditorTabs.SelectedIndex = selectedEditorTabIndex;
                    }
                }));
            }
        }

        private void RefreshNames(List<PackageUpdate> updates = null)
        {
            if (updates == null)
            {
                //initial loading
                //we don't update the left side with this
                NamesList.ReplaceAll(Pcc.Names.Select((name, i) =>
                    new IndexedName(i, name))); //we replaceall so we don't add one by one and trigger tons of notifications
            }
            else
            {
                //only modify the list
                updates = [.. updates.OrderBy(x => x.Index)]; //ensure ascending order
                foreach (PackageUpdate update in updates)
                {
                    if (update.Index >= Pcc.NameCount)
                    {
                        continue;
                    }

                    if (update.Change == PackageChange.NameAdd) //names are 0 indexed
                    {
                        var nr = Pcc.Names[update.Index];
                        var indexedName = new IndexedName(update.Index, nr);
                        if (update.Index < NamesList.Count)
                        {
                            NamesList[update.Index] = indexedName;
                        }
                        else
                        {
                            NamesList.Add(indexedName);
                        }

                        while (NamesList.Count > Pcc.NameCount)
                        {
                            NamesList.RemoveAt(NamesList.Count - 1);
                        }

                        if (CurrentView == CurrentViewMode.Names)
                        {
                            LeftSideList_ItemsSource.ReplaceAll(NamesList);
                        }
                    }
                    else if (update.Change == PackageChange.NameEdit)
                    {
                        IndexedName indexed = new IndexedName(update.Index, Pcc.Names[update.Index]);
                        NamesList[update.Index] = indexed;
                        if (CurrentView == CurrentViewMode.Names)
                        {
                            LeftSideList_ItemsSource.ReplaceAll(NamesList);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Listbox selected item changed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LeftSide_SelectedItemChanged(object sender, SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (CurrentView == CurrentViewMode.Names)
            {
                return;
            }

            ApplySelectionPreview();
        }

        private void LeftSide_ListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            while (source is not ListBoxItem && source != null)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is not ListBoxItem item)
            {
                return;
            }

            if (!item.IsSelected)
            {
                LeftSide_ListView.SelectedItems.Clear();
                item.IsSelected = true;
            }

            item.Focus();
        }

        private void TreeEntryContainer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: TreeViewEntry clickedNode })
            {
                return;
            }

            var visibleNodes = GetVisibleTreeNodes();
            if (visibleNodes.Count == 0)
            {
                return;
            }

            bool ctrlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (shiftPressed && _treeSelectionAnchor is not null)
            {
                int anchorIndex = visibleNodes.IndexOf(_treeSelectionAnchor);
                int clickedIndex = visibleNodes.IndexOf(clickedNode);
                if (anchorIndex >= 0 && clickedIndex >= 0)
                {
                    int start = Math.Min(anchorIndex, clickedIndex);
                    int end = Math.Max(anchorIndex, clickedIndex);
                    SetTreeMultiSelection(visibleNodes.Skip(start).Take(end - start + 1), clickedNode, updatePrimarySelection: true);
                    e.Handled = true;
                    return;
                }
            }

            if (ctrlPressed)
            {
                var newSelection = _selectedTreeItems.ToList();
                bool removedClickedNode = newSelection.Remove(clickedNode);
                if (!removedClickedNode)
                {
                    newSelection.Add(clickedNode);
                }

                TreeViewEntry primaryNode = clickedNode;
                if (newSelection.Count == 0)
                {
                    newSelection.Add(clickedNode);
                }
                else if (removedClickedNode)
                {
                    primaryNode = newSelection[0];
                }

                SetTreeMultiSelection(newSelection, primaryNode, updatePrimarySelection: true);
                e.Handled = true;
                return;
            }

            SetTreeMultiSelection([clickedNode], clickedNode, updatePrimarySelection: true);
        }

        private void TreeEntryContainer_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: TreeViewEntry clickedNode })
            {
                return;
            }

            if (_selectedTreeItems.Contains(clickedNode))
            {
                clickedNode.IsProgramaticallySelecting = true;
                SelectedItem = clickedNode;
                return;
            }

            SetTreeMultiSelection([clickedNode], clickedNode, updatePrimarySelection: true);
        }

        private void ApplySelectionPreview()
        {
            if (_delaySelectionPreview)
            {
                RequestPreview();
                return;
            }

            CancelPendingPreview();
            Preview();
        }

        private void RequestPreview(bool isRefresh = false)
        {
            _pendingPreviewIsRefresh |= isRefresh;

            if (_pendingPreviewOperation?.Status == DispatcherOperationStatus.Pending)
            {
                _pendingPreviewOperation.Abort();
            }

            _pendingPreviewOperation = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _pendingPreviewOperation = null;
                bool refresh = _pendingPreviewIsRefresh;
                _pendingPreviewIsRefresh = false;
                Preview(refresh);
            }));
        }

        private void CancelPendingPreview()
        {
            _pendingPreviewIsRefresh = false;
            if (_pendingPreviewOperation?.Status == DispatcherOperationStatus.Pending)
            {
                _pendingPreviewOperation.Abort();
            }

            _pendingPreviewOperation = null;
        }

        private void RunWithDeferredPreview(Action action)
        {
            _delaySelectionPreview = true;
            try
            {
                action();
            }
            finally
            {
                _delaySelectionPreview = false;
            }
        }

        private void ClearPreviewPane()
        {
            CancelPendingPreview();
            foreach (ExportLoaderControl exportLoader in ExportLoaders.Keys)
            {
                exportLoader.UnloadExport();
            }

            EditorTabs.IsEnabled = false;
            Metadata_Tab.Visibility = Visibility.Collapsed;
            MetadataTab_MetadataEditor.ClearMetadataPane();
            Intro_Tab.Visibility = Visibility.Visible;
            Intro_Tab.IsSelected = true;
        }

        /// <summary>
        /// Prepares the right side of PackageEditorWPF for the current selected entry.
        /// This may take a moment if the data that is being loaded is large or complex.
        /// </summary>
        /// <param name="isRefresh">true if this is just a refresh of the currently-loaded export</param>
        private void Preview(bool isRefresh = false)
        {
            if (!TryGetSelectedEntry(out IEntry selectedEntry))
            {
                ClearPreviewPane();
                return;
            }

            EditorTabs.IsEnabled = true;
            Metadata_Tab.Visibility = Visibility.Visible;
            Intro_Tab.Visibility = Visibility.Collapsed;
            //Debug.WriteLine("New selection: " + n);
            if (CurrentView is CurrentViewMode.Imports or CurrentViewMode.Exports or CurrentViewMode.Tree)
            {
                Interpreter_Tab.IsEnabled = selectedEntry is ExportEntry;
                if (selectedEntry is ExportEntry exportEntry)
                {
                    foreach ((ExportLoaderControl exportLoader, TabItem tab) in ExportLoaders)
                    {
                        try
                        {
                            if (exportLoader.CanParse(exportEntry))
                            {
                                exportLoader.LoadExport(exportEntry);
                                tab.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                tab.Visibility = Visibility.Collapsed;
                                exportLoader.UnloadExport();
                            }
                        }
                        catch (Exception e)
                        {
                            new ExceptionHandlerDialog(e).ShowDialog();
                        }
                    }

                    if (Interpreter_Tab.IsSelected && exportEntry.ClassName == "Class")
                    {
                        //We are on interpreter tab, selecting class. Switch to binary interpreter as interpreter will never be useful
                        BinaryInterpreter_Tab.IsSelected = true;
                    }
                    if (Interpreter_Tab.IsSelected && Bytecode_Tab.IsVisible)
                    {
                        Bytecode_Tab.IsSelected = true;
                    }
                }
                else if (selectedEntry is ImportEntry importEntry)
                {
                    MetadataTab_MetadataEditor.LoadImport(importEntry);
                    foreach (KeyValuePair<ExportLoaderControl, TabItem> entry in ExportLoaders)
                    {
                        if (entry.Key != MetadataTab_MetadataEditor)
                        {
                            entry.Value.Visibility = Visibility.Collapsed;
                            entry.Key.UnloadExport();
                        }
                    }

                    Metadata_Tab.IsSelected = true;
                }

                //CHECK THE CURRENT TAB IS VISIBLE/ENABLED. IF NOT, CHOOSE FIRST TAB THAT IS 
                var currentTab = (TabItem)EditorTabs.Items[EditorTabs.SelectedIndex];
                if (!currentTab.IsEnabled || !currentTab.IsVisible)
                {
                    int index = 0;
                    while (index < EditorTabs.Items.Count)
                    {
                        TabItem ti = (TabItem)EditorTabs.Items[index];
                        if (ti.IsEnabled && ti.IsVisible)
                        {
                            EditorTabs.SelectedIndex = index;
                            break;
                        }

                        index++;
                    }
                }
            }
        }

        /// <summary>
        /// Handler for when the Goto button is clicked
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void GotoButton_Clicked(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(Goto_TextBox.Text, out int n))
            {
                GoToNumber(n);
            }
        }

        /// <summary>
        /// Selects the entry that corresponds to the given index
        /// </summary>
        /// <param name="entryIndex">Unreal-indexed entry number</param>
        public bool GoToNumber(int entryIndex)
        {
            if (entryIndex == 0)
            {
                return false; //PackageEditorWPF uses Unreal Indexing for entries
            }

            if (IsLoadingFile)
            {
                QueuedGotoNumber = entryIndex;
                return false;
            }

            switch (CurrentView)
            {
                case CurrentViewMode.Tree:
                    {
                        /*if (entryIndex >= -pcc.ImportCount && entryIndex < pcc.ExportCount)
                        {
                            //List<AdvancedTreeViewItem<TreeViewItem>> noNameNodes = AllTreeViewNodes.Where(s => s.Name.Length == 0).ToList();
                            var nodeName = entryIndex.ToString().Replace("-", "n");
                            List<AdvancedTreeViewItem<TreeViewItem>> nodes = AllTreeViewNodes.Where(s => s.Name.Length > 0 && s.Name.Substring(1) == nodeName).ToList();
                            if (nodes.Count > 0)
                            {
                                nodes[0].BringIntoView();
                                Dispatcher.BeginInvoke(DispatcherPriority.Background, (NoArgDelegate)delegate { nodes[0].ParentNodeValue.SelectItem(nodes[0]); });
                            }
                        }*/
                        //DispatcherHelper.EmptyQueue();
                        var list = AllTreeViewNodesX[0].FlattenTree();
                        List<TreeViewEntry> selectNode =
                            list.Where(s => s.Entry != null && s.UIndex == entryIndex).ToList();
                        if (Enumerable.Any(selectNode))
                        {
                            //selectNode[0].ExpandParents();
                            selectNode[0].IsProgramaticallySelecting = true;
                            SelectedItem = selectNode[0];
                            //FocusTreeViewNodeOld(selectNode[0]);

                            //selectNode[0].Focus(LeftSide_TreeView);
                            return true;
                        }

                        QueuedGotoNumber = entryIndex; //May be trying to select node that doesn't exist yet
                        break;
                    }
                case CurrentViewMode.Exports:
                case CurrentViewMode.Imports:
                    {
                        //Check bounds
                        var entry = Pcc.GetEntry(entryIndex);
                        if (entry != null)
                        {
                            //UI switch
                            if (CurrentView == CurrentViewMode.Exports && entry is ImportEntry)
                            {
                                CurrentView = CurrentViewMode.Imports;
                            }
                            else if (CurrentView == CurrentViewMode.Imports && entry is ExportEntry)
                            {
                                CurrentView = CurrentViewMode.Exports;
                            }

                            LeftSide_ListView.SelectedIndex = Math.Abs(entryIndex) - 1;
                            return true;
                        }

                        break;
                    }
                case CurrentViewMode.Names when entryIndex >= 0 && entryIndex < LeftSide_ListView.Items.Count:
                    //Names
                    LeftSide_ListView.SelectedIndex = entryIndex;
                    return true;
            }

            return false;
        }

        public bool GoToEntry(string instancedFullPath)
        {
            if (instancedFullPath == null) return false;
            if (Pcc.FindEntry(instancedFullPath) is IEntry entry)
            {
                CurrentView = CurrentViewMode.Tree;
                return GoToNumber(entry.UIndex);
            }
            return false;
        }

        /// <summary>
        /// Handler for the keyup event while the Goto Textbox is focused. It will issue the Goto button function when the enter key is pressed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Goto_TextBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && !e.IsRepeat)
            {
                GotoButton_Clicked(null, null);
            }
            else
            {
                if (Goto_TextBox.Text.Length == 0)
                {
                    Goto_Preview_TextBox.Text = "";
                    return;
                }

                if (int.TryParse(Goto_TextBox.Text, out int index))
                {
                    if (CurrentView == CurrentViewMode.Names)
                    {
                        if (index >= 0 && index < Pcc.NameCount)
                        {
                            Goto_Preview_TextBox.Text = Pcc.GetNameEntry(index);
                        }
                        else
                        {
                            Goto_Preview_TextBox.Text = "Invalid value";
                        }
                    }
                    else
                    {
                        if (index == 0)
                        {
                            Goto_Preview_TextBox.Text = "Invalid value";
                        }
                        else
                        {
                            var entry = Pcc.GetEntry(index);
                            if (entry != null)
                            {
                                Goto_Preview_TextBox.Text = GetEntryPreviewText(entry);
                            }
                            else
                            {
                                Goto_Preview_TextBox.Text = "Index out of bounds of entry list";
                            }
                        }
                    }
                }
                else
                {
                    Goto_Preview_TextBox.Text = "Invalid value";
                }
            }
        }

        private void SearchStringRefs()
        {
            if (Pcc == null)
            {
                return;
            }

            string searchTerm = StringRefSearchText?.Trim();
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return;
            }

            BusyText = $"Searching string refs for '{searchTerm}'...";
            IsBusy = true;
            Task.Run(() => FindStringRefUsages(searchTerm)).ContinueWithOnUIThread(prevTask =>
            {
                IsBusy = false;

                List<EntryStringPair> results = prevTask.Result;
                if (results.Count == 0)
                {
                    MessageBox.Show(this, $"No StringRef usages matching '{searchTerm}' were found.", "StringRef search");
                    return;
                }

                new ListDialog(
                    results,
                    $"{results.Count} StringRef match{(results.Count == 1 ? string.Empty : "es")}",
                    "Double-click a result to navigate to the owning export and property.",
                    this)
                {
                    DoubleClickEntryHandler = stringRefUsageDoubleClick
                }.Show();
            });
        }

        private List<EntryStringPair> FindStringRefUsages(string searchTerm)
        {
            var results = new List<EntryStringPair>();
            var resolvedTextCache = new Dictionary<int, string>();
            int? exactStringRef = int.TryParse(searchTerm, out int parsedStringRef) ? parsedStringRef : null;

            foreach (ExportEntry export in Pcc.Exports)
            {
                try
                {
                    CollectDerivedStringRefUsages(export, results, searchTerm, exactStringRef, resolvedTextCache);
                }
                catch
                {
                    // Ignore exports that fail to parse so search can continue through the package.
                }

                try
                {
                    CollectStringRefUsages(export.GetProperties(), export, results, searchTerm, exactStringRef, string.Empty, resolvedTextCache);
                }
                catch
                {
                    // Ignore exports that fail to parse so search can continue through the package.
                }
            }

            return results
                .OrderBy(result => result.Entry?.UIndex ?? int.MaxValue)
                .ThenBy(result => result.Message, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void CollectStringRefUsages(
            PropertyCollection props,
            ExportEntry export,
            List<EntryStringPair> results,
            string searchTerm,
            int? exactStringRef,
            string pathPrefix,
            Dictionary<int, string> resolvedTextCache)
        {
            foreach (Property prop in props)
            {
                switch (prop)
                {
                    case StructProperty structProperty:
                        CollectStringRefUsages(structProperty.Properties, export, results, searchTerm, exactStringRef, $"{pathPrefix}{structProperty.Name}.", resolvedTextCache);
                        break;
                    case ArrayProperty<StructProperty> structArray:
                        for (int i = 0; i < structArray.Count; i++)
                        {
                            CollectStringRefUsages(structArray[i].Properties, export, results, searchTerm, exactStringRef, $"{pathPrefix}{structArray.Name}[{i}].", resolvedTextCache);
                        }
                        break;
                    case ArrayProperty<StringRefProperty> stringRefArray:
                        for (int i = 0; i < stringRefArray.Count; i++)
                        {
                            AddStringRefUsage(results, export, $"{pathPrefix}{stringRefArray.Name}[{i}] value", stringRefArray[i].Value, searchTerm, exactStringRef, resolvedTextCache);
                        }
                        break;
                    case StringRefProperty stringRefProperty:
                        AddStringRefUsage(results, export, $"{pathPrefix}{stringRefProperty.Name} value", stringRefProperty.Value, searchTerm, exactStringRef, resolvedTextCache);
                        break;
                    case IntProperty intProperty when IsStringRefIntProperty(intProperty):
                        AddStringRefUsage(results, export, $"{pathPrefix}{intProperty.Name} value", intProperty.Value, searchTerm, exactStringRef, resolvedTextCache);
                        break;
                }
            }
        }

        private void CollectDerivedStringRefUsages(
            ExportEntry export,
            List<EntryStringPair> results,
            string searchTerm,
            int? exactStringRef,
            Dictionary<int, string> resolvedTextCache)
        {
            IEnumerable<int> stringRefs = export.ClassName switch
            {
                "WwiseEvent" => TryParseWwiseEventSubtitleStringRef(export.ObjectName.Name, out int stringRef) ? [stringRef] : [],
                "WwiseStream" => EnumerateEncodedSubtitleStringRefs(export.ObjectName.Name),
                "SoundNodeWave" or "SoundCue" => EnumerateEncodedSubtitleStringRefs(export.ObjectName.Instanced),
                _ => []
            };

            foreach (int stringRef in stringRefs.Distinct())
            {
                AddStringRefUsage(results, export, "Header: Object Name", stringRef, searchTerm, exactStringRef, resolvedTextCache);
            }
        }

        private static bool TryParseWwiseEventSubtitleStringRef(string objectName, out int stringRef)
        {
            stringRef = 0;
            if (string.IsNullOrWhiteSpace(objectName) || !objectName.StartsWith("VO_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string parsed = objectName[3..];
            int nextUnderscore = parsed.IndexOf('_');
            if (nextUnderscore > 0)
            {
                parsed = parsed[..nextUnderscore];
            }

            return int.TryParse(parsed, out stringRef) && stringRef > 0;
        }

        private static IEnumerable<int> EnumerateEncodedSubtitleStringRefs(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
            {
                yield break;
            }

            foreach (string segment in objectName.Split('_', ','))
            {
                if (int.TryParse(segment, out int stringRef) && stringRef > 0)
                {
                    yield return stringRef;
                }
            }
        }

        private void AddStringRefUsage(
            List<EntryStringPair> results,
            ExportEntry export,
            string propertyPath,
            int stringRef,
            string searchTerm,
            int? exactStringRef,
            Dictionary<int, string> resolvedTextCache)
        {
            if (stringRef <= 0)
            {
                return;
            }

            string resolvedText = ResolveStringRefSearchText(export, stringRef, resolvedTextCache);
            bool matches = exactStringRef.HasValue
                ? stringRef == exactStringRef.Value
                : !string.IsNullOrWhiteSpace(resolvedText)
                  && resolvedText.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

            if (!matches)
            {
                return;
            }

            string message = $"#{export.UIndex} {export.ObjectName.Instanced}: StringRef: {propertyPath} | {stringRef}";
            if (!string.IsNullOrWhiteSpace(resolvedText))
            {
                message += $" | {resolvedText.Replace("\r", string.Empty).Replace("\n", " ")}";
            }

            results.Add(new EntryStringPair(export, message));
        }

        private static bool IsStringRefIntProperty(IntProperty intProperty)
        {
            string propertyName = intProperty.Name.Name;
            return !string.IsNullOrWhiteSpace(propertyName)
                && (CommonStringRefPropertyNames.Contains(propertyName)
                    || propertyName.Contains("StrRef", StringComparison.OrdinalIgnoreCase)
                    || propertyName.Contains("StringRef", StringComparison.OrdinalIgnoreCase)
                    || propertyName.Contains("StringID", StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveStringRefSearchText(ExportEntry export, int stringRef, Dictionary<int, string> resolvedTextCache)
        {
            if (resolvedTextCache.TryGetValue(stringRef, out string resolvedText))
            {
                return resolvedText;
            }

            resolvedText = TLKManagerWPF.GlobalFindStrRefbyID(stringRef, export.FileRef);
            if (resolvedText == "No Data")
            {
                resolvedText = null;
            }

            resolvedTextCache[stringRef] = resolvedText;
            return resolvedText;
        }

        /// <summary>
        /// Drag/drop dragover handler for the entry list treeview
        /// </summary>
        /// <param name="dropInfo"></param>
        void IDropTarget.DragOver(IDropInfo dropInfo)
        {
            if (dropInfo.Data is TreeViewEntry { Parent: not null })
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                dropInfo.Effects = DragDropEffects.Copy;
            }
        }

        /// <summary>
        /// Drop handler for the entry list treeview
        /// </summary>
        /// <param name="dropInfo"></param>
        void IDropTarget.Drop(IDropInfo dropInfo)
        {
            if (dropInfo.TargetItem is TreeViewEntry targetItem && dropInfo.Data is TreeViewEntry sourceItem &&
                sourceItem.Parent != null)
            {
                var dragInfo = dropInfo.DragInfo;
                var sourceWindow = Window.GetWindow(dragInfo.VisualSource) as PackageEditorWindow;
                if (targetItem.Game.IsLEGame() != sourceItem.Game.IsLEGame() &&
                    !App.IsDebug &&
                    sourceItem.Entry.Game != MEGame.UDK) // allow UDK -> OT and LE)
                {
                    MessageBox.Show(
                        "Cannot port assets between Original Trilogy (OT) games and Legendary Edition (LE) games in release builds of Legendary Explorer.", "Cannot port asset", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 07/06/2022
                // Holding shift will allow to drag an export to another link in the same package
                // Check if the path of the target and the source is the same. If so, offer to merge instead
                var isShiftHeld = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                var isSamePackageDrop = (targetItem.Entry != null && sourceItem.Entry.FileRef == targetItem.Entry.FileRef) // entry to entry
                                        || (targetItem.PackageRef != null && sourceItem.Entry.FileRef == targetItem.PackageRef); // entry to root

                if (sourceItem == targetItem || (isSamePackageDrop && !isShiftHeld))
                {
                    return; // ignore
                }

                if (isSamePackageDrop && isShiftHeld)
                {
                    ChangeEntryLink(sourceItem.Entry, targetItem?.Entry?.UIndex ?? 0);
                    return;
                }

                var portingOption = TreeMergeDialog.GetMergeType(sourceWindow, this, sourceItem, targetItem, Pcc.Game);

                if (portingOption.PortingOptionChosen == EntryImporter.PortingOption.Cancel)
                {
                    return;
                }

                if (sourceItem.Entry.FileRef == null)
                {
                    return;
                }

                IEntry sourceEntry = sourceItem.Entry;
                IEntry targetLinkEntry = targetItem.Entry;

                int originalIndex = -1;
                bool hadChanges = false;
                bool hadHeaderChanges = false;
                if (portingOption.PortingOptionChosen != EntryImporter.PortingOption.ReplaceSingular
                    && portingOption.PortingOptionChosen != EntryImporter.PortingOption.ReplaceSingularWithRelink
                    && targetItem.Entry?.FileRef.FindEntry(sourceItem.Entry.InstancedFullPath) != null)
                {
                    // It's a duplicate. Offer to index it, as this will break the lookup if it's identical on inbound
                    // (it will just install into an existing entry)
                    originalIndex = sourceEntry.indexValue;
                    hadChanges = sourceEntry.EntryHasPendingChanges;
                    hadHeaderChanges = sourceEntry.HeaderChanged;
                    sourceEntry.indexValue = targetItem.Entry.FileRef.GetNextIndexedName(sourceEntry.ObjectName).Number;
                }

                // Load the object DB if games are different
                string objectDBPath = AppDirectories.GetObjectDatabasePath(targetItem.Game);
                bool shouldUseDonors = portingOption.PortUsingDonors && sourceEntry.Game != targetItem.Game && sourceEntry.Game != MEGame.UDK;
                ObjectInstanceDB objectDB = null;
                if (shouldUseDonors)
                {
                    if (File.Exists(objectDBPath))
                    {
                        using FileStream fs = File.OpenRead(objectDBPath);
                        objectDB = ObjectInstanceDB.Deserialize(targetItem.Game, fs);
                    }
                    else
                    {
                        var result = MessageBox.Show("Port With Donors checkbox was selected, but no object database was found! Continue operation without donors?",
                            "No object database", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                        if (result is not MessageBoxResult.Yes)
                        {
                            return;
                        }
                    }
                }

                // To profile this, run dotTrace and attach to the process, make sure to choose option to profile via API
                //MeasureProfiler.StartCollectingData(); // Start profiling
                //var sw = new Stopwatch();
                //sw.Start();

                int numExports = Pcc.ExportCount;
                //Import!
                var rop = new RelinkerOptionsPackage
                {
                    IsCrossGame = sourceEntry.Game != targetItem.Game && sourceEntry.Game != MEGame.UDK,
                    Cache = new PackageCache(),
                    TargetGameDonorDB = objectDB,
                    ImportExportDependencies = portingOption.PortingOptionChosen is EntryImporter.PortingOption.CloneAllDependencies
                        or EntryImporter.PortingOption.ReplaceSingularWithRelink,
                    GenerateImportsForGlobalFiles = portingOption.PortGlobalsAsImports,
                    PortImportsMemorySafe = portingOption.PortExportsMemorySafe,
                    PortExportsAsImportsWhenPossible = portingOption.PortExportsAsImportsWhenPossible,
                };

                if (portingOption.PortingOptionChosen is not EntryImporter.PortingOption.ReplaceSingular
                    and not EntryImporter.PortingOption.ReplaceSingularWithRelink)
                {
                    MapParentSequenceToDestination(sourceEntry, targetLinkEntry, rop);
                }

                var relinkResults = EntryImporter.ImportAndRelinkEntries(portingOption.PortingOptionChosen, sourceEntry, Pcc,
                    targetLinkEntry, true, rop, out IEntry newEntry);

                if (newEntry is ExportEntry importedExport
                    && portingOption.PortingOptionChosen is not EntryImporter.PortingOption.ReplaceSingular
                        and not EntryImporter.PortingOption.ReplaceSingularWithRelink)
                {
                    SynchronizeImportedSequenceObjects(sourceEntry, targetLinkEntry, importedExport, rop);
                }

                if (sourceEntry is ExportEntry { ClassName: "BioEvtSysTrackGesture" } sourceGestureTrack
                    && newEntry is ExportEntry destinationGestureTrack)
                {
                    relinkResults.AddRange(MatineeHelper.CloneGestureTrackAnimSets(sourceGestureTrack, destinationGestureTrack, rop));
                }

                if (originalIndex >= 0)
                {
                    //index was temporarily adjusted for porting. restore state
                    sourceEntry.indexValue = originalIndex;
                    sourceEntry.HeaderChanged = hadHeaderChanges;
                    sourceEntry.EntryHasPendingChanges = hadChanges;
                }

                var importedEntries = Pcc.Exports.Skip(numExports).Cast<IEntry>().ToList();
                TryAddToPersistentLevel(importedEntries);
                TryAddToStreamingLevelsList(importedEntries);

                if (portingOption.PortingOptionChosen is not EntryImporter.PortingOption.ReplaceSingular
                    and not EntryImporter.PortingOption.ReplaceSingularWithRelink
                    && newEntry != null
                    && ShouldAddToInterpList(newEntry))
                {
                    AddToInterpList(newEntry);
                }

                if (sourceEntry is ExportEntry sourceExport && sourceEntry.Parent is ExportEntry sourceLink && targetLinkEntry is ExportEntry targetLink && newEntry is ExportEntry newExp) {
                    if (sourceLink.ClassName == "StaticMeshCollectionActor" && targetLink.ClassName == "StaticMeshCollectionActor" && newExp.ClassName == "StaticMeshComponent")
                    {
                        var sourceCollectionBin = ObjectBinary.From<StaticMeshCollectionActor>(sourceLink);
                        var targetCollectionBin = ObjectBinary.From<StaticMeshCollectionActor>(targetLink);

                        // Must write before serializing out
                        targetCollectionBin.Components.Add(newEntry.UIndex);
                        targetLink.WriteProperty(new ArrayProperty<ObjectProperty>(targetCollectionBin.Components.Select(x=>new ObjectProperty(x)), "StaticMeshComponents"));
                        
                        var sourceIndex = sourceCollectionBin.Components.IndexOf(sourceEntry.UIndex);
                        targetCollectionBin.LocalToWorldTransforms.Add(sourceCollectionBin.LocalToWorldTransforms[sourceIndex]); 
                        targetLink.WriteBinary(targetCollectionBin);
                    }
                }

                //sw.Stop();
                //MessageBox.Show($"Took {sw.ElapsedMilliseconds}ms");
                //MeasureProfiler.SaveData(); // End profiling
                if ((relinkResults?.Count ?? 0) > 0)
                {
                    var ld = new ListDialog(relinkResults, "Relink report",
                        "The following items reported relinking issues.", this)
                    { DoubleClickEntryHandler = entryDoubleClick };
                    ld.Show();
                }
                else
                {
                    MessageBox.Show(
                        "Items have been ported and relinked with no reported issues.\nNote that this does not mean all binary properties were relinked, only supported ones were.");
                }

                RefreshView();
                GoToNumber(newEntry.UIndex);
            }
        }

        private async Task FindNextObjectByClassAsync(string searchClass, bool reverse)
        {
            if (Pcc == null || string.IsNullOrWhiteSpace(searchClass))
            {
                return;
            }

            IMEPackage package = Pcc;
            CurrentViewMode view = CurrentView;
            CancellationTokenSource searchCancellation = BeginEntrySearch();
            CancellationToken cancellationToken = searchCancellation.Token;

            void LoopFunc(ref int integer, int count)
            {
                if (reverse)
                {
                    integer--;
                }
                else
                {
                    integer++;
                }

                if (integer < 0)
                {
                    integer = count - 1;
                }
                else if (integer >= count)
                {
                    integer = 0;
                }
            }

            try
            {
                if (view == CurrentViewMode.Tree)
                {
                    TreeViewEntry selectedNode = (TreeViewEntry)LeftSide_TreeView.SelectedItem;
                    List<TreeViewEntry> items = AllTreeViewNodesX[0].FlattenTree();
                    int pos = selectedNode == null ? 0 : items.IndexOf(selectedNode);
                    LoopFunc(ref pos, items.Count);
                    for (int i = pos, numSearched = 0;
                        numSearched < items.Count;
                        LoopFunc(ref i, items.Count), numSearched++)
                    {
                        if (!await ContinueEntrySearchAsync(numSearched, package, view, cancellationToken))
                        {
                            return;
                        }

                        TreeViewEntry node = items[i];
                        if (node.Entry == null)
                        {
                            continue;
                        }

                        if (node.Entry.ClassName.Equals(searchClass))
                        {
                            node.IsProgramaticallySelecting = true;
                            RunWithDeferredPreview(() => SelectedItem = node);
                            return;
                        }
                    }
                }
                else
                {
                    int n = LeftSide_ListView.SelectedIndex;
                    int start = n == -1 ? 0 : n + 1;
                    if (view == CurrentViewMode.Exports)
                    {
                        int count = package.ExportCount;
                        for (int i = start; i < count; i++)
                        {
                            if (package.ExportCount != count
                                || !await ContinueEntrySearchAsync(i - start, package, view, cancellationToken))
                            {
                                return;
                            }

                            if (package.Exports[i].ClassName == searchClass)
                            {
                                RunWithDeferredPreview(() => LeftSide_ListView.SelectedIndex = i);
                                return;
                            }
                        }
                    }
                    else if (view == CurrentViewMode.Imports)
                    {
                        int count = package.ImportCount;
                        for (int i = start; i < count; i++)
                        {
                            if (package.ImportCount != count
                                || !await ContinueEntrySearchAsync(i - start, package, view, cancellationToken))
                            {
                                return;
                            }

                            if (package.Imports[i].ClassName == searchClass)
                            {
                                RunWithDeferredPreview(() => LeftSide_ListView.SelectedIndex = i);
                                return;
                            }
                        }
                    }
                }
            }
            finally
            {
                EndEntrySearch(searchCancellation);
            }
        }

        private async void FindObjectByClass_Click(object sender, RoutedEventArgs e)
        {
            if (Pcc == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedClassSearch))
            {
                await SelectClassSearchAsync(runSearchAfterSelection: true);
                return;
            }

            await FindNextObjectByClassAsync(SelectedClassSearch, Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
        }

        private async void SelectedClass_TextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await SelectClassSearchAsync(runSearchAfterSelection: false);
        }

        private async Task<bool> SelectClassSearchAsync(bool runSearchAfterSelection)
        {
            if (Pcc == null)
            {
                return false;
            }

            var classes = Pcc.Exports
                .Select(x => x.ClassName)
                .NonNull()
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            if (classes.Count == 0)
            {
                return false;
            }

            string chosenClass = StringSelectorDialog.GetValue(
                this,
                "Select a class to find.",
                "Class selector",
                classes,
                string.IsNullOrWhiteSpace(SelectedClassSearch) ? classes.FirstOrDefault() : SelectedClassSearch);
            if (string.IsNullOrWhiteSpace(chosenClass))
            {
                return false;
            }

            SelectedClassSearch = chosenClass;
            if (runSearchAfterSelection)
            {
                await FindNextObjectByClassAsync(chosenClass, Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
            }

            return true;
        }

        /// <summary>
        /// Click handler for the search button.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void SearchButton_Clicked(object sender, RoutedEventArgs e)
        {
            await SearchAsync(Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
        }

        /// <summary>
        /// Key handler for the search box. This listens for the enter key.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Searchbox_OnKeyUpHandler(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                await SearchAsync(Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
            }
        }

        /// <summary>
        /// Takes the contents of the search box and finds the next instance of it.
        /// </summary>
        private async Task SearchAsync(bool reverseSearch)
        {
            if (Pcc == null || string.IsNullOrWhiteSpace(Search_TextBox.Text))
            {
                return;
            }

            IMEPackage package = Pcc;
            CurrentViewMode view = CurrentView;
            int start = LeftSide_ListView.SelectedIndex;
            string searchTerm = Search_TextBox.Text.Trim();
            CancellationTokenSource searchCancellation = BeginEntrySearch();
            CancellationToken cancellationToken = searchCancellation.Token;

            void LoopFunc(ref int integer, int count)
            {
                if (reverseSearch)
                {
                    integer--;
                }
                else
                {
                    integer++;
                }

                if (integer < 0)
                {
                    integer = count - 1;
                }
                else if (integer >= count)
                {
                    integer = 0;
                }
            }

            try
            {
                if (view == CurrentViewMode.Names)
                {
                    int count = package.NameCount;
                    LoopFunc(ref start, count);
                    for (int i = start, numSearched = 0;
                         numSearched < count;
                         LoopFunc(ref i, count), numSearched++)
                    {
                        if (package.NameCount != count
                            || !await ContinueEntrySearchAsync(numSearched, package, view, cancellationToken))
                        {
                            return;
                        }

                        if (package.Names[i].Contains(searchTerm, StringComparison.InvariantCultureIgnoreCase))
                        {
                            LeftSide_ListView.SelectedIndex = i;
                            return;
                        }
                    }
                }

                if (view == CurrentViewMode.Imports)
                {
                    int count = package.ImportCount;
                    LoopFunc(ref start, count);
                    for (int i = start, numSearched = 0;
                         numSearched < count;
                         LoopFunc(ref i, count), numSearched++)
                    {
                        if (package.ImportCount != count
                            || !await ContinueEntrySearchAsync(numSearched, package, view, cancellationToken))
                        {
                            return;
                        }

                        if (package.Imports[i].ObjectName.Name.Contains(searchTerm, StringComparison.InvariantCultureIgnoreCase))
                        {
                            RunWithDeferredPreview(() => LeftSide_ListView.SelectedIndex = i);
                            return;
                        }
                    }
                }

                if (view == CurrentViewMode.Exports)
                {
                    int count = package.ExportCount;
                    LoopFunc(ref start, count);
                    for (int i = start, numSearched = 0;
                         numSearched < count;
                         LoopFunc(ref i, count), numSearched++)
                    {
                        if (package.ExportCount != count
                            || !await ContinueEntrySearchAsync(numSearched, package, view, cancellationToken))
                        {
                            return;
                        }

                        if (package.Exports[i].ObjectName.Name.Contains(searchTerm, StringComparison.InvariantCultureIgnoreCase))
                        {
                            RunWithDeferredPreview(() => LeftSide_ListView.SelectedIndex = i);
                            return;
                        }
                    }
                }

                if (view == CurrentViewMode.Tree && AllTreeViewNodesX.Count > 0)
                {
                    TreeViewEntry selectedNode = (TreeViewEntry)LeftSide_TreeView.SelectedItem;
                    List<TreeViewEntry> items = AllTreeViewNodesX[0].FlattenTree();
                    int pos = selectedNode == null ? 0 : items.IndexOf(selectedNode);
                    LoopFunc(ref pos, items.Count);

                    for (int numSearched = 0; numSearched < items.Count; LoopFunc(ref pos, items.Count), numSearched++)
                    {
                        if (!await ContinueEntrySearchAsync(numSearched, package, view, cancellationToken))
                        {
                            return;
                        }

                        TreeViewEntry node = items[pos];
                        if (node.Entry == null)
                        {
                            continue;
                        }

                        if (node.Entry.ObjectName.Instanced.Contains(searchTerm, StringComparison.InvariantCultureIgnoreCase))
                        {
                            node.IsProgramaticallySelecting = true;
                            RunWithDeferredPreview(() => SelectedItem = node);
                            return;
                        }
                    }
                }
            }
            finally
            {
                EndEntrySearch(searchCancellation);
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                // Assuming you have one file that you care about, pass it off to whatever
                // handling code you have defined.
                LoadFile(files[0]);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                string ext = Path.GetExtension(files[0]).ToLower();
                if (ext != ".u" && ext != ".upk" && ext != ".pcc" && ext != ".sfm" && ext != ".xxx" && ext != ".udk")
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                }
            }
        }

        private void TouchComfyMode_Clicked(object sender, RoutedEventArgs e)
        {
            Settings.PackageEditor_TouchComfyMode = !Settings.PackageEditor_TouchComfyMode;
            TouchComfySettings.ModeSwitched();
        }

        private void ShowImpExpPrefix_Clicked(object sender, RoutedEventArgs e)
        {
            Settings.PackageEditor_ShowImpExpPrefix =
                !Settings.PackageEditor_ShowImpExpPrefix;
            if (Enumerable.Any(AllTreeViewNodesX))
            {
                AllTreeViewNodesX[0].FlattenTree().ForEach(x => x.RefreshDisplayName());
            }
        }

        private void PackageEditorWPF_Closing(object sender, CancelEventArgs e)
        {
            if (!e.Cancel)
            {
                SoundTab_Soundpanel.FreeAudioResources();
                foreach (ExportLoaderControl el in ExportLoaders.Keys)
                {
                    el.Dispose(); //Remove hosted winforms references
                }

                LeftSideList_ItemsSource.ClearEx();
                ResetTreeView();
                RecentsController?.Dispose();
            }
        }

        private void ResetTreeView()
        {
            ClearTreeMultiSelection();
            _treeSelectionAnchor = null;
            if (AllTreeViewNodesX.Count > 0)
            {
                foreach (TreeViewEntry tv in AllTreeViewNodesX[0].FlattenTree())
                {
                    tv.Dispose();
                }
            }
            AllTreeViewNodesX.ClearEx();
        }

        private TreeViewScrollState? CaptureTreeViewScrollState()
        {
            if (CurrentView != CurrentViewMode.Tree)
            {
                return null;
            }

            var scrollViewer = FindVisualChild<ScrollViewer>(LeftSide_TreeView);
            return scrollViewer is null
                ? null
                : new TreeViewScrollState(scrollViewer.HorizontalOffset, scrollViewer.VerticalOffset);
        }

        private void RestoreTreeViewViewport(TreeViewScrollState? scrollState)
        {
            if (scrollState is not { } state || CurrentView != CurrentViewMode.Tree)
            {
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (FindVisualChild<ScrollViewer>(LeftSide_TreeView) is { } scrollViewer)
                {
                    scrollViewer.ScrollToHorizontalOffset(state.HorizontalOffset);
                    scrollViewer.ScrollToVerticalOffset(state.VerticalOffset);
                }

                LeftSide_TreeView.Focus();
                Keyboard.Focus(LeftSide_TreeView);
            }));
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is null)
            {
                return null;
            }

            for (int i = 0, childCount = VisualTreeHelper.GetChildrenCount(parent); i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                if (FindVisualChild<T>(child) is { } descendant)
                {
                    return descendant;
                }
            }

            return null;
        }

        private void OpenIn_Clicked(object sender, RoutedEventArgs e)
        {
            var myValue = (string)((MenuItem)sender).Tag;
            switch (myValue)
            {
                case "SequenceEditor":
                    var seqEditor = new Sequence_Editor.SequenceEditorWPF(Pcc);
                    seqEditor.Show();
                    break;
                case "FaceFXEditor":
                    var facefxEditor = new FaceFXEditor.FaceFXEditorWindow();
                    facefxEditor.LoadFile(Pcc.FilePath);
                    facefxEditor.Show();
                    break;
                case "SoundplorerWPF":
                    var soundplorerWPF = new Soundplorer.SoundplorerWPF();
                    soundplorerWPF.LoadFile(Pcc.FilePath);
                    soundplorerWPF.Show();
                    break;
                case "DialogueEditor":
                    var dialogueEditorWPF = new DialogueEditorWindow();
                    dialogueEditorWPF.LoadFile(Pcc.FilePath);
                    dialogueEditorWPF.Show();
                    break;
                case "PathfindingEditor":
                    var pathEditor = new PathfindingEditor.PathfindingEditorWindow(Pcc);
                    pathEditor.Show();
                    break;
                case "LevelEditor":
                    var levelEditor = new LevelEditor.LevelEditor();
                    levelEditor.Show();
                    _ = levelEditor.LoadFileAsync(Pcc.FilePath);
                    break;
                case "Meshplorer":
                    var meshplorer = new MeshplorerWindow();
                    meshplorer.LoadFile(Pcc.FilePath);
                    meshplorer.Show();
                    break;

            }
        }

        private void MatchMaterialsToSkeletalMesh_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: var dataContext } } })
            {
                MatchMaterialsToSkeletalMesh();
                return;
            }

            ExportEntry export = dataContext switch
            {
                ExportEntry exportEntry => exportEntry,
                TreeViewEntry { Entry: ExportEntry exportEntry } => exportEntry,
                _ => null
            };

            if (export is null)
            {
                MatchMaterialsToSkeletalMesh();
                return;
            }

            if (!InterpreterExportLoader.CanMatchMaterialsToSkeletalMesh(export))
            {
                MessageBox.Show(this,
                    "This action only works on SkeletalMeshComponent exports.",
                    "Match MaterialInstanceConstants to SkeletalMesh",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            InterpreterExportLoader.MatchMaterialsToSkeletalMesh(this, export);
            if (ReferenceEquals(export, SelectedItem?.Entry) || GetSelected(out int selectedIndex) && selectedIndex == export.UIndex)
            {
                Preview(true);
            }
        }

        private void RestoreMaterialFromAssetDatabase_Click(object sender, RoutedEventArgs e)
        {
            ExportEntry export = null;
            if (sender is MenuItem { Parent: ContextMenu contextMenu } && TryGetContextMenuExport(contextMenu, out var contextExport))
            {
                export = contextExport;
            }
            else
            {
                TryGetSelectedExport(out export);
            }

            if (!CanRestoreMaterialFromAssetDatabase(export))
            {
                MessageBox.Show(this,
                    "This action only works on Material and MaterialInstanceConstant exports.",
                    "Restore from Asset Database material",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PackageEditorExperimentsScottina.RestoreMaterialFromChosenAssetDatabase(this, export, () =>
            {
                if (ReferenceEquals(export, SelectedItem?.Entry) || GetSelected(out int selectedIndex) && selectedIndex == export.UIndex)
                {
                    Preview(true);
                }
            });
        }

        private void AddMissingTexturesToInstancesMap_Click(object sender, RoutedEventArgs e)
        {
            ExportEntry export = null;
            if (sender is MenuItem { Parent: ContextMenu contextMenu } && TryGetContextMenuExport(contextMenu, out var contextExport))
            {
                export = contextExport;
            }
            else
            {
                TryGetSelectedExport(out export);
            }

            if (!CanAddMissingTexturesToInstancesMap(export))
            {
                MessageBox.Show(this,
                    "This action only works on TheWorld.PersistentLevel.",
                    "Add missing textures to TextureToInstancesMap",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            IsBusy = true;
            BusyText = "Adding missing textures to TextureToInstancesMap...";
            Task.Run(() => LevelTools.AddMissingTexturesToInstancesMap(Pcc, TieredPackageCache.GetGlobalPackageCache(Pcc.Game))).ContinueWithOnUIThread(task =>
            {
                IsBusy = false;

                if (task.Exception is not null)
                {
                    MessageBox.Show(this,
                        "Error adding missing textures to TextureToInstancesMap:\n" + task.Exception.FlattenException(),
                        "Add missing textures to TextureToInstancesMap",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                if (ReferenceEquals(export, SelectedItem?.Entry) || GetSelected(out int selectedIndex) && selectedIndex == export.UIndex)
                {
                    Preview(true);
                }

                int addedCount = task.Result;
                MessageBox.Show(this,
                    addedCount > 0
                        ? $"Added {addedCount} missing texture entr{(addedCount == 1 ? "y" : "ies")} to PersistentLevel.TextureToInstancesMap."
                        : "No missing streamed textures were found to add to PersistentLevel.TextureToInstancesMap.",
                    "Add missing textures to TextureToInstancesMap",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
        }

        private void StripLightmap_Click(object sender, RoutedEventArgs e)
        {
            ExportEntry export = null;
            if (sender is MenuItem { Parent: ContextMenu contextMenu } && TryGetContextMenuExport(contextMenu, out var contextExport))
            {
                export = contextExport;
            }
            else
            {
                TryGetSelectedExport(out export);
            }

            if (!CanStripLightmap(export))
            {
                MessageBox.Show(this,
                    "This action only works on StaticMeshComponent exports.",
                    "Strip LightMap",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PackageEditorExperimentsM.StripLightmap(export);
            if (ReferenceEquals(export, SelectedItem?.Entry) || GetSelected(out int selectedIndex) && selectedIndex == export.UIndex)
            {
                Preview(true);
            }
        }

        private void CopyFullPath_Click(object sender, RoutedEventArgs e)
        {
            IEntry entry = null;
            if (sender is MenuItem { Parent: ContextMenu contextMenu } && TryGetContextMenuEntry(contextMenu, out var contextEntry))
            {
                entry = contextEntry;
            }
            else
            {
                TryGetSelectedEntry(out entry);
            }

            if (entry is not null)
            {
                Clipboard.SetText(entry.InstancedFullPath);
            }
        }

        private void EntryContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu contextMenu)
            {
                return;
            }

            var changeLinksMenuItem = contextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Tag, "ChangeLinksForSelectedEntries"));

            var matchMicMenuItem = contextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Tag, "MatchMaterialsToSkeletalMesh"));
            var restoreMaterialMenuItem = contextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Tag, "RestoreMaterialFromAssetDatabase"));
            var addMissingTexturesMenuItem = contextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Tag, "AddMissingTexturesToInstancesMap"));
            var stripLightmapMenuItem = contextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Tag, "StripLightmap"));
            var stripShadowmapMenuItem = contextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Tag, "StripShadowmap"));
            var copyFullPathMenuItem = contextMenu.Items
                .OfType<MenuItem>()
                .FirstOrDefault(item => Equals(item.Tag, "CopyFullPath"));
            if (changeLinksMenuItem is null && matchMicMenuItem is null && restoreMaterialMenuItem is null && addMissingTexturesMenuItem is null && stripLightmapMenuItem is null && stripShadowmapMenuItem is null && copyFullPathMenuItem is null)
            {
                return;
            }

            bool hasEntry = TryGetContextMenuEntry(contextMenu, out var entry);
            ExportEntry export = entry as ExportEntry;
            bool hasExport = export is not null;

            if (changeLinksMenuItem is not null)
            {
                changeLinksMenuItem.Visibility = GetSelectedLinkableEntries().Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (copyFullPathMenuItem is not null)
            {
                copyFullPathMenuItem.Visibility = hasEntry
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (matchMicMenuItem is not null)
            {
                matchMicMenuItem.Visibility = hasExport && InterpreterExportLoader.CanMatchMaterialsToSkeletalMesh(export)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (restoreMaterialMenuItem is not null)
            {
                restoreMaterialMenuItem.Visibility = hasExport && CanRestoreMaterialFromAssetDatabase(export)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (addMissingTexturesMenuItem is not null)
            {
                addMissingTexturesMenuItem.Visibility = hasExport && CanAddMissingTexturesToInstancesMap(export)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (stripLightmapMenuItem is not null)
            {
                stripLightmapMenuItem.Visibility = hasExport && CanStripLightmap(export)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (stripShadowmapMenuItem is not null)
            {
                stripShadowmapMenuItem.Visibility = hasExport && CanStripShadowmap(export)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private static bool TryGetContextMenuEntry(ContextMenu contextMenu, out IEntry entry)
        {
            object dataContext = (contextMenu.PlacementTarget as FrameworkElement)?.DataContext;
            entry = dataContext switch
            {
                IEntry packageEntry => packageEntry,
                TreeViewEntry { Entry: IEntry packageEntry } => packageEntry,
                _ => null
            };

            return entry is not null;
        }

        private static bool TryGetContextMenuExport(ContextMenu contextMenu, out ExportEntry export)
        {
            export = TryGetContextMenuEntry(contextMenu, out var entry)
                ? entry as ExportEntry
                : null;

            return export is not null;
        }

        private void HexConverterMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(AppDirectories.HexConverterPath))
            {
                Process.Start(AppDirectories.HexConverterPath);
            }
            else
            {
                new HexConverter.MainWindow().Show();
            }
        }

        private void BinaryInterpreterWPF_AlwaysAutoParse_Click(object sender, RoutedEventArgs e)
        {
            //BinaryInterpreterWPF_AlwaysAutoParse_MenuItem.IsChecked = !BinaryInterpreterWPF_AlwaysAutoParse_MenuItem.IsChecked;
            Settings.BinaryInterpreter_SkipAutoParseSizeCheck = !Settings.BinaryInterpreter_SkipAutoParseSizeCheck;
        }

        private void AssociatePCCSFM_Clicked(object sender, RoutedEventArgs e)
        {
            FileAssociations.AssociatePCCSFM();
        }

        private void AssociateUPKUDK_Clicked(object sender, RoutedEventArgs e)
        {
            FileAssociations.AssociateUPKUDK();
        }

        private void AssociateOtherFiles_Clicked(object sender, RoutedEventArgs e)
        {
            FileAssociations.AssociateOthers();
        }

        private void TLKManagerWPF_MenuItem_Click(object sender, RoutedEventArgs e)
        {
            new TlkManagerNS.TLKManagerWPF().Show();
        }

        private void PropertyParsing_UnknownArrayAsObj_Click(object sender, RoutedEventArgs e)
        {
            Settings.Global_PropertyParsing_ParseUnknownArrayTypeAsObject =
                !Settings.Global_PropertyParsing_ParseUnknownArrayTypeAsObject;
        }

        private void MountEditor_Click(object sender, RoutedEventArgs e)
        {
            new MountEditor.MountEditorWindow().Show();
        }

        private void EmbeddedTextureViewer_AutoLoad_Click(object sender, RoutedEventArgs e)
        {
            Settings.TextureViewer_AutoLoadMip =
                !Settings.TextureViewer_AutoLoadMip;
        }

        private void InterpreterWPF_AdvancedMode_Click(object sender, RoutedEventArgs e)
        {
            Settings.Interpreter_AdvancedDisplay =
                !Settings.Interpreter_AdvancedDisplay;
        }

        private void InterpreterWPF_Colorize_MenuItem_Click(object sender, RoutedEventArgs e)
        {
            Settings.Interpreter_Colorize = !Settings.Interpreter_Colorize;
        }

        private void InterpreterWPF_ArrayPropertySizeLimit_MenuItem_Click(object sender, RoutedEventArgs e)
        {
            Settings.Interpreter_LimitArrayPropertySize =
                !Settings.Interpreter_LimitArrayPropertySize;
        }

        private void ShowExportIcons_Click(object sender, RoutedEventArgs e)
        {
            Settings.PackageEditor_ShowExportTypeIcons =
                !Settings.PackageEditor_ShowExportTypeIcons;

            // this triggers binding updates
            LeftSide_TreeView.DataContext = null;
            LeftSide_TreeView.DataContext = this;
        }

        private async void ShowOnlyEditedTreeViewItems_Clicked(object sender, RoutedEventArgs e)
        {
            if (ShowOnlyEditedTreeViewItems)
            {
                ShowOnlyEditedTreeViewItems = false;
                return;
            }

            if (!SharedPackageTools.CanCompareToUnmodded(this))
            {
                MessageBox.Show(this, "Can only compare packages from the Original Trilogy or Legendary Edition.",
                    "Can't compare", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsBusy = true;
            BusyText = "Finding unmodded candidates...";
            SharedPackageTools.UnmoddedCandidatesLookup foundCandidates;
            try
            {
                foundCandidates = await Task.Run(() => SharedPackageTools.GetUnmoddedCandidatesForPackage(this));
            }
            finally
            {
                IsBusy = false;
            }

            if (!foundCandidates.Any())
            {
                MessageBox.Show(this, "Cannot find any candidates for this file!");
                return;
            }

            var choices = foundCandidates.DiskFiles.ToList();
            choices.AddRange(foundCandidates.SFARPackageStreams.Select(x => x.Key));

            var choice = SharedPackageTools.SelectUnmodifiedComparisonCandidate(this, choices);
            if (string.IsNullOrEmpty(choice))
            {
                return;
            }

            using var comparePackage = OpenRestoreCandidatePackage(foundCandidates, choice);
            if (comparePackage == null)
            {
                MessageBox.Show(this, "Could not open the selected unmodded package.");
                return;
            }

            IsBusy = true;
            BusyText = "Comparing packages...";
            List<EntryStringPair> results;
            try
            {
                results = await Task.Run(() => Pcc.CompareToPackage(comparePackage));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error comparing packages");
                return;
            }
            finally
            {
                IsBusy = false;
            }

            SetComparedChangedEntries(results);

            if (_comparedChangedEntryIndices.Count == 0)
            {
                MessageBox.Show(this, "No changes between names/imports/exports were found between the files.", "Packages seem identical");
                return;
            }

            ShowOnlyEditedTreeViewItems = true;
            CurrentView = CurrentViewMode.Tree;
        }

        private bool HasShaderCache() => PackageIsLoaded() && Pcc.Exports.Any(exp => exp.ClassName == "ShaderCache");

        private void CompactShaderCache()
        {
            IsBusy = true;
            BusyText = "Compacting local ShaderCaches";
            Task.Run(() => ShaderCacheManipulator.CompactSeekFreeShaderCaches(Pcc)).ContinueWithOnUIThread(_ => IsBusy = false);
        }

        private void InterpreterWPF_LinearColorWheel_MenuItem_Click(object sender, RoutedEventArgs e)
        {
            Settings.Interpreter_ShowLinearColorWheel =
                !Settings.Interpreter_ShowLinearColorWheel;
        }

        private void ShowExportMetadataInTree_Clicked(object sender, RoutedEventArgs e)
        {
            Settings.PackageEditor_ShowTreeEntrySubText =
                !Settings.PackageEditor_ShowTreeEntrySubText;
            if (AllTreeViewNodesX.Any)
            {
                foreach (TreeViewEntry tv in AllTreeViewNodesX[0].FlattenTree())
                {
                    tv.RefreshSubText();
                }
            }
        }

        private void ApplyTreeViewEditedFilter()
        {
            if (!AllTreeViewNodesX.Any)
            {
                return;
            }

            UpdateTreeViewEditedVisibility(AllTreeViewNodesX[0], isRoot: true);
        }

        private bool UpdateTreeViewEditedVisibility(TreeViewEntry node, bool isRoot = false)
        {
            bool hasVisibleEditedDescendant = false;
            foreach (TreeViewEntry child in node.Sublinks)
            {
                hasVisibleEditedDescendant |= UpdateTreeViewEditedVisibility(child);
            }

            bool isEditedEntry = IsEditedTreeEntry(node.Entry);

            bool isVisible = !ShowOnlyEditedTreeViewItems
                             || isRoot
                             || isEditedEntry
                             || hasVisibleEditedDescendant;
            node.IsVisibleInTree = isVisible;

            if (ShowOnlyEditedTreeViewItems)
            {
                node.IsExpanded = isRoot || hasVisibleEditedDescendant;
            }

            return isVisible;
        }

        internal void SetComparedChangedEntries(IEnumerable<EntryStringPair> results)
        {
            _comparedChangedEntryIndices.Clear();

            if (results != null)
            {
                foreach (int uIndex in results
                             .Select(result => result.Entry)
                             .Where(entry => entry?.FileRef == Pcc && entry.UIndex != 0)
                             .Select(entry => entry.UIndex)
                             .Distinct())
                {
                    _comparedChangedEntryIndices.Add(uIndex);
                }
            }

            ApplyTreeViewEditedFilter();
        }

        private bool IsEditedTreeEntry(IEntry entry)
        {
            return entry is not null
                && (entry.EntryHasPendingChanges || _comparedChangedEntryIndices.Contains(entry.UIndex));
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton.Equals(MouseButton.XButton1))
                NavigateToPreviousEntry();
            if (e.ChangedButton.Equals(MouseButton.XButton2))
                NavigateToNextEntry();
        }

        private void PackageEditorWPF_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            OnPropertyChanged(nameof(StringRefSearchBoxWidth));
        }

        private void NavigateToNextEntry()
        {
            if (ForwardsEntries.Any())
            {
                if (SelectedItem != null && SelectedItem.UIndex != 0 && ForwardsEntries[0].UIndex != SelectedItem.UIndex)
                {
                    //Debug.WriteLine("Push onto backwards: " + SelectedItem.UIndex);
                    BackwardsEntries.Insert(0, Pcc.GetEntry(SelectedItem.UIndex));
                }

                var entry = ForwardsEntries[0];
                ForwardsEntries.RemoveAt(0);
                IsBackForwardsNavigationEvent = true;
                GoToNumber(entry.UIndex);
                IsBackForwardsNavigationEvent = true;
            }
        }

        public bool IsBackForwardsNavigationEvent = false;

        private void NavigateToPreviousEntry()
        {
            if (BackwardsEntries.Any())
            {
                if (SelectedItem != null && SelectedItem.UIndex != 0 && BackwardsEntries[0].UIndex != SelectedItem.UIndex)
                {
                    //Debug.WriteLine("Push onto forwards: " + SelectedItem.UIndex);
                    ForwardsEntries.Insert(0, Pcc.GetEntry(SelectedItem.UIndex));
                }

                var entry = BackwardsEntries[0];
                BackwardsEntries.RemoveAt(0); // Might want to make this an extension method. M3 uses 'PullFromFront()'
                IsBackForwardsNavigationEvent = true;
                GoToNumber(entry.UIndex);
                IsBackForwardsNavigationEvent = false;
            }
        }

        private void ReplaceReferenceLinks()
        {
            if (TryGetSelectedEntry(out IEntry selectedEntry))
            {
                var replacement = EntrySelector.GetEntry<IEntry>(this, Pcc, "Select replacement reference (search by UIndex, name, class, or full path)");
                if (replacement == null || replacement.UIndex == 0)
                    return;

                BusyText = "Replacing references...";
                IsBusy = true;

                Task.Run(() => selectedEntry.ReplaceAllReferencesToThisOne(replacement)).ContinueWithOnUIThread(
                    prevTask =>
                    {
                        IsBusy = false;
                        MessageBox.Show($"Replaced {prevTask.Result} reference links.");
                    });
            }
        }

        public void LoadFileFromStream(Stream packageStream, string associatedFilePath, int goToIndex = 0, string goToEntry = null)
        {
            // Todo: Maybe prompt if there are pending changes to the current package?
            try
            {
                preloadPackage(Path.GetFileName(associatedFilePath), packageStream.Length);
                LoadMEPackage(packageStream, associatedFilePath);
                _selectedItem = null; // We change the backing data so we don't fire off a tree event since it checks if Pcc is null.
                if (goToIndex == 0 && !string.IsNullOrWhiteSpace(goToEntry))
                {
                    goToIndex = Pcc.FindEntry(goToEntry)?.UIndex ?? 0;
                }
                postloadPackage(associatedFilePath, goToIndex);

                // Loading from stream is not supported for saving or direct loading.
                // RecentsController.AddRecent(s, false, Pcc?.Game);
                // RecentsController.SaveRecentList(true);
            }
            catch (Exception e) when (!App.IsDebug)
            {
                StatusBar_LeftMostText.Text = "Failed to load " + associatedFilePath;
                MessageBox.Show($"Error loading {associatedFilePath}:\n{e.Message}");
                IsBusy = false;
                IsBusyTaskbar = false;
                //throw e;
            }
        }

        public void PropogateRecentsChange(string propogationSource, IEnumerable<RecentsControl.RecentItem> newRecents)
        {
            RecentsController.PropogateRecentsChange(false, newRecents);
        }

        public string Toolname => "PackageEditor";
    }
}