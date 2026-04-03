using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Misc;
using MessageBox =
Xceed.Wpf.Toolkit.MessageBox;
using
LegendaryExplorer.UserControls.Interfaces;
using
LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.SharedUI.Bases;
using
LegendaryExplorer.Tools.LevelEditor.Scene3D;
using
LegendaryExplorerCore.GameFilesystem;
using
LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using
LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using
Microsoft.Win32;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.PackageEditor;
using LegendaryExplorer.Tools.PackageEditor.Experiments;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using
Newtonsoft.Json;
using System;
using System.Collections.Generic;
using
System.ComponentModel;
using System.IO;
using System.Linq;
using
System.Numerics;
using System.Text.RegularExpressions;
using
System.Threading.Tasks;
using System.Windows;
using
System.Windows.Controls;
using System.Windows.Data;
using
System.Windows.Input;
using System.Windows.Threading;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Interaction logic for LevelEditor.xaml
/// </summary>
public class RecentFileSet
{
    public MEGame Game { get; set; }
    public List<string> FilePaths { get; set; } = [];
    public List<string> ReadOnlyFilePaths { get; set; } = [];

    [JsonIgnore]
    public string DisplayName => FilePaths.Count switch
    {
        0 => "(empty)",
        1 => Path.GetFileName(FilePaths[0]),
        _ => $"{Path.GetFileName(FilePaths[0])} (+{FilePaths.Count - 1} more)"
    };

    [JsonIgnore]
    public string TooltipText => string.Join("\n", FilePaths.Select(Path.GetFileName));

}

public partial class LevelEditor : WPFBase, ISceneRenderContextConfigurable, IActorEditorContext
{
    private static readonly (string PropertyName, string DisplayName)[] LightingChannelMenuItems =
    [
        ("bInitialized", "bInitialized"),
        ("BSP", "BSP"),
        ("Static", "Static"),
        ("Dynamic", "Dynamic"),
        ("CompositeDynamic", "Composite Dynamic"),
        ("Skybox", "Skybox"),
        ("Unnamed_1", "Unnamed 1"),
        ("Unnamed_2", "Unnamed 2"),
        ("Unnamed_3", "Unnamed 3"),
        ("Unnamed_4", "Unnamed 4"),
        ("Unnamed_5", "Unnamed 5"),
        ("Unnamed_6", "Unnamed 6"),
        ("Cinematic_1", "Cinematic 1"),
        ("Cinematic_2", "Cinematic 2"),
        ("Cinematic_3", "Cinematic 3"),
        ("Cinematic_4", "Cinematic 4"),
        ("Cinematic_5", "Cinematic 5"),
        ("Cinematic_6", "Cinematic 6"),
        ("Cinematic_7", "Cinematic 7"),
        ("Cinematic_8", "Cinematic 8"),
        ("Cinematic_9", "Cinematic 9"),
        ("Cinematic_10", "Cinematic 10"),
        ("Gameplay_1", "Gameplay 1"),
        ("Gameplay_2", "Gameplay 2"),
        ("Gameplay_3", "Gameplay 3"),
        ("Gameplay_4", "Gameplay 4"),
        ("Crowd", "Crowd")
    ];

    private static readonly (string PropertyName, string DisplayName)[] CollisionMenuItems =
    [
        ("CollideActors", "Collide Actors"),
        ("BlockActors", "Block Actors"),
        ("BlockRigidBody", "Block Rigid Body")
    ];

    private static readonly (string PropertyName, string DisplayName)[] LightingMenuItems =
    [
        ("bAcceptsLights", "Accepts Lights"),
        ("bAcceptsDynamicLights", "Accepts Dynamic Lights")
    ];

    private static readonly (string PropertyName, string DisplayName)[] ShadowMenuItems =
    [
        ("bCastDynamicShadow", "Cast Dynamic Shadow"),
        ("CastShadow", "Cast Shadow"),
        ("bCastHiddenShadow", "Cast Hidden Shadow")
    ];

    private static readonly (string PropertyName, string DisplayName)[] LightShadowMenuItems =
    [
        ("CastDynamicShadows", "Cast Dynamic Shadows"),
        ("CastShadows", "Cast Shadows"),
        ("CastStaticShadows", "Cast Static Shadows")
    ];

    public LevelEditorRenderContext RenderContext { get; }

    public ObservableCollectionExtended<OpenLevelFile> OpenFiles { get; } = [];
    private OpenLevelFile _activeFile;
    public OpenLevelFile ActiveFile
    {
        get => _activeFile;
        private set => SetProperty(ref _activeFile, value);
    }

    public ObservableCollectionExtended<ActorProxy> Actors { get; } = [];
    public ICollectionView ActorsView { get; }
    private string _actorFilterText = "";

    private bool _hasAnyFileOpen;
    public bool HasAnyFileOpen
    {
        get => _hasAnyFileOpen;
        private set => SetProperty(ref _hasAnyFileOpen, value);
    }

    private MEGame _game = MEGame.Unknown;
    public MEGame Game
    {
        get => _game;
        private set => SetProperty(ref _game, value);
    }

    private ActorProxy selectedActor;
    public ActorProxy SelectedActor
    {
        get => selectedActor;
        set
        {
            SelectActor(value, true);
        }
    }

    private bool isDirty;
    public bool IsDirty
    {
        get => isDirty;
        set => SetProperty(ref isDirty, value);
    }

