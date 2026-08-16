using System.Collections.Generic;
using System.Windows.Media;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Tools.SFXGalaxyEditor;

public enum SFXGalaxyNodeKind
{
    Galaxy,
    Cluster,
    System,
    Star,
    Planet,
    AsteroidBelt,
    MassRelay,
    FuelDepot,
    Reaper,
    Anomaly,
    Feature,
    WarAsset,
    Object
}

public sealed class SFXGalaxyNode
{
    public ExportEntry Export { get; init; }
    public SFXGalaxyNode Parent { get; set; }
    public List<SFXGalaxyNode> Children { get; } = [];
    public SFXGalaxyNodeKind Kind { get; init; }
    public string DisplayName { get; init; }
    public string Description { get; init; }
    public bool IsImplicitStar { get; init; }
    public int PosX { get; set; }
    public int PosY { get; set; }

    public bool CanNavigateInto => !IsImplicitStar
        && Kind is SFXGalaxyNodeKind.Galaxy or SFXGalaxyNodeKind.Cluster or SFXGalaxyNodeKind.System;
    public string ExportLabel => IsImplicitStar ? "Implicit SFXSystem star" : $"[{Export.UIndex}] {Export.ClassName}";
    public string KindLabel => Kind switch
    {
        SFXGalaxyNodeKind.AsteroidBelt => "Asteroid belt",
        SFXGalaxyNodeKind.MassRelay => "Mass relay",
        SFXGalaxyNodeKind.FuelDepot => "Fuel depot",
        SFXGalaxyNodeKind.WarAsset => "War asset",
        _ => Kind.ToString()
    };

    public string Glyph => Kind switch
    {
        SFXGalaxyNodeKind.Galaxy => "✦",
        SFXGalaxyNodeKind.Cluster => "✧",
        SFXGalaxyNodeKind.System => "⊙",
        SFXGalaxyNodeKind.Star => "☀",
        SFXGalaxyNodeKind.Planet => "●",
        SFXGalaxyNodeKind.AsteroidBelt => "◌",
        SFXGalaxyNodeKind.MassRelay => "⋈",
        SFXGalaxyNodeKind.FuelDepot => "▣",
        SFXGalaxyNodeKind.Reaper => "◆",
        SFXGalaxyNodeKind.Anomaly => "◇",
        SFXGalaxyNodeKind.Feature => "▲",
        SFXGalaxyNodeKind.WarAsset => "◆",
        _ => "•"
    };

    public Brush KindBrush => Kind switch
    {
        SFXGalaxyNodeKind.Galaxy => Brushes.DeepSkyBlue,
        SFXGalaxyNodeKind.Cluster => Brushes.Cyan,
        SFXGalaxyNodeKind.System => Brushes.LightSkyBlue,
        SFXGalaxyNodeKind.Star => Brushes.Gold,
        SFXGalaxyNodeKind.Planet => Brushes.DodgerBlue,
        SFXGalaxyNodeKind.AsteroidBelt => Brushes.Tan,
        SFXGalaxyNodeKind.MassRelay => Brushes.Orange,
        SFXGalaxyNodeKind.FuelDepot => Brushes.LimeGreen,
        SFXGalaxyNodeKind.Reaper => Brushes.IndianRed,
        SFXGalaxyNodeKind.Anomaly => Brushes.MediumPurple,
        SFXGalaxyNodeKind.Feature => Brushes.LightGreen,
        SFXGalaxyNodeKind.WarAsset => Brushes.Goldenrod,
        _ => Brushes.Silver
    };

    public IEnumerable<SFXGalaxyNode> SelfAndDescendants()
    {
        yield return this;
        foreach (SFXGalaxyNode child in Children)
        {
            foreach (SFXGalaxyNode descendant in child.SelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }

    public override string ToString() => DisplayName;
}

public sealed record SFXGalaxyEditableExport(ExportEntry Export, string Label)
{
    public override string ToString() => Label;
}

public sealed record SFXGalaxyPlanetMaterialSlot(string PropertyName, string DisplayName, string MeshPath)
{
    public override string ToString() => DisplayName;
}
