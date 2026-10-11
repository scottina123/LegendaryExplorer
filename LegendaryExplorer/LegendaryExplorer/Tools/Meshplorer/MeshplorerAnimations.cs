using System;
using System.IO;
using System.Threading;
using System.Windows;
using LegendaryExplorer.Dialogs;
using LegendaryExplorer.Tools.AssetDatabase;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.Meshplorer;

public partial class MeshplorerWindow
{
    private MeshplorerAnimationCatalog _animationCatalog;
    private MeshplorerAnimationCatalog.Entry _selectedAnimationEntry;
    private CancellationTokenSource _animationLoading;
    private bool _animationWindowClosed;

    private async void OpenAnimationPreview_Click(object sender, RoutedEventArgs e)
    {
        var mesh = CurrentExport;
        if (mesh?.ClassName != "SkeletalMesh" || !mesh.Game.IsMEGame()) return;
        _animationLoading?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _animationLoading = cancellation;
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
                Xceed.Wpf.Toolkit.MessageBox.Show(this,
                    $"No animation sequences in the {mesh.Game} Asset Database. Rebuild the database to add animations.",
                    "Animation preview");
                return;
            }
            using (var preview = new MeshplorerAnimationPickerPreview(_animationCatalog, mesh))
            {
                EntrySelector.GetItem(this, _animationCatalog.Entries,
                    $"Select an animation from the {mesh.Game} Asset Database to preview on the selected mesh.",
                    _selectedAnimationEntry, searchHelpText: "Search animation name, sequence name, or animation group",
                    preview: preview, previewSelectionChanged: entry =>
                    {
                        preview.SelectAnimation(entry);
                        if (entry != null) _selectedAnimationEntry = entry;
                    }, previewOnly: true, windowTitle: "Animation preview");
            }
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
            if (ReferenceEquals(_animationLoading, cancellation))
            {
                _animationLoading = null;
                IsBusy = false;
            }
        }
    }
}
