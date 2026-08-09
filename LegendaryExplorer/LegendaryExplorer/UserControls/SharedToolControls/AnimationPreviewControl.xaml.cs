using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FontAwesome5;
using LegendaryExplorer.Misc;
using LegendaryExplorer.Misc.AppSettings;
using LegendaryExplorer.SharedUI;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.UserControls.Interfaces;
using LegendaryExplorer.UserControls.ExportLoaderControls.TextureViewer;
using LegendaryExplorerCore.Gammtek;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using SkeletalMesh = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;
using LegacyScene3D = LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;

namespace LegendaryExplorer.UserControls.SharedToolControls;

/// <summary>
/// Self-contained animation preview control with 3D viewport, CPU skinning, and playback controls.
/// Supports both AnimSequence (frame-based) and FaceFxAnimSet (time-based) playback.
/// </summary>
public partial class AnimationPreviewControl : NotifyPropertyChangedControlBase, ISceneRenderContextConfigurable
{
    private sealed class PreviewMeshComponent : IDisposable
    {
        public LegacyScene3D.ModelPreview<LegacyScene3D.WorldVertex> Preview { get; init; }
        public LegacySkinnedMeshRenderer Renderer { get; init; }

        public void Dispose() => Preview?.Dispose();
    }

    public sealed class AnimationTimelineClip
    {
        public ExportEntry AnimationExport { get; init; }
        public float StartTime { get; init; }
        public float EndTime { get; init; }
        public float AnimationStartTime { get; init; }
        public float AnimationEndTime { get; init; }
        public float PlayRate { get; init; } = 1f;
        public float BlendInDuration { get; init; }
        public float BlendOutDuration { get; init; }
        public float Weight { get; init; } = 1f;
        public bool Loop { get; init; }
    }

    private readonly PackageCache _packageCache = new();
    private LegacyScene3D.MeshRenderContext _meshContext;
    private LegacyScene3D.ModelPreview<LegacyScene3D.WorldVertex> _meshPreview;
    private LegacySkinnedMeshRenderer _skinnedRenderer;
    private AnimPlayer _animPlayer;
    private SkeletalMesh _skm;
    private readonly Dictionary<PreviewActorModelComponent, PreviewMeshComponent> _additionalMeshComponents = [];
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
                    _meshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.AlphaAsBlack;
                else
                    _meshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.AlphaAsBlack;
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
                    _meshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.EnableRedChannel;
                else
                    _meshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.EnableRedChannel;
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
                    _meshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.EnableGreenChannel;
                else
                    _meshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.EnableGreenChannel;
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
                    _meshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.EnableBlueChannel;
                else
                    _meshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.EnableBlueChannel;
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
                    _meshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.EnableAlphaChannel;
                else
                    _meshContext.CurrentTextureViewFlags &= ~TextureRenderContext.TextureViewFlags.EnableAlphaChannel;
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
            ? ThemeManager.DarkCanvasMediaColor
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

    /// <summary>
    /// Fires once when a non-looping animation reaches its end.
    /// </summary>
    public event Action AnimationCompleted;

    private bool _animEndFired;

    public AnimationPreviewControl()
    {
        PlayPauseCommand = new GenericCommand(TogglePlayPause);
        DataContext = this;
        _backgroundColor = GetThemeDefaultBackgroundColor();
        InitializeComponent();

        _meshContext = new LegacyScene3D.MeshRenderContext
        {
            BackgroundColor = _backgroundColor
        };
        _meshContext.CurrentTextureViewFlags |= TextureRenderContext.TextureViewFlags.AlphaAsBlack;
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
        LoadSkeletalMesh(PreviewActorModelComponent.Body, skeletalMeshExport);
    }

