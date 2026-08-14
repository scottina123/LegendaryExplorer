using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Collections;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Newtonsoft.Json;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;
using BioStageBinary = LegendaryExplorerCore.Unreal.BinaryConverters.BioStage;
using CoreColor = LegendaryExplorerCore.SharpDX.Color;
using SkeletalMeshBinary = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

/// <summary>
/// Edits a BioStage actor, its property/binary camera lists, and the RefSkeleton of its attached mesh
/// in one 3D view. Stage geometry is deliberately rendered as wireframe because authored stage meshes
/// are commonly invisible or use placeholder materials.
/// </summary>
public sealed partial class BioStageEditor : ExportLoaderControl, IActorEditorContext,
    ISceneRenderContextConfigurable
{
    public sealed class NumericEditor : NotifyPropertyChangedBase
    {
        private readonly Action<double> changed;
        private double value;

        public NumericEditor(string label, double value, double minimum, double maximum, double increment,
            Action<double> changed, string toolTip = null, bool isEditable = true, string formatString = "0.###")
        {
            Label = label;
            this.value = value;
            Minimum = minimum;
            Maximum = maximum;
            Increment = increment;
            LargeIncrement = increment * 10;
            this.changed = changed;
            ToolTip = toolTip;
            IsEditable = isEditable;
            FormatString = formatString;
        }

        public string Label { get; }
        public double Minimum { get; }
        public double Maximum { get; }
        public double Increment { get; }
        public double LargeIncrement { get; }
        public string ToolTip { get; }
        public bool IsEditable { get; }
        public string FormatString { get; }

        public double Value
        {
            get => value;
            set
            {
                if (SetProperty(ref this.value, value) && IsEditable)
                {
                    changed?.Invoke(value);
                }
            }
        }

        internal void SetValueWithoutCommit(double newValue)
        {
            if (value != newValue)
            {
                value = newValue;
                OnPropertyChanged(nameof(Value));
            }
        }
    }

    public sealed record BoneParentOption(int Index, string DisplayName);

    public sealed class CameraItem : NotifyPropertyChangedBase
    {
        private readonly BioStageEditor owner;
        private NameReference name;
        private string nameText;
        private bool disableHeightAdjustment;

        internal CameraItem(BioStageEditor owner, NameReference name, StructProperty propertyEntry,
            PropertyCollection binaryEntry)
        {
            this.owner = owner;
            this.name = name;
            nameText = name.Instanced;
            PropertyEntry = propertyEntry;
            BinaryEntry = binaryEntry;

            float ReadFloat(string propertyName, float fallback)
            {
                float value = propertyEntry?.GetProp<FloatProperty>(propertyName)?.Value ?? fallback;
                return binaryEntry?.GetProp<FloatProperty>(propertyName)?.Value ?? value;
            }

            disableHeightAdjustment = binaryEntry?.GetProp<BoolProperty>("bDisableHeightAdjustment")?.Value
                                      ?? propertyEntry?.GetProp<BoolProperty>("bDisableHeightAdjustment")?.Value
                                      ?? false;
            Editors =
            [
                new NumericEditor("FOV", ReadFloat("fFov", 60), 1, 179, 0.1,
                    value => owner.SetCameraFloat(this, "fFov", (float)value),
                    "Horizontal field of view in degrees"),
                new NumericEditor("Near plane", ReadFloat("fNearPlane", 10), 0, 10000, 0.1,
                    value => owner.SetCameraFloat(this, "fNearPlane", (float)value),
                    "Camera near clipping plane"),
                new NumericEditor("Height delta", ReadFloat("fHeightDelta", 0), -10000, 10000, 0.1,
                    value => owner.SetCameraFloat(this, "fHeightDelta", (float)value),
                    "World Z offset applied after the camera bone"),
                new NumericEditor("Pitch delta", ReadFloat("fPitchDelta", 0), -180, 180, 0.1,
                    value => owner.SetCameraFloat(this, "fPitchDelta", (float)value),
                    "Pitch offset in degrees"),
                new NumericEditor("Yaw delta", ReadFloat("fYawDelta", 0), -180, 180, 0.1,
                    value => owner.SetCameraFloat(this, "fYawDelta", (float)value),
                    "Yaw offset in degrees")
            ];
        }

        internal StructProperty PropertyEntry { get; set; }
        internal PropertyCollection BinaryEntry { get; set; }
        internal NameReference Name => name;

        public ObservableCollection<NumericEditor> Editors { get; }
        public string DisplayName => name.Instanced;
        public string SourceLabel => PropertyEntry is not null && BinaryEntry is not null
            ? "property + binary"
            : PropertyEntry is not null ? "property" : "binary";

        public string NameText
        {
            get => nameText;
            set => SetProperty(ref nameText, value);
        }

        public bool DisableHeightAdjustment
        {
            get => disableHeightAdjustment;
            set
            {
                if (SetProperty(ref disableHeightAdjustment, value))
                {
                    owner.SetCameraBool(this, "bDisableHeightAdjustment", value);
                }
            }
        }

        internal float GetValue(string label, float fallback = 0) =>
            (float)(Editors.FirstOrDefault(editor => editor.Label == label)?.Value ?? fallback);

        internal void SetName(NameReference newName)
        {
            name = newName;
            nameText = newName.Instanced;
            OnPropertyChanged(nameof(NameText));
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public sealed class BoneItem : NotifyPropertyChangedBase, IHitProxy, ITransformWidgetTarget
    {
        private readonly BioStageEditor owner;
        private string nameText;

        internal BoneItem(BioStageEditor owner, int index)
        {
            this.owner = owner;
            Index = index;
            nameText = Bone.Name.Instanced;
            Vector3 rotation = Rotator.FromQuaternion(Bone.Orientation).GetDegreesVector();
            CoreColor color = Bone.BoneColor;
            double flagsMaximum = Math.Max(65535d, Bone.Flags);
            Editors =
            [
                new NumericEditor("Position X", Bone.Position.X, -100000, 100000, 0.1,
                    value => SetPositionComponent(0, (float)value)),
                new NumericEditor("Position Y", Bone.Position.Y, -100000, 100000, 0.1,
                    value => SetPositionComponent(1, (float)value)),
                new NumericEditor("Position Z", Bone.Position.Z, -100000, 100000, 0.1,
                    value => SetPositionComponent(2, (float)value)),
                new NumericEditor("Rotation roll", rotation.X, -180, 180, 0.1,
                    _ => SetRotationFromEditors()),
                new NumericEditor("Rotation pitch", rotation.Y, -180, 180, 0.1,
                    _ => SetRotationFromEditors()),
                new NumericEditor("Rotation yaw", rotation.Z, -180, 180, 0.1,
                    _ => SetRotationFromEditors()),
                new NumericEditor("Flags", Bone.Flags, 0, flagsMaximum, 1,
                    value => { Bone.Flags = (uint)Math.Clamp(Math.Round(value), 0, uint.MaxValue); Changed(); },
                    "Raw RefSkeleton bone flags", formatString: "0"),
                new NumericEditor("Child count", Bone.NumChildren, 0, Math.Max(256, Bone.NumChildren), 1,
                    null, "Derived from the parent indices", isEditable: false, formatString: "0"),
                new NumericEditor("Color R", color.R, 0, 255, 1, _ => SetColorFromEditors(), formatString: "0"),
                new NumericEditor("Color G", color.G, 0, 255, 1, _ => SetColorFromEditors(), formatString: "0"),
                new NumericEditor("Color B", color.B, 0, 255, 1, _ => SetColorFromEditors(), formatString: "0"),
                new NumericEditor("Color A", color.A, 0, 255, 1, _ => SetColorFromEditors(), formatString: "0")
            ];
        }

        private MeshBone Bone => owner.meshBinary.RefSkeleton[Index];

        public int Index { get; }
        public string DisplayName => $"{Index}: {Bone.Name.Instanced}";
        public Visibility CameraMarkerVisibility => owner.HasCamera(Bone.Name)
            ? Visibility.Visible
            : Visibility.Collapsed;

        public string NameText
        {
            get => nameText;
            set => SetProperty(ref nameText, value);
        }

        public int ParentIndex
        {
            get => Bone.ParentIndex;
            set => owner.ChangeBoneParent(this, value);
        }

        public IReadOnlyList<BoneParentOption> ParentOptions => owner.GetParentOptions(Index);
        public ObservableCollection<NumericEditor> Editors { get; }

        private NumericEditor Editor(string label) => Editors.First(editor => editor.Label == label);

        private void SetPositionComponent(int component, float value)
        {
            Vector3 position = Bone.Position;
            if (component == 0) position.X = value;
            else if (component == 1) position.Y = value;
            else position.Z = value;
            Bone.Position = position;
            Changed();
        }

        private void SetRotationFromEditors()
        {
            var degrees = new Vector3((float)Editor("Rotation roll").Value,
                (float)Editor("Rotation pitch").Value, (float)Editor("Rotation yaw").Value);
            Bone.Orientation = Quaternion.Normalize(Rotator.FromDegreesVector(degrees).ToQuaternion());
            Changed();
        }

        private void SetColorFromEditors()
        {
            Bone.BoneColor = new CoreColor(
                (byte)Math.Clamp(Math.Round(Editor("Color R").Value), 0, 255),
                (byte)Math.Clamp(Math.Round(Editor("Color G").Value), 0, 255),
                (byte)Math.Clamp(Math.Round(Editor("Color B").Value), 0, 255),
                (byte)Math.Clamp(Math.Round(Editor("Color A").Value), 0, 255));
            Changed();
        }

        private void Changed()
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(LocalToWorld));
            owner.OnBoneChanged(this);
        }

        internal void RefreshFromBone()
        {
            Vector3 rotation = Rotator.FromQuaternion(Bone.Orientation).GetDegreesVector();
            Editor("Position X").SetValueWithoutCommit(Bone.Position.X);
            Editor("Position Y").SetValueWithoutCommit(Bone.Position.Y);
            Editor("Position Z").SetValueWithoutCommit(Bone.Position.Z);
            Editor("Rotation roll").SetValueWithoutCommit(rotation.X);
            Editor("Rotation pitch").SetValueWithoutCommit(rotation.Y);
            Editor("Rotation yaw").SetValueWithoutCommit(rotation.Z);
            Editor("Flags").SetValueWithoutCommit(Bone.Flags);
            Editor("Child count").SetValueWithoutCommit(Bone.NumChildren);
            Editor("Color R").SetValueWithoutCommit(Bone.BoneColor.R);
            Editor("Color G").SetValueWithoutCommit(Bone.BoneColor.G);
            Editor("Color B").SetValueWithoutCommit(Bone.BoneColor.B);
            Editor("Color A").SetValueWithoutCommit(Bone.BoneColor.A);
            OnPropertyChanged(nameof(ParentIndex));
            OnPropertyChanged(nameof(ParentOptions));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(CameraMarkerVisibility));
            OnPropertyChanged(nameof(LocalToWorld));
        }

        internal void ApplyName(NameReference newName)
        {
            Bone.Name = newName;
            nameText = newName.Instanced;
            OnPropertyChanged(nameof(NameText));
            OnPropertyChanged(nameof(DisplayName));
        }

        public int HitID { get; set; }
        public int HitPriority => IHitProxy.UIPriority;

        Vector3 ITransformWidgetTarget.Location
        {
            get => LocalToWorld.Translation;
            set => owner.SetBoneWorldLocation(this, value);
        }

        Rotator ITransformWidgetTarget.Rotation
        {
            get => owner.GetBoneWorldRotation(Index);
            set => owner.SetBoneWorldRotation(this, value);
        }

        public float DrawScale { get; set; } = 1;
        public Vector3 DrawScale3D { get; set; } = Vector3.One;
        public bool IsReadOnly => false;
        public Matrix4x4 LocalToWorld => owner.GetBoneWorldTransform(Index);
        public TransformSnapshot SnapshotTransform() => new(LocalToWorld.Translation,
            owner.GetBoneWorldRotation(Index), DrawScale, DrawScale3D);
    }

    private static readonly RenderPass[] BackdropRenderPasses =
        [RenderPass.Base, RenderPass.Hair, RenderPass.Collision];
    private static readonly object SessionLevelPathsLock = new();
    private static readonly List<string> SessionLevelPaths = [];

    private readonly List<IMEPackage> levelPackages = [];
    private readonly List<string> levelPaths = [];
    private readonly List<ActorProxy> levelActors = [];
    private readonly List<(Vector3 Start, Vector3 End)> stageWireframeEdges = [];
    private readonly Dictionary<int, List<(Vector3 A, Vector3 B, Vector3 C)>> stageTrianglesByBone = [];
    private readonly DispatcherTimer meshWriteTimer;
    private bool eventsAttached;
    private bool sessionLevelsRestored;
    private bool meshWritePending;
    private bool disposed;
    private bool cameraFocusMode;
    private bool focusUsesSelectedBone;
    private bool showCollision = Settings.LevelEditor_ShowCollision;
    private bool showLightIcons;
    private bool showVolumes = Settings.LevelEditor_ShowVolumes;
    private bool showVolumetrics;
    private bool unlit = Settings.LevelEditor_Unlit;
    private bool setAlphaToBlack = true;
    private bool showRedChannel = true;
    private bool showGreenChannel = true;
    private bool showBlueChannel = true;
    private bool showAlphaChannel = true;
    private System.Windows.Media.Color backgroundColor;
    private string currentExportName;
    private string sceneStatus = "Select a BioStage export, then optionally open a level backdrop.";
    private string stageMeshPath = "No attached SkeletalMesh found.";
    private CameraItem selectedCamera;
    private BoneItem selectedBone;

    private BioStageBinary stageBinary;
    private ArrayProperty<StructProperty> cameraArray;
    private ExportEntry cameraPropertyOwner;
    private ExportEntry meshExport;
    private SkeletalMeshBinary meshBinary;
    private ModelPreview<WorldVertex> stagePreview;
    private Vector3 stageLocation;
    private Vector3 stageRotation;
    private float stageDrawScale = 1;
    private Vector3 stageDrawScale3D = Vector3.One;
    private string stageLocationPropertyName = "location";

    public BioStageEditor() : base("BioStage Editor")
    {
        RenderContext = new LevelEditorRenderContext
        {
            ConstrainedAspectRatio = 16f / 9f,
            UseGameShaderMeshPreviews = false,
            UseGameShaderStaticMeshPreviews = false,
            UseSrgbColorManagement = true,
            ShowCameraPositionInStatsOverlay = false,
        };
        backgroundColor = LevelEditor.GetThemeDefaultBackgroundColor();
        RenderContext.BackgroundColor = backgroundColor;
        RenderContext.ShowLightIcons = false;
        RenderContext.ShowEmitterIcons = false;
        RenderContext.ShowPointsOfInterest = false;
        RenderContext.SetShowEmitterVfx(false);
        if (unlit) RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Unlit;
        ApplyColorChannelFlags();

        ToggleTranslateCommand = new GenericCommand(() => SetWidgetMode(EWidgetMode.Translate));
        ToggleRotateCommand = new GenericCommand(() => SetWidgetMode(EWidgetMode.Rotate));
        ToggleScaleCommand = new GenericCommand(() => SetWidgetMode(EWidgetMode.Translate));
        ToggleUniformScaleCommand = new GenericCommand(() => SetWidgetMode(EWidgetMode.Translate));
        ToggleLocalCoordsCommand = new GenericCommand(() => UseLocalCoordsForWidget = !UseLocalCoordsForWidget);

        InitializeComponent();
        SceneViewer.Context = RenderContext;
        RenderContext.EnableTransformWidget();
        meshWriteTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(175)
        };
        meshWriteTimer.Tick += MeshWriteTimer_Tick;
        PreviewMouseMove += (_, _) => RenderContext.NotifyUserActivity();
        PreviewMouseDown += (_, _) => RenderContext.NotifyUserActivity();
        PreviewMouseWheel += (_, _) => RenderContext.NotifyUserActivity();
        PreviewKeyDown += (_, _) => RenderContext.NotifyUserActivity();
    }

    public LevelEditorRenderContext RenderContext { get; }
    public bool IsApplyingUndoRedo => false;
    public ObservableCollection<NumericEditor> StageEditors { get; } = [];
    public ObservableCollection<CameraItem> Cameras { get; } = [];
    public ObservableCollection<BoneItem> Bones { get; } = [];

    public GenericCommand ToggleTranslateCommand { get; }
    public GenericCommand ToggleRotateCommand { get; }
    public GenericCommand ToggleScaleCommand { get; }
    public GenericCommand ToggleUniformScaleCommand { get; }
    public GenericCommand ToggleLocalCoordsCommand { get; }

    public string CurrentExportName
    {
        get => currentExportName;
        private set => SetProperty(ref currentExportName, value);
    }

    public string SceneStatus
    {
        get => sceneStatus;
        private set => SetProperty(ref sceneStatus, value);
    }

    public string StageMeshPath
    {
        get => stageMeshPath;
        private set => SetProperty(ref stageMeshPath, value);
    }

    public CameraItem SelectedCamera
    {
        get => selectedCamera;
        set
        {
            if (SetProperty(ref selectedCamera, value) && CameraFocusMode)
            {
                focusUsesSelectedBone = false;
                PreviewSelectedCamera();
            }
        }
    }

    public BoneItem SelectedBone
    {
        get => selectedBone;
        set
        {
            if (!SetProperty(ref selectedBone, value)) return;
            RenderContext.TransformWidget.Attach = CameraFocusMode ? null : value;
            if (CameraFocusMode && focusUsesSelectedBone) PreviewSelectedBoneCamera();
            SceneViewer?.MarkRenderDirty();
        }
    }

    public bool CameraFocusMode
    {
        get => cameraFocusMode;
        set
        {
            if (!SetProperty(ref cameraFocusMode, value)) return;
            SceneViewer.IsMouseInputEnabled = !value;
            RenderContext.TransformWidget.Attach = value ? null : SelectedBone;
            if (value)
            {
                if (focusUsesSelectedBone) PreviewSelectedBoneCamera();
                else PreviewSelectedCamera();
            }
            else
            {
                focusUsesSelectedBone = false;
            }
            SceneViewer.MarkRenderDirty();
        }
    }

    public bool UseLocalCoordsForWidget
    {
        get => RenderContext.TransformWidget.UseLocalCoords;
        set => SetProperty(ref RenderContext.TransformWidget.UseLocalCoords, value);
    }

    public bool ShowCollision
    {
        get => showCollision;
        set { if (SetProperty(ref showCollision, value)) SceneViewer?.MarkRenderDirty(); }
    }

    public bool ShowLightIcons
    {
        get => showLightIcons;
        set
        {
            if (SetProperty(ref showLightIcons, value))
            {
                RenderContext.ShowLightIcons = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    public bool ShowVolumes
    {
        get => showVolumes;
        set { if (SetProperty(ref showVolumes, value)) SceneViewer?.MarkRenderDirty(); }
    }

    public bool ShowVolumetrics
    {
        get => showVolumetrics;
        set { if (SetProperty(ref showVolumetrics, value)) SceneViewer?.MarkRenderDirty(); }
    }

    public bool Unlit
    {
        get => unlit;
        set
        {
            if (!SetProperty(ref unlit, value)) return;
            if (value) RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Unlit;
            else RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.Unlit;
            SceneViewer?.MarkRenderDirty();
        }
    }

    public bool SetAlphaToBlack
    {
        get => setAlphaToBlack;
        set { if (SetProperty(ref setAlphaToBlack, value)) ApplyColorChannelFlags(); }
    }

    public bool ShowRedChannel
    {
        get => showRedChannel;
        set { if (SetProperty(ref showRedChannel, value)) ApplyColorChannelFlags(); }
    }

    public bool ShowGreenChannel
    {
        get => showGreenChannel;
        set { if (SetProperty(ref showGreenChannel, value)) ApplyColorChannelFlags(); }
    }

    public bool ShowBlueChannel
    {
        get => showBlueChannel;
        set { if (SetProperty(ref showBlueChannel, value)) ApplyColorChannelFlags(); }
    }

    public bool ShowAlphaChannel
    {
        get => showAlphaChannel;
        set { if (SetProperty(ref showAlphaChannel, value)) ApplyColorChannelFlags(); }
    }

    public System.Windows.Media.Color BackgroundColor
    {
        get => backgroundColor;
        set
        {
            if (SetProperty(ref backgroundColor, value))
            {
                RenderContext.BackgroundColor = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private void ApplyColorChannelFlags()
    {
        SetRenderFlag(LevelEditorRenderContext.ShaderFlags.AlphaAsBlack, setAlphaToBlack);
        SetRenderFlag(LevelEditorRenderContext.ShaderFlags.EnableRedChannel, showRedChannel);
        SetRenderFlag(LevelEditorRenderContext.ShaderFlags.EnableGreenChannel, showGreenChannel);
        SetRenderFlag(LevelEditorRenderContext.ShaderFlags.EnableBlueChannel, showBlueChannel);
        SetRenderFlag(LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel, showAlphaChannel);
        SceneViewer?.MarkRenderDirty();
    }

    private void SetRenderFlag(LevelEditorRenderContext.ShaderFlags flag, bool enabled)
    {
        if (enabled) RenderContext.RenderFlags |= flag;
        else RenderContext.RenderFlags &= ~flag;
    }

    public override bool CanParse(ExportEntry exportEntry) =>
        exportEntry?.ClassName == "BioStage" && exportEntry.Game.IsGame3();

    public override void LoadExport(ExportEntry exportEntry)
    {
        if (!CanParse(exportEntry))
        {
            UnloadExport();
            return;
        }

        FlushMeshWrite();
        UnregisterBones();
        DisposeStagePreview();
        CurrentLoadedExport = exportEntry;
        CurrentExportName = $"{exportEntry.UIndex}: {exportEntry.InstancedFullPath}";
        stageBinary = ObjectBinary.From<BioStageBinary>(exportEntry) ?? BioStageBinary.Create();
        cameraPropertyOwner = FindCameraPropertyOwner(exportEntry) ?? exportEntry;
        cameraArray = cameraPropertyOwner.GetProperty<ArrayProperty<StructProperty>>("m_aCameraList")
                      ?? new ArrayProperty<StructProperty>("m_aCameraList");
        LoadStageTransform();
        meshExport = FindAttachedSkeletalMesh(exportEntry);
        meshBinary = meshExport is null ? null : ObjectBinary.From<SkeletalMeshBinary>(meshExport);
        StageMeshPath = meshExport is null
            ? "No attached SkeletalMesh found."
            : $"#{meshExport.UIndex} {meshExport.InstancedFullPath}";
        BuildStagePreview();
        RebuildCameras();
        RebuildBones();
        SceneStatus = meshBinary is null
            ? $"BioStage loaded; no editable RefSkeleton found; {levelPaths.Count} backdrop file(s)."
            : $"{Bones.Count} bone(s), {Cameras.Count} camera(s); {levelPaths.Count} backdrop file(s).";
        SceneViewer.SetShouldRender(true);
        SceneViewer.MarkRenderDirty();
        _ = RestoreSessionLevelsAsync();
    }

    public override void UnloadExport()
    {
        FlushMeshWrite();
        CameraFocusMode = false;
        UnregisterBones();
        DisposeStagePreview();
        StageEditors.Clear();
        Cameras.Clear();
        Bones.Clear();
        SelectedCamera = null;
        SelectedBone = null;
        stageBinary = null;
        cameraArray = null;
        cameraPropertyOwner = null;
        meshBinary = null;
        meshExport = null;
        CurrentLoadedExport = null;
        CurrentExportName = null;
        StageMeshPath = "No attached SkeletalMesh found.";
        SceneViewer?.MarkRenderDirty();
    }

    public override void PopOut()
    {
        if (CurrentLoadedExport is null) return;
        var window = new ExportLoaderHostedWindow(new BioStageEditor(), CurrentLoadedExport)
        {
            Title = $"BioStage Editor - {CurrentLoadedExport.UIndex} {CurrentLoadedExport.InstancedFullPath}"
        };
        window.Show();
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        meshWriteTimer.Stop();
        meshWriteTimer.Tick -= MeshWriteTimer_Tick;
        FlushMeshWrite();
        UnloadExport();
        CloseLevels(rebuildStagePreview: false);
        DetachEvents();
        SceneViewer.Dispose();
    }

    private void LoadStageTransform()
    {
        PropertyCollection properties = CurrentLoadedExport.GetProperties();
        StructProperty locationProperty = properties.GetProp<StructProperty>("location")
                                          ?? properties.GetProp<StructProperty>("Location");
        stageLocationPropertyName = locationProperty?.Name.Name ?? "location";
        stageLocation = locationProperty is null ? Vector3.Zero : CommonStructs.GetVector3(locationProperty);
        StructProperty rotationProperty = properties.GetProp<StructProperty>("Rotation");
        stageRotation = rotationProperty is null
            ? Vector3.Zero
            : CommonStructs.GetRotator(rotationProperty).GetDegreesVector();
        stageDrawScale = properties.GetProp<FloatProperty>("DrawScale")?.Value ?? 1;
        StructProperty scaleProperty = properties.GetProp<StructProperty>("DrawScale3D");
        stageDrawScale3D = scaleProperty is null ? Vector3.One : CommonStructs.GetVector3(scaleProperty);

        StageEditors.Clear();
        StageEditors.Add(new NumericEditor("Location X", stageLocation.X, -100000, 100000, 1,
            value => SetStageLocation(0, (float)value)));
        StageEditors.Add(new NumericEditor("Location Y", stageLocation.Y, -100000, 100000, 1,
            value => SetStageLocation(1, (float)value)));
        StageEditors.Add(new NumericEditor("Location Z", stageLocation.Z, -100000, 100000, 1,
            value => SetStageLocation(2, (float)value)));
        StageEditors.Add(new NumericEditor("Rotation roll", stageRotation.X, -180, 180, 0.1,
            value => SetStageRotation(0, (float)value)));
        StageEditors.Add(new NumericEditor("Rotation pitch", stageRotation.Y, -180, 180, 0.1,
            value => SetStageRotation(1, (float)value)));
        StageEditors.Add(new NumericEditor("Rotation yaw", stageRotation.Z, -180, 180, 0.1,
            value => SetStageRotation(2, (float)value)));
        StageEditors.Add(new NumericEditor("Draw scale", stageDrawScale, 0.001, 100, 0.01,
            value => { stageDrawScale = (float)value; CommitStageTransform(); }));
        StageEditors.Add(new NumericEditor("Scale X", stageDrawScale3D.X, -100, 100, 0.01,
            value => SetStageScale(0, (float)value)));
        StageEditors.Add(new NumericEditor("Scale Y", stageDrawScale3D.Y, -100, 100, 0.01,
            value => SetStageScale(1, (float)value)));
        StageEditors.Add(new NumericEditor("Scale Z", stageDrawScale3D.Z, -100, 100, 0.01,
            value => SetStageScale(2, (float)value)));
    }

    private void SetStageLocation(int component, float value)
    {
        if (component == 0) stageLocation.X = value;
        else if (component == 1) stageLocation.Y = value;
        else stageLocation.Z = value;
        CommitStageTransform();
    }

    private void SetStageRotation(int component, float value)
    {
        if (component == 0) stageRotation.X = value;
        else if (component == 1) stageRotation.Y = value;
        else stageRotation.Z = value;
        CommitStageTransform();
    }

    private void SetStageScale(int component, float value)
    {
        if (component == 0) stageDrawScale3D.X = value;
        else if (component == 1) stageDrawScale3D.Y = value;
        else stageDrawScale3D.Z = value;
        CommitStageTransform();
    }

    private void CommitStageTransform()
    {
        if (CurrentLoadedExport is null) return;
        CurrentLoadedExport.WriteProperty(CommonStructs.Vector3Prop(stageLocation, stageLocationPropertyName));
        CurrentLoadedExport.WriteProperty(CommonStructs.RotatorProp(
            Rotator.FromDegreesVector(stageRotation), "Rotation"));
        CurrentLoadedExport.WriteProperty(new FloatProperty(stageDrawScale, "DrawScale"));
        CurrentLoadedExport.WriteProperty(CommonStructs.Vector3Prop(stageDrawScale3D, "DrawScale3D"));
        stagePreview?.UpdateLocalToWorld(GetStageTransform());
        if (CameraFocusMode) PreviewActiveCamera();
        SceneViewer?.MarkRenderDirty();
    }

    private Matrix4x4 GetStageTransform() => ActorUtils.ComposeLocalToWorld(stageLocation,
        Rotator.FromDegreesVector(stageRotation), stageDrawScale * stageDrawScale3D);

    private static ExportEntry FindCameraPropertyOwner(ExportEntry stage)
    {
        var visited = new HashSet<ExportEntry>();
        for (ExportEntry current = stage; current is not null && visited.Add(current);
             current = current.Archetype as ExportEntry)
        {
            if (current.GetProperty<ArrayProperty<StructProperty>>("m_aCameraList") is not null)
                return current;
        }
        return null;
    }

    private static ExportEntry FindAttachedSkeletalMesh(ExportEntry stage)
    {
        var visited = new HashSet<ExportEntry>();
        var roots = new List<ExportEntry>();
        for (ExportEntry current = stage; current is not null && !roots.Contains(current);
             current = current.Archetype as ExportEntry)
            roots.Add(current);

        foreach (ExportEntry root in roots)
        {
            IEnumerable<ExportEntry> candidates = root.FileRef.Exports
                .Where(export => export == root || export.IsDescendantOf(root))
                .Prepend(root);
            foreach (ExportEntry candidate in candidates)
            {
                if (!visited.Add(candidate)) continue;
                if (candidate.ClassName == "SkeletalMesh") return candidate;

                foreach (ObjectProperty objectProperty in candidate.GetProperties().OfType<ObjectProperty>())
                {
                    if (objectProperty.ResolveToEntry(candidate.FileRef) is not ExportEntry referenced) continue;
                    if (referenced.ClassName == "SkeletalMesh") return referenced;
                    if (referenced.IsA("SkeletalMeshComponent")
                        && referenced.GetProperty<ObjectProperty>("SkeletalMesh")?.ResolveToEntry(referenced.FileRef)
                        is ExportEntry componentMesh && componentMesh.ClassName == "SkeletalMesh")
                    {
                        return componentMesh;
                    }
                }

                if (candidate.GetProperty<ObjectProperty>("SkeletalMesh")?.ResolveToEntry(candidate.FileRef)
                    is ExportEntry directMesh && directMesh.ClassName == "SkeletalMesh")
                {
                    return directMesh;
                }
            }
        }
        return null;
    }

    private void BuildStagePreview()
    {
        DisposeStagePreview();
        if (meshBinary?.LODModels is not { Length: > 0 }) return;
        try
        {
            stagePreview = new ModelPreview<WorldVertex>(RenderContext, meshBinary, loadOnlyFirstLod: true);
            stagePreview.UpdateLocalToWorld(GetStageTransform());
            if (stagePreview.LODs is { Count: > 0 })
            {
                Mesh<WorldVertex> sourceMesh = stagePreview.LODs[0].Mesh;
                BuildStageWireframeEdges(sourceMesh);
            }
        }
        catch (Exception exception)
        {
            stageWireframeEdges.Clear();
            stageTrianglesByBone.Clear();
            stagePreview?.Dispose();
            stagePreview = null;
            SceneStatus = $"RefSkeleton loaded, but the stage wireframe could not be built: {exception.Message}";
        }
    }

    private void DisposeStagePreview()
    {
        stageWireframeEdges.Clear();
        stageTrianglesByBone.Clear();
        stagePreview?.Dispose();
        stagePreview = null;
    }

    private void BuildStageWireframeEdges(Mesh<WorldVertex> sourceMesh)
    {
        stageWireframeEdges.Clear();
        stageTrianglesByBone.Clear();
        var uniqueEdges = new HashSet<(uint Start, uint End)>();

        void AddEdge(uint start, uint end)
        {
            if (start == end || start >= sourceMesh.Vertices.Count || end >= sourceMesh.Vertices.Count) return;
            if (start > end) (start, end) = (end, start);
            if (!uniqueEdges.Add((start, end))) return;
            stageWireframeEdges.Add((sourceMesh.Vertices[(int)start].Position,
                sourceMesh.Vertices[(int)end].Position));
        }

        for (int triangleIndex = 0; triangleIndex < sourceMesh.Triangles.Count; triangleIndex++)
        {
            Triangle triangle = sourceMesh.Triangles[triangleIndex];
            if (triangle.Vertex1 >= sourceMesh.Vertices.Count
                || triangle.Vertex2 >= sourceMesh.Vertices.Count
                || triangle.Vertex3 >= sourceMesh.Vertices.Count)
                continue;

            var triangleVertices = (sourceMesh.Vertices[(int)triangle.Vertex1].Position,
                sourceMesh.Vertices[(int)triangle.Vertex2].Position,
                sourceMesh.Vertices[(int)triangle.Vertex3].Position);
            int boneIndex = ResolveTriangleBoneIndex(triangleIndex, triangle, triangleVertices);
            if (!stageTrianglesByBone.TryGetValue(boneIndex, out List<(Vector3 A, Vector3 B, Vector3 C)> triangles))
            {
                triangles = [];
                stageTrianglesByBone.Add(boneIndex, triangles);
            }
            triangles.Add(triangleVertices);
            AddEdge(triangle.Vertex1, triangle.Vertex2);
            AddEdge(triangle.Vertex2, triangle.Vertex3);
            AddEdge(triangle.Vertex3, triangle.Vertex1);
        }
    }

    private int ResolveTriangleBoneIndex(int triangleIndex, Triangle triangle,
        (Vector3 A, Vector3 B, Vector3 C) vertices)
    {
        if (meshBinary?.LODModels is not { Length: > 0 } || meshBinary.RefSkeleton is null) return -1;
        StaticLODModel lod = meshBinary.LODModels[0];
        uint firstIndex = (uint)(triangleIndex * 3);
        SkelMeshSection section = null;
        foreach (SkelMeshSection candidate in lod.Sections ?? [])
        {
            uint sectionEnd = candidate.BaseIndex + (uint)Math.Max(candidate.NumTriangles, 0) * 3;
            if (firstIndex >= candidate.BaseIndex && firstIndex < sectionEnd)
            {
                section = candidate;
                break;
            }
        }

        if (section is not null && lod.Chunks is not null && section.ChunkIndex < lod.Chunks.Length)
        {
            SkelMeshChunk chunk = lod.Chunks[section.ChunkIndex];
            var accumulatedWeights = new int[meshBinary.RefSkeleton.Length];

            void AccumulateVertex(uint vertexIndex)
            {
                Influences influenceBones;
                Influences influenceWeights;
                if (meshExport.Game == MEGame.ME1)
                {
                    if (lod.ME1VertexBufferGPUSkin is null || vertexIndex >= lod.ME1VertexBufferGPUSkin.Length) return;
                    SoftSkinVertex vertex = lod.ME1VertexBufferGPUSkin[vertexIndex];
                    influenceBones = vertex.InfluenceBones;
                    influenceWeights = vertex.InfluenceWeights;
                }
                else
                {
                    if (lod.VertexBufferGPUSkin?.VertexData is not { } vertexData || vertexIndex >= vertexData.Length) return;
                    GPUSkinVertex vertex = vertexData[vertexIndex];
                    influenceBones = vertex.InfluenceBones;
                    influenceWeights = vertex.InfluenceWeights;
                }

                for (int influenceIndex = 0; influenceIndex < 4; influenceIndex++)
                {
                    int localBoneIndex = influenceBones[influenceIndex];
                    int weight = influenceWeights[influenceIndex];
                    if (weight == 0 || chunk.BoneMap is null || localBoneIndex >= chunk.BoneMap.Length) continue;
                    int globalBoneIndex = chunk.BoneMap[localBoneIndex];
                    if ((uint)globalBoneIndex < accumulatedWeights.Length)
                        accumulatedWeights[globalBoneIndex] += weight;
                }
            }

            AccumulateVertex(triangle.Vertex1);
            AccumulateVertex(triangle.Vertex2);
            AccumulateVertex(triangle.Vertex3);
            int strongestBone = -1;
            int strongestWeight = 0;
            for (int boneIndex = 0; boneIndex < accumulatedWeights.Length; boneIndex++)
            {
                if (accumulatedWeights[boneIndex] <= strongestWeight) continue;
                strongestWeight = accumulatedWeights[boneIndex];
                strongestBone = boneIndex;
            }
            if (strongestBone >= 0) return strongestBone;
        }

        Vector3 centroid = (vertices.A + vertices.B + vertices.C) / 3;
        Vector3 worldCentroid = Vector3.Transform(centroid, GetStageTransform());
        int closestBone = -1;
        float closestDistanceSquared = float.MaxValue;
        for (int boneIndex = 0; boneIndex < meshBinary.RefSkeleton.Length; boneIndex++)
        {
            float distanceSquared = Vector3.DistanceSquared(worldCentroid,
                GetBoneWorldTransform(boneIndex).Translation);
            if (distanceSquared >= closestDistanceSquared) continue;
            closestDistanceSquared = distanceSquared;
            closestBone = boneIndex;
        }
        return closestBone;
    }

    private void RebuildCameras(NameReference? selectName = null)
    {
        NameReference? desired = selectName ?? SelectedCamera?.Name;
        Cameras.Clear();
        var propertyEntries = new List<(NameReference Name, StructProperty Entry)>();
        foreach (StructProperty camera in cameraArray?.Values ?? [])
        {
            if (camera.GetProp<NameProperty>("nmCameraTag")?.Value is { } name
                && !string.IsNullOrWhiteSpace(name.Name) && name != "None")
            {
                propertyEntries.Add((name, camera));
            }
        }

        var usedBinaryEntries = new HashSet<PropertyCollection>();
        foreach ((NameReference name, StructProperty propertyEntry) in propertyEntries)
        {
            PropertyCollection binaryEntry = stageBinary?.CameraList
                .Where(pair => pair.Key == name && !usedBinaryEntries.Contains(pair.Value))
                .Select(pair => pair.Value).FirstOrDefault();
            if (binaryEntry is not null) usedBinaryEntries.Add(binaryEntry);
            Cameras.Add(new CameraItem(this, name, propertyEntry, binaryEntry));
        }
        foreach ((NameReference name, PropertyCollection binaryEntry) in stageBinary?.CameraList ?? [])
        {
            if (!usedBinaryEntries.Contains(binaryEntry))
            {
                Cameras.Add(new CameraItem(this, name, null, binaryEntry));
            }
        }

        SelectedCamera = desired is { } desiredName
            ? Cameras.FirstOrDefault(item => item.Name == desiredName) ?? Cameras.FirstOrDefault()
            : Cameras.FirstOrDefault();
        foreach (BoneItem bone in Bones) bone.RefreshFromBone();
        OnPropertyChanged(nameof(Cameras));
    }

    internal bool HasCamera(NameReference boneName) => Cameras.Any(camera => camera.Name == boneName);

    private static void SetProperty(PropertyCollection properties, Property property)
    {
        if (properties is null) return;
        properties.RemoveAll(item => item is NoneProperty);
        properties.AddOrReplaceProp(property);
    }

    private void SetCameraFloat(CameraItem camera, string propertyName, float value)
    {
        if (camera is null) return;
        if (camera.PropertyEntry is not null)
            SetProperty(camera.PropertyEntry.Properties, new FloatProperty(value, propertyName));
        if (camera.BinaryEntry is not null)
            SetProperty(camera.BinaryEntry, new FloatProperty(value, propertyName));
        CommitCameraLists(camera.PropertyEntry is not null, camera.BinaryEntry is not null);
        if (CameraFocusMode && (ReferenceEquals(camera, SelectedCamera)
                                || focusUsesSelectedBone && SelectedBone is not null
                                && camera.Name == meshBinary.RefSkeleton[SelectedBone.Index].Name))
            PreviewActiveCamera();
    }

    private void SetCameraBool(CameraItem camera, string propertyName, bool value)
    {
        if (camera is null) return;
        if (camera.PropertyEntry is not null)
            SetProperty(camera.PropertyEntry.Properties, new BoolProperty(value, propertyName));
        if (camera.BinaryEntry is not null)
            SetProperty(camera.BinaryEntry, new BoolProperty(value, propertyName));
        CommitCameraLists(camera.PropertyEntry is not null, camera.BinaryEntry is not null);
        if (CameraFocusMode && (ReferenceEquals(camera, SelectedCamera)
                                || focusUsesSelectedBone && SelectedBone is not null
                                && camera.Name == meshBinary.RefSkeleton[SelectedBone.Index].Name))
            PreviewActiveCamera();
    }

    private void CommitCameraLists(bool propertyChanged = true, bool binaryChanged = true)
    {
        if (CurrentLoadedExport is null) return;
        if (propertyChanged) (cameraPropertyOwner ?? CurrentLoadedExport).WriteProperty(cameraArray);
        if (binaryChanged) CurrentLoadedExport.WriteBinary(stageBinary);
        SceneStatus = $"{Bones.Count} bone(s), {Cameras.Count} camera(s); camera list updated.";
        SceneViewer?.MarkRenderDirty();
    }

    private static PropertyCollection CreateCameraProperties(bool includeTag, NameReference name,
        CameraItem template = null)
    {
        var properties = new PropertyCollection();
        if (includeTag) properties.Add(new NameProperty(name, "nmCameraTag"));
        properties.Add(new FloatProperty(template?.GetValue("FOV", 60) ?? 60, "fFov"));
        properties.Add(new FloatProperty(template?.GetValue("Near plane", 10) ?? 10, "fNearPlane"));
        properties.Add(new FloatProperty(template?.GetValue("Height delta") ?? 0, "fHeightDelta"));
        properties.Add(new FloatProperty(template?.GetValue("Pitch delta") ?? 0, "fPitchDelta"));
        properties.Add(new FloatProperty(template?.GetValue("Yaw delta") ?? 0, "fYawDelta"));
        properties.Add(new BoolProperty(template?.DisableHeightAdjustment ?? false, "bDisableHeightAdjustment"));
        return properties;
    }

    private NameReference CreateUniqueName(NameReference source)
    {
        var names = new HashSet<NameReference>(Cameras.Select(camera => camera.Name));
        if (meshBinary?.RefSkeleton is not null)
            names.UnionWith(meshBinary.RefSkeleton.Select(bone => bone.Name));
        int number = Math.Max(1, source.Number + 1);
        var candidate = new NameReference(source.Name, number);
        while (names.Contains(candidate)) candidate = new NameReference(source.Name, ++number);
        return candidate;
    }

    private void AddCamera(NameReference name, CameraItem template = null, bool cloneExactProperties = false)
    {
        if (CurrentLoadedExport is null || stageBinary is null) return;
        CurrentLoadedExport.FileRef.FindNameOrAdd(name.Name);

        StructProperty propertyEntry;
        if (cloneExactProperties && template?.PropertyEntry is not null)
        {
            propertyEntry = template.PropertyEntry.DeepClone();
            SetProperty(propertyEntry.Properties, new NameProperty(name, "nmCameraTag"));
        }
        else
        {
            propertyEntry = new StructProperty("BioStageCamera", CreateCameraProperties(true, name, template));
        }
        cameraArray.Add(propertyEntry);

        PropertyCollection binaryEntry = cloneExactProperties && template?.BinaryEntry is not null
            ? template.BinaryEntry.DeepClone()
            : CreateCameraProperties(false, name, template);
        binaryEntry.RemoveAll(property => property is NoneProperty || property.Name == "nmCameraTag");
        stageBinary.CameraList.Add(name, binaryEntry);
        CommitCameraLists();
        RebuildCameras(name);
    }

    private void AddSelectedBoneToCameraList_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBone is null || meshBinary is null) return;
        NameReference name = meshBinary.RefSkeleton[SelectedBone.Index].Name;
        if (HasCamera(name))
        {
            SelectedCamera = Cameras.First(camera => camera.Name == name);
            SceneStatus = $"{name.Instanced} is already in the camera list.";
            return;
        }
        AddCamera(name);
    }

    private void CloneCamera_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCamera is null) return;
        NameReference newName = CreateUniqueName(SelectedCamera.Name);
        AddCamera(newName, SelectedCamera, cloneExactProperties: true);
    }

    private void RemoveCamera_Click(object sender, RoutedEventArgs e)
    {
        CameraItem camera = SelectedCamera;
        if (camera is null) return;
        if (camera.PropertyEntry is not null) cameraArray.Remove(camera.PropertyEntry);
        if (camera.BinaryEntry is not null)
        {
            stageBinary.CameraList = new UMultiMap<NameReference, PropertyCollection>(stageBinary.CameraList
                .Where(pair => !ReferenceEquals(pair.Value, camera.BinaryEntry)));
        }
        CommitCameraLists(camera.PropertyEntry is not null, camera.BinaryEntry is not null);
        RebuildCameras();
    }

    private void RemoveCamerasByName(NameReference name)
    {
        int propertyRemoved = cameraArray?.Values.RemoveAll(camera =>
            camera.GetProp<NameProperty>("nmCameraTag")?.Value == name) ?? 0;
        int binaryBefore = stageBinary?.CameraList.Count ?? 0;
        if (stageBinary is not null)
        {
            stageBinary.CameraList = new UMultiMap<NameReference, PropertyCollection>(stageBinary.CameraList
                .Where(pair => pair.Key != name));
        }
        bool binaryChanged = stageBinary is not null && stageBinary.CameraList.Count != binaryBefore;
        if (propertyRemoved > 0 || binaryChanged)
        {
            CommitCameraLists(propertyRemoved > 0, binaryChanged);
            RebuildCameras();
        }
    }

    private void ApplyCameraName_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCamera is null || string.IsNullOrWhiteSpace(SelectedCamera.NameText)) return;
        NameReference newName;
        try
        {
            newName = NameReference.FromInstancedString(SelectedCamera.NameText.Trim());
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Invalid camera name: {exception.Message}");
            return;
        }
        if (newName == SelectedCamera.Name) return;
        if (Cameras.Any(camera => !ReferenceEquals(camera, SelectedCamera) && camera.Name == newName))
        {
            MessageBox.Show($"A camera named {newName.Instanced} already exists.");
            return;
        }

        CameraItem selected = SelectedCamera;
        NameReference oldName = selected.Name;
        CurrentLoadedExport.FileRef.FindNameOrAdd(newName.Name);
        if (selected.PropertyEntry is not null)
            SetProperty(selected.PropertyEntry.Properties, new NameProperty(newName, "nmCameraTag"));
        if (selected.BinaryEntry is not null)
        {
            stageBinary.CameraList = new UMultiMap<NameReference, PropertyCollection>(stageBinary.CameraList.Select(pair =>
                ReferenceEquals(pair.Value, selected.BinaryEntry)
                    ? new KeyValuePair<NameReference, PropertyCollection>(newName, pair.Value)
                    : pair));
        }
        selected.SetName(newName);
        CommitCameraLists(selected.PropertyEntry is not null, selected.BinaryEntry is not null);
        RebuildCameras(newName);
        SceneStatus = $"Renamed camera {oldName.Instanced} to {newName.Instanced}.";
    }

    private void CameraListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedCamera is null) return;
        focusUsesSelectedBone = false;
        CameraFocusMode = true;
        PreviewSelectedCamera();
    }

    private void RebuildBones(int selectedIndex = -1)
    {
        UnregisterBones();
        Bones.Clear();
        if (meshBinary?.RefSkeleton is not null)
        {
            for (int index = 0; index < meshBinary.RefSkeleton.Length; index++)
                Bones.Add(new BoneItem(this, index));
        }
        RegisterBones();
        SelectedBone = Bones.Count == 0 ? null : Bones[Math.Clamp(selectedIndex < 0 ? 0 : selectedIndex, 0, Bones.Count - 1)];
    }

    private void RegisterBones()
    {
        foreach (BoneItem bone in Bones)
        {
            if (bone.HitID == 0) RenderContext.AddHitProxy(bone);
        }
    }

    private void UnregisterBones()
    {
        foreach (BoneItem bone in Bones)
            RenderContext.RemoveHitProxy(bone);
    }

    internal IReadOnlyList<BoneParentOption> GetParentOptions(int boneIndex)
    {
        var options = new List<BoneParentOption> { new(-1, "Root (-1)") };
        if (meshBinary?.RefSkeleton is null) return options;
        for (int index = 0; index < meshBinary.RefSkeleton.Length; index++)
        {
            if (index == boneIndex || IsDescendantOf(index, boneIndex)) continue;
            options.Add(new BoneParentOption(index, $"{index}: {meshBinary.RefSkeleton[index].Name.Instanced}"));
        }
        return options;
    }

    private bool IsDescendantOf(int possibleDescendant, int possibleAncestor)
    {
        if (meshBinary?.RefSkeleton is null) return false;
        var visited = new HashSet<int>();
        int current = possibleDescendant;
        while (current >= 0 && current < meshBinary.RefSkeleton.Length && visited.Add(current))
        {
            current = meshBinary.RefSkeleton[current].ParentIndex;
            if (current == possibleAncestor) return true;
        }
        return false;
    }

    internal void ChangeBoneParent(BoneItem item, int parentIndex)
    {
        if (meshBinary?.RefSkeleton is null || item is null) return;
        if (parentIndex == item.Index || parentIndex >= meshBinary.RefSkeleton.Length
            || parentIndex < -1 || IsDescendantOf(parentIndex, item.Index))
        {
            item.RefreshFromBone();
            return;
        }
        MeshBone bone = meshBinary.RefSkeleton[item.Index];
        if (bone.ParentIndex == parentIndex) return;
        bone.ParentIndex = parentIndex;
        RecalculateSkeletonHierarchy();
        OnBoneChanged(item);
        foreach (BoneItem boneItem in Bones) boneItem.RefreshFromBone();
    }

    private void RecalculateSkeletonHierarchy()
    {
        if (meshBinary?.RefSkeleton is null) return;
        foreach (MeshBone bone in meshBinary.RefSkeleton) bone.NumChildren = 0;
        int maxDepth = 0;
        for (int index = 0; index < meshBinary.RefSkeleton.Length; index++)
        {
            int parent = meshBinary.RefSkeleton[index].ParentIndex;
            if (parent >= 0 && parent < meshBinary.RefSkeleton.Length && parent != index)
                meshBinary.RefSkeleton[parent].NumChildren++;

            int depth = 1;
            var visited = new HashSet<int> { index };
            while (parent >= 0 && parent < meshBinary.RefSkeleton.Length && visited.Add(parent))
            {
                depth++;
                parent = meshBinary.RefSkeleton[parent].ParentIndex;
            }
            maxDepth = Math.Max(maxDepth, depth);
        }
        meshBinary.SkeletalDepth = maxDepth;
        RebuildNameIndexMap();
    }

    private void RebuildNameIndexMap()
    {
        if (meshBinary?.RefSkeleton is null) return;
        meshBinary.NameIndexMap ??= [];
        meshBinary.NameIndexMap.Clear();
        for (int index = 0; index < meshBinary.RefSkeleton.Length; index++)
            meshBinary.NameIndexMap.Add(meshBinary.RefSkeleton[index].Name, index);
    }

    internal void OnBoneChanged(BoneItem item)
    {
        ScheduleMeshWrite();
        if (CameraFocusMode && (focusUsesSelectedBone && ReferenceEquals(item, SelectedBone)
                                || !focusUsesSelectedBone && SelectedCamera is not null
                                && meshBinary.RefSkeleton[item.Index].Name == SelectedCamera.Name))
            PreviewActiveCamera();
        SceneViewer?.MarkRenderDirty();
    }

    private void ScheduleMeshWrite()
    {
        if (meshExport is null || meshBinary is null) return;
        meshWritePending = true;
        meshWriteTimer.Stop();
        meshWriteTimer.Start();
    }

    private void MeshWriteTimer_Tick(object sender, EventArgs e)
    {
        meshWriteTimer.Stop();
        FlushMeshWrite();
    }

    private void FlushMeshWrite()
    {
        if (!meshWritePending || meshExport is null || meshBinary is null) return;
        meshWritePending = false;
        meshExport.WriteBinary(meshBinary);
        SceneStatus = $"{Bones.Count} bone(s), {Cameras.Count} camera(s); RefSkeleton updated.";
    }

    private void CloneBone_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBone is null || meshBinary?.RefSkeleton is null) return;
        FlushMeshWrite();
        MeshBone source = meshBinary.RefSkeleton[SelectedBone.Index];
        NameReference newName = CreateUniqueName(source.Name);
        CurrentLoadedExport.FileRef.FindNameOrAdd(newName.Name);
        var clone = new MeshBone
        {
            Name = newName,
            Flags = source.Flags,
            Orientation = source.Orientation,
            Position = source.Position,
            NumChildren = 0,
            ParentIndex = source.ParentIndex,
            BoneColor = source.BoneColor
        };
        meshBinary.RefSkeleton = [.. meshBinary.RefSkeleton, clone];
        RecalculateSkeletonHierarchy();
        meshExport.WriteBinary(meshBinary);
        RebuildBones(meshBinary.RefSkeleton.Length - 1);
        SceneStatus = $"Cloned {source.Name.Instanced} as {newName.Instanced}.";
        SceneViewer.MarkRenderDirty();
    }

    private bool IsBoneUsedByLod(int boneIndex)
    {
        if (meshBinary?.LODModels is null) return false;
        foreach (StaticLODModel lod in meshBinary.LODModels)
        {
            if (lod.ActiveBoneIndices?.Contains((ushort)boneIndex) == true
                || lod.RequiredBones?.Contains((byte)boneIndex) == true
                || lod.Chunks?.Any(chunk => chunk.BoneMap?.Contains((ushort)boneIndex) == true) == true)
                return true;
        }
        return false;
    }

    private void RemoveBone_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBone is null || meshBinary?.RefSkeleton is null) return;
        int removeIndex = SelectedBone.Index;
        if (IsBoneUsedByLod(removeIndex))
        {
            MessageBox.Show("This bone is referenced by a mesh LOD and cannot be removed safely. Camera/dummy bones that are not used for skinning can be removed.");
            return;
        }
        if (removeIndex == 0 && meshBinary.RefSkeleton.Length > 1)
        {
            MessageBox.Show("The root bone cannot be removed while other bones remain.");
            return;
        }

        FlushMeshWrite();
        MeshBone removed = meshBinary.RefSkeleton[removeIndex];
        int replacementParent = removed.ParentIndex;
        var remaining = meshBinary.RefSkeleton.Where((_, index) => index != removeIndex).ToArray();
        foreach (MeshBone bone in remaining)
        {
            if (bone.ParentIndex == removeIndex) bone.ParentIndex = replacementParent;
            if (bone.ParentIndex > removeIndex) bone.ParentIndex--;
        }
        meshBinary.RefSkeleton = remaining;
        RemapLodBoneIndicesAfterRemoval(removeIndex);
        if (meshBinary.PerPolyBoneKDOPs?.Length == remaining.Length + 1)
            meshBinary.PerPolyBoneKDOPs = meshBinary.PerPolyBoneKDOPs.Where((_, index) => index != removeIndex).ToArray();
        if (meshBinary.BoneBreakNames is not null)
            meshBinary.BoneBreakNames = meshBinary.BoneBreakNames
                .Where(name => !string.Equals(name, removed.Name.Instanced, StringComparison.OrdinalIgnoreCase)).ToArray();
        RecalculateSkeletonHierarchy();
        meshExport.WriteBinary(meshBinary);
        RemoveCamerasByName(removed.Name);
        RebuildBones(Math.Min(removeIndex, remaining.Length - 1));
        if (stagePreview?.LODs is { Count: > 0 }) BuildStageWireframeEdges(stagePreview.LODs[0].Mesh);
        SceneStatus = $"Removed bone {removed.Name.Instanced} and any matching camera entries.";
        SceneViewer.MarkRenderDirty();
    }

    private void RemapLodBoneIndicesAfterRemoval(int removeIndex)
    {
        if (meshBinary?.LODModels is null) return;
        foreach (StaticLODModel lod in meshBinary.LODModels)
        {
            if (lod.ActiveBoneIndices is not null)
                for (int i = 0; i < lod.ActiveBoneIndices.Length; i++)
                    if (lod.ActiveBoneIndices[i] > removeIndex) lod.ActiveBoneIndices[i]--;
            if (lod.RequiredBones is not null)
                for (int i = 0; i < lod.RequiredBones.Length; i++)
                    if (lod.RequiredBones[i] > removeIndex) lod.RequiredBones[i]--;
            foreach (SkelMeshChunk chunk in lod.Chunks ?? [])
                for (int i = 0; i < (chunk.BoneMap?.Length ?? 0); i++)
                    if (chunk.BoneMap[i] > removeIndex) chunk.BoneMap[i]--;
        }
    }

    private void ApplyBoneName_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBone is null || string.IsNullOrWhiteSpace(SelectedBone.NameText)) return;
        NameReference newName;
        try
        {
            newName = NameReference.FromInstancedString(SelectedBone.NameText.Trim());
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Invalid bone name: {exception.Message}");
            return;
        }
        NameReference oldName = meshBinary.RefSkeleton[SelectedBone.Index].Name;
        if (oldName == newName) return;
        if (meshBinary.RefSkeleton.Where((_, index) => index != SelectedBone.Index)
            .Any(bone => bone.Name == newName))
        {
            MessageBox.Show($"A bone named {newName.Instanced} already exists.");
            return;
        }
        CurrentLoadedExport.FileRef.FindNameOrAdd(newName.Name);
        SelectedBone.ApplyName(newName);
        RenameCameraTags(oldName, newName);
        RebuildNameIndexMap();
        FlushMeshWrite();
        meshExport.WriteBinary(meshBinary);
        RebuildCameras(newName);
        SceneStatus = $"Renamed bone {oldName.Instanced} to {newName.Instanced}; matching camera tags were updated.";
        SceneViewer.MarkRenderDirty();
    }

    private void RenameCameraTags(NameReference oldName, NameReference newName)
    {
        bool propertyChanged = false;
        foreach (StructProperty camera in cameraArray?.Values ?? [])
        {
            NameProperty tag = camera.GetProp<NameProperty>("nmCameraTag");
            if (tag?.Value != oldName) continue;
            tag.Value = newName;
            propertyChanged = true;
        }
        bool binaryChanged = stageBinary?.CameraList.Any(pair => pair.Key == oldName) == true;
        if (binaryChanged)
        {
            stageBinary.CameraList = new UMultiMap<NameReference, PropertyCollection>(stageBinary.CameraList.Select(pair =>
                pair.Key == oldName
                    ? new KeyValuePair<NameReference, PropertyCollection>(newName, pair.Value)
                    : pair));
        }
        if (propertyChanged || binaryChanged) CommitCameraLists(propertyChanged, binaryChanged);
    }

    private Matrix4x4 GetBoneComponentTransform(int boneIndex)
    {
        if (meshBinary?.RefSkeleton is null || boneIndex < 0 || boneIndex >= meshBinary.RefSkeleton.Length)
            return Matrix4x4.Identity;
        var cache = new Dictionary<int, Matrix4x4>();
        var active = new HashSet<int>();
        return Resolve(boneIndex);

        Matrix4x4 Resolve(int index)
        {
            if (cache.TryGetValue(index, out Matrix4x4 cached)) return cached;
            if (!active.Add(index)) return Matrix4x4.Identity;
            MeshBone bone = meshBinary.RefSkeleton[index];
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(bone.Orientation))
                              * Matrix4x4.CreateTranslation(bone.Position);
            int parent = bone.ParentIndex;
            Matrix4x4 result = parent >= 0 && parent < meshBinary.RefSkeleton.Length && parent != index
                ? local * Resolve(parent)
                : local;
            active.Remove(index);
            cache[index] = result;
            return result;
        }
    }

    internal Matrix4x4 GetBoneWorldTransform(int boneIndex) =>
        GetBoneComponentTransform(boneIndex) * GetStageTransform();

    internal Rotator GetBoneWorldRotation(int boneIndex)
    {
        Matrix4x4 world = GetBoneWorldTransform(boneIndex);
        return Matrix4x4.Decompose(world, out _, out Quaternion rotation, out _)
            ? Rotator.FromQuaternion(Quaternion.Normalize(rotation))
            : new Rotator(0, 0, 0);
    }

    private Matrix4x4 GetBoneParentWorldTransform(int boneIndex)
    {
        int parent = meshBinary.RefSkeleton[boneIndex].ParentIndex;
        return parent >= 0 && parent < meshBinary.RefSkeleton.Length
            ? GetBoneComponentTransform(parent) * GetStageTransform()
            : GetStageTransform();
    }

    internal void SetBoneWorldLocation(BoneItem item, Vector3 worldLocation)
    {
        Matrix4x4 parentWorld = GetBoneParentWorldTransform(item.Index);
        if (!Matrix4x4.Invert(parentWorld, out Matrix4x4 worldToParent)) return;
        meshBinary.RefSkeleton[item.Index].Position = Vector3.Transform(worldLocation, worldToParent);
        item.RefreshFromBone();
        OnBoneChanged(item);
    }

    internal void SetBoneWorldRotation(BoneItem item, Rotator worldRotation)
    {
        Matrix4x4 parentWorld = GetBoneParentWorldTransform(item.Index);
        if (!Matrix4x4.Decompose(parentWorld, out _, out Quaternion parentRotation, out _)) return;
        Matrix4x4 desiredWorld = worldRotation.ToRotationMatrix();
        Matrix4x4 parentRotationMatrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(parentRotation));
        if (!Matrix4x4.Invert(parentRotationMatrix, out Matrix4x4 inverseParentRotation)) return;
        Quaternion localRotation = Quaternion.CreateFromRotationMatrix(desiredWorld * inverseParentRotation);
        meshBinary.RefSkeleton[item.Index].Orientation = Quaternion.Normalize(localRotation);
        item.RefreshFromBone();
        OnBoneChanged(item);
    }

    private void PreviewSelectedCamera()
    {
        if (!CameraFocusMode || SelectedCamera is null || meshBinary?.RefSkeleton is null) return;
        int boneIndex = Array.FindIndex(meshBinary.RefSkeleton, bone => bone.Name == SelectedCamera.Name);
        if (boneIndex < 0)
        {
            SceneStatus = $"Camera {SelectedCamera.DisplayName} has no matching RefSkeleton bone.";
            return;
        }

        PreviewCameraFromBone(Bones.FirstOrDefault(bone => bone.Index == boneIndex), SelectedCamera);
    }

    private void PreviewActiveCamera()
    {
        if (focusUsesSelectedBone) PreviewSelectedBoneCamera();
        else PreviewSelectedCamera();
    }

    private void PreviewSelectedBoneCamera()
    {
        if (!CameraFocusMode || SelectedBone is null || meshBinary?.RefSkeleton is null) return;
        CameraItem cameraSettings = Cameras.FirstOrDefault(camera =>
            camera.Name == meshBinary.RefSkeleton[SelectedBone.Index].Name);
        PreviewCameraFromBone(SelectedBone, cameraSettings);
    }

    private void PreviewCameraFromBone(BoneItem bone, CameraItem cameraSettings)
    {
        if (bone is null || meshBinary?.RefSkeleton is null) return;

        Matrix4x4 world = GetBoneWorldTransform(bone.Index);
        Vector3 rotation = GetBoneWorldRotation(bone.Index).GetDegreesVector();
        Vector3 location = world.Translation;
        if (cameraSettings is not null)
        {
            location.Z += cameraSettings.GetValue("Height delta");
            rotation.Y += cameraSettings.GetValue("Pitch delta");
            rotation.Z += cameraSettings.GetValue("Yaw delta");
        }
        const float degreesToRadians = MathF.PI / 180f;
        RenderContext.Camera.Position = location;
        RenderContext.Camera.Roll = rotation.X * degreesToRadians;
        RenderContext.Camera.Pitch = rotation.Y * degreesToRadians;
        RenderContext.Camera.Yaw = rotation.Z * degreesToRadians;
        if (cameraSettings is not null)
        {
            RenderContext.Camera.FOV = Math.Clamp(cameraSettings.GetValue("FOV", 60), 1, 179) * degreesToRadians;
            RenderContext.Camera.ZNear = Math.Max(0.01f, cameraSettings.GetValue("Near plane", 10));
        }
        RenderContext.Camera.FocusDepth = 0;
        SceneStatus = cameraSettings is null
            ? $"Viewing from bone {bone.DisplayName}; no matching camera settings were found."
            : $"Viewing through {cameraSettings.DisplayName} using bone {bone.Index}.";
        SceneViewer.MarkRenderDirty();
    }

    private void SetWidgetMode(EWidgetMode mode)
    {
        RenderContext.TransformWidget.Mode = mode;
        RenderContext.TransformWidget.VisibleAxes = EWidgetAxis.XYZ;
        RenderContext.TransformWidget.CurrentAxis = EWidgetAxis.None;
        SceneViewer?.MarkRenderDirty();
    }

    private void TranslateMode_Checked(object sender, RoutedEventArgs e) => SetWidgetMode(EWidgetMode.Translate);
    private void RotateMode_Checked(object sender, RoutedEventArgs e) => SetWidgetMode(EWidgetMode.Rotate);

    private void FrameCamera_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBone is null)
        {
            SceneStatus = "Select a RefSkeleton bone to frame its camera.";
            return;
        }

        CameraItem matchingCamera = Cameras.FirstOrDefault(camera =>
            camera.Name == meshBinary.RefSkeleton[SelectedBone.Index].Name);
        if (matchingCamera is not null) SelectedCamera = matchingCamera;
        focusUsesSelectedBone = true;
        CameraFocusMode = true;
        PreviewSelectedBoneCamera();
    }

    private void FocusStage_Click(object sender, RoutedEventArgs e) => FocusStage();

    private void FocusStage()
    {
        if (stagePreview?.LODs is not { Count: > 0 }) return;
        BoxSphereBounds bounds = stagePreview.LODs[0].Mesh.BaseBounds.TransformBy(GetStageTransform());
        FocusPoint(bounds.Origin, MathF.Max(bounds.SphereRadius, 100));
    }

    private void FocusBone(BoneItem bone)
    {
        if (bone is null) return;
        FocusPoint(bone.LocalToWorld.Translation, 120);
    }

    private void FocusPoint(Vector3 point, float radius)
    {
        CameraFocusMode = false;
        float distance = MathF.Max(radius, 50) * 2;
        (float sin, float cos) = MathF.SinCos(MathF.PI / 2.5f);
        RenderContext.Camera.Position = new Vector3(point.X, point.Y + sin * distance, point.Z + cos * distance);
        RenderContext.Camera.OrientTowards(point);
        RenderContext.Camera.FocusDepth = 0;
        SceneViewer.MarkRenderDirty();
        SceneViewer.Focus();
    }

    private void BoneListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => FocusBone(SelectedBone);

    private void BoneListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(BoneListBox, e.OriginalSource as DependencyObject)
            is ListBoxItem { DataContext: BoneItem bone })
            SelectedBone = bone;
    }

    private void BioStageEditor_Loaded(object sender, RoutedEventArgs e)
    {
        AttachEvents();
        SceneViewer.SetShouldRender(true);
        _ = RestoreSessionLevelsAsync();
    }

    private void BioStageEditor_Unloaded(object sender, RoutedEventArgs e) => SceneViewer.SetShouldRender(false);

    private void AttachEvents()
    {
        if (eventsAttached) return;
        RenderContext.RenderScene += RenderScene;
        RenderContext.SelectHitProxy += SelectHitProxy;
        RenderContext.RightClickHitProxy += RightClickHitProxy;
        RenderContext.SelectActor += IgnoreActorSelection;
        RenderContext.RightClickActor += IgnoreActorRightClick;
        eventsAttached = true;
    }

    private void DetachEvents()
    {
        if (!eventsAttached) return;
        RenderContext.RenderScene -= RenderScene;
        RenderContext.SelectHitProxy -= SelectHitProxy;
        RenderContext.RightClickHitProxy -= RightClickHitProxy;
        RenderContext.SelectActor -= IgnoreActorSelection;
        RenderContext.RightClickActor -= IgnoreActorRightClick;
        eventsAttached = false;
    }

    private void SelectHitProxy(IHitProxy hitProxy)
    {
        if (hitProxy is BoneItem bone) SelectBoneFromViewport(bone);
    }

    private void RightClickHitProxy(IHitProxy hitProxy)
    {
        if (hitProxy is not BoneItem bone) return;
        SelectBoneFromViewport(bone);
        var menu = new ContextMenu();
        var addCamera = new MenuItem { Header = "Add to Camera List" };
        addCamera.Click += AddSelectedBoneToCameraList_Click;
        menu.Items.Add(addCamera);
        menu.Items.Add(new Separator());
        var clone = new MenuItem { Header = "Clone Bone" };
        clone.Click += CloneBone_Click;
        menu.Items.Add(clone);
        var remove = new MenuItem { Header = "Remove Bone" };
        remove.Click += RemoveBone_Click;
        menu.Items.Add(remove);
        menu.PlacementTarget = SceneViewer;
        menu.IsOpen = true;
    }

    private void SelectBoneFromViewport(BoneItem bone)
    {
        SelectedBone = bone;
        DetailsTabs.SelectedItem = RefSkeletonTab;
        BoneListBox.ScrollIntoView(bone);
    }

    private void IgnoreActorSelection(ActorProxy actor) =>
        RenderContext.TransformWidget.Attach = CameraFocusMode ? null : SelectedBone;

    private void IgnoreActorRightClick(ActorProxy actor)
    {
    }

    private void RenderScene(object sender, EventArgs e)
    {
        MeshRenderContext.BoundsVisibilityTester visibility = RenderContext.CreateBoundsVisibilityTester();
        foreach (RenderPass pass in BackdropRenderPasses)
        {
            if (pass == RenderPass.Collision && !ShowCollision) continue;
            foreach (ActorProxy actor in RenderContext.DrawList_3D)
            {
                if (actor is EmitterActorProxy or SFXPointOfInterestProxy) continue;
                if (actor.IsVolume && !ShowVolumes) continue;
                if (actor.IsVolumetricMesh && !ShowVolumetrics) continue;
                if (!visibility.IsVisible(RenderContext.GetActorBounds(actor))) continue;
                int hitId = actor.HitID;
                RenderContext.CurrentHitTestId = new Vector3((hitId & 0xFF) / 255f,
                    ((hitId >> 8) & 0xFF) / 255f, ((hitId >> 16) & 0xFF) / 255f);
                actor.Render(RenderContext, pass);
            }
        }

        RenderStageWireframe();
        if (!CameraFocusMode) RenderSkeleton();
        RenderContext.DrawUI();
    }

    private void RenderStageWireframe()
    {
        if (stageWireframeEdges.Count == 0 && stageTrianglesByBone.Count == 0) return;
        Matrix4x4 localToWorld = GetStageTransform();
        foreach ((int boneIndex, List<(Vector3 A, Vector3 B, Vector3 C)> triangles) in stageTrianglesByBone)
        {
            BoneItem bone = (uint)boneIndex < Bones.Count ? Bones[boneIndex] : null;
            int hitId = bone?.HitID ?? 0;
            float alpha = ReferenceEquals(bone, SelectedBone) ? 0.55f : 0.35f;
            var faceBuilder = RenderContext.Primitives.BuildMesh(new Vector4(1, 1, 0, alpha), hitId, localToWorld);
            int vertexIndex = 0;
            foreach ((Vector3 a, Vector3 b, Vector3 c) in triangles)
            {
                faceBuilder.AddVertex(a);
                faceBuilder.AddVertex(b);
                faceBuilder.AddVertex(c);
                faceBuilder.AddTriangle(vertexIndex, vertexIndex + 1, vertexIndex + 2);
                vertexIndex += 3;
            }
        }

        var wireframeColor = new Vector4(1, 1, 0, 1);
        foreach ((Vector3 start, Vector3 end) in stageWireframeEdges)
        {
            RenderContext.Primitives.AddLine(Vector3.Transform(start, localToWorld),
                Vector3.Transform(end, localToWorld), wireframeColor, 0);
        }
    }

    private void RenderSkeleton()
    {
        if (meshBinary?.RefSkeleton is null) return;
        var boneColor = new Vector4(1, 1, 0, 1);
        for (int index = 0; index < Bones.Count; index++)
        {
            BoneItem item = Bones[index];
            Vector3 position = item.LocalToWorld.Translation;
            int parentIndex = meshBinary.RefSkeleton[index].ParentIndex;
            if (parentIndex >= 0 && parentIndex < Bones.Count)
                RenderContext.Primitives.AddLine(Bones[parentIndex].LocalToWorld.Translation, position,
                    boneColor, item.HitID);
            DrawBoneMarker(item, position);
        }
    }

    private void DrawBoneMarker(BoneItem item, Vector3 position)
    {
        float radius = ReferenceEquals(item, SelectedBone) ? 15 : 10;
        var boneColor = new Vector4(1, 1, 0, 1);
        bool isCamera = HasCamera(meshBinary.RefSkeleton[item.Index].Name);
        RenderContext.Primitives.AddLine(position - Vector3.UnitX * radius,
            position + Vector3.UnitX * radius, boneColor, item.HitID);
        RenderContext.Primitives.AddLine(position - Vector3.UnitY * radius,
            position + Vector3.UnitY * radius, boneColor, item.HitID);
        RenderContext.Primitives.AddLine(position - Vector3.UnitZ * radius,
            position + Vector3.UnitZ * radius, boneColor, item.HitID);

        if (!isCamera) return;
        Quaternion orientation = GetBoneWorldRotation(item.Index).ToQuaternion();
        Vector3 forward = Vector3.Transform(Vector3.UnitX, orientation);
        Vector3 right = Vector3.Transform(Vector3.UnitY, orientation);
        Vector3 up = Vector3.Transform(Vector3.UnitZ, orientation);
        RenderContext.Primitives.AddLine(position, position + forward * 55, new Vector4(1, 0.15f, 0.15f, 1), item.HitID);
        RenderContext.Primitives.AddLine(position, position + right * 40, new Vector4(0.15f, 1, 0.15f, 1), item.HitID);
        RenderContext.Primitives.AddLine(position, position + up * 40, new Vector4(0.2f, 0.45f, 1, 1), item.HitID);
    }

    private async void OpenLevel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDirectories.GetOpenPackageDialog();
        if (DirectoryMemory.ShowDialog(dialog) == true)
            await LoadLevelAsync(dialog.FileName, replace: true).ConfigureAwait(true);
    }

    private async void AddLevel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDirectories.GetOpenPackageDialog();
        if (DirectoryMemory.ShowDialog(dialog) == true)
            await LoadLevelAsync(dialog.FileName, replace: false).ConfigureAwait(true);
    }

    private void UnloadLevel_Click(object sender, RoutedEventArgs e)
    {
        CloseLevels();
        UpdateSessionLevelPaths();
        SceneStatus = $"{Bones.Count} bone(s), {Cameras.Count} camera(s); no level backdrop loaded.";
    }

    private async Task LoadLevelAsync(string path, bool replace, bool updateSession = true)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (replace) CloseLevels();
            if (levelPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;

            SceneStatus = $"Loading {Path.GetFileName(path)}...";
            await Task.Delay(1).ConfigureAwait(true);
            IMEPackage package = MEPackageHandler.OpenMEPackage(path);
            ExportEntry levelExport = package.Exports.FirstOrDefault(export => export.ClassName == "Level");
            if (levelExport is null)
            {
                package.Dispose();
                MessageBox.Show($"{Path.GetFileName(path)} is not a level file.");
                return;
            }

            Level level = levelExport.GetBinaryData<Level>();
            List<ActorProxy> actors = LoadActors(level);
            levelPackages.Add(package);
            levelPaths.Add(path);
            levelActors.AddRange(actors);
            RenderContext.LoadActors(actors);
            RegisterBones();
            RenderContext.EnableTransformWidget();
            RenderContext.TransformWidget.Attach = CameraFocusMode ? null : SelectedBone;
            if (updateSession) UpdateSessionLevelPaths();
            RecordRecentSet();
            SceneStatus = $"{Bones.Count} bone(s), {Cameras.Count} camera(s); {levelPaths.Count} backdrop file(s).";
            SceneViewer.SetShouldRender(true);
            SceneViewer.MarkRenderDirty();
        }
        catch (Exception exception)
        {
            SceneStatus = $"Failed to load {Path.GetFileName(path)}.";
            MessageBox.Show($"Unable to open level file:\n{exception.Message}");
        }
    }

    private List<ActorProxy> LoadActors(Level level)
    {
        var actors = new List<ActorProxy>();
        IEnumerable<ExportEntry> actorExports = level.Actors
            .Where(level.Export.FileRef.IsUExport)
            .Select(level.Export.FileRef.GetUExport);
        foreach (ExportEntry actorExport in actorExports)
        {
            if (actorExport.ClassName == "StaticMeshCollectionActor")
            {
                var collection = actorExport.GetBinaryData<StaticMeshCollectionActor>();
                for (int index = 0; index < collection.Components.Count; index++)
                {
                    if (level.Export.FileRef.TryGetUExport(collection.Components[index], out ExportEntry component))
                        actors.Add(new StaticMeshComponentActorProxy(this, component, collection, index));
                }
            }
            else if (actorExport.ClassName == "StaticLightCollectionActor")
            {
                var collection = actorExport.GetBinaryData<StaticLightCollectionActor>();
                for (int index = 0; index < collection.Components.Count; index++)
                {
                    if (!level.Export.FileRef.TryGetUExport(collection.Components[index], out ExportEntry lightExport))
                        continue;
                    ActorProxy light = GlobalUnrealObjectInfo.IsA(lightExport.ClassName, "SpotLightComponent", lightExport.Game)
                        ? new SpotLightComponentActorProxy(this, lightExport, collection, index)
                        : GlobalUnrealObjectInfo.IsA(lightExport.ClassName, "DirectionalLightComponent", lightExport.Game)
                            ? new DirectionalLightComponentActorProxy(this, lightExport, collection, index)
                            : new PointLightComponentActorProxy(this, lightExport, collection, index);
                    actors.Add(light);
                }
            }
            else if (ActorProxy.Create(this, actorExport) is { } actor)
            {
                actors.Add(actor);
            }
        }
        foreach (ActorProxy actor in actors) actor.ResolveAttachment(actors);
        return actors.OrderBy(actor => actor.Export.UIndex).ToList();
    }

    private void CloseLevels(bool rebuildStagePreview = true)
    {
        DisposeStagePreview();
        RenderContext.UnloadLevel();
        RenderContext.EnableTransformWidget();
        levelActors.Clear();
        foreach (IMEPackage package in levelPackages) package.Dispose();
        levelPackages.Clear();
        levelPaths.Clear();
        foreach (BoneItem bone in Bones) bone.HitID = 0;
        RegisterBones();
        RenderContext.TransformWidget.Attach = CameraFocusMode ? null : SelectedBone;
        if (rebuildStagePreview) BuildStagePreview();
        SceneViewer?.MarkRenderDirty();
    }

    private async Task RestoreSessionLevelsAsync()
    {
        if (sessionLevelsRestored) return;
        List<string> paths;
        lock (SessionLevelPathsLock)
            paths = SessionLevelPaths.Where(File.Exists).ToList();
        if (paths.Count == 0) return;
        sessionLevelsRestored = true;
        foreach (string path in paths)
            await LoadLevelAsync(path, replace: false, updateSession: false).ConfigureAwait(true);
    }

    private void UpdateSessionLevelPaths()
    {
        lock (SessionLevelPathsLock)
        {
            SessionLevelPaths.Clear();
            SessionLevelPaths.AddRange(levelPaths);
        }
    }

    private void RecentLevelsButton_Click(object sender, RoutedEventArgs e)
    {
        RecentLevelsMenu.PlacementTarget = RecentLevelsButton;
        RecentLevelsMenu.IsOpen = true;
    }

    private void RecentLevelsMenu_Opened(object sender, RoutedEventArgs e)
    {
        RecentLevelsMenu.Items.Clear();
        List<RecentFileSet> recentSets = LoadRecentSets();
        if (recentSets.Count == 0)
        {
            RecentLevelsMenu.Items.Add(new MenuItem { Header = "No recent levels", IsEnabled = false });
            return;
        }
        foreach (RecentFileSet set in recentSets)
        {
            var item = new MenuItem
            {
                Header = set.DisplayName.Replace("_", "__"),
                ToolTip = set.TooltipText,
                Tag = set
            };
            item.Click += RecentLevel_Click;
            RecentLevelsMenu.Items.Add(item);
        }
    }

    private async void RecentLevel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RecentFileSet set }) return;
        List<string> existingPaths = set.FilePaths.Where(File.Exists).ToList();
        if (existingPaths.Count == 0)
        {
            MessageBox.Show("None of the recent level files exist anymore.");
            return;
        }
        for (int index = 0; index < existingPaths.Count; index++)
            await LoadLevelAsync(existingPaths[index], replace: index == 0).ConfigureAwait(true);
    }

    private static string RecentSetsFile => Path.Combine(
        Directory.CreateDirectory(Path.Combine(AppDirectories.AppDataFolder, "LevelEditor")).FullName,
        "RECENTSETS");

    private static List<RecentFileSet> LoadRecentSets()
    {
        if (!File.Exists(RecentSetsFile)) return [];
        try
        {
            List<RecentFileSet> sets = JsonConvert.DeserializeObject<List<RecentFileSet>>(
                File.ReadAllText(RecentSetsFile)) ?? [];
            foreach (RecentFileSet set in sets)
            {
                set.FilePaths.RemoveAll(path => !File.Exists(path));
                set.ReadOnlyFilePaths.RemoveAll(path => !File.Exists(path));
            }
            return sets.Where(set => set.FilePaths.Count > 0).ToList();
        }
        catch
        {
            return [];
        }
    }

    private void RecordRecentSet()
    {
        if (levelPaths.Count == 0) return;
        List<RecentFileSet> sets = LoadRecentSets();
        sets.RemoveAll(set => set.FilePaths.Count > 0
                              && set.FilePaths[0].Equals(levelPaths[0], StringComparison.OrdinalIgnoreCase));
        sets.Insert(0, new RecentFileSet
        {
            Game = levelPackages[0].Game,
            FilePaths = [.. levelPaths],
            ReadOnlyFilePaths = []
        });
        if (sets.Count > 10) sets.RemoveRange(10, sets.Count - 10);
        File.WriteAllText(RecentSetsFile, JsonConvert.SerializeObject(sets, Formatting.Indented));
    }
}
