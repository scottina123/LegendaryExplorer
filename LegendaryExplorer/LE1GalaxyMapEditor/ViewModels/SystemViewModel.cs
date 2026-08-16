using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using System.Windows.Media;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed class SystemViewModel : MapViewModelBase
{
    public SystemViewModel(
        GalaxySystem system,
        IReadOnlyList<HierarchyNodeViewModel> planets,
        Action<HierarchyNodeViewModel> select,
        ImageSource? backgroundTexture = null,
        bool usesNebulaBackground = false)
    {
        System = system;
        Planets = planets;
        BackgroundTexture = backgroundTexture;
        UsesNebulaBackground = usesNebulaBackground;
        SelectCommand = new RelayCommand<HierarchyNodeViewModel>(node =>
        {
            if (node is not null) select(node);
        });
    }

    public GalaxySystem System { get; }
    public IReadOnlyList<HierarchyNodeViewModel> Planets { get; }
    public IReadOnlyList<Planet> PlanetModels => Planets.Select(node => (Planet)node.Item).ToArray();
    public ImageSource? BackgroundTexture { get; }
    public bool UsesNebulaBackground { get; }
    public double BackgroundScale => UsesNebulaBackground ? 2d : 1d;
    public RelayCommand<HierarchyNodeViewModel> SelectCommand { get; }
}
