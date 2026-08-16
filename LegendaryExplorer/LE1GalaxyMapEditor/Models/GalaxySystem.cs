using System.Collections.ObjectModel;

namespace LE1GalaxyMapEditor.Models;

public sealed class GalaxySystem : GalaxyMapRow
{
    private string _label = string.Empty;
    private int _clusterRowId;
    private Cluster? _cluster;
    private double _x;
    private double _y;
    private int _name;
    private string _nameText = string.Empty;
    private double _scale;
    private int _showNebula;

    public override GalaxyMapTable Table => GalaxyMapTable.System;

    public string Label
    {
        get => _label;
        set
        {
            if (SetProperty(ref _label, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public int ClusterRowId { get => _clusterRowId; set => SetProperty(ref _clusterRowId, value); }
    public Cluster? Cluster { get => _cluster; internal set => SetProperty(ref _cluster, value); }
    public double X { get => _x; set => SetProperty(ref _x, value); }
    public double Y { get => _y; set => SetProperty(ref _y, value); }

    public int Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string NameText
    {
        get => _nameText;
        set
        {
            if (SetProperty(ref _nameText, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public double Scale { get => _scale; set => SetProperty(ref _scale, value); }
    public int ShowNebula { get => _showNebula; set => SetProperty(ref _showNebula, value); }

    public ObservableCollection<Planet> Planets { get; } = [];

    public string DisplayName => FirstUseful(NameText, Label, Name.ToString(), $"System {RowId}");
}
