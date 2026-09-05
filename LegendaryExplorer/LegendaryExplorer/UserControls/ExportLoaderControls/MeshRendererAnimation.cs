using System;
using System.Linq;
using System.Numerics;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class MeshRenderer
{
    private SkeletalMesh _animationMesh;
    private AnimSequence _previewAnimation;
    private AnimSequencePlayer _previewAnimationPlayer;
    private LegacySkinnedMeshRenderer[] _animationRenderers = [];
    private bool _animationPoseDirty;
    private bool _animationPlaying;
    private double _animationPosition;

    public bool HasPreviewAnimation => _previewAnimationPlayer?.HasAnimation == true;
    public bool IsPreviewAnimationPlaying => _animationPlaying && HasPreviewAnimation;
    public double AnimationDuration => _previewAnimationPlayer?.Duration ?? 0;
    public string AnimationPlayPauseText => _animationPlaying ? "Pause" : "Play";
    public string AnimationPositionText => $"{AnimationPosition:F2} / {AnimationDuration:F2} s";

    private string _animationPreviewStatus;
    public string AnimationPreviewStatus
    {
        get => _animationPreviewStatus;
        private set => SetProperty(ref _animationPreviewStatus, value);
    }

    public double AnimationPosition
    {
        get => _animationPosition;
        set
        {
            if (!double.IsFinite(value) || !HasPreviewAnimation) return;
            if (SetProperty(ref _animationPosition, Math.Clamp(value, 0, AnimationDuration)))
            {
                _previewAnimationPlayer.CurrentTime = (float)_animationPosition;
                _animationPoseDirty = true;
                OnPropertyChanged(nameof(AnimationPositionText));
            }
        }
    }

    /// <summary>Applies an animation to the preview only. The caller owns its source package.</summary>
    public void SetPreviewAnimation(AnimSequence animation)
    {
        _previewAnimation = animation;
        _animationPosition = 0;
        _animationPlaying = animation != null;
        InitializeAnimationPlayer();
    }

    public void TogglePreviewAnimationPlayback()
    {
        if (!HasPreviewAnimation) return;
        _animationPlaying = !_animationPlaying;
        OnPropertyChanged(nameof(AnimationPlayPauseText));
    }

    public void PausePreviewAnimation()
    {
        _animationPlaying = false;
        OnPropertyChanged(nameof(AnimationPlayPauseText));
    }

    private void InitializeAnimationPreview(SkeletalMesh mesh)
    {
        _animationMesh = mesh;
        InitializeAnimationPlayer();
    }

    private void InitializeAnimationPlayer()
    {
        _previewAnimationPlayer = null;
        AnimationPreviewStatus = null;
        try
        {
            if (_animationMesh?.RefSkeleton is { Length: > 0 }
                && (_previewAnimation != null || _animationRenderers.Length > 0))
            {
                var player = new AnimSequencePlayer(_animationMesh);
                // Resolve track names and translation rules while the source package is retained.
                using var cache = new PackageCache();
                player.SetAnimation(_previewAnimation, cache, animationDataIsPrepared: _previewAnimation?.RawAnimationData != null);
                if (_previewAnimation != null && !_animationMesh.RefSkeleton.Any(bone =>
                        _previewAnimation.Bones.Contains(bone.Name.Instanced, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("This animation has no bones in common with the selected mesh.");
                player.IsLooping = true;
                player.CurrentTime = (float)Math.Clamp(_animationPosition, 0, player.Duration);
                if (_animationRenderers.Length == 0)
                {
                    _animationRenderers = _animationMesh.LODModels.Select(lod =>
                    {
                        var renderer = new LegacySkinnedMeshRenderer();
                        renderer.BuildFromSkeletalMesh(CurrentLoadedExport.Game, lod);
                        return renderer;
                    }).ToArray();
                }
                _previewAnimationPlayer = player;
                // Reset every previously animated LOD when returning to the reference pose.
                for (int lod = 0; lod < _animationRenderers.Length; lod++)
                    UpdateAnimationLod(lod);
                UpdateAnimationSkeleton();
            }
        }
        catch (Exception ex)
        {
            _previewAnimationPlayer = null;
            _animationPlaying = false;
            AnimationPreviewStatus = $"Could not play animation: {ex.Message}";
        }
        NotifyAnimationPlayback();
    }

    private void UpdatePreviewAnimation(float timeStep)
    {
        if (_previewAnimationPlayer == null) return;
        if (_animationPlaying && HasPreviewAnimation)
        {
            _previewAnimationPlayer.AdvanceTime(timeStep);
            _animationPosition = _previewAnimationPlayer.CurrentTime;
            OnPropertyChanged(nameof(AnimationPosition));
            OnPropertyChanged(nameof(AnimationPositionText));
            _animationPoseDirty = true;
        }
        if (_animationPoseDirty)
        {
            try
            {
                UpdateAnimationLod(CurrentLOD);
                UpdateAnimationSkeleton();
            }
            catch (Exception ex)
            {
                PausePreviewAnimation();
                AnimationPreviewStatus = $"Could not update animation: {ex.Message}";
            }
            _animationPoseDirty = false;
        }
    }

    private void UpdateAnimationLod(int lod)
    {
        if (_previewAnimationPlayer == null || !MeshContext.IsReady
            || lod < 0 || lod >= _animationRenderers.Length) return;
        var renderer = _animationRenderers[lod];
        if (LEXPreview != null && lod < LEXPreview.LODs.Count)
            renderer.UpdateSkinning(MeshContext.ImmediateContext, LEXPreview.LODs[lod].Mesh, _previewAnimationPlayer);
        if (GameShaderPreview != null && lod < GameShaderPreview.LODs.Count)
            renderer.UpdateSkinning(MeshContext.ImmediateContext, GameShaderPreview.LODs[lod].Mesh,
                _previewAnimationPlayer, CurrentLoadedExport.Game);
    }

    private void UpdateAnimationSkeleton()
    {
        if (!ShowSkeleton || _previewAnimationPlayer == null || !MeshContext.IsReady) return;
        _previewAnimationPlayer.ComputeSkinningMatrices();
        Vector3[] positions = _previewAnimationPlayer.BoneComponentSpaceTransforms
            .Select(transform => new Vector3(-transform.M41, transform.M43, transform.M42)).ToArray();
        BuildSkeletonLineBuffer(_animationMesh, positions);
    }

    private void UnloadAnimationPreview()
    {
        _animationMesh = null;
        _previewAnimationPlayer = null;
        _animationRenderers = [];
        _animationPoseDirty = false;
        NotifyAnimationPlayback();
    }

    private void NotifyAnimationPlayback()
    {
        OnPropertyChanged(nameof(HasPreviewAnimation));
        OnPropertyChanged(nameof(AnimationDuration));
        OnPropertyChanged(nameof(AnimationPosition));
        OnPropertyChanged(nameof(AnimationPositionText));
        OnPropertyChanged(nameof(AnimationPlayPauseText));
    }
}
