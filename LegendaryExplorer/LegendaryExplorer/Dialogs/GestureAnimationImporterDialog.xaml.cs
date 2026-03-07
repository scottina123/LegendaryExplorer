using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Helpers;
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
            set { _selectedAnimation = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(SelectedAnimationDetails)); }
        }

        public bool CanImport => SelectedAnimation != null;

        public string SelectedAnimationDetails
        {
            get
            {
                if (SelectedAnimation == null) return "No animation selected.";
                return $"Sequence: {SelectedAnimation.AnimSequence}\n" +
                       $"Name: {SelectedAnimation.SeqName}\n" +
                       $"AnimData: {SelectedAnimation.AnimData}\n" +
                       $"Length: {SelectedAnimation.Length:F2}s\n" +
                       $"Frames: {SelectedAnimation.Frames}\n" +
                       $"Compression: {SelectedAnimation.Compression}";
            }
        }

        public ObservableCollectionExtended<AnimationRecord> FilteredAnimations { get; } = new();
        public ObservableCollectionExtended<GestureEntryViewModel> GestureEntries { get; } = new();

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
            set { _selectedAmbPerf = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanImportAmbPerf)); }
        }

        public bool CanImportAmbPerf => SelectedAmbPerf != null;

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

        #endregion

        // File list from DB for resolving paths
        private List<(string FileName, string ContentDir)> _fileListExtended = new();

        // Animation preview state
        private IMEPackage _animPreviewPcc;
        private IMEPackage _ambPerfPreviewPcc;
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
                GbPropertyGroups.Visibility = Visibility.Collapsed;
                GbGestureKeySettings.Visibility = Visibility.Collapsed;
                EditGesturesTab.Visibility = Visibility.Collapsed;
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

            LoadExistingGestures();
            LoadDatabaseAsync();
        }

        private async void LoadDatabaseAsync()
        {
            MEGame game = _selectedAnimSourceGame;
            string dbPath = AssetDatabaseWindow.GetDBPath(game);
            if (!File.Exists(dbPath))
            {
                AnimationStatusText = $"No {game} asset database found. Please generate one in the Asset Database tool.";
                return;
            }

            AnimationStatusText = $"Loading {game} database...";

            _db = new AssetDB();
            await AssetDatabaseWindow.LoadDatabase(dbPath, game, _db, CancellationToken.None);

            if (_db.DatabaseVersion != AssetDatabaseWindow.dbCurrentBuild)
            {
                AnimationStatusText = $"{game} asset database is out of date. Please regenerate it in the Asset Database tool.";
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

        private void LoadExistingGestures()
        {
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
                LoadDatabaseAsync();
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

            if (!TryResolveAnimationSource(anim, out string filePath, out int animUIndex))
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
            if (fileKey < 0 || fileKey >= _fileListExtended.Count) return null;
            var (fileName, contentDir) = _fileListExtended[fileKey];
            string rootPath = MEDirectories.GetDefaultGamePath(_selectedAnimSourceGame);
            if (rootPath == null || !Directory.Exists(rootPath)) return null;
            return Directory.EnumerateFiles(rootPath, $"{fileName}.*", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.Contains(contentDir));
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
                    AddGestureEntry(setName, seqName, poseChecked, gestureChecked, transitionChecked, startingPoseChecked);
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
        private (string setName, string seqName) ImportAnimationFromDatabase(AnimationRecord animation)
        {
            if (!TryResolveAnimationSource(animation, out string sourceFilePath, out int animUIndex))
            {
                throw new Exception("Could not resolve the animation's source file. Make sure the game is properly configured.");
            }

            using IMEPackage sourcePackage = MEPackageHandler.OpenMEPackage(sourceFilePath);
            ExportEntry sourceAnimSeq = sourcePackage.GetUExport(animUIndex);

            IEntry parent = EntryImporter.GetOrAddCrossImportOrPackage(sourceAnimSeq.ParentFullPath, sourcePackage, _pcc, new RelinkerOptionsPackage());
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies, sourceAnimSeq, _pcc, parent, true, new RelinkerOptionsPackage(), out IEntry importedEntry);
            ExportEntry importedAnimSeq = (ExportEntry)importedEntry;

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

        private bool TryResolveAnimationSource(AnimationRecord anim, out string filePath, out int uIndex)
        {
            filePath = null;
            uIndex = 0;
            if (anim?.Usages == null || !anim.Usages.Any()) return false;

            foreach (var usage in anim.Usages)
            {
                int fileListIndex = usage.FileKey;
                uIndex = usage.UIndex;

                if (fileListIndex < 0 || fileListIndex >= _fileListExtended.Count) continue;

                var (fileName, contentDir) = _fileListExtended[fileListIndex];
                string rootPath = MEDirectories.GetDefaultGamePath(_selectedAnimSourceGame);
                if (rootPath == null || !Directory.Exists(rootPath)) continue;

                filePath = Directory.EnumerateFiles(rootPath, $"{fileName}.*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Contains(contentDir));

                if (filePath != null) return true;
            }

            return false;
        }

        /// <summary>
        /// Find an existing BioDynamicAnimSet in the target sequence, or import one from the source package.
        /// Matches by m_nmOrigSetName so that KIS_DYN_* sets with the same anim set name are reused.
        /// Never creates a BioDynamicAnimSet from scratch — always imports a real one to avoid malformed binary.
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
                throw new Exception("Could not find a BioDynamicAnimSet in the source package to import.");
            }

            // Import the BioDynamicAnimSet as a child of the SFXModule_Gestures
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
                sourceDynAnimSet, _pcc, gestureModule, true, new RelinkerOptionsPackage(), out IEntry importedDynEntry);
            ExportEntry importedDynAnimSet = (ExportEntry)importedDynEntry;

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
                throw new Exception("Could not find a BioDynamicAnimSet in the source package to import.");
            }

            // Import the BioDynamicAnimSet as a child of the SkeletalMeshComponent
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
                sourceDynAnimSet, _pcc, skelMeshComp, true, new RelinkerOptionsPackage(), out IEntry importedDynEntry);
            ExportEntry importedDynAnimSet = (ExportEntry)importedDynEntry;

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
                throw new Exception("Could not find a BioDynamicAnimSet in the source package to import.");
            }

            // Import the BioDynamicAnimSet from the source package into the sequence
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneAllDependencies,
                sourceDynAnimSet, _pcc, sequenceExport, true, new RelinkerOptionsPackage(), out IEntry importedDynEntry);
            ExportEntry importedDynAnimSet = (ExportEntry)importedDynEntry;

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

        private void AddGestureEntry(string animSetName, string animSeqName, bool pose, bool gesture, bool transition, bool startingPose)
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
            gestures.Add(gestureStruct);
            _gestureTrackExport.WriteProperty(gestures);

            // Add to m_aTrackKeys
            var trackKeys = _gestureTrackExport.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys") ?? new ArrayProperty<StructProperty>("m_aTrackKeys");
            trackKeys.Add(trackKeyStruct);
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
                Width = 900,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            CustomWindowChrome.ApplyCustomChrome(pickerWindow);

            AnimationRecord selectedAnim = null;
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
            DockPanel.SetDock(meshCombo, Dock.Top);
            previewPanel.Children.Add(meshCombo);
            previewPanel.Children.Add(pickerPreview);

            // Search box
            var searchBox = new TextBox { Margin = new Thickness(5) };

            // Animation list
            var listBox = new ListBox
            {
                Margin = new Thickness(5),
                DisplayMemberPath = "AnimSequence"
            };
            listBox.ItemsSource = _allAnimations;
            listBox.SelectionChanged += (s, args) =>
            {
                var anim = listBox.SelectedItem as AnimationRecord;
                if (anim != null && anim.Usages.Any())
                {
                    if (TryResolveAnimationSource(anim, out string fp, out int uIdx))
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
            listBox.MouseDoubleClick += (s, args) =>
            {
                selectedAnim = listBox.SelectedItem as AnimationRecord;
                if (selectedAnim != null) pickerWindow.DialogResult = true;
            };
            searchBox.TextChanged += (s, args) =>
            {
                string filter = searchBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(filter))
                    listBox.ItemsSource = _allAnimations;
                else
                    listBox.ItemsSource = _allAnimations.Where(a =>
                        a.AnimSequence.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (a.SeqName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
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

            // Right panel: search + list + buttons
            var listPanel = new DockPanel();
            DockPanel.SetDock(searchBox, Dock.Top);
            DockPanel.SetDock(buttonPanel, Dock.Bottom);
            listPanel.Children.Add(searchBox);
            listPanel.Children.Add(buttonPanel);
            listPanel.Children.Add(listBox);

            // Main layout: preview | list
            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 200 });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(previewPanel, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(listPanel, 2);
            mainGrid.Children.Add(previewPanel);
            mainGrid.Children.Add(splitter);
            mainGrid.Children.Add(listPanel);

            pickerWindow.Content = mainGrid;
            pickerWindow.Closing += (s, args) =>
            {
                pickerPreview.Dispose();
                pickerAnimPcc?.Dispose();
            };

            if (pickerWindow.ShowDialog() != true || selectedAnim == null)
                return null;

            return ImportAnimationFromDatabase(selectedAnim);
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
            var usage = anim.Usages.First();
            string filePath = GetFilePath(usage.FileKey);
            if (filePath == null)
            {
                AmbPerfPreviewControl.ClearAnimation();
                return;
            }

            _ambPerfPreviewPcc?.Dispose();
            _ambPerfPreviewPcc = MEPackageHandler.OpenMEPackage(filePath);

            if (!_ambPerfPreviewPcc.IsUExport(usage.UIndex))
            {
                AmbPerfPreviewControl.ClearAnimation();
                return;
            }

            ExportEntry ambPerfExport = _ambPerfPreviewPcc.GetUExport(usage.UIndex);

            // Find a child AnimSequence to preview
            ExportEntry animSeqToPreview = null;
            foreach (var child in ambPerfExport.GetChildren())
            {
                if (child is ExportEntry childExp && childExp.ClassName == "BioDynamicAnimSet")
                {
                    var seqs = childExp.GetProperty<ArrayProperty<ObjectProperty>>("Sequences");
                    if (seqs != null)
                    {
                        foreach (var seqRef in seqs)
                        {
                            if (_ambPerfPreviewPcc.TryGetUExport(seqRef.Value, out ExportEntry seqExp) && seqExp.ClassName == "AnimSequence")
                            {
                                animSeqToPreview = seqExp;
                                break;
                            }
                        }
                    }
                    if (animSeqToPreview != null) break;
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
            if (!ambPerf.Usages.Any())
                throw new Exception("No usages found for this ambient performance.");

            // Find the source SFXAmbPerfGameData export
            string sourceFilePath = null;
            int sourceUIndex = 0;
            foreach (var usage in ambPerf.Usages)
            {
                string filePath = GetFilePath(usage.FileKey);
                if (filePath == null) continue;

                using var testPkg = MEPackageHandler.OpenMEPackage(filePath);
                if (testPkg.IsUExport(usage.UIndex) && testPkg.GetUExport(usage.UIndex).ClassName == "SFXAmbPerfGameData")
                {
                    sourceFilePath = filePath;
                    sourceUIndex = usage.UIndex;
                    break;
                }
            }

            if (sourceFilePath == null)
                throw new Exception("Could not resolve the ambient performance's source file.");

            using IMEPackage sourcePackage = MEPackageHandler.OpenMEPackage(sourceFilePath);
            ExportEntry sourceExport = sourcePackage.GetUExport(sourceUIndex);

            // Preserve the original package hierarchy (e.g. BIOG_GesturesConfig.WalkToThinkingFrustrated)
            // rather than parenting under SFXModule_Gestures
            IEntry parent = EntryImporter.GetOrAddCrossImportOrPackage(sourceExport.ParentFullPath, sourcePackage, _pcc, new RelinkerOptionsPackage());

            var rop = new RelinkerOptionsPackage();
            EntryImporter.ImportAndRelinkEntries(EntryImporter.PortingOption.CloneTreeAsChild,
                sourceExport, _pcc, parent, true, rop, out IEntry importedEntry);

            // Set m_pPerfGameData on the gesture module
            _gestureTrackExport.WriteProperty(new ObjectProperty(importedEntry.UIndex, "m_pPerfGameData"));
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
            _animPreviewPcc?.Dispose();
            _animPreviewPcc = null;
            _ambPerfPreviewPcc?.Dispose();
            _ambPerfPreviewPcc = null;
        }
    }
}