    public void LoadSkeletalMesh(PreviewActorModelComponent component, ExportEntry skeletalMeshExport)
    {
        if (!_meshContext.IsReady) return;

        if (component is not PreviewActorModelComponent.Body)
        {
            LoadAdditionalSkeletalMesh(component, skeletalMeshExport);
            return;
        }

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
            _meshPreview = new LegacyScene3D.ModelPreview<LegacyScene3D.WorldVertex>(_meshContext.Device, skm, _meshContext.TextureCache, _packageCache);

            _skinnedRenderer = new LegacySkinnedMeshRenderer();
            _skinnedRenderer.BuildFromSkeletalMesh(skeletalMeshExport.FileRef.Game, skm.LODModels[0]);

            _skm = skm;

            var mesh = _meshPreview.LODs[0].Mesh;
            // Center camera on mesh
            _meshContext.Camera.FocusDepth = mesh.AABBHalfSize.Length() * 1.75f;
            _meshContext.Camera.Position = mesh.AABBCenter;
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

    private void LoadAdditionalSkeletalMesh(PreviewActorModelComponent component, ExportEntry skeletalMeshExport)
    {
        try
        {
            var skeletalMesh = ObjectBinary.From<SkeletalMesh>(skeletalMeshExport);
            if (skeletalMesh.LODModels.Length is 0)
            {
                throw new Exception("Mesh has no LODs!");
            }

            var preview = new LegacyScene3D.ModelPreview<LegacyScene3D.WorldVertex>(_meshContext.Device,
                skeletalMesh, _meshContext.TextureCache, _packageCache);
            var renderer = new LegacySkinnedMeshRenderer();
            renderer.BuildFromSkeletalMesh(skeletalMeshExport.FileRef.Game, skeletalMesh.LODModels[0],
                skeletalMesh.RefSkeleton, _skm?.RefSkeleton);

            if (_additionalMeshComponents.Remove(component, out PreviewMeshComponent previousComponent))
            {
                previousComponent.Dispose();
            }
            _additionalMeshComponents[component] = new PreviewMeshComponent
            {
                Preview = preview,
                Renderer = renderer
            };
            UpdateSkinningOneShot();
            _meshContext.ErrorText = null;
        }
        catch (Exception ex)
        {
            _meshContext.ErrorText = $"Error loading {component.ToString().ToLowerInvariant()} mesh: {ex.Message}";
        }
    }

    public void ClearSkeletalMesh(PreviewActorModelComponent component)
    {
        if (component is PreviewActorModelComponent.Body)
        {
            Clear();
            return;
        }

        if (_additionalMeshComponents.Remove(component, out PreviewMeshComponent meshComponent))
        {
            meshComponent.Dispose();
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

            animSeqPlayer.SetAnimation(animSequence, _packageCache);
            _meshContext.ErrorText = null;

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
        catch (Exception ex)
        {
            _meshContext.ErrorText = $"Error loading animation: {ex.Message}";
        }
    }

    public void LoadAnimSequenceTimeline(IEnumerable<AnimationTimelineClip> clips)
    {
        if (_animPlayer?.IsPlaying is true)
        {
            Pause();
        }
        _lastAnimExport = null;
        _lastFxActor = null;
        _lastFxAnimSet = null;
        _lastFxLine = null;
        _animEndFired = false;
        if (_skm == null) return;

        try
        {
            if (_animPlayer is not AnimSequencePlayer animSeqPlayer)
            {
                _animPlayer = animSeqPlayer = new AnimSequencePlayer(_skm) { PlaybackSpeed = (float)PlaybackSpeed };
            }

            var scheduledClips = new List<AnimSequencePlayer.ScheduledAnimationClip>();
            foreach (AnimationTimelineClip clip in clips)
            {
                if (clip.AnimationExport == null)
                {
                    continue;
                }

                var animation = ObjectBinary.From<AnimSequence>(clip.AnimationExport);
                animation.DecompressAnimationData();
                scheduledClips.Add(new AnimSequencePlayer.ScheduledAnimationClip
                {
                    Animation = animation,
                    StartTime = clip.StartTime,
                    EndTime = clip.EndTime,
                    AnimationStartTime = clip.AnimationStartTime,
                    AnimationEndTime = clip.AnimationEndTime,
                    PlayRate = clip.PlayRate,
                    BlendInDuration = clip.BlendInDuration,
                    BlendOutDuration = clip.BlendOutDuration,
                    Weight = clip.Weight,
                    Loop = clip.Loop,
                });
            }

            animSeqPlayer.SetAnimationTimeline(scheduledClips, _packageCache);
            animSeqPlayer.IsLooping = false;
            _meshContext.ErrorText = null;

            IsTimeMode = true;
            AnimSliderMin = animSeqPlayer.StartTime;
            AnimSliderMax = animSeqPlayer.EndTime;
            _animSliderValue = animSeqPlayer.StartTime;
            OnPropertyChanged(nameof(AnimSliderValue));
            OnPropertyChanged(nameof(AnimPositionText));
            UpdateSkinningOneShot();
        }
        catch (Exception ex)
        {
            _meshContext.ErrorText = $"Error loading animation timeline: {ex.Message}";
        }
    }

    /// <summary>
    /// Loads an AnimSequence for single-shot (non-looping) playback.
    /// When the animation reaches its end, <see cref="AnimationCompleted"/> will fire.
    /// </summary>
    public void LoadAnimSequenceNonLooping(ExportEntry animSequenceExport)
    {
        LoadAnimSequence(animSequenceExport);
        if (_animPlayer != null)
        {
            _animPlayer.IsLooping = false;
            _animEndFired = false;
        }
    }

    /// <summary>
    /// Crossfades from the current animation pose to a new AnimSequence over <paramref name="blendDuration"/> seconds.
    /// The new animation plays in non-looping mode; <see cref="AnimationCompleted"/> fires when it reaches its end.
    /// </summary>
    public void CrossfadeToAnimSequence(ExportEntry animSequenceExport, float blendDuration)
    {
        _lastAnimExport = animSequenceExport;
        _lastFxActor = null;
        _lastFxAnimSet = null;
        _lastFxLine = null;
        _animEndFired = false;
        if (_skm == null) return;

        try
        {
            var animSequence = ObjectBinary.From<AnimSequence>(animSequenceExport);
            animSequence.DecompressAnimationData();

            if (_animPlayer is not AnimSequencePlayer animSeqPlayer)
                _animPlayer = animSeqPlayer = new AnimSequencePlayer(_skm) { PlaybackSpeed = (float)PlaybackSpeed };

            animSeqPlayer.CrossfadeTo(animSequence, blendDuration, _packageCache);
            animSeqPlayer.IsLooping = false;
            _meshContext.ErrorText = null;

            IsTimeMode = false;
            AnimSliderMin = 0;
            AnimSliderMax = Math.Max(0, animSeqPlayer.TotalFrames - 1);
            _animSliderValue = 0;
            OnPropertyChanged(nameof(AnimSliderValue));
            OnPropertyChanged(nameof(AnimPositionText));

            if (!animSeqPlayer.IsPlaying)
            {
                Play();
            }
        }
        catch (Exception ex)
        {
            _meshContext.ErrorText = $"Error loading animation: {ex.Message}";
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
        _animEndFired = false;
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
        _animEndFired = false;
        _meshPreview?.Dispose();
        _meshPreview = null;
        foreach (PreviewMeshComponent meshComponent in _additionalMeshComponents.Values)
        {
            meshComponent.Dispose();
        }
        _additionalMeshComponents.Clear();
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
        OnPropertyChanged(nameof(PlayPauseIcon));
        IsPlayingChanged?.Invoke(_animPlayer.IsPlaying);
    }

    public void Play()
    {
        if (_animPlayer == null) return;
        _animPlayer.IsPlaying = true;
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
        foreach (PreviewMeshComponent meshComponent in _additionalMeshComponents.Values)
        {
            meshComponent.Dispose();
        }
        _additionalMeshComponents.Clear();
        _packageCache.Dispose();
        SceneViewer?.Dispose();
    }

    #endregion

    #region Internal Rendering

    private void UpdateSkinningOneShot()
    {
        if (_skinnedRenderer == null || _meshPreview == null || _animPlayer == null) return;
        if (!_meshContext.IsReady || _meshPreview.LODs.Count == 0) return;

        _skinnedRenderer.UpdateSkinning(_meshContext.ImmediateContext, _meshPreview.LODs[0].Mesh, _animPlayer);
        foreach (PreviewMeshComponent meshComponent in _additionalMeshComponents.Values)
        {
            if (meshComponent.Preview is { LODs.Count: > 0 })
            {
                meshComponent.Renderer.UpdateSkinning(_meshContext.ImmediateContext,
                    meshComponent.Preview.LODs[0].Mesh, _animPlayer);
            }
        }
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

                // Detect non-looping animation reaching its end
                if (!_animPlayer.IsLooping && !_animEndFired
                    && _animPlayer.CurrentTime >= _animPlayer.EndTime)
                {
                    _animEndFired = true;
                    AnimationCompleted?.Invoke();
                }
            }

            //FaceFx must be re-calced every frame, even when not playing, so that preview works properly in the editor
            if (isPlaying || _animPlayer is FaceFxPlayer)
            {
                _skinnedRenderer.UpdateSkinning(_meshContext.ImmediateContext, _meshPreview.LODs[0].Mesh, _animPlayer);
                foreach (PreviewMeshComponent meshComponent in _additionalMeshComponents.Values)
                {
                    if (meshComponent.Preview is { LODs.Count: > 0 })
                    {
                        meshComponent.Renderer.UpdateSkinning(_meshContext.ImmediateContext,
                            meshComponent.Preview.LODs[0].Mesh, _animPlayer);
                    }
                }
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
            _meshContext.Camera.Position = _meshPreview.LODs[0].Mesh.AABBCenter;
        }
        foreach (LegacyScene3D.RenderPass renderPass in Enum.GetValues<LegacyScene3D.RenderPass>())
        {
            _meshPreview.Render(renderPass, _meshContext, 0, Matrix4x4.Identity);
            foreach (PreviewMeshComponent meshComponent in _additionalMeshComponents.Values)
            {
                meshComponent.Preview.Render(renderPass, _meshContext, 0, Matrix4x4.Identity);
            }
        }
    }

    #endregion
}

internal sealed class LegacySkinnedMeshRenderer
{
    private SkinVertex[] _skinVertices;

    private struct SkinVertex
    {
        public Vector3 BindPosition;
        public Vector3 BindNormal;
        public Vector2 UV;
        public int Bone0;
        public int Bone1;
        public int Bone2;
        public int Bone3;
        public float Weight0;
        public float Weight1;
        public float Weight2;
        public float Weight3;
    }

    public void BuildFromSkeletalMesh(MEGame game, StaticLODModel lodModel)
        => BuildFromSkeletalMesh(game, lodModel, null, null);

    public void BuildFromSkeletalMesh(MEGame game, StaticLODModel lodModel, MeshBone[] sourceSkeleton,
        MeshBone[] animationSkeleton)
    {
        bool isME1 = game == MEGame.ME1;
        int vertexCount = isME1 ? lodModel.ME1VertexBufferGPUSkin.Length : (int)lodModel.NumVertices;
        _skinVertices = new SkinVertex[vertexCount];
        int[] boneMap = BuildAnimationBoneMap(sourceSkeleton, animationSkeleton);

        if (isME1)
        {
            for (int v = 0; v < vertexCount; v++)
            {
                var sv = lodModel.ME1VertexBufferGPUSkin[v];
                var chunk = FindChunkForVertex(lodModel, v);
                ref var skinVert = ref _skinVertices[v];
                skinVert.BindPosition = sv.Position;
                skinVert.BindNormal = (Vector3)sv.TangentZ;
                skinVert.UV = sv.UV;
                ResolveInfluences(ref skinVert, sv.InfluenceBones, sv.InfluenceWeights, chunk, boneMap);
            }
        }
        else
        {
            for (int v = 0; v < vertexCount; v++)
            {
                var gv = lodModel.VertexBufferGPUSkin.VertexData[v];
                var chunk = FindChunkForVertex(lodModel, v);
                ref var skinVert = ref _skinVertices[v];
                skinVert.BindPosition = gv.Position;
                skinVert.BindNormal = (Vector3)gv.TangentZ;
                skinVert.UV = gv.UV;
                ResolveInfluences(ref skinVert, gv.InfluenceBones, gv.InfluenceWeights, chunk, boneMap);
            }
        }
    }

    private static SkelMeshChunk FindChunkForVertex(StaticLODModel lodModel, int vertexIndex)
    {
        foreach (var chunk in lodModel.Chunks)
        {
            int chunkStart = (int)chunk.BaseVertexIndex;
            int chunkEnd = chunkStart + chunk.NumRigidVertices + chunk.NumSoftVertices;
            if (vertexIndex >= chunkStart && vertexIndex < chunkEnd)
                return chunk;
        }

        return lodModel.Chunks[0];
    }

    private static void ResolveInfluences(ref SkinVertex skinVert, Influences bones, Influences weights,
        SkelMeshChunk chunk, int[] boneMap)
    {
        skinVert.Bone0 = ResolveBoneIndex(bones[0], chunk, boneMap);
        skinVert.Bone1 = ResolveBoneIndex(bones[1], chunk, boneMap);
        skinVert.Bone2 = ResolveBoneIndex(bones[2], chunk, boneMap);
        skinVert.Bone3 = ResolveBoneIndex(bones[3], chunk, boneMap);

        float w0 = weights[0] / 255f;
        float w1 = weights[1] / 255f;
        float w2 = weights[2] / 255f;
        float w3 = weights[3] / 255f;
        float total = w0 + w1 + w2 + w3;
        if (total > 0)
        {
            skinVert.Weight0 = w0 / total;
            skinVert.Weight1 = w1 / total;
            skinVert.Weight2 = w2 / total;
            skinVert.Weight3 = w3 / total;
        }
        else
        {
            skinVert.Weight0 = 1f;
            skinVert.Weight1 = 0f;
            skinVert.Weight2 = 0f;
            skinVert.Weight3 = 0f;
        }
    }

    private static int ResolveBoneIndex(byte influenceBone, SkelMeshChunk chunk, int[] boneMap)
    {
        int sourceIndex = influenceBone < chunk.BoneMap.Length ? chunk.BoneMap[influenceBone] : 0;
        return boneMap is not null && sourceIndex < boneMap.Length ? boneMap[sourceIndex] : sourceIndex;
    }

    private static int[] BuildAnimationBoneMap(MeshBone[] sourceSkeleton, MeshBone[] animationSkeleton)
    {
        if (sourceSkeleton is null || animationSkeleton is null || ReferenceEquals(sourceSkeleton, animationSkeleton))
        {
            return null;
        }

        Dictionary<string, int> animationBones = animationSkeleton
            .Select((bone, index) => (Name: bone.Name.Name, Index: index))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
        int[] map = new int[sourceSkeleton.Length];
        for (int sourceIndex = 0; sourceIndex < sourceSkeleton.Length; sourceIndex++)
        {
            int candidate = sourceIndex;
            while (candidate >= 0 && candidate < sourceSkeleton.Length
                   && !animationBones.TryGetValue(sourceSkeleton[candidate].Name.Name, out map[sourceIndex]))
            {
                int parent = sourceSkeleton[candidate].ParentIndex;
                candidate = parent == candidate ? -1 : parent;
            }
        }
        return map;
    }

    public void UpdateSkinning(SharpDX.Direct3D11.DeviceContext context, LegacyScene3D.Mesh<LegacyScene3D.WorldVertex> mesh, AnimPlayer animPlayer)
    {
        if (_skinVertices == null || mesh == null)
            return;

        var skinningMatrices = animPlayer.ComputeSkinningMatrices();
        if (skinningMatrices == null)
            return;

        int vertexCount = Math.Min(_skinVertices.Length, mesh.Vertices.Count);
        for (int i = 0; i < vertexCount; i++)
        {
            ref var sv = ref _skinVertices[i];

            var blended = BlendMatrix(
                skinningMatrices, sv.Bone0, sv.Weight0,
                sv.Bone1, sv.Weight1,
                sv.Bone2, sv.Weight2,
                sv.Bone3, sv.Weight3);

            var skinnedPos = Vector3.Transform(sv.BindPosition, blended);
            var skinnedNormal = Vector3.TransformNormal(sv.BindNormal, blended);

            mesh.Vertices[i] = new LegacyScene3D.WorldVertex(
                new Vector3(-skinnedPos.X, skinnedPos.Z, skinnedPos.Y),
                new Vector3(-skinnedNormal.X, skinnedNormal.Z, skinnedNormal.Y),
                sv.UV);
        }

        mesh.RebuildBuffer(context.Device);
    }

    private static Matrix4x4 BlendMatrix(Matrix4x4[] matrices, int b0, float w0, int b1, float w1, int b2, float w2, int b3, float w3)
    {
        var m = matrices[b0 < matrices.Length ? b0 : 0] * w0;
        if (w1 > 0 && b1 < matrices.Length) m += matrices[b1] * w1;
        if (w2 > 0 && b2 < matrices.Length) m += matrices[b2] * w2;
        if (w3 > 0 && b3 < matrices.Length) m += matrices[b3] * w3;
        return m;
    }
}
