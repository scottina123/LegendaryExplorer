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
using LegendaryExplorer.Tools.AssetViewer;
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
using System.Threading;
using
System.Threading.Tasks;
using System.Windows;
using
System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        ("BlockRigidBody", "Block Rigid Body"),
        ("BlockNonZeroExtent", "Block Non Zero Extent"),
        ("BlockZeroExtent", "Block Zero Extent")
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
    public bool PreviewConfiguredActorAnimations => true;

    public ObservableCollectionExtended<OpenLevelFile> OpenFiles { get; } = [];
    private OpenLevelFile _activeFile;
    public OpenLevelFile ActiveFile
    {
        get => _activeFile;
        private set
        {
            if (SetProperty(ref _activeFile, value))
            {
                if (value is null)
                {
                    UnLoadMEPackage();
                }
                else
                {
                    RegisterPackage(value.Package);
                }

                UpdateStatusBarText();
            }
        }
    }

    public ObservableCollectionExtended<ActorProxy> Actors { get; } = [];
    public ICollectionView ActorsView { get; }
    private string _actorFilterText = "";
    private readonly record struct VisibleActor(ActorProxy Actor, BoxSphereBounds Bounds, Vector3 HitTestId);
    private readonly List<VisibleActor> visibleActors = [];

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
    private ActorProxy _levelLiveMaterialActor;
    private IMEPackage _levelLiveMaterialActorPackage;
    private int _levelLiveMaterialActorUIndex;
    private Point? _levelMaterialPickMouseDownPosition;
    private bool _isLevelMaterialEditorOpen;
    public bool IsLevelMaterialEditorOpen
    {
        get => _isLevelMaterialEditorOpen;
        private set => SetProperty(ref _isLevelMaterialEditorOpen, value);
    }
    private IMEPackage _levelMorphEditorActorPackage;
    private int _levelMorphEditorActorUIndex;
    private bool _isLevelMorphEditorOpen;
    public bool IsLevelMorphEditorOpen
    {
        get => _isLevelMorphEditorOpen;
        private set => SetProperty(ref _isLevelMorphEditorOpen, value);
    }
    private string _levelMorphEditorTitle = "Morph Editor";
    public string LevelMorphEditorTitle
    {
        get => _levelMorphEditorTitle;
        private set => SetProperty(ref _levelMorphEditorTitle, value);
    }
    private char _actorLocationScrubAxis = 'X';
    private double _actorLocationScrubAccumulator;
    private double _actorLocationScrubPreviousHorizontalChange;
    private string _actorScaleScrubAxes = "X";
    private double _actorScaleScrubAccumulator;
    private double _actorScaleScrubPreviousHorizontalChange;
    private string _actorRotationDialAxis = nameof(ActorProxy.PitchDegrees);
    private bool _actorRotationDialDragging;
    private double _actorRotationDialAngleAccumulator;
    private double _actorRotationDialPreviousAngle;
    private bool _isActorTransformScrubbing;
    private ActorProxy _actorTransformScrubActor;
    private TransformSnapshot? _actorTransformScrubBefore;
    private double _lightRadiusScrubAccumulator;
    private double _lightRadiusScrubPreviousHorizontalChange;
    private double _lightBrightnessScrubAccumulator;
    private double _lightBrightnessScrubPreviousHorizontalChange;
    private string _lightDialProperty = nameof(ActorProxy.InnerConeAngle);
    private bool _lightValueDialDragging;
    private double _lightValueDialAccumulator;
    private double _lightValueDialPreviousAngle;
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

    private bool _showPathing;
    public bool ShowPathing
    {
        get => _showPathing;
        set
        {
            if (SetProperty(ref _showPathing, value))
            {
                RenderContext.NavigationOverlay.ShowPaths = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private bool _showCover;
    public bool ShowCover
    {
        get => _showCover;
        set
        {
            if (SetProperty(ref _showCover, value))
            {
                RenderContext.NavigationOverlay.ShowCover = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private float _navigationPawnRadius = 34f;
    public float NavigationPawnRadius { get => _navigationPawnRadius; set => SetProperty(ref _navigationPawnRadius, value); }
    private float _navigationPawnHeight = 90f;
    public float NavigationPawnHeight { get => _navigationPawnHeight; set => SetProperty(ref _navigationPawnHeight, value); }
    private float _navigationGridSpacing = 256f;
    public float NavigationGridSpacing { get => _navigationGridSpacing; set => SetProperty(ref _navigationGridSpacing, value); }
    private float _navigationMaximumSlope = 45f;
    public float NavigationMaximumSlope { get => _navigationMaximumSlope; set => SetProperty(ref _navigationMaximumSlope, value); }
    private float _navigationMaximumStepUp = 35f;
    public float NavigationMaximumStepUp { get => _navigationMaximumStepUp; set => SetProperty(ref _navigationMaximumStepUp, value); }
    private float _navigationMaximumStepDown = 45f;
    public float NavigationMaximumStepDown { get => _navigationMaximumStepDown; set => SetProperty(ref _navigationMaximumStepDown, value); }
    private float _navigationMaximumDrop = 160f;
    public float NavigationMaximumDrop { get => _navigationMaximumDrop; set => SetProperty(ref _navigationMaximumDrop, value); }
    private float _navigationConnectionDistance = 400f;
    public float NavigationConnectionDistance { get => _navigationConnectionDistance; set => SetProperty(ref _navigationConnectionDistance, value); }
    private float _navigationGenerationRadius = 10000f;
    public float NavigationGenerationRadius { get => _navigationGenerationRadius; set => SetProperty(ref _navigationGenerationRadius, value); }
    private bool _generateCover = true;
    public bool GenerateCover { get => _generateCover; set => SetProperty(ref _generateCover, value); }

    private int _lightmassResolution = 64;
    public int LightmassResolution { get => _lightmassResolution; set => SetProperty(ref _lightmassResolution, value); }
    public IReadOnlyList<int> LightmassResolutions { get; } = [64, 128, 256, 512, 1024];
    private int _actorLightmassResolution = 64;
    private float _lightmassAmbientIntensity = 0.12f;
    public float LightmassAmbientIntensity { get => _lightmassAmbientIntensity; set => SetProperty(ref _lightmassAmbientIntensity, value); }
    private float _lightmassShadowBias = 1f;
    public float LightmassShadowBias { get => _lightmassShadowBias; set => SetProperty(ref _lightmassShadowBias, value); }
    private int _lightmassShadowSamples = 8;
    public int LightmassShadowSamples { get => _lightmassShadowSamples; set => SetProperty(ref _lightmassShadowSamples, value); }
    private float _lightmassSourceRadius = 16f;
    public float LightmassSourceRadius { get => _lightmassSourceRadius; set => SetProperty(ref _lightmassSourceRadius, value); }
    private float _lightmassDirectionalSourceAngle = 0.5f;
    public float LightmassDirectionalSourceAngle { get => _lightmassDirectionalSourceAngle; set => SetProperty(ref _lightmassDirectionalSourceAngle, value); }
    private int _lightmassWorkerThreads;
    public int LightmassWorkerThreads { get => _lightmassWorkerThreads; set => SetProperty(ref _lightmassWorkerThreads, value); }
    private int _lightmassWorkTileSize = 16;
    public int LightmassWorkTileSize { get => _lightmassWorkTileSize; set => SetProperty(ref _lightmassWorkTileSize, value); }
    public IReadOnlyList<int> LightmassWorkTileSizes { get; } = [8, 16, 32, 64, 128];
    private StaticLightingBakeBackend _lightmassBackend = StaticLightingBaker.IsNativeBackendAvailable
        ? StaticLightingBakeBackend.NativeCpp
        : StaticLightingBakeBackend.CSharp;
    public StaticLightingBakeBackend LightmassBackend { get => _lightmassBackend; set => SetProperty(ref _lightmassBackend, value); }
    public IReadOnlyList<LightmassBackendChoice> LightmassBackends { get; } =
    [
        new(StaticLightingBakeBackend.NativeCpp, "Native C++"),
        new(StaticLightingBakeBackend.CSharp, "C#")
    ];
    private string _lightmassTextureCacheName = "";
    public string LightmassTextureCacheName { get => _lightmassTextureCacheName; set => SetProperty(ref _lightmassTextureCacheName, value); }

    public sealed record LightmassBackendChoice(StaticLightingBakeBackend Backend, string DisplayName);

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

    public float LightIconRadius
    {
        get => RenderContext.LightIconRadius;
        set => SetProperty(ref RenderContext.LightIconRadius, value);
    }

    public int MaxLightIcons
    {
        get => RenderContext.MaxLightIcons;
        set => SetProperty(ref RenderContext.MaxLightIcons, value);
    }

    private bool _showEmitterVfx = Settings.LevelEditor_ShowEmitterVfx;
    public bool ShowEmitterVfx
    {
        get => _showEmitterVfx;
        set
        {
            if (SetProperty(ref _showEmitterVfx, value))
            {
                Settings.LevelEditor_ShowEmitterVfx = value;
                RenderContext.SetShowEmitterVfx(value);
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private bool _showDecalActors = true;
    public bool ShowDecalActors
    {
        get => _showDecalActors;
        set
        {
            if (SetProperty(ref _showDecalActors, value))
            {
                RenderContext.ShowDecalActors = value;
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private bool _showPointsOfInterest = true;
    public bool ShowPointsOfInterest
    {
        get => _showPointsOfInterest;
        set
        {
            if (SetProperty(ref _showPointsOfInterest, value))
            {
                RenderContext.ShowPointsOfInterest = value;
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

    private bool _zCutoffEnabled;
    public bool ZCutoffEnabled
    {
        get => _zCutoffEnabled;
        set
        {
            if (SetProperty(ref _zCutoffEnabled, value))
            {
                SceneViewer?.MarkRenderDirty();
            }
        }
    }

    private float _zCutoff;
    public float ZCutoff
    {
        get => _zCutoff;
        set
        {
            if (SetProperty(ref _zCutoff, value))
            {
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
            ? ThemeManager.DarkCanvasMediaColor
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
    private long _lastCameraTextUpdateTimestamp;

    public ObservableCollectionExtended<RecentFileSet> RecentSets { get; } = [];

    private static string RecentSetsFile => Path.Combine(
        Directory.CreateDirectory(Path.Combine(AppDirectories.AppDataFolder, "LevelEditor")).FullName,
        "RECENTSETS");

    public LevelEditor() : base("LevelEditor")
    {
        RenderContext = new LevelEditorRenderContext();
        RenderContext.ShowLightIcons = _showLightIcons;
        RenderContext.SetShowEmitterVfx(_showEmitterVfx);
        RenderContext.TransformWidget.OnDragComplete = OnWidgetDragComplete;
        _backgroundColor = GetThemeDefaultBackgroundColor();
        RenderContext.BackgroundColor = _backgroundColor;
        ActorsView = CollectionViewSource.GetDefaultView(Actors);
        ActorsView.Filter = ActorFilter;
        ActorsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActorProxy.OwningFile)));

        LoadCommands();
        InitializeComponent();
        // Resource preparation should yield for interactions anywhere in the editor, not only over the viewport.
        PreviewMouseMove += (_, _) => RenderContext.NotifyUserActivity();
        PreviewMouseDown += (_, _) => RenderContext.NotifyUserActivity();
        PreviewMouseWheel += (_, _) => RenderContext.NotifyUserActivity();
        PreviewKeyDown += (_, _) => RenderContext.NotifyUserActivity();
        ApplyPointOfInterestToolTipTheme(Settings.Global_DarkMode_Enabled);
        LevelLiveMaterialEditor.CloseMaterialEditorRequested += LevelLiveMaterialEditor_CloseRequested;
        LevelLiveMaterialEditor.LiveMaterialPreviewChanged += LevelLiveMaterialEditor_PreviewChanged;
        LoadRecentSets();

        SceneViewer.Context = RenderContext;
        UndoHistory.PropertyChanged += UndoHistory_PropertyChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object sender, bool isDarkMode)
    {
        BackgroundColor = GetThemeDefaultBackgroundColor();
        ApplyPointOfInterestToolTipTheme(isDarkMode);
    }

    private void ApplyPointOfInterestToolTipTheme(bool isDarkMode)
    {
        if (isDarkMode)
        {
            PointOfInterestToolTip.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x26));
            PointOfInterestToolTip.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0));
            PointOfInterestToolTip.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46));
        }
        else
        {
            PointOfInterestToolTip.Background = SystemColors.InfoBrush;
            PointOfInterestToolTip.Foreground = SystemColors.InfoTextBrush;
            PointOfInterestToolTip.BorderBrush = SystemColors.ActiveBorderBrush;
        }
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
        long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (timestamp - _lastCameraTextUpdateTimestamp >= System.Diagnostics.Stopwatch.Frequency / 10)
        {
            _lastCameraTextUpdateTimestamp = timestamp;
            UpdateCameraPositionText();
            UpdateCameraRotationText();
        }
        if (RenderContext.ShowEmitterVfx)
        {
            RenderContext.QueueVisibleEmitterResources();
            int visibleEmitterCount = 0;
            foreach (EmitterActorProxy emitter in RenderContext.DrawList_3D.OfType<EmitterActorProxy>())
            {
                if (RenderContext.IsBoundsVisible(RenderContext.GetActorBounds(emitter)))
                {
                    emitter.UpdateScene(RenderContext, e);
                    visibleEmitterCount += emitter.Components.OfType<ParticleSystemComponentProxy>()
                        .Count(component => component.HasRenderableVfx);
                }
            }
            RenderContext.SetVisibleEmitterInstanceCount(visibleEmitterCount);
        }
    }

    private void RenderScene(object sender, EventArgs e)
    {
        RenderContext.ShowVolumes = ShowVolumes;
        RenderContext.ShowVolumetrics = ShowVolumetrics;
        BuildVisibleActorList();
        Span<RenderPass> passes = (ShowCollision, RenderContext.ShouldRenderHitTestPass) switch
        {
            (true, true) => [RenderPass.Base, RenderPass.Hair, RenderPass.Collision, RenderPass.HitTest],
            (true, false) => [RenderPass.Base, RenderPass.Hair, RenderPass.Collision],
            (false, true) => [RenderPass.Base, RenderPass.Hair, RenderPass.HitTest],
            _ => [RenderPass.Base, RenderPass.Hair]
        };

        foreach (RenderPass pass in passes)
        {
            DoRenderPass(pass);
        }

        RenderContext.DrawUI();
    }

    private void BuildVisibleActorList()
    {
        visibleActors.Clear();
        MeshRenderContext.BoundsVisibilityTester visibility = RenderContext.CreateBoundsVisibilityTester();
        for (int actorIndex = 0; actorIndex < RenderContext.DrawList_3D.Count; actorIndex++)
        {
            ActorProxy actor = RenderContext.DrawList_3D[actorIndex];
            if (actor is DecalActorProxy && !ShowDecalActors) continue;
            if (actor is SFXPointOfInterestProxy && !ShowPointsOfInterest) continue;
            if (actor.IsVolume && !ShowVolumes) continue;
            if (actor.IsVolumetricMesh && !ShowVolumetrics) continue;
            if (_zCutoffEnabled && actor.Location.Z >= _zCutoff) continue;
            if (HasIncompleteCharacterResources(actor)) continue;

            BoxSphereBounds bounds = RenderContext.GetActorBounds(actor);
            if (!visibility.IsVisible(bounds)) continue;

            int hitID = actor.HitID;
            var hitTestId = new Vector3((hitID & 0xFF) / 255f, ((hitID >> 8) & 0xFF) / 255f,
                ((hitID >> 16) & 0xFF) / 255f);
            visibleActors.Add(new VisibleActor(actor, bounds, hitTestId));
        }
    }

    private static bool HasIncompleteCharacterResources(ActorProxy actor)
    {
        if (actor is not (SkeletalMeshActorProxy or SFXStuntActorProxy))
        {
            return false;
        }
        foreach (PrimitiveComponentProxy component in actor.Components)
        {
            if (component is MeshComponentProxy { RenderResourcesInitialized: false })
            {
                return true;
            }
        }
        return false;
    }

    void DoRenderPass(RenderPass pass)
    {
        for (int i = 0; i < visibleActors.Count; i++)
        {
            VisibleActor visibleActor = visibleActors[i];
            ActorProxy actor = visibleActor.Actor;
            RenderContext.CurrentHitTestId = visibleActor.HitTestId;
            // Keep the actor selected for material picking and editor synchronization, but do not tint it
            // blue while evaluating live material changes. The selection shader obscures the actual result.
            if (actor == selectedActor && !IsLevelMaterialEditorOpen)
            {
                RenderContext.RenderFlags |= LevelEditorRenderContext.ShaderFlags.Selected;
            }
            actor.Render(RenderContext, pass);
            if (pass == RenderPass.Base && actor == selectedActor
                && !string.IsNullOrWhiteSpace(actor.PreviewAnimationName))
            {
                Vector3 labelPosition = visibleActor.Bounds.Origin
                                        + Vector3.UnitZ * (MathF.Abs(visibleActor.Bounds.BoxExtent.Z) + 20f);
                if (RenderContext.WorldToPixel(labelPosition, out Vector2 pixel))
                {
                    RenderContext.ScreenLabels.Add(new ScreenLabel(pixel.X, pixel.Y,
                        $"Animation: {actor.PreviewAnimationName}"));
                }
            }
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
                RenderContext.TransformWidget.Attach = selectedActor;
                RenderContext.PrioritizeActorResources(selectedActor);
                if (focus)
                {
                    FocusOnBounds(selectedActor.GetBounds());
                }
                selectedActor.PropertyChanged += OnActorPropertyChanged;
                _preEditSnapshot = selectedActor.SnapshotTransform();
                RefreshPropertiesExportSelection(selectedActor, PropertiesTabControl.SelectedIndex);
            }
            else
            {
                RenderContext.TransformWidget.Attach = null;
                RenderContext.PrioritizeActorResources(null);
                _preEditSnapshot = null;
                UnloadPropertyTabs();
            }
            SynchronizeLevelLiveMaterialEditor(selectedActor);
            SynchronizeLevelMorphEditor(selectedActor);
            UpdateActorRotationDialIndicator();
            UpdateLightValueDialIndicator();
        }
    }

    private void ActorLocationScrubAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string { Length: 1 } axis })
        {
            _actorLocationScrubAxis = axis[0];
        }
    }

    private void ActorLocationScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly)
        {
            e.Handled = true;
            return;
        }

        _actorLocationScrubAccumulator = 0;
        _actorLocationScrubPreviousHorizontalChange = 0;
        BeginActorTransformScrub();
    }

    private void ActorLocationScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !double.IsFinite(e.HorizontalChange))
        {
            return;
        }

        double horizontalChange = e.HorizontalChange - _actorLocationScrubPreviousHorizontalChange;
        _actorLocationScrubPreviousHorizontalChange = e.HorizontalChange;
        _actorLocationScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_actorLocationScrubAccumulator / dragStep);
        if (stepCount == 0)
        {
            return;
        }

        _actorLocationScrubAccumulator -= stepCount * dragStep;
        float delta = stepCount * PosIncrement;
        Vector3 location = SelectedActor.Location;
        switch (_actorLocationScrubAxis)
        {
            case 'X':
                location.X += delta;
                break;
            case 'Y':
                location.Y += delta;
                break;
            case 'Z':
                location.Z += delta;
                break;
        }

        SelectedActor.Location = location;
    }

    private void ActorLocationScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        EndActorTransformScrub();
        SceneViewer?.MarkRenderDirty();
    }

    private void ActorScaleScrubAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axes })
        {
            _actorScaleScrubAxes = axes;
        }
    }

    private void ActorScaleScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly)
        {
            e.Handled = true;
            return;
        }

        _actorScaleScrubAccumulator = 0;
        _actorScaleScrubPreviousHorizontalChange = 0;
        BeginActorTransformScrub();
    }

    private void ActorScaleScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !double.IsFinite(e.HorizontalChange))
        {
            return;
        }

        double horizontalChange = e.HorizontalChange - _actorScaleScrubPreviousHorizontalChange;
        _actorScaleScrubPreviousHorizontalChange = e.HorizontalChange;
        _actorScaleScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_actorScaleScrubAccumulator / dragStep);
        if (stepCount == 0)
        {
            return;
        }

        _actorScaleScrubAccumulator -= stepCount * dragStep;
        float delta = stepCount * ScaleIncrement;
        Vector3 scale = SelectedActor.DrawScale3D;
        if (_actorScaleScrubAxes is "X" or "All") scale.X += delta;
        if (_actorScaleScrubAxes is "Y" or "All") scale.Y += delta;
        if (_actorScaleScrubAxes is "Z" or "All") scale.Z += delta;
        SelectedActor.DrawScale3D = scale;
    }

    private void ActorScaleScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        EndActorTransformScrub();
        SceneViewer?.MarkRenderDirty();
    }

    private void ActorRotationDialAxis_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string axis })
        {
            _actorRotationDialAxis = axis switch
            {
                "Pitch" => nameof(ActorProxy.PitchDegrees),
                "Roll" => nameof(ActorProxy.RollDegrees),
                "Yaw" => nameof(ActorProxy.YawDegrees),
                _ => _actorRotationDialAxis
            };
            UpdateActorRotationDialIndicator();
        }
    }

    private void ActorRotationDial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || RotIncrement <= 0)
        {
            return;
        }

        _actorRotationDialPreviousAngle = GetActorRotationDialPointerAngle(e.GetPosition(ActorRotationDial));
        _actorRotationDialAngleAccumulator = 0;
        _actorRotationDialDragging = ActorRotationDial.CaptureMouse();
        if (_actorRotationDialDragging)
        {
            BeginActorTransformScrub();
        }
        e.Handled = true;
    }

    private void ActorRotationDial_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_actorRotationDialDragging || SelectedActor is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        double pointerAngle = GetActorRotationDialPointerAngle(e.GetPosition(ActorRotationDial));
        double angleDelta = NormalizeActorRotationDialAngle(pointerAngle - _actorRotationDialPreviousAngle);
        _actorRotationDialPreviousAngle = pointerAngle;
        _actorRotationDialAngleAccumulator += angleDelta;

        int stepCount = (int)(_actorRotationDialAngleAccumulator / RotIncrement);
        if (stepCount == 0)
        {
            return;
        }

        _actorRotationDialAngleAccumulator -= stepCount * RotIncrement;
        float delta = stepCount * RotIncrement;
        switch (_actorRotationDialAxis)
        {
            case nameof(ActorProxy.PitchDegrees):
                SelectedActor.PitchDegrees += delta;
                break;
            case nameof(ActorProxy.RollDegrees):
                SelectedActor.RollDegrees += delta;
                break;
            case nameof(ActorProxy.YawDegrees):
                SelectedActor.YawDegrees += delta;
                break;
        }

        UpdateActorRotationDialIndicator();
        e.Handled = true;
    }

    private void ActorRotationDial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_actorRotationDialDragging)
        {
            return;
        }

        _actorRotationDialDragging = false;
        EndActorTransformScrub();
        ActorRotationDial.ReleaseMouseCapture();
        SceneViewer?.MarkRenderDirty();
        e.Handled = true;
    }

    private void ActorRotationDial_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _actorRotationDialDragging = false;
        EndActorTransformScrub();
    }

    private void BeginActorTransformScrub()
    {
        _actorTransformScrubActor = SelectedActor;
        _actorTransformScrubBefore = SelectedActor?.SnapshotTransform();
        _isActorTransformScrubbing = _actorTransformScrubActor is not null;
    }

    private void EndActorTransformScrub()
    {
        if (!_isActorTransformScrubbing || _actorTransformScrubActor is null || _actorTransformScrubBefore is not { } before)
        {
            return;
        }

        TransformSnapshot after = _actorTransformScrubActor.SnapshotTransform();
        if (!before.Equals(after))
        {
            UndoHistory.Push(new TransformAction(
                _actorTransformScrubActor,
                before,
                after,
                $"Drag {_actorTransformScrubActor.Export.ObjectName.Instanced}"));
            _preEditSnapshot = after;
        }

        _isActorTransformScrubbing = false;
        _actorTransformScrubActor = null;
        _actorTransformScrubBefore = null;
        RefreshSelectedPropertiesPreview();
        UpdateActorRotationDialIndicator();
    }

    private void UpdateActorRotationDialIndicator()
    {
        if (ActorRotationDialIndicator?.RenderTransform is not System.Windows.Media.RotateTransform indicatorTransform)
        {
            return;
        }

        indicatorTransform.Angle = _actorRotationDialAxis switch
        {
            nameof(ActorProxy.PitchDegrees) => SelectedActor?.PitchDegrees ?? 0,
            nameof(ActorProxy.RollDegrees) => SelectedActor?.RollDegrees ?? 0,
            nameof(ActorProxy.YawDegrees) => SelectedActor?.YawDegrees ?? 0,
            _ => 0
        };
    }

    private static double GetActorRotationDialPointerAngle(Point pointerPosition)
        => Math.Atan2(pointerPosition.Y - 60d, pointerPosition.X - 60d) * 180d / Math.PI + 90d;

    private static double NormalizeActorRotationDialAngle(double angle)
    {
        while (angle > 180d) angle -= 360d;
        while (angle < -180d) angle += 360d;
        return angle;
    }

    private void LightDialProperty_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string propertyName })
        {
            _lightDialProperty = propertyName;
            UpdateLightValueDialIndicator();
        }
    }

    private void LightRadiusScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !SelectedActor.HasLightRadius)
        {
            e.Handled = true;
            return;
        }

        _lightRadiusScrubAccumulator = 0;
        _lightRadiusScrubPreviousHorizontalChange = 0;
    }

    private void LightRadiusScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !double.IsFinite(e.HorizontalChange))
        {
            return;
        }

        double horizontalChange = e.HorizontalChange - _lightRadiusScrubPreviousHorizontalChange;
        _lightRadiusScrubPreviousHorizontalChange = e.HorizontalChange;
        _lightRadiusScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_lightRadiusScrubAccumulator / dragStep);
        if (stepCount == 0)
        {
            return;
        }

        _lightRadiusScrubAccumulator -= stepCount * dragStep;
        SelectedActor.LightRadius = MathF.Max(0, SelectedActor.LightRadius + stepCount * 16f);
    }

    private void LightRadiusScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SceneViewer?.MarkRenderDirty();
    }

    private void LightBrightnessScrubThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly || !SelectedActor.HasLightSettings)
        {
            e.Handled = true;
            return;
        }

        _lightBrightnessScrubAccumulator = 0;
        _lightBrightnessScrubPreviousHorizontalChange = 0;
    }

    private void LightBrightnessScrubThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SelectedActor is null
            || SelectedActor.IsReadOnly
            || !SelectedActor.HasLightSettings
            || !double.IsFinite(e.HorizontalChange))
        {
            return;
        }

        double horizontalChange = e.HorizontalChange - _lightBrightnessScrubPreviousHorizontalChange;
        _lightBrightnessScrubPreviousHorizontalChange = e.HorizontalChange;
        _lightBrightnessScrubAccumulator += horizontalChange;
        double dragStep = SystemParameters.MinimumHorizontalDragDistance;
        int stepCount = (int)(_lightBrightnessScrubAccumulator / dragStep);
        if (stepCount == 0)
        {
            return;
        }

        _lightBrightnessScrubAccumulator -= stepCount * dragStep;
        SelectedActor.Brightness = MathF.Max(0, SelectedActor.Brightness + stepCount * 0.25f);
    }

    private void LightBrightnessScrubThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SceneViewer?.MarkRenderDirty();
    }

    private void LightValueDial_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SelectedActor is null
            || SelectedActor.IsReadOnly
            || !SelectedActor.HasConeAngles)
        {
            return;
        }

        _lightValueDialPreviousAngle = GetActorRotationDialPointerAngle(e.GetPosition(LightValueDial));
        _lightValueDialAccumulator = 0;
        _lightValueDialDragging = LightValueDial.CaptureMouse();
        e.Handled = true;
    }

    private void LightValueDial_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_lightValueDialDragging || SelectedActor is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        double pointerAngle = GetActorRotationDialPointerAngle(e.GetPosition(LightValueDial));
        double angleDelta = NormalizeActorRotationDialAngle(pointerAngle - _lightValueDialPreviousAngle);
        _lightValueDialPreviousAngle = pointerAngle;
        _lightValueDialAccumulator += angleDelta;

        const float increment = 1f;
        int stepCount = (int)(_lightValueDialAccumulator / increment);
        if (stepCount == 0)
        {
            return;
        }

        _lightValueDialAccumulator -= stepCount * increment;
        float delta = stepCount * increment;
        switch (_lightDialProperty)
        {
            case nameof(ActorProxy.InnerConeAngle):
                SelectedActor.InnerConeAngle = Math.Clamp(SelectedActor.InnerConeAngle + delta, 0, 89.9f);
                break;
            case nameof(ActorProxy.OuterConeAngle):
                SelectedActor.OuterConeAngle = Math.Clamp(SelectedActor.OuterConeAngle + delta, 0, 89.9f);
                break;
            case "ConeAngles":
                SelectedActor.InnerConeAngle = Math.Clamp(SelectedActor.InnerConeAngle + delta, 0, 89.9f);
                SelectedActor.OuterConeAngle = Math.Clamp(SelectedActor.OuterConeAngle + delta, 0, 89.9f);
                break;
        }

        UpdateLightValueDialIndicator();
        e.Handled = true;
    }

    private void LightValueDial_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_lightValueDialDragging)
        {
            return;
        }

        _lightValueDialDragging = false;
        LightValueDial.ReleaseMouseCapture();
        SceneViewer?.MarkRenderDirty();
        e.Handled = true;
    }

    private void LightValueDial_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _lightValueDialDragging = false;
    }

    private void UpdateLightValueDialIndicator()
    {
        if (LightValueDialIndicator?.RenderTransform is not System.Windows.Media.RotateTransform indicatorTransform)
        {
            return;
        }

        indicatorTransform.Angle = _lightDialProperty switch
        {
            nameof(ActorProxy.InnerConeAngle) => SelectedActor?.InnerConeAngle ?? 0,
            nameof(ActorProxy.OuterConeAngle) => SelectedActor?.OuterConeAngle ?? 0,
            "ConeAngles" when SelectedActor is not null => (SelectedActor.InnerConeAngle + SelectedActor.OuterConeAngle) / 2f,
            _ => 0
        };
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
            if (RenderContext.Camera.FirstPerson)
            {
                (float sin, float cos) = MathF.SinCos(MathF.PI / 2.5f);
                RenderContext.Camera.Position = new Vector3(origin.X, origin.Y + sin * hyp, origin.Z + cos * hyp);
                RenderContext.Camera.OrientTowards(origin);
            }
            else
            {
                RenderContext.Camera.Position = origin;
                RenderContext.Camera.FocusDepth = hyp;
            }
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

        IsBusy = true;
        BusyText = $"Opening {Path.GetFileName(path)}...";
        await Task.Yield();

        // Package decompression, level-binary parsing, and actor-proxy discovery are CPU and IO heavy
        // for large modded levels. None of them mutate WPF collections, so keep them off the dispatcher.
        using IMEPackage pcc = await Task.Run(() => MEPackageHandler.OpenMEPackage(path)).ConfigureAwait(true);
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

        bool isFirstFile = OpenFiles.Count == 1;

        BusyText = $"Scanning {Path.GetFileName(path)}...";
        Level levelBin = await Task.Run(() => levelExport.GetBinaryData<Level>()).ConfigureAwait(true);
        LoadActorsResult actorLoad = await Task.Run(() => LoadActors(levelBin, openFile)).ConfigureAwait(true);
        var (actors, ignoredClasses) = actorLoad;
        var sorted = actors.OrderBy(actor => actor.Export.UIndex).ToList();
        openFile.Actors.AddRange(sorted);
        Actors.AddRange(sorted);
        RenderContext.LoadActors(sorted);
        RefreshNavigationOverlay();

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
        RenderContext.NavigationOverlay.Refresh([]);
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
        RefreshNavigationOverlay();
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

        UpdateStatusBarText();
    }

    private void UpdateStatusBarText()
    {
        StatusBar_LeftMostText.Text = OpenFiles.Count switch
        {
            0 => "Select package file to load",
            _ when ActiveFile is not null => GetStatusBarText(),
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
    public ICommand GenerateNavigationCommand { get; set; }
    public ICommand GenerateStaticLightingCommand { get; set; }
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
        GenerateNavigationCommand = new GenericCommand(GenerateNavigation,
            () => ActiveFile is { IsReadOnly: false } && Game.IsGame3() && !IsBusy);
        GenerateStaticLightingCommand = new GenericCommand(GenerateStaticLighting,
            () => OpenFiles.Any(file => file.IncludeInLightmass && !file.IsReadOnly) && !IsBusy);
    }

    private void SnapActorToCamera()
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly) return;
        SelectedActor.Location = RenderContext.Camera.Position;
        SceneViewer?.MarkRenderDirty();
    }

    private async void GenerateNavigation()
    {
        if (ActiveFile is not { IsReadOnly: false } activeFile)
            return;
        if (!Game.IsGame3())
        {
            MessageBox.Show(this, "Automatic cover serialization currently supports ME3 and LE3 levels.",
                "Navigation Generator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = new NavigationGenerationSettings
        {
            PawnRadius = NavigationPawnRadius,
            PawnHeight = NavigationPawnHeight,
            GridSpacing = NavigationGridSpacing,
            MaximumSlopeDegrees = NavigationMaximumSlope,
            MaximumStepUp = NavigationMaximumStepUp,
            MaximumStepDown = NavigationMaximumStepDown,
            MaximumSafeDrop = NavigationMaximumDrop,
            ConnectionDistance = NavigationConnectionDistance,
            GenerationRadius = NavigationGenerationRadius,
            GenerateCover = GenerateCover
        };

        try
        {
            settings.Validate();
            IsBusy = true;
            IsBusyTaskbar = true;
            BusyText = "Building collision acceleration structure...";
            ActorProxy[] collisionActors = Actors.ToArray();
            Vector3 generationCenter = RenderContext.Camera.Position;
            var progress = new Progress<string>(message => BusyText = message);
            var generation = await Task.Run(() =>
            {
                LevelCollisionScene collision = LevelCollisionScene.Build(collisionActors);
                NavigationGenerationResult result = new NavigationGenerator(collision, settings).Generate(
                    generationCenter, CancellationToken.None, progress);
                return (Result: result, collision.NavigationSourceCount, collision.CoverSourceCount,
                    collision.NavigationTriangleCount, collision.CoverTriangleCount);
            }).ConfigureAwait(true);
            NavigationGenerationResult generated = generation.Result;

            IsBusy = false;
            IsBusyTaskbar = false;
            int mantleConnections = generated.CoverLinks.Sum(link =>
                link.Slots.Count(slot => slot.MantleTargetLink >= 0)) / 2;
            string summary = $"Generated {generated.Nodes.Count:N0} path nodes, " +
                             $"{generated.Edges.Count:N0} directed connections, " +
                             $"{generated.CoverLinks.Count:N0} cover links, and " +
                             $"{generated.CoverLinks.Sum(link => link.Slots.Count):N0} cover slots, including " +
                             $"{mantleConnections:N0} validated vault connections.\n\n" +
                             $"Scanned {generation.CoverSourceCount:N0} mesh/brush components for cover " +
                             $"({generation.CoverTriangleCount:N0} triangles); " +
                             $"{generation.NavigationSourceCount:N0} components provide blocking navigation collision.\n\n" +
                             $"Append these objects to {activeFile.FileName}? Existing navigation is preserved, " +
                             "and the package is not saved to disk until you use Save.";
            if (MessageBox.Show(this, summary, "Navigation Generator", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            IsBusy = true;
            IsBusyTaskbar = true;
            BusyText = "Serializing navigation and cover...";
            NavigationSerializationResult serialized = NavigationSerializer.Write(activeFile, generated, settings);
            RefreshNavigationOverlay();
            ShowPathing = true;
            ShowCover = GenerateCover;
            TextBelowActors = $"Navigation generator: {serialized.PathNodeCount:N0} nodes, " +
                              $"{serialized.ReachSpecCount:N0} ReachSpecs, " +
                              $"{serialized.CoverLinkCount:N0} cover links / {serialized.CoverSlotCount:N0} slots; " +
                              $"cover geometry from {generation.CoverSourceCount:N0} components.";
            SceneViewer?.MarkRenderDirty();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Navigation Generator", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsBusyTaskbar = false;
        }
    }

    private void RefreshNavigationOverlay()
    {
        RenderContext.NavigationOverlay.Refresh(OpenFiles);
        SceneViewer?.MarkRenderDirty();
    }

    #endregion

    #region Undo/Redo
    public readonly UndoHistory UndoHistory = new();
    private TransformSnapshot? _preEditSnapshot;
    private bool _isApplyingUndoRedo;
    private bool _isRefreshingActorFromPackageUpdate;
    private (int UIndex, IMEPackage Package, bool Focus) _pendingSelect;
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
        CommitChanges();

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
            CommitChangesForFile(file);
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
        if (DirectoryMemory.ShowDialog(d) == true)
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
            ExportEntry updatedPropertiesExport = file.Package.GetEntry(_selectedPropertiesExportUIndex) as ExportEntry;
            bool propertyEditorWrite = LevelEditorInterpreter.ConsumePendingPropertyWrite(
                updatedPropertiesExport, out bool requiresActorRebuild);
            if (file.Actors.FirstOrDefault(actor => actor.TestUIndexes(updatedExports)) is { } actor)
            {
                int actorUIndex = actor.Export.UIndex;
                bool rebuildMeshActor = actor is not CollectionActorComponentProxy
                                        && updatedPropertiesExport is not null
                                        && (!propertyEditorWrite || requiresActorRebuild)
                                        && (updatedPropertiesExport.IsA("StaticMeshComponent")
                                            || updatedPropertiesExport.IsA("SkeletalMeshComponent")
                                            || updatedPropertiesExport.IsA("ParticleSystemComponent"));
                _isRefreshingActorFromPackageUpdate = true;
                try
                {
                    if (rebuildMeshActor)
                    {
                        // Mesh references are constructor-time render resources. Recreate the proxy so an
                        // edit made in the embedded property panel is visible immediately. The remembered
                        // package/UIndex pair keeps the same actor/component selected in the dropdown.
                        RefreshActorInViewport(actor);
                        actor = file.Actors.FirstOrDefault(candidate => candidate.Export.UIndex == actorUIndex);
                    }
                    else
                    {
                        actor.RefreshFromExport();
                    }
                }
                finally
                {
                    _isRefreshingActorFromPackageUpdate = false;
                }

                if (actor is not null && actor == SelectedActor)
                {
                    _preEditSnapshot = SelectedActor.SnapshotTransform();
                }
            }

            if (updatedPropertiesExport is not null)
            {
                if (!propertyEditorWrite)
                {
                    _selectedPropertiesExport = updatedPropertiesExport;
                    OnPropertyChanged(nameof(SelectedPropertiesExport));
                    LevelEditorInterpreter.LoadExport(updatedPropertiesExport);
                    LevelEditorMetadata.LoadExport(updatedPropertiesExport);
                }
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
            (Vector3 Position, float Pitch, float Yaw, float Roll) savedCamPOV = default;
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
                        savedCamPOV = (RenderContext.Camera.Position, RenderContext.Camera.Pitch,
                            RenderContext.Camera.Yaw, RenderContext.Camera.Roll);
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
                SelectActor(Actors.FirstOrDefault(a => a.Export.UIndex == _pendingSelect.UIndex && a.Export.FileRef == file.Package), _pendingSelect.Focus);
                _pendingSelect = default;
            }
            else if (reselectUIndex is not 0)
            {
                ActorProxy reselect = Actors.FirstOrDefault(a =>
                    a.Export.UIndex == reselectUIndex && a.Export.FileRef == file.Package);
                if (reselect is not null)
                {
                    SelectActor(reselect, false);
                    (RenderContext.Camera.Position, RenderContext.Camera.Pitch,
                        RenderContext.Camera.Yaw, RenderContext.Camera.Roll) = savedCamPOV;
                }
            }
        }
    }

    private void ReloadFile(OpenLevelFile file)
    {
        // Remove all actors for this file, then re-load
        (Vector3 Position, float Pitch, float Yaw, float Roll) savedCamPOV = default;
        int reselectUIndex = 0;
        if (SelectedActor is not null && file.Actors.Contains(SelectedActor))
        {
            savedCamPOV = (RenderContext.Camera.Position, RenderContext.Camera.Pitch,
                RenderContext.Camera.Yaw, RenderContext.Camera.Roll);
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
                bool focusPendingSelection = _pendingSelect.Focus;
                _pendingSelect = default;
                if (pendingSelect is not null)
                {
                    SelectActor(pendingSelect, focusPendingSelection);
                }
            }
            else if (reselectUIndex is not 0)
            {
                var reselect = Actors.FirstOrDefault(a => a.Export.UIndex == reselectUIndex && a.Export.FileRef == file.Package);
                if (reselect is not null)
                {
                    SelectActor(reselect, false);
                    (RenderContext.Camera.Position, RenderContext.Camera.Pitch,
                        RenderContext.Camera.Yaw, RenderContext.Camera.Roll) = savedCamPOV;
                }
            }

            file.IsDirty = false;
            RefreshNavigationOverlay();
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
            if (e.PropertyName is nameof(ActorProxy.LightRadius) or nameof(ActorProxy.InnerConeAngle) or nameof(ActorProxy.OuterConeAngle))
            {
                UpdateLightValueDialIndicator();
            }
            SceneViewer?.MarkRenderDirty();
            return;
        }

        if (e.PropertyName is not (nameof(ActorProxy.Location) or nameof(ActorProxy.Rotation) or nameof(ActorProxy.DrawScale) or nameof(ActorProxy.DrawScale3D))) return;

        SceneViewer?.MarkRenderDirty();
        if (_isActorTransformScrubbing)
        {
            return;
        }
        RefreshSelectedPropertiesPreview();
        if (e.PropertyName == nameof(ActorProxy.Rotation))
        {
            UpdateActorRotationDialIndicator();
        }
        if (!_isActorTransformScrubbing && sender is ActorProxy actor && _preEditSnapshot is { } before)
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
        if (obj is not ActorProxy actor) return false;

        // Allow searching by export index (UIndex), e.g. "1234" or "#1234"
        ReadOnlySpan<char> numericFilter = _actorFilterText.AsSpan().Trim();
        if (numericFilter.Length > 0 && numericFilter[0] is '#')
        {
            numericFilter = numericFilter[1..];
        }
        if (numericFilter.Length > 0 && int.TryParse(numericFilter, out int uIndex) && actor.Export.UIndex == uIndex)
        {
            return true;
        }

        return actor.Export.ObjectName.Instanced?.Contains(_actorFilterText, StringComparison.OrdinalIgnoreCase) == true
               || actor.Tag.Instanced?.Contains(_actorFilterText, StringComparison.OrdinalIgnoreCase) == true
               || actor.DisplaySubtitle?.Contains(_actorFilterText, StringComparison.OrdinalIgnoreCase) == true;
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
        if (DirectoryMemory.ShowDialog(d) == true)
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
        if (DirectoryMemory.ShowDialog(d) == true)
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

        CloseLevelMorphEditor();
        CloseAllFiles();

        RenderContext.UpdateScene -= UpdateScene;
        RenderContext.RenderScene -= RenderScene;
        RenderContext.SelectActor -= ViewportActorSelect;
        RenderContext.RightClickActor -= OnViewportRightClickActor;
        RenderContext.RightClickViewport -= OnViewportRightClickViewport;

        UndoHistory.PropertyChanged -= UndoHistory_PropertyChanged;
        UndoHistory.Clear();

        LevelLiveMaterialEditor.CloseMaterialEditorRequested -= LevelLiveMaterialEditor_CloseRequested;
        LevelLiveMaterialEditor.LiveMaterialPreviewChanged -= LevelLiveMaterialEditor_PreviewChanged;
        LevelLiveMaterialEditor.Dispose();
        LevelMorphEditor.Dispose();
        SceneViewer.Dispose();
    }

    private void LevelEditor_Loaded(object sender, RoutedEventArgs e)
    {
        RenderContext.UpdateScene += UpdateScene;
        RenderContext.RenderScene += RenderScene;
        RenderContext.SelectActor += ViewportActorSelect;
        RenderContext.RightClickActor += OnViewportRightClickActor;
        RenderContext.RightClickViewport += OnViewportRightClickViewport;

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

    private bool _busyProgressIsIndeterminate = true;
    public bool BusyProgressIsIndeterminate
    {
        get => _busyProgressIsIndeterminate;
        set => SetProperty(ref _busyProgressIsIndeterminate, value);
    }

    private double _busyProgressMaximum = 1d;
    public double BusyProgressMaximum
    {
        get => _busyProgressMaximum;
        set => SetProperty(ref _busyProgressMaximum, Math.Max(1d, value));
    }

    private double _busyProgressValue;
    public double BusyProgressValue
    {
        get => _busyProgressValue;
        set => SetProperty(ref _busyProgressValue, Math.Clamp(value, 0d, BusyProgressMaximum));
    }

    private void UpdateStaticLightingScanProgress(StaticLightingBuildProgress progress)
    {
        BusyText = progress.DisplayText;
        BusyProgressIsIndeterminate = !progress.IsDeterminate;
        BusyProgressMaximum = Math.Max(1, progress.Total);
        BusyProgressValue = progress.IsDeterminate ? progress.Current : 0;
    }

    private sealed class LatestUiProgress<T>(Dispatcher dispatcher, Action<T> update) : IProgress<T>
    {
        private readonly object gate = new();
        private T latest;
        private bool scheduled;

        public void Report(T value)
        {
            lock (gate)
            {
                latest = value;
                if (scheduled)
                    return;
                scheduled = true;
            }
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DeliverLatest));
        }

        private void DeliverLatest()
        {
            T value;
            lock (gate)
            {
                value = latest;
                scheduled = false;
            }
            update(value);
        }
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

        if (actor is SFXPointOfInterestProxy
            && actor.Export.GetProperty<ArrayProperty<ObjectProperty>>("Modules") is { } modules)
        {
            foreach (ObjectProperty moduleReference in modules)
            {
                if (actor.Export.FileRef.TryGetUExport(moduleReference.Value, out ExportEntry module)
                    && module.ClassName == "SFXSimpleUseModule")
                {
                    PropertiesExportList.Add(module);
                }
            }
        }

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
        Point viewportPoint = Mouse.GetPosition(SceneViewer);
        var contextMenu = BuildActorContextMenu(actor, viewportPoint);
        contextMenu.PlacementTarget = SceneViewer;
        contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        contextMenu.IsOpen = true;
    }

    private void OnViewportRightClickViewport()
    {
        Point viewportPoint = Mouse.GetPosition(SceneViewer);
        var contextMenu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = SceneViewer,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };

        contextMenu.Items.Add(BuildCreateActorMenu(GetViewportLocationAtSelectedActorDepth(viewportPoint)));
        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var snapItem = new System.Windows.Controls.MenuItem
        {
            Header = "Snap Selected Actor Here",
            IsEnabled = SelectedActor is not null && !SelectedActor.IsReadOnly
        };
        snapItem.Click += (_, _) => SnapSelectedActorToViewportPoint(viewportPoint);
        contextMenu.Items.Add(snapItem);

        contextMenu.IsOpen = true;
    }

    private void SnapSelectedActorToViewportPoint(Point viewportPoint)
    {
        if (SelectedActor is null || SelectedActor.IsReadOnly) return;

        SelectedActor.Location = GetViewportLocationAtSelectedActorDepth(viewportPoint);
        SceneViewer?.MarkRenderDirty();
    }

    private Vector3 GetViewportLocationAtSelectedActorDepth(Point viewportPoint)
    {
        Vector3 referenceLocation = SelectedActor?.Location ?? RenderContext.Camera.Position + RenderContext.Camera.CameraForward * 100f;
        return GetViewportLocationAtDepth(viewportPoint, referenceLocation);
    }

    private Vector3 GetViewportLocationAtDepth(Point viewportPoint, Vector3 referenceLocation)
    {
        float width = MathF.Max(RenderContext.Width, 1f);
        float height = MathF.Max(RenderContext.Height, 1f);
        float normalizedX = ((float)viewportPoint.X / width * 2f) - 1f;
        float normalizedY = 1f - ((float)viewportPoint.Y / height * 2f);
        Vector3 forward = RenderContext.Camera.CameraForward;
        Vector3 right = RenderContext.Camera.CameraRight;
        Vector3 up = RenderContext.Camera.CameraUp;
        Vector3 cameraPosition = RenderContext.Camera.Position;

        if (RenderContext.Camera.IsOrthographic)
        {
            return cameraPosition
                   + (right * (normalizedX * RenderContext.Camera.OrthoWidth * 0.5f))
                   + (up * (normalizedY * RenderContext.Camera.OrthoWidth / MathF.Max(RenderContext.Camera.aspect, float.Epsilon) * 0.5f))
                   + (forward * Vector3.Dot(referenceLocation - cameraPosition, forward));
        }

        float halfHeightAtUnitDepth = MathF.Tan(RenderContext.Camera.FOV * 0.5f);
        Vector3 rayDirection = Vector3.Normalize(forward + right * normalizedX * halfHeightAtUnitDepth * RenderContext.Camera.aspect + up * normalizedY * halfHeightAtUnitDepth);
        float denominator = Vector3.Dot(rayDirection, forward);
        if (MathF.Abs(denominator) < 0.0001f)
        {
            return referenceLocation;
        }

        float distance = Vector3.Dot(referenceLocation - cameraPosition, forward) / denominator;
        if (distance <= 0f || float.IsNaN(distance) || float.IsInfinity(distance))
        {
            return referenceLocation;
        }

        return cameraPosition + rayDirection * distance;
    }

    private System.Windows.Controls.MenuItem BuildCreateActorMenu(Vector3 location)
    {
        var createMenu = new System.Windows.Controls.MenuItem
        {
            Header = "Create",
            IsEnabled = ActiveFile is { IsReadOnly: false }
        };

        var interpActorItem = new System.Windows.Controls.MenuItem { Header = "Interp Actor..." };
        interpActorItem.Click += async (_, _) => await CreateStaticMeshActor("InterpActor", location);
        createMenu.Items.Add(interpActorItem);

        var staticMeshActorItem = new System.Windows.Controls.MenuItem { Header = "Static Mesh Actor..." };
        staticMeshActorItem.Click += async (_, _) => await CreateStaticMeshActor("StaticMeshActor", location);
        createMenu.Items.Add(staticMeshActorItem);

        var emitterItem = new System.Windows.Controls.MenuItem { Header = "Emitter..." };
        emitterItem.Click += async (_, _) => await CreateEmitter(location);
        createMenu.Items.Add(emitterItem);

        var pointOfInterestItem = new System.Windows.Controls.MenuItem
        {
            Header = "Point of Interest",
            IsEnabled = Game.IsGame3()
        };
        pointOfInterestItem.Click += (_, _) => CreatePointOfInterest(location);
        createMenu.Items.Add(pointOfInterestItem);

        var pointLightItem = new System.Windows.Controls.MenuItem { Header = "Point Light" };
        pointLightItem.Click += (_, _) => CreatePointLight(location);
        createMenu.Items.Add(pointLightItem);

        var spotLightItem = new System.Windows.Controls.MenuItem { Header = "Spot Light" };
        spotLightItem.Click += (_, _) => CreateSpotLight(location);
        createMenu.Items.Add(spotLightItem);

        return createMenu;
    }

    private async void GenerateStaticLighting() => await GenerateStaticLightingCore(null,
        Math.Min(LightmassResolution, StaticLightingBaker.MaximumLevelTextureResolution),
        StaticLightingMappingMode.Automatic);

    private async Task GenerateStaticLightingForActor(ActorProxy actor, int textureResolution,
        StaticLightingMappingMode mappingMode)
    {
        if (actor is null || actor.IsReadOnly || IsBusy)
            return;

        StaticMeshComponentProxy[] components = GetStaticLightingComponents(actor);
        if (components.Length == 0)
        {
            MessageBox.Show(this, "The selected actor has no static-mesh components eligible for Lightmass.",
                "Create Actor Lightmass", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (mappingMode != StaticLightingMappingMode.Vertex1D)
            _actorLightmassResolution = textureResolution;
        await GenerateStaticLightingCore(actor, textureResolution, mappingMode);
    }

    private async Task GenerateStaticLightingCore(ActorProxy targetActor, int textureResolution,
        StaticLightingMappingMode mappingMode)
    {
        bool isSingleActor = targetActor is not null;
        OpenLevelFile[] targetFiles = isSingleActor
            ? targetActor.OwningFile is { IsReadOnly: false } file ? [file] : []
            : OpenFiles.Where(file => file.IncludeInLightmass && !file.IsReadOnly).ToArray();
        if (targetFiles.Length == 0)
        {
            MessageBox.Show(this, isSingleActor
                    ? "The selected actor's level is read-only."
                    : "Select at least one writable loaded level to receive static lighting.",
                isSingleActor ? "Create Actor Lightmass" : "Create Lightmass",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = new StaticLightingGenerationSettings
        {
            TextureResolution = textureResolution,
            MappingMode = mappingMode,
            AmbientIntensity = LightmassAmbientIntensity,
            ShadowBias = LightmassShadowBias,
            ShadowSampleCount = LightmassShadowSamples,
            DefaultLightSourceRadius = LightmassSourceRadius,
            DirectionalSourceAngleDegrees = LightmassDirectionalSourceAngle,
            WorkerThreads = LightmassWorkerThreads,
            WorkTileSize = LightmassWorkTileSize,
            TextureCacheName = LightmassTextureCacheName,
            Backend = LightmassBackend
        };

        try
        {
            settings.Validate();
            StaticMeshComponentProxy[] actorComponents = isSingleActor
                ? GetStaticLightingComponents(targetActor)
                : [];
            IReadOnlySet<ExportEntry> exactTargetComponents = isSingleActor
                ? actorComponents.Select(component => component.Export).ToHashSet()
                : null;
            string targetList = isSingleActor
                ? $"  • {targetActor.Export.UIndex}: {targetActor.Export.ObjectName.Instanced} " +
                  $"({actorComponents.Length:N0} static component{(actorComponents.Length == 1 ? "" : "s")})"
                : string.Join("\n", targetFiles.Select(file => $"  • {file.FileName}"));
            string storageDescription = Game == MEGame.ME1
                ? "ME1 light data will be stored in the selected packages (ME1 does not use TFC texture caches)."
                : string.IsNullOrWhiteSpace(settings.TextureCacheName)
                    ? "Each target folder's existing TFC will be reused; if none exists, a dedicated Lightmass TFC will be created."
                    : $"Texture cache: {Path.GetFileNameWithoutExtension(settings.TextureCacheName)}.tfc";
            string targetDescription = isSingleActor
                ? "Build static lighting for this actor?"
                : $"Build static lighting for {targetFiles.Length:N0} loaded level(s)?";
            string mappingDescription = settings.MappingMode switch
            {
                StaticLightingMappingMode.Texture2D =>
                    $"Mapping: prefer LightMap2D at {settings.TextureResolution}×{settings.TextureResolution}; isolated collapsed UV triangles are repaired automatically, and components without a runtime-compatible lightmap UV transparently use LightMap1D.",
                StaticLightingMappingMode.Vertex1D =>
                    "Mapping: force LightMap1D per-vertex lighting.",
                _ =>
                    $"Mapping: automatic detail-aware selection; every LightMap2D receiver uses exactly {settings.TextureResolution}×{settings.TextureResolution}, and compact/dense receivers use LightMap1D."
            };
            string confirmation = $"{targetDescription}\n\n{targetList}\n\n" +
                                  $"Lights and occluding static geometry are gathered from all {OpenFiles.Count:N0} loaded levels. " +
                                  $"Existing lighting channels are preserved and strictly filter which lights may contribute. " +
                                  $"Components authored for emissive static lighting are reduced to bounded area samples and spatially culled per receiver. " +
                                  mappingDescription + "\n\n" +
                                  $"Soft shadows: {settings.ShadowSampleCount:N0} deterministic samples, " +
                                  $"{settings.DefaultLightSourceRadius:F1} point/spot radius, " +
                                  $"{settings.DirectionalSourceAngleDegrees:F1}° directional angle.\n" +
                                  $"Bake workers: {settings.EffectiveWorkerThreads:N0}; UV/spatial work tile: {settings.WorkTileSize}×{settings.WorkTileSize}.\n\n" +
                                  $"Bake backend: {(settings.Backend == StaticLightingBakeBackend.NativeCpp ? "Native C++" : "C#")}.\n\n" +
                                  storageDescription + "\n\nThe packages remain unsaved until you use Save, but TFC data is appended during generation.";
            string dialogTitle = isSingleActor ? "Create Actor Lightmass" : "Create Lightmass";
            if (MessageBox.Show(this, confirmation, dialogTitle, MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            IsBusy = true;
            IsBusyTaskbar = true;
            BusyText = "Collecting static meshes and lights from loaded levels...";
            BusyProgressIsIndeterminate = true;
            BusyProgressMaximum = 1;
            BusyProgressValue = 0;
            IProgress<StaticLightingBuildProgress> scanProgress =
                new LatestUiProgress<StaticLightingBuildProgress>(Dispatcher, UpdateStaticLightingScanProgress);
            IProgress<string> progress = new LatestUiProgress<string>(Dispatcher, message =>
            {
                BusyText = message;
                BusyProgressIsIndeterminate = true;
            });
            var targetSet = targetFiles.ToHashSet();
            ActorProxy[] sceneActors = Actors.ToArray();
            StaticLightingBakeResult bake = await Task.Run(() =>
            {
                var scene = StaticLightingBaker.BuildScene(sceneActors, targetSet, RenderContext,
                    exactTargetComponents, settings.MappingMode, settings.TextureResolution,
                    settings.Backend, settings.EffectiveWorkerThreads, scanProgress);
                if (scene.Targets.Count == 0)
                    return new StaticLightingBakeResult
                    {
                        Components = [],
                        SourceTriangleCount = scene.Collision.TriangleCount,
                        LightCount = scene.Lights.Count,
                        EmissiveEmitterCount = scene.EmissiveEmitters.Count,
                        TextureMappedComponentCount = 0,
                        VertexMappedComponentCount = 0,
                        Backend = settings.Backend,
                        SceneDiagnostics = scene.Diagnostics
                    };
                return new StaticLightingBaker(scene.Targets, scene.Lights, scene.Collision, settings,
                        scene.Diagnostics, scene.EmissiveEmitters)
                    .Bake(CancellationToken.None, progress, scanProgress);
            }).ConfigureAwait(true);

            if (bake.Components.Count == 0 && bake.SceneDiagnostics.ExcludedUnlitReceivers.Count == 0)
            {
                MessageBox.Show(this, isSingleActor
                        ? "The selected actor has no renderable static mesh components to bake."
                        : "No writable static mesh components were found in the selected levels.",
                    dialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BusyText = "Writing lightmaps, shadow maps, and texture-cache data...";
            BusyProgressIsIndeterminate = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            StaticLightingWriteResult written = StaticLightingWriter.Write(bake, settings);
            var writtenComponents = bake.Components.Select(component => component.Target.Component)
                .Concat(bake.SceneDiagnostics.ExcludedUnlitReceivers.Select(receiver => receiver.Component))
                .ToHashSet();
            foreach (PrimitiveComponentProxy component in Actors.SelectMany(actor => actor.Components))
            {
                if (writtenComponents.Contains(component.Export))
                    component.RefreshFromExport();
            }
            SceneViewer?.MarkRenderDirty();
            int mappingFallbackCount = bake.UvFallbackComponentCount;
            string mappingFallbackWarning = mappingFallbackCount > 0
                ? $"WARNING: {mappingFallbackCount:N0} component{(mappingFallbackCount == 1 ? " was" : "s were")} diverted to LightMap1D because no valid runtime lightmap UV was available.\n\n"
                : "";
            int mappingConflictTexels = bake.Components.Sum(component =>
                component.Diagnostics.MappingConflictTexelCount);
            int repairedUvTriangleCount = bake.Components
                .Where(component => component.Texture is not null)
                .Sum(component => component.Diagnostics.Mapping.DegenerateUvTriangleCount);
            string cacheSummary = written.TextureCachePaths.Count == 0
                ? "Lighting data was stored in the level packages."
                : "Texture cache output:\n" + string.Join("\n", written.TextureCachePaths.Select(path => $"  • {path}"));
            TextBelowActors = $"Lightmass: {written.ComponentCount:N0} static components, " +
                              $"{written.ExcludedUnlitReceiverCount:N0} unlit receivers protected, " +
                              $"{written.LightMapTextureCount:N0} lightmap textures, {written.ShadowMapCount:N0} shadow maps; " +
                              $"{written.IrrelevantLightReferenceCount:N0} irrelevant-light references; " +
                              $"{bake.WorkUnitCount:N0} parallel work units on {bake.WorkerCount:N0} workers; " +
                              $"{bake.RaysCast:N0} rays, {bake.AverageVisibility:P1} average visibility; " +
                              $"{bake.LightCount:N0} lights / {bake.EmissiveEmitterCount:N0} emissive area samples / " +
                              $"{bake.SourceTriangleCount:N0} occluder triangles from all loaded levels; " +
                              $"{(bake.Backend == StaticLightingBakeBackend.NativeCpp ? "Native C++" : "C#")} backend.";
            MessageBox.Show(this,
                mappingFallbackWarning +
                $"Generated static lighting for {written.ComponentCount:N0} components.\n\n" +
                $"Unlit-material receivers kept lightmap-free: {written.ExcludedUnlitReceiverCount:N0}\n" +
                $"2D lightmaps: {bake.TextureMappedComponentCount:N0} components\n" +
                $"Vertex-lightmap fallback: {bake.VertexMappedComponentCount:N0} components\n" +
                $"Lightmap textures: {written.LightMapTextureCount:N0}\n" +
                $"Shadow maps: {written.ShadowMapCount:N0}\n" +
                $"Irrelevant-light references: {written.IrrelevantLightReferenceCount:N0}\n" +
                $"Parallel work units: {bake.WorkUnitCount:N0} on {bake.WorkerCount:N0} workers\n" +
                $"Bake backend: {(bake.Backend == StaticLightingBakeBackend.NativeCpp ? "Native C++" : "C#")}\n" +
                $"Rays / occluded samples: {bake.RaysCast:N0} / {bake.OccludedSamples:N0}\n" +
                $"Emissive area samples / evaluated / rays: {bake.EmissiveEmitterCount:N0} / {bake.EmissiveSamplesEvaluated:N0} / {bake.EmissiveRaysCast:N0}\n" +
                $"Average direct visibility: {bake.AverageVisibility:P1}\n" +
                $"Average direct / environment contribution: {bake.AverageDirectContribution:F3} / {bake.AverageEnvironmentContribution:F3}\n" +
                $"Rejected receiver self-intersections: {bake.RejectedSelfIntersections:N0}\n" +
                $"Repaired degenerate UV triangles: {repairedUvTriangleCount:N0}\n" +
                $"UV mapping fallbacks / conflicting texels: {mappingFallbackCount:N0} / {mappingConflictTexels:N0}\n" +
                $"Scene extraction / light gathering: {bake.SceneDiagnostics.SceneExtractionMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.LightGatheringMilliseconds / 1000d:F2}s\n" +
                $"Mesh prep / receiver prep / BVH: {bake.SceneDiagnostics.MeshPreparationMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.ReceiverPreparationMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.BvhConstructionMilliseconds / 1000d:F2}s ({bake.SceneDiagnostics.BvhNodeCount:N0} nodes)\n" +
                $"Emissive preprocessing / receiver culling: {bake.SceneDiagnostics.EmissivePreprocessingMilliseconds / 1000d:F2}s / {bake.EmissiveReceiverCullingMilliseconds / 1000d:F2}s " +
                $"({bake.SceneDiagnostics.EmissiveSourceTriangleCount:N0} source triangles -> {bake.SceneDiagnostics.AreaEmitterSampleCount:N0} samples, {bake.SceneDiagnostics.AreaEmitterBvhNodeCount:N0} nodes)\n" +
                $"Worker time — light prep / 2D raster / 1D sampling: {bake.LightPreparationMilliseconds / 1000d:F2}s / {bake.TextureRasterizationMilliseconds / 1000d:F2}s / {bake.VertexSamplingMilliseconds / 1000d:F2}s\n" +
                $"Worker time — direct lighting / visibility rays: {bake.DirectLightingMilliseconds / 1000d:F2}s / {bake.ShadowRayMilliseconds / 1000d:F2}s\n" +
                $"Worker time — occupied texels / filtering / texture construction: {bake.OccupiedTexelDiscoveryMilliseconds / 1000d:F2}s / {bake.FilteringMilliseconds / 1000d:F2}s / {bake.TextureConstructionMilliseconds / 1000d:F2}s\n" +
                (bake.Backend == StaticLightingBakeBackend.NativeCpp
                    ? $"Native topology / instances / light scan: {bake.SceneDiagnostics.NativeTopologyScanMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.NativeInstanceScanMilliseconds / 1000d:F2}s / {bake.SceneDiagnostics.NativeLightScanMilliseconds / 1000d:F2}s\n" +
                      $"Native BVH / compute: {bake.NativeBvhConstructionMilliseconds / 1000d:F2}s ({bake.NativeBvhNodeCount:N0} nodes) / {bake.NativeComputeMilliseconds / 1000d:F2}s\n" +
                      $"Native 1D / 2D / shadow traversal: {bake.NativeBake1DMilliseconds / 1000d:F2}s / {bake.NativeBake2DMilliseconds / 1000d:F2}s / {bake.NativeShadowTraversalMilliseconds / 1000d:F2}s\n" +
                      $"Native samples / occupied texels / relevant lights: {bake.NativeSamplesProcessed:N0} / {bake.NativeOccupiedTexels:N0} / {bake.NativeRelevantLights:N0}\n" +
                      $"Native BVH visits / triangle tests / any-hit early-outs: {bake.NativeBvhNodesVisited:N0} / {bake.NativeRayTriangleTests:N0} / {bake.NativeAnyHitEarlyOuts:N0}\n" +
                      $"Native throughput: {bake.NativeSamplesPerSecond:N0} samples/s, {bake.NativeRaysPerSecond:N0} rays/s\n"
                    : "") +
                $"Texture-resolved emissive detail, multi-bounce GI and denoising: not present in this direct-light baker\n" +
                $"Bake wall time / total serialization: {bake.BakeMilliseconds / 1000d:F2}s / {written.SerializationMilliseconds / 1000d:F2}s\n" +
                $"LightMap1D / LightMap2D serialization: {written.LightMap1DSerializationMilliseconds / 1000d:F2}s / {written.LightMap2DSerializationMilliseconds / 1000d:F2}s\n" +
                $"Components replacing existing lighting: {written.ReplacedExistingComponentCount:N0}\n\n" +
                $"{cacheSummary}\n\nUse Save All to write the modified level packages.",
                dialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.FlattenException(),
                isSingleActor ? "Create Actor Lightmass" : "Create Lightmass", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsBusyTaskbar = false;
        }
    }

    private void CreatePointOfInterest(Vector3 location)
    {
        if (ActiveFile is not { IsReadOnly: false } activeFile || !activeFile.Package.Game.IsGame3())
        {
            return;
        }

        ExportEntry actorExport = null;
        try
        {
            IMEPackage package = activeFile.Package;
            actorExport = ExportCreator.CreateExport(package, "SFXPointOfInterest", "SFXPointOfInterest",
                activeFile.LevelExport, createWithStack: true);
            ExportEntry selectionModule = ExportCreator.CreateExport(package, "tempSelectionModule",
                "SFXSimpleUseModule", actorExport, indexed: false);
            selectionModule.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            selectionModule.Archetype = PreviewLevelBuilder.GetImportArchetype(package, "SFXGame",
                "Default__SFXPointOfInterest.tempSelectionModule");
            selectionModule.WriteProperties([
                new StringRefProperty(0, "m_srGameName"),
                new BoolProperty(false, "m_bTargetable"),
                new EnumProperty("ETargetTipText", package.Game, "m_TargetTipText")
            ]);

            var modules = new ArrayProperty<ObjectProperty>("Modules")
            {
                new ObjectProperty(selectionModule)
            };
            actorExport.WriteProperties([
                modules,
                CommonStructs.Vector3Prop(location, "location"),
                new NameProperty("SFXPointOfInterest", "Tag")
            ]);

            ActorProxy actor = ActorProxy.Create(this, actorExport)
                               ?? throw new InvalidOperationException(
                                   "The level editor cannot render SFXPointOfInterest actors.");
            actor.OwningFile = activeFile;

            Level level = activeFile.LevelExport.GetBinaryData<Level>();
            level.Actors.Add(actorExport.UIndex);
            activeFile.LevelExport.WriteBinary(level);
            AddActor(actor);
            SelectActor(actor, false);
            activeFile.IsDirty = true;
            UndoHistory.Clear();
            _preEditSnapshot = null;
            SceneViewer?.MarkRenderDirty();
        }
        catch (Exception ex)
        {
            if (actorExport is not null && !actorExport.IsTrash())
            {
                EntryPruner.TrashEntryAndDescendants(actorExport);
            }
            MessageBox.Show(this, $"Failed to create SFXPointOfInterest:\n{ex.Message}", "Error");
        }
    }

    private void CreatePointLight(Vector3 location)
    {
        if (ActiveFile is not { IsReadOnly: false } activeFile)
        {
            return;
        }

        ExportEntry actorExport = null;
        ActorProxy actor = null;
        try
        {
            IMEPackage package = activeFile.Package;
            actorExport = ExportCreator.CreateExport(package, "PointLight", "PointLight", activeFile.LevelExport, createWithStack: true);
            ExportEntry componentExport = ExportCreator.CreateExport(package, "PointLightComponent", "PointLightComponent", actorExport, prePropBinary: new byte[8]);
            componentExport.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            componentExport.Archetype = PreviewLevelBuilder.GetImportArchetype(package, "Engine", "Default__PointLight.PointLightComponent0");

            actorExport.WriteProperties([
                new ObjectProperty(componentExport, "LightComponent"),
                CommonStructs.Vector3Prop(location, "Location")
            ]);

            componentExport.WritePropertiesAndBinary(CreatePointLightProperties(location), LightComponent.Create());

            actor = ActorProxy.Create(this, actorExport)
                    ?? throw new InvalidOperationException("The level editor cannot render PointLight actors.");
            actor.OwningFile = activeFile;

            Level level = activeFile.LevelExport.GetBinaryData<Level>();
            level.Actors.Add(actorExport.UIndex);
            activeFile.LevelExport.WriteBinary(level);
            AddActor(actor);
            SelectActor(actor, false);
            activeFile.IsDirty = true;
            UndoHistory.Clear();
            _preEditSnapshot = null;
            SceneViewer?.MarkRenderDirty();
        }
        catch (Exception ex)
        {
            if (actorExport is not null && !actorExport.IsTrash())
            {
                EntryPruner.TrashEntryAndDescendants(actorExport);
            }
            MessageBox.Show(this, $"Failed to create PointLight:\n{ex.Message}", "Error");
        }
    }

    private static PropertyCollection CreatePointLightProperties(Vector3 location)
    {
        return [
            new FloatProperty(600f, "Radius"),
            new FloatProperty(5f, "Brightness"),
            CommonStructs.ColorProp(System.Drawing.Color.White, "LightColor"),
            new BoolProperty(true, "bHasLightEverBeenBuiltIntoLightMap"),
            CreateInitializedLightingChannelsProperty(),
            CommonStructs.ColorProp(System.Drawing.Color.White, "LightEnv_BouncedModulationColor"),
            new FloatProperty(0.129841f, "LightEnv_BouncedLightBrightness"),
            new BoolProperty(true, "CastsShadows"),
            CommonStructs.GuidProp(Guid.NewGuid(), "LightGuid"),
            CommonStructs.GuidProp(Guid.NewGuid(), "LightmassGuid"),
            CommonStructs.MatrixProp(ActorUtils.ComposeLocalToWorld(location, new Rotator(0, 0, 0), Vector3.One), "CachedParentToWorld"),
            new StructProperty("LightmassPointLightSettings", [
                new FloatProperty(0.2f, "IndirectLightingScale")
            ], "LightmassSettings"),
            new ArrayProperty<NameProperty>("OtherLevelsToAffect")
        ];
    }

    private void CreateSpotLight(Vector3 location)
    {
        if (ActiveFile is not { IsReadOnly: false } activeFile)
        {
            return;
        }

        ExportEntry actorExport = null;
        ActorProxy actor = null;
        try
        {
            IMEPackage package = activeFile.Package;
            actorExport = ExportCreator.CreateExport(package, "SpotLight", "SpotLight", activeFile.LevelExport, createWithStack: true);
            ExportEntry componentExport = ExportCreator.CreateExport(package, "SpotLightComponent", "SpotLightComponent", actorExport, prePropBinary: new byte[8]);
            componentExport.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            componentExport.Archetype = PreviewLevelBuilder.GetImportArchetype(package, "Engine", "Default__SpotLight.SpotLightComponent0");

            actorExport.WriteProperties([
                new ObjectProperty(componentExport, "LightComponent"),
                CommonStructs.Vector3Prop(location, "Location")
            ]);

            componentExport.WritePropertiesAndBinary(CreateSpotLightProperties(location), LightComponent.Create());

            actor = ActorProxy.Create(this, actorExport)
                    ?? throw new InvalidOperationException("The level editor cannot render SpotLight actors.");
            actor.OwningFile = activeFile;

            Level level = activeFile.LevelExport.GetBinaryData<Level>();
            level.Actors.Add(actorExport.UIndex);
            activeFile.LevelExport.WriteBinary(level);
            AddActor(actor);
            SelectActor(actor, false);
            activeFile.IsDirty = true;
            UndoHistory.Clear();
            _preEditSnapshot = null;
            SceneViewer?.MarkRenderDirty();
        }
        catch (Exception ex)
        {
            if (actorExport is not null && !actorExport.IsTrash())
            {
                EntryPruner.TrashEntryAndDescendants(actorExport);
            }
            MessageBox.Show(this, $"Failed to create SpotLight:\n{ex.Message}", "Error");
        }
    }

    private static PropertyCollection CreateSpotLightProperties(Vector3 location)
    {
        return [
            new FloatProperty(50f, "InnerConeAngle"),
            new FloatProperty(70f, "OuterConeAngle"),
            new FloatProperty(200f, "Radius"),
            new FloatProperty(2f, "Brightness"),
            CommonStructs.ColorProp(System.Drawing.Color.White, "LightColor"),
            new BoolProperty(true, "bHasLightEverBeenBuiltIntoLightMap"),
            CreateInitializedLightingChannelsProperty(),
            CommonStructs.ColorProp(System.Drawing.Color.White, "LightEnv_BouncedModulationColor"),
            new FloatProperty(0.129841f, "LightEnv_BouncedLightBrightness"),
            new BoolProperty(true, "CastsShadows"),
            CommonStructs.GuidProp(Guid.NewGuid(), "LightGuid"),
            CommonStructs.GuidProp(Guid.NewGuid(), "LightmassGuid"),
            CommonStructs.MatrixProp(ActorUtils.ComposeLocalToWorld(location, new Rotator(0, 0, 0), Vector3.One), "CachedParentToWorld"),
            new StructProperty("LightmassPointLightSettings", [
                new FloatProperty(0.2f, "IndirectLightingScale")
            ], "LightmassSettings"),
            new ArrayProperty<NameProperty>("OtherLevelsToAffect")
        ];
    }

    private async Task CreateEmitter(Vector3 location)
    {
        if (ActiveFile is not { IsReadOnly: false } activeFile)
        {
            return;
        }

        var picker = new ParticleSystemPickerDialog(Game, activeFile.Package, this);
        if (picker.ShowDialog() != true || picker.SelectedResult is null)
        {
            return;
        }

        ExportEntry actorExport = null;
        ActorProxy actor = null;
        IsBusy = true;
        BusyText = "Creating emitter...";
        await Task.Delay(1).ConfigureAwait(true);

        try
        {
            IMEPackage package = activeFile.Package;
            AssetImportResult importResult = AssetImportHelper.GetOrImportAsset(
                picker.SelectedResult.Value,
                package,
                "ParticleSystem");

            actorExport = ExportCreator.CreateExport(package, "Emitter", "Emitter", activeFile.LevelExport, createWithStack: true);
            ExportEntry componentExport = ExportCreator.CreateExport(
                package,
                "ParticleSystemComponent",
                "ParticleSystemComponent",
                actorExport,
                prePropBinary: new byte[8]);
            componentExport.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            componentExport.Archetype = PreviewLevelBuilder.GetImportArchetype(
                package,
                "Engine",
                "Default__Emitter.ParticleSystemComponent0");

            actorExport.WriteProperties([
                new ObjectProperty(componentExport, "ParticleSystemComponent"),
                CommonStructs.Vector3Prop(location, "Location")
            ]);
            componentExport.WriteProperties([
                new ObjectProperty(importResult.Entry, "Template"),
                new BoolProperty(true, "bJustAttached"),
                new ObjectProperty(0, "ReplacementPrimitive"),
                CreateInitializedLightingChannelsProperty("Static", "Dynamic", "CompositeDynamic")
            ]);

            actor = ActorProxy.Create(this, actorExport)
                    ?? throw new InvalidOperationException("The level editor cannot render Emitter actors.");
            actor.OwningFile = activeFile;

            _pendingSelect = (actorExport.UIndex, package, false);
            Level level = activeFile.LevelExport.GetBinaryData<Level>();
            level.Actors.Add(actorExport.UIndex);
            activeFile.LevelExport.WriteBinary(level);
            UndoHistory.Clear();
            _preEditSnapshot = null;

            if (importResult.RelinkWarnings.Count > 0)
            {
                string warnings = string.Join("\n", importResult.RelinkWarnings);
                MessageBox.Show(
                    this,
                    $"Import completed with {importResult.RelinkWarnings.Count} relink warning(s):\n{warnings}",
                    "Import Warnings");
            }
        }
        catch (Exception ex)
        {
            if (actorExport is not null && !actorExport.IsTrash())
            {
                EntryPruner.TrashEntryAndDescendants(actorExport);
            }
            MessageBox.Show(this, $"Failed to create Emitter:\n{ex.Message}", "Error");
        }
        finally
        {
            actor?.Dispose();
            IsBusy = false;
        }
    }

    private async Task CreateStaticMeshActor(string actorClassName, Vector3 location)
    {
        if (ActiveFile is not { IsReadOnly: false } activeFile)
        {
            return;
        }

        ExportEntry actorExport = null;
        ActorProxy actor = null;
        try
        {
            IMEPackage package = activeFile.Package;
            actorExport = ExportCreator.CreateExport(package, actorClassName, actorClassName, activeFile.LevelExport, createWithStack: true);
            ExportEntry componentExport = ExportCreator.CreateExport(package, "StaticMeshComponent", "StaticMeshComponent", actorExport, prePropBinary: new byte[8]);
            componentExport.ObjectFlags |= UnrealFlags.EObjectFlags.Transactional;
            componentExport.Archetype = PreviewLevelBuilder.GetImportArchetype(package, "Engine", $"Default__{actorClassName}.StaticMeshComponent0");

            actorExport.WriteProperties([
                new ObjectProperty(componentExport, "StaticMeshComponent"),
                new ObjectProperty(componentExport, "CollisionComponent"),
                CommonStructs.Vector3Prop(location, "Location")
            ]);

            componentExport.WritePropertiesAndBinary([
                new ObjectProperty(0, "ReplacementPrimitive"),
                new BoolProperty(true, "bCastDynamicShadow"),
                new BoolProperty(true, "CastShadow"),
                new BoolProperty(true, "CollideActors"),
                new BoolProperty(true, "BlockActors"),
                CreateInitializedLightingChannelsProperty()
            ], new byte[4]);

            actor = ActorProxy.Create(this, actorExport)
                    ?? throw new InvalidOperationException($"The level editor cannot render {actorClassName} actors.");
            actor.OwningFile = activeFile;
            if (!await ReplaceStaticMesh(actor, componentExport, false))
            {
                EntryPruner.TrashEntryAndDescendants(actorExport);
                return;
            }

            _pendingSelect = (actorExport.UIndex, package, false);
            Level level = activeFile.LevelExport.GetBinaryData<Level>();
            level.Actors.Add(actorExport.UIndex);
            activeFile.LevelExport.WriteBinary(level);
            UndoHistory.Clear();
            _preEditSnapshot = null;
        }
        catch (Exception ex)
        {
            if (actorExport is not null && !actorExport.IsTrash())
            {
                EntryPruner.TrashEntryAndDescendants(actorExport);
            }
            MessageBox.Show(this, $"Failed to create {actorClassName}:\n{ex.Message}", "Error");
        }
        finally
        {
            actor?.Dispose();
        }
    }

    private static StructProperty CreateInitializedLightingChannelsProperty(params string[] enabledChannels)
    {
        PropertyCollection properties = [];
        foreach ((string propertyName, _) in LightingChannelMenuItems)
        {
            bool isEnabled = propertyName == "bInitialized"
                             || enabledChannels.Contains(propertyName, StringComparer.Ordinal);
            properties.Add(new BoolProperty(isEnabled, propertyName));
        }

        return new StructProperty("LightingChannelContainer", properties, "LightingChannels");
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

    private void MeshExportsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject current)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(MeshExportsList, current)
            is ListBoxItem { DataContext: ActorProxy actor })
        {
            // Selection assignment alone does nothing when this item was already selected, so focus
            // explicitly on every double-click.
            SelectedActor = actor;
            FocusOnBounds(actor.GetBounds());
            SceneViewer.MarkRenderDirty();
            e.Handled = true;
        }
    }

    private System.Windows.Controls.ContextMenu BuildActorContextMenu(ActorProxy actor, Point? viewportPoint = null)
    {
        var contextMenu = new System.Windows.Controls.ContextMenu();

        if (viewportPoint.HasValue)
        {
            contextMenu.Items.Add(BuildCreateActorMenu(GetViewportLocationAtDepth(viewportPoint.Value, actor.Location)));
            contextMenu.Items.Add(new System.Windows.Controls.Separator());
        }

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

        if (viewportPoint.HasValue)
        {
            var snapHereItem = new System.Windows.Controls.MenuItem
            {
                Header = "Snap Selected Actor Here",
                IsEnabled = SelectedActor is not null && !SelectedActor.IsReadOnly
            };
            Point snapPoint = viewportPoint.Value;
            snapHereItem.Click += (_, _) => SnapSelectedActorToViewportPoint(snapPoint);
            contextMenu.Items.Add(snapHereItem);
        }

        var openPEItem = new System.Windows.Controls.MenuItem { Header = "Open in Package Editor" };
        openPEItem.Click += (_, _) =>
        {
            var p = new PackageEditorWindow();
            p.Show();
            p.LoadFile(actor.Export.FileRef.FilePath, actor.Export.UIndex);
            p.Activate();
        };
        contextMenu.Items.Add(openPEItem);

        if (actor is SkeletalMeshActorProxy or SFXStuntActorProxy)
        {
            var liveMaterialEditorItem = new System.Windows.Controls.MenuItem
            {
                Header = "Open Live Material Editor..."
            };
            liveMaterialEditorItem.Click += (_, _) => OpenActorPreviewEditor(actor, openMorphEditor: false);
            contextMenu.Items.Add(liveMaterialEditorItem);
        }

        if (ActorHasEditableMorph(actor))
        {
            var morphEditorItem = new System.Windows.Controls.MenuItem
            {
                Header = "Open Morph Editor..."
            };
            morphEditorItem.Click += (_, _) => OpenActorPreviewEditor(actor, openMorphEditor: true);
            contextMenu.Items.Add(morphEditorItem);
        }

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

            if (GetStaticLightingComponents(actor).Length > 0)
            {
                var generateLightmassMenu = new System.Windows.Controls.MenuItem
                {
                    Header = "Generate Lightmass",
                    IsEnabled = !actor.IsReadOnly && !IsBusy
                };
                generateLightmassMenu.Items.Add(BuildActorLightmassResolutionMenu(actor,
                    "Automatic (detail-aware)", StaticLightingMappingMode.Automatic,
                    "Uses LightMap2D for architectural, large, or broad low-poly receivers and LightMap1D for compact or dense receivers."));
                generateLightmassMenu.Items.Add(BuildActorLightmassResolutionMenu(actor,
                    "LightMap2D (texture)", StaticLightingMappingMode.Texture2D,
                    "Generates a texture mapping at the selected resolution, repairs isolated collapsed UV triangles, and falls back to LightMap1D when no valid runtime lightmap UV exists."));
                var vertexLightmassItem = new System.Windows.Controls.MenuItem
                {
                    Header = "LightMap1D (vertex)",
                    ToolTip = "Forces interpolated per-vertex lighting; no lightmap texture is generated."
                };
                vertexLightmassItem.Click += async (_, _) => await GenerateStaticLightingForActor(actor,
                    LightmassResolution, StaticLightingMappingMode.Vertex1D);
                generateLightmassMenu.Items.Add(vertexLightmassItem);
                contextMenu.Items.Add(generateLightmassMenu);
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
            replaceMeshItem.Click += async (_, _) => await ReplaceStaticMesh(actor, smcExport);
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

    private void OpenActorPreviewEditor(ActorProxy actor, bool openMorphEditor)
    {
        SelectedActor = actor;
        if (!openMorphEditor)
        {
            OpenLevelLiveMaterialEditor(actor);
            return;
        }

        OpenLevelMorphEditor(actor);
    }

    private void OpenLevelLiveMaterialEditor(ActorProxy actor)
    {
        CloseLevelMorphEditor();
        _levelLiveMaterialActor = actor;
        _levelLiveMaterialActorPackage = actor.Export.FileRef;
        _levelLiveMaterialActorUIndex = actor.Export.UIndex;
        LevelLiveMaterialEditor.LoadExternalLiveMaterialEditor(actor, RenderContext);
        IsLevelMaterialEditorOpen = true;
        SceneViewer.MarkRenderDirty();
    }

    private void OpenLevelMorphEditor(ActorProxy actor)
    {
        if (IsLevelMorphEditorOpen
            && actor.Export.FileRef == _levelMorphEditorActorPackage
            && actor.Export.UIndex == _levelMorphEditorActorUIndex)
        {
            return;
        }

        CloseLevelLiveMaterialEditor();
        _levelMorphEditorActorPackage = actor.Export.FileRef;
        _levelMorphEditorActorUIndex = actor.Export.UIndex;
        LevelMorphEditorTitle = $"Morph Editor - {actor.Export.UIndex}: {actor.Export.ObjectName.Instanced}";
        IsLevelMorphEditorOpen = true;
        LevelMorphEditor.LoadExport(actor.Export);
    }

    private void CloseLevelMorphEditor()
    {
        if (IsLevelMorphEditorOpen || LevelMorphEditor.CurrentLoadedExport is not null)
        {
            LevelMorphEditor.UnloadExport();
        }
        IsLevelMorphEditorOpen = false;
        _levelMorphEditorActorPackage = null;
        _levelMorphEditorActorUIndex = 0;
        LevelMorphEditorTitle = "Morph Editor";
    }

    private void SynchronizeLevelMorphEditor(ActorProxy actor)
    {
        if (!IsLevelMorphEditorOpen)
        {
            return;
        }

        if (actor is null
            || actor.Export.FileRef != _levelMorphEditorActorPackage
            || actor.Export.UIndex != _levelMorphEditorActorUIndex)
        {
            CloseLevelMorphEditor();
            return;
        }
    }

    private void CloseLevelMorphEditor_Click(object sender, RoutedEventArgs e) =>
        CloseLevelMorphEditor();

    private void CloseLevelLiveMaterialEditor()
    {
        IsLevelMaterialEditorOpen = false;
        _levelMaterialPickMouseDownPosition = null;
        _levelLiveMaterialActor = null;
        _levelLiveMaterialActorPackage = null;
        _levelLiveMaterialActorUIndex = 0;
        LevelLiveMaterialEditor.UnloadExternalLiveMaterialEditor();
        SceneViewer.MarkRenderDirty();
    }

    private void SynchronizeLevelLiveMaterialEditor(ActorProxy actor)
    {
        if (!IsLevelMaterialEditorOpen)
        {
            return;
        }

        if (actor is null
            || actor.Export.FileRef != _levelLiveMaterialActorPackage
            || actor.Export.UIndex != _levelLiveMaterialActorUIndex)
        {
            CloseLevelLiveMaterialEditor();
            return;
        }

        if (!ReferenceEquals(actor, _levelLiveMaterialActor))
        {
            _levelLiveMaterialActor = actor;
            LevelLiveMaterialEditor.LoadExternalLiveMaterialEditor(actor, RenderContext);
        }
    }

    private void LevelLiveMaterialEditor_CloseRequested(object sender, EventArgs e) =>
        CloseLevelLiveMaterialEditor();

    private void LevelLiveMaterialEditor_PreviewChanged(object sender, EventArgs e) =>
        SceneViewer.MarkRenderDirty();

    private void SceneViewer_PreviewMouseDownForMaterialPicking(object sender, MouseButtonEventArgs e)
    {
        if (IsLevelMaterialEditorOpen && e.ChangedButton == MouseButton.Left && _levelLiveMaterialActor is not null)
        {
            _levelMaterialPickMouseDownPosition = e.GetPosition(SceneViewer);
        }
    }

    private void SceneViewer_PreviewMouseUpForMaterialPicking(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _levelMaterialPickMouseDownPosition is not { } mouseDownPosition)
        {
            return;
        }

        _levelMaterialPickMouseDownPosition = null;
        Point mouseUpPosition = e.GetPosition(SceneViewer);
        System.Windows.Vector clickMovement = mouseUpPosition - mouseDownPosition;
        if (clickMovement.LengthSquared <= 16)
        {
            LevelLiveMaterialEditor.TrySelectLiveMaterialVectorAtPixel(
                _levelLiveMaterialActor, mouseUpPosition, SceneViewer, RenderContext);
        }
    }

    private bool ActorHasEditableMorph(ActorProxy rootActor)
    {
        var pending = new Stack<ActorProxy>();
        var visited = new HashSet<ActorProxy>();
        pending.Push(rootActor);

        while (pending.Count > 0)
        {
            ActorProxy actor = pending.Pop();
            if (!visited.Add(actor))
            {
                continue;
            }

            if (HasResolvableMorphHead(actor.Export)
                || actor.Components.OfType<SkeletalMeshComponentProxy>()
                    .Any(component => HasResolvableMorphHead(component.Export)))
            {
                return true;
            }

            foreach (ActorProxy attachedActor in actor.Attached)
            {
                pending.Push(attachedActor);
            }
        }

        return false;
    }

    private bool HasResolvableMorphHead(ExportEntry owner)
    {
        try
        {
            ObjectProperty morphHead = owner.GetCondensedProperties().GetProp<ObjectProperty>("MorphHead")
                                       ?? owner.GetProperty<ObjectProperty>("MorphHead");
            return morphHead?.ResolveToExport(owner.FileRef, RenderContext.PackageCache) is not null;
        }
        catch
        {
            return false;
        }
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

        bool addedAnyItems = false;

        foreach ((string propertyName, string displayName) in CollisionMenuItems)
        {
            if (!ComponentSupportsBoolProperty(componentExport, propertyName))
            {
                continue;
            }

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
            addedAnyItems = true;
        }

        return addedAnyItems ? collisionMenu : null;
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

    private static bool ComponentSupportsBoolProperty(ExportEntry export, string propertyName)
    {
        return GlobalUnrealObjectInfo.GetPropertyInfo(export.Game, NameReference.FromInstancedString(propertyName), export.ClassName, containingExport: export)?.Type == PropertyType.BoolProperty;
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

        _pendingSelect = (clonedExport.UIndex, actor.OwningFile.Package, true);
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

    private static StaticMeshComponentProxy[] GetStaticLightingComponents(ActorProxy actor) =>
        actor.Components.OfType<StaticMeshComponentProxy>()
            .Where(StaticLightingBaker.IsStaticLightingCandidate)
            .ToArray();

    private System.Windows.Controls.MenuItem BuildActorLightmassResolutionMenu(ActorProxy actor,
        string header, StaticLightingMappingMode mappingMode, string toolTip)
    {
        var mappingMenu = new System.Windows.Controls.MenuItem
        {
            Header = header,
            ToolTip = toolTip
        };

        AddResolutionItem(StaticLightingBaker.MaximumActorTextureResolution);
        foreach (int resolution in LightmassResolutions.Reverse())
            AddResolutionItem(resolution);
        return mappingMenu;

        void AddResolutionItem(int resolution)
        {
            int selectedResolution = resolution;
            var resolutionItem = new System.Windows.Controls.MenuItem
            {
                Header = $"{resolution} × {resolution}",
                IsCheckable = true,
                IsChecked = _actorLightmassResolution == resolution
            };
            resolutionItem.Click += async (_, _) => await GenerateStaticLightingForActor(actor,
                selectedResolution, mappingMode);
            mappingMenu.Items.Add(resolutionItem);
        }
    }

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

    private async Task<bool> ReplaceStaticMesh(ActorProxy actor, ExportEntry componentExport, bool refreshActor = true)
    {
        var picker = new StaticMeshPickerDialog(Game, componentExport.FileRef, this);
        if (picker.ShowDialog() != true || picker.SelectedResult is null) return false;

        IsBusy = true;
        BusyText = "Replacing static mesh...";
        await Task.Delay(1).ConfigureAwait(true);

        try
        {
            AssetImportResult importResult = AssetImportHelper.GetOrImportAsset(
                picker.SelectedResult.Value,
                componentExport.FileRef,
                "StaticMesh");
            var props = componentExport.GetProperties();
            props.AddOrReplaceProp(new ObjectProperty(importResult.Entry.UIndex, "StaticMesh"));
            componentExport.WriteProperties(props);

            if (importResult.RelinkWarnings.Count > 0)
            {
                string warnings = string.Join("\n", importResult.RelinkWarnings);
                MessageBox.Show(this, $"Import completed with {importResult.RelinkWarnings.Count} relink warning(s):\n{warnings}", "Import Warnings");
            }

            if (refreshActor)
            {
                RefreshActorInViewport(actor);
            }
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to replace static mesh:\n{ex.Message}", "Error");
            return false;
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
        var savedCamera = (RenderContext.Camera.Position, RenderContext.Camera.Pitch,
            RenderContext.Camera.Yaw, RenderContext.Camera.Roll);
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
                SelectActor(newActor, false);
                (RenderContext.Camera.Position, RenderContext.Camera.Pitch,
                    RenderContext.Camera.Yaw, RenderContext.Camera.Roll) = savedCamera;
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

        IsBusy = true;
        BusyText = "Replacing skeletal mesh...";
        await Task.Delay(1).ConfigureAwait(true);

        try
        {
            AssetImportResult importResult = AssetImportHelper.GetOrImportAsset(
                picker.SelectedResult.Value,
                componentExport.FileRef,
                "SkeletalMesh");
            var props = componentExport.GetProperties();
            props.AddOrReplaceProp(new ObjectProperty(importResult.Entry.UIndex, "SkeletalMesh"));
            componentExport.WriteProperties(props);

            if (importResult.RelinkWarnings.Count > 0)
            {
                string warnings = string.Join("\n", importResult.RelinkWarnings);
                MessageBox.Show(this, $"Import completed with {importResult.RelinkWarnings.Count} relink warning(s):\n{warnings}", "Import Warnings");
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
