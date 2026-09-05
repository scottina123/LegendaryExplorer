using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorer.Tools.LevelEditor;
using LegendaryExplorer.Tools.Meshplorer;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.Animation;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.UserControls.ExportLoaderControls;

public partial class ActorPreviewControl
{
    private MeshplorerAnimationCatalog _animationCatalog;
    private MeshplorerAnimationCatalog.Entry _selectedAnimationEntry;
    private MeshplorerAnimationCatalog.LoadedAnimation _loadedAnimation;
    private CancellationTokenSource _animationLoading;
    private SkeletalMeshComponentProxy[] _animationComponents = [];
    private readonly List<SkeletalMeshComponentProxy> _animatedComponents = [];
    private AnimSequencePlayer _previewAnimationPlayer;
    private bool _animationPlaying;
    private bool _animationDisposed;
    private bool _resumeAnimationAfterDrag;
    private double _animationPosition;

    public bool CanPreviewActorAnimations => ShowPreviewToolbar && _animationComponents.Length > 0
                                             && CurrentLoadedExport?.Game.IsMEGame() == true;
    public bool HasPreviewAnimation => _previewAnimationPlayer?.HasAnimation == true;
    public bool IsPreviewAnimationPlaying => _animationPlaying && HasPreviewAnimation;
    public double AnimationDuration => _previewAnimationPlayer?.Duration ?? 0;
    public string AnimationPlayPauseText => IsPreviewAnimationPlaying ? "Pause" : "Play";
    public string AnimationPositionText => $"{AnimationPosition:F2} / {AnimationDuration:F2} s";

