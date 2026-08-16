using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using System.Windows.Media;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed class GalaxyViewModel : MapViewModelBase
{
    public GalaxyViewModel(
        IReadOnlyList<HierarchyNodeViewModel> clusters,
        IReadOnlyList<RelayConnection> relays,
        Action<HierarchyNodeViewModel> select,
        Action<HierarchyNodeViewModel> enterCluster,
        bool isAddingRelay,
        ImageSource? backgroundTexture)
    {
        Clusters = clusters;
        Relays = relays;
        IsAddingRelay = isAddingRelay;
        BackgroundTexture = backgroundTexture;
        SelectCommand = new RelayCommand<HierarchyNodeViewModel>(node =>
        {
            if (node is not null) select(node);
        });
        EnterClusterCommand = new RelayCommand<HierarchyNodeViewModel>(node =>
        {
            if (node is not null) enterCluster(node);
        });
    }

    public IReadOnlyList<HierarchyNodeViewModel> Clusters { get; }
    public IReadOnlyList<RelayConnection> Relays { get; }
    public ImageSource? BackgroundTexture { get; }
    public int ResolvedRelayCount => Relays.Count(relay => relay.IsResolved);
    public bool IsAddingRelay { get; }
    public RelayCommand<HierarchyNodeViewModel> SelectCommand { get; }
    public RelayCommand<HierarchyNodeViewModel> EnterClusterCommand { get; }
}
