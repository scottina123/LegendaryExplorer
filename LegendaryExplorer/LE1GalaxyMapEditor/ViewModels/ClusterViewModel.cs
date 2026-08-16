using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using System.Windows.Media;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed class ClusterViewModel : MapViewModelBase
{
    public ClusterViewModel(
        Cluster cluster,
        IReadOnlyList<HierarchyNodeViewModel> systems,
        Action<HierarchyNodeViewModel> select,
        Action<HierarchyNodeViewModel> enterSystem,
        ImageSource? backgroundTexture)
    {
        Cluster = cluster;
        Systems = systems;
        _backgroundTexture = backgroundTexture;
        SelectCommand = new RelayCommand<HierarchyNodeViewModel>(node =>
        {
            if (node is not null) select(node);
        });
        EnterSystemCommand = new RelayCommand<HierarchyNodeViewModel>(node =>
        {
            if (node is not null) enterSystem(node);
        });
    }

    public Cluster Cluster { get; }
    public IReadOnlyList<HierarchyNodeViewModel> Systems { get; }
    private ImageSource? _backgroundTexture;
    public ImageSource? BackgroundTexture
    {
        get => _backgroundTexture;
        private set => SetProperty(ref _backgroundTexture, value);
    }

    public RelayCommand<HierarchyNodeViewModel> SelectCommand { get; }
    public RelayCommand<HierarchyNodeViewModel> EnterSystemCommand { get; }

    public void UpdateBackgroundTexture(ImageSource? backgroundTexture)
        => BackgroundTexture = backgroundTexture;
}
