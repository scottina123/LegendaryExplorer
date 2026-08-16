using System.Globalization;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Creates new physical rows in the writable active module. This is deliberately
/// separate from copy-on-write overrides: every ID allocated here must be unused
/// and inside the module's reservation for its table.
/// </summary>
public sealed class GalaxyMapRowFactory
{
    private static readonly HashSet<string> ClusterColumns = Fields(
        "Label", "X", "Y", "Name", "NameText", "SphereSize", "Background");
    private static readonly HashSet<string> SystemColumns = Fields(
        "Label", "Cluster", "X", "Y", "Name", "NameText", "Scale", "ShowNebula");
    private static readonly HashSet<string> PlanetColumns = Fields(
        "Label", "System", "X", "Y", "Name", "NameText", "ActiveWorld", "Description",
        "ButtonLabel", "Map", "Scale", "RingColor", "OrbitRing", "SystemLevelType",
        "PlanetLevelType", "Event", "ImageIndex");

    private readonly GalaxyMapWorkspace _workspace;
    private readonly ModuleIdAllocator _allocator;

    public GalaxyMapRowFactory(GalaxyMapWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _allocator = new ModuleIdAllocator(workspace);
    }

    public Cluster CreateCluster(
        string? nameText = null,
        double x = 0.5,
        double y = 0.5,
        string? label = null)
    {
        var layer = RequireActiveLayer();
        var rowId = _allocator.NextAvailable(layer.Module, GalaxyMapTable.Cluster);
        var labelNumber = string.IsNullOrWhiteSpace(label)
            ? NextClusterLabelNumber(rowId)
            : ValidateNewClusterLabel(label);
        var row = new Cluster
        {
            RowId = rowId,
            Label = FormatLabel("Cluster", labelNumber),
            X = x,
            Y = y,
            Name = 0,
            NameText = UsefulName(nameText, "New Cluster"),
            SphereSize = 4,
            Background = "BIOA_GalaxyMap_T.Cluster01"
        };

        return AddNewRow(row, ClusterColumns,
            column => GalaxyMapDefaults.ExtraValue(GalaxyMapTable.Cluster, column));
    }

    public GalaxySystem CreateSystem(
        Cluster cluster,
        string? nameText = null,
        double x = 0.5,
        double y = 0.5)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var layer = RequireActiveLayer();
        var effectiveCluster = _workspace.EffectiveDocument.ClustersByRowId.GetValueOrDefault(cluster.RowId)
            ?? throw new InvalidOperationException($"Cluster row {cluster.RowId} is not present in the effective map.");
        var rowId = _allocator.NextAvailable(layer.Module, GalaxyMapTable.System);
        var labelNumber = NextScopedLabelNumber(
            effectiveCluster.Systems.Select(system => system.Label), "System");
        var row = new GalaxySystem
        {
            RowId = rowId,
            Label = FormatLabel("System", labelNumber),
            ClusterRowId = effectiveCluster.RowId,
            X = x,
            Y = y,
            Name = 0,
            NameText = UsefulName(nameText, "New System"),
            Scale = 1,
            ShowNebula = 0
        };

