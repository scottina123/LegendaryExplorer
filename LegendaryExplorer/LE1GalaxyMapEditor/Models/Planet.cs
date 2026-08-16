namespace LE1GalaxyMapEditor.Models;

public enum PlanetVisualKind
{
    Planet,
    AsteroidBelt,
    Anomaly,
    RingedPlanet,
    Relay,
    FuelDepot,
    Sun,
    Object
}

public enum PlanetCreationTemplate
{
    GenericPlanet,
    RingedPlanet,
    AsteroidBelt,
    HiddenAnomaly,
    AnomalyOrShip
}

public sealed class Planet : GalaxyMapRow
{
    private string _label = string.Empty;
    private int _systemRowId;
    private GalaxySystem? _system;
    private double _x;
    private double _y;
    private int _name;
    private string _nameText = string.Empty;
    private int _activeWorld;
    private int? _description;
    private int? _buttonLabel;
    private int _mapRowId = -1;
    private MapEntry? _linkedMap;
    private double _scale;
    private long _ringColor;
    private int _orbitRing;
    private int _systemLevelType;
    private int? _planetLevelType;
    private string _event = string.Empty;
    private int? _imageIndex;
    private PlotPlanetEntry? _plotPlanet;

    public override GalaxyMapTable Table => GalaxyMapTable.Planet;

    public string Label
    {
        get => _label;
        set
        {
            if (SetProperty(ref _label, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(VisualKind));
            }
        }
    }

    public int SystemRowId { get => _systemRowId; set => SetProperty(ref _systemRowId, value); }
    public GalaxySystem? System { get => _system; internal set => SetProperty(ref _system, value); }
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
                OnPropertyChanged(nameof(VisualKind));
            }
        }
    }

    public int ActiveWorld { get => _activeWorld; set => SetProperty(ref _activeWorld, value); }
    public int? Description { get => _description; set => SetProperty(ref _description, value); }
    public int? ButtonLabel { get => _buttonLabel; set => SetProperty(ref _buttonLabel, value); }
    public int MapRowId { get => _mapRowId; set => SetProperty(ref _mapRowId, value); }
    public MapEntry? LinkedMap { get => _linkedMap; internal set => SetProperty(ref _linkedMap, value); }
    public double Scale { get => _scale; set => SetProperty(ref _scale, value); }
    /// <summary>
    /// Packed game colour value. It must support both vanilla's -1 sentinel and
    /// unsigned 32-bit packed values found in expansion CSVs.
    /// </summary>
    public long RingColor { get => _ringColor; set => SetProperty(ref _ringColor, value); }
    public int OrbitRing
    {
        get => _orbitRing;
        set
        {
            if (SetProperty(ref _orbitRing, value))
            {
                OnPropertyChanged(nameof(VisualKind));
                OnPropertyChanged(nameof(IsAsteroidBelt));
            }
        }
    }

    public int SystemLevelType
    {
        get => _systemLevelType;
        set
        {
            if (SetProperty(ref _systemLevelType, value))
            {
                OnPropertyChanged(nameof(VisualKind));
            }
        }
    }

    public int? PlanetLevelType { get => _planetLevelType; set => SetProperty(ref _planetLevelType, value); }
    public string Event { get => _event; set => SetProperty(ref _event, value); }
    public int? ImageIndex { get => _imageIndex; set => SetProperty(ref _imageIndex, value); }
    public PlotPlanetEntry? PlotPlanet { get => _plotPlanet; internal set => SetProperty(ref _plotPlanet, value); }

    public string DisplayName => FirstUseful(NameText, Label, Name.ToString(), $"Planet {RowId}");
    public bool IsAsteroidBelt => OrbitRing == 2;

    public PlanetVisualKind VisualKind
    {
        get
        {
            if (IsAsteroidBelt) return PlanetVisualKind.AsteroidBelt;

            bool Contains(string value) =>
                Label.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                NameText.Contains(value, StringComparison.OrdinalIgnoreCase);

            if (Contains("relay")) return PlanetVisualKind.Relay;
            if (Contains("depot")) return PlanetVisualKind.FuelDepot;
            if (Contains("sun") || Contains("star")) return PlanetVisualKind.Sun;
            if (Contains("anomaly")) return PlanetVisualKind.Anomaly;

            return SystemLevelType switch
            {
                0 => PlanetVisualKind.Planet,
                1 => PlanetVisualKind.Anomaly,
                2 => PlanetVisualKind.RingedPlanet,
                3 => PlanetVisualKind.Relay,
                4 => PlanetVisualKind.FuelDepot,
                5 => PlanetVisualKind.Sun,
                _ => PlanetVisualKind.Object
            };
        }
    }
}
