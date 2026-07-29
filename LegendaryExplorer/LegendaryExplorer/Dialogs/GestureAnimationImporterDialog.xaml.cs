using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Matinee;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Collections;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs
{
    /// <summary>
    /// Gesture entry view model for editing existing gestures
    /// </summary>
    public class GestureEntryViewModel : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public string DisplayName => $"Gesture {Index}";
        public StructProperty GestureStruct { get; set; }
        public StructProperty TrackKeyStruct { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class AnimationSourceOption
    {
        public string DisplayName { get; init; }
        public string FilePath { get; init; }
        public int UIndex { get; init; }
    }

    public sealed class GestureImportTargetOption
    {
        public int? GestureIndex { get; init; }
        public string DisplayName { get; init; }
    }

    public sealed class GestureTrackSourceOption
    {
        public string DisplayName { get; init; }
        public string FilePath { get; init; }
        public int UIndex { get; init; }
    }

    public partial class GestureAnimationImporterDialog : Window, INotifyPropertyChanged
    {
        private readonly ExportEntry _gestureTrackExport;
        private readonly IMEPackage _pcc;
        private AssetDB _db;
        private List<AnimationRecord> _allAnimations = new();

        /// <summary>
        /// The game whose asset database is currently loaded for browsing animations.
        /// May differ from _pcc.Game when browsing cross-game animations.
        /// </summary>
        private MEGame _selectedAnimSourceGame;

        /// <summary>
        /// Games available for browsing in the source game selector.
        /// </summary>
        public static MEGame[] AvailableSourceGames { get; } = [MEGame.LE3, MEGame.LE2, MEGame.LE1];

        /// <summary>
        /// True if the target export is an SFXModule_Gestures (SFXStuntActor).
        /// </summary>
        private bool IsSFXModuleGestures => _gestureTrackExport.ClassName == "SFXModule_Gestures";

        /// <summary>
        /// True if the target export is an SFXSkeletalMeshActor.
        /// </summary>
        private bool IsSFXSkeletalMeshActor => _gestureTrackExport.ClassName == "SFXSkeletalMeshActor";

        /// <summary>
        /// True if the target export is an SFXSeqAct_SetAmbientPerformance.
        /// </summary>
        private bool IsSFXSeqActSetAmbientPerformance => _gestureTrackExport.ClassName == "SFXSeqAct_SetAmbientPerformance";

        /// <summary>
        /// True if the target stores its default pose set directly on itself via m_pDefaultPoseSet.
        /// </summary>
        private bool UsesDefaultPoseSetTarget => IsSFXModuleGestures || IsSFXSeqActSetAmbientPerformance;

        /// <summary>
        /// Finds the main SkeletalMeshComponent of an SFXSkeletalMeshActor — the one with
        /// AnimNodeSequence or BioDynamicAnimSet children (typically named SkeletalMeshComponent0),
        /// not HeadMesh0/HairMesh0/GearMesh0.
        /// </summary>
        private ExportEntry FindMainSkeletalMeshComponent(ExportEntry skeletalMeshActor)
        {
            var skelMeshComponents = _pcc.Exports
                .Where(exp => exp.idxLink == skeletalMeshActor.UIndex && exp.ClassName == "SkeletalMeshComponent")
                .ToList();

            // Prefer one that already has an AnimNodeSequence or BioDynamicAnimSet child
            foreach (var comp in skelMeshComponents)
            {
                bool hasRelevantChild = _pcc.Exports.Any(exp =>
                    exp.idxLink == comp.UIndex &&
                    exp.ClassName is "AnimNodeSequence" or "BioDynamicAnimSet");
                if (hasRelevantChild)
                    return comp;
            }

            // Fall back to the one whose name starts with "SkeletalMeshComponent"
            return skelMeshComponents.FirstOrDefault(c => c.ObjectName.Name.StartsWith("SkeletalMeshComponent"))
                   ?? skelMeshComponents.FirstOrDefault();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region Bindable Properties

        private string _targetExportInfo;
        public string TargetExportInfo
        {
            get => _targetExportInfo;
            set { _targetExportInfo = value; OnPropertyChanged(); }
        }

        private string _animationStatusText = "Loading database...";
        public string AnimationStatusText
        {
            get => _animationStatusText;
            set { _animationStatusText = value; OnPropertyChanged(); }
        }

        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        private AnimationRecord _selectedAnimation;
        public AnimationRecord SelectedAnimation
        {
            get => _selectedAnimation;
            set
            {
                _selectedAnimation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanImport));
                OnPropertyChanged(nameof(SelectedAnimationDetails));
                RefreshSelectedAnimationSources();
            }
        }

        public bool CanImport => SelectedAnimation != null;

        public string SelectedAnimationDetails
        {
            get => GetAnimationDetailsText(SelectedAnimation);
        }

        public ObservableCollectionExtended<AnimationRecord> FilteredAnimations { get; } = new();
        public ObservableCollectionExtended<GestureEntryViewModel> GestureEntries { get; } = new();
        public ObservableCollectionExtended<AnimationSourceOption> AvailableAnimationSources { get; } = new();
        public ObservableCollectionExtended<GestureImportTargetOption> GestureImportTargets { get; } = new();

        private AnimationSourceOption _selectedAnimationSource;
        public AnimationSourceOption SelectedAnimationSource
        {
            get => _selectedAnimationSource;
            set
            {
                _selectedAnimationSource = value;
                OnPropertyChanged();
                if (SelectedAnimation != null)
                {
                    LoadAnimationPreview(SelectedAnimation, AnimPreviewControl);
                }
            }
        }

        private GestureImportTargetOption _selectedGestureImportTarget;
        public GestureImportTargetOption SelectedGestureImportTarget
        {
            get => _selectedGestureImportTarget;
            set
            {
                _selectedGestureImportTarget = value;
                OnPropertyChanged();
            }
        }

        private GestureEntryViewModel _selectedGestureEntry;
        public GestureEntryViewModel SelectedGestureEntry
        {
            get => _selectedGestureEntry;
            set { _selectedGestureEntry = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelectedGesture)); LoadGestureProperties(); }
        }

        public bool HasSelectedGesture => SelectedGestureEntry != null;

        // Edit properties for gesture editor
        private string _editPoseSet = "None";
        public string EditPoseSet { get => _editPoseSet; set { _editPoseSet = value; OnPropertyChanged(); } }
        private string _editPoseAnim = "None";
        public string EditPoseAnim { get => _editPoseAnim; set { _editPoseAnim = value; OnPropertyChanged(); } }
        private string _editGestureSet = "None";
        public string EditGestureSet { get => _editGestureSet; set { _editGestureSet = value; OnPropertyChanged(); } }
        private string _editGestureAnim = "None";
        public string EditGestureAnim { get => _editGestureAnim; set { _editGestureAnim = value; OnPropertyChanged(); } }
        private string _editTransitionSet = "None";
        public string EditTransitionSet { get => _editTransitionSet; set { _editTransitionSet = value; OnPropertyChanged(); } }
        private string _editTransitionAnim = "None";
        public string EditTransitionAnim { get => _editTransitionAnim; set { _editTransitionAnim = value; OnPropertyChanged(); } }
        private string _editPlayRate = "1";
        public string EditPlayRate { get => _editPlayRate; set { _editPlayRate = value; OnPropertyChanged(); } }
        private string _editStartOffset = "0";
        public string EditStartOffset { get => _editStartOffset; set { _editStartOffset = value; OnPropertyChanged(); } }
        private string _editEndOffset = "0";
        public string EditEndOffset { get => _editEndOffset; set { _editEndOffset = value; OnPropertyChanged(); } }
        private string _editStartBlendDuration = "0.1";
        public string EditStartBlendDuration { get => _editStartBlendDuration; set { _editStartBlendDuration = value; OnPropertyChanged(); } }
        private string _editEndBlendDuration = "0.1";
        public string EditEndBlendDuration { get => _editEndBlendDuration; set { _editEndBlendDuration = value; OnPropertyChanged(); } }
        private string _editWeight = "1";
        public string EditWeight { get => _editWeight; set { _editWeight = value; OnPropertyChanged(); } }
        private bool _editOneShotAnim;
        public bool EditOneShotAnim { get => _editOneShotAnim; set { _editOneShotAnim = value; OnPropertyChanged(); } }
        private bool _editSnapToPose;
        public bool EditSnapToPose { get => _editSnapToPose; set { _editSnapToPose = value; OnPropertyChanged(); } }
        private bool _editPlayUntilNext;
        public bool EditPlayUntilNext { get => _editPlayUntilNext; set { _editPlayUntilNext = value; OnPropertyChanged(); } }
        private bool _editUseDynAnimSets;
        public bool EditUseDynAnimSets { get => _editUseDynAnimSets; set { _editUseDynAnimSets = value; OnPropertyChanged(); } }

        // Starting pose — track-level properties (not per-gesture)
        private string _editStartingPoseSet = "None";
        public string EditStartingPoseSet { get => _editStartingPoseSet; set { _editStartingPoseSet = value; OnPropertyChanged(); } }
        private string _editStartingPoseAnim = "None";
        public string EditStartingPoseAnim { get => _editStartingPoseAnim; set { _editStartingPoseAnim = value; OnPropertyChanged(); } }

        // Ambient Performance properties
        private List<AnimationRecord> _allAmbPerfs = new();
        public ObservableCollectionExtended<AnimationRecord> FilteredAmbPerfs { get; } = new();

        private AnimationRecord _selectedAmbPerf;
        public AnimationRecord SelectedAmbPerf
        {
            get => _selectedAmbPerf;
            set
            {
                _selectedAmbPerf = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanImportAmbPerf));
                RefreshSelectedAmbPerfSources();
            }
        }

        public bool CanImportAmbPerf => SelectedAmbPerf != null;

        public ObservableCollectionExtended<AnimationSourceOption> AvailableAmbPerfSources { get; } = new();

        private AnimationSourceOption _selectedAmbPerfSource;
        public AnimationSourceOption SelectedAmbPerfSource
        {
            get => _selectedAmbPerfSource;
            set
            {
                _selectedAmbPerfSource = value;
                OnPropertyChanged();
                if (SelectedAmbPerf != null)
                {
                    LoadAmbPerfPreview(SelectedAmbPerf);
                }
            }
        }

        private string _ambPerfStatusText = "";
        public string AmbPerfStatusText
        {
            get => _ambPerfStatusText;
            set { _ambPerfStatusText = value; OnPropertyChanged(); }
        }

        private string _currentAmbPerfInfo = "";
        public string CurrentAmbPerfInfo
        {
            get => _currentAmbPerfInfo;
            set { _currentAmbPerfInfo = value; OnPropertyChanged(); }
        }

        private List<GestureTrackRecord> _allGestureTracks = [];
        public ObservableCollectionExtended<GestureTrackRecord> FilteredGestureTracks { get; } = new();
        public ObservableCollectionExtended<GestureTrackSourceOption> AvailableGestureTrackSources { get; } = new();
        public ObservableCollectionExtended<AssetDatabaseWindow.GestureFilterCriterion> GestureTrackCriteria { get; } = new();

        private string _gestureTrackStartingPoseSet;
        public string GestureTrackStartingPoseSet
        {
            get => _gestureTrackStartingPoseSet;
            set { _gestureTrackStartingPoseSet = value; OnPropertyChanged(); ApplyGestureTrackFilter(); }
        }

        private string _gestureTrackStartingPoseAnim;
        public string GestureTrackStartingPoseAnim
        {
            get => _gestureTrackStartingPoseAnim;
            set { _gestureTrackStartingPoseAnim = value; OnPropertyChanged(); ApplyGestureTrackFilter(); }
        }

        private string _gestureTrackNodeTlkFilter;
        public string GestureTrackNodeTlkFilter
        {
            get => _gestureTrackNodeTlkFilter;
            set { _gestureTrackNodeTlkFilter = value; OnPropertyChanged(); ApplyGestureTrackFilter(); }
        }

        private GestureTrackRecord _selectedGestureTrack;
        public GestureTrackRecord SelectedGestureTrack
        {
            get => _selectedGestureTrack;
            set
            {
                _selectedGestureTrack = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanImportGestureTrack));
                RefreshSelectedGestureTrackSources();
            }
        }

        private GestureTrackSourceOption _selectedGestureTrackSource;
        public GestureTrackSourceOption SelectedGestureTrackSource
        {
            get => _selectedGestureTrackSource;
            set
            {
                _selectedGestureTrackSource = value;
                OnPropertyChanged();
            }
        }

        private string _gestureTrackStatusText = "";
        public string GestureTrackStatusText
        {
            get => _gestureTrackStatusText;
            set { _gestureTrackStatusText = value; OnPropertyChanged(); }
        }

        public bool CanImportGestureTrack => SelectedGestureTrackSource != null;

        #endregion

        // File list from DB for resolving paths
        private List<(string FileName, string ContentDir)> _fileListExtended = new();

        // Animation preview state
        private IMEPackage _animPreviewPcc;
        private IMEPackage _ambPerfPreviewPcc;
        private IMEPackage _gestureTrackPreviewPcc;
        private readonly PackageCache _ambPerfPreviewPackageCache = new();
        private List<MeshRecord> _skeletonMeshes;

        public GestureAnimationImporterDialog(ExportEntry gestureTrackExport, Window owner)
        {
            _gestureTrackExport = gestureTrackExport;
            _pcc = gestureTrackExport.FileRef;
            Owner = owner;

            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);

            TargetExportInfo = $"Target: {gestureTrackExport.InstancedFullPath} (UIndex {gestureTrackExport.UIndex}) in {Path.GetFileName(_pcc.FilePath)}";

            // Hide gesture-track-specific UI for non-BioEvtSysTrackGesture targets
            if (UsesDefaultPoseSetTarget || IsSFXSkeletalMeshActor)
            {
                GbGestureImportTarget.Visibility = Visibility.Collapsed;
                GbPropertyGroups.Visibility = Visibility.Collapsed;
                GbGestureKeySettings.Visibility = Visibility.Collapsed;
                EditGesturesTab.Visibility = Visibility.Collapsed;
                TrackGesturesTab.Visibility = Visibility.Collapsed;
            }

            // Show Ambient Performances tab for targets that expose m_pPerfGameData
            if (UsesDefaultPoseSetTarget)
            {
                AmbPerfTab.Visibility = Visibility.Visible;
                LoadCurrentAmbPerfInfo();
            }

            // Initialize source game selector
            _selectedAnimSourceGame = _pcc.Game;
            SourceGameComboBox.ItemsSource = AvailableSourceGames;
            SourceGameComboBox.SelectedItem = _pcc.Game;

            EnsureGestureTrackCriteria();
            LoadExistingGestures();
            _ = LoadDatabaseAsync();
        }

        private void ClearLoadedAnimationDatabaseState()
        {
            _db = null;
            _fileListExtended.Clear();
            _allAnimations = [];
            _skeletonMeshes = [];
            SelectedAnimation = null;
            AvailableAnimationSources.ClearEx();
            SelectedAnimationSource = null;
            FilteredAnimations.ReplaceAll(_allAnimations);
            PreviewMeshComboBox.ItemsSource = null;
            PreviewMeshComboBox.SelectedItem = null;
            AnimPreviewControl.ClearAnimation();
            _allGestureTracks = [];
            SelectedGestureTrack = null;
            AvailableGestureTrackSources.ClearEx();
            SelectedGestureTrackSource = null;
            FilteredGestureTracks.ClearEx();

            if (UsesDefaultPoseSetTarget)
            {
                _allAmbPerfs = [];
                SelectedAmbPerf = null;
                AvailableAmbPerfSources.ClearEx();
                SelectedAmbPerfSource = null;
                FilteredAmbPerfs.ReplaceAll(_allAmbPerfs);
                AmbPerfMeshComboBox.ItemsSource = null;
                AmbPerfMeshComboBox.SelectedItem = null;
                AmbPerfPreviewControl.ClearAnimation();
            }
        }

        private async Task LoadDatabaseAsync()
        {
            MEGame game = _selectedAnimSourceGame;
            string dbPath = AssetDatabaseWindow.GetDBPath(game);
            if (!File.Exists(dbPath))
            {
                ClearLoadedAnimationDatabaseState();
                AnimationStatusText = $"No {game} asset database found. Please generate one in the Asset Database tool.";
                if (UsesDefaultPoseSetTarget)
                {
                    AmbPerfStatusText = AnimationStatusText;
                }
                return;
            }

            AnimationStatusText = $"Loading {game} database...";

            _db = new AssetDB();
            await AssetDatabaseWindow.LoadDatabase(dbPath, game, _db, CancellationToken.None);

            if (_db.DatabaseVersion != AssetDatabaseWindow.dbCurrentBuild)
            {
                ClearLoadedAnimationDatabaseState();
                AnimationStatusText = $"{game} asset database is out of date. Please regenerate it in the Asset Database tool.";
                if (UsesDefaultPoseSetTarget)
                {
                    AmbPerfStatusText = AnimationStatusText;
                }
                return;
            }

            // Build file list for resolving paths
            _fileListExtended.Clear();
            foreach ((string fileName, int dirIndex) in _db.FileList)
            {
                _fileListExtended.Add((fileName, _db.ContentDir[dirIndex]));
            }

            _allAnimations = _db.Animations.Where(a => !a.IsAmbPerf).ToList();
            FilteredAnimations.ReplaceAll(_allAnimations);
            AnimationStatusText = $"{_allAnimations.Count} {game} animations loaded.";

            _allGestureTracks = _db.GestureTracks.ToList();
            LoadGestureTrackTlkStrings(game, _db.Localization);
            FilteredGestureTracks.ReplaceAll(_allGestureTracks);
            GestureTrackStatusText = $"{_allGestureTracks.Count} {game} gesture tracks loaded.";

            // Set up skeleton mesh list for animation preview
            _skeletonMeshes = _db.Meshes.Where(m => m.IsSkeleton).ToList();
            PreviewMeshComboBox.ItemsSource = _skeletonMeshes;

            string defaultMesh = game switch
            {
                MEGame.LE1 or MEGame.ME1 => "QRN_FAC_ARM_LGTa_MDL",
                MEGame.LE2 or MEGame.ME2 => "QRN_TLI_LGTa_MDL",
                _ => "QRN_ARM_TLIa_MDL"
            };
            int meshIdx = _skeletonMeshes.FindIndex(mr => mr.MeshName == defaultMesh);
            if (meshIdx >= 0)
            {
                PreviewMeshComboBox.SelectedIndex = meshIdx;
            }

            // Load ambient performances for targets that expose m_pPerfGameData
            if (UsesDefaultPoseSetTarget)
            {
                _allAmbPerfs = _db.Animations.Where(a => a.IsAmbPerf).ToList();
                FilteredAmbPerfs.ReplaceAll(_allAmbPerfs);
                AmbPerfStatusText = $"{_allAmbPerfs.Count} {game} ambient performances loaded.";
                AmbPerfMeshComboBox.ItemsSource = _skeletonMeshes;
                if (meshIdx >= 0)
                {
                    AmbPerfMeshComboBox.SelectedIndex = meshIdx;
                }
            }
        }

        private void LoadGestureTrackTlkStrings(MEGame game, MELocalization localization)
        {
            var mergedTlkValues = new Dictionary<int, string>();
            string gamePath = MEDirectories.GetDefaultGamePath(game);
            if (!string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath))
            {
                var talkFiles = game.IsGame1()
                    ? TLKSystem.LoadTLKs(game, localization, male: false, gamePath)
                        .Concat(TLKSystem.LoadTLKs(game, localization, male: true, gamePath))
                    : TLKSystem.LoadTLKs(game, localization, male: true, gamePath);

                foreach (var talkFile in talkFiles)
                {
                    foreach (var stringRef in talkFile.StringRefs.Where(stringRef => stringRef.StringID > 0))
                    {
                        mergedTlkValues[stringRef.StringID] = NormalizeTlkText(stringRef.Data);
                    }
                }
            }

            foreach (GestureTrackRecord track in _allGestureTracks)
            {
                track.NodeTlkString = track.NodeStrRef > 0
                    ? mergedTlkValues.GetValueOrDefault(track.NodeStrRef, $"TLK #{track.NodeStrRef}")
                    : string.Empty;
            }
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

        private void LoadExistingGestures()
        {
            int? selectedGestureIndex = SelectedGestureImportTarget?.GestureIndex;
            GestureEntries.ClearEx();
            var gestures = _gestureTrackExport.GetProperty<ArrayProperty<StructProperty>>("m_aGestures");
            var trackKeys = _gestureTrackExport.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys");
            if (gestures != null)
            {
                for (int i = 0; i < gestures.Count; i++)
                {
                    GestureEntries.Add(new GestureEntryViewModel
                    {
                        Index = i,
                        GestureStruct = gestures[i],
                        TrackKeyStruct = trackKeys != null && i < trackKeys.Count ? trackKeys[i] : null
                    });
                }
            }

            // Load track-level starting pose properties
            EditStartingPoseSet = _gestureTrackExport.GetProperty<NameProperty>("nmStartingPoseSet")?.Value.Instanced ?? "None";
            EditStartingPoseAnim = _gestureTrackExport.GetProperty<NameProperty>("nmStartingPoseAnim")?.Value.Instanced ?? "None";

            RefreshGestureImportTargets(selectedGestureIndex);
        }

        private void RefreshGestureImportTargets(int? preferredGestureIndex = null)
        {
            List<GestureImportTargetOption> options =
            [
                new GestureImportTargetOption { DisplayName = "Create New BioGestureData", GestureIndex = null },
                .. GestureEntries.Select(entry => new GestureImportTargetOption
                {
                    GestureIndex = entry.Index,
                    DisplayName = $"BioGestureData {entry.Index}: {DescribeGestureEntry(entry.GestureStruct)}"
                })
            ];

            GestureImportTargets.ReplaceAll(options);
            SelectedGestureImportTarget = options.FirstOrDefault(option => option.GestureIndex == preferredGestureIndex) ?? options[0];
        }

        private static string DescribeGestureEntry(StructProperty gestureStruct)
        {
            if (gestureStruct == null)
            {
                return "Empty";
            }

            string[] animationNames =
            [
                gestureStruct.GetProp<NameProperty>("nmPoseAnim")?.Value.Instanced,
                gestureStruct.GetProp<NameProperty>("nmGestureAnim")?.Value.Instanced,
                gestureStruct.GetProp<NameProperty>("nmTransitionAnim")?.Value.Instanced,
            ];

            string firstName = animationNames.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && name != "None");
            return firstName ?? "No animation assigned";
        }

        private void LoadGestureProperties()
        {
            if (SelectedGestureEntry?.GestureStruct == null) return;
            var g = SelectedGestureEntry.GestureStruct;
            EditPoseSet = g.GetProp<NameProperty>("nmPoseSet")?.Value.Instanced ?? "None";
            EditPoseAnim = g.GetProp<NameProperty>("nmPoseAnim")?.Value.Instanced ?? "None";
            EditGestureSet = g.GetProp<NameProperty>("nmGestureSet")?.Value.Instanced ?? "None";
            EditGestureAnim = g.GetProp<NameProperty>("nmGestureAnim")?.Value.Instanced ?? "None";
            EditTransitionSet = g.GetProp<NameProperty>("nmTransitionSet")?.Value.Instanced ?? "None";
            EditTransitionAnim = g.GetProp<NameProperty>("nmTransitionAnim")?.Value.Instanced ?? "None";
            EditPlayRate = (g.GetProp<FloatProperty>("fPlayRate")?.Value ?? 1f).ToString("F2");
            EditStartOffset = (g.GetProp<FloatProperty>("fStartOffset")?.Value ?? 0f).ToString("F2");
            EditEndOffset = (g.GetProp<FloatProperty>("fEndOffset")?.Value ?? 0f).ToString("F2");
            EditStartBlendDuration = (g.GetProp<FloatProperty>("fStartBlendDuration")?.Value ?? 0.1f).ToString("F2");
            EditEndBlendDuration = (g.GetProp<FloatProperty>("fEndBlendDuration")?.Value ?? 0.1f).ToString("F2");
            EditWeight = (g.GetProp<FloatProperty>("fWeight")?.Value ?? 1f).ToString("F2");
            EditOneShotAnim = g.GetProp<BoolProperty>("bOneShotAnim") ?? false;
            EditSnapToPose = g.GetProp<BoolProperty>("bSnapToPose") ?? false;
            EditPlayUntilNext = g.GetProp<BoolProperty>("bPlayUntilNext") ?? false;
            EditUseDynAnimSets = g.GetProp<BoolProperty>("bUseDynAnimSets") ?? false;
        }

        #region Source Game Selection

        private void SourceGameComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SourceGameComboBox.SelectedItem is MEGame game && game != _selectedAnimSourceGame)
            {
                _selectedAnimSourceGame = game;
                SelectedAnimation = null;
                AnimPreviewControl.ClearAnimation();
                _ = LoadDatabaseAsync();
            }
        }

        #endregion

        #region Search/Filter

        private string _lastSearchText = "";

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _lastSearchText = SearchBox.Text?.Trim() ?? "";
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (string.IsNullOrEmpty(_lastSearchText))
            {
                FilteredAnimations.ReplaceAll(_allAnimations);
            }
            else
            {
                var filtered = _allAnimations.Where(a =>
                    (a.AnimSequence?.Contains(_lastSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (a.SeqName?.Contains(_lastSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (a.AnimData?.Contains(_lastSearchText, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                FilteredAnimations.ReplaceAll(filtered);
            }
            AnimationStatusText = $"{FilteredAnimations.Count} / {_allAnimations.Count} animations shown.";
        }

        private void EnsureGestureTrackCriteria()
        {
            if (GestureTrackCriteria.Count == 0)
            {
                AddGestureTrackCriterion();
            }
            else
            {
                UpdateGestureTrackCriteriaMetadata();
            }
        }

        private void AddGestureTrackCriterion()
        {
            var criterion = new AssetDatabaseWindow.GestureFilterCriterion();
            criterion.PropertyChanged += GestureTrackCriterion_PropertyChanged;
            GestureTrackCriteria.Add(criterion);
            UpdateGestureTrackCriteriaMetadata();
        }

        private void RemoveGestureTrackCriterion(AssetDatabaseWindow.GestureFilterCriterion criterion)
        {
            if (criterion == null)
            {
                return;
            }

            criterion.PropertyChanged -= GestureTrackCriterion_PropertyChanged;
            GestureTrackCriteria.Remove(criterion);
            EnsureGestureTrackCriteria();
            ApplyGestureTrackFilter();
        }

        private void UpdateGestureTrackCriteriaMetadata()
        {
            for (int i = 0; i < GestureTrackCriteria.Count; i++)
            {
                GestureTrackCriteria[i].GroupLabel = $"Gesture {i + 1}:";
                GestureTrackCriteria[i].CanRemove = GestureTrackCriteria.Count > 1;
            }
        }

        private void GestureTrackCriterion_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not nameof(AssetDatabaseWindow.GestureFilterCriterion.GroupLabel)
                and not nameof(AssetDatabaseWindow.GestureFilterCriterion.CanRemove))
            {
                ApplyGestureTrackFilter();
            }
        }

        private void AddGestureTrackCriterion_Click(object sender, RoutedEventArgs e) => AddGestureTrackCriterion();

        private void RemoveGestureTrackCriterion_Click(object sender, RoutedEventArgs e) =>
            RemoveGestureTrackCriterion((sender as FrameworkElement)?.Tag as AssetDatabaseWindow.GestureFilterCriterion);

        private void ApplyGestureTrackFilter()
        {
            FilteredGestureTracks.ReplaceAll(_allGestureTracks.Where(MatchesGestureTrackFilters));
            GestureTrackStatusText = $"{FilteredGestureTracks.Count} / {_allGestureTracks.Count} gesture tracks shown.";
        }

        private bool MatchesGestureTrackFilters(GestureTrackRecord track)
        {
            if (!MatchesGestureValue(track.StartingPoseSet, GestureTrackStartingPoseSet)
                || !MatchesGestureValue(track.StartingPoseAnim, GestureTrackStartingPoseAnim)
                || !MatchesGestureNodeTlk(track, GestureTrackNodeTlkFilter))
            {
                return false;
            }

            return GestureTrackCriteria
                .Where(criterion => criterion.HasValues)
                .All(criterion => track.Gestures.Any(gesture => MatchesGestureCriterion(gesture, criterion)));
        }

        private static bool MatchesGestureNodeTlk(GestureTrackRecord track, string filter) =>
            string.IsNullOrWhiteSpace(filter)
            || ContainsText(track.NodeTlkString, filter.Trim())
            || (track.NodeStrRef > 0 && ContainsText(track.NodeStrRef.ToString(), filter.Trim()));

        private static bool MatchesGestureCriterion(GestureDataRecord gesture, AssetDatabaseWindow.GestureFilterCriterion criterion) =>
            MatchesGestureValue(gesture.PoseSet, criterion.PoseSet)
            && MatchesGestureValue(gesture.PoseAnim, criterion.PoseAnim)
            && MatchesGestureValue(gesture.GestureSet, criterion.GestureSet)
            && MatchesGestureValue(gesture.GestureAnim, criterion.GestureAnim)
            && MatchesGestureValue(gesture.TransitionSet, criterion.TransitionSet)
            && MatchesGestureValue(gesture.TransitionAnim, criterion.TransitionAnim);

        private static bool MatchesGestureValue(string value, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            string trimmedFilter = filter.Trim();
            return string.Equals(trimmedFilter, "None", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(value, "None", StringComparison.OrdinalIgnoreCase)
                : ContainsText(value, trimmedFilter);
        }

        private static bool ContainsText(string value, string filter) =>
            value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

        #endregion

        #region Animation Import

        private void AnimationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(SelectedAnimation));
            OnPropertyChanged(nameof(CanImport));
            OnPropertyChanged(nameof(SelectedAnimationDetails));
            LoadAnimationPreview(SelectedAnimation, AnimPreviewControl);
        }

        private void PreviewMesh_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadPreviewMesh(PreviewMeshComboBox.SelectedItem as MeshRecord, AnimPreviewControl);
        }

        /// <summary>
        /// Resolves a MeshRecord from the database and loads it into the given preview control.
        /// </summary>
        private void LoadPreviewMesh(MeshRecord meshRecord, AnimationPreviewControl previewControl)
        {
            if (meshRecord == null || !meshRecord.Usages.Any()) return;

            string filePath = null;
            int uIndex = 0;
            foreach (var (fileKey, tempUIndex, _) in meshRecord.Usages)
            {
                filePath = GetFilePath(fileKey);
                if (filePath != null)
                {
                    uIndex = tempUIndex;
                    break;
                }
            }

            if (filePath == null)
            {
                previewControl.Clear();
                return;
            }

            using var meshPackage = MEPackageHandler.OpenMEPackage(filePath);
            if (meshPackage.IsUExport(uIndex))
            {
                previewControl.LoadSkeletalMesh(meshPackage.GetUExport(uIndex));
            }
        }

        /// <summary>
        /// Resolves an AnimationRecord from the database and loads it into the given preview control for playback.
        /// </summary>
        private void LoadAnimationPreview(AnimationRecord anim, AnimationPreviewControl previewControl)
        {
            if (anim == null || !anim.Usages.Any())
            {
                previewControl.ClearAnimation();
                return;
            }

            if (!TryResolveAnimationSource(anim, out string filePath, out int animUIndex, ReferenceEquals(anim, SelectedAnimation) ? SelectedAnimationSource : null))
            {
                previewControl.ClearAnimation();
                return;
            }

            _animPreviewPcc?.Dispose();
            _animPreviewPcc = MEPackageHandler.OpenMEPackage(filePath);

            if (_animPreviewPcc.IsUExport(animUIndex))
            {
                previewControl.LoadAnimSequence(_animPreviewPcc.GetUExport(animUIndex));
                previewControl.Play();
            }
        }

        /// <summary>
        /// Resolves a file key from the database into a file path on disk.
        /// </summary>
        private string GetFilePath(int fileKey)
        {
            return TryGetFilePath(fileKey, out string filePath, out _, out _) ? filePath : null;
        }

        private void ImportAnimation_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAnimation == null)
            {
                MessageBox.Show("Please select an animation first.", "No Animation Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool poseChecked = CbPoseGroup.IsChecked == true;
            bool gestureChecked = CbGestureGroup.IsChecked == true;
            bool transitionChecked = CbTransitionGroup.IsChecked == true;
            bool startingPoseChecked = CbStartingPoseGroup.IsChecked == true;

            if (!poseChecked && !gestureChecked && !transitionChecked && !startingPoseChecked)
            {
                MessageBox.Show("Please select at least one property group.", "No Group Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var (setName, seqName) = ImportAnimationFromDatabase(SelectedAnimation);

                if (UsesDefaultPoseSetTarget)
                {
                    // For default-pose targets, set m_nmDefaultPoseAnim on the export
                    _gestureTrackExport.WriteProperty(new NameProperty(seqName, "m_nmDefaultPoseAnim"));
                }
                else if (IsSFXSkeletalMeshActor)
                {
                    // For SFXSkeletalMeshActor, set AnimSeqName on the AnimNodeSequence child
                    ExportEntry skelMeshComp = FindMainSkeletalMeshComponent(_gestureTrackExport);
                    if (skelMeshComp != null)
                    {
                        ExportEntry animNodeSeq = _pcc.Exports.FirstOrDefault(exp =>
                            exp.idxLink == skelMeshComp.UIndex && exp.ClassName == "AnimNodeSequence");
                        if (animNodeSeq != null)
                        {
                            animNodeSeq.WriteProperty(new NameProperty(seqName, "AnimSeqName"));
                        }
                    }
                }
                else
                {
                    // For BioEvtSysTrackGesture, create BioGestureData and BioTrackKey
                    AddGestureEntry(setName, seqName, poseChecked, gestureChecked, transitionChecked, startingPoseChecked, SelectedGestureImportTarget?.GestureIndex);
                }

                // Reload the gesture list
                LoadExistingGestures();

                StatusMessage = $"Successfully imported {SelectedAnimation.AnimSequence} and added gesture entry.";
                MessageBox.Show($"Animation '{SelectedAnimation.AnimSequence}' has been imported and linked to the gesture track.", "Import Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing animation: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Core method that imports an animation from the asset database into the package.
        /// Resolves the source file, imports the AnimSequence, finds/imports the BioDynamicAnimSet,
        /// and sets m_bUseDynamicAnimSets on the gesture track.
        /// Returns the (setName, seqName) for use in gesture property assignment.
        /// </summary>
        private (string setName, string seqName) ImportAnimationFromDatabase(AnimationRecord animation, AnimationSourceOption preferredSource = null)
        {
            preferredSource ??= ReferenceEquals(animation, SelectedAnimation) ? SelectedAnimationSource : null;
            if (!TryResolveAnimationSource(animation, out string sourceFilePath, out int animUIndex, preferredSource))
            {
                throw new Exception("Could not resolve the animation's source file. Make sure the game is properly configured.");
            }

            using IMEPackage sourcePackage = MEPackageHandler.OpenMEPackage(sourceFilePath);
            ExportEntry sourceAnimSeq = sourcePackage.GetUExport(animUIndex);

            var relinkerOptions = new RelinkerOptionsPackage
            {
                ImportExportDependencies = true,
                PortImportsMemorySafe = true,
                PortExportsAsImportsWhenPossible = true,
            };

            IEntry parent = EntryImporter.GetOrAddCrossImportOrPackage(sourceAnimSeq.ParentFullPath, sourcePackage, _pcc, relinkerOptions);
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, sourceAnimSeq, _pcc, parent, true, relinkerOptions, out IEntry importedEntry);
            ExportEntry importedAnimSeq = GetImportedExport(importedEntry, sourceAnimSeq, parent, relinkerOptions, "animation sequence");

            NameReference seqNameRef = importedAnimSeq.GetProperty<NameProperty>("SequenceName").Value;
            IEntry bioAnimSetData = _pcc.GetEntry(importedAnimSeq.GetProperty<ObjectProperty>("m_pBioAnimSetData").Value);
            string setName = importedAnimSeq.ObjectName.Name.RemoveRight(seqNameRef.Name.Length + 1);
            if (string.IsNullOrWhiteSpace(setName))
                setName = seqNameRef.Name;

            ExportEntry dynamicAnimSet = FindOrImportBioDynamicAnimSet(_gestureTrackExport, bioAnimSetData, setName, importedAnimSeq, sourcePackage, sourceAnimSeq);
            AddAnimSequenceToDynamicAnimSet(dynamicAnimSet, importedAnimSeq, bioAnimSetData, setName);

            if (!UsesDefaultPoseSetTarget && !IsSFXSkeletalMeshActor)
            {
                _gestureTrackExport.WriteProperty(new BoolProperty(true, "m_bUseDynamicAnimSets"));
            }

            return (setName, seqNameRef.Name);
        }

        private ExportEntry GetImportedExport(IEntry importedEntry, ExportEntry sourceExport, IEntry parent, RelinkerOptionsPackage relinkerOptions, string entryDescription)
        {
            if (importedEntry is ExportEntry importedExport)
            {
                return importedExport;
            }

            if (importedEntry is not ImportEntry importedImport)
            {
                throw new Exception($"Imported {entryDescription} '{sourceExport.ObjectName}' was not an export.");
            }

            ExportEntry existingExport = _pcc.FindExport(importedImport.InstancedFullPath);
            if (existingExport != null)
            {
                return existingExport;
            }

            if (!EntryImporter.TryResolveImport(importedImport, out ExportEntry resolvedExport, cache: relinkerOptions.Cache, fileResolver: relinkerOptions.DestinationCustomImportFileResolver))
            {
                throw new Exception($"Imported {entryDescription} '{sourceExport.ObjectName}' resolved to an import, but its definition could not be found.");
            }

            return ImportResolvedExport(resolvedExport, parent, relinkerOptions, entryDescription);
        }

        private ExportEntry ImportResolvedExport(ExportEntry resolvedExport, IEntry parent, RelinkerOptionsPackage relinkerOptions, string entryDescription)
        {
            var exportRelinkerOptions = new RelinkerOptionsPackage
            {
                Cache = relinkerOptions.Cache,
                DestinationCustomImportFileResolver = relinkerOptions.DestinationCustomImportFileResolver,
                ErrorOccurredCallback = relinkerOptions.ErrorOccurredCallback,
                GamePathOverride = relinkerOptions.GamePathOverride,
                ImportChildrenOfPackages = relinkerOptions.ImportChildrenOfPackages,
                ImportExportDependencies = relinkerOptions.ImportExportDependencies,
                IsCrossGame = resolvedExport.FileRef.Game != _pcc.Game,
                PortImportsMemorySafe = relinkerOptions.PortImportsMemorySafe,
                PortLocalizationImportsMemorySafe = relinkerOptions.PortLocalizationImportsMemorySafe,
                RelinkAllowDifferingClassesInRelink = relinkerOptions.RelinkAllowDifferingClassesInRelink,
                SourceCustomImportFileResolver = relinkerOptions.SourceCustomImportFileResolver,
                TargetGameDonorDB = relinkerOptions.TargetGameDonorDB,
            };

            IEntry importedResolvedEntry = EntryImporter.ImportExport(_pcc, resolvedExport, parent?.UIndex ?? 0, exportRelinkerOptions);
            if (importedResolvedEntry is not ExportEntry importedExport)
            {
                throw new Exception($"Resolved {entryDescription} '{resolvedExport.ObjectName}' could not be imported as an export.");
            }

            Relinker.RelinkAll(exportRelinkerOptions);
            return importedExport;
        }

        private bool TryResolveAnimationSource(AnimationRecord anim, out string filePath, out int uIndex, AnimationSourceOption preferredSource = null)
        {
            filePath = null;
            uIndex = 0;
            if (anim?.Usages == null || !anim.Usages.Any()) return false;

            if (preferredSource != null && File.Exists(preferredSource.FilePath))
            {
                filePath = preferredSource.FilePath;
                uIndex = preferredSource.UIndex;
                return true;
            }

            foreach (var usage in anim.Usages)
            {
                int fileListIndex = usage.FileKey;
                uIndex = usage.UIndex;

                if (!TryGetFilePath(fileListIndex, out filePath, out _, out _)) continue;

                if (filePath != null) return true;
            }

            return false;
        }

        private void RefreshSelectedAnimationSources()
        {
            List<AnimationSourceOption> options = GetAnimationSourceOptions(SelectedAnimation);
            AvailableAnimationSources.ReplaceAll(options);

            if (options.Count == 0)
            {
                SelectedAnimationSource = null;
                return;
            }

            if (SelectedAnimationSource != null)
            {
                AnimationSourceOption matchingOption = options.FirstOrDefault(option =>
                    option.UIndex == SelectedAnimationSource.UIndex &&
                    option.FilePath.CaseInsensitiveEquals(SelectedAnimationSource.FilePath));
                if (matchingOption != null)
                {
                    SelectedAnimationSource = matchingOption;
                    return;
                }
            }

            SelectedAnimationSource = options[0];
        }

        private List<AnimationSourceOption> GetAnimationSourceOptions(AnimationRecord anim)
        {
            if (anim?.Usages == null || !anim.Usages.Any())
            {
                return [];
            }

            var options = new List<AnimationSourceOption>();
            foreach (AnimUsage usage in anim.Usages)
            {
                if (!TryGetFilePath(usage.FileKey, out string filePath, out string fileName, out string contentDir))
                {
                    continue;
                }

                if (options.Any(option => option.UIndex == usage.UIndex && option.FilePath.CaseInsensitiveEquals(filePath)))
                {
                    continue;
                }

                options.Add(new AnimationSourceOption
                {
                    DisplayName = $"{fileName} ({contentDir})",
                    FilePath = filePath,
                    UIndex = usage.UIndex,
                });
            }

            return options;
        }

        private void RefreshSelectedGestureTrackSources()
        {
            List<GestureTrackSourceOption> options = GetGestureTrackSourceOptions(SelectedGestureTrack);
            AvailableGestureTrackSources.ReplaceAll(options);

            if (options.Count == 0)
            {
                SelectedGestureTrackSource = null;
                OnPropertyChanged(nameof(CanImportGestureTrack));
                return;
            }

            if (SelectedGestureTrackSource != null)
            {
                GestureTrackSourceOption matchingOption = options.FirstOrDefault(option =>
                    option.UIndex == SelectedGestureTrackSource.UIndex
                    && option.FilePath.CaseInsensitiveEquals(SelectedGestureTrackSource.FilePath));
                if (matchingOption != null)
                {
                    SelectedGestureTrackSource = matchingOption;
                    OnPropertyChanged(nameof(CanImportGestureTrack));
                    return;
                }
            }

            SelectedGestureTrackSource = options[0];
            OnPropertyChanged(nameof(CanImportGestureTrack));
        }

        private List<GestureTrackSourceOption> GetGestureTrackSourceOptions(GestureTrackRecord track)
        {
            if (track?.Usages == null || track.Usages.Count == 0)
            {
                return [];
            }

            var options = new List<GestureTrackSourceOption>();
            foreach (GestureTrackUsage usage in track.Usages)
            {
                if (!TryGetFilePath(usage.FileKey, out string filePath, out string fileName, out string contentDir))
                {
                    continue;
                }

                if (options.Any(option => option.UIndex == usage.UIndex && option.FilePath.CaseInsensitiveEquals(filePath)))
                {
                    continue;
                }

                options.Add(new GestureTrackSourceOption
                {
                    DisplayName = $"{fileName} ({contentDir})",
                    FilePath = filePath,
                    UIndex = usage.UIndex,
                });
            }

            return options;
        }

        private void LoadGestureTrackPreview()
        {
            GestureTrackPreviewControl.UnloadExport();
            _gestureTrackPreviewPcc?.Dispose();
            _gestureTrackPreviewPcc = null;

            if (SelectedGestureTrackSource == null || !File.Exists(SelectedGestureTrackSource.FilePath))
            {
                return;
            }

            _gestureTrackPreviewPcc = MEPackageHandler.OpenMEPackage(SelectedGestureTrackSource.FilePath);
            if (!_gestureTrackPreviewPcc.IsUExport(SelectedGestureTrackSource.UIndex))
            {
                _gestureTrackPreviewPcc.Dispose();
                _gestureTrackPreviewPcc = null;
                return;
            }

            ExportEntry sourceTrack = _gestureTrackPreviewPcc.GetUExport(SelectedGestureTrackSource.UIndex);
            if (GestureTrackPreviewControl.CanParse(sourceTrack))
            {
                GestureTrackPreviewControl.LoadExport(sourceTrack);
            }
        }

        private bool TryGetFilePath(int fileKey, out string filePath, out string fileName, out string contentDir)
        {
            filePath = null;
            fileName = null;
            contentDir = null;

            if (fileKey < 0 || fileKey >= _fileListExtended.Count)
            {
                return false;
            }

            (fileName, contentDir) = _fileListExtended[fileKey];
            string fileNamePattern = fileName;
            string contentDirPath = contentDir;
            string rootPath = MEDirectories.GetDefaultGamePath(_selectedAnimSourceGame);
            if (rootPath == null || !Directory.Exists(rootPath))
            {
                return false;
            }

            filePath = Directory.EnumerateFiles(rootPath, $"{fileNamePattern}.*", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.Contains(contentDirPath));

            return filePath != null;
        }

        /// <summary>
        /// Find an existing BioDynamicAnimSet in the target sequence, or import one from the source package.
        /// Matches by m_nmOrigSetName so that KIS_DYN_* sets with the same anim set name are reused.
        /// Creates a target BioDynamicAnimSet when the source package only contains the AnimSequence.
        /// For SFXModule_Gestures, uses m_pDefaultPoseSet instead of the sequence shared anim sets.
        /// </summary>
        private ExportEntry FindOrImportBioDynamicAnimSet(ExportEntry gestureTrack, IEntry bioAnimSetData, string setName, ExportEntry importedAnimSeq, IMEPackage sourcePackage, ExportEntry sourceAnimSeq)
        {
            if (UsesDefaultPoseSetTarget)
            {
                return FindOrImportBioDynamicAnimSetForSFXModule(gestureTrack, bioAnimSetData, setName, importedAnimSeq, sourcePackage, sourceAnimSeq);
            }

            if (IsSFXSkeletalMeshActor)
            {
                return FindOrImportBioDynamicAnimSetForSkeletalMeshActor(gestureTrack, bioAnimSetData, setName, importedAnimSeq, sourcePackage, sourceAnimSeq);
            }

            return FindOrImportBioDynamicAnimSetForMatinee(gestureTrack, bioAnimSetData, setName, importedAnimSeq, sourcePackage, sourceAnimSeq);
        }

        private ExportEntry CreateBioDynamicAnimSet(IEntry parent, IEntry bioAnimSetData, string setName, ExportEntry importedAnimSeq)
        {
            ExportEntry dynAnimSet = ExportCreator.CreateExport(_pcc, $"KIS_DYN_{setName}", "BioDynamicAnimSet", parent);
            NameReference seqName = importedAnimSeq.GetProperty<NameProperty>("SequenceName").Value;
            dynAnimSet.WriteProperty(new ObjectProperty(bioAnimSetData.UIndex, "m_pBioAnimSetData"));
            dynAnimSet.WriteProperty(new NameProperty(setName, "m_nmOrigSetName"));
            dynAnimSet.WriteProperty(new ArrayProperty<ObjectProperty>("Sequences")
            {
                new ObjectProperty(importedAnimSeq.UIndex)
            });
            dynAnimSet.WriteBinary(new BioDynamicAnimSet
            {
                SequenceNamesToUnkMap = new UMultiMap<NameReference, int>(
                [
                    new KeyValuePair<NameReference, int>(seqName, 1)
                ])
            });

            return dynAnimSet;
        }

        /// <summary>
        /// SFXModule_Gestures path: BioDynamicAnimSets are children of the module and referenced via m_pDefaultPoseSet.
        /// </summary>
        private ExportEntry FindOrImportBioDynamicAnimSetForSFXModule(ExportEntry gestureModule, IEntry bioAnimSetData, string setName, ExportEntry importedAnimSeq, IMEPackage sourcePackage, ExportEntry sourceAnimSeq)
        {
            // Check if there's already a BioDynamicAnimSet via m_pDefaultPoseSet
            var defaultPoseSetProp = gestureModule.GetProperty<ObjectProperty>("m_pDefaultPoseSet");
            if (defaultPoseSetProp != null && _pcc.TryGetUExport(defaultPoseSetProp.Value, out ExportEntry existingDynSet)
                && existingDynSet.ClassName == "BioDynamicAnimSet")
            {
                // Clear existing Sequences and binary so the new animation replaces the old one.
                NameReference existingSeqName = importedAnimSeq.GetProperty<NameProperty>("SequenceName").Value;
                existingDynSet.WriteProperty(new ObjectProperty(bioAnimSetData.UIndex, "m_pBioAnimSetData"));
                existingDynSet.WriteProperty(new NameProperty(setName, "m_nmOrigSetName"));
                existingDynSet.WriteProperty(new ArrayProperty<ObjectProperty>("Sequences")
                {
                    new ObjectProperty(importedAnimSeq.UIndex)
                });
                existingDynSet.WriteBinary(new BioDynamicAnimSet
                {
                    SequenceNamesToUnkMap = new UMultiMap<NameReference, int>(
                    [
                        new KeyValuePair<NameReference, int>(existingSeqName, 1)
                    ])
                });
                return existingDynSet;
            }

            // No existing set — import one from the source package
            ExportEntry sourceDynAnimSet = FindSourceBioDynamicAnimSet(sourcePackage, sourceAnimSeq);
            if (sourceDynAnimSet == null)
            {
                ExportEntry createdDynAnimSet = CreateBioDynamicAnimSet(gestureModule, bioAnimSetData, setName, importedAnimSeq);
                gestureModule.WriteProperty(new ObjectProperty(createdDynAnimSet.UIndex, "m_pDefaultPoseSet"));
                return createdDynAnimSet;
            }

            // Import the BioDynamicAnimSet as a child of the SFXModule_Gestures
            var relinkerOptions = new RelinkerOptionsPackage();
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
                sourceDynAnimSet, _pcc, gestureModule, true, relinkerOptions, out IEntry importedDynEntry);
            ExportEntry importedDynAnimSet = GetImportedExport(importedDynEntry, sourceDynAnimSet, gestureModule, relinkerOptions, "BioDynamicAnimSet");
            EnsureUniqueObjectNameIndex(importedDynAnimSet);

            // Clear stale Sequences from the cloned source and initialize with the new animation
            importedDynAnimSet.WriteProperty(new ObjectProperty(bioAnimSetData.UIndex, "m_pBioAnimSetData"));
            importedDynAnimSet.WriteProperty(new NameProperty(setName, "m_nmOrigSetName"));
            NameReference seqName = importedAnimSeq.GetProperty<NameProperty>("SequenceName").Value;
            importedDynAnimSet.WriteProperty(new ArrayProperty<ObjectProperty>("Sequences")
            {
                new ObjectProperty(importedAnimSeq.UIndex)
            });
            importedDynAnimSet.WriteBinary(new BioDynamicAnimSet
            {
                SequenceNamesToUnkMap = new UMultiMap<NameReference, int>(
                [
                    new KeyValuePair<NameReference, int>(seqName, 1)
                ])
            });

            // Set m_pDefaultPoseSet on the SFXModule_Gestures to reference the new set
            gestureModule.WriteProperty(new ObjectProperty(importedDynAnimSet.UIndex, "m_pDefaultPoseSet"));

            return importedDynAnimSet;
        }

        /// <summary>
        /// SFXSkeletalMeshActor path: BioDynamicAnimSet is a child of the SkeletalMeshComponent.
        /// </summary>
        private ExportEntry FindOrImportBioDynamicAnimSetForSkeletalMeshActor(ExportEntry skeletalMeshActor, IEntry bioAnimSetData, string setName, ExportEntry importedAnimSeq, IMEPackage sourcePackage, ExportEntry sourceAnimSeq)
        {
            // Find the main SkeletalMeshComponent child (not HeadMesh0/HairMesh0/GearMesh0)
            ExportEntry skelMeshComp = FindMainSkeletalMeshComponent(skeletalMeshActor);
            if (skelMeshComp == null)
            {
                throw new Exception("Could not find a SkeletalMeshComponent child of the SFXSkeletalMeshActor.");
            }

            // Check if there's already a BioDynamicAnimSet under the SkeletalMeshComponent
            ExportEntry existingDynSet = _pcc.Exports.FirstOrDefault(exp =>
                exp.idxLink == skelMeshComp.UIndex && exp.ClassName == "BioDynamicAnimSet");
            if (existingDynSet != null)
            {
                // Clear existing Sequences and binary so the new animation replaces the old one.
                // SFXSkeletalMeshActor plays one animation at a time, so we replace rather than append.
                NameReference existingSeqName = importedAnimSeq.GetProperty<NameProperty>("SequenceName").Value;
                existingDynSet.WriteProperty(new ObjectProperty(bioAnimSetData.UIndex, "m_pBioAnimSetData"));
                existingDynSet.WriteProperty(new NameProperty(setName, "m_nmOrigSetName"));
                existingDynSet.WriteProperty(new ArrayProperty<ObjectProperty>("Sequences")
                {
                    new ObjectProperty(importedAnimSeq.UIndex)
                });
                existingDynSet.WriteBinary(new BioDynamicAnimSet
                {
                    SequenceNamesToUnkMap = new UMultiMap<NameReference, int>(
                    [
                        new KeyValuePair<NameReference, int>(existingSeqName, 1)
                    ])
                });
                return existingDynSet;
            }

            // No existing set — import one from the source package
            ExportEntry sourceDynAnimSet = FindSourceBioDynamicAnimSet(sourcePackage, sourceAnimSeq);
            if (sourceDynAnimSet == null)
            {
                return CreateBioDynamicAnimSet(skelMeshComp, bioAnimSetData, setName, importedAnimSeq);
            }

            // Import the BioDynamicAnimSet as a child of the SkeletalMeshComponent
            var relinkerOptions = new RelinkerOptionsPackage();
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
                sourceDynAnimSet, _pcc, skelMeshComp, true, relinkerOptions, out IEntry importedDynEntry);
            ExportEntry importedDynAnimSet = GetImportedExport(importedDynEntry, sourceDynAnimSet, skelMeshComp, relinkerOptions, "BioDynamicAnimSet");
            EnsureUniqueObjectNameIndex(importedDynAnimSet);

            // Clear stale Sequences from the cloned source and initialize with the new animation
            importedDynAnimSet.WriteProperty(new ObjectProperty(bioAnimSetData.UIndex, "m_pBioAnimSetData"));
            importedDynAnimSet.WriteProperty(new NameProperty(setName, "m_nmOrigSetName"));
            NameReference seqName = importedAnimSeq.GetProperty<NameProperty>("SequenceName").Value;
            importedDynAnimSet.WriteProperty(new ArrayProperty<ObjectProperty>("Sequences")
            {
                new ObjectProperty(importedAnimSeq.UIndex)
            });
            importedDynAnimSet.WriteBinary(new BioDynamicAnimSet
            {
                SequenceNamesToUnkMap = new UMultiMap<NameReference, int>(
                [
                    new KeyValuePair<NameReference, int>(seqName, 1)
                ])
            });

            return importedDynAnimSet;
        }

        /// <summary>
        /// BioEvtSysTrackGesture (matinee) path: BioDynamicAnimSets live in the parent sequence's shared anim sets.
        /// </summary>
        private ExportEntry FindOrImportBioDynamicAnimSetForMatinee(ExportEntry gestureTrack, IEntry bioAnimSetData, string setName, ExportEntry importedAnimSeq, IMEPackage sourcePackage, ExportEntry sourceAnimSeq)
        {
            // Walk up the tree to find the InterpData, then the sequence
            ExportEntry interpGroup = _pcc.GetUExport(gestureTrack.Parent.UIndex);
            ExportEntry interpData = _pcc.GetUExport(interpGroup.Parent.UIndex);

            // Find the Interp that references this InterpData
            ExportEntry sequenceExport = FindParentSequence(interpData);
            if (sequenceExport == null)
            {
                // Fallback: use the interpData's parent
                sequenceExport = _pcc.GetUExport(interpData.Parent.UIndex);
            }

            // BioDynamicAnimSets live in m_aSFXSharedAnimSets (LE2/LE3/ME2/ME3) or m_aBioDynAnimSets (LE1/ME1)
            string sharedAnimSetsPropName = _pcc.Game is MEGame.LE1 or MEGame.ME1 ? "m_aBioDynAnimSets" : "m_aSFXSharedAnimSets";

            // Look for an existing KIS_DYN_* BioDynamicAnimSet already referenced by this sequence
            // that has the same m_nmOrigSetName (anim set name). If found, reuse it.
            var sharedAnimSets = sequenceExport.GetProperty<ArrayProperty<ObjectProperty>>(sharedAnimSetsPropName);
            if (sharedAnimSets != null)
            {
                foreach (var animSetRef in sharedAnimSets)
                {
                    if (!_pcc.TryGetUExport(animSetRef.Value, out ExportEntry existingDynSet)) continue;
                    if (existingDynSet.ClassName != "BioDynamicAnimSet") continue;

                    var existingSetName = existingDynSet.GetProperty<NameProperty>("m_nmOrigSetName");
                    if (existingSetName != null && existingSetName.Value.Name == setName)
                    {
                        return existingDynSet; // Reuse this existing KIS_DYN set — caller will add the anim to it
                    }
                }
            }

            // None found in target — import from source
            ExportEntry sourceDynAnimSet = FindSourceBioDynamicAnimSet(sourcePackage, sourceAnimSeq);
            if (sourceDynAnimSet == null)
            {
                ExportEntry createdDynAnimSet = CreateBioDynamicAnimSet(sequenceExport, bioAnimSetData, setName, importedAnimSeq);
                if (sharedAnimSets == null)
                {
                    sharedAnimSets = new ArrayProperty<ObjectProperty>(sharedAnimSetsPropName);
                }
                sharedAnimSets.Add(new ObjectProperty(createdDynAnimSet.UIndex));
                sequenceExport.WriteProperty(sharedAnimSets);
                return createdDynAnimSet;
            }

            // Import the BioDynamicAnimSet from the source package into the sequence
            var relinkerOptions = new RelinkerOptionsPackage();
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
                sourceDynAnimSet, _pcc, sequenceExport, true, relinkerOptions, out IEntry importedDynEntry);
            ExportEntry importedDynAnimSet = GetImportedExport(importedDynEntry, sourceDynAnimSet, sequenceExport, relinkerOptions, "BioDynamicAnimSet");
            EnsureUniqueObjectNameIndex(importedDynAnimSet);

            // Clear stale Sequences from the cloned source and initialize with the new animation
            importedDynAnimSet.WriteProperty(new ObjectProperty(bioAnimSetData.UIndex, "m_pBioAnimSetData"));
            importedDynAnimSet.WriteProperty(new NameProperty(setName, "m_nmOrigSetName"));
            NameReference seqName = importedAnimSeq.GetProperty<NameProperty>("SequenceName").Value;
            importedDynAnimSet.WriteProperty(new ArrayProperty<ObjectProperty>("Sequences")
            {
                new ObjectProperty(importedAnimSeq.UIndex)
            });
            importedDynAnimSet.WriteBinary(new BioDynamicAnimSet
            {
                SequenceNamesToUnkMap = new UMultiMap<NameReference, int>(
                [
                    new KeyValuePair<NameReference, int>(seqName, 1)
                ])
            });

            // Add to the sequence's shared anim sets property
            if (sharedAnimSets == null)
            {
                sharedAnimSets = new ArrayProperty<ObjectProperty>(sharedAnimSetsPropName);
            }
            sharedAnimSets.Add(new ObjectProperty(importedDynAnimSet.UIndex));
            sequenceExport.WriteProperty(sharedAnimSets);

            return importedDynAnimSet;
        }

        private void EnsureUniqueObjectNameIndex(ExportEntry importedDynAnimSet)
        {
            bool hasDuplicateIndex = _pcc.Exports.Any(export =>
                !ReferenceEquals(export, importedDynAnimSet) &&
                export.idxLink == importedDynAnimSet.idxLink &&
                export.ObjectName == importedDynAnimSet.ObjectName);

            if (hasDuplicateIndex)
            {
                importedDynAnimSet.indexValue = _pcc.GetNextIndexForInstancedName(importedDynAnimSet);
            }
        }

        /// <summary>
        /// Searches the source package for a BioDynamicAnimSet to use as a template for importing.
        /// </summary>
        private static ExportEntry FindSourceBioDynamicAnimSet(IMEPackage sourcePackage, ExportEntry sourceAnimSeq)
        {
            var sourceAnimSetDataRef = sourceAnimSeq.GetProperty<ObjectProperty>("m_pBioAnimSetData");
            ExportEntry sourceDynAnimSet = null;

            // First pass: find a KIS_DYN_* named BioDynamicAnimSet with matching m_pBioAnimSetData
            if (sourceAnimSetDataRef != null)
            {
                foreach (var exp in sourcePackage.Exports)
                {
                    if (exp.ClassName != "BioDynamicAnimSet") continue;
                    if (!exp.ObjectName.Name.StartsWith("KIS_DYN_", StringComparison.OrdinalIgnoreCase)) continue;
                    var dataRef = exp.GetProperty<ObjectProperty>("m_pBioAnimSetData");
                    if (dataRef != null && dataRef.Value == sourceAnimSetDataRef.Value)
                    {
                        sourceDynAnimSet = exp;
                        break;
                    }
                }
            }

            // Second pass: any KIS_DYN_* BioDynamicAnimSet
            sourceDynAnimSet ??= sourcePackage.Exports.FirstOrDefault(exp =>
                exp.ClassName == "BioDynamicAnimSet" &&
                exp.ObjectName.Name.StartsWith("KIS_DYN_", StringComparison.OrdinalIgnoreCase));

            // Third pass: any BioDynamicAnimSet at all
            sourceDynAnimSet ??= sourcePackage.Exports.FirstOrDefault(exp => exp.ClassName == "BioDynamicAnimSet");

            return sourceDynAnimSet;
        }

        private ExportEntry FindParentSequence(ExportEntry interpData)
        {
            // Search for an Interp (SeqAct_Interp) that references this InterpData
            foreach (var export in _pcc.Exports)
            {
                if (export.ClassName == "SeqAct_Interp" || export.ClassName == "BioSeqAct_PMCheckConditional")
                {
                    var interpDataProp = export.GetProperty<ObjectProperty>("InterpData");
                    if (interpDataProp != null && interpDataProp.Value == interpData.UIndex)
                    {
                        // Found the Interp; its parent sequence is what we want
                        if (_pcc.TryGetUExport(export.Parent?.UIndex ?? 0, out ExportEntry parentSeq))
                        {
                            return parentSeq;
                        }
                    }
                }
            }

            return null;
        }

        private void AddAnimSequenceToDynamicAnimSet(ExportEntry dynamicAnimSet, ExportEntry importedAnimSeq, IEntry bioAnimSetData, string setName)
        {
            // Ensure BioAnimSetData and set name are correct
            dynamicAnimSet.WriteProperty(new ObjectProperty(bioAnimSetData.UIndex, "m_pBioAnimSetData"));
            dynamicAnimSet.WriteProperty(new NameProperty(setName, "m_nmOrigSetName"));

            // Add AnimSequence to the Sequences property array
            var sequences = dynamicAnimSet.GetProperty<ArrayProperty<ObjectProperty>>("Sequences") ?? new ArrayProperty<ObjectProperty>("Sequences");

            // Check if already present
            if (!sequences.Any(s => s.Value == importedAnimSeq.UIndex))
            {
                sequences.Add(new ObjectProperty(importedAnimSeq.UIndex));
                dynamicAnimSet.WriteProperty(sequences);

                // Update the binary SequenceNamesToUnkMap to include the new sequence name
                NameReference seqName = importedAnimSeq.GetProperty<NameProperty>("SequenceName").Value;
                var dynBin = dynamicAnimSet.GetBinaryData<BioDynamicAnimSet>();
                if (!dynBin.SequenceNamesToUnkMap.Any(kvp => kvp.Key == seqName))
                {
                    dynBin.SequenceNamesToUnkMap.Add(seqName, 1);
                }
                dynamicAnimSet.WriteBinary(dynBin);
            }
        }

        private void AddGestureEntry(string animSetName, string animSeqName, bool pose, bool gesture, bool transition, bool startingPose, int? targetGestureIndex)
        {
            MEGame game = _pcc.Game;

            // Build BioGestureData properties
            PropertyCollection gestureProps = new PropertyCollection();
            gestureProps.AddOrReplaceProp(new ArrayProperty<IntProperty>("aChainedGestures"));

            // Group 1: Pose
            gestureProps.AddOrReplaceProp(new NameProperty(pose ? animSetName : "None", "nmPoseSet"));
            gestureProps.AddOrReplaceProp(new NameProperty(pose ? animSeqName : "None", "nmPoseAnim"));

            // Group 2: Gesture
            gestureProps.AddOrReplaceProp(new NameProperty(gesture ? animSetName : "None", "nmGestureSet"));
            gestureProps.AddOrReplaceProp(new NameProperty(gesture ? animSeqName : "None", "nmGestureAnim"));

            // Group 3: Transition
            gestureProps.AddOrReplaceProp(new NameProperty(transition ? animSetName : "None", "nmTransitionSet"));
            gestureProps.AddOrReplaceProp(new NameProperty(transition ? animSeqName : "None", "nmTransitionAnim"));

            // Playback defaults
            gestureProps.AddOrReplaceProp(new FloatProperty(float.TryParse(PlayRateUpDown.Value?.ToString(), out float pr) ? pr : 1f, "fPlayRate"));
            gestureProps.AddOrReplaceProp(new FloatProperty(0, "fStartOffset"));
            gestureProps.AddOrReplaceProp(new FloatProperty(0, "fEndOffset"));
            gestureProps.AddOrReplaceProp(new FloatProperty(0.1f, "fStartBlendDuration"));
            gestureProps.AddOrReplaceProp(new FloatProperty(0.1f, "fEndBlendDuration"));
            gestureProps.AddOrReplaceProp(new FloatProperty(1, "fWeight"));
            gestureProps.AddOrReplaceProp(new FloatProperty(0, "fTransBlendTime"));
            gestureProps.AddOrReplaceProp(new BoolProperty(false, "bInvalidData"));
            gestureProps.AddOrReplaceProp(new BoolProperty(CbOneShotAnim.IsChecked == true, "bOneShotAnim"));
            gestureProps.AddOrReplaceProp(new BoolProperty(false, "bChainToPrevious"));
            gestureProps.AddOrReplaceProp(new BoolProperty(CbPlayUntilNext.IsChecked == true, "bPlayUntilNext"));
            gestureProps.AddOrReplaceProp(new BoolProperty(false, "bTerminateAllGestures"));
            gestureProps.AddOrReplaceProp(new BoolProperty(true, "bUseDynAnimSets"));
            gestureProps.AddOrReplaceProp(new BoolProperty(CbSnapToPose.IsChecked == true, "bSnapToPose"));

            if (game >= MEGame.ME3)
            {
                gestureProps.AddOrReplaceProp(new EnumProperty("None", "EBioValidPoseGroups", game, "ePoseFilter"));
                gestureProps.AddOrReplaceProp(new EnumProperty("None", "EBioGestureValidPoses", game, "ePose"));
                gestureProps.AddOrReplaceProp(new EnumProperty("None", "EBioGestureGroups", game, "eGestureFiler"));
                gestureProps.AddOrReplaceProp(new EnumProperty("None", "EBioGestureValidGestures", game, "eGesture"));
            }

            var gestureStruct = new StructProperty("BioGestureData", gestureProps, "BioGestureData");

            // Build BioTrackKey properties
            PropertyCollection keyProps = new PropertyCollection();
            keyProps.AddOrReplaceProp(new NameProperty("None", "KeyName"));
            keyProps.AddOrReplaceProp(new FloatProperty(float.TryParse(KeyTimeUpDown.Value?.ToString(), out float kt) ? kt : 0f, "fTime"));

            var trackKeyStruct = new StructProperty("BioTrackKey", keyProps, "BioTrackKey");

            // Add to m_aGestures
            var gestures = _gestureTrackExport.GetProperty<ArrayProperty<StructProperty>>("m_aGestures") ?? new ArrayProperty<StructProperty>("m_aGestures");
            if (targetGestureIndex is int gestureIndex && gestureIndex >= 0 && gestureIndex < gestures.Count)
            {
                gestures[gestureIndex] = gestureStruct;
            }
            else
            {
                gestures.Add(gestureStruct);
            }
            _gestureTrackExport.WriteProperty(gestures);

            // Add to m_aTrackKeys
            var trackKeys = _gestureTrackExport.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys") ?? new ArrayProperty<StructProperty>("m_aTrackKeys");
            if (targetGestureIndex is int trackKeyIndex && trackKeyIndex >= 0 && trackKeyIndex < trackKeys.Count)
            {
                trackKeys[trackKeyIndex] = trackKeyStruct;
            }
            else
            {
                trackKeys.Add(trackKeyStruct);
            }
            _gestureTrackExport.WriteProperty(trackKeys);

            // Group 4: Starting Pose (top-level properties on the track, not per-gesture)
            if (startingPose)
            {
                _gestureTrackExport.WriteProperty(new NameProperty(animSetName, "nmStartingPoseSet"));
                _gestureTrackExport.WriteProperty(new NameProperty(animSeqName, "nmStartingPoseAnim"));
            }
        }

        #endregion

        #region Gesture Editor

        private void GestureListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Handled by SelectedGestureEntry binding
        }

        private static string GetAnimationDetailsText(AnimationRecord animation)
        {
            if (animation == null) return "No animation selected.";

            return $"Sequence: {animation.AnimSequence}\n" +
                   $"Name: {animation.SeqName}\n" +
                   $"AnimData: {animation.AnimData}\n" +
                   $"Length: {animation.Length:F2}s\n" +
                   $"Frames: {animation.Frames}\n" +
                   $"Compression: {animation.Compression}";
        }

        /// <summary>
        /// Opens a picker dialog for the user to select an animation from the database,
        /// imports it, and returns the (setName, seqName). Returns null if cancelled.
        /// Includes an animation preview viewport.
        /// </summary>
        private (string setName, string seqName)? BrowseAndImportAnimation()
        {
            if (_allAnimations == null || _allAnimations.Count == 0)
            {
                MessageBox.Show("No animations available. Please ensure the asset database is loaded.", "No Animations", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var pickerWindow = new Window
            {
                Title = "Select Animation",
                Width = 1250,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            CustomWindowChrome.ApplyCustomChrome(pickerWindow);

            AnimationRecord selectedAnim = null;
            AnimationSourceOption selectedSource = null;
            IMEPackage pickerAnimPcc = null;

            // Preview control
            var pickerPreview = new AnimationPreviewControl();

            // Mesh selector
            var meshCombo = new ComboBox { DisplayMemberPath = "MeshName", Margin = new Thickness(5, 0, 5, 5) };
            if (_skeletonMeshes != null)
            {
                meshCombo.ItemsSource = _skeletonMeshes;
                meshCombo.SelectionChanged += (s, args) =>
                {
                    LoadPreviewMesh(meshCombo.SelectedItem as MeshRecord, pickerPreview);
                };
                // Set to the same mesh as the main preview if available
                if (PreviewMeshComboBox.SelectedIndex >= 0)
                    meshCombo.SelectedIndex = PreviewMeshComboBox.SelectedIndex;
            }

            // Left panel: preview
            var previewPanel = new DockPanel { MinWidth = 250 };
            var previewHeader = new TextBlock { Text = "Animation Preview", FontWeight = FontWeights.Bold, Margin = new Thickness(5, 0, 5, 4) };
            DockPanel.SetDock(previewHeader, Dock.Top);
            previewPanel.Children.Add(previewHeader);
            DockPanel.SetDock(meshCombo, Dock.Top);
            previewPanel.Children.Add(meshCombo);
            previewPanel.Children.Add(pickerPreview);

            // Search box
            var searchBox = new TextBox { Margin = new Thickness(5) };
            var sourceGameCombo = new ComboBox
            {
                Width = 80,
                Margin = new Thickness(0, 0, 5, 0),
                ItemsSource = AvailableSourceGames,
                SelectedItem = _selectedAnimSourceGame
            };
            var sourcePackageCombo = new ComboBox
            {
                Margin = new Thickness(5, 0, 5, 4),
                DisplayMemberPath = "DisplayName"
            };
            var searchPanel = new DockPanel { Margin = new Thickness(5, 0, 5, 4) };
            var sourceGameLabel = new TextBlock { Text = "Source Game: ", VerticalAlignment = VerticalAlignment.Center };
            var searchLabel = new TextBlock { Text = "  Search: ", VerticalAlignment = VerticalAlignment.Center };
            var sourcePackagePanel = new DockPanel { Margin = new Thickness(5, 0, 5, 4) };
            var sourcePackageLabel = new TextBlock { Text = "Source Package: ", VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(sourceGameLabel, Dock.Left);
            DockPanel.SetDock(sourceGameCombo, Dock.Left);
            DockPanel.SetDock(searchLabel, Dock.Left);
            DockPanel.SetDock(sourcePackageLabel, Dock.Left);
            searchBox.Margin = new Thickness(0);
            searchPanel.Children.Add(sourceGameLabel);
            searchPanel.Children.Add(sourceGameCombo);
            searchPanel.Children.Add(searchLabel);
            searchPanel.Children.Add(searchBox);
            sourcePackagePanel.Children.Add(sourcePackageLabel);
            sourcePackagePanel.Children.Add(sourcePackageCombo);
            var statusText = new TextBlock
            {
                Margin = new Thickness(5, 0, 5, 4),
                Opacity = 0.6,
                Text = $"{_allAnimations.Count} / {_allAnimations.Count} animations shown."
            };
            var detailsText = new TextBlock
            {
                Text = GetAnimationDetailsText(null),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };

            // Animation list
            var listBox = new ListBox
            {
                Margin = new Thickness(5),
                DisplayMemberPath = "AnimSequence"
            };
            listBox.ItemsSource = _allAnimations;

            void ApplyPickerFilter()
            {
                string filter = searchBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(filter))
                {
                    listBox.ItemsSource = _allAnimations;
                    statusText.Text = $"{_allAnimations.Count} / {_allAnimations.Count} animations shown.";
                    return;
                }

                var filteredAnimations = _allAnimations.Where(a =>
                    a.AnimSequence.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (a.SeqName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (a.AnimData?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                listBox.ItemsSource = filteredAnimations;
                statusText.Text = $"{filteredAnimations.Count} / {_allAnimations.Count} animations shown.";
            }

            void RefreshPickerSourceOptions(AnimationRecord anim)
            {
                List<AnimationSourceOption> options = GetAnimationSourceOptions(anim);
                sourcePackageCombo.ItemsSource = options;

                if (options.Count == 0)
                {
                    selectedSource = null;
                    sourcePackageCombo.SelectedItem = null;
                    return;
                }

                AnimationSourceOption matchingOption = selectedSource != null
                    ? options.FirstOrDefault(option => option.UIndex == selectedSource.UIndex && option.FilePath.CaseInsensitiveEquals(selectedSource.FilePath))
                    : null;
                selectedSource = matchingOption ?? options[0];
                sourcePackageCombo.SelectedItem = selectedSource;
            }

            void RefreshPickerAnimationBrowser()
            {
                selectedAnim = null;
                selectedSource = null;
                listBox.SelectedItem = null;
                detailsText.Text = GetAnimationDetailsText(null);
                pickerPreview.ClearAnimation();
                sourcePackageCombo.ItemsSource = null;
                sourcePackageCombo.SelectedItem = null;
                meshCombo.ItemsSource = _skeletonMeshes;
                meshCombo.SelectedItem = PreviewMeshComboBox.SelectedItem as MeshRecord;
                if (meshCombo.SelectedItem is MeshRecord selectedMesh)
                {
                    pickerWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        LoadPreviewMesh(selectedMesh, pickerPreview);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                ApplyPickerFilter();
            }

            listBox.SelectionChanged += (s, args) =>
            {
                var anim = listBox.SelectedItem as AnimationRecord;
                selectedAnim = anim;
                RefreshPickerSourceOptions(anim);
                detailsText.Text = GetAnimationDetailsText(anim);
                if (anim != null && anim.Usages.Any())
                {
                    if (TryResolveAnimationSource(anim, out string fp, out int uIdx, selectedSource))
                    {
                        pickerAnimPcc?.Dispose();
                        pickerAnimPcc = MEPackageHandler.OpenMEPackage(fp);
                        if (pickerAnimPcc.IsUExport(uIdx))
                        {
                            pickerPreview.LoadAnimSequence(pickerAnimPcc.GetUExport(uIdx));
                            pickerPreview.Play();
                        }
                    }
                }
                else
                {
                    pickerPreview.ClearAnimation();
                }
            };
            sourcePackageCombo.SelectionChanged += (s, args) =>
            {
                selectedSource = sourcePackageCombo.SelectedItem as AnimationSourceOption;
                if (selectedAnim != null && TryResolveAnimationSource(selectedAnim, out string fp, out int uIdx, selectedSource))
                {
                    pickerAnimPcc?.Dispose();
                    pickerAnimPcc = MEPackageHandler.OpenMEPackage(fp);
                    if (pickerAnimPcc.IsUExport(uIdx))
                    {
                        pickerPreview.LoadAnimSequence(pickerAnimPcc.GetUExport(uIdx));
                        pickerPreview.Play();
                        return;
                    }
                }

                pickerPreview.ClearAnimation();
            };
            listBox.MouseDoubleClick += (s, args) =>
            {
                selectedAnim = listBox.SelectedItem as AnimationRecord;
                if (selectedAnim != null) pickerWindow.DialogResult = true;
            };
            searchBox.TextChanged += (s, args) =>
            {
                ApplyPickerFilter();
            };
            sourceGameCombo.SelectionChanged += async (s, args) =>
            {
                if (sourceGameCombo.SelectedItem is MEGame game && game != _selectedAnimSourceGame)
                {
                    _selectedAnimSourceGame = game;
                    SourceGameComboBox.SelectedItem = game;
                    await LoadDatabaseAsync();
                    RefreshPickerAnimationBrowser();
                }
            };

            var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(5), IsDefault = true };
            okButton.Click += (s, args) =>
            {
                selectedAnim = listBox.SelectedItem as AnimationRecord;
                if (selectedAnim != null) pickerWindow.DialogResult = true;
            };
            var cancelButton = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(5), IsCancel = true };

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            // Middle panel: search + list + buttons
            var listPanel = new DockPanel();
            var listHeader = new TextBlock { Text = "Animations from Asset Database", FontWeight = FontWeights.Bold, Margin = new Thickness(5, 0, 5, 4) };
            DockPanel.SetDock(listHeader, Dock.Top);
            DockPanel.SetDock(searchPanel, Dock.Top);
            DockPanel.SetDock(sourcePackagePanel, Dock.Top);
            DockPanel.SetDock(statusText, Dock.Top);
            listPanel.Children.Add(listHeader);
            listPanel.Children.Add(searchPanel);
            listPanel.Children.Add(sourcePackagePanel);
            listPanel.Children.Add(statusText);
            listPanel.Children.Add(listBox);

            // Right panel: selected animation details
            var detailsPanel = new DockPanel { Margin = new Thickness(5, 0, 0, 0), MinWidth = 250 };
            var detailsHeader = new TextBlock { Text = "Selected Animation Details", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(detailsHeader, Dock.Top);
            var detailsGroup = new GroupBox { Header = "Animation Details", Padding = new Thickness(8), Content = detailsText };
            DockPanel.SetDock(buttonPanel, Dock.Bottom);
            detailsPanel.Children.Add(detailsHeader);
            detailsPanel.Children.Add(buttonPanel);
            detailsPanel.Children.Add(detailsGroup);

            // Main layout: preview | list | details
            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
            var splitter2 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 200 });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 250 });
            Grid.SetColumn(previewPanel, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(listPanel, 2);
            Grid.SetColumn(splitter2, 3);
            Grid.SetColumn(detailsPanel, 4);
            mainGrid.Children.Add(previewPanel);
            mainGrid.Children.Add(splitter);
            mainGrid.Children.Add(listPanel);
            mainGrid.Children.Add(splitter2);
            mainGrid.Children.Add(detailsPanel);

            pickerWindow.Content = mainGrid;
            pickerWindow.Closing += (s, args) =>
            {
                pickerPreview.Dispose();
                pickerAnimPcc?.Dispose();
            };

            pickerWindow.Loaded += (s, args) =>
            {
                RefreshPickerAnimationBrowser();
            };

            if (pickerWindow.ShowDialog() != true || selectedAnim == null)
                return null;

            return ImportAnimationFromDatabase(selectedAnim, selectedSource);
        }

        private void BrowsePoseAnim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = BrowseAndImportAnimation();
                if (result == null) return;
                EditPoseSet = result.Value.setName;
                EditPoseAnim = result.Value.seqName;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing animation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearPoseAnim_Click(object sender, RoutedEventArgs e)
        {
            EditPoseSet = "None";
            EditPoseAnim = "None";
        }

        private void BrowseGestureAnim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = BrowseAndImportAnimation();
                if (result == null) return;
                EditGestureSet = result.Value.setName;
                EditGestureAnim = result.Value.seqName;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing animation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearGestureAnim_Click(object sender, RoutedEventArgs e)
        {
            EditGestureSet = "None";
            EditGestureAnim = "None";
        }

        private void BrowseTransitionAnim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = BrowseAndImportAnimation();
                if (result == null) return;
                EditTransitionSet = result.Value.setName;
                EditTransitionAnim = result.Value.seqName;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing animation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearTransitionAnim_Click(object sender, RoutedEventArgs e)
        {
            EditTransitionSet = "None";
            EditTransitionAnim = "None";
        }

        private void BrowseStartingPoseAnim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = BrowseAndImportAnimation();
                if (result == null) return;
                EditStartingPoseSet = result.Value.setName;
                EditStartingPoseAnim = result.Value.seqName;
                SaveStartingPose();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing animation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearStartingPoseAnim_Click(object sender, RoutedEventArgs e)
        {
            EditStartingPoseSet = "None";
            EditStartingPoseAnim = "None";
            SaveStartingPose();
        }

        private void SaveStartingPose()
        {
            _gestureTrackExport.WriteProperty(new NameProperty(EditStartingPoseSet, "nmStartingPoseSet"));
            _gestureTrackExport.WriteProperty(new NameProperty(EditStartingPoseAnim, "nmStartingPoseAnim"));
            StatusMessage = "Starting pose saved.";
        }

        private void SaveGesture_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGestureEntry?.GestureStruct == null)
            {
                MessageBox.Show("No gesture selected.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                int idx = SelectedGestureEntry.Index;
                var gestures = _gestureTrackExport.GetProperty<ArrayProperty<StructProperty>>("m_aGestures");
                if (gestures == null || idx >= gestures.Count) return;

                var g = gestures[idx];
                g.Properties.AddOrReplaceProp(new NameProperty(EditPoseSet, "nmPoseSet"));
                g.Properties.AddOrReplaceProp(new NameProperty(EditPoseAnim, "nmPoseAnim"));
                g.Properties.AddOrReplaceProp(new NameProperty(EditGestureSet, "nmGestureSet"));
                g.Properties.AddOrReplaceProp(new NameProperty(EditGestureAnim, "nmGestureAnim"));
                g.Properties.AddOrReplaceProp(new NameProperty(EditTransitionSet, "nmTransitionSet"));
                g.Properties.AddOrReplaceProp(new NameProperty(EditTransitionAnim, "nmTransitionAnim"));
                g.Properties.AddOrReplaceProp(new FloatProperty(float.TryParse(EditPlayRate, out float pr) ? pr : 1f, "fPlayRate"));
                g.Properties.AddOrReplaceProp(new FloatProperty(float.TryParse(EditStartOffset, out float so) ? so : 0f, "fStartOffset"));
                g.Properties.AddOrReplaceProp(new FloatProperty(float.TryParse(EditEndOffset, out float eo) ? eo : 0f, "fEndOffset"));
                g.Properties.AddOrReplaceProp(new FloatProperty(float.TryParse(EditStartBlendDuration, out float sbd) ? sbd : 0.1f, "fStartBlendDuration"));
                g.Properties.AddOrReplaceProp(new FloatProperty(float.TryParse(EditEndBlendDuration, out float ebd) ? ebd : 0.1f, "fEndBlendDuration"));
                g.Properties.AddOrReplaceProp(new FloatProperty(float.TryParse(EditWeight, out float w) ? w : 1f, "fWeight"));
                g.Properties.AddOrReplaceProp(new BoolProperty(EditOneShotAnim, "bOneShotAnim"));
                g.Properties.AddOrReplaceProp(new BoolProperty(EditSnapToPose, "bSnapToPose"));
                g.Properties.AddOrReplaceProp(new BoolProperty(EditPlayUntilNext, "bPlayUntilNext"));
                g.Properties.AddOrReplaceProp(new BoolProperty(EditUseDynAnimSets, "bUseDynAnimSets"));

                // Only write m_aGestures — never touch m_aTrackKeys from the edit gesture tab
                _gestureTrackExport.WriteProperty(gestures);
                LoadExistingGestures();
                StatusMessage = $"Gesture {idx} saved.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving gesture: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveGesture_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGestureEntry == null)
            {
                MessageBox.Show("No gesture selected.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"Remove Gesture {SelectedGestureEntry.Index}?", "Confirm Remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                int idx = SelectedGestureEntry.Index;

                var gestures = _gestureTrackExport.GetProperty<ArrayProperty<StructProperty>>("m_aGestures");
                var trackKeys = _gestureTrackExport.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys");

                if (gestures != null && idx < gestures.Count)
                {
                    gestures.RemoveAt(idx);
                    _gestureTrackExport.WriteProperty(gestures);
                }

                if (trackKeys != null && idx < trackKeys.Count)
                {
                    trackKeys.RemoveAt(idx);
                    _gestureTrackExport.WriteProperty(trackKeys);
                }

                LoadExistingGestures();
                StatusMessage = $"Gesture {idx} removed.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error removing gesture: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Gesture Track Import

        private void ImportGestureTrack_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedGestureTrack == null || SelectedGestureTrackSource == null)
            {
                MessageBox.Show("Please select a gesture track and source package first.", "No Gesture Track Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                $"Replace '{_gestureTrackExport.ObjectName.Instanced}' with all data and references from '{SelectedGestureTrack.TrackName}'?\n\nThe destination actor lookup properties will be preserved.",
                "Replace Gesture Track",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                ReplaceGestureTrackFromDatabase(SelectedGestureTrackSource);
                LoadExistingGestures();
                StatusMessage = $"Successfully replaced the gesture track with '{SelectedGestureTrack.TrackName}'.";
                MessageBox.Show(
                    $"Gesture track '{SelectedGestureTrack.TrackName}' was imported with its links and referenced exports.",
                    "Import Successful",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Gesture track import failed: {ex.Message}";
                MessageBox.Show($"Error importing gesture track: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReplaceGestureTrackFromDatabase(GestureTrackSourceOption sourceOption)
        {
            if (!File.Exists(sourceOption.FilePath))
            {
                throw new FileNotFoundException("The selected source package could not be found.", sourceOption.FilePath);
            }

            if (_pcc.FilePath.CaseInsensitiveEquals(sourceOption.FilePath) && _gestureTrackExport.UIndex == sourceOption.UIndex)
            {
                throw new InvalidOperationException("The selected source is the destination gesture track.");
            }

            using IMEPackage sourcePackage = MEPackageHandler.OpenMEPackage(sourceOption.FilePath);
            if (!sourcePackage.IsUExport(sourceOption.UIndex))
            {
                throw new InvalidOperationException($"Export {sourceOption.UIndex} does not exist in the selected source package.");
            }

            ExportEntry sourceTrack = sourcePackage.GetUExport(sourceOption.UIndex);
            if (!sourceTrack.IsA("BioEvtSysTrackGesture"))
            {
                throw new InvalidOperationException($"Export {sourceOption.UIndex} is not a BioEvtSysTrackGesture.");
            }

            Property[] protectedProperties =
            [
                _gestureTrackExport.GetProperty<EnumProperty>("m_eFindActorMode")?.DeepClone(),
                _gestureTrackExport.GetProperty<NameProperty>("m_nmFindActor")?.DeepClone(),
            ];

            var relinkerErrors = new List<string>();
            var relinkerOptions = new RelinkerOptionsPackage
            {
                ImportExportDependencies = true,
                PortImportsMemorySafe = true,
                PortExportsAsImportsWhenPossible = true,
                ErrorOccurredCallback = relinkerErrors.Add,
                RelinkPropertyMutator = (export, properties) =>
                {
                    if (!ReferenceEquals(export, sourceTrack))
                    {
                        return;
                    }

                    foreach (Property property in protectedProperties)
                    {
                        if (property != null)
                        {
                            properties.AddOrReplaceProp(property.DeepClone());
                        }
                    }
                },
            };

            List<EntryStringPair> relinkReport = EntryImporter.ImportAndRelinkEntries(
                EntryImporter.PortingOption.ReplaceSingularWithRelink,
                sourceTrack,
                _pcc,
                _gestureTrackExport,
                true,
                relinkerOptions,
                out IEntry replacedEntry);

            if (!ReferenceEquals(replacedEntry, _gestureTrackExport))
            {
                throw new InvalidOperationException("The relinker did not replace the destination gesture track in place.");
            }

            relinkReport.AddRange(MatineeHelper.CloneGestureTrackAnimSets(sourceTrack, _gestureTrackExport, relinkerOptions));
            _gestureTrackExport.WriteProperty(new BoolProperty(true, "m_bUseDynamicAnimSets"));

            foreach (Property property in protectedProperties)
            {
                if (property != null)
                {
                    _gestureTrackExport.WriteProperty(property.DeepClone());
                }
            }

            if (relinkerErrors.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, relinkerErrors.Distinct()));
            }

            if (relinkReport.Count > 0)
            {
                StatusMessage = $"Gesture track imported with {relinkReport.Count} relinker warning(s).";
            }
        }

        #endregion

        #region Ambient Performances

        private void LoadCurrentAmbPerfInfo()
        {
            var perfProp = _gestureTrackExport.GetProperty<ObjectProperty>("m_pPerfGameData");
            if (perfProp != null && _pcc.TryGetEntry(perfProp.Value, out IEntry perfEntry))
            {
                CurrentAmbPerfInfo = $"Current: {perfEntry.ObjectName.Instanced} (#{perfEntry.UIndex})";
            }
            else
            {
                CurrentAmbPerfInfo = "Current: None";
            }
        }

        private void AmbPerfSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = AmbPerfSearchBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(filter))
            {
                FilteredAmbPerfs.ReplaceAll(_allAmbPerfs);
            }
            else
            {
                FilteredAmbPerfs.ReplaceAll(_allAmbPerfs.Where(a =>
                    a.AnimSequence.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (a.SeqName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)));
            }
        }

        private void AmbPerfListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedAmbPerf != null)
            {
                LoadAmbPerfPreview(SelectedAmbPerf);
            }
        }

        private void AmbPerfMesh_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadPreviewMesh(AmbPerfMeshComboBox.SelectedItem as MeshRecord, AmbPerfPreviewControl);
        }

        private void LoadAmbPerfPreview(AnimationRecord anim)
        {
            if (anim == null || !anim.Usages.Any())
            {
                AmbPerfPreviewControl.ClearAnimation();
                return;
            }

            // Ambient performances are SFXAmbPerfGameData, not AnimSequences.
            // Find the first AnimSequence child to preview.
            if (!TryResolveAmbPerfSource(anim, out string filePath, out int ambientPerfUIndex, SelectedAmbPerfSource))
            {
                AmbPerfPreviewControl.ClearAnimation();
                return;
            }

            AmbPerfPreviewControl.ClearAnimation();
            _ambPerfPreviewPackageCache.ReleasePackages();
            _ambPerfPreviewPcc?.Dispose();
            _ambPerfPreviewPcc = MEPackageHandler.OpenMEPackage(filePath);

            if (!_ambPerfPreviewPcc.IsUExport(ambientPerfUIndex))
            {
                AmbPerfPreviewControl.ClearAnimation();
                return;
            }

            ExportEntry ambPerfExport = _ambPerfPreviewPcc.GetUExport(ambientPerfUIndex);

            // Find an AnimSequence in either local or imported dynamic animation sets.
            ExportEntry animSeqToPreview = null;
            var dynamicAnimSets = new HashSet<ExportEntry>();
            var animSetReferences = ambPerfExport.GetProperty<ArrayProperty<ObjectProperty>>("m_aAnimsets");
            if (animSetReferences != null)
            {
                foreach (ObjectProperty animSetReference in animSetReferences)
                {
                    if (animSetReference.ResolveToExport(_ambPerfPreviewPcc, _ambPerfPreviewPackageCache) is { ClassName: "BioDynamicAnimSet" } animSet)
                    {
                        dynamicAnimSets.Add(animSet);
                    }
                }
            }

            foreach (var child in ambPerfExport.GetChildren())
            {
                if (child is ExportEntry childExp && childExp.ClassName == "BioDynamicAnimSet")
                {
                    dynamicAnimSets.Add(childExp);
                }
            }

            foreach (ExportEntry dynamicAnimSet in dynamicAnimSets)
            {
                var sequences = dynamicAnimSet.GetProperty<ArrayProperty<ObjectProperty>>("Sequences");
                if (sequences == null)
                {
                    continue;
                }

                foreach (ObjectProperty sequenceReference in sequences)
                {
                    if (sequenceReference.ResolveToExport(dynamicAnimSet.FileRef, _ambPerfPreviewPackageCache) is { ClassName: "AnimSequence" } sequence)
                    {
                        animSeqToPreview = sequence;
                        break;
                    }
                }

                if (animSeqToPreview != null)
                {
                    break;
                }
            }

            if (animSeqToPreview != null)
            {
                AmbPerfPreviewControl.LoadAnimSequence(animSeqToPreview);
                AmbPerfPreviewControl.Play();
            }
            else
            {
                AmbPerfPreviewControl.ClearAnimation();
            }
        }

        private void ImportAmbPerf_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAmbPerf == null)
            {
                MessageBox.Show("Please select an ambient performance first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                ImportAmbientPerformance(SelectedAmbPerf);
                LoadCurrentAmbPerfInfo();
                StatusMessage = $"Successfully imported ambient performance '{SelectedAmbPerf.AnimSequence}'.";
                MessageBox.Show($"Ambient performance '{SelectedAmbPerf.AnimSequence}' has been imported and linked to m_pPerfGameData.", "Import Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing ambient performance: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Imports an SFXAmbPerfGameData from the source package and sets m_pPerfGameData on the gesture module.
        /// </summary>
        private void ImportAmbientPerformance(AnimationRecord ambPerf)
        {
            if (ambPerf?.Usages == null || !ambPerf.Usages.Any())
                throw new Exception("No usages found for this ambient performance.");

            if (!TryResolveAmbPerfSource(ambPerf, out string sourceFilePath, out int sourceUIndex, SelectedAmbPerfSource))
                throw new Exception("Could not resolve the ambient performance's source file.");

            using IMEPackage sourcePackage = MEPackageHandler.OpenMEPackage(sourceFilePath);
            ExportEntry sourceExport = sourcePackage.GetUExport(sourceUIndex);

            // Preserve the original package hierarchy (e.g. BIOG_GesturesConfig.WalkToThinkingFrustrated)
            // rather than parenting under SFXModule_Gestures
            var rop = new RelinkerOptionsPackage
            {
                ImportExportDependencies = true,
                PortImportsMemorySafe = true,
                PortExportsAsImportsWhenPossible = true,
            };
            IEntry parent = EntryImporter.GetOrAddCrossImportOrPackage(sourceExport.ParentFullPath, sourcePackage, _pcc, rop);
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneTreeAsChild,
                sourceExport, _pcc, parent, true, rop, out IEntry importedEntry);

            // Set m_pPerfGameData on the gesture module
            _gestureTrackExport.WriteProperty(new ObjectProperty(importedEntry.UIndex, "m_pPerfGameData"));
        }

        private void RefreshSelectedAmbPerfSources()
        {
            List<AnimationSourceOption> options = GetAmbientPerformanceSourceOptions(SelectedAmbPerf);
            AvailableAmbPerfSources.ReplaceAll(options);

            if (options.Count == 0)
            {
                SelectedAmbPerfSource = null;
                return;
            }

            if (SelectedAmbPerfSource != null)
            {
                AnimationSourceOption matchingOption = options.FirstOrDefault(option =>
                    option.UIndex == SelectedAmbPerfSource.UIndex &&
                    option.FilePath.CaseInsensitiveEquals(SelectedAmbPerfSource.FilePath));
                if (matchingOption != null)
                {
                    SelectedAmbPerfSource = matchingOption;
                    return;
                }
            }

            SelectedAmbPerfSource = options[0];
        }

        private List<AnimationSourceOption> GetAmbientPerformanceSourceOptions(AnimationRecord ambPerf)
        {
            if (ambPerf?.Usages == null || !ambPerf.Usages.Any())
            {
                return [];
            }

            var options = new List<AnimationSourceOption>();
            foreach (AnimUsage usage in ambPerf.Usages)
            {
                if (!TryGetFilePath(usage.FileKey, out string filePath, out string fileName, out string contentDir))
                {
                    continue;
                }

                using var testPkg = MEPackageHandler.OpenMEPackage(filePath);
                if (!testPkg.IsUExport(usage.UIndex) || testPkg.GetUExport(usage.UIndex).ClassName != "SFXAmbPerfGameData")
                {
                    continue;
                }

                if (options.Any(option => option.UIndex == usage.UIndex && option.FilePath.CaseInsensitiveEquals(filePath)))
                {
                    continue;
                }

                options.Add(new AnimationSourceOption
                {
                    DisplayName = $"{fileName} ({contentDir})",
                    FilePath = filePath,
                    UIndex = usage.UIndex,
                });
            }

            return options;
        }

        private bool TryResolveAmbPerfSource(AnimationRecord ambPerf, out string filePath, out int uIndex, AnimationSourceOption preferredSource = null)
        {
            filePath = null;
            uIndex = 0;
            if (ambPerf?.Usages == null || !ambPerf.Usages.Any())
            {
                return false;
            }

            if (preferredSource != null && File.Exists(preferredSource.FilePath))
            {
                using var preferredPkg = MEPackageHandler.OpenMEPackage(preferredSource.FilePath);
                if (preferredPkg.IsUExport(preferredSource.UIndex) && preferredPkg.GetUExport(preferredSource.UIndex).ClassName == "SFXAmbPerfGameData")
                {
                    filePath = preferredSource.FilePath;
                    uIndex = preferredSource.UIndex;
                    return true;
                }
            }

            foreach (AnimUsage usage in ambPerf.Usages)
            {
                if (!TryGetFilePath(usage.FileKey, out string candidateFilePath, out _, out _))
                {
                    continue;
                }

                using var testPkg = MEPackageHandler.OpenMEPackage(candidateFilePath);
                if (testPkg.IsUExport(usage.UIndex) && testPkg.GetUExport(usage.UIndex).ClassName == "SFXAmbPerfGameData")
                {
                    filePath = candidateFilePath;
                    uIndex = usage.UIndex;
                    return true;
                }
            }

            return false;
        }

        #endregion

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            AnimPreviewControl?.Dispose();
            AmbPerfPreviewControl?.Dispose();
            GestureTrackPreviewControl?.Dispose();
            _animPreviewPcc?.Dispose();
            _animPreviewPcc = null;
            _ambPerfPreviewPcc?.Dispose();
            _ambPerfPreviewPcc = null;
            _gestureTrackPreviewPcc?.Dispose();
            _gestureTrackPreviewPcc = null;
            _ambPerfPreviewPackageCache.Dispose();
        }
    }
}