        return AddNewRow(row, SystemColumns,
            column => GalaxyMapDefaults.ExtraValue(GalaxyMapTable.System, column));
    }

    public Planet CreatePlanet(
        GalaxySystem system,
        PlanetCreationTemplate template = PlanetCreationTemplate.GenericPlanet,
        string? nameText = null,
        int name = 0,
        double? scale = null,
        double x = 0.5,
        double y = 0.5)
    {
        ArgumentNullException.ThrowIfNull(system);
        var layer = RequireActiveLayer();
        var effectiveSystem = _workspace.EffectiveDocument.SystemsByRowId.GetValueOrDefault(system.RowId)
            ?? throw new InvalidOperationException($"System row {system.RowId} is not present in the effective map.");
        var rowId = _allocator.NextAvailable(layer.Module, GalaxyMapTable.Planet);
        var labelNumber = NextScopedLabelNumber(
            effectiveSystem.Planets.Select(planet => planet.Label), "Planet");
        var label = FormatLabel("Planet", labelNumber);
        var row = new Planet
        {
            RowId = rowId,
            Label = label,
            SystemRowId = effectiveSystem.RowId,
            X = x,
            Y = y,
            Name = name,
            NameText = UsefulName(nameText, GalaxyMapDefaults.DefaultPlanetName(template)),
            ActiveWorld = TryDeriveActiveWorld(effectiveSystem, label, out var activeWorld) ? activeWorld : 0,
            Description = null,
            ButtonLabel = null,
            MapRowId = -1,
            RingColor = -1,
            Event = string.Empty,
            ImageIndex = -1
        };

        GalaxyMapDefaults.ApplyPlanetTemplate(row, template);
        if (scale is { } requestedScale)
        {
            row.Scale = requestedScale;
        }
        GalaxyMapDefaults.ApplyTemplateExtraValues(row, template);

        return AddNewRow(row, PlanetColumns,
            column => GalaxyMapDefaults.ExtraValue(GalaxyMapTable.Planet, column));
    }

    public static bool TryDeriveActiveWorld(GalaxySystem system, string planetLabel, out int activeWorld)
    {
        ArgumentNullException.ThrowIfNull(system);
        activeWorld = 0;
        return system.Cluster is { } cluster &&
               GalaxyMapIdentity.TryDeriveActiveWorld(
                   cluster.Label, system.Label, planetLabel, out activeWorld);
    }

    private T AddNewRow<T>(
        T row,
        IReadOnlySet<string> knownColumns,
        Func<string, string> extraDefault)
        where T : GalaxyMapRow
    {
        var layer = RequireActiveLayer();
        var schema = layer.GetSchema(row.Table) ?? CsvGalaxyMapLoader.GetCanonicalSchema(row.Table);
        if (layer.GetSchema(row.Table) is null)
        {
            layer.SetSchema(schema);
        }

        foreach (var header in schema.Headers.Skip(1))
        {
            if (!knownColumns.Contains(header))
            {
                if (!row.ExtraFields.ContainsKey(header))
                {
                    row.AddExtraField(header, extraDefault(header));
                }
            }
        }

        row.Origin = new GalaxyMapRowOrigin(layer.Module, OverridesLowerLayer: false);
        var values = schema.Headers
            .Select((header, index) => index == 0
                ? row.RowId.ToString(CultureInfo.InvariantCulture)
                : CsvValue(row, header))
            .ToArray();
        var snapshot = new CsvRowSnapshot(
            $"GalaxyMap_{row.Table}_part.csv",
            sourceRowNumber: 0,
            schema.Headers,
            values);
        for (var index = 0; index < schema.Headers.Count; index++)
        {
            snapshot.MarkDirty(index == 0 ? CsvRowSnapshot.RowIdColumnName : schema.Headers[index]);
        }

        row.CsvSnapshot = snapshot;
        layer.Upsert(row);
        var sourceOrder = layer.GetSourceRowOrder(row.Table).ToList();
        if (!sourceOrder.Contains(row.RowId))
        {
            sourceOrder.Add(row.RowId);
        }
        layer.SetSourceRowOrder(row.Table, sourceOrder);
        return row;
    }

    private GalaxyMapLayer RequireActiveLayer()
    {
        var layer = _workspace.ActiveLayer
            ?? throw new InvalidOperationException("Create or select a writable active module before adding rows.");
        if (layer.Module.IsReadOnly || layer.Module.IsBaseGame)
        {
            throw new InvalidOperationException("New rows cannot be added to a read-only module or BASEGAME.");
        }

        return layer;
    }

    private int NextClusterLabelNumber(int preferredRowId)
    {
        var used = LabelNumbers(
            _workspace.Layers.SelectMany(layer => layer.Clusters).Select(cluster => cluster.Label),
            GalaxyMapIdentityKind.Cluster);
        if (preferredRowId is >= GalaxyMapIdentityLimits.MinAuthoredClusterLabel and <= GalaxyMapIdentityLimits.MaxClusterLabel &&
            !used.Contains(preferredRowId))
        {
            return preferredRowId;
        }

        return NextAvailable(
            used,
            GalaxyMapIdentityLimits.MinAuthoredClusterLabel,
            GalaxyMapIdentityLimits.MaxClusterLabel,
            "Cluster");
    }

    private static int NextScopedLabelNumber(IEnumerable<string> labels, string prefix)
    {
        var kind = IdentityKind(prefix);
        return NextAvailable(LabelNumbers(labels, kind), 1, GalaxyMapIdentityLimits.MaxLabel(kind), prefix);
    }

    private static HashSet<int> LabelNumbers(
        IEnumerable<string> labels,
        GalaxyMapIdentityKind kind)
    {
        var used = new HashSet<int>();
        foreach (var label in labels)
        {
            var parsed = GalaxyMapIdentity.ParseLabelSyntax(label, kind);
            if (parsed.Status == GalaxyMapLabelParseStatus.Parsed && parsed.Suffix > 0)
            {
                used.Add(parsed.Suffix);
            }
        }

        return used;
    }

    private int ValidateNewClusterLabel(string label)
    {
        var parsed = GalaxyMapIdentity.ParseLabelSyntax(label, GalaxyMapIdentityKind.Cluster);
        var number = parsed.Suffix;
        if (parsed.Status != GalaxyMapLabelParseStatus.Parsed ||
            number is < GalaxyMapIdentityLimits.MinAuthoredClusterLabel or > GalaxyMapIdentityLimits.MaxClusterLabel)
        {
            throw new InvalidOperationException("New module Clusters must use a label from Cluster22 to Cluster99.");
        }
        if (_workspace.Layers.SelectMany(layer => layer.Clusters).Any(cluster =>
            {
                var existing = GalaxyMapIdentity.ParseLabelSyntax(
                    cluster.Label, GalaxyMapIdentityKind.Cluster);
                return existing.Status == GalaxyMapLabelParseStatus.Parsed &&
                       existing.Suffix == number;
            }))
        {
            throw new InvalidOperationException($"Cluster{number:D2} is already used by a mounted module.");
        }
        return number;
    }

    private static int NextAvailable(IReadOnlySet<int> used, int minimum, int maximum, string prefix)
    {
        for (var candidate = minimum; candidate <= maximum; candidate++)
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException(
            $"No {prefix} label is available in the supported {prefix}{minimum:D2}-{prefix}{maximum:D2} range.");
    }

    private static GalaxyMapIdentityKind IdentityKind(string prefix)
        => Enum.TryParse<GalaxyMapIdentityKind>(prefix, ignoreCase: true, out var kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(prefix), prefix, "Unknown galaxy-map label prefix.");

    private static string FormatLabel(string prefix, int number)
        => $"{prefix}{number.ToString(number < 100 ? "D2" : "D", CultureInfo.InvariantCulture)}";

    private static string UsefulName(string? supplied, string fallback)
        => string.IsNullOrWhiteSpace(supplied) ? fallback : supplied.Trim();

    private static HashSet<string> Fields(params string[] names)
        => new(names, StringComparer.OrdinalIgnoreCase);

    private static string CsvValue(GalaxyMapRow row, string column)
    {
        if (row.ExtraFields.TryGetValue(column, out var extra))
        {
            return extra;
        }

        return row switch
        {
            Cluster cluster => ClusterValue(cluster, column),
            GalaxySystem system => SystemValue(system, column),
            Planet planet => PlanetValue(planet, column),
            _ => throw new ArgumentException($"The factory does not create {row.Table} rows.", nameof(row))
        };
    }

    private static string ClusterValue(Cluster row, string column) => column.ToUpperInvariant() switch
    {
        "LABEL" => row.Label,
        "X" => Number(row.X),
        "Y" => Number(row.Y),
        "NAME" => Integer(row.Name),
        "NAMETEXT" => row.NameText,
        "SPHERESIZE" => Number(row.SphereSize),
        "BACKGROUND" => row.Background,
        _ => string.Empty
    };

    private static string SystemValue(GalaxySystem row, string column) => column.ToUpperInvariant() switch
    {
        "LABEL" => row.Label,
        "CLUSTER" => Integer(row.ClusterRowId),
        "X" => Number(row.X),
        "Y" => Number(row.Y),
        "NAME" => Integer(row.Name),
        "NAMETEXT" => row.NameText,
        "SCALE" => Number(row.Scale),
        "SHOWNEBULA" => Integer(row.ShowNebula),
        _ => string.Empty
    };

    private static string PlanetValue(Planet row, string column) => column.ToUpperInvariant() switch
    {
        "LABEL" => row.Label,
        "SYSTEM" => Integer(row.SystemRowId),
        "X" => Number(row.X),
        "Y" => Number(row.Y),
        "NAME" => Integer(row.Name),
        "NAMETEXT" => row.NameText,
        "ACTIVEWORLD" => Integer(row.ActiveWorld),
        "DESCRIPTION" => NullableInteger(row.Description),
        "BUTTONLABEL" => NullableInteger(row.ButtonLabel),
        "MAP" => Integer(row.MapRowId),
        "SCALE" => Number(row.Scale),
        "RINGCOLOR" => row.RingColor.ToString(CultureInfo.InvariantCulture),
        "ORBITRING" => Integer(row.OrbitRing),
        "SYSTEMLEVELTYPE" => Integer(row.SystemLevelType),
        "PLANETLEVELTYPE" => NullableInteger(row.PlanetLevelType),
        "EVENT" => row.Event,
        "IMAGEINDEX" => NullableInteger(row.ImageIndex),
        _ => string.Empty
    };

    private static string Number(double value) => GalaxyMapNumber.Serialize(value);
    private static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string NullableInteger(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

}
