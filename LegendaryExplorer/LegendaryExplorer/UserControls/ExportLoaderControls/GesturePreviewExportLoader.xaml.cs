using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class GesturePreviewExportLoader : ExportLoaderControl
{
    public sealed class GesturePlaybackSettings
    {
        public float PlayRate { get; init; } = 1f;
        public float StartOffset { get; init; }
        public float EndOffset { get; init; }
        public float StartBlendDuration { get; init; }
        public float EndBlendDuration { get; init; }
        public float Weight { get; init; } = 1f;
        public float TransitionBlendTime { get; init; }
        public bool InvalidData { get; init; }
        public bool OneShotAnimation { get; init; }
        public bool ChainToPrevious { get; init; }
        public bool PlayUntilNext { get; init; }
        public bool TerminateAllGestures { get; init; }
        public bool UseDynamicAnimationSets { get; init; }
        public bool SnapToPose { get; init; }
        public string PoseFilter { get; init; }
        public string Pose { get; init; }
        public string GestureFilter { get; init; }
        public string Gesture { get; init; }
        public string ChainedGestures { get; init; }

        public string TimingText => $"Rate {PlayRate:F2}x | Cutoffs {StartOffset:F2}s / {EndOffset:F2}s | Blends {StartBlendDuration:F2}s / {EndBlendDuration:F2}s | Weight {Weight:F2} | Transition blend {TransitionBlendTime:F2}s";
        public string FlagsText => $"One-shot: {OneShotAnimation} | Chain previous: {ChainToPrevious} | Until next: {PlayUntilNext} | Terminate all: {TerminateAllGestures} | Snap: {SnapToPose} | Dynamic sets: {UseDynamicAnimationSets} | Invalid: {InvalidData}";
        public string FiltersText => $"Pose: {PoseFilter}/{Pose} | Gesture: {GestureFilter}/{Gesture} | Chained: {ChainedGestures}";
    }

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
        public GesturePlaybackSettings Settings { get; init; }
        public bool IsControlMarker { get; init; }
        public float AnimationDuration => AnimationExport?.GetProperty<FloatProperty>("SequenceLength")?.Value ?? 0;
        public string DisplayName => GestureIndex is int index ? $"Gesture {index}: {SlotName}" : SlotName;
        public string TimelineText => GestureIndex is int ? $"Track time: {Time:F2}s" : "Track start";
        public string ReferenceText => IsControlMarker ? "Gesture control key" : $"{SetName.Instanced}.{AnimationName.Instanced}";
        public string ResolutionText => IsControlMarker
            ? "No animation; applies gesture-control flags"
            : AnimationExport == null ? "Animation not found in shared animation sets" : $"Export {AnimationExport.UIndex}";
        public string TimingText => Settings?.TimingText ?? $"Starting pose offset: {Time:F2}s";
        public string FlagsText => Settings?.FlagsText;
        public string FiltersText => Settings?.FiltersText;
    }

    private readonly List<(string FileName, string ContentDir)> _databaseFiles = [];
    private readonly PackageCache _packageCache = new();
    private AssetDB _meshDatabase;
    private List<MeshRecord> _previewMeshes = [];
    private bool _updatingPreviewModels;
    private CancellationTokenSource _databaseLoadCancellationTokenSource;
    private List<GestureAnimationItem> _animations = [];
    private GestureAnimationItem _selectedAnimation;
    private string _statusText = "Load a preview mesh to play the gesture track.";
    private string _trackPropertiesText;

    public GestureAnimationItem SelectedAnimation
    {
        get => _selectedAnimation;
        set => SetProperty(ref _selectedAnimation, value);
    }

    public string TrackPropertiesText
    {
        get => _trackPropertiesText;
        private set => SetProperty(ref _trackPropertiesText, value);
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
        AnimPreviewControl.AnimTimeChanged += AnimPreviewControl_AnimTimeChanged;
    }

    public override bool CanParse(ExportEntry exportEntry) =>
        !exportEntry.IsDefaultObject && exportEntry.IsA("BioEvtSysTrackGesture");

    public override void LoadExport(ExportEntry exportEntry)
    {
        UnloadExport();
        CurrentLoadedExport = exportEntry;
        _animations = BuildAnimationTimeline(exportEntry, _packageCache);
        TrackPropertiesText = DescribeTrackProperties(exportEntry);
        AnimationListBox.ItemsSource = _animations;
        SelectedAnimation = _animations.FirstOrDefault();
        StatusText = _animations.Count == 0
            ? "This gesture track does not reference any animations."
            : $"Loaded {_animations.Count} animation slots in chronological order.";
        _ = LoadMeshDatabaseAsync(exportEntry.Game);
    }

    internal static List<GestureAnimationItem> BuildAnimationTimeline(ExportEntry track, PackageCache packageCache)
    {
        var dynamicAnimSets = FindSharedDynamicAnimSets(track, packageCache);
        var result = new List<GestureAnimationItem>();
        AddStartingPose(track, dynamicAnimSets, result, packageCache);

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
            GesturePlaybackSettings settings = ReadPlaybackSettings(properties, startBlendDuration, endBlendDuration);

            int animationCount = result.Count;
            AddAnimation(result, index, time, 0, startBlendDuration, "Pose", properties, "nmPoseSet", "nmPoseAnim", settings, dynamicAnimSets, packageCache);
            AddAnimation(result, index, time, 1, startBlendDuration, "Gesture", properties, "nmGestureSet", "nmGestureAnim", settings, dynamicAnimSets, packageCache);
            AddAnimation(result, index, time, 2, endBlendDuration, "Transition", properties, "nmTransitionSet", "nmTransitionAnim", settings, dynamicAnimSets, packageCache);
            if (result.Count == animationCount)
            {
                result.Add(new GestureAnimationItem
                {
                    GestureIndex = index,
                    Time = time,
                    SlotOrder = 3,
                    SlotName = "Control",
                    SetName = "None",
                    AnimationName = "None",
                    Settings = settings,
                    IsControlMarker = true,
                });
            }
        }

        return result.OrderBy(item => item.GestureIndex.HasValue)
            .ThenBy(item => item.Time)
            .ThenBy(item => item.GestureIndex)
            .ThenBy(item => item.SlotOrder)
            .ToList();
    }

    private static void AddStartingPose(ExportEntry track, IReadOnlyList<ExportEntry> dynamicAnimSets,
        ICollection<GestureAnimationItem> result, PackageCache packageCache)
    {
        var setName = track.GetProperty<NameProperty>("nmStartingPoseSet")?.Value ?? "None";
        var animationName = track.GetProperty<NameProperty>("nmStartingPoseAnim")?.Value ?? "None";
        if (IsNone(animationName))
        {
            return;
        }

        var settings = new GesturePlaybackSettings
        {
            StartOffset = Math.Max(0, track.GetProperty<FloatProperty>("m_fStartPoseOffset")?.Value ?? 0),
        };
        result.Add(CreateAnimationItem(null, 0, -1, 0, "Starting Pose", setName, animationName, dynamicAnimSets, packageCache, settings));
    }

    private static void AddAnimation(ICollection<GestureAnimationItem> result, int gestureIndex, float time, int slotOrder,
        float blendDuration, string slotName, PropertyCollection properties, string setPropertyName, string animationPropertyName,
        GesturePlaybackSettings settings, IReadOnlyList<ExportEntry> dynamicAnimSets, PackageCache packageCache)
    {
        var setName = properties.GetProp<NameProperty>(setPropertyName)?.Value ?? "None";
        var animationName = properties.GetProp<NameProperty>(animationPropertyName)?.Value ?? "None";
        if (!IsNone(animationName))
        {
            result.Add(CreateAnimationItem(gestureIndex, time, slotOrder, blendDuration, slotName, setName, animationName, dynamicAnimSets, packageCache, settings));
        }
    }

    private static GestureAnimationItem CreateAnimationItem(int? gestureIndex, float time, int slotOrder, float blendDuration,
        string slotName, NameReference setName, NameReference animationName, IReadOnlyList<ExportEntry> dynamicAnimSets,
        PackageCache packageCache, GesturePlaybackSettings settings = null)
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
            AnimationExport = ResolveAnimation(setName, animationName, dynamicAnimSets, packageCache),
            Settings = settings,
        };
    }

    private static GesturePlaybackSettings ReadPlaybackSettings(PropertyCollection properties, float startBlendDuration, float endBlendDuration)
    {
        string EnumValue(string propertyName) => properties.GetProp<EnumProperty>(propertyName)?.Value.Instanced ?? "None";
        var chainedGestures = properties.GetProp<ArrayProperty<IntProperty>>("aChainedGestures");
        return new GesturePlaybackSettings
        {
            PlayRate = Math.Max(0.0001f, properties.GetProp<FloatProperty>("fPlayRate")?.Value ?? 1f),
            StartOffset = Math.Max(0, properties.GetProp<FloatProperty>("fStartOffset")?.Value ?? 0),
            EndOffset = Math.Max(0, properties.GetProp<FloatProperty>("fEndOffset")?.Value ?? 0),
            StartBlendDuration = Math.Max(0, startBlendDuration),
            EndBlendDuration = Math.Max(0, endBlendDuration),
            Weight = Math.Max(0, properties.GetProp<FloatProperty>("fWeight")?.Value ?? 1f),
            TransitionBlendTime = Math.Max(0, properties.GetProp<FloatProperty>("fTransBlendTime")?.Value ?? 0),
            InvalidData = properties.GetProp<BoolProperty>("bInvalidData")?.Value ?? false,
            OneShotAnimation = properties.GetProp<BoolProperty>("bOneShotAnim")?.Value ?? false,
            ChainToPrevious = properties.GetProp<BoolProperty>("bChainToPrevious")?.Value ?? false,
            PlayUntilNext = properties.GetProp<BoolProperty>("bPlayUntilNext")?.Value ?? false,
            TerminateAllGestures = properties.GetProp<BoolProperty>("bTerminateAllGestures")?.Value ?? false,
            UseDynamicAnimationSets = properties.GetProp<BoolProperty>("bUseDynAnimSets")?.Value ?? false,
            SnapToPose = properties.GetProp<BoolProperty>("bSnapToPose")?.Value ?? false,
            PoseFilter = EnumValue("ePoseFilter"),
            Pose = EnumValue("ePose"),
            GestureFilter = EnumValue("eGestureFiler"),
            Gesture = EnumValue("eGesture"),
            ChainedGestures = chainedGestures is { Count: > 0 }
                ? string.Join(", ", chainedGestures.Select(property => property.Value))
                : "None",
        };
    }

    private static string DescribeTrackProperties(ExportEntry track)
    {
        string startingPose = track.GetProperty<EnumProperty>("eStartingPose")?.Value.Instanced ?? "None";
        string poseFilter = track.GetProperty<EnumProperty>("ePoseFilter")?.Value.Instanced ?? "None";
        float startingPoseOffset = track.GetProperty<FloatProperty>("m_fStartPoseOffset")?.Value ?? 0;
        bool useDynamicSets = track.GetProperty<BoolProperty>("m_bUseDynamicAnimSets")?.Value ?? false;
        string actor = track.GetProperty<NameProperty>("m_nmFindActor")?.Value.Instanced ?? "None";
        string title = track.GetProperty<StrProperty>("TrackTitle")?.Value ?? track.ObjectName.Instanced;
        return $"{title} | Actor: {actor} | Starting pose: {startingPose} | Pose filter: {poseFilter} | Starting offset: {startingPoseOffset:F2}s | Dynamic sets: {useDynamicSets}";
    }

    private static ExportEntry ResolveAnimation(NameReference setName, NameReference animationName,
        IReadOnlyList<ExportEntry> dynamicAnimSets, PackageCache packageCache)
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
                ExportEntry sequence = sequenceReference.ResolveToExport(animSet.FileRef, packageCache);
                if (sequence?.ClassName != "AnimSequence")
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

    internal static List<ExportEntry> FindSharedDynamicAnimSets(ExportEntry track, PackageCache packageCache)
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
                ExportEntry animSet = reference.ResolveToExport(package, packageCache);
                if (animSet?.ClassName == "BioDynamicAnimSet")
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
            _meshDatabase = database;
            _previewMeshes = meshes;
            ResetPreviewModels(game);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Could not load preview meshes: {exception.Message}";
        }
    }

    private void PreviewMeshSelect_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingPreviewModels || sender is not Button { Tag: string componentName }
            || !Enum.TryParse(componentName, out PreviewActorModelComponent component))
        {
            return;
        }
        string current = GetPreviewMeshTextBox(component).Text;
        MeshRecord mesh = PreviewActorModelDefaults.SelectMesh(this, _previewMeshes, component, current);
        if (mesh is null) return;
        GetPreviewMeshTextBox(component).Text = mesh.MeshName;
        if (PreviewActorModelDefaults.IsNone(mesh))
        {
            AnimPreviewControl.ClearSkeletalMesh(component);
        }
        else
        {
            LoadPreviewMesh(mesh, component, false);
        }
    }

    private void ResetPreviewModels_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentLoadedExport is not null)
        {
            ResetPreviewModels(CurrentLoadedExport.Game);
        }
    }

    private void ResetPreviewModels(MEGame game)
    {
        _updatingPreviewModels = true;
        try
        {
            foreach (PreviewActorModelComponent component in Enum.GetValues<PreviewActorModelComponent>())
            {
                MeshRecord mesh = PreviewActorModelDefaults.FindDefaultMesh(_previewMeshes, _meshDatabase, component, game);
                GetPreviewMeshTextBox(component).Text = mesh?.MeshName ?? PreviewActorModelDefaults.NoneMeshName;
                if (mesh is not null)
                {
                    LoadPreviewMesh(mesh, component, true);
                }
                else
                {
                    AnimPreviewControl.ClearSkeletalMesh(component);
                }
            }
        }
        finally
        {
            _updatingPreviewModels = false;
        }
    }

    private TextBox GetPreviewMeshTextBox(PreviewActorModelComponent component) => component switch
    {
        PreviewActorModelComponent.Body => PreviewMeshTextBox,
        PreviewActorModelComponent.Head => PreviewHeadMeshTextBox,
        PreviewActorModelComponent.Hair => PreviewHairMeshTextBox,
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };

    private void LoadPreviewMesh(MeshRecord mesh, PreviewActorModelComponent component, bool baseGameOnly)
    {

        string rootPath = MEDirectories.GetDefaultGamePath(CurrentLoadedExport.Game);
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            StatusText = $"The {CurrentLoadedExport.Game} installation path is not configured.";
            return;
        }

        try
        {
            foreach (MeshUsage usage in PreviewActorModelDefaults.GetUsages(mesh, _meshDatabase, baseGameOnly))
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
                    AnimPreviewControl.LoadSkeletalMesh(component, meshExport);
                    if (component is PreviewActorModelComponent.Body)
                    {
                        PlayAllAnimations();
                    }
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
        List<GestureAnimationItem> resolvedAnimations = _animations.Where(item => item.AnimationExport != null).ToList();
        if (resolvedAnimations.Count == 0)
        {
            AnimPreviewControl.ClearAnimation();
            StatusText = "No animations in this gesture track could be resolved.";
            return;
        }

        List<AnimationPreviewControl.AnimationTimelineClip> timeline = BuildPlaybackTimeline(_animations);
        if (timeline.Count == 0)
        {
            AnimPreviewControl.ClearAnimation();
            StatusText = "This gesture track has no valid animation time ranges to play.";
            return;
        }

        SelectedAnimation = resolvedAnimations[0];
        AnimationListBox.SelectedItem = SelectedAnimation;
        AnimPreviewControl.LoadAnimSequenceTimeline(timeline);
        AnimPreviewControl.Play();
        StatusText = $"Playing {resolvedAnimations.Count} resolved animations as one blended gesture timeline.";
    }

    private void AnimPreviewControl_AnimationCompleted()
    {
        StatusText = "Finished playing the gesture track.";
    }

    private void AnimPreviewControl_AnimTimeChanged(float time)
    {
        GestureAnimationItem activeItem = _animations
            .Where(item => item.GestureIndex.HasValue && item.Time <= time)
            .OrderByDescending(item => item.Time)
            .ThenBy(item => item.SlotOrder)
            .FirstOrDefault() ?? _animations.FirstOrDefault();
        if (activeItem != null && activeItem != SelectedAnimation)
        {
            SelectedAnimation = activeItem;
            AnimationListBox.SelectedItem = activeItem;
            AnimationListBox.ScrollIntoView(activeItem);
        }
    }

    private void AnimationListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AnimationListBox.SelectedItem is not GestureAnimationItem item || item.AnimationExport == null)
        {
            return;
        }

        List<AnimationPreviewControl.AnimationTimelineClip> timeline = BuildPlaybackTimeline(_animations);
        if (timeline.Count == 0)
        {
            return;
        }

        AnimPreviewControl.LoadAnimSequenceTimeline(timeline);
        AnimPreviewControl.AnimSliderValue = item.Time;
        SelectedAnimation = item;
        StatusText = $"Moved to {item.DisplayName} at {item.Time:F2}s.";
    }

    internal static List<AnimationPreviewControl.AnimationTimelineClip> BuildPlaybackTimeline(List<GestureAnimationItem> animations)
    {
        var timeline = new List<AnimationPreviewControl.AnimationTimelineClip>();
        List<IGrouping<int?, GestureAnimationItem>> gestureGroups = animations
            .Where(item => item.GestureIndex.HasValue && item.Settings is { InvalidData: false })
            .GroupBy(item => item.GestureIndex)
            .OrderBy(group => group.First().Time)
            .ToList();

        for (int groupIndex = 0; groupIndex < gestureGroups.Count; groupIndex++)
        {
            IGrouping<int?, GestureAnimationItem> group = gestureGroups[groupIndex];
            List<GestureAnimationItem> items = group.OrderBy(item => item.SlotOrder).ToList();
            GesturePlaybackSettings settings = items[0].Settings;
            float keyTime = items[0].Time;
            IGrouping<int?, GestureAnimationItem> nextTimeGroup = gestureGroups
                .Skip(groupIndex + 1)
                .FirstOrDefault(nextGroup => nextGroup.First().Time > keyTime);
            float? nextKeyTime = nextTimeGroup?.First().Time;
            float? terminationTime = gestureGroups
                .Skip(groupIndex + 1)
                .FirstOrDefault(nextGroup => nextGroup.Any(item => item.Settings.TerminateAllGestures))?
                .First().Time;
            float? chainTime = nextTimeGroup?.Any(item => item.Settings.ChainToPrevious) == true
                ? nextKeyTime
                : null;
            float? cutoffTime = terminationTime is null
                ? chainTime
                : chainTime is null ? terminationTime : Math.Min(terminationTime.Value, chainTime.Value);

            GestureAnimationItem pose = items.FirstOrDefault(item => item.SlotName == "Pose" && item.AnimationExport is not null);
            GestureAnimationItem gesture = items.FirstOrDefault(item => item.SlotName == "Gesture" && item.AnimationExport is not null);
            GestureAnimationItem transition = items.FirstOrDefault(item => item.SlotName == "Transition" && item.AnimationExport is not null);
            float primaryDuration = GetPlaybackDuration(gesture ?? pose, settings);
            float primaryEnd = keyTime + primaryDuration;
            if (cutoffTime is float cutoff)
            {
                primaryEnd = Math.Min(primaryEnd, cutoff);
            }
            if (settings.PlayUntilNext && nextKeyTime is float next)
            {
                primaryEnd = next;
            }

            AddTimelineClip(timeline, pose, keyTime, primaryEnd, settings, settings.SnapToPose ? 0 : settings.StartBlendDuration,
                settings.EndBlendDuration, settings.PlayUntilNext && !settings.OneShotAnimation);
            AddTimelineClip(timeline, gesture, keyTime, primaryEnd, settings, settings.SnapToPose ? 0 : settings.StartBlendDuration,
                settings.EndBlendDuration, settings.PlayUntilNext && !settings.OneShotAnimation);

            if (transition != null)
            {
                float transitionBlend = settings.TransitionBlendTime > 0 ? settings.TransitionBlendTime : settings.EndBlendDuration;
                float transitionStart = keyTime;
                float transitionEnd = transitionStart + GetPlaybackDuration(transition, settings);
                if (cutoffTime is float transitionCutoff)
                {
                    transitionEnd = Math.Min(transitionEnd, transitionCutoff);
                }
                AddTimelineClip(timeline, transition, transitionStart, transitionEnd, settings, transitionBlend, 0, false);
            }
        }

        GestureAnimationItem startingPose = animations.FirstOrDefault(item => !item.GestureIndex.HasValue);
        if (startingPose != null && timeline.Count == 0)
        {
            float startingPoseDuration = Math.Max(0.001f, GetPlaybackDuration(startingPose, startingPose.Settings));
            AddTimelineClip(timeline, startingPose, 0, startingPoseDuration, startingPose.Settings, 0, 0, true, insertAtStart: true);
        }
        else if (startingPose != null)
        {
            float timelineStart = timeline.Min(clip => clip.StartTime);
            float timelineEnd = timeline.Max(clip => clip.EndTime);
            AddTimelineClip(timeline, startingPose, timelineStart, timelineEnd, startingPose.Settings, 0, 0, true, insertAtStart: true);
        }

        return timeline;
    }

    private static float GetPlaybackDuration(GestureAnimationItem item, GesturePlaybackSettings settings)
    {
        if (item == null)
        {
            return 0;
        }

        float sourceDuration = Math.Max(0, item.AnimationDuration - settings.StartOffset - settings.EndOffset);
        return sourceDuration / Math.Max(0.0001f, settings.PlayRate);
    }

    private static void AddTimelineClip(IList<AnimationPreviewControl.AnimationTimelineClip> timeline, GestureAnimationItem item,
        float startTime, float endTime, GesturePlaybackSettings settings, float blendIn, float blendOut, bool loop,
        bool insertAtStart = false)
    {
        if (item?.AnimationExport == null || endTime <= startTime)
        {
            return;
        }

        float animationStart = Math.Min(settings.StartOffset, item.AnimationDuration);
        float animationEnd = Math.Max(animationStart, item.AnimationDuration - settings.EndOffset);
        if (animationEnd <= animationStart)
        {
            return;
        }

        var clip = new AnimationPreviewControl.AnimationTimelineClip
        {
            AnimationExport = item.AnimationExport,
            StartTime = startTime,
            EndTime = endTime,
            AnimationStartTime = animationStart,
            AnimationEndTime = animationEnd,
            PlayRate = settings.PlayRate,
            BlendInDuration = blendIn,
            BlendOutDuration = blendOut,
            Weight = settings.Weight,
            Loop = loop,
        };
        if (insertAtStart)
        {
            timeline.Insert(0, clip);
        }
        else
        {
            timeline.Add(clip);
        }
    }

    public override void UnloadExport()
    {
        _databaseLoadCancellationTokenSource?.Cancel();
        _animations = [];
        AnimationListBox.ItemsSource = null;
        PreviewMeshTextBox.Clear();
        PreviewHeadMeshTextBox.Clear();
        PreviewHairMeshTextBox.Clear();
        _meshDatabase = null;
        _previewMeshes = [];
        SelectedAnimation = null;
        TrackPropertiesText = null;
        StatusText = "Load a preview mesh to play the gesture track.";
        AnimPreviewControl.Clear();
        _packageCache.ReleasePackages();
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
        AnimPreviewControl.AnimTimeChanged -= AnimPreviewControl_AnimTimeChanged;
        AnimPreviewControl.Dispose();
        _packageCache.Dispose();
    }
}
