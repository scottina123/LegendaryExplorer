using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.UserControls.SharedToolControls;
using LegendaryExplorer.UserControls.SharedToolControls.LegacyScene3D;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.Meshplorer;

/// <summary>Uses the Asset Database animation viewport while browsing Meshplorer's animation catalog.</summary>
internal sealed class MeshplorerAnimationPickerPreview : DockPanel, IDisposable
{
    private readonly Func<MeshplorerAnimationCatalog.Entry, CancellationToken,
        Task<MeshplorerAnimationCatalog.LoadedAnimation>> _loadAnimation;
    private readonly ExportEntry _mesh;
    private readonly TextBlock _status;
    private CancellationTokenSource _loading;
    private MeshplorerAnimationCatalog.LoadedAnimation _loadedAnimation;
    private bool _disposed;

    internal AnimationPreviewControl Preview { get; } = new();
    internal Task PreviewLoadingTask { get; private set; } = Task.CompletedTask;
    public string StatusText => _status.Text;
    private MeshRenderContext Context => (MeshRenderContext)Preview.SceneViewer.Context;

    public MeshplorerAnimationPickerPreview(MeshplorerAnimationCatalog catalog, ExportEntry mesh)
        : this(catalog.LoadAnimationAsync, mesh)
    {
    }

    internal MeshplorerAnimationPickerPreview(Func<MeshplorerAnimationCatalog.Entry, CancellationToken,
        Task<MeshplorerAnimationCatalog.LoadedAnimation>> loadAnimation, ExportEntry mesh)
    {
        _loadAnimation = loadAnimation ?? throw new ArgumentNullException(nameof(loadAnimation));
        _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        var header = new TextBlock
        {
            Text = "Animation preview",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        SetDock(header, Dock.Top);
        Children.Add(header);
        var meshName = new TextBlock
        {
            Text = mesh.ObjectName.Instanced,
            ToolTip = mesh.InstancedFullPath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 4),
        };
        SetDock(meshName, Dock.Top);
        Children.Add(meshName);
        _status = new TextBlock
        {
            Text = "Select an animation to preview.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        SetDock(_status, Dock.Top);
        Children.Add(_status);
        Children.Add(Preview);
        // The inner viewport creates its Direct3D resources on Loaded before this handler runs.
        Preview.SceneViewer.Loaded += PreviewViewport_Loaded;
    }

    private void PreviewViewport_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || !Context.IsReady) return;
        if (Preview.CurrentMesh == null)
            Preview.LoadSkeletalMesh(_mesh);
        TryPlayAnimation();
    }

    public void SelectAnimation(MeshplorerAnimationCatalog.Entry entry)
    {
        if (_disposed) return;
        _loading?.Cancel();
        Preview.Pause();
        Preview.ClearAnimation();
        _loadedAnimation?.Dispose();
        _loadedAnimation = null;
        _status.Text = entry == null ? "Select an animation to preview." : $"Loading {entry.Record.AnimSequence}...";
        PreviewLoadingTask = entry == null ? Task.CompletedTask : LoadAnimationAsync(entry);
    }

    private async Task LoadAnimationAsync(MeshplorerAnimationCatalog.Entry entry)
    {
        using var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        MeshplorerAnimationCatalog.LoadedAnimation loaded = null;
        try
        {
            loaded = await _loadAnimation(entry, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_loading, cancellation)) return;
            _loadedAnimation = loaded;
            loaded = null;
            TryPlayAnimation();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_loading, cancellation))
                _status.Text = $"Could not preview animation: {ex.Message}";
        }
        finally
        {
            // Superseded and closed pickers must release even loaders that finish after cancellation.
            loaded?.Dispose();
            if (ReferenceEquals(_loading, cancellation)) _loading = null;
        }
    }

    private void TryPlayAnimation()
    {
        if (Preview.CurrentMesh == null)
        {
            if (Context.IsReady)
                _status.Text = Context.ErrorText ?? "Could not load the selected mesh for animation preview.";
            return;
        }
        if (_loadedAnimation == null) return;
        Preview.LoadAnimSequence(_loadedAnimation.Sequence.Export, preparedAnimation: _loadedAnimation.Sequence);
        if (Context.ErrorText is { Length: > 0 } animationError)
        {
            _status.Text = animationError;
            return;
        }
        Preview.Play();
        _status.Text = _loadedAnimation.Sequence.Name.Instanced;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loading?.Cancel();
        Preview.SceneViewer.Loaded -= PreviewViewport_Loaded;
        Preview.Pause();
        Preview.ClearAnimation();
        Preview.Dispose();
        _loadedAnimation?.Dispose();
        _loadedAnimation = null;
    }
}
