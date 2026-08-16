namespace LE1GalaxyMapEditor.Models;

public sealed class PlotPlanetEntry : GalaxyMapRow
{
    private int _code;
    private int _name;
    private string _nameText = string.Empty;

    public override GalaxyMapTable Table => GalaxyMapTable.PlotPlanet;

    public int Code { get => _code; set => SetProperty(ref _code, value); }
    public int Name { get => _name; set => SetProperty(ref _name, value); }
    public string NameText { get => _nameText; set => SetProperty(ref _nameText, value); }
}
