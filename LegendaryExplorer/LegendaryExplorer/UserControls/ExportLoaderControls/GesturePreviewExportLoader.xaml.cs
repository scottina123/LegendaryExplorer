using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class GesturePreviewExportLoader : ExportLoaderControl
{
    public sealed class GestureAnimationItem
    {
        public int? GestureIndex { get; init; }
        public float Time { get; init; }
        public float BlendDuration { get; init; }
        public int SlotOrder { get; init; }
        public string SlotName { get; init; }
        public NameReference SetName { get; init; }
        public NameReference AnimationName { get; init; }
        public ExportEntry AnimationExport { get; set; }
        public string DisplayName => GestureIndex is int index ? $"Gesture {index}: {SlotName}" : SlotName;
        public string TimelineText => GestureIndex is int ? $"Track time: {Time:F2}s" : "Track start";
        public string ReferenceText => $"{SetName.Instanced}.{AnimationName.Instanced}";
        public string ResolutionText => AnimationExport == null ? "Animation not found in shared animation sets" : $"Export {AnimationExport.UIndex}";
    }

    private readonly List<(string FileName, string ContentDir)> _databaseFiles = [];
    private CancellationTokenSource _databaseLoadCancellationTokenSource;
    private List<GestureAnimationItem> _animations = [];
    private Queue<GestureAnimationItem> _playbackQueue;
    private GestureAnimationItem _selectedAnimation;
    private string _statusText = "Load a preview mesh to play the gesture track.";

    public GestureAnimationItem SelectedAnimation
    {
        get => _selectedAnimation;
        set => SetProperty(ref _selectedAnimation, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public GesturePreviewExportLoader() : base("Gesture Preview")
    {
        DataContext = this;
        InitializeComponent();
        AnimPreviewControl.AnimationCompleted += AnimPreviewControl_AnimationCompleted;
    }

    public override bool CanParse(ExportEntry exportEntry) =>
        !exportEntry.IsDefaultObject && exportEntry.IsA("BioEvtSysTrackGesture");

    public override void LoadExport(ExportEntry exportEntry)
    {
        UnloadExport();
        CurrentLoadedExport = exportEntry;
        _animations = BuildAnimationTimeline(exportEntry);
        AnimationListBox.ItemsSource = _animations;
        SelectedAnimation = _animations.FirstOrDefault();
        StatusText = _animations.Count == 0
            ? "This gesture track does not reference any animations."
            : $"Loaded {_animations.Count} animation slots in chronological order.";
        _ = LoadMeshDatabaseAsync(exportEntry.Game);
    }

    private List<GestureAnimationItem> BuildAnimationTimeline(ExportEntry track)
    {
        var dynamicAnimSets = FindSharedDynamicAnimSets(track);
        var result = new List<GestureAnimationItem>();
        AddStartingPose(track, dynamicAnimSets, result);

        var gestures = track.GetProperty<ArrayProperty<StructProperty>>("m_aGestures");
        var trackKeys = track.GetProperty<ArrayProperty<StructProperty>>("m_aTrackKeys");
        if (gestures == null)
        {
            return result;
        }

        for (int index = 0; index < gestures.Count; index++)
        {
            PropertyCollection properties = gestures[index].Properties;
            float time = trackKeys != null && index < trackKeys.Count
                ? trackKeys[index].Properties.GetProp<FloatProperty>("fTime")?.Value ?? 0
                : 0;
            float startBlendDuration = properties.GetProp<FloatProperty>("fStartBlendDuration")?.Value ?? 0;
            float endBlendDuration = properties.GetProp<FloatProperty>("fEndBlendDuration")?.Value ?? 0;

            AddAnimation(result, index, time, 0, startBlendDuration, "Pose", properties, "nmPoseSet", "nmPoseAnim", dynamicAnimSets);
            AddAnimation(result, index, time, 1, startBlendDuration, "Gesture", properties, "nmGestureSet", "nmGestureAnim", dynamicAnimSets);
            AddAnimation(result, index, time, 2, endBlendDuration, "Transition", properties, "nmTransitionSet", "nmTransitionAnim", dynamicAnimSets);
        }

        return result.OrderBy(item => item.GestureIndex.HasValue)
            .ThenBy(item => item.Time)
            .ThenBy(item => item.GestureIndex)
            .ThenBy(item => item.SlotOrder)
            .ToList();
    }

    private static void AddStartingPose(ExportEntry track, IReadOnlyList<ExportEntry> dynamicAnimSets, ICollection<GestureAnimationItem> result)
    {
        var setName = track.GetProperty<NameProperty>("nmStartingPoseSet")?.Value ?? "None";
        var animationName = track.GetProperty<NameProperty>("nmStartingPoseAnim")?.Value ?? "None";
        if (IsNone(animationName))
        {
            return;
        }

        result.Add(CreateAnimationItem(null, 0, -1, 0, "Starting Pose", setName, animationName, dynamicAnimSets));
    }

    private static void AddAnimation(ICollection<GestureAnimationItem> result, int gestureIndex, float time, int slotOrder,
        float blendDuration, string slotName, PropertyCollection properties, string setPropertyName, string animationPropertyName,
        IReadOnlyList<ExportEntry> dynamicAnimSets)
    {
        var setName = properties.GetProp<NameProperty>(setPropertyName)?.Value ?? "None";
        var animationName = properties.GetProp<NameProperty>(animationPropertyName)?.Value ?? "None";
        if (!IsNone(animationName))
        {
            result.Add(CreateAnimationItem(gestureIndex, time, slotOrder, blendDuration, slotName, setName, animationName, dynamicAnimSets));
        }
    }

    private static GestureAnimationItem CreateAnimationItem(int? gestureIndex, float time, int slotOrder, float blendDuration,
        string slotName, NameReference setName, NameReference animationName, IReadOnlyList<ExportEntry> dynamicAnimSets)
    {
        return new GestureAnimationItem
        {
            GestureIndex = gestureIndex,
            Time = time,
            BlendDuration = blendDuration,
            SlotOrder = slotOrder,
            SlotName = slotName,
            SetName = setName,
            AnimationName = animationName,
            AnimationExport = ResolveAnimation(setName, animationName, dynamicAnimSets),
        };
    }

    private static ExportEntry ResolveAnimation(NameReference setName, NameReference animationName, IReadOnlyList<ExportEntry> dynamicAnimSets)
    {
        IEnumerable<ExportEntry> candidates = dynamicAnimSets;
        if (!IsNone(setName))
        {
            candidates = candidates.Where(animSet =>
                string.Equals(animSet.GetProperty<NameProperty>("m_nmOrigSetName")?.Value.Name, setName.Name, StringComparison.OrdinalIgnoreCase));
        }

        foreach (ExportEntry animSet in candidates)
        {
            var sequences = animSet.GetProperty<ArrayProperty<ObjectProperty>>("Sequences");
            if (sequences == null)
            {
                continue;
            }

            foreach (ObjectProperty sequenceReference in sequences)
            {
                if (!animSet.FileRef.TryGetUExport(sequenceReference.Value, out ExportEntry sequence)
                    || sequence.ClassName != "AnimSequence")
                {
                    continue;
                }

                NameReference sequenceName = sequence.GetProperty<NameProperty>("SequenceName")?.Value ?? sequence.ObjectName;
                if (string.Equals(sequenceName.Name, animationName.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return sequence;
                }
            }
        }

        return null;
    }

    private static List<ExportEntry> FindSharedDynamicAnimSets(ExportEntry track)
    {
        IMEPackage package = track.FileRef;
        ExportEntry interpData = track.Parent is ExportEntry interpGroup && interpGroup.Parent is ExportEntry parentInterpData
            ? parentInterpData
            : null;
        ExportEntry sequence = interpData == null ? null : FindParentSequence(interpData);
        sequence ??= interpData?.Parent as ExportEntry;

        string propertyName = package.Game is MEGame.ME1 or MEGame.LE1 ? "m_aBioDynAnimSets" : "m_aSFXSharedAnimSets";
        var references = sequence?.GetProperty<ArrayProperty<ObjectProperty>>(propertyName);
        var dynamicAnimSets = new List<ExportEntry>();
        if (references != null)
        {
            foreach (ObjectProperty reference in references)
            {
                if (package.TryGetUExport(reference.Value, out ExportEntry animSet) && animSet.ClassName == "BioDynamicAnimSet")
                {
                    dynamicAnimSets.Add(animSet);
                }
            }
        }

        if (dynamicAnimSets.Count == 0)
        {
            dynamicAnimSets.AddRange(package.Exports.Where(export => export.ClassName == "BioDynamicAnimSet"));
        }

        return dynamicAnimSets;
    }

    private static ExportEntry FindParentSequence(ExportEntry interpData)
    {
        foreach (ExportEntry export in interpData.FileRef.Exports)
        {
            if (export.ClassName is not ("SeqAct_Interp" or "BioSeqAct_PMCheckConditional"))
            {
                continue;
            }

            if (export.GetProperty<ObjectProperty>("InterpData")?.Value == interpData.UIndex)
            {
                return export.Parent as ExportEntry;
            }
        }

        return null;
    }

    private static bool IsNone(NameReference name) =>
        string.IsNullOrWhiteSpace(name.Name) || name.Name.Equals("None", StringComparison.OrdinalIgnoreCase);

    private async Task LoadMeshDatabaseAsync(MEGame game)
    {
        _databaseLoadCancellationTokenSource?.Cancel();
        _databaseLoadCancellationTokenSource?.Dispose();
        _databaseLoadCancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = _databaseLoadCancellationTokenSource.Token;
        string databasePath = AssetDatabaseWindow.GetDBPath(game);
        if (!File.Exists(databasePath))
        {
            StatusText = $"No {game} Asset Database was found. Generate one to select a preview mesh.";
            return;
        }

        try
        {
            var database = new AssetDB();
            await AssetDatabaseWindow.LoadDatabase(databasePath, game, database, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (database.DatabaseVersion != AssetDatabaseWindow.dbCurrentBuild)
            {
                StatusText = $"The {game} Asset Database is out of date. Regenerate it to select a preview mesh.";
                return;
            }

            _databaseFiles.Clear();
            _databaseFiles.AddRange(database.FileList.Select(file => (file.FileName, database.ContentDir[file.DirectoryKey])));
            List<MeshRecord> meshes = database.Meshes.Where(mesh => mesh.IsSkeleton).ToList();
            PreviewMeshComboBox.ItemsSource = meshes;
            string defaultMesh = game switch
            {
                MEGame.ME1 or MEGame.LE1 => "QRN_FAC_ARM_LGTa_MDL",
                MEGame.ME2 or MEGame.LE2 => "QRN_TLI_LGTa_MDL",
                _ => "QRN_ARM_TLIa_MDL",
            };
            PreviewMeshComboBox.SelectedItem = meshes.FirstOrDefault(mesh => mesh.MeshName == defaultMesh) ?? meshes.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Could not load preview meshes: {exception.Message}";
        }
    }

    private void PreviewMeshComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewMeshComboBox.SelectedItem is not MeshRecord mesh)
        {
            return;
        }

        string rootPath = MEDirectories.GetDefaultGamePath(CurrentLoadedExport.Game);
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            StatusText = $"The {CurrentLoadedExport.Game} installation path is not configured.";
            return;
        }

        try
        {
            foreach (MeshUsage usage in mesh.Usages)
            {
                if (usage.FileKey < 0 || usage.FileKey >= _databaseFiles.Count)
                {
                    continue;
                }

                var (fileName, contentDir) = _databaseFiles[usage.FileKey];
                string filePath = Directory.EnumerateFiles(rootPath, $"{fileName}.*", SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.Contains(contentDir, StringComparison.OrdinalIgnoreCase));
                if (filePath == null)
                {
                    continue;
                }

                using IMEPackage meshPackage = MEPackageHandler.OpenMEPackage(filePath);
                if (meshPackage.TryGetUExport(usage.UIndex, out ExportEntry meshExport))
                {
                    AnimPreviewControl.LoadSkeletalMesh(meshExport);
                    PlayAllAnimations();
                    return;
                }
            }

            StatusText = $"Could not resolve mesh '{mesh.MeshName}' to an installed package.";
        }
        catch (Exception exception)
        {
            StatusText = $"Could not load mesh '{mesh.MeshName}': {exception.Message}";
        }
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e)
    {
        PlayAllAnimations();
    }

    private void PlayAllAnimations()
    {
        List<GestureAnimationItem> animations = _animations.Where(item => item.AnimationExport != null).ToList();
        if (animations.Count == 0)
        {
            AnimPreviewControl.ClearAnimation();
            StatusText = "No animations in this gesture track could be resolved.";
            return;
        }

        GestureAnimationItem first = animations[0];
        SelectedAnimation = first;
        AnimationListBox.SelectedItem = first;
        _playbackQueue = new Queue<GestureAnimationItem>(animations.Skip(1));
        AnimPreviewControl.LoadAnimSequenceNonLooping(first.AnimationExport);
        AnimPreviewControl.Play();
        StatusText = $"Playing all {animations.Count} resolved animations in chronological order.";
    }

    private void AnimPreviewControl_AnimationCompleted()
    {
        if (_playbackQueue is not { Count: > 0 })
        {
            _playbackQueue = null;
            StatusText = "Finished playing the gesture track.";
            return;
        }

        GestureAnimationItem next = _playbackQueue.Dequeue();
        SelectedAnimation = next;
        AnimationListBox.SelectedItem = next;
        AnimationListBox.ScrollIntoView(next);
        AnimPreviewControl.CrossfadeToAnimSequence(next.AnimationExport, next.BlendDuration);
        AnimPreviewControl.Play();
    }

    public override void UnloadExport()
    {
        _databaseLoadCancellationTokenSource?.Cancel();
        _playbackQueue = null;
        _animations = [];
        AnimationListBox.ItemsSource = null;
        PreviewMeshComboBox.ItemsSource = null;
        SelectedAnimation = null;
        AnimPreviewControl.Clear();
        CurrentLoadedExport = null;
    }

    public override void PopOut()
    {
        if (CurrentLoadedExport == null)
        {
            return;
        }

        var window = new ExportLoaderHostedWindow(new GesturePreviewExportLoader(), CurrentLoadedExport)
        {
            Title = $"Gesture Preview - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath}",
        };
        window.Show();
    }

    public override void Dispose()
    {
        _databaseLoadCancellationTokenSource?.Cancel();
        _databaseLoadCancellationTokenSource?.Dispose();
        AnimPreviewControl.AnimationCompleted -= AnimPreviewControl_AnimationCompleted;
        AnimPreviewControl.Dispose();
    }
}
