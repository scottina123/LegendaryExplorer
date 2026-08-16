using System.Collections.ObjectModel;

namespace LE1GalaxyMapEditor.Models;

public sealed class Cluster : GalaxyMapRow
{
    private string _label = string.Empty;
    private double _x;
    private double _y;
    private int _name;
    private string _nameText = string.Empty;
    private double _sphereSize;
    private string _background = string.Empty;

    public override GalaxyMapTable Table => GalaxyMapTable.Cluster;

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

    public double SphereSize { get => _sphereSize; set => SetProperty(ref _sphereSize, value); }
    public string Background { get => _background; set => SetProperty(ref _background, value); }

    public ObservableCollection<GalaxySystem> Systems { get; } = [];

    public string DisplayName => FirstUseful(NameText, Label, Name.ToString(), $"Cluster {RowId}");
}