    private string _selectedAnimationName = "No animation selected";
    public string SelectedAnimationName
    {
        get => _selectedAnimationName;
        private set => SetProperty(ref _selectedAnimationName, value);
    }

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
                ApplyPreviewAnimationPosition();
                OnPropertyChanged(nameof(AnimationPositionText));
                SceneViewer.MarkRenderDirty();
            }
        }
    }

    internal void InitializeActorAnimationPreview(ActorProxy actor)
    {
        _animationComponents = EnumerateActors(actor).SelectMany(item => item.Components)
            .OfType<SkeletalMeshComponentProxy>().Where(component => component.CanPreviewAnimation)
            .Distinct().ToArray();
        OnPropertyChanged(nameof(CanPreviewActorAnimations));
    }

    private async void ChooseActorAnimation_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPreviewActorAnimations) return;
        var requestedExport = CurrentLoadedExport;
        int loadVersion = _actorLoadVersion;
        _animationLoading?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _animationLoading = cancellation;
        MeshplorerAnimationCatalog.LoadedAnimation loaded = null;
        bool IsCurrentRequest() => !_animationDisposed && loadVersion == _actorLoadVersion
                                  && ReferenceEquals(requestedExport, CurrentLoadedExport)
                                  && ReferenceEquals(_animationLoading, cancellation);
        try
        {
            BusyText = $"Loading {requestedExport.Game} animation entries...";
            IsBusy = true;
            DateTime stamp = File.GetLastWriteTimeUtc(AssetDatabaseWindow.GetDBPath(requestedExport.Game));
            if (_animationCatalog?.Game != requestedExport.Game || _animationCatalog.DatabaseStamp != stamp)
            {
                var catalog = await MeshplorerAnimationCatalog.LoadAsync(requestedExport.Game, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrentRequest()) return;
                _animationCatalog = catalog;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRequest()) return;
            IsBusy = false;
            if (_animationCatalog.Entries.Count == 0)
            {
                AnimationPreviewStatus = $"No animation sequences in the {requestedExport.Game} Asset Database. Rebuild the database to add animations.";
                return;
            }
            var selected = EntrySelector.GetItem(Window.GetWindow(this), _animationCatalog.Entries,
                $"Choose an animation from the {requestedExport.Game} Asset Database to loop on the entire actor.",
                _selectedAnimationEntry, searchHelpText: "Search animation name, sequence name, or animation group");
            if (selected is null || !IsCurrentRequest()) return;
            BusyText = $"Loading {selected.Record.AnimSequence}...";
            IsBusy = true;
            loaded = await _animationCatalog.LoadAnimationAsync(selected, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRequest()) return;
            SetPreviewAnimation(loaded.Sequence);
            _loadedAnimation?.Dispose();
            _loadedAnimation = loaded;
            loaded = null;
            _selectedAnimationEntry = selected;
            SelectedAnimationName = selected.Record.AnimSequence;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (IsCurrentRequest())
            {
                _animationCatalog = null;
                AnimationPreviewStatus = exception.Message;
            }
        }
        finally
        {
            loaded?.Dispose();
            if (IsCurrentRequest())
            {
                _animationLoading = null;
                IsBusy = false;
                BusyText = null;
            }
        }
    }

    /// <summary>Applies an animation to every compatible skeletal component. The caller owns its package.</summary>
    internal void SetPreviewAnimation(AnimSequence animation)
    {
        _previewAnimationPlayer = null;
        _animatedComponents.Clear();
        _animationPosition = 0;
        _animationPlaying = false;
        AnimationPreviewStatus = null;
        try
        {
            if (animation is not null && (!float.IsFinite(animation.SequenceLength)
                || animation.SequenceLength < 0 || animation.NumFrames <= 0))
                throw new InvalidOperationException("The animation contains no playable frames.");
            foreach (SkeletalMeshComponentProxy component in _animationComponents)
            {
                if (component.SetPreviewAnimation(animation) is { } player)
                {
                    _previewAnimationPlayer ??= player;
                    _animatedComponents.Add(component);
                }
            }
            if (animation is not null && !HasPreviewAnimation)
                throw new InvalidOperationException("This animation has no bones in common with the actor's meshes.");
            _animationPlaying = HasPreviewAnimation;
            if (HasPreviewAnimation && _animatedComponents.Count < _animationComponents.Length)
                AnimationPreviewStatus = "Some meshes have no matching animation bones and remain in their reference pose.";
        }
        catch (Exception exception)
        {
            foreach (SkeletalMeshComponentProxy component in _animationComponents)
                component.SetPreviewAnimation(null);
            _previewAnimationPlayer = null;
            _animatedComponents.Clear();
            AnimationPreviewStatus = $"Could not play animation: {exception.Message}";
        }
        NotifyAnimationPlayback();
    }

    internal void UpdatePreviewAnimation(float deltaTime)
    {
        if (!IsPreviewAnimationPlaying || !float.IsFinite(deltaTime) || deltaTime < 0) return;
        _previewAnimationPlayer.AdvanceTime(deltaTime);
        _animationPosition = _previewAnimationPlayer.CurrentTime;
        ApplyPreviewAnimationPosition();
        OnPropertyChanged(nameof(AnimationPosition));
        OnPropertyChanged(nameof(AnimationPositionText));
    }

    private void ApplyPreviewAnimationPosition()
    {
        // Each mesh keeps its own skeleton/morph mapping, but samples exactly the same instant.
        foreach (SkeletalMeshComponentProxy component in _animatedComponents)
            component.SetPreviewAnimationTime((float)_animationPosition);
    }

    internal void TogglePreviewAnimationPlayback()
    {
        if (!HasPreviewAnimation) return;
        _animationPlaying = !_animationPlaying;
        NotifyAnimationPlayback();
    }

    internal void PausePreviewAnimation()
    {
        _animationPlaying = false;
        NotifyAnimationPlayback();
    }

    private void NotifyAnimationPlayback()
    {
        RenderContext.ForceContinuousRendering = IsPreviewAnimationPlaying && AnimationDuration > 0;
        OnPropertyChanged(nameof(HasPreviewAnimation));
        OnPropertyChanged(nameof(IsPreviewAnimationPlaying));
        OnPropertyChanged(nameof(AnimationDuration));
        OnPropertyChanged(nameof(AnimationPosition));
        OnPropertyChanged(nameof(AnimationPositionText));
        OnPropertyChanged(nameof(AnimationPlayPauseText));
        SceneViewer?.MarkRenderDirty();
    }

    private void ClearActorAnimation_Click(object sender, RoutedEventArgs e) => ClearActorAnimation();

    private void ClearActorAnimation()
    {
        if (_animationLoading is not null)
        {
            _animationLoading.Cancel();
            IsBusy = false;
            BusyText = null;
        }
        _animationLoading = null;
        _resumeAnimationAfterDrag = false;
        SetPreviewAnimation(null);
        _loadedAnimation?.Dispose();
        _loadedAnimation = null;
        _selectedAnimationEntry = null;
        SelectedAnimationName = "No animation selected";
    }

    private void UnloadActorAnimationPreview()
    {
        ClearActorAnimation();
        _animationComponents = [];
        OnPropertyChanged(nameof(CanPreviewActorAnimations));
    }

    private void ActorAnimationPlayPause_Click(object sender, RoutedEventArgs e) => TogglePreviewAnimationPlayback();

    private void ActorAnimationTimeline_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resumeAnimationAfterDrag = IsPreviewAnimationPlaying;
        PausePreviewAnimation();
    }

    private void ActorAnimationTimeline_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_resumeAnimationAfterDrag && !IsPreviewAnimationPlaying)
            TogglePreviewAnimationPlayback();
        _resumeAnimationAfterDrag = false;
    }
}
