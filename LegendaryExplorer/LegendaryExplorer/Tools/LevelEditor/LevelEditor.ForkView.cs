using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorerCore.Packages;
using Microsoft.Win32;
using MessageBox = LegendaryExplorer.SharedUI.ThemedMessageBox;

namespace LegendaryExplorer.Tools.LevelEditor;

/// <summary>
/// Fork: the View options dropdown (show flags, stats overlay, in-game shaders, channel and background options), light icon limits, Z cutoff, Capture
/// Viewport and Copy viewport issues, and the per-frame hooks the fork adds to upstream's scene update and render
/// </summary>
public partial class LevelEditor
{
    #region Construction and teardown

    /// <summary>Applies the fork's persisted view options to the render context, before the window's controls bind to them</summary>
    private void InitializeForkState()
    {
        RenderContext.UseGameShaders = _useGameMeshShaders;
        RenderContext.ShowStatsOverlay = _showStatsOverlay;
        RenderContext.ShowSelectedLightWireframe = _showSelectedLightWireframe;
        RenderContext.ShowLightIcons = _showLightIcons;
        ApplyEffectsViewOptions();
        RenderContext.ShowDecalActors = _showDecalActors;
        RenderContext.ShowPointsOfInterest = _showPointsOfInterest;
        RenderContext.ShowVolumes = _showVolumes;
        RenderContext.ShowVolumetrics = _showVolumetrics;
        // The fork's orbit/first-person choice is app-wide
        RenderContext.Camera.FirstPerson = !Settings.Global_UseOrbitCameraControls;
        _backgroundColor = GetThemeDefaultBackgroundColor();
        RenderContext.BackgroundColor = _backgroundColor;
        if (Settings.LevelEditor_Unlit) _lightingMode = ViewportLightingMode.Unlit;
        // The fork's Unlit checkbox follows the Lighting menu
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LightingMode)) return;
            Settings.LevelEditor_Unlit = LightingMode == ViewportLightingMode.Unlit;
            OnPropertyChanged(nameof(Unlit));
        };
        if (IsBakePreview) return;
        LevelPresetsView = CreateLevelPresetsView();
        Actors.CollectionChanged += ForkActors_CollectionChanged;
        OpenFiles.CollectionChanged += (_, _) => OnForkOpenFilesChanged();
    }

    /// <summary>Wires the fork's controls once the window's are made</summary>
    private void InitializeForkUi()
    {
        CopyViewportIssuesCommand = SceneViewer.CreateCopyViewportIssuesCommand(() => TextBelowActors);
        if (IsBakePreview) return;
        if (_lightingMode == ViewportLightingMode.Unlit) RenderContext.LightingMode = ViewportLightingMode.Unlit;
        InitializeForkLandingPage();
        SyncViewOptionsWithOutlinerCategories();
        // Resource preparation yields to interaction anywhere in the editor, not only over the viewport
        PreviewMouseMove += (_, _) => RenderContext.NotifyUserActivity();
        PreviewMouseDown += (_, _) => RenderContext.NotifyUserActivity();
        PreviewMouseWheel += (_, _) => RenderContext.NotifyUserActivity();
        ApplyPointOfInterestToolTipTheme();
        LevelLiveMaterialEditor.CloseMaterialEditorRequested += LevelLiveMaterialEditor_CloseRequested;
        LevelLiveMaterialEditor.LiveMaterialPreviewChanged += LevelLiveMaterialEditor_PreviewChanged;
        VolumeDrawing.PropertyChanged += VolumeDrawingSettingsChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    /// <summary>Releases what <see cref="InitializeForkUi"/> took (static events would keep a closed editor alive)</summary>
    private void DisposeForkUi()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        Actors.CollectionChanged -= ForkActors_CollectionChanged;
        VolumeDrawing.PropertyChanged -= VolumeDrawingSettingsChanged;
        _volumeXrayState?.Dispose();
        _volumeXrayState = null;
        LevelLiveMaterialEditor.CloseMaterialEditorRequested -= LevelLiveMaterialEditor_CloseRequested;
        LevelLiveMaterialEditor.LiveMaterialPreviewChanged -= LevelLiveMaterialEditor_PreviewChanged;
        LevelLiveMaterialEditor.Dispose();
        LevelMorphEditor.Dispose();
        DisposeForkLandingPage();
    }

    /// <summary>Closes what edits the scene outside upstream's model (volume drawing, the material and morph editors) before the files close</summary>
    private void CloseForkEditors()
    {
        CloseLevelMorphEditor();
        CloseLevelLiveMaterialEditor();
        CancelVolumeDrawing();
    }

    private void OnThemeChanged(object sender, bool isDarkMode)
    {
        BackgroundColor = GetThemeDefaultBackgroundColor();
        ApplyPointOfInterestToolTipTheme();
    }

    private void ApplyPointOfInterestToolTipTheme()
    {
        // Same keys as the themes' ToolTip style, so Light, Dark and Modern Dark all resolve from their own dictionaries
        PointOfInterestToolTip.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, SystemColors.InfoBrushKey);
        PointOfInterestToolTip.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, SystemColors.InfoTextBrushKey);
        PointOfInterestToolTip.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
    }

    #endregion

    #region Active file

    private OpenLevelFile _activeFile;
    /// <summary>
    /// Fork: the file the selection is in, else the first open one. Volume drawing, navigation generation and the Create menu work in it; the status
    /// bar's mount warning and last-saved time are its package's
    /// </summary>
    public OpenLevelFile ActiveFile
    {
        get => _activeFile;
        private set
        {
            if (SetProperty(ref _activeFile, value))
            {
                OnPropertyChanged(nameof(Pcc));
                if (IsDrawingVolume && value != _volumeDrawFile) CancelVolumeDrawing();
            }
        }
    }

    /// <summary>Fork: the active file's package (status bar)</summary>
    public IMEPackage Pcc => ActiveFile?.Package;

    private void UpdateActiveFile() => ActiveFile = selectedActor?.OwningFile is { } file && OpenFiles.Contains(file) ? file : OpenFiles.FirstOrDefault();

    private void OnForkOpenFilesChanged()
    {
        UpdateActiveFile();
        RefreshNavigationOverlay();
        if (OpenFiles.Count == 0)
        {
            ResetCameraPositionText();
            ResetCameraRotationText();
            UnloadPropertyTabs();
            CloseLevelMorphEditor();
            CloseLevelLiveMaterialEditor();
        }
    }

    #endregion

    #region Per-frame hooks

    private long _lastCameraTextUpdateTimestamp;

    private void UpdateForkScene(float timestep)
    {
        long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (timestamp - _lastCameraTextUpdateTimestamp >= System.Diagnostics.Stopwatch.Frequency / 10)
        {
            _lastCameraTextUpdateTimestamp = timestamp;
            UpdateCameraPositionText();
            UpdateCameraRotationText();
        }
        ApplyArrowKeyCameraInput(timestep);
    }

    //the light actors whose icons are drawn this frame: the nearest MaxLightIcons within LightIconRadius (and the selected one)
    private readonly HashSet<ActorProxy> lightIconsShown = [];
    private readonly List<(ActorProxy Light, float DistanceSquared)> lightIconCandidates = [];

    /// <summary>Fork: the light icon budget (toolbar's Light Icons Radius and Max), worked out once per frame</summary>
    private void PrepareForkVisibility()
    {
        lightIconsShown.Clear();
        lightIconCandidates.Clear();
        float radius = RenderContext.LightIconRadius;
        int max = RenderContext.MaxLightIcons;
        Vector3 eye = RenderContext.Camera.EyePosition;
        foreach (ActorProxy actor in RenderContext.DrawList_3D)
        {
            if (actor is not ILightProxy || !actor.HasSpatialRepresentation) continue;
            float distanceSquared = Vector3.DistanceSquared(actor.Location, eye);
            if (radius <= 0 || distanceSquared <= radius * radius) lightIconCandidates.Add((actor, distanceSquared));
        }
        if (lightIconCandidates.Count > max) lightIconCandidates.Sort((a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
        for (int i = 0; i < lightIconCandidates.Count && i < max; i++) lightIconsShown.Add(lightIconCandidates[i].Light);
    }

    /// <summary>
    /// Fork: actors the fork's own view options hide on top of upstream's visibility: the Z cutoff, the volume being edited, and light icons beyond the
    /// light icon budget (a light actor draws only its icon: its light is the level's)
    /// </summary>
    private bool IsVisibleInForkView(ActorProxy actor)
    {
        if (_volumeEdit != null && actor.Export == _volumeEdit.Actor) return false;
        if (_zCutoffEnabled && actor.HasSpatialRepresentation && actor.Location.Z >= _zCutoff) return false;
        if (actor is ILightProxy && actor != selectedActor && !lightIconsShown.Contains(actor)) return false;
        return true;
    }

    /// <summary>Fork overlays: pathing and cover, the selected actor's preview animation name, and the volume being drawn or edited</summary>
    private void RenderForkOverlays()
    {
        if (navigationOverlay.ShowPaths || navigationOverlay.ShowCover) navigationOverlay.Draw(RenderContext);
        if (selectedActor is { HasSpatialRepresentation: true } actor && !string.IsNullOrWhiteSpace(actor.PreviewAnimationName) && visibleActors.Contains(actor))
        {
            BoxSphereBoundsLabel(actor, $"Animation: {actor.PreviewAnimationName}");
        }
        RenderVolumeDrawing();
    }

    private void BoxSphereBoundsLabel(ActorProxy actor, string text)
    {
        var bounds = actor.GetBounds();
        Vector3 labelPosition = bounds.Origin + Vector3.UnitZ * (MathF.Abs(bounds.BoxExtent.Z) + 20f);
        if (RenderContext.WorldToPixel(labelPosition, out Vector2 pixel))
        {
            RenderContext.ScreenLabels.Add(new ScreenLabel(pixel.X, pixel.Y, text));
        }
    }

    #endregion

    #region View options

    private bool _showStatsOverlay = Settings.LevelEditor_ShowStatsOverlay;
    public bool ShowStatsOverlay
    {
        get => _showStatsOverlay;
        set
        {
            if (SetProperty(ref _showStatsOverlay, value))
            {
                Settings.LevelEditor_ShowStatsOverlay = value;
                RenderContext.ShowStatsOverlay = value;
            }
        }
    }

    private bool _showSelectedLightWireframe = Settings.LevelEditor_ShowSelectedLightWireframe;
    public bool ShowSelectedLightWireframe
    {
        get => _showSelectedLightWireframe;
        set
        {
            if (SetProperty(ref _showSelectedLightWireframe, value))
            {
                Settings.LevelEditor_ShowSelectedLightWireframe = value;
                RenderContext.ShowSelectedLightWireframe = value;
                // Upstream's light gizmo draws the selected light's radius or cone and its direction
                RenderContext.LightGizmo.Attach = value ? selectedActor as ILightProxy : null;
            }
        }
    }

    private bool _showLightIcons = Settings.LevelEditor_ShowLightIcons;
    /// <summary>Light icons are the outliner's Lights category (its eye toggle shows the same)</summary>
    public bool ShowLightIcons
    {
        get => _showLightIcons;
        set
        {
            if (SetProperty(ref _showLightIcons, value))
            {
                Settings.LevelEditor_ShowLightIcons = value;
                RenderContext.ShowLightIcons = value;
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

    private bool _useGameMeshShaders = Settings.LevelEditor_UseGameMeshShaders;
    public bool UseGameMeshShaders
    {
        get => _useGameMeshShaders;
        set
        {
            if (SetProperty(ref _useGameMeshShaders, value))
            {
                Settings.LevelEditor_UseGameMeshShaders = value;
                // Prepared shaders are kept, for instant switching; materials loaded while off are prepared on the next frame (RenderScene)
                RenderContext.UseGameShaders = value;
                RenderContext.InvalidateStaticSceneCache();
            }
        }
    }

    private bool _showEmitters = Settings.LevelEditor_ShowEmitters;
    public bool ShowEmitters
    {
        get => _showEmitters;
        set
        {
            if (SetProperty(ref _showEmitters, value))
            {
                Settings.LevelEditor_ShowEmitters = value;
                ApplyEffectsViewOptions();
            }
        }
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
                ApplyEffectsViewOptions();
            }
        }
    }

    private bool _showLensFlareIcons = Settings.LevelEditor_ShowLensFlareIcons;
    public bool ShowLensFlareIcons
    {
        get => _showLensFlareIcons;
        set
        {
            if (SetProperty(ref _showLensFlareIcons, value))
            {
                Settings.LevelEditor_ShowLensFlareIcons = value;
                ApplyEffectsViewOptions();
            }
        }
    }

    /// <summary>
    /// The emitter and lens flare options onto the render context. Emitters and lens flare sources are upstream's "Effects helpers" outliner category,
    /// hidden by default there: "Show emitters" also shows or hides that category (and its eye toggle sets "Show emitters": see
    /// <see cref="SyncViewOptionsWithOutlinerCategories"/>), so effects show by default as they did in the fork
    /// </summary>
    private void ApplyEffectsViewOptions()
    {
        RenderContext.ShowEmitters = _showEmitters;
        RenderContext.ShowEmitterIcons = _showEmitters;
        RenderContext.SetShowEmitterVfx(_showEmitters && _showEmitterVfx);
        RenderContext.ShowLensFlareIcons = _showLensFlareIcons;
        RenderContext.GetCategoryState(EffectsHelpersCategory).IsVisible = _showEmitters;
    }

    private const string EffectsHelpersCategory = "Effects helpers";

    /// <summary>
    /// The outliner's category eye toggles and the fork's matching View options stay in step both ways: Effects helpers and Show emitters, Volumes and
    /// Volumes / models, Volumetric meshes and Show volumetric meshes, Lights and Show light icons (each option is persisted, as the fork's were)
    /// </summary>
    private void SyncViewOptionsWithOutlinerCategories()
    {
        Follow(EffectsHelpersCategory, visible => ShowEmitters = visible);
        Follow("Volumes", visible => ShowVolumes = visible);
        Follow("Volumetric meshes", visible => ShowVolumetrics = visible);
        Follow("Lights", visible => ShowLightIcons = visible);

        void Follow(string category, Action<bool> setOption)
        {
            OutlinerCategoryState state = RenderContext.GetCategoryState(category);
            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OutlinerCategoryState.IsVisible)) setOption(state.IsVisible);
            };
        }
    }

    private bool _showDecalActors = true;
    public bool ShowDecalActors
    {
        get => _showDecalActors;
        set
        {
            if (SetProperty(ref _showDecalActors, value)) RenderContext.ShowDecalActors = value;
        }
    }

    private bool _showPointsOfInterest = true;
    public bool ShowPointsOfInterest
    {
        get => _showPointsOfInterest;
        set
        {
            if (SetProperty(ref _showPointsOfInterest, value)) RenderContext.ShowPointsOfInterest = value;
        }
    }

    private bool _showVolumes = Settings.LevelEditor_ShowVolumes;
    /// <summary>Volumes are the outliner's Volumes category (its eye toggle shows the same)</summary>
    public bool ShowVolumes
    {
        get => _showVolumes;
        set
        {
            if (SetProperty(ref _showVolumes, value))
            {
                Settings.LevelEditor_ShowVolumes = value;
                RenderContext.ShowVolumes = value;
            }
        }
    }

    private bool _showVolumetrics = Settings.LevelEditor_ShowVolumetrics;
    /// <summary>Volumetric meshes are the outliner's Volumetric meshes category</summary>
    public bool ShowVolumetrics
    {
        get => _showVolumetrics;
        set
        {
            if (SetProperty(ref _showVolumetrics, value))
            {
                Settings.LevelEditor_ShowVolumetrics = value;
                RenderContext.ShowVolumetrics = value;
            }
        }
    }

    /// <summary>Fork's Unlit checkbox: upstream's Unlit lighting mode (cleared, it returns to level lighting). Kept for the session, as the fork did</summary>
    public bool Unlit
    {
        get => LightingMode == ViewportLightingMode.Unlit;
        set
        {
            if (value == Unlit) return;
            Settings.LevelEditor_Unlit = value;
            LightingMode = value ? ViewportLightingMode.Unlit : ViewportLightingMode.Level;
            OnPropertyChanged();
        }
    }

    private bool _showPathing;
    public bool ShowPathing
    {
        get => _showPathing;
        set
        {
            if (SetProperty(ref _showPathing, value)) navigationOverlay.ShowPaths = value;
        }
    }

    private bool _showCover;
    public bool ShowCover
    {
        get => _showCover;
        set
        {
            if (SetProperty(ref _showCover, value)) navigationOverlay.ShowCover = value;
        }
    }

    private bool _zCutoffEnabled;
    public bool ZCutoffEnabled
    {
        get => _zCutoffEnabled;
        set => SetProperty(ref _zCutoffEnabled, value);
    }

    private float _zCutoff;
    public float ZCutoff
    {
        get => _zCutoff;
        set => SetProperty(ref _zCutoff, value);
    }

    #endregion

    #region Gizmo mode (two sets of radio buttons: the toolbar's and View options')

    public bool IsTranslateMode
    {
        get => RenderContext.TransformWidget.Mode == EWidgetMode.Translate;
        set { if (value && ToggleTranslateCommand.CanExecute(null)) ToggleTranslateCommand.Execute(null); else OnWidgetModeChanged(); }
    }

    public bool IsRotateMode
    {
        get => RenderContext.TransformWidget.Mode == EWidgetMode.Rotate;
        set { if (value && ToggleRotateCommand.CanExecute(null)) ToggleRotateCommand.Execute(null); else OnWidgetModeChanged(); }
    }

    public bool IsScaleMode
    {
        get => RenderContext.TransformWidget.Mode == EWidgetMode.Scale;
        set { if (value && ToggleScaleCommand.CanExecute(null)) ToggleScaleCommand.Execute(null); else OnWidgetModeChanged(); }
    }

    public bool IsUniformScaleMode
    {
        get => RenderContext.TransformWidget.Mode == EWidgetMode.UniformScale;
        set { if (value && ToggleUniformScaleCommand.CanExecute(null)) ToggleUniformScaleCommand.Execute(null); else OnWidgetModeChanged(); }
    }

    private void OnWidgetModeChanged()
    {
        OnPropertyChanged(nameof(IsTranslateMode));
        OnPropertyChanged(nameof(IsRotateMode));
        OnPropertyChanged(nameof(IsScaleMode));
        OnPropertyChanged(nameof(IsUniformScaleMode));
    }

    #endregion

    #region Channels and background (the fork's scene options)

    private bool _setAlphaToBlack = true;
    public bool SetAlphaToBlack
    {
        get => _setAlphaToBlack;
        set { if (SetProperty(ref _setAlphaToBlack, value)) SetRenderFlag(LevelEditorRenderContext.ShaderFlags.AlphaAsBlack, value); }
    }

    private bool _showRedChannel = true;
    public bool ShowRedChannel
    {
        get => _showRedChannel;
        set { if (SetProperty(ref _showRedChannel, value)) SetRenderFlag(LevelEditorRenderContext.ShaderFlags.EnableRedChannel, value); }
    }

    private bool _showGreenChannel = true;
    public bool ShowGreenChannel
    {
        get => _showGreenChannel;
        set { if (SetProperty(ref _showGreenChannel, value)) SetRenderFlag(LevelEditorRenderContext.ShaderFlags.EnableGreenChannel, value); }
    }

    private bool _showBlueChannel = true;
    public bool ShowBlueChannel
    {
        get => _showBlueChannel;
        set { if (SetProperty(ref _showBlueChannel, value)) SetRenderFlag(LevelEditorRenderContext.ShaderFlags.EnableBlueChannel, value); }
    }

    private bool _showAlphaChannel = true;
    public bool ShowAlphaChannel
    {
        get => _showAlphaChannel;
        set { if (SetProperty(ref _showAlphaChannel, value)) SetRenderFlag(LevelEditorRenderContext.ShaderFlags.EnableAlphaChannel, value); }
    }

    private void SetRenderFlag(LevelEditorRenderContext.ShaderFlags flag, bool on)
    {
        if (on) RenderContext.RenderFlags |= flag;
        else RenderContext.RenderFlags &= ~flag;
    }

    private System.Windows.Media.Color _backgroundColor;
    public System.Windows.Media.Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (SetProperty(ref _backgroundColor, value)) RenderContext.BackgroundColor = value;
        }
    }

    /// <summary>
    /// The default viewport background for the current theme: dark mode uses the same dark background as the Sequence Editor. Shared with the fork's other
    /// 3D tools (camera preset preview, BioStage editor, curve editor)
    /// </summary>
    public static System.Windows.Media.Color GetThemeDefaultBackgroundColor()
    {
        return Settings.Global_DarkMode_Enabled
            ? ThemeManager.DarkCanvasMediaColor
            : System.Windows.Media.Color.FromRgb(128, 128, 128);
    }

    #endregion

    #region Capture viewport and viewport issues

    public ICommand CaptureViewportCommand { get; private set; }

    private ICommand _copyViewportIssuesCommand;
    public ICommand CopyViewportIssuesCommand
    {
        get => _copyViewportIssuesCommand;
        private set => SetProperty(ref _copyViewportIssuesCommand, value);
    }

    private void CaptureViewport()
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".png",
            FileName = $"LevelEditorScreenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            Filter = "PNG image|*.png"
        };

        if (DirectoryMemory.ShowDialog(dialog) != true)
        {
            return;
        }

        System.Windows.Media.Imaging.BitmapSource screenshot = SceneViewer.CaptureBitmap();
        if (screenshot is null)
        {
            MessageBox.Show(this, "The viewport is not ready to capture.", "Capture Viewport", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(screenshot));
        using FileStream stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    #endregion
}
