using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class MeshplorerWindow
{
    private MeshplorerAnimationCatalog _animationCatalog;
    private MeshplorerAnimationCatalog.Entry _selectedAnimationEntry;
    private MeshplorerAnimationCatalog.LoadedAnimation _loadedAnimation;
    private CancellationTokenSource _animationLoading;
    private bool _animationWindowClosed;
    private bool _resumeAnimationAfterDrag;
    private string _selectedAnimationName = "No animation selected";

    public string SelectedAnimationName
    {
        get => _selectedAnimationName;
        private set => SetProperty(ref _selectedAnimationName, value);
    }

    private async void ChooseAnimation_Click(object sender, RoutedEventArgs e)
    {
        var mesh = CurrentExport;
        if (mesh?.ClassName != "SkeletalMesh" || !mesh.Game.IsMEGame()) return;
        _animationLoading?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _animationLoading = cancellation;
        MeshplorerAnimationCatalog.LoadedAnimation loaded = null;
        try
        {
            BusyText = $"Loading {mesh.Game} animation entries...";
            IsBusy = true;
            DateTime stamp = File.GetLastWriteTimeUtc(AssetDatabaseWindow.GetDBPath(mesh.Game));
            if (_animationCatalog?.Game != mesh.Game || _animationCatalog.DatabaseStamp != stamp)
                _animationCatalog = await MeshplorerAnimationCatalog.LoadAsync(mesh.Game, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_animationWindowClosed || !ReferenceEquals(mesh, CurrentExport)) return;
            IsBusy = false;
            if (_animationCatalog.Entries.Count == 0)
            {
                SelectedAnimationName = $"No animation sequences in the {mesh.Game} Asset Database. Rebuild the database to add animations.";
                return;
            }
            var selected = EntrySelector.GetItem(this, _animationCatalog.Entries,
                $"Choose an animation from the {mesh.Game} Asset Database to loop on the selected mesh.",
                _selectedAnimationEntry, searchHelpText: "Search animation name, sequence name, or animation group");
            if (selected == null) return;
            BusyText = $"Loading {selected.Record.AnimSequence}...";
            IsBusy = true;
            loaded = await _animationCatalog.LoadAnimationAsync(selected, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_animationWindowClosed || !ReferenceEquals(mesh, CurrentExport)) return;
            Mesh3DViewer.SetPreviewAnimation(loaded.Sequence);
            _loadedAnimation?.Dispose();
            _loadedAnimation = loaded;
            loaded = null;
            _selectedAnimationEntry = selected;
            SelectedAnimationName = selected.Record.AnimSequence;
            Mesh3DViewer.StartRendering();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _animationCatalog = null;
            if (!_animationWindowClosed)
                Xceed.Wpf.Toolkit.MessageBox.Show(this, ex.Message, "Animation preview");
        }
        finally
        {
            loaded?.Dispose();
            if (ReferenceEquals(_animationLoading, cancellation))
            {
                _animationLoading = null;
                IsBusy = false;
            }
        }
    }

    private void ClearAnimation_Click(object sender, RoutedEventArgs e) => ClearAnimation();

    private void ClearAnimation()
    {
        _animationLoading?.Cancel();
        _resumeAnimationAfterDrag = false;
        Mesh3DViewer.SetPreviewAnimation(null);
        _loadedAnimation?.Dispose();
        _loadedAnimation = null;
        _selectedAnimationEntry = null;
        SelectedAnimationName = "No animation selected";
    }

    private void AnimationPlayPause_Click(object sender, RoutedEventArgs e) => Mesh3DViewer.TogglePreviewAnimationPlayback();

    private void AnimationTimeline_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resumeAnimationAfterDrag = Mesh3DViewer.IsPreviewAnimationPlaying;
        Mesh3DViewer.PausePreviewAnimation();
    }

    private void AnimationTimeline_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_resumeAnimationAfterDrag && !Mesh3DViewer.IsPreviewAnimationPlaying)
            Mesh3DViewer.TogglePreviewAnimationPlayback();
        _resumeAnimationAfterDrag = false;
    }
}
