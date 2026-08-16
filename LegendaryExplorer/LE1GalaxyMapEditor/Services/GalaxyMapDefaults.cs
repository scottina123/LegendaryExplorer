using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Canonical defaults for newly-authored rows. Keeping these in one place prevents
/// the factory, clone workflow and linked-row workflow from quietly disagreeing.
/// </summary>
public static class GalaxyMapDefaults
{
    public const int AlwaysConditional = 1;
    public const int AlwaysFunction = 974;
    public const int AlwaysParameter = 1;
    public const int HiddenFunction = 975;

    public static string ExtraValue(GalaxyMapTable table, string column)
        => table switch
        {
            GalaxyMapTable.Cluster => ClusterExtraValue(column),
            GalaxyMapTable.System => SystemExtraValue(column),
            GalaxyMapTable.Planet => PlanetExtraValue(column),
            GalaxyMapTable.PlotPlanet => PlotPlanetExtraValue(column),
            _ => string.Empty
        };

    public static void ApplyPlanetTemplate(Planet row, PlanetCreationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(row);
        (row.OrbitRing, row.SystemLevelType, row.PlanetLevelType, row.Scale) = template switch
        {
            PlanetCreationTemplate.GenericPlanet => (1, 0, 1, 4d),
            PlanetCreationTemplate.RingedPlanet => (1, 2, 1, 5d),
            PlanetCreationTemplate.AsteroidBelt => (2, 0, 0, 0.01d),
            PlanetCreationTemplate.HiddenAnomaly => (0, 1, 2, 1d),
            PlanetCreationTemplate.AnomalyOrShip => (0, 1, 2, 1d),
            _ => throw new ArgumentOutOfRangeException(nameof(template), template, null)
        };
        row.RingColor = -1;
    }

    public static string DefaultPlanetName(PlanetCreationTemplate template) => template switch
    {
        PlanetCreationTemplate.RingedPlanet => "New Ringed Planet",
        PlanetCreationTemplate.AsteroidBelt => "New Asteroid Belt",
        PlanetCreationTemplate.HiddenAnomaly => "New Hidden Anomaly",
        PlanetCreationTemplate.AnomalyOrShip => "New Anomaly / Ship",
        _ => "New Planet"
    };

    public static void ApplyTemplateExtraValues(Planet row, PlanetCreationTemplate template)
    {
        if (template is not PlanetCreationTemplate.HiddenAnomaly and not PlanetCreationTemplate.AsteroidBelt)
        {
            return;
        }

        // Vanilla hidden asteroid/anomaly entries and asteroid-belt anchors use
        // 975 for visibility. Hidden anomalies retain independent interaction
        // rules; every vanilla asteroid belt uses 975 for all three rule scopes.
        row.SetExtraField("VisibleConditional", "1");
        row.SetExtraField("VisibleFunction", HiddenFunction.ToString());
        row.SetExtraField("VisibleParameter", "1");
        row.SetExtraField("UsableConditional", "1");
        row.SetExtraField("UsableFunction", (template == PlanetCreationTemplate.AsteroidBelt ? HiddenFunction : AlwaysFunction).ToString());
        row.SetExtraField("UsableParameter", "1");
        row.SetExtraField("UsablePlanetConditional", "1");
        row.SetExtraField("UsablePlanetFunction", (template == PlanetCreationTemplate.AsteroidBelt ? HiddenFunction : AlwaysFunction).ToString());
        row.SetExtraField("UsablePlanetParameter", "1");
    }

    private static string ClusterExtraValue(string column) => column.ToUpperInvariant() switch
    {
        "COLOUR" or "COLOUR2" => "-1",
        "NEBULARDENSITY" or "CLOUDTILE" => "1",
        "SPHEREINTENSITY" => "3",
        "VISIBLECONDITIONAL" or "USABLECONDITIONAL" => "1",
        "VISIBLEFUNCTION" or "USABLEFUNCTION" => "974",
        "VISIBLEPARAMETER" or "USABLEPARAMETER" => "1",
        _ => string.Empty
    };

    private static string SystemExtraValue(string column) => column.ToUpperInvariant() switch
    {
        "COLOUR" or "COLOUR2" or "FLARETINT" => "-1",
        "EXITMAP" => "0",
        "VISIBLECONDITIONAL" or "USABLECONDITIONAL" => "1",
        "VISIBLEFUNCTION" or "USABLEFUNCTION" => "974",
        "VISIBLEPARAMETER" or "USABLEPARAMETER" => "1",
        _ => string.Empty
    };

    private static string PlanetExtraValue(string column)
    {
        var upper = column.ToUpperInvariant();
        if (upper is "SUNCOLOR0" or "SUNCOLOR1" or "SUNCOLOR2")
        {
            return "-1";
        }

        if (upper is "SHADER" or "CONTINENTMASK01" or "CONTINENTMASK02" or
            "CONTINENT_TEXTURE" or "OCEAN_TEXTURE" or "ATMOSPHEREMASTER" or
            "CITY_EMISSIVE" or "NORMAL_MAP" or "EVENTMESSAGE")
        {
            return string.Empty;
        }

        return upper switch
        {
            "VISIBLECONDITIONAL" or "USABLECONDITIONAL" or "USABLEPLANETCONDITIONAL" => "1",
            "VISIBLEFUNCTION" or "USABLEFUNCTION" or "USABLEPLANETFUNCTION" => "974",
            "VISIBLEPARAMETER" or "USABLEPARAMETER" or "USABLEPLANETPARAMETER" => "1",
            "EVENTPARAMETER" => "1",
            "OPACITY" => "1",
            _ => "0"
        };
    }

    private static string PlotPlanetExtraValue(string column) => column.ToUpperInvariant() switch
    {
        "VISIBLECONDITIONAL" or "USABLECONDITIONAL" => "1",
        "VISIBLEFUNCTION" or "USABLEFUNCTION" => "974",
        "VISIBLEPARAMETER" or "USABLEPARAMETER" => "1",
        _ => string.Empty
    };
}