    private bool _showCollision = Settings.LevelEditor_ShowCollision;
    public bool ShowCollision
    {
        get => _showCollision;
        set
        {
            if (SetProperty(ref _showCollision, value))
            {
                Settings.LevelEditor_ShowCollision = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private bool _showLightIcons = Settings.LevelEditor_ShowLightIcons;
    public bool ShowLightIcons
    {
        get => _showLightIcons;
        set
        {
            if (SetProperty(ref _showLightIcons, value))
            {
                Settings.LevelEditor_ShowLightIcons = value;
                RenderContext.ShowLightIcons = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private bool _showVolumes = Settings.LevelEditor_ShowVolumes;
    public bool ShowVolumes
    {
        get => _showVolumes;
        set
        {
            if (SetProperty(ref _showVolumes, value))
            {
                Settings.LevelEditor_ShowVolumes = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private bool _showVolumetrics = Settings.LevelEditor_ShowVolumetrics;
    public bool ShowVolumetrics
    {
        get => _showVolumetrics;
        set
        {
            if (SetProperty(ref _showVolumetrics, value))
            {
                Settings.LevelEditor_ShowVolumetrics = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private bool _unlit = Settings.LevelEditor_Unlit;
    public bool Unlit
    {
        get => _unlit;
        set
        {
            if (SetProperty(ref _unlit, value))
            {
                Settings.LevelEditor_Unlit = value;
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Unlit;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.Unlit;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    #region ISceneRenderContextConfigurable

    private bool _setAlphaToBlack = true;
    public bool SetAlphaToBlack
    {
        get => _setAlphaToBlack;
        set
        {
            if (SetProperty(ref _setAlphaToBlack, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.AlphaAsBlack;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.AlphaAsBlack;
            }
        }
    }

    private bool _showRedChannel = true;
    public bool ShowRedChannel
    {
        get => _showRedChannel;
        set
        {
            if (SetProperty(ref _showRedChannel, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableRedChannel;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableRedChannel;
            }
        }
    }

    private bool _showGreenChannel = true;
    public bool ShowGreenChannel
    {
        get => _showGreenChannel;
        set
        {
            if (SetProperty(ref _showGreenChannel, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableGreenChannel;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableGreenChannel;
            }
        }
    }

    private bool _showBlueChannel = true;
    public bool ShowBlueChannel
    {
        get => _showBlueChannel;
        set
        {
            if (SetProperty(ref _showBlueChannel, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableBlueChannel;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableBlueChannel;
            }
        }
    }

    private bool _showAlphaChannel = true;
    public bool ShowAlphaChannel
    {
        get => _showAlphaChannel;
        set
        {
            if (SetProperty(ref _showAlphaChannel, value))
            {
                if (value)
                    RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel;
                else
                    RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel;
            }
        }
    }

    private System.Windows.Media.Color _backgroundColor;
    public System.Windows.Media.Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (SetProperty(ref _backgroundColor, value))
            {
                RenderContext.BackgroundColor = value;
            }
        }
    }

    /// <summary>
    /// Returns the default background color for the current theme.
    /// Dark mode uses the same dark background as the Sequence Editor.
    /// </summary>
    public static System.Windows.Media.Color GetThemeDefaultBackgroundColor()
    {
        return Settings.Global_DarkMode_Enabled
            ? System.Windows.Media.Color.FromRgb(30, 30, 30)
            : System.Windows.Media.Color.FromRgb(128, 128, 128);
    }

    #endregion

    public bool UseLocalCoordsForWidget
    {
        get => RenderContext.TransformWidget.UseLocalCoords;
        set => SetProperty(ref RenderContext.TransformWidget.UseLocalCoords, value);
    }

    public bool TranslateSnapEnabled
    {
        get => RenderContext.TransformWidget.TranslateSnapEnabled;
        set => SetProperty(ref RenderContext.TransformWidget.TranslateSnapEnabled, value);
    }
    public float TranslateSnapValue
    {
        get => RenderContext.TransformWidget.TranslateSnapValue;
        set => SetProperty(ref RenderContext.TransformWidget.TranslateSnapValue, value);
    }
    public bool RotateSnapEnabled
    {
        get => RenderContext.TransformWidget.RotateSnapEnabled;
        set => SetProperty(ref RenderContext.TransformWidget.RotateSnapEnabled, value);
    }
    public float RotateSnapValue
    {
        get => RenderContext.TransformWidget.RotateSnapValue;
        set => SetProperty(ref RenderContext.TransformWidget.RotateSnapValue, value);
    }
    public bool ScaleSnapEnabled
    {
        get => RenderContext.TransformWidget.ScaleSnapEnabled;
        set => SetProperty(ref RenderContext.TransformWidget.ScaleSnapEnabled, value);
    }
    public float ScaleSnapValue
    {
        get => RenderContext.TransformWidget.ScaleSnapValue;
        set => SetProperty(ref RenderContext.TransformWidget.ScaleSnapValue, value);
    }

    private string _cameraPositionX = "0";
    public string CameraPositionX
    {
        get => _cameraPositionX;
        set => SetProperty(ref _cameraPositionX, value);
    }

    private string _cameraPositionY = "0";
    public string CameraPositionY
    {
        get => _cameraPositionY;
        set => SetProperty(ref _cameraPositionY, value);
    }

    private string _cameraPositionZ = "0";
    public string CameraPositionZ
    {
        get => _cameraPositionZ;
        set => SetProperty(ref _cameraPositionZ, value);
    }

    private string _cameraRotationX = "0";
    public string CameraRotationX
    {
        get => _cameraRotationX;
        set => SetProperty(ref _cameraRotationX, value);
    }

    private string _cameraRotationY = "0";
    public string CameraRotationY
    {
        get => _cameraRotationY;
        set => SetProperty(ref _cameraRotationY, value);
    }

    private string _cameraRotationZ = "0";
    public string CameraRotationZ
    {
        get => _cameraRotationZ;
        set => SetProperty(ref _cameraRotationZ, value);
    }

    private float _cameraPositionStep = 10f;
    public float CameraPositionStep
    {
        get => _cameraPositionStep;
        set => SetProperty(ref _cameraPositionStep, value);
    }

    private float _cameraRotationStep = 5f;
    public float CameraRotationStep
    {
        get => _cameraRotationStep;
        set => SetProperty(ref _cameraRotationStep, value);
    }

    private bool _updatingCameraPositionText;
    private int _cameraPositionEditorsFocused;
    private bool _updatingCameraRotationText;
    private int _cameraRotationEditorsFocused;

    public ObservableCollectionExtended<RecentFileSet> RecentSets { get; } = [];

    private static string RecentSetsFile => Path.Combine(
        Directory.CreateDirectory(Path.Combine(AppDirectories.AppDataFolder, "LevelEditor")).FullName,
        "RECENTSETS");

    public LevelEditor() : base("LevelEditor")
    {
        RenderContext = new LevelEditorRenderContext();
        RenderContext.ShowLightIcons = _showLightIcons;
        RenderContext.TransformWidget.OnDragComplete = OnWidgetDragComplete;
        _backgroundColor = GetThemeDefaultBackgroundColor();
        RenderContext.BackgroundColor = _backgroundColor;
        ActorsView = CollectionViewSource.GetDefaultView(Actors);
        ActorsView.Filter = ActorFilter;
        ActorsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActorProxy.OwningFile)));

        LoadCommands();
        InitializeComponent();
        LoadRecentSets();

        SceneViewer.Context = RenderContext;
        UndoHistory.PropertyChanged += UndoHistory_PropertyChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object sender, bool isDarkMode)
    {
        BackgroundColor = GetThemeDefaultBackgroundColor();
    }

    private string FileQueuedForLoad;
    private int ExportQueuedForFocusing;

    public LevelEditor(ExportEntry exportToLoad) : this()
    {
        FileQueuedForLoad = exportToLoad.FileRef.FilePath;
        ExportQueuedForFocusing = exportToLoad.UIndex;
    }

    private void UpdateScene(object sender, float e)
    {
        UpdateCameraPositionText();
        UpdateCameraRotationText();
    }

    private void RenderScene(object sender, EventArgs e)
    {
        RenderContext.ShowVolumes = ShowVolumes;
        RenderContext.ShowVolumetrics = ShowVolumetrics;
        RenderContext.RefreshSceneLights();
        Span<RenderPass> passes = ShowCollision
            ? [RenderPass.Base, RenderPass.Hair, RenderPass.Collision]
            : [RenderPass.Base, RenderPass.Hair];

        foreach (RenderPass pass in passes)
        {
            DoRenderPass(pass);
        }

        RenderContext.DrawUI();
    }
    void DoRenderPass(RenderPass pass)
    {
        for (int i = 0; i < RenderContext.DrawList_3D.Count; i++)
        {
            ActorProxy actor = RenderContext.DrawList_3D[i];
            if (actor.IsVolume && !ShowVolumes) continue;
            if (actor.IsVolumetricMesh && !ShowVolumetrics) continue;
            int hitID = actor.HitID;
            RenderContext.CurrentHitTestId = new Vector3((hitID & 0xFF) / 255f, ((hitID >> 8) & 0xFF) / 255f, ((hitID >> 16) & 0xFF) / 255f);
            if (actor == selectedActor)
            {
                RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Selected;
            }
            actor.Render(RenderContext, pass);
            RenderContext.RenderFlags &= ~LevelEditorRenderContext.ShaderFlags.Selected;
        }
    }

    private void CameraPositionAdjustButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        string[] parts = tag.Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[1], out float direction)) return;

        Vector3 position = RenderContext.Camera.Position;
        if (float.TryParse(CameraPositionX, out float x)) position.X = x;
        if (float.TryParse(CameraPositionY, out float y)) position.Y = y;
        if (float.TryParse(CameraPositionZ, out float z)) position.Z = z;

        float delta = CameraPositionStep * direction;
        switch (parts[0])
        {
            case "X":
                position.X += delta;
                break;
            case "Y":
                position.Y += delta;
                break;
            case "Z":
                position.Z += delta;
                break;
            default:
                return;
        }

        CameraPositionX = position.X.ToString("0.##");
        CameraPositionY = position.Y.ToString("0.##");
        CameraPositionZ = position.Z.ToString("0.##");
        MoveCameraToEnteredPosition();
    }

    private void CameraRotationAdjustButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        string[] parts = tag.Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[1], out float direction)) return;

        float x = RenderContext.Camera.Roll.RadiansToDegrees();
        float y = RenderContext.Camera.Pitch.RadiansToDegrees();
        float z = RenderContext.Camera.Yaw.RadiansToDegrees();
        if (float.TryParse(CameraRotationX, out float enteredX)) x = enteredX;
        if (float.TryParse(CameraRotationY, out float enteredY)) y = enteredY;
        if (float.TryParse(CameraRotationZ, out float enteredZ)) z = enteredZ;

        float delta = CameraRotationStep * direction;
        switch (parts[0])
        {
            case "X":
                x += delta;
                break;
            case "Y":
                y += delta;
                break;
            case "Z":
                z += delta;
                break;
            default:
                return;
        }

        CameraRotationX = x.ToString("0.##");
        CameraRotationY = y.ToString("0.##");
        CameraRotationZ = z.ToString("0.##");
        MoveCameraToEnteredRotation();
    }

    private void ViewportActorSelect(ActorProxy actor)
    {
        SelectActor(actor, false);
        MeshExportsList.ScrollIntoView(selectedActor);
    }

    private void SelectActor(ActorProxy actor, bool focus)
    {
        var prev = selectedActor;
        if (SetProperty(ref selectedActor, actor, nameof(SelectedActor)))
        {
            ActiveFile = actor?.OwningFile ?? OpenFiles.FirstOrDefault();
            SceneViewer?.MarkRenderDirty();
            if (prev is not null)
            {
                prev.PropertyChanged -= OnActorPropertyChanged;
            }
            if (selectedActor is not null)
                {
                    if (focus)
                    {
                        FocusOnBounds(selectedActor.GetBounds());
                        RenderContext.TransformWidget.Attach = selectedActor;
                    }
                    selectedActor.PropertyChanged += OnActorPropertyChanged;
                    _preEditSnapshot = selectedActor.SnapshotTransform();

                RefreshPropertiesExportSelection(selectedActor, PropertiesTabControl.SelectedIndex);
                }
                else
                {
                    _preEditSnapshot = null;
                    UnloadPropertyTabs();
                }
        }
    }

    private void CenterView()
    {
        if (Actors.Count > 0)
        {
            BoxSphereBounds fullBounds = Actors[0].GetBounds();
            for (int i = 1; i < Actors.Count; i++)
            {
                fullBounds = fullBounds.Union(Actors[i].GetBounds());
            }
            FocusOnBounds(fullBounds);
        }
        else
        {
            RenderContext.Camera.Position = Vector3.Zero;
            RenderContext.Camera.Pitch = -MathF.PI / 5.0f;
            RenderContext.Camera.Yaw = MathF.PI / 4.0f;
        }

        UpdateCameraPositionText();
        UpdateCameraRotationText();
    }

    private void FocusOnBounds(BoxSphereBounds fullBounds)
    {
        Vector3 origin = fullBounds.Origin;
        if (RenderContext.Camera.IsOrthographic)
        {
            RenderContext.Camera.Position = new Vector3(origin.X, origin.Y, RenderContext.Camera.ZFar * 0.4f);
            RenderContext.Camera.OrthoWidth = fullBounds.SphereRadius.Clamp(10, float.MaxValue) * 3f;
        }
        else
        {
            float hyp = fullBounds.SphereRadius.Clamp(10, float.MaxValue) * 2;
            (float sin, float cos) = MathF.SinCos(MathF.PI / 2.5f);
            RenderContext.Camera.Position = new Vector3(origin.X, origin.Y + sin * hyp, origin.Z + cos * hyp);
            RenderContext.Camera.OrientTowards(origin);
        }
        UpdateCameraPositionText();
        UpdateCameraRotationText();
    }

    private const float CameraButtonMoveStep = 256f;
    private bool _isRotatingCameraFromPad;
    private Point _lastCameraRotatePadPoint;

    private void MoveCameraPlanar(float forwardAmount, float rightAmount)
    {
        if (!HasAnyFileOpen) return;

        float yaw = RenderContext.Camera.Yaw;
        Vector3 planarForward = new(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        Vector3 planarRight = new(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        Vector3 direction = (planarForward * forwardAmount) + (planarRight * rightAmount);
        if (direction.LengthSquared() > 1f)
        {
            direction = Vector3.Normalize(direction);
        }

        RenderContext.Camera.Position += direction * CameraButtonMoveStep;
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer?.MarkRenderDirty();
        SceneViewer?.Focus();
    }

    private void MoveCameraVertical(float amount)
    {
        if (!HasAnyFileOpen) return;

        RenderContext.Camera.Position += Vector3.UnitZ * (amount * CameraButtonMoveStep);
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer?.MarkRenderDirty();
        SceneViewer?.Focus();
    }

    private void CameraMoveXYButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        string[] parts = tag.Split(',');
        if (parts.Length != 2) return;

        if (float.TryParse(parts[0], out float forwardAmount)
            && float.TryParse(parts[1], out float rightAmount))
        {
            MoveCameraPlanar(forwardAmount, rightAmount);
        }
    }

    private void CameraMoveZButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        if (float.TryParse(tag, out float amount))
        {
            MoveCameraVertical(amount);
        }
    }

    private void CameraRotatePad_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasAnyFileOpen || sender is not System.Windows.Controls.Border element) return;

        _isRotatingCameraFromPad = true;
        _lastCameraRotatePadPoint = e.GetPosition(element);
        element.CaptureMouse();
        SceneViewer?.Focus();
        e.Handled = true;
    }

    private void CameraRotatePad_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRotatingCameraFromPad || sender is not System.Windows.Controls.Border element) return;

        Point currentPoint = e.GetPosition(element);
        double deltaX = currentPoint.X - _lastCameraRotatePadPoint.X;
        double deltaY = currentPoint.Y - _lastCameraRotatePadPoint.Y;
        _lastCameraRotatePadPoint = currentPoint;

        RenderContext.Camera.Yaw += (float)(deltaX * 0.01);
        RenderContext.Camera.Pitch = (RenderContext.Camera.Pitch - (float)(deltaY * 0.01))
            .Clamp(-MathF.PI / 2 + 0.01f, MathF.PI / 2 - 0.01f);

        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer?.MarkRenderDirty();
        e.Handled = true;
    }

    private void CameraRotatePad_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRotatingCameraFromPad || sender is not System.Windows.Controls.Border element) return;

        _isRotatingCameraFromPad = false;
        element.ReleaseMouseCapture();
        e.Handled = true;
    }

    private bool AreCameraPositionBoxesFocused() => _cameraPositionEditorsFocused > 0;
    private bool AreCameraRotationBoxesFocused() => _cameraRotationEditorsFocused > 0;

    private void UpdateCameraPositionText()
    {
        if (AreCameraPositionBoxesFocused()) return;

        _updatingCameraPositionText = true;
        try
        {
            Vector3 position = RenderContext.Camera.Position;
            CameraPositionX = position.X.ToString("0.##");
            CameraPositionY = position.Y.ToString("0.##");
            CameraPositionZ = position.Z.ToString("0.##");
        }
        finally
        {
            _updatingCameraPositionText = false;
        }
    }

    private void UpdateCameraRotationText()
    {
        if (AreCameraRotationBoxesFocused()) return;

        _updatingCameraRotationText = true;
        try
        {
            CameraRotationX = RenderContext.Camera.Roll.RadiansToDegrees().ToString("0.##");
            CameraRotationY = RenderContext.Camera.Pitch.RadiansToDegrees().ToString("0.##");
            CameraRotationZ = RenderContext.Camera.Yaw.RadiansToDegrees().ToString("0.##");
        }
        finally
        {
            _updatingCameraRotationText = false;
        }
    }

    private void ResetCameraRotationText()
    {
        _updatingCameraRotationText = true;
        try
        {
            CameraRotationX = "0";
            CameraRotationY = "0";
            CameraRotationZ = "0";
        }
        finally
        {
            _updatingCameraRotationText = false;
        }
    }

    private void ResetCameraPositionText()
    {
        _updatingCameraPositionText = true;
        try
        {
            CameraPositionX = "0";
            CameraPositionY = "0";
            CameraPositionZ = "0";
        }
        finally
        {
            _updatingCameraPositionText = false;
        }
    }

    private void CameraPositionBoxes_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            MoveCameraToEnteredPosition();
            e.Handled = true;
        }
    }

