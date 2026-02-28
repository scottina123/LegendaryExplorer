using System;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FontAwesome5;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.SharpDX;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SkeletalMesh = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;

namespace LegendaryExplorer.UserControls.SharedToolControls;

/// <summary>
/// Self-contained animation preview control with 3D viewport, CPU skinning, and playback controls.
/// Supports both AnimSequence (frame-based) and FaceFxAnimSet (time-based) playback.
/// </summary>
public partial class AnimationPreviewControl : NotifyPropertyChangedControlBase, ISceneRenderContextConfigurable
{
    private MeshRenderContext _meshContext;
    private ModelPreview<WorldVertex> _meshPreview;
    private SkinnedMeshRenderer _skinnedRenderer;
    private AnimPlayer _animPlayer;
    private SkeletalMesh _skm;
    private bool _previewInitialized;
    private ExportEntry _lastAnimExport;
    private FaceFXAsset _lastFxActor;
    private FaceFXAnimSet _lastFxAnimSet;
    private FaceFXLine _lastFxLine;

    public SkeletalMesh CurrentMesh => _skm;

    #region Bindable Properties

    private double _animSliderValue;
    /// <summary>
    /// Current slider position. In frame mode this is the frame index; in time mode it is seconds (may be negative).
    /// </summary>
    public double AnimSliderValue
    {
        get => _animSliderValue;
        set
        {
            if (SetProperty(ref _animSliderValue, value) && _animPlayer != null)
            {
                if (IsTimeMode)
                {
                    _animPlayer.CurrentTime = (float)value;
                    AnimTimeChanged?.Invoke((float)value);
                }
                else
                    ((AnimSequencePlayer)_animPlayer).CurrentFrame = (int)value;

                if (!_animPlayer.IsPlaying)
                    UpdateSkinningOneShot();

                OnPropertyChanged(nameof(AnimPositionText));
            }
        }
    }

    private double _animSliderMin;
    public double AnimSliderMin
    {
        get => _animSliderMin;
        set => SetProperty(ref _animSliderMin, value);
    }

    private double _animSliderMax;
    public double AnimSliderMax
    {
        get => _animSliderMax;
        set => SetProperty(ref _animSliderMax, value);
    }

    private bool _isTimeMode;
    /// <summary>
    /// True when showing a FaceFx animation (time-based, continuous slider).
    /// False when showing an AnimSequence (frame-based, snapped slider).
    /// </summary>
    public bool IsTimeMode
    {
        get => _isTimeMode;
        set => SetProperty(ref _isTimeMode, value);
    }

    /// <summary>
    /// Display text for the current playback position. "N / M" in frame mode, "Xs / Ys" in time mode.
    /// </summary>
    public EFontAwesomeIcon PlayPauseIcon => _animPlayer is { IsPlaying: true }
        ? EFontAwesomeIcon.Solid_Pause
        : EFontAwesomeIcon.Solid_Play;

    public string AnimPositionText => IsTimeMode
        ? $"{_animSliderValue:F2}s / {_animSliderMax:F2}s"
        : $"{(int)_animSliderValue} / {(int)_animSliderMax}";

