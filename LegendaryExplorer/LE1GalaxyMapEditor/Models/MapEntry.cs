namespace LE1GalaxyMapEditor.Models;

public sealed class MapEntry : GalaxyMapRow
{
    private string _mapName = string.Empty;
    private string _startPoint = string.Empty;

    public override GalaxyMapTable Table => GalaxyMapTable.Map;

    public string MapName { get => _mapName; set => SetProperty(ref _mapName, value); }
    public string StartPoint { get => _startPoint; set => SetProperty(ref _startPoint, value); }
}