    private void CameraPositionBoxes_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _cameraPositionEditorsFocused++;
    }

    private void CameraPositionBoxes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _cameraPositionEditorsFocused = Math.Max(0, _cameraPositionEditorsFocused - 1);
        if (_updatingCameraPositionText) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_updatingCameraPositionText && !AreCameraPositionBoxesFocused())
            {
                MoveCameraToEnteredPosition();
            }
        }));
    }

    private void MoveCameraToEnteredPosition()
    {
        if (!HasAnyFileOpen) return;

        if (!float.TryParse(CameraPositionX, out float x)
            || !float.TryParse(CameraPositionY, out float y)
            || !float.TryParse(CameraPositionZ, out float z))
        {
            UpdateCameraPositionText();
            return;
        }

        RenderContext.Camera.Position = new Vector3(x, y, z);
        UpdateCameraPositionText();
        UpdateCameraRotationText();
        SceneViewer?.MarkRenderDirty();
        SceneViewer?.Focus();
    }

    private void CameraRotationBoxes_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            MoveCameraToEnteredRotation();
            e.Handled = true;
        }
    }

    private void CameraRotationBoxes_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _cameraRotationEditorsFocused++;
    }

    private void CameraRotationBoxes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _cameraRotationEditorsFocused = Math.Max(0, _cameraRotationEditorsFocused - 1);
        if (_updatingCameraRotationText) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_updatingCameraRotationText && !AreCameraRotationBoxesFocused())
            {
                MoveCameraToEnteredRotation();
            }
        }));
    }

    private void MoveCameraToEnteredRotation()
    {
        if (!HasAnyFileOpen) return;

        if (!float.TryParse(CameraRotationX, out float x)
            || !float.TryParse(CameraRotationY, out float y)
            || !float.TryParse(CameraRotationZ, out float z))
        {
            UpdateCameraRotationText();
            return;
        }

        RenderContext.Camera.Roll = x.DegreesToRadians();
        RenderContext.Camera.Pitch = y.DegreesToRadians().Clamp(-MathF.PI / 2 + 0.01f, MathF.PI / 2 - 0.01f);
        RenderContext.Camera.Yaw = z.DegreesToRadians();
        UpdateCameraRotationText();
        SceneViewer?.MarkRenderDirty();
        SceneViewer?.Focus();
    }

    private void CoordinateEditor_GotFocus(object sender, RoutedEventArgs e)
    {
        if (Keyboard.PrimaryDevice.IsKeyDown(Key.Tab) && sender is TextBox tb)
        {
            tb.SelectAll();
        }
    }

    #region File Management

    public async Task LoadFileAsync(string s)
    {
        try
        {
            CloseAllFiles();
            Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ContextIdle, null);


            using var guard = new RenderGuard(this);

            await AddLevelFile(s).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            StatusBar_LeftMostText.Text = "Failed to load " + Path.GetFileName(s);
            MessageBox.Show($"Error loading {Path.GetFileName(s)}:\n{e.Message}");
            IsBusy = false;
            IsBusyTaskbar = false;
        }
    }

    private async Task AddLevelFile(string path)
    {
        path = Path.GetFullPath(path);

        if (OpenFiles.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"{Path.GetFileName(path)} is already open.");
            return;
        }

        using IMEPackage pcc = MEPackageHandler.OpenMEPackage(path);
        if (OpenFiles.Count > 0 && pcc.Game != Game)
        {
            MessageBox.Show(this, $"Cannot mix games. The open files are {Game}, but {Path.GetFileName(path)} is {pcc.Game}.");
            return;
        }
        Game = pcc.Game;
        ExportEntry levelExport = pcc.Exports.FirstOrDefault(exp => exp.ClassName == "Level");
        if (levelExport is null)
        {
            MessageBox.Show(this, $"{Path.GetFileName(path)} is not a level file!");
            return;
        }

        var openFile = new OpenLevelFile(this, pcc, levelExport);
        // Register the OpenLevelFile as a user of the package for update notifications
        pcc.RegisterTool(openFile);
        OpenFiles.Add(openFile);
        ActiveFile = openFile;
        HasAnyFileOpen = true;

        RecordCurrentFilesAsRecent();

        Level levelBin = levelExport.GetBinaryData<Level>();
        bool isFirstFile = OpenFiles.Count == 1;

        IsBusy = true;
        BusyText = $"Loading {Path.GetFileName(path)}...";

        // Yield to let the BusyIndicator paint before the heavy work begins
        await Task.Delay(1).ConfigureAwait(true);

        var (actors, ignoredClasses) = LoadActors(levelBin, openFile);
        var sorted = actors.OrderBy(actor => actor.Export.UIndex).ToList();
        openFile.Actors.AddRange(sorted);
        Actors.AddRange(sorted);
        RenderContext.LoadActors(sorted);

        if (isFirstFile)
        {
            CenterView();
        }

        if (ignoredClasses.Count > 0)
        {
            string existing = string.IsNullOrEmpty(TextBelowActors) ? "" : TextBelowActors + "\n";
            TextBelowActors = existing + $"{Path.GetFileName(path)} unrendered: {string.Join(", ", ignoredClasses)}";
        }

        if (ExportQueuedForFocusing > 0)
        {
            if (sorted.FirstOrDefault(a => a.Export.UIndex == ExportQueuedForFocusing) is { } proxy)
            {
                SelectedActor = proxy;
                ExportQueuedForFocusing = 0;
            }
        }

        UpdateTitle();
    }

    private void CloseAllFiles()
    {
        Game = MEGame.Unknown;
        ActiveFile = null;
        if (selectedActor is not null)
        {
            selectedActor.PropertyChanged -= OnActorPropertyChanged;
            selectedActor = null;
        }
        SceneViewer.SetShouldRender(false);
        RenderContext.UnloadLevel();
        Actors.Clear();
        foreach (var file in OpenFiles)
        {
            file.Dispose();
        }
        OpenFiles.Clear();
        HasAnyFileOpen = false;
        ResetCameraPositionText();
        ResetCameraRotationText();
        TextBelowActors = "";
        IsDirty = false;
        UndoHistory.Clear();
        _preEditSnapshot = null;
        UnloadPropertyTabs();
    }

    public void CloseFile(OpenLevelFile file)
    {
        if (file is null) return;

        if (file.IsDirty)
        {
            var result = MessageBox.Show(this,
                $"{file.FileName} has uncommitted changes. Close anyway?",
                "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }
        else if (file.Package.IsModified && file.Package.Users.Count <= 1)
        {
            var result = MessageBox.Show(this,
                $"{file.FileName} has unsaved changes. Close anyway?",
                "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        if (SelectedActor is not null && file.Actors.Contains(SelectedActor))
        {
            SelectedActor = null;
        }

        var actorsToRemove = file.Actors.ToList();
        Actors.RemoveRange(actorsToRemove);
        foreach (var actor in actorsToRemove)
        {
            RenderContext.RemoveActor(actor);
            actor.Dispose();
        }

        file.Dispose();
        OpenFiles.Remove(file);
        ActiveFile = SelectedActor?.OwningFile ?? OpenFiles.FirstOrDefault();
        HasAnyFileOpen = OpenFiles.Count > 0;
        UpdateGlobalDirtyState();
        UpdateTitle();

        if (OpenFiles.Count == 0)
        {
            SceneViewer.SetShouldRender(false);
            TextBelowActors = "";
            UndoHistory.Clear();
            _preEditSnapshot = null;
            UnloadPropertyTabs();
        }
    }

    public void CloseFileByName(string fileName)
    {
        var file = OpenFiles.FirstOrDefault(f => f.FileName == fileName);
        if (file is not null)
        {
            CloseFile(file);
        }
    }

    private void UpdateTitle()
    {
        if (OpenFiles.Count == 0)
            Title = "Level Editor";
        else if (OpenFiles.Count == 1)
            Title = $"Level Editor - {OpenFiles[0].FilePath}";
        else
            Title = $"Level Editor - {OpenFiles.Count} files";

        StatusBar_LeftMostText.Text = OpenFiles.Count switch
        {
            0 => "Select package file to load",
            1 => OpenFiles[0].FileName,
            _ => $"{OpenFiles.Count} files loaded"
        };
    }

    #endregion

    #region Actor Loading

    private readonly record struct LoadActorsResult(List<ActorProxy> actors, HashSet<string> ignoredActorClasses);

    private LoadActorsResult LoadActors(Level level, OpenLevelFile owningFile)
    {
        var actorExports = level.Actors.Where(level.Export.FileRef.IsUExport).Select(level.Export.FileRef.GetUExport);
        var actors = new List<ActorProxy>();
        HashSet<string> ignoredActorClasses = [];
        foreach (var actorExport in actorExports)
        {
            var className = actorExport.ClassName;
            if (className is "StaticMeshCollectionActor")
            {
                var smca = actorExport.GetBinaryData<StaticMeshCollectionActor>();
                for (int i = 0; i < smca.Components.Count; i++)
                {
                    if (level.Export.FileRef.TryGetUExport(smca.Components[i], out ExportEntry smcExport))
                    {
                        var smcActor = new StaticMeshComponentActorProxy(this, smcExport, smca, i);
                        smcActor.OwningFile = owningFile;
                        actors.Add(smcActor);
                    }
                }
            }
            else if (className is "StaticLightCollectionActor")
            {
                var slca = actorExport.GetBinaryData<StaticLightCollectionActor>();
                for (int i = 0; i < slca.Components.Count; i++)
                {
                    if (level.Export.FileRef.TryGetUExport(slca.Components[i], out ExportEntry lightExport))
                    {
                        ActorProxy lightActor = GlobalUnrealObjectInfo.IsA(lightExport.ClassName, "SpotLightComponent", lightExport.Game)
                            ? new SpotLightComponentActorProxy(this, lightExport, slca, i)
                            : GlobalUnrealObjectInfo.IsA(lightExport.ClassName, "DirectionalLightComponent", lightExport.Game)
                                ? new DirectionalLightComponentActorProxy(this, lightExport, slca, i)
                                : new PointLightComponentActorProxy(this, lightExport, slca, i);
                        lightActor.OwningFile = owningFile;
                        actors.Add(lightActor);
                    }
                }
            }
            else if (ActorProxy.Create(this, actorExport) is { } actorProxy)
            {
                actorProxy.OwningFile = owningFile;
                actors.Add(actorProxy);
            }
            else if (className is not "BioWorldInfo")
            {
                ignoredActorClasses.Add(className);
            }
        }
        foreach (var actor in actors)
        {
            actor.ResolveAttachment(actors);
        }
        return new(actors, ignoredActorClasses);
    }

    public void RemoveActor(ActorProxy actor)
    {
        if (Actors.Remove(actor))
        {
            actor.Detach();
            actor.OwningFile?.Actors.Remove(actor);
            RenderContext.RemoveActor(actor);
            actor.Dispose();
        }
    }

    public void AddActor(ActorProxy actor, bool sort = true)
    {
        if (!Actors.Contains(actor))
        {
            Actors.Add(actor);
            actor.ResolveAttachment(Actors);
            actor.OwningFile?.Actors.Add(actor);
            RenderContext.AddActor(actor);
            if (sort)
            {
                Actors.Sort(a => a.Export.UIndex);
            }
        }
    }

    #endregion

    #region Commands

    public ICommand OpenFileCommand { get; set; }
    public ICommand AddFileCommand { get; set; }
    public ICommand SaveAllCommand { get; set; }
    public ICommand SaveAsCommand { get; set; }
    public ICommand SaveSingleFileCommand { get; set; }
    public ICommand CloseFileCommand { get; set; }
    public ICommand ToggleTranslateCommand { get; set; }
    public ICommand ToggleRotateCommand { get; set; }
    public ICommand ToggleScaleCommand { get; set; }
    public ICommand ToggleUniformScaleCommand { get; set; }
    public ICommand CommitChangesCommand { get; set; }
    public ICommand CommitSingleFileCommand { get; set; }
    public ICommand LoadRelatedLevelsCommand { get; set; }
    public ICommand FocusSelectedCommand { get; set; }
    public ICommand ToggleLocalCoordsCommand { get; set; }
    public ICommand OpenInPackageEditorCommand { get; set; }
    public ICommand OpenRecentSetCommand { get; set; }
    public ICommand UndoCommand { get; set; }
    public ICommand RedoCommand { get; set; }
    public ICommand ViewActorPropertiesCommand { get; set; }
    public ICommand ViewActorMetadataCommand { get; set; }
    public ICommand CloneActorTreeCommand { get; set; }
    public ICommand TrashActorCommand { get; set; }
    public ICommand SnapActorToCameraCommand { get; set; }
    public ICommand ToggleOrthoViewCommand { get; set; }
    private void LoadCommands()
    {
        OpenFileCommand = new GenericCommand(OpenFile);
        AddFileCommand = new GenericCommand(AddFile);
        SaveAllCommand = new GenericCommand(SaveAllFiles, PackageIsLoaded);
        SaveAsCommand = new GenericCommand(SaveFileAs, PackageIsLoaded);
        SaveSingleFileCommand = new RelayCommand(SaveSingleFileExecute, _ => PackageIsLoaded());
        CloseFileCommand = new RelayCommand(CloseFileExecute);
        ToggleTranslateCommand = new GenericCommand(() => { RenderContext.TransformWidget.Mode = EWidgetMode.Translate; CurrentModeName = "Translate"; }, PackageIsLoaded);
        ToggleRotateCommand = new GenericCommand(() => { RenderContext.TransformWidget.Mode = EWidgetMode.Rotate; CurrentModeName = "Rotate"; }, PackageIsLoaded);
        ToggleScaleCommand = new GenericCommand(() => { RenderContext.TransformWidget.Mode = EWidgetMode.Scale; CurrentModeName = "Scale"; }, PackageIsLoaded);
        ToggleUniformScaleCommand = new GenericCommand(() => { RenderContext.TransformWidget.Mode = EWidgetMode.UniformScale; CurrentModeName = "Uniform Scale"; }, PackageIsLoaded);
        CommitChangesCommand = new GenericCommand(CommitChanges, PackageIsLoaded);
        CommitSingleFileCommand = new RelayCommand(CommitSingleFileExecute, _ => PackageIsLoaded());
        LoadRelatedLevelsCommand = new GenericCommand(LoadRelatedLevels, PackageIsLoaded);
        FocusSelectedCommand = new GenericCommand(() =>
        {
            if (SelectedActor is not null)
            {
                FocusOnBounds(SelectedActor.GetBounds());
            }
        }, () => PackageIsLoaded() && SelectedActor is not null);
        ToggleLocalCoordsCommand = new GenericCommand(() => UseLocalCoordsForWidget = !UseLocalCoordsForWidget, PackageIsLoaded);
        OpenInPackageEditorCommand = new GenericCommand(() =>
        {
            if (SelectedActor is not null)
            {
                var p = new PackageEditorWindow();
                p.Show();
                p.LoadFile(SelectedActor.Export.FileRef.FilePath, SelectedActor.Export.UIndex);
                p.Activate();
            }
        }, () => PackageIsLoaded() && SelectedActor is not null);
        OpenRecentSetCommand = new RelayCommand(obj => { if (obj is RecentFileSet set) OpenRecentFileSet(set); });
        UndoCommand = new GenericCommand(Undo, () => UndoHistory.CanUndo);
        RedoCommand = new GenericCommand(Redo, () => UndoHistory.CanRedo);
        ViewActorPropertiesCommand = new GenericCommand(() => LoadExportIntoTabs(SelectedActor?.Export, 0), () => SelectedActor is not null);
        ViewActorMetadataCommand = new GenericCommand(() => LoadExportIntoTabs(SelectedActor?.Export, 1), () => SelectedActor is not null);
        CloneActorTreeCommand = new GenericCommand(CloneActorTree,
            () => SelectedActor is not null && !SelectedActor.IsReadOnly);
        TrashActorCommand = new GenericCommand(TrashActor,
            () => SelectedActor is not null && !SelectedActor.IsReadOnly);
        SnapActorToCameraCommand = new GenericCommand(SnapActorToCamera,
            () => SelectedActor is not null && !SelectedActor.IsReadOnly);
        ToggleOrthoViewCommand = new GenericCommand(() => IsOrthographicView = !IsOrthographicView);
    }

    private void SnapActorToCamera()
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly) return;
        SelectedActor.Location = RenderContext.Camera.Position;
        SceneViewer?.MarkRenderDirty();
    }

    #endregion

    #region Undo/Redo
    public readonly UndoHistory UndoHistory = new();
    private TransformSnapshot? _preEditSnapshot;
    private bool _isApplyingUndoRedo;
    private bool _isRefreshingActorFromPackageUpdate;
    private (int UIndex, IMEPackage Package) _pendingSelect;
    public bool IsApplyingUndoRedo => _isApplyingUndoRedo;

    public bool CanUndo => UndoHistory.CanUndo;
    public bool CanRedo => UndoHistory.CanRedo;

    private void UndoHistory_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void Undo_Clicked(object sender, RoutedEventArgs e) => Undo();
    public void Undo()
    {
        if (UndoHistory.CanUndo)
        {
            _isApplyingUndoRedo = true;
            UndoHistory.Undo();
            _isApplyingUndoRedo = false;
            if (SelectedActor is not null)
            {
                _preEditSnapshot = SelectedActor.SnapshotTransform();
            }
        }
    }

    public void Redo_Clicked(object sender, RoutedEventArgs e) => Redo();
    void Redo()
    {
        if (UndoHistory.CanRedo)
        {
            _isApplyingUndoRedo = true;
            UndoHistory.Redo();
            _isApplyingUndoRedo = false;
            if (SelectedActor is not null)
            {
                _preEditSnapshot = SelectedActor.SnapshotTransform();
            }
        }
    }
    #endregion

    #region Load Related Levels

    private async void LoadRelatedLevels()
    {
        if (OpenFiles.Count == 0) return;

        var firstFile = OpenFiles[0];
        string rootFilename = firstFile.FileName;
        MEGame game = Game;

        if (rootFilename.StartsWith("Bio") && rootFilename.Length > 3
            && rootFilename[3] is 'P' or 'D' or 'A' or 'S'
            && rootFilename.Split('_') is [_, string levelIdent, ..]
            && levelIdent.Split('.') is [string realLevelIdent, ..])
        {
            List<(string filename, string path)> candidates = [];
            var regex = new Regex($"^Bio[PDA]_{realLevelIdent}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var openFilePaths = OpenFiles.Select(f => Path.GetFileName(f.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach ((string filename, string path) in MELoadedFiles.GetFilesLoadedInGame(game))
            {
                if (regex.IsMatch(filename) && !filename.Contains("_LOC_", StringComparison.OrdinalIgnoreCase) && !openFilePaths.Contains(filename))
                {
                    candidates.Add((filename, path));
                }
            }
            if (candidates.Count is 0) return;

            var dialogItems = candidates.Select(c => new CheckedListItem
            {
                DisplayName = c.filename,
                IsSelected = true,
                Tag = c.path
            }).ToList();

            var dialog = new CheckedListDialog(dialogItems, "Load Related Levels",
                $"Select levels related to {realLevelIdent} to load:", this);

            if (dialog.ShowDialog() != true) return;

            var selectedPaths = dialog.GetSelectedItems().Select(i => (string)i.Tag).ToList();
            if (selectedPaths.Count is 0) return;

            using var guard = new RenderGuard(this);

            foreach (string path in selectedPaths)
            {
                await AddLevelFile(path).ConfigureAwait(true);
            }
        }
    }

    #endregion

    #region Commit & Save

    private void CommitChanges()
    {
        if (!PackageIsLoaded() || Actors.Count is 0) return;

        foreach (var file in OpenFiles)
        {
            if (!file.IsReadOnly)
                CommitChangesForFile(file);
        }
        IsDirty = false;
    }

    private void CommitChangesForFile(OpenLevelFile file)
    {
        Dictionary<int, StaticCollectionActor> collectionActorMap = [];

        foreach (ActorProxy actor in file.Actors)
        {
            if (!actor.IsDirty)
            {
                continue;
            }
            if (actor is CollectionActorComponentProxy cacp)
            {
                if (!collectionActorMap.TryGetValue(cacp.CollectionActorExport.UIndex, out var collectionActor))
                {
                    collectionActor = (StaticCollectionActor)ObjectBinary.From(cacp.CollectionActorExport);
                    collectionActorMap.Add(cacp.CollectionActorExport.UIndex, collectionActor);
                }
                cacp.CommitChanges(collectionActor);
            }
            else
            {
                actor.CommitChanges();
            }
            actor.MarkClean();
        }

        foreach (var collectionActor in collectionActorMap.Values)
        {
            collectionActor.Export.WriteBinary(collectionActor);
        }
        file.IsDirty = false;
    }

    private void CommitSingleFileExecute(object parameter)
    {
        OpenLevelFile file = ResolveFileParameter(parameter);
        if (file is not null && !file.IsReadOnly)
        {
            CommitChangesForFile(file);
        }
    }

    private async void SaveAllFiles()
    {
        if (IsDirty)
        {
            switch (MessageBox.Show("Do you want to commit your Level Editor changes before saving all files?", "Uncommitted changes", MessageBoxButton.YesNoCancel))
            {
                case MessageBoxResult.Yes:
                    CommitChanges();
                    break;
                case MessageBoxResult.No:
                    break;
                case MessageBoxResult.Cancel:
                default:
                    return;
            }
        }
        foreach (var file in OpenFiles)
        {
            if (!file.IsReadOnly && file.Package.IsModified)
            {
                await file.Package.SaveAsync();
            }
        }
    }

    private async void SaveSingleFileExecute(object parameter)
    {
        OpenLevelFile file = ResolveFileParameter(parameter);
        if (file is null || file.IsReadOnly) return;

        if (file.IsDirty)
        {
            switch (MessageBox.Show($"Do you want to commit changes to {file.FileName} before saving?", "Uncommitted changes", MessageBoxButton.YesNoCancel))
            {
                case MessageBoxResult.Yes:
                    CommitChangesForFile(file);
                    break;
                case MessageBoxResult.No:
                    break;
                case MessageBoxResult.Cancel:
                default:
                    return;
            }
        }
        await file.Package.SaveAsync();
    }

    private void CloseFileExecute(object parameter)
    {
        OpenLevelFile file = ResolveFileParameter(parameter);
        if (file is not null)
        {
            CloseFile(file);
        }
    }

    private OpenLevelFile ResolveFileParameter(object parameter)
    {
        if (parameter is OpenLevelFile file) return file;
        if (parameter is string fileName)
        {
            return OpenFiles.FirstOrDefault(f => f.FileName == fileName);
        }
        return null;
    }

    private async void SaveFileAs()
    {
        if (OpenFiles.Count == 0) return;

        // Save As applies to the first file when only one is open,
        // otherwise prompt which file to save
        OpenLevelFile fileToSave;
        if (OpenFiles.Count == 1)
        {
            fileToSave = OpenFiles[0];
        }
        else
        {
            // For multi-file, Save As saves all files to a chosen directory
            // For simplicity, just save the selected actor's file, or the first file
            fileToSave = SelectedActor?.OwningFile ?? OpenFiles[0];
        }

        if (fileToSave.IsDirty)
        {
            switch (MessageBox.Show($"Do you want to commit changes to {fileToSave.FileName} before saving?", "Uncommitted changes", MessageBoxButton.YesNoCancel))
            {
                case MessageBoxResult.Yes:
                    CommitChangesForFile(fileToSave);
                    break;
                case MessageBoxResult.No:
                    break;
                case MessageBoxResult.Cancel:
                default:
                    return;
            }
        }

        string fileFilter;
        switch (fileToSave.Package.Game)
        {
            case MEGame.ME1:
                fileFilter = GameFileFilters.ME1SaveFileFilter;
                break;
            case MEGame.ME2:
            case MEGame.ME3:
                fileFilter = GameFileFilters.ME3ME2SaveFileFilter;
                break;
            default:
                string extension = Path.GetExtension(fileToSave.FilePath);
                fileFilter = $"*{extension}|*{extension}";
                break;
        }
        var d = new SaveFileDialog { Filter = fileFilter };
        if (d.ShowDialog() == true)
        {
            IsBusy = true;
            BusyText = "Saving...";
            await fileToSave.Package.SaveAsync(d.FileName);
            IsBusy = false;
        }
    }

    #endregion

    #region HandleUpdate

    public void HandleFileUpdate(OpenLevelFile file, List<PackageUpdate> updates)
    {
        if (file.LevelExport is null) return;

        IEnumerable<PackageUpdate> relevantUpdates = updates.Where(x => x.Change.Has(PackageChange.Export));
        HashSet<int> updatedExports = relevantUpdates.Select(x => x.Index).ToHashSet();

        // Detect structural changes (level binary or collection actor binary modified).
        // When structural changes are present (e.g. after clone/trash), skip the
        // lightweight property-refresh path so the scene is fully reloaded.
        bool structuralChange = updatedExports.Contains(file.LevelExport.UIndex);
        if (!structuralChange)
        {
            foreach (var actor in file.Actors)
            {
                if (actor is CollectionActorComponentProxy cacp && updatedExports.Contains(cacp.CollectionActorExport.UIndex))
                {
                    structuralChange = true;
                    break;
                }
            }
        }

        if (!structuralChange
            && _selectedPropertiesExport is not null
            && _selectedPropertiesExportPackage == file.Package
            && updatedExports.Contains(_selectedPropertiesExportUIndex))
        {
            if (file.Actors.FirstOrDefault(actor => actor.TestUIndexes(updatedExports)) is { } actor)
            {
                _isRefreshingActorFromPackageUpdate = true;
                try
                {
                    actor.RefreshFromExport();
                }
                finally
                {
                    _isRefreshingActorFromPackageUpdate = false;
                }

                if (actor == SelectedActor)
                {
                    _preEditSnapshot = SelectedActor.SnapshotTransform();
                }
            }

            if (file.Package.GetEntry(_selectedPropertiesExportUIndex) is ExportEntry updatedPropertiesExport)
            {
                _selectedPropertiesExport = updatedPropertiesExport;
                LevelEditorInterpreter.LoadExport(updatedPropertiesExport);
                LevelEditorMetadata.LoadExport(updatedPropertiesExport);
            }

            SceneViewer?.MarkRenderDirty();
            UpdateGlobalDirtyState();
            return;
        }

        if (updatedExports.Contains(file.LevelExport.UIndex))
        {
            ReloadFile(file);
        }
        else
        {
            bool updated = false;
            int reselectUIndex = 0;
            (Vector3, float, float) savedCamPOV = default;
            Vector3 savedActorPos = default;
            HashSet<int> collectionActorsToUpdate = [];
            for (int i = file.Actors.Count - 1; i >= 0; i--)
            {
                ActorProxy alteredActor = file.Actors[i];
                if (alteredActor.TestUIndexes(updatedExports))
                {
                    updated = true;
                    if (alteredActor == SelectedActor)
                    {
                        reselectUIndex = alteredActor.Export.UIndex;
                        savedCamPOV = (RenderContext.Camera.Position, RenderContext.Camera.Pitch, RenderContext.Camera.Yaw);
                        savedActorPos = SelectedActor.Location;
                    }
                    if (alteredActor is CollectionActorComponentProxy cacp)
                    {
                        collectionActorsToUpdate.Add(cacp.CollectionActorExport.UIndex);
                        continue;
                    }
                    RemoveActor(alteredActor);
                    if (file.Package.GetEntry(alteredActor.Export.UIndex) is ExportEntry actorExport
                        && ActorProxy.Create(this, actorExport) is { } actorProxy)
                    {
                        actorProxy.OwningFile = file;
                        AddActor(actorProxy);
                    }
                }
            }
            foreach (int collectionActorUIndex in collectionActorsToUpdate)
            {
                for (int i = file.Actors.Count - 1; i >= 0; i--)
                {
                    if (file.Actors[i] is CollectionActorComponentProxy cacp
                        && cacp.CollectionActorExport.UIndex == collectionActorUIndex)
                    {
                        RemoveActor(file.Actors[i]);
                    }
                }
                if (file.Package.GetEntry(collectionActorUIndex) is ExportEntry newCollectionActor)
                {
                    string className = newCollectionActor.ClassName;
                    if (className is "StaticMeshCollectionActor")
                    {
                        var smca = newCollectionActor.GetBinaryData<StaticMeshCollectionActor>();
                        for (int i = 0; i < smca.Components.Count; i++)
                        {
                            if (file.Package.TryGetUExport(smca.Components[i], out ExportEntry smcExport))
                            {
                                var smcActor = new StaticMeshComponentActorProxy(this, smcExport, smca, i);
                                smcActor.OwningFile = file;
                                AddActor(smcActor, false);
                            }
                        }
                    }
                    else if (className is "StaticLightCollectionActor")
                    {
                        var slca = newCollectionActor.GetBinaryData<StaticLightCollectionActor>();
                        for (int i = 0; i < slca.Components.Count; i++)
                        {
                            if (file.Package.TryGetUExport(slca.Components[i], out ExportEntry lightExport))
                            {
                                ActorProxy lightActor = GlobalUnrealObjectInfo.IsA(lightExport.ClassName, "SpotLightComponent", lightExport.Game)
                                    ? new SpotLightComponentActorProxy(this, lightExport, slca, i)
                                    : GlobalUnrealObjectInfo.IsA(lightExport.ClassName, "DirectionalLightComponent", lightExport.Game)
                                        ? new DirectionalLightComponentActorProxy(this, lightExport, slca, i)
                                        : new PointLightComponentActorProxy(this, lightExport, slca, i);
                                lightActor.OwningFile = file;
                                AddActor(lightActor, false);
                            }
                        }
                    }
                }
            }
            if (updated)
            {
                Actors.Sort(a => a.Export.UIndex);
                UpdateGlobalDirtyState();
            }

            if (_pendingSelect.UIndex > 0 && _pendingSelect.Package == file.Package)
            {
                SelectedActor = Actors.FirstOrDefault(a => a.Export.UIndex == _pendingSelect.UIndex && a.Export.FileRef == file.Package);
                _pendingSelect = default;
            }
            else if (reselectUIndex is not 0)
            {
                SelectedActor = Actors.FirstOrDefault(a => a.Export.UIndex == reselectUIndex && a.Export.FileRef == file.Package);
                if (SelectedActor is not null)
                {
                    (RenderContext.Camera.Position, RenderContext.Camera.Pitch, RenderContext.Camera.Yaw)
                    = (savedCamPOV.Item1 + SelectedActor.Location - savedActorPos, savedCamPOV.Item2, savedCamPOV.Item3);
                }
            }
        }
    }

    private void ReloadFile(OpenLevelFile file)
    {
        // Remove all actors for this file, then re-load
        (Vector3, float, float) savedCamPOV = default;
        Vector3 savedActorPos = default;
        int reselectUIndex = 0;
        if (SelectedActor is not null && file.Actors.Contains(SelectedActor))
        {
            savedCamPOV = (RenderContext.Camera.Position, RenderContext.Camera.Pitch, RenderContext.Camera.Yaw);
            savedActorPos = SelectedActor.Location;
            reselectUIndex = SelectedActor.Export.UIndex;
            SelectedActor = null;
        }

        var actorsToReload = file.Actors.ToList();
        Actors.RemoveRange(actorsToReload);
        foreach (var actor in actorsToReload)
        {
            RenderContext.RemoveActor(actor);
            actor.Dispose();
        }
        file.Actors.Clear();

        Level levelBin = file.LevelExport.GetBinaryData<Level>();
        var (actors, _) = LoadActors(levelBin, file);
        var sorted = actors.OrderBy(a => a.Export.UIndex).ToList();
        file.Actors.AddRange(sorted);
        Actors.AddRange(sorted);
        RenderContext.LoadActors(sorted);

        if (_pendingSelect.UIndex > 0 && _pendingSelect.Package == file.Package)
            {
                var pendingSelect = Actors.FirstOrDefault(a => a.Export.UIndex == _pendingSelect.UIndex && a.Export.FileRef == file.Package);
                _pendingSelect = default;
                if (pendingSelect is not null)
                {
                    SelectedActor = pendingSelect;
                }
            }
            else if (reselectUIndex is not 0)
            {
                var reselect = Actors.FirstOrDefault(a => a.Export.UIndex == reselectUIndex && a.Export.FileRef == file.Package);
                if (reselect is not null)
                {
                    SelectedActor = reselect;
                    (RenderContext.Camera.Position, RenderContext.Camera.Pitch, RenderContext.Camera.Yaw)
                    = (savedCamPOV.Item1 + reselect.Location - savedActorPos, savedCamPOV.Item2, savedCamPOV.Item3);
                }
            }

            file.IsDirty = false;
        }

    #endregion

    public void UpdateGlobalDirtyState()
    {
        IsDirty = OpenFiles.Any(f => f.IsDirty);
    }

    public override void HandleUpdate(List<PackageUpdate> updates) { }

    private bool PackageIsLoaded() => OpenFiles.Count > 0;

    private void OnActorPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_isRefreshingActorFromPackageUpdate) return;
        if (_isApplyingUndoRedo || RenderContext.TransformWidget.IsDragging) return;

        if (e.PropertyName is nameof(ActorProxy.LightRadius)
            or nameof(ActorProxy.Brightness)
            or nameof(ActorProxy.InnerConeAngle)
            or nameof(ActorProxy.OuterConeAngle)
            or nameof(ActorProxy.LightColor)
            or nameof(ActorProxy.LightEnv_BouncedModulationColor)
            or nameof(ActorProxy.ApplyBouncedModulationColor))
        {
            SceneViewer?.MarkRenderDirty();
            return;
        }

        if (e.PropertyName is not (nameof(ActorProxy.Location) or nameof(ActorProxy.Rotation) or nameof(ActorProxy.DrawScale) or nameof(ActorProxy.DrawScale3D))) return;

        SceneViewer?.MarkRenderDirty();
        RefreshSelectedPropertiesPreview();
        if (sender is ActorProxy actor && _preEditSnapshot is { } before)
        {
            var after = actor.SnapshotTransform();
            if (!before.Equals(after))
            {
                UndoHistory.Push(new TransformAction(actor, before, after, $"Edit {actor.Export.ObjectName.Instanced}"));
                _preEditSnapshot = after;
            }
        }
    }

    private void OnWidgetDragComplete(ActorProxy actor, TransformSnapshot before, TransformSnapshot after)
    {
        if (before.Equals(after)) return;
        UndoHistory.Push(new TransformAction(actor, before, after, $"Drag {actor.Export.ObjectName.Instanced}"));
        _preEditSnapshot = after;
        RefreshSelectedPropertiesPreview();
    }

    private bool ActorFilter(object obj)
    {
        if (string.IsNullOrEmpty(_actorFilterText)) return true;
        return obj is ActorProxy actor &&
               (actor.Export.ObjectName.Instanced.Contains(_actorFilterText, StringComparison.OrdinalIgnoreCase)
                || actor.Tag.Instanced.Contains(_actorFilterText, StringComparison.OrdinalIgnoreCase));
    }

    private void ActorFilter_TextBox_KeyUp(object sender, KeyEventArgs e)
    {
        _actorFilterText = ActorFilter_TextBox.Text;
        ActorsView.Refresh();
    }

    private void Goto_TextBox_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && !e.IsRepeat)
        {
            GotoButton_Clicked(null, null);
        }
    }

    private void GotoButton_Clicked(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(Goto_TextBox.Text, out int uIdx)
            && Actors.FirstOrDefault(a => a.Export.UIndex == uIdx) is ActorProxy actor)
        {
            SelectedActor = actor;
        }
    }

    #region Open / Drag-Drop

    private async void OpenFile()
    {
        var d = AppDirectories.GetOpenPackageDialog();
        if (d.ShowDialog() == true)
        {

#if !DEBUG
            try
            {
#endif
                await LoadFileAsync(d.FileName);
#if !DEBUG
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to open file:" + ex.Message);
            }
#endif
        }
    }

    private async void AddFile()
    {
        var d = AppDirectories.GetOpenPackageDialog();
        if (d.ShowDialog() == true)
        {

#if !DEBUG
            try
            {
#endif
                using var guard = new RenderGuard(this);
                await AddLevelFile(d.FileName).ConfigureAwait(true);
#if !DEBUG
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to open file:" + ex.Message);
            }
#endif
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            string ext = Path.GetExtension(files[0]).ToLower();
            if (ext != ".upk" && ext != ".pcc" && ext != ".sfm")
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            bool isFirst = true;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length is 0) return;
            if (PackageIsLoaded())
            {
                string q = files.Length is 1 ? "these files" : "";
                var result = MessageBox.Show("Do you want to add" + q + "to the existing level view? Select no to unload all open files first.", "Add to files?", MessageBoxButton.YesNoCancel);
                if (result == MessageBoxResult.Cancel) return;
                isFirst = result == MessageBoxResult.No;
            }
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext is not (".upk" or ".pcc" or ".sfm")) continue;

                if (isFirst && OpenFiles.Count == 0)
                {
                    await LoadFileAsync(file);
                    isFirst = false;
                }
                else
                {
                    using var guard = new RenderGuard(this);

                    await AddLevelFile(file).ConfigureAwait(true);
                    isFirst = false;
                }
            }
        }
    }

    #endregion

    #region Window Lifecycle

    private void LevelEditor_Closing(object sender, CancelEventArgs e)
    {
        if (e.Cancel) return;

        var dirtyFiles = OpenFiles.Where(f => f.IsDirty || f.Package.IsModified).ToList();
        if (dirtyFiles.Count > 0)
        {
            string fileNames = string.Join(",\n", dirtyFiles.Select(f => f.FileName));
            var result = MessageBox.Show(this,
                $"The following files have unsaved changes:\n{fileNames}\n\nClose anyway?",
                "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
        }

        CloseAllFiles();

        RenderContext.UpdateScene -= UpdateScene;
        RenderContext.RenderScene -= RenderScene;
        RenderContext.SelectActor -= ViewportActorSelect;
        RenderContext.RightClickActor -= OnViewportRightClickActor;

        UndoHistory.PropertyChanged -= UndoHistory_PropertyChanged;
        UndoHistory.Clear();

        SceneViewer.Dispose();
    }

    private void LevelEditor_Loaded(object sender, RoutedEventArgs e)
    {
        RenderContext.UpdateScene += UpdateScene;
        RenderContext.RenderScene += RenderScene;
        RenderContext.SelectActor += ViewportActorSelect;
        RenderContext.RightClickActor += OnViewportRightClickActor;

        if (_unlit)
            RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Unlit;

        if (!string.IsNullOrEmpty(FileQueuedForLoad))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                LoadFileAsync(FileQueuedForLoad);
                FileQueuedForLoad = null;

                Activate();
            }));
        }
    }

    #endregion

    #region Recent File Sets

    private void LoadRecentSets()
    {
        if (!File.Exists(RecentSetsFile)) return;
        try
        {
            var json = File.ReadAllText(RecentSetsFile);
            var sets = JsonConvert.DeserializeObject<List<RecentFileSet>>(json);
            if (sets is null) return;
            foreach (var set in sets)
            {
                set.FilePaths.RemoveAll(p => !File.Exists(p));
                if (set.FilePaths.Count > 0)
                    RecentSets.Add(set);
            }
        }
        catch { /* corrupt file, ignore */ }
        RefreshRecentsMenu();
    }

    private void SaveRecentSets()
    {
        var json = JsonConvert.SerializeObject(RecentSets.ToList(), Formatting.Indented);
        File.WriteAllText(RecentSetsFile, json);
        RefreshRecentsMenu();
    }

    private void RecordCurrentFilesAsRecent()
    {
        if (OpenFiles.Count == 0) return;
        var currentPaths = OpenFiles.Select(f => f.FilePath).ToList();

        for (int i = 0; i < RecentSets.Count; i++)
        {
            var existing = RecentSets[i].FilePaths;
            if (existing.Count > 0 && existing[0] == currentPaths[0])
            {
                RecentSets.RemoveAt(i);
            }
        }

        RecentSets.Insert(0, new RecentFileSet
        {
            Game = Game,
            FilePaths = currentPaths,
            ReadOnlyFilePaths = OpenFiles.Where(f => f.IsReadOnly).Select(f => f.FilePath).ToList()
        });

        while (RecentSets.Count > 10)
            RecentSets.RemoveAt(RecentSets.Count - 1);

        SaveRecentSets();
    }

    private async void OpenRecentFileSet(RecentFileSet set)
    {
        CloseAllFiles();

        using var guard = new RenderGuard(this);

        foreach (string path in set.FilePaths)
        {
            if (File.Exists(path))
            {
                await AddLevelFile(path).ConfigureAwait(true);
                var openFile = OpenFiles.LastOrDefault(f => f.FilePath == path);
                if (openFile is not null && set.ReadOnlyFilePaths.Contains(path))
                    openFile.IsReadOnly = true;
            }
        }
    }

    private void RefreshRecentsMenu()
    {
        Recents_MenuItem.Items.Clear();
        Recents_MenuItem.IsEnabled = RecentSets.Count > 0;
        foreach (var set in RecentSets)
        {
            var mi = new MenuItem
            {
                Header = set.DisplayName.Replace("_", "__"),
                ToolTip = set.TooltipText,
                Tag = set
            };
            mi.Click += (_, _) => OpenRecentFileSet((RecentFileSet)mi.Tag);
            Recents_MenuItem.Items.Add(mi);
        }
    }

    #endregion

    #region UI Properties

    private float _posIncrement = 10f;
    public float PosIncrement
    {
        get => _posIncrement;
        set => SetProperty(ref _posIncrement, value);
    }

    private float _rotIncrement = 5f;
    public float RotIncrement
    {
        get => _rotIncrement;
        set => SetProperty(ref _rotIncrement, value);
    }

    private float _scaleIncrement = 0.1f;
    public float ScaleIncrement
    {
        get => _scaleIncrement;
        set => SetProperty(ref _scaleIncrement, value);
    }

    private string textBelowActors;
    public string TextBelowActors { get => textBelowActors; set => SetProperty(ref textBelowActors, value); }

    private string _currentModeName = "Translate";
    public string CurrentModeName { get => _currentModeName; set => SetProperty(ref _currentModeName, value); }

    private bool _isOrthographicView;
    public bool IsOrthographicView
    {
        get => _isOrthographicView;
        set
        {
            if (SetProperty(ref _isOrthographicView, value))
            {
                if (value)
                {
                    RenderContext.Camera.SavePerspectiveState();
                    RenderContext.Camera.IsOrthographic = true;
                    var pos = RenderContext.Camera.Position;
                    RenderContext.Camera.Position = new Vector3(pos.X, pos.Y, RenderContext.Camera.ZFar * 0.4f);
                    float focusDepth = RenderContext.Camera.FocusDepth;
                    RenderContext.Camera.OrthoWidth = MathF.Max(focusDepth * 4f, 500f);
                }
                else
                {
                    RenderContext.Camera.IsOrthographic = false;
                    RenderContext.Camera.RestorePerspectiveState();
                }
            }
        }
    }

    private void ResetUncommittedChanges_Click(object sender, RoutedEventArgs e)
    {
        if (IsDirty && MessageBox.Show("Are you sure you want to reset uncommitted changes?", "Reset confirmation", MessageBoxButton.YesNo) is MessageBoxResult.Yes)
        {
            foreach (var file in OpenFiles)
            {
                ReloadFile(file);
            }
        }
    }

    #endregion



    #region Busy variables

    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private bool _isBusyTaskbar;

    public bool IsBusyTaskbar
    {
        get => _isBusyTaskbar;
        set => SetProperty(ref _isBusyTaskbar, value);
    }

    private string _busyText;

    public string BusyText
    {
        get => _busyText;
        set => SetProperty(ref _busyText, value);
    }

    public virtual void SetBusy(string text = null)
    {
        BusyText = text;
        IsBusy = true;
    }
    public virtual void EndBusy()
    {
        IsBusy = false;
    }

    public void HandleSaveStateChange(bool isSaving)
    {
        if (isSaving)
        {
            SetBusy("Saving");
        }
        else
        {
            EndBusy();
        }
    }

    #endregion

    #region Properties / Metadata panel

    public ObservableCollectionExtended<ExportEntry> PropertiesExportList { get; } = [];

    private ExportEntry _selectedPropertiesExport;
    private IMEPackage _selectedPropertiesExportPackage;
    private int _selectedPropertiesExportUIndex;

    public ExportEntry SelectedPropertiesExport
    {
        get => _selectedPropertiesExport;
        set
        {
            if (SetProperty(ref _selectedPropertiesExport, value) && value is not null)
            {
                _selectedPropertiesExportPackage = value.FileRef;
                _selectedPropertiesExportUIndex = value.UIndex;
                LevelEditorInterpreter.LoadExport(value);
                LevelEditorMetadata.LoadExport(value);
            }
        }
    }

    private void LoadExportIntoTabs(ExportEntry export, int tabIndex)
    {
        if (export is null) return;
        PropertiesTabControl.SelectedIndex = tabIndex;
        SelectedPropertiesExport = export;
    }

    private void RefreshSelectedPropertiesPreview()
    {
        if (SelectedActor is null || SelectedPropertiesExport is null) return;
        if (SelectedPropertiesExport.FileRef != SelectedActor.Export.FileRef || SelectedPropertiesExport.UIndex != SelectedActor.Export.UIndex) return;

        LevelEditorInterpreter.LoadExport(SelectedActor.Export, SelectedActor.GetPropertiesForInterpreter());
    }

    private void RefreshPropertiesExportSelection(ActorProxy actor, int tabIndex)
    {
        PropertiesExportList.Clear();
        PropertiesExportList.Add(actor.Export);
        foreach (var component in actor.Components)
        {
            if (component.Export.UIndex != selectedActor.Export.UIndex)
            {
                PropertiesExportList.Add(component.Export);
            }
        }

        ExportEntry exportToLoad = PropertiesExportList.FirstOrDefault(x => x.FileRef == _selectedPropertiesExportPackage && x.UIndex == _selectedPropertiesExportUIndex)
                                 ?? actor.Export;
        LoadExportIntoTabs(exportToLoad, tabIndex);
    }

    private void UnloadPropertyTabs()
    {
        if (_selectedPropertiesExport is null && PropertiesExportList.Count == 0) return;
        PropertiesExportList.Clear();
        _selectedPropertiesExport = null;
        OnPropertyChanged(nameof(SelectedPropertiesExport));
        LevelEditorInterpreter.UnloadExport();
        LevelEditorMetadata.UnloadExport();
    }

    private void OnViewportRightClickActor(ActorProxy actor)
    {
        var contextMenu = BuildActorContextMenu(actor);
        contextMenu.PlacementTarget = SceneViewer;
        contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        contextMenu.IsOpen = true;
    }

    private void MeshExportsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement fe) { e.Handled = true; return; }

        ActorProxy actor = null;
        DependencyObject current = fe;
        while (current is not null)
        {
            if (current is ListBoxItem lbi && lbi.DataContext is ActorProxy a) { actor = a; break; }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        if (actor is null) { e.Handled = true; return; }

        SelectedActor = actor;
        var contextMenu = BuildActorContextMenu(actor);
        contextMenu.PlacementTarget = fe;
        contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private System.Windows.Controls.ContextMenu BuildActorContextMenu(ActorProxy actor)
    {
        var contextMenu = new System.Windows.Controls.ContextMenu();

        AddPropertiesMenuItems(contextMenu, actor.Export,
            $"{actor.Export.UIndex}: {actor.Export.ObjectName.Instanced} ({actor.Export.ClassName})");

        if (actor.Components.Count > 0)
        {
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            foreach (var component in actor.Components)
            {
                AddPropertiesMenuItems(contextMenu, component.Export,
                    $"{component.Export.UIndex}: {component.Export.ObjectName.Instanced} ({component.Export.ClassName})");
            }
        }

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var focusItem = new System.Windows.Controls.MenuItem { Header = "Focus Camera" };
        focusItem.Click += (_, _) =>
        {
            SelectedActor = actor;
            FocusOnBounds(actor.GetBounds());
        };
        contextMenu.Items.Add(focusItem);

        var openPEItem = new System.Windows.Controls.MenuItem { Header = "Open in Package Editor" };
        openPEItem.Click += (_, _) =>
        {
            var p = new PackageEditorWindow();
            p.Show();
            p.LoadFile(actor.Export.FileRef.FilePath, actor.Export.UIndex);
            p.Activate();
        };
        contextMenu.Items.Add(openPEItem);

        ExportEntry lightingTargetExport = GetLightingChannelsTargetExport(actor);
        if (lightingTargetExport is not null)
        {
            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            var lightingChannelsMenu = BuildLightingChannelsMenu(actor, lightingTargetExport);
            if (lightingChannelsMenu is not null)
            {
                contextMenu.Items.Add(lightingChannelsMenu);
            }

            if (lightingTargetExport.IsA("LightComponent"))
            {
                var lightShadowMenu = BuildLightShadowMenu(actor, lightingTargetExport);
                if (lightShadowMenu is not null)
                {
                    contextMenu.Items.Add(lightShadowMenu);
                }

                var enabledItem = new System.Windows.Controls.MenuItem
                {
                    Header = "Enabled",
                    IsCheckable = true,
                    IsChecked = GetBoolPropertyValue(actor.Export, "bEnabled"),
                    IsEnabled = !actor.IsReadOnly,
                    StaysOpenOnClick = true
                };
                enabledItem.Click += (_, _) => SetBoolPropertyValue(actor.Export, "bEnabled", enabledItem.IsChecked);
                contextMenu.Items.Add(enabledItem);
            }
        }

        ExportEntry smcExport = GetStaticMeshComponentExport(actor);
        if (smcExport is not null)
        {
            if (lightingTargetExport is null)
            {
                contextMenu.Items.Add(new System.Windows.Controls.Separator());
            }

            var collisionMenu = BuildCollisionMenu(actor, smcExport);
            if (collisionMenu is not null)
            {
                contextMenu.Items.Add(collisionMenu);
            }

            var shadowMenu = BuildShadowMenu(actor, smcExport);
            if (shadowMenu is not null)
            {
                contextMenu.Items.Add(shadowMenu);
            }

            var stripLightMapItem = new System.Windows.Controls.MenuItem
            {
                Header = "Strip LightMap (EXPERIMENTAL)",
                IsEnabled = !actor.IsReadOnly
            };
            stripLightMapItem.Click += (_, _) => StripStaticMeshComponentLightmap(actor, smcExport);
            contextMenu.Items.Add(stripLightMapItem);

            var stripShadowMapItem = new System.Windows.Controls.MenuItem
            {
                Header = "Strip ShadowMap (EXPERIMENTAL)",
                IsEnabled = !actor.IsReadOnly
            };
            stripShadowMapItem.Click += (_, _) => StripStaticMeshComponentShadowmap(actor, smcExport);
            contextMenu.Items.Add(stripShadowMapItem);

            var replaceMeshItem = new System.Windows.Controls.MenuItem
            {
                Header = "Replace Static Mesh...",
                IsEnabled = !actor.IsReadOnly
            };
            replaceMeshItem.Click += (_, _) => ReplaceStaticMesh(actor, smcExport);
            contextMenu.Items.Add(replaceMeshItem);
        }

        var skeletalMeshComponents = GetSkeletalMeshComponentExports(actor);
        if (skeletalMeshComponents.Count > 0)
        {
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            if (skeletalMeshComponents.Count == 1)
            {
                var replaceSkeletalItem = new System.Windows.Controls.MenuItem
                {
                    Header = "Replace Skeletal Mesh...",
                    IsEnabled = !actor.IsReadOnly
                };
                replaceSkeletalItem.Click += (_, _) => ReplaceSkeletalMesh(actor, skeletalMeshComponents[0]);
                contextMenu.Items.Add(replaceSkeletalItem);
            }
            else
            {
                var skeletalMenu = new System.Windows.Controls.MenuItem
                {
                    Header = "Replace Skeletal Mesh"
                };
                foreach (var smc in skeletalMeshComponents)
                {
                    var subItem = new System.Windows.Controls.MenuItem
                    {
                        Header = $"{smc.UIndex}: {smc.ObjectName.Instanced}",
                        IsEnabled = !actor.IsReadOnly
                    };
                    subItem.Click += (_, _) => ReplaceSkeletalMesh(actor, smc);
                    skeletalMenu.Items.Add(subItem);
                }
                contextMenu.Items.Add(skeletalMenu);
            }
        }

        if (actor is SFXSkeletalMeshActorProxy)
        {
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
            var gestureItem = new System.Windows.Controls.MenuItem
            {
                Header = "Open Gesture Animation Importer...",
                IsEnabled = !actor.IsReadOnly
            };
            gestureItem.Click += (_, _) =>
            {
                var dialog = new GestureAnimationImporterDialog(actor.Export, this);
                dialog.ShowDialog();
            };
            contextMenu.Items.Add(gestureItem);
        }
        else if (actor is SFXStuntActorProxy)
        {
            var gestureModules = actor.Export.FileRef.Exports
                .Where(e => e.idxLink == actor.Export.UIndex && e.ClassName == "SFXModule_Gestures")
                .ToList();
            if (gestureModules.Count > 0)
            {
                contextMenu.Items.Add(new System.Windows.Controls.Separator());
                if (gestureModules.Count == 1)
                {
                    var gestureItem = new System.Windows.Controls.MenuItem
                    {
                        Header = "Open Gesture Animation Importer...",
                        IsEnabled = !actor.IsReadOnly
                    };
                    gestureItem.Click += (_, _) =>
                    {
                        var dialog = new GestureAnimationImporterDialog(gestureModules[0], this);
                        dialog.ShowDialog();
                    };
                    contextMenu.Items.Add(gestureItem);
                }
                else
                {
                    var gestureMenu = new System.Windows.Controls.MenuItem
                    {
                        Header = "Open Gesture Animation Importer"
                    };
                    foreach (var module in gestureModules)
                    {
                        var subItem = new System.Windows.Controls.MenuItem
                        {
                            Header = $"{module.UIndex}: {module.ObjectName.Instanced}",
                            IsEnabled = !actor.IsReadOnly
                        };
                        subItem.Click += (_, _) =>
                        {
                            var dialog = new GestureAnimationImporterDialog(module, this);
                            dialog.ShowDialog();
                        };
                        gestureMenu.Items.Add(subItem);
                    }
                    contextMenu.Items.Add(gestureMenu);
                }
            }
        }

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var cloneTreeItem = new System.Windows.Controls.MenuItem
        {
            Header = "Clone Tree",
            IsEnabled = !actor.IsReadOnly
        };
        cloneTreeItem.Click += (_, _) =>
        {
            SelectedActor = actor;
            CloneActorTree(actor);
        };
        contextMenu.Items.Add(cloneTreeItem);

        var trashItem = new System.Windows.Controls.MenuItem
        {
            Header = "Trash Actor",
            IsEnabled = !actor.IsReadOnly
        };
        trashItem.Click += (_, _) =>
        {
            SelectedActor = actor;
            TrashActor(actor);
        };
        contextMenu.Items.Add(trashItem);

        return contextMenu;
    }

    private void AddPropertiesMenuItems(System.Windows.Controls.ContextMenu contextMenu, ExportEntry export, string label)
    {
        var parentItem = new System.Windows.Controls.MenuItem { Header = label };

        var propsItem = new System.Windows.Controls.MenuItem { Header = "Properties" };
        propsItem.Click += (_, _) => LoadExportIntoTabs(export, 0);
        parentItem.Items.Add(propsItem);

        var metaItem = new System.Windows.Controls.MenuItem { Header = "Metadata" };
        metaItem.Click += (_, _) => LoadExportIntoTabs(export, 1);
        parentItem.Items.Add(metaItem);

        contextMenu.Items.Add(parentItem);
    }

    private System.Windows.Controls.MenuItem BuildLightingChannelsMenu(ActorProxy actor, ExportEntry componentExport)
    {
        if (componentExport is null)
        {
            return null;
        }

        var lightingChannelsMenu = new System.Windows.Controls.MenuItem
        {
            Header = "Lighting Channels",
            IsEnabled = !actor.IsReadOnly
        };

        foreach ((string propertyName, string displayName) in LightingChannelMenuItems)
        {
            var channelItem = new System.Windows.Controls.MenuItem
            {
                Header = displayName,
                IsCheckable = true,
                IsChecked = GetLightingChannelValue(componentExport, propertyName),
                IsEnabled = !actor.IsReadOnly,
                StaysOpenOnClick = true
            };
            channelItem.Click += (_, _) =>
            {
                SetLightingChannelValue(componentExport, propertyName, channelItem.IsChecked);
                actor.Components.FirstOrDefault(c => c.Export == componentExport)?.RefreshFromExport();
                SceneViewer?.MarkRenderDirty();
            };
            lightingChannelsMenu.Items.Add(channelItem);
        }

        if (componentExport.IsA("StaticMeshComponent"))
        {
            var lightingMenu = new System.Windows.Controls.MenuItem
            {
                Header = "Lighting",
                IsEnabled = !actor.IsReadOnly
            };

            foreach ((string propertyName, string displayName) in LightingMenuItems)
            {
                var lightingItem = new System.Windows.Controls.MenuItem
                {
                    Header = displayName,
                    IsCheckable = true,
                    IsChecked = GetBoolPropertyValue(componentExport, propertyName),
                    IsEnabled = !actor.IsReadOnly,
                    StaysOpenOnClick = true
                };
                lightingItem.Click += (_, _) => SetBoolPropertyValue(componentExport, propertyName, lightingItem.IsChecked);
                lightingMenu.Items.Add(lightingItem);
            }

            lightingChannelsMenu.Items.Add(new Separator());
            lightingChannelsMenu.Items.Add(lightingMenu);
        }

        return lightingChannelsMenu;
    }

    private static bool GetLightingChannelValue(ExportEntry export, string propertyName)
    {
        var lightingChannels = export.GetProperty<StructProperty>("LightingChannels");
        if (lightingChannels is null)
        {
            return false;
        }

        if (propertyName == "bInitialized")
        {
            return lightingChannels.Properties.GetProp<BoolProperty>("bInitialized")?.Value
                   ?? lightingChannels.Properties.GetProp<BoolProperty>("bIsInitialized")?.Value
                   ?? false;
        }

        return lightingChannels.Properties.GetProp<BoolProperty>(NameReference.FromInstancedString(propertyName))?.Value ?? false;
    }

    private System.Windows.Controls.MenuItem BuildCollisionMenu(ActorProxy actor, ExportEntry componentExport)
    {
        if (componentExport is null)
        {
            return null;
        }

        var collisionMenu = new System.Windows.Controls.MenuItem
        {
            Header = "Collision",
            IsEnabled = !actor.IsReadOnly
        };

        foreach ((string propertyName, string displayName) in CollisionMenuItems)
        {
            var collisionItem = new System.Windows.Controls.MenuItem
            {
                Header = displayName,
                IsCheckable = true,
                IsChecked = GetBoolPropertyValue(componentExport, propertyName),
                IsEnabled = !actor.IsReadOnly,
                StaysOpenOnClick = true
            };
            collisionItem.Click += (_, _) => SetBoolPropertyValue(componentExport, propertyName, collisionItem.IsChecked);
            collisionMenu.Items.Add(collisionItem);
        }

        return collisionMenu;
    }

    private System.Windows.Controls.MenuItem BuildShadowMenu(ActorProxy actor, ExportEntry componentExport)
    {
        if (componentExport is null)
        {
            return null;
        }

        var shadowMenu = new System.Windows.Controls.MenuItem
        {
            Header = "Shadow",
            IsEnabled = !actor.IsReadOnly
        };

        foreach ((string propertyName, string displayName) in ShadowMenuItems)
        {
            var shadowItem = new System.Windows.Controls.MenuItem
            {
                Header = displayName,
                IsCheckable = true,
                IsChecked = GetBoolPropertyValue(componentExport, propertyName),
                IsEnabled = !actor.IsReadOnly,
                StaysOpenOnClick = true
            };
            shadowItem.Click += (_, _) => SetBoolPropertyValue(componentExport, propertyName, shadowItem.IsChecked);
            shadowMenu.Items.Add(shadowItem);
        }

        return shadowMenu;
    }

    private System.Windows.Controls.MenuItem BuildLightShadowMenu(ActorProxy actor, ExportEntry componentExport)
    {
        if (componentExport is null)
        {
            return null;
        }

        var shadowMenu = new System.Windows.Controls.MenuItem
        {
            Header = "Shadows",
            IsEnabled = !actor.IsReadOnly
        };

        foreach ((string propertyName, string displayName) in LightShadowMenuItems)
        {
            var shadowItem = new System.Windows.Controls.MenuItem
            {
                Header = displayName,
                IsCheckable = true,
                IsChecked = GetBoolPropertyValue(componentExport, propertyName),
                IsEnabled = !actor.IsReadOnly,
                StaysOpenOnClick = true
            };
            shadowItem.Click += (_, _) => SetBoolPropertyValue(componentExport, propertyName, shadowItem.IsChecked);
            shadowMenu.Items.Add(shadowItem);
        }

        return shadowMenu;
    }

    private static bool GetBoolPropertyValue(ExportEntry export, string propertyName)
    {
        return export.GetProperty<BoolProperty>(propertyName)?.Value ?? false;
    }

    private static void SetBoolPropertyValue(ExportEntry export, string propertyName, bool value)
    {
        PropertyCollection props = export.GetProperties();
        props.AddOrReplaceProp(new BoolProperty(value, propertyName));
        export.WriteProperties(props);
    }

    private static void SetLightingChannelValue(ExportEntry export, string propertyName, bool value)
    {
        PropertyCollection props = export.GetProperties();
        StructProperty lightingChannels = GetOrCreateLightingChannelsProperty(props);
        if (propertyName == "bInitialized")
        {
            string initPropertyName = lightingChannels.Properties.GetProp<BoolProperty>("bInitialized") is not null
                ? "bInitialized"
                : lightingChannels.Properties.GetProp<BoolProperty>("bIsInitialized") is not null
                    ? "bIsInitialized"
                    : "bInitialized";
            lightingChannels.Properties.AddOrReplaceProp(new BoolProperty(value, initPropertyName));
        }
        else
        {
            NameReference nameRef = NameReference.FromInstancedString(propertyName);
            lightingChannels.Properties.AddOrReplaceProp(new BoolProperty(value, nameRef));
        }
        props.AddOrReplaceProp(lightingChannels);
        export.WriteProperties(props);
    }

    private static StructProperty GetOrCreateLightingChannelsProperty(PropertyCollection props)
    {
        var lightingChannels = props.GetProp<StructProperty>("LightingChannels");
        if (lightingChannels is null)
        {
            lightingChannels = new StructProperty("LightingChannelContainer", false,
                new BoolProperty(true, "bInitialized"))
            {
                Name = "LightingChannels"
            };
            return lightingChannels;
        }

        if (lightingChannels.Properties.GetProp<BoolProperty>("bIsInitialized") is null
            && lightingChannels.Properties.GetProp<BoolProperty>("bInitialized") is null)
        {
            lightingChannels.Properties.AddOrReplaceProp(new BoolProperty(true, "bInitialized"));
        }

        return lightingChannels;
    }

    #endregion

    #region Clone / Trash

    private void CloneActorTree()
    {
        if (SelectedActor is null) return;
        CloneActorTree(SelectedActor);
    }

    private void CloneActorTree(ActorProxy actor)
    {
        ExportEntry clonedExport = EntryCloner.CloneTree(actor.Export);

        if (actor is CollectionActorComponentProxy collectionActorComponent)
        {
            if (!TryAddClonedCollectionActorComponent(collectionActorComponent, clonedExport))
            {
                EntryPruner.TrashEntryAndDescendants(clonedExport);
                MessageBox.Show(this,
                    "The collection component was cloned, but it could not be added to the parent collection actor.",
                    "Clone Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }
        else
        {
            Level levelBin = actor.OwningFile.LevelExport.GetBinaryData<Level>();
            levelBin.Actors.Add(clonedExport.UIndex);
            actor.OwningFile.LevelExport.WriteBinary(levelBin);
        }

        _pendingSelect = (clonedExport.UIndex, actor.OwningFile.Package);
        UndoHistory.Clear();
        _preEditSnapshot = null;
    }

    private void TrashActor()
    {
        if (SelectedActor is null) return;
        TrashActor(SelectedActor);
    }

    private void TrashActor(ActorProxy actor)
    {
        if (MessageBox.Show(this,
                $"Permanently delete '{actor.Export.ObjectName.Instanced}'?\n\nThe export and all its children will be trashed and cannot be recovered via Undo.",
                "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (actor is CollectionActorComponentProxy collectionActorComponent)
        {
            if (!TryRemoveCollectionActorComponent(collectionActorComponent))
            {
                MessageBox.Show(this,
                    "The collection component could not be removed from the parent collection actor.",
                    "Delete Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }
        else
        {
            Level levelBin = actor.OwningFile.LevelExport.GetBinaryData<Level>();
            levelBin.Actors.Remove(actor.Export.UIndex);
            actor.OwningFile.LevelExport.WriteBinary(levelBin);
        }

        EntryPruner.TrashEntryAndDescendants(actor.Export);
        UndoHistory.Clear();
        _preEditSnapshot = null;
    }

    private static bool TryAddClonedCollectionActorComponent(CollectionActorComponentProxy actor, ExportEntry clonedExport)
    {
        if (ObjectBinary.From(actor.CollectionActorExport) is not StaticCollectionActor collectionActor)
        {
            return false;
        }

        var componentsProp = actor.CollectionActorExport.GetProperty<ArrayProperty<ObjectProperty>>(collectionActor.ComponentPropName);
        if (componentsProp == null)
        {
            return false;
        }

        int originalIndex = componentsProp.IndexOf(new ObjectProperty(actor.Export));
        if (originalIndex < 0 || originalIndex >= collectionActor.LocalToWorldTransforms.Count)
        {
            return false;
        }

        componentsProp.Add(new ObjectProperty(clonedExport));
        actor.CollectionActorExport.WriteProperty(componentsProp);

        collectionActor.Components ??= [];
        collectionActor.LocalToWorldTransforms ??= [];
        collectionActor.Components.Add(clonedExport.UIndex);
        collectionActor.LocalToWorldTransforms.Add(collectionActor.LocalToWorldTransforms[originalIndex]);
        actor.CollectionActorExport.WriteBinary(collectionActor);
        return true;
    }

    private static bool TryRemoveCollectionActorComponent(CollectionActorComponentProxy actor)
    {
        if (ObjectBinary.From(actor.CollectionActorExport) is not StaticCollectionActor collectionActor)
        {
            return false;
        }

        var componentsProp = actor.CollectionActorExport.GetProperty<ArrayProperty<ObjectProperty>>(collectionActor.ComponentPropName);
        if (componentsProp == null)
        {
            return false;
        }

        int indexToRemove = -1;
        for (int i = 0; i < componentsProp.Count; i++)
        {
            if (componentsProp[i].Value == actor.Export.UIndex)
            {
                indexToRemove = i;
                break;
            }
        }

        if (indexToRemove < 0)
        {
            return false;
        }

        componentsProp.RemoveAt(indexToRemove);
        actor.CollectionActorExport.WriteProperty(componentsProp);

        if (collectionActor.Components != null && indexToRemove < collectionActor.Components.Count)
        {
            collectionActor.Components.RemoveAt(indexToRemove);
        }

        if (collectionActor.LocalToWorldTransforms != null && indexToRemove < collectionActor.LocalToWorldTransforms.Count)
        {
            collectionActor.LocalToWorldTransforms.RemoveAt(indexToRemove);
        }

        actor.CollectionActorExport.WriteBinary(collectionActor);
        return true;
    }

    #endregion

    #region Static Mesh Replacement

    private static ExportEntry GetStaticMeshComponentExport(ActorProxy actor) =>
        actor switch
        {
            StaticMeshActorProxy sma   => sma.StaticMeshComponent?.Export,
            DynamicSMActorProxy  dsma  => dsma.StaticMeshComponent?.Export,
            StaticMeshComponentActorProxy => actor.Export,
            _ => actor.Components
                .Select(component => component.Export)
                .FirstOrDefault(export => export.IsA("StaticMeshComponent"))
        };

    private void StripStaticMeshComponentLightmap(ActorProxy actor, ExportEntry componentExport)
    {
        if (componentExport is null)
        {
            return;
        }

        SelectedActor = actor;
        PackageEditorExperimentsM.StripLightmap(componentExport);
        SceneViewer?.MarkRenderDirty();
    }

    private void StripStaticMeshComponentShadowmap(ActorProxy actor, ExportEntry componentExport)
    {
        if (componentExport is null)
        {
            return;
        }

        SelectedActor = actor;
        PackageEditorExperimentsM.StripShadowmap(componentExport);
        SceneViewer?.MarkRenderDirty();
    }

    private static ExportEntry GetLightingChannelsTargetExport(ActorProxy actor)
    {
        if (actor.Export.IsA("StaticMeshComponent") || actor.Export.IsA("SkeletalMeshComponent") || actor.Export.IsA("LightComponent"))
        {
            return actor.Export;
        }

        return actor.Components
            .Select(component => component.Export)
            .FirstOrDefault(export => export.IsA("StaticMeshComponent") || export.IsA("SkeletalMeshComponent") || export.IsA("LightComponent"));
    }

    private static ExportEntry FindNearestPackageExport(IEntry entry)
    {
        while (entry is not null)
        {
            if (entry is ExportEntry { ClassName: "Package" } packageExport)
            {
                return packageExport;
            }

            entry = entry.Parent;
        }

        return null;
    }

    private ExportEntry ResolveStaticMeshImportParent(ExportEntry sourceMeshExport, IMEPackage destinationPackage)
    {
        List<ExportEntry> sourcePackageChain = [];
        for (IEntry entry = sourceMeshExport.Parent; entry is not null; entry = entry.Parent)
        {
            if (entry is ExportEntry { ClassName: "Package" } packageExport)
            {
                sourcePackageChain.Add(packageExport);
            }
        }

        if (sourcePackageChain.Count == 0)
        {
            return null;
        }

        sourcePackageChain.Reverse();

        ExportEntry currentParent = null;
        foreach (ExportEntry sourcePackageExport in sourcePackageChain)
        {
            currentParent = destinationPackage.CreatePackageExport(sourcePackageExport.ObjectName, currentParent);
        }

        return currentParent;
    }

    private async void ReplaceStaticMesh(ActorProxy actor, ExportEntry componentExport)
    {
        var picker = new StaticMeshPickerDialog(Game, componentExport.FileRef, this);
        if (picker.ShowDialog() != true || picker.SelectedResult is null) return;

        var (sourcePath, sourceUIndex) = picker.SelectedResult.Value;

        IsBusy = true;
        BusyText = "Replacing static mesh...";
        await Task.Delay(1).ConfigureAwait(true);

        try
        {
            if (sourcePath is null)
            {
                // Local mesh – just update the property reference
                var props = componentExport.GetProperties();
                props.AddOrReplaceProp(new ObjectProperty(sourceUIndex, "StaticMesh"));
                componentExport.WriteProperties(props);
            }
            else
            {
                // External mesh – import with dependencies
                using IMEPackage sourcePcc = MEPackageHandler.OpenMEPackage(sourcePath);
                ExportEntry meshExport = sourcePcc.GetUExport(sourceUIndex);
                ExportEntry importParent = ResolveStaticMeshImportParent(meshExport, componentExport.FileRef);

                var rop = new RelinkerOptionsPackage
                {
                    ImportExportDependencies = true,
                    PortImportsMemorySafe = true,
                    Cache = new PackageCache()
                };

                var relinkResults = EntryImporter.ImportAndRelinkEntries(
                    EntryImporter.PortingOption.CloneAllDependencies,
                    meshExport,
                    componentExport.FileRef,
                    importParent,
                    true,
                    rop,
                    out IEntry importedEntry);

                if (importedEntry is ExportEntry importedExport && importParent is not null && importedExport.Parent != importParent)
                {
                    importedExport.Parent = importParent;
                }

                if (importedEntry is not null)
                {
                    var props = componentExport.GetProperties();
                    props.AddOrReplaceProp(new ObjectProperty(importedEntry.UIndex, "StaticMesh"));
                    componentExport.WriteProperties(props);
                }

                if (relinkResults?.Count > 0)
                {
                    string warnings = string.Join("\n", relinkResults.Select(r => r.Message));
                    MessageBox.Show(this, $"Import completed with {relinkResults.Count} relink warning(s):\n{warnings}", "Import Warnings");
                }
            }

            RefreshActorInViewport(actor);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to replace static mesh:\n{ex.Message}", "Error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    private void RefreshActorInViewport(ActorProxy oldActor)
    {
        bool wasSelected = SelectedActor == oldActor;
        var owningFile = oldActor.OwningFile;
        ExportEntry actorExport = oldActor.Export;

        // Remove the old proxy from the scene
        if (Actors.Remove(oldActor))
        {
            owningFile?.Actors.Remove(oldActor);
            RenderContext.RemoveActor(oldActor);
            oldActor.Dispose();
        }

        // Recreate from the same export
        if (ActorProxy.Create(this, actorExport) is { } newActor)
        {
            newActor.OwningFile = owningFile;
            Actors.Add(newActor);
            owningFile?.Actors.Add(newActor);
            RenderContext.AddActor(newActor);
            Actors.Sort(a => a.Export.UIndex);

            if (wasSelected)
            {
                SelectedActor = newActor;
            }
        }
    }

    #region Skeletal Mesh Replacement

    private static List<ExportEntry> GetSkeletalMeshComponentExports(ActorProxy actor) =>
        actor.Components
            .Select(c => c.Export)
            .Where(e => e.IsA("SkeletalMeshComponent"))
            .ToList();

    private async void ReplaceSkeletalMesh(ActorProxy actor, ExportEntry componentExport)
    {
        var picker = new SkeletalMeshPickerDialog(Game, componentExport.FileRef, this);
        if (picker.ShowDialog() != true || picker.SelectedResult is null) return;

        var (sourcePath, sourceUIndex) = picker.SelectedResult.Value;

        IsBusy = true;
        BusyText = "Replacing skeletal mesh...";
        await Task.Delay(1).ConfigureAwait(true);

        try
        {
            if (sourcePath is null)
            {
                // Local mesh – just update the property reference
                var props = componentExport.GetProperties();
                props.AddOrReplaceProp(new ObjectProperty(sourceUIndex, "SkeletalMesh"));
                componentExport.WriteProperties(props);
            }
            else
            {
                // External mesh – import with dependencies
                using IMEPackage sourcePcc = MEPackageHandler.OpenMEPackage(sourcePath);
                ExportEntry meshExport = sourcePcc.GetUExport(sourceUIndex);
                ExportEntry importParent = ResolveStaticMeshImportParent(meshExport, componentExport.FileRef);

                var rop = new RelinkerOptionsPackage
                {
                    ImportExportDependencies = true,
                    PortImportsMemorySafe = true,
                    Cache = new PackageCache()
                };

                var relinkResults = EntryImporter.ImportAndRelinkEntries(
                    EntryImporter.PortingOption.CloneAllDependencies,
                    meshExport,
                    componentExport.FileRef,
                    importParent,
                    true,
                    rop,
                    out IEntry importedEntry);

                if (importedEntry is ExportEntry importedExport && importParent is not null && importedExport.Parent != importParent)
                {
                    importedExport.Parent = importParent;
                }

                if (importedEntry is not null)
                {
                    var props = componentExport.GetProperties();
                    props.AddOrReplaceProp(new ObjectProperty(importedEntry.UIndex, "SkeletalMesh"));
                    componentExport.WriteProperties(props);
                }

                if (relinkResults?.Count > 0)
                {
                    string warnings = string.Join("\n", relinkResults.Select(r => r.Message));
                    MessageBox.Show(this, $"Import completed with {relinkResults.Count} relink warning(s):\n{warnings}", "Import Warnings");
                }
            }

            // Match MaterialInstanceConstants to the new SkeletalMesh
            if (InterpreterExportLoader.CanMatchMaterialsToSkeletalMesh(componentExport))
            {
                InterpreterExportLoader.MatchMaterialsToSkeletalMesh(this, componentExport);
            }

            // Refresh the actor in the viewport so the new mesh is visible immediately
            RefreshActorInViewport(actor);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to replace skeletal mesh:\n{ex.Message}", "Error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    private void AddOtherLevel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedActor is null) return;
        string levelName = NewOtherLevel_TextBox.Text?.Trim();
        if (string.IsNullOrEmpty(levelName)) return;
        SelectedActor.AddOtherLevelToAffect(levelName);
        NewOtherLevel_TextBox.Text = "";
    }

    private void RemoveOtherLevel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedActor is null || OtherLevelsListBox.SelectedItem is not string selectedLevel) return;
        SelectedActor.RemoveOtherLevelFromAffect(selectedLevel);
    }

    private readonly struct RenderGuard : IDisposable
    {
        private readonly LevelEditor levelEditor;

        public RenderGuard(LevelEditor levEd)
        {
            levelEditor = levEd;
            levelEditor.SceneViewer.SetShouldRender(false);
            levelEditor.SetBusy();
        }

        public readonly void Dispose()
        {
            levelEditor.SceneViewer.SetShouldRender(true);
            levelEditor.EndBusy();
        }
    }

}