    private double _playbackSpeed = 1.0;
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            if (SetProperty(ref _playbackSpeed, value) && _animPlayer != null)
            {
                _animPlayer.PlaybackSpeed = (float)value;
            }
        }
    }

    private bool removeOffset = true;
    public bool RemoveOffset
    {
        get => removeOffset;
        set => SetProperty(ref removeOffset, value);
    }

    #endregion

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
                    _meshContext.RenderFlags |= RenderContext.ShaderFlags.AlphaAsBlack;
                else
                    _meshContext.RenderFlags &= ~RenderContext.ShaderFlags.AlphaAsBlack;
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
                    _meshContext.RenderFlags |= RenderContext.ShaderFlags.EnableRedChannel;
                else
                    _meshContext.RenderFlags &= ~RenderContext.ShaderFlags.EnableRedChannel;
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
                    _meshContext.RenderFlags |= RenderContext.ShaderFlags.EnableGreenChannel;
                else
                    _meshContext.RenderFlags &= ~RenderContext.ShaderFlags.EnableGreenChannel;
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
                    _meshContext.RenderFlags |= RenderContext.ShaderFlags.EnableBlueChannel;
                else
                    _meshContext.RenderFlags &= ~RenderContext.ShaderFlags.EnableBlueChannel;
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
                    _meshContext.RenderFlags |= RenderContext.ShaderFlags.EnableAlphaChannel;
                else
                    _meshContext.RenderFlags &= ~RenderContext.ShaderFlags.EnableAlphaChannel;
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
                _meshContext.BackgroundColor = value;
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

    #region Commands

    public ICommand PlayPauseCommand { get; }

    #endregion

    /// <summary>
    /// Fires when the animation time changes, either during playback or when the slider is scrubbed.
    /// Only fires in time mode (FaceFx animations).
    /// </summary>
    public event Action<float> AnimTimeChanged;

    /// <summary>
    /// Fires when the playing state changes. True = started playing, False = paused/stopped.
    /// </summary>
    public event Action<bool> IsPlayingChanged;

    public AnimationPreviewControl()
    {
        PlayPauseCommand = new GenericCommand(TogglePlayPause);
        DataContext = this;
        _backgroundColor = GetThemeDefaultBackgroundColor();
        InitializeComponent();

        _meshContext = new MeshRenderContext
        {
            BackgroundColor = _backgroundColor
        };
        SceneViewer.Context = _meshContext;
        SceneViewer.Loaded += SceneViewer_Loaded;

        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(object sender, bool isDarkMode)
    {
        BackgroundColor = GetThemeDefaultBackgroundColor();
    }

    private void SceneViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (_meshContext.IsReady && !_previewInitialized)
        {
            _meshContext.UpdateScene += OnUpdateScene;
            _meshContext.RenderScene += OnRenderScene;
            _previewInitialized = true;
            SceneViewer.SetShouldRender(true);
        }
    }

    #region Public API

    public void LoadSkeletalMesh(ExportEntry skeletalMeshExport)
    {
        if (!_meshContext.IsReady) return;

        // Dispose old preview
        _meshPreview?.Dispose();
        _meshPreview = null;
        _skinnedRenderer = null;
        var oldIsPlaying = _animPlayer?.IsPlaying ?? false;
        var oldTime = AnimSliderValue;
        _animPlayer = null;
        _skm = null;

        try
        {
            var skm = ObjectBinary.From<SkeletalMesh>(skeletalMeshExport);

            if (skm.LODModels.Length is 0)
            {
                throw new Exception("Mesh has no LODs!");
            }
            _meshPreview = new ModelPreview<WorldVertex>(_meshContext, skm);

            _skinnedRenderer = new SkinnedMeshRenderer();
            _skinnedRenderer.BuildFromSkeletalMesh(skeletalMeshExport.FileRef.Game, skm.LODModels[0]);

            _skm = skm;

            Mesh<WorldVertex> mesh = _meshPreview.LODs[0].Mesh;
            // Center camera on mesh
            _meshContext.Camera.FocusDepth = mesh.TransformedBounds.SphereRadius * 1.75f;
            _meshContext.Camera.Position = mesh.TransformedBounds.Origin;
            _meshContext.Camera.Pitch = -MathF.PI / 7.0f;

            // Reload last animation if any
            if (_lastAnimExport != null)
            {
                LoadAnimSequence(_lastAnimExport);
                // set to the same frame as before
                AnimSliderValue = oldTime;
                // keep playing if it was playing before
                if (oldIsPlaying)
                {
                    Play();
                }
            }
            else if (_lastFxLine != null)
            {
                LoadFaceFxAnimation(_lastFxActor, _lastFxAnimSet, _lastFxLine);
            }
        }
        catch (Exception ex)
        {
            _meshContext.ErrorText = $"Error loading mesh: {ex.Message}";
        }
    }

    public void LoadAnimSequence(ExportEntry animSequenceExport)
    {
        bool resume = false;
        if (_animPlayer?.IsPlaying is true)
        {
            resume = true;
            Pause();
        }
        _lastAnimExport = animSequenceExport;
        _lastFxActor = null;
        _lastFxAnimSet = null;
        _lastFxLine = null;
        if (_skm == null) return;

        try
        {
            var animSequence = ObjectBinary.From<AnimSequence>(animSequenceExport);
            animSequence.DecompressAnimationData();

            // Create or reuse AnimSequencePlayer
            if (_animPlayer is not AnimSequencePlayer animSeqPlayer)
                _animPlayer = animSeqPlayer = new AnimSequencePlayer(_skm) { PlaybackSpeed = (float)PlaybackSpeed };

            animSeqPlayer.SetAnimation(animSequence);

            IsTimeMode = false;
            AnimSliderMin = 0;
            AnimSliderMax = Math.Max(0, animSeqPlayer.TotalFrames - 1);
            _animSliderValue = 0;
            OnPropertyChanged(nameof(AnimSliderValue));
            OnPropertyChanged(nameof(AnimPositionText));

            if (resume)
            {
                Play();
            }
            else
            {
                UpdateSkinningOneShot();
            }
        }
        catch (Exception)
        {
            // Animation might not be compatible with this skeleton
        }
    }

    /// <summary>
    /// Loads a FaceFx animation line for playback. The slider will be time-based (in seconds),
    /// with minimum at <see cref="FaceFxPlayer.StartTime"/> (which may be negative).
    /// When <paramref name="animSet"/> is null the line is expected to come from <paramref name="fxActor"/> directly
    /// </summary>
    public void LoadFaceFxAnimation(FaceFXAsset fxActor, FaceFXAnimSet animSet, FaceFXLine line)
    {
        if(_animPlayer?.IsPlaying is true)
        {
            Pause();
        }
        _lastAnimExport = null;
        _lastFxActor = fxActor;
        _lastFxAnimSet = animSet;
        _lastFxLine = line;
        if (_skm == null) return;

        try
        {
            // Create or reuse FaceFxPlayer
            if (_animPlayer is not FaceFxPlayer faceFxPlayer)
                _animPlayer = faceFxPlayer = new FaceFxPlayer(_skm) { PlaybackSpeed = (float)PlaybackSpeed };

            faceFxPlayer.FxActor = fxActor;
            faceFxPlayer.AnimSet = animSet;
            faceFxPlayer.SetFaceFXLine(line);

            IsTimeMode = true;
            AnimSliderMin = faceFxPlayer.StartTime;
            AnimSliderMax = faceFxPlayer.EndTime;
            _animSliderValue = faceFxPlayer.StartTime;
            OnPropertyChanged(nameof(AnimSliderValue));
            OnPropertyChanged(nameof(AnimPositionText));

            UpdateSkinningOneShot();
        }
        catch (Exception ex)
        {
            _meshContext.ErrorText = $"Error loading FaceFx animation: {ex.Message}";
        }
    }

    public void ClearAnimation()
    {
        _lastAnimExport = null;
        _lastFxActor = null;
        _lastFxAnimSet = null;
        _lastFxLine = null;
        if (_animPlayer is AnimSequencePlayer asp)
            asp.SetAnimation(null);
        else if (_animPlayer is FaceFxPlayer ffp)
            ffp.SetFaceFXLine(null);
        IsTimeMode = false;
        AnimSliderMin = 0;
        AnimSliderMax = 0;
        _animSliderValue = 0;
        OnPropertyChanged(nameof(AnimSliderValue));
        OnPropertyChanged(nameof(AnimPositionText));
    }

    public void Clear()
    {
        _lastAnimExport = null;
        _lastFxActor = null;
        _lastFxAnimSet = null;
        _lastFxLine = null;
        _meshPreview?.Dispose();
        _meshPreview = null;
        _skinnedRenderer = null;
        _animPlayer = null;
        _skm = null;
        IsTimeMode = false;
        AnimSliderMin = 0;
        AnimSliderMax = 0;
        _animSliderValue = 0;
        OnPropertyChanged(nameof(AnimSliderValue));
        OnPropertyChanged(nameof(AnimPositionText));
    }

    public void TogglePlayPause()
    {
        if (_animPlayer == null) return;
        _animPlayer.IsPlaying = !_animPlayer.IsPlaying;
        if (_animPlayer.IsPlaying)
            SceneViewer.MarkRenderDirty();
        OnPropertyChanged(nameof(PlayPauseIcon));
        IsPlayingChanged?.Invoke(_animPlayer.IsPlaying);
    }

    public void Play()
    {
        if (_animPlayer == null) return;
        _animPlayer.IsPlaying = true;
        SceneViewer.MarkRenderDirty();
        OnPropertyChanged(nameof(PlayPauseIcon));
        IsPlayingChanged?.Invoke(true);
    }

    public void Pause()
    {
        if (_animPlayer == null) return;
        _animPlayer.IsPlaying = false;
        OnPropertyChanged(nameof(PlayPauseIcon));
        IsPlayingChanged?.Invoke(false);
    }

    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        if (_previewInitialized)
        {
            _meshContext.UpdateScene -= OnUpdateScene;
            _meshContext.RenderScene -= OnRenderScene;
        }
        _meshPreview?.Dispose();
        SceneViewer?.Dispose();
    }

    #endregion

    #region Internal Rendering

    private void UpdateSkinningOneShot()
    {
        if (_skinnedRenderer == null || _meshPreview == null || _animPlayer == null) return;
        if (!_meshContext.IsReady || _meshPreview.LODs.Count == 0) return;

        _skinnedRenderer.UpdateSkinning(_meshContext.ImmediateContext, _meshPreview.LODs[0].Mesh, _animPlayer);
    }

    private void OnUpdateScene(object sender, float timestep)
    {
        bool isPlaying = _animPlayer is { IsPlaying: true };
        if (_skinnedRenderer != null && _meshPreview is { LODs.Count: > 0 })
        {
            if (isPlaying)
            {
                var oldTime = _animPlayer.CurrentTime;
                _animPlayer.AdvanceTime(timestep);
                if (_animPlayer.CurrentTime < oldTime)
                {
                    IsPlayingChanged?.Invoke(true);
                }
            }

            //FaceFx must be re-calced every frame, even when not playing, so that preview works properly in the editor
            if (isPlaying || _animPlayer is FaceFxPlayer)
            {
                _skinnedRenderer.UpdateSkinning(_meshContext.ImmediateContext, _meshPreview.LODs[0].Mesh, _animPlayer);
            }

            if (isPlaying)
            {
                // Mirror back to slider without triggering setter logic
                _animSliderValue = IsTimeMode
                ? _animPlayer.CurrentTime
                : ((AnimSequencePlayer)_animPlayer).CurrentFrame;
                OnPropertyChanged(nameof(AnimSliderValue));
                OnPropertyChanged(nameof(AnimPositionText));

                if (IsTimeMode)
                    AnimTimeChanged?.Invoke(_animPlayer.CurrentTime);
            }

            if (isPlaying)
            {
                SceneViewer.MarkRenderDirty();
            }
        }
    }

    private void OnRenderScene(object sender, EventArgs e)
    {
        if (_meshPreview is not { LODs.Count: > 0 })
        {
            return;
        }
        if (RemoveOffset)
        {
            _meshContext.Camera.Position = _meshPreview.LODs[0].Mesh.TransformedBounds.Origin;
        }
        foreach (RenderPass renderPass in Enum.GetValues<RenderPass>())
        {
            _meshPreview.Render(renderPass, _meshContext, 0);
        }
    }

    #endregion
}
