using System.Globalization;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Services;

/// <summary>
/// Validates both the physical module layers and their composed, effective galaxy map.
/// Layer checks catch unsafe CSV/range collisions; document checks deliberately run only
/// after composition so DLC rows may reference BASEGAME or lower mounted modules.
/// </summary>
public sealed class GalaxyMapValidator
{
    private static readonly string[] PackedPlanetAppearanceColumns = PlanetAppearanceSchema.Properties
        .Where(property => property.Editor == PlanetAppearanceEditorKind.PackedColor)
        .SelectMany(property => property.Columns)
        .ToArray();

    private static readonly GalaxyMapTable[] ReservableTables =
    [
        GalaxyMapTable.Cluster,
        GalaxyMapTable.System,
        GalaxyMapTable.Planet,
        GalaxyMapTable.Map,
        GalaxyMapTable.Relay
    ];

    public IReadOnlyList<ValidationDiagnostic> Validate(GalaxyMapWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var diagnostics = new List<ValidationDiagnostic>();

        ValidateModuleRanges(workspace, diagnostics);
        ValidateLayers(workspace, diagnostics);
        ValidateEffectiveDocument(workspace.EffectiveDocument, diagnostics, validateCollectionOrder: false);
        return Order(diagnostics);
    }

    public IReadOnlyList<ValidationDiagnostic> Validate(GalaxyMapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var diagnostics = new List<ValidationDiagnostic>();
        ValidateEffectiveDocument(document, diagnostics, validateCollectionOrder: true);
        return Order(diagnostics);
    }

    private static void ValidateModuleRanges(
        GalaxyMapWorkspace workspace,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        var modules = workspace.Modules.OrderBy(module => module.LoadOrder)
            .ThenBy(module => module.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var left in modules)
        {
            foreach (var right in modules.Where(candidate =>
                         string.Compare(left.Tag, candidate.Tag, StringComparison.OrdinalIgnoreCase) < 0))
            {
                foreach (var table in ReservableTables)
                {
                    var leftRange = left.Reservations.GetRange(table);
                    var rightRange = right.Reservations.GetRange(table);
                    if (leftRange is not { } first || rightRange is not { } second || !first.Overlaps(second))
                    {
                        continue;
                    }

                    diagnostics.Add(new ValidationDiagnostic(
                        "MOD-RANGE-OVERLAP",
                        ValidationSeverity.Error,
                        $"{left.Tag}'s {table} range {first} overlaps {right.Tag}'s range {second}.",
                        left.Tag,
                        table.ToString()));
                }
            }
        }

        var layers = workspace.Layers.ToArray();
        for (var layerIndex = 1; layerIndex < layers.Length; layerIndex++)
        {
            var layer = layers[layerIndex];
            foreach (var table in ReservableTables)
            {
                if (layer.Module.Reservations.GetRange(table) is not { } range)
                {
                    continue;
                }

                var collisions = layers.Take(layerIndex)
                    .SelectMany(lower => ReservationRows(lower, table))
                    .Where(row => range.Contains(row.RowId))
                    .GroupBy(row => row.RowId)
                    .Select(group => group.Last())
                    .ToArray();
                foreach (var collision in collisions)
                {
                    diagnostics.Add(new ValidationDiagnostic(
                        "MOD-RANGE-ROW-OVERLAP",
                        ValidationSeverity.Error,
                        $"{layer.Module.Tag}'s {table} range {range} contains existing row ID " +
                        $"{collision.RowId} from {collision.Origin?.ModuleTag ?? "a lower layer"}.",
                        layer.Module.Tag,
                        table.ToString(),
                        collision.RowId,
                        "Row ID"));
                }
            }
        }
    }

    private static IEnumerable<GalaxyMapRow> ReservationRows(
        GalaxyMapLayer layer,
        GalaxyMapTable table)
        => table == GalaxyMapTable.Planet
            ? layer.Rows(GalaxyMapTable.Planet).Concat(layer.Rows(GalaxyMapTable.PlotPlanet))
            : layer.Rows(table);

    private static void ValidateLayers(
        GalaxyMapWorkspace workspace,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        var lowerRows = Enum.GetValues<GalaxyMapTable>()
            .ToDictionary(table => table, _ => new Dictionary<int, GalaxyMapModule>());
        var canonicalSchemas = workspace.BaseLayer.Schemas;

        foreach (var layer in workspace.Layers)
        {
            foreach (var table in Enum.GetValues<GalaxyMapTable>())
            {
                var rows = layer.Rows(table).ToArray();
                ValidatePhysicalOrder(layer, table, diagnostics);
                ValidateLayerSchema(layer, table, rows.Length, canonicalSchemas.GetValueOrDefault(table), diagnostics);

                foreach (var duplicate in rows.GroupBy(row => row.RowId).Where(group => group.Count() > 1))
                {
                    diagnostics.Add(new ValidationDiagnostic(
                        "ID-DUPLICATE-LAYER",
                        ValidationSeverity.Error,
                        $"{layer.Module.Tag} contains {duplicate.Count()} {table} rows with ID {duplicate.Key}. " +
                        "Same-ID overrides are valid only across different layers.",
                        layer.Module.Tag,
                        table.ToString(),
                        duplicate.Key));
                }

                foreach (var row in rows)
                {
                    if (row.RowId < 0)
                    {
                        AddForRow(diagnostics, row, "ID-NEGATIVE", ValidationSeverity.Error,
                            "Row ID must be zero or greater.", "Row ID", layer.Module.Tag);
                    }

                    var hasLowerRow = lowerRows[table].TryGetValue(row.RowId, out var lowerModule);
                    if (layer.Module.IsBaseGame)
                    {
                        continue;
                    }

                    if (hasLowerRow)
                    {
                        if (lowerModule!.IsBaseGame)
                        {
                            AddForRow(diagnostics, row, "ID-BASEGAME-OVERRIDE", ValidationSeverity.Info,
                                $"This row intentionally mounts above BASEGAME {table} row {row.RowId}.",
                                "Row ID", layer.Module.Tag);
                        }
                        else
                        {
                            AddForRow(diagnostics, row, "ID-MODULE-OVERRIDE", ValidationSeverity.Info,
                                $"This row intentionally mounts above {lowerModule.Tag} {table} row {row.RowId}.",
                                "Row ID", layer.Module.Tag);
                        }

                        continue;
                    }

                    var isPlotExtensionForExistingPlanet = table == GalaxyMapTable.PlotPlanet &&
                        workspace.EffectiveDocument.PlanetsByRowId.ContainsKey(row.RowId);
                    if (isPlotExtensionForExistingPlanet)
                    {
                        AddForRow(diagnostics, row, "ID-PLOTPLANET-EXTENSION", ValidationSeverity.Info,
                            $"PlotPlanet row {row.RowId} intentionally extends the Planet with the same row ID.",
                            "Row ID", layer.Module.Tag);
                        continue;
                    }

                    var reservation = layer.Module.Reservations.GetRange(table);
                    if (reservation is null)
                    {
                        AddForRow(diagnostics, row, "ID-NO-RESERVATION", ValidationSeverity.Error,
                            $"{layer.Module.Tag} has no reserved {table} row ID range for this new row.",
                            "Row ID", layer.Module.Tag);
                    }
                    else if (!reservation.Value.Contains(row.RowId))
                    {
                        AddForRow(diagnostics, row, "ID-OUTSIDE-RESERVATION", ValidationSeverity.Error,
                            $"New {table} row ID {row.RowId} is outside {layer.Module.Tag}'s reserved range {reservation}.",
                            "Row ID", layer.Module.Tag);
                    }
                }

                foreach (var row in rows)
                {
                    lowerRows[table][row.RowId] = layer.Module;
                }
            }
        }
    }

    private static void ValidatePhysicalOrder(
        GalaxyMapLayer layer,
        GalaxyMapTable table,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        var sourceOrder = layer.GetSourceRowOrder(table);
        for (var index = 1; index < sourceOrder.Count; index++)
        {
            if (sourceOrder[index] > sourceOrder[index - 1])
            {
                continue;
            }

            diagnostics.Add(new ValidationDiagnostic(
                "CSV-ROW-ORDER",
                ValidationSeverity.Warning,
                $"CSV row IDs must be in strictly increasing numerical order; {sourceOrder[index]} follows " +
                $"{sourceOrder[index - 1]}. The writer will sort this file before saving.",
                layer.Module.Tag,
                table.ToString(),
                sourceOrder[index]));
        }
    }

    private static void ValidateLayerSchema(
        GalaxyMapLayer layer,
        GalaxyMapTable table,
        int rowCount,
        CsvTableSchema? canonical,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        var schema = layer.GetSchema(table);
        if (schema is null)
        {
            if (rowCount > 0 && !layer.Module.IsBaseGame)
            {
                diagnostics.Add(new ValidationDiagnostic(
                    "CSV-SCHEMA-MISSING",
                    ValidationSeverity.Warning,
                    $"The {table} layer has rows but no captured CSV schema. The canonical BASEGAME header will be used.",
                    layer.Module.Tag,
                    table.ToString()));
            }

            return;
        }

        if (schema.Headers.Count == 0 || !string.IsNullOrWhiteSpace(schema.Headers[0]))
        {
            diagnostics.Add(new ValidationDiagnostic(
                "CSV-ROW-ID-HEADER",
                ValidationSeverity.Error,
                "The first CSV header must be unnamed because it contains the 2DA Row ID.",
                layer.Module.Tag,
                table.ToString(),
                ColumnName: "Row ID"));
        }

        var duplicateHeader = schema.Headers.Skip(1)
            .GroupBy(header => header, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateHeader is not null)
        {
            diagnostics.Add(new ValidationDiagnostic(
                "CSV-DUPLICATE-HEADER",
                ValidationSeverity.Error,
                "CSV headers after Row ID must be non-blank and unique.",
                layer.Module.Tag,
                table.ToString(),
                ColumnName: duplicateHeader.Key));
        }

        if (canonical is null || layer.Module.IsBaseGame)
        {
            return;
        }

        var expected = canonical.Headers.Skip(1).ToArray();
        var actual = schema.Headers.Skip(1).ToArray();
        var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
        {
            var details = string.Join(" ", new[]
            {
                missing.Length == 0 ? string.Empty : $"Missing: {string.Join(", ", missing)}.",
                extra.Length == 0 ? string.Empty : $"Unexpected: {string.Join(", ", extra)}."
            }.Where(text => text.Length > 0));
            diagnostics.Add(new ValidationDiagnostic(
                "CSV-SCHEMA-MISMATCH",
                ValidationSeverity.Error,
                $"The _part schema must match the BASEGAME {table} columns. {details}",
                layer.Module.Tag,
                table.ToString()));
            return;
        }

        if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new ValidationDiagnostic(
                "CSV-COLUMN-ORDER",
                ValidationSeverity.Warning,
                "Columns are not in the canonical BASEGAME order. The writer will restore canonical order.",
                layer.Module.Tag,
                table.ToString()));
        }
    }

    private static void ValidateEffectiveDocument(
        GalaxyMapDocument document,
        ICollection<ValidationDiagnostic> diagnostics,
        bool validateCollectionOrder)
    {
        var tableRows = new Dictionary<GalaxyMapTable, GalaxyMapRow[]>
        {
            [GalaxyMapTable.Cluster] = document.Clusters.Cast<GalaxyMapRow>().ToArray(),
            [GalaxyMapTable.System] = document.Systems.Cast<GalaxyMapRow>().ToArray(),
            [GalaxyMapTable.Planet] = document.Planets.Cast<GalaxyMapRow>().ToArray(),
            [GalaxyMapTable.PlotPlanet] = document.PlotPlanets.Cast<GalaxyMapRow>().ToArray(),
            [GalaxyMapTable.Map] = document.Maps.Cast<GalaxyMapRow>().ToArray(),
            [GalaxyMapTable.Relay] = document.Relays.Cast<GalaxyMapRow>().ToArray()
        };

        foreach (var (table, rows) in tableRows)
        {
            ValidateEffectiveIds(table, rows, diagnostics, validateCollectionOrder);
            foreach (var row in rows)
            {
                ValidateAvailabilityRules(row, diagnostics);
            }
        }

        var clusters = FirstById(document.Clusters);
        var systems = FirstById(document.Systems);
        var planets = FirstById(document.Planets);
        var plotPlanets = FirstById(document.PlotPlanets);
        var maps = FirstById(document.Maps);

        ValidateClusters(document.Clusters, diagnostics);
        ValidateSystems(document.Systems, clusters, diagnostics);
        ValidatePlanets(document.Planets, systems, clusters, maps, diagnostics);
        ValidatePlotPlanets(document.PlotPlanets, planets, diagnostics);
        ValidateMapLinks(document.Planets, maps, diagnostics);
        ValidateRelays(document.Relays, document.Clusters, diagnostics);

        // Ensure the dictionaries are actually exercised here so a future validator change
        // does not accidentally omit effective-table duplicate checks.
        _ = plotPlanets;
    }

    private static void ValidateEffectiveIds(
        GalaxyMapTable table,
        IReadOnlyList<GalaxyMapRow> rows,
        ICollection<ValidationDiagnostic> diagnostics,
        bool validateOrder)
    {
        foreach (var duplicate in rows.GroupBy(row => row.RowId).Where(group => group.Count() > 1))
        {
            foreach (var row in duplicate)
            {
                AddForRow(diagnostics, row, "ID-DUPLICATE-EFFECTIVE", ValidationSeverity.Error,
                    $"The effective {table} table contains duplicate row ID {row.RowId}.", "Row ID");
            }
        }

        foreach (var row in rows.Where(row => row.RowId < 0))
        {
            AddForRow(diagnostics, row, "ID-NEGATIVE", ValidationSeverity.Error,
                "Row ID must be zero or greater.", "Row ID");
        }

        if (!validateOrder)
        {
            return;
        }

        for (var index = 1; index < rows.Count; index++)
        {
            if (rows[index].RowId > rows[index - 1].RowId)
            {
                continue;
            }

            AddForRow(diagnostics, rows[index], "CSV-ROW-ORDER", ValidationSeverity.Warning,
                $"Row ID {rows[index].RowId} follows {rows[index - 1].RowId}; rows should be in increasing order.",
                "Row ID");
        }
    }

    private static void ValidateClusters(
        IEnumerable<Cluster> clusters,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        var codes = new Dictionary<int, Cluster>();
        foreach (var cluster in clusters)
        {
            ValidateCoordinate(cluster, cluster.X, "X", diagnostics);
            ValidateCoordinate(cluster, cluster.Y, "Y", diagnostics);
            ValidatePositiveFinite(cluster, cluster.SphereSize, nameof(Cluster.SphereSize), diagnostics);

            var parsed = GalaxyMapIdentity.ParseLabelSyntax(
                cluster.Label, GalaxyMapIdentityKind.Cluster);
            if (parsed.Status == GalaxyMapLabelParseStatus.Malformed)
            {
                AddForRow(diagnostics, cluster, "LABEL-CLUSTER", ValidationSeverity.Error,
                    $"Label '{cluster.Label}' must use the form ClusterNN so Relay and ActiveWorld codes can resolve.",
                    nameof(Cluster.Label));
                continue;
            }

            if (parsed.Status == GalaxyMapLabelParseStatus.NumericOverflow ||
                !GalaxyMapIdentity.IsValidExistingSuffix(
                    GalaxyMapIdentityKind.Cluster, parsed.Suffix))
            {
                AddForRow(diagnostics, cluster, "LABEL-CLUSTER-RANGE", ValidationSeverity.Error,
                    "A Cluster label suffix must be in the game-supported range 1-99.",
                    nameof(Cluster.Label));
                continue;
            }

            var suffix = parsed.Suffix;
            if (codes.TryGetValue(suffix, out var existing))
            {
                AddForRow(diagnostics, cluster, "LABEL-CLUSTER-DUPLICATE", ValidationSeverity.Error,
                    $"Cluster label code {suffix} is also used by row {existing.RowId}.", nameof(Cluster.Label));
            }
            else
            {
                codes[suffix] = cluster;
            }
        }
    }

    private static void ValidateSystems(
        IEnumerable<GalaxySystem> systems,
        IReadOnlyDictionary<int, Cluster> clusters,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        var parentScopedCodes = new Dictionary<(int ClusterRowId, int Suffix), GalaxySystem>();
        foreach (var system in systems)
        {
            ValidateCoordinate(system, system.X, "X", diagnostics);
            ValidateCoordinate(system, system.Y, "Y", diagnostics);
            ValidateNonNegativeFinite(system, system.Scale, nameof(GalaxySystem.Scale), diagnostics);
            if (system.ShowNebula is not 0 and not 1)
            {
                AddForRow(diagnostics, system, "TYPE-SHOW-NEBULA", ValidationSeverity.Warning,
                    $"ShowNebula is normally 0 or 1, not {system.ShowNebula}.", nameof(GalaxySystem.ShowNebula));
            }

            if (!clusters.ContainsKey(system.ClusterRowId))
            {
                AddForRow(diagnostics, system, "REF-SYSTEM-CLUSTER", ValidationSeverity.Error,
                    $"Cluster row {system.ClusterRowId} is not available in the effective module stack.",
                    nameof(GalaxySystem.ClusterRowId));
            }

            var parsed = GalaxyMapIdentity.ParseLabelSyntax(
                system.Label, GalaxyMapIdentityKind.System);
            if (parsed.Status == GalaxyMapLabelParseStatus.Malformed)
            {
                AddForRow(diagnostics, system, "LABEL-SYSTEM", ValidationSeverity.Error,
                    $"Label '{system.Label}' must use the form SystemNN for ActiveWorld encoding.",
                    nameof(GalaxySystem.Label));
                continue;
            }

            if (parsed.Status == GalaxyMapLabelParseStatus.NumericOverflow)
            {
                AddForRow(diagnostics, system, "LABEL-SYSTEM-RANGE", ValidationSeverity.Error,
                    "A System label suffix must be in the game-supported range 1-9 (System01-System09).",
                    nameof(GalaxySystem.Label));
                continue;
            }

            var suffix = parsed.Suffix;
            if (!GalaxyMapIdentity.IsValidExistingSuffix(
                    GalaxyMapIdentityKind.System, suffix))
            {
                AddForRow(diagnostics, system, "LABEL-SYSTEM-RANGE", ValidationSeverity.Error,
                    "A System label suffix must be in the game-supported range 1-9 (System01-System09).",
                    nameof(GalaxySystem.Label));
            }

            var key = (system.ClusterRowId, suffix);
            if (parentScopedCodes.TryGetValue(key, out var existing))
            {
                AddForRow(diagnostics, system, "LABEL-SYSTEM-DUPLICATE", ValidationSeverity.Error,
                    $"System label code {suffix:00} is also used by System row {existing.RowId} in this Cluster.",
                    nameof(GalaxySystem.Label));
            }
            else
            {
                parentScopedCodes[key] = system;
            }
        }
    }

    private static void ValidatePlanets(
        IEnumerable<Planet> planets,
        IReadOnlyDictionary<int, GalaxySystem> systems,
        IReadOnlyDictionary<int, Cluster> clusters,
        IReadOnlyDictionary<int, MapEntry> maps,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        var parentScopedCodes = new Dictionary<(int SystemRowId, int Suffix), Planet>();
        var activeWorlds = new Dictionary<int, Planet>();

        foreach (var planet in planets)
        {
            ValidateCoordinate(planet, planet.X, "X", diagnostics);
            ValidateCoordinate(planet, planet.Y, "Y", diagnostics);
            ValidateNonNegativeFinite(planet, planet.Scale, nameof(Planet.Scale), diagnostics);

            if (!systems.TryGetValue(planet.SystemRowId, out var system))
            {
                AddForRow(diagnostics, planet, "REF-PLANET-SYSTEM", ValidationSeverity.Error,
                    $"System row {planet.SystemRowId} is not available in the effective module stack.",
                    nameof(Planet.SystemRowId));
            }

            if (planet.MapRowId < -1)
            {
                AddForRow(diagnostics, planet, "TYPE-MAP-SENTINEL", ValidationSeverity.Error,
                    "Map must be -1 for no link, or a non-negative Map row ID.", nameof(Planet.MapRowId));
            }
            else if (planet.MapRowId >= 0 && !maps.ContainsKey(planet.MapRowId))
            {
                AddForRow(diagnostics, planet, "REF-PLANET-MAP", ValidationSeverity.Error,
                    $"Map row {planet.MapRowId} is not available in the effective module stack.",
                    nameof(Planet.MapRowId));
            }

            if (planet.OrbitRing is < 0 or > 2)
            {
                AddForRow(diagnostics, planet, "TYPE-ORBIT-RING", ValidationSeverity.Warning,
                    $"OrbitRing value {planet.OrbitRing} is outside the known 0, 1 and 2 values.",
                    nameof(Planet.OrbitRing));
            }

            if (planet.OrbitRing == 2 && (planet.SystemLevelType != 0 || planet.PlanetLevelType != 0))
            {
                AddForRow(diagnostics, planet, "TYPE-ASTEROID-BELT-COMBINATION", ValidationSeverity.Warning,
                    "Vanilla asteroid belts use SystemLevelType 0 and PlanetLevelType 0.",
                    nameof(Planet.OrbitRing));
            }

            if (planet.OrbitRing == 2)
            {
                foreach (var functionName in new[] { "VisibleFunction", "UsableFunction", "UsablePlanetFunction" })
                {
                    if (planet.ExtraFields.TryGetValue(functionName, out var function) && function != "975")
                    {
                        AddForRow(diagnostics, planet, "TYPE-ASTEROID-BELT-RULE", ValidationSeverity.Warning,
                            $"All vanilla asteroid belts use function 975 for {functionName}; another value may expose the belt anchor.",
                            functionName);
                    }
                }
            }

            if (planet.SystemLevelType == 2 && planet.OrbitRing != 1)
            {
                AddForRow(diagnostics, planet, "TYPE-RINGED-PLANET-ORBIT", ValidationSeverity.Warning,
                    "A ringed planet normally uses OrbitRing 1.", nameof(Planet.OrbitRing));
            }

            if (planet.SystemLevelType == 1 && planet.PlanetLevelType is not 2 and not 4)
            {
                AddForRow(diagnostics, planet, "TYPE-ANOMALY-SELECTION", ValidationSeverity.Warning,
                    "Anomalies and ships normally use PlanetLevelType 2; Citadel uses the special value 4.",
                    nameof(Planet.PlanetLevelType));
            }

            if (planet.SystemLevelType is < 0 or > 5)
            {
                AddForRow(diagnostics, planet, "TYPE-SYSTEM-LEVEL", ValidationSeverity.Warning,
                    $"SystemLevelType value {planet.SystemLevelType} is not currently recognised.",
                    nameof(Planet.SystemLevelType));
            }

            if (planet.PlanetLevelType is null)
            {
                AddForRow(diagnostics, planet, "TYPE-PLANET-LEVEL-MISSING", ValidationSeverity.Error,
                    "PlanetLevelType is required; a blank value cannot be safely written.",
                    nameof(Planet.PlanetLevelType));
            }
            else if (planet.PlanetLevelType is < 0 or > 7)
            {
                AddForRow(diagnostics, planet, "TYPE-PLANET-LEVEL", ValidationSeverity.Warning,
                    $"PlanetLevelType value {planet.PlanetLevelType} is outside the known 0-7 values.",
                    nameof(Planet.PlanetLevelType));
            }
            else if (planet.PlanetLevelType is 3 or 5 or 7)
            {
                AddForRow(diagnostics, planet, "TYPE-PLANET-LEVEL-BROKEN", ValidationSeverity.Warning,
                    $"PlanetLevelType {planet.PlanetLevelType} is recognised by the schema but is known to be broken in LE1.",
                    nameof(Planet.PlanetLevelType));
            }

            if (planet.SystemLevelType != 2 && planet.RingColor != -1)
            {
                AddForRow(diagnostics, planet, "TYPE-RING-COLOR-NONRINGED", ValidationSeverity.Warning,
                    "RingColor should be -1 unless SystemLevelType is 2 (ringed planet).",
                    nameof(Planet.RingColor));
            }

            if (planet.RingColor is < int.MinValue or > uint.MaxValue)
            {
                AddForRow(diagnostics, planet, "TYPE-RING-COLOR", ValidationSeverity.Error,
                    "RingColor must fit either a signed or unsigned packed 32-bit colour value.",
                    nameof(Planet.RingColor));
            }

            foreach (var column in PackedPlanetAppearanceColumns)
            {
                if (!planet.ExtraFields.TryGetValue(column, out var token))
                {
                    continue;
                }

                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var packed) &&
                    packed is >= int.MinValue and <= uint.MaxValue)
                {
                    continue;
                }

                AddForRow(diagnostics, planet, "TYPE-PLANET-PACKED-COLOR", ValidationSeverity.Warning,
                    $"{column} should be a signed or unsigned packed 32-bit colour value; " +
                    $"'{token}' will render as black in the editor preview.",
                    column);
            }

            var parsed = GalaxyMapIdentity.ParseLabelSyntax(
                planet.Label, GalaxyMapIdentityKind.Planet);
            if (parsed.Status == GalaxyMapLabelParseStatus.Malformed)
            {
                AddForRow(diagnostics, planet, "LABEL-PLANET", ValidationSeverity.Error,
                    $"Label '{planet.Label}' must use the form PlanetNN for ActiveWorld encoding.",
                    nameof(Planet.Label));
            }
            else if (parsed.Status == GalaxyMapLabelParseStatus.NumericOverflow)
            {
                AddForRow(diagnostics, planet, "LABEL-PLANET-RANGE", ValidationSeverity.Error,
                    "A Planet label suffix must fit the positive two-digit ActiveWorld segment (1-99).",
                    nameof(Planet.Label));
            }
            else
            {
                var planetSuffix = parsed.Suffix;
                if (!GalaxyMapIdentity.IsValidExistingSuffix(
                        GalaxyMapIdentityKind.Planet, planetSuffix))
                {
                    AddForRow(diagnostics, planet, "LABEL-PLANET-RANGE", ValidationSeverity.Error,
                        "A Planet label suffix must fit the positive two-digit ActiveWorld segment (1-99).",
                        nameof(Planet.Label));
                }

                var parentKey = (planet.SystemRowId, planetSuffix);
                if (parentScopedCodes.TryGetValue(parentKey, out var existing))
                {
                    AddForRow(diagnostics, planet, "LABEL-PLANET-DUPLICATE", ValidationSeverity.Error,
                        $"Planet label code {planetSuffix:00} is also used by Planet row {existing.RowId} in this System.",
                        nameof(Planet.Label));
                }
                else
                {
                    parentScopedCodes[parentKey] = planet;
                }

                ValidateActiveWorld(planet, planetSuffix, system, clusters, diagnostics);
            }

            if (activeWorlds.TryGetValue(planet.ActiveWorld, out var sameActiveWorld))
            {
                AddForRow(diagnostics, planet, "ACTIVEWORLD-DUPLICATE", ValidationSeverity.Error,
                    $"ActiveWorld {planet.ActiveWorld} is also used by Planet row {sameActiveWorld.RowId}.",
                    nameof(Planet.ActiveWorld));
            }
            else
            {
                activeWorlds[planet.ActiveWorld] = planet;
            }
        }
    }

    private static void ValidateActiveWorld(
        Planet planet,
        int planetSuffix,
        GalaxySystem? system,
        IReadOnlyDictionary<int, Cluster> clusters,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        if (system is null || !clusters.TryGetValue(system.ClusterRowId, out var cluster) ||
            !GalaxyMapIdentity.IsValidExistingSuffix(
                GalaxyMapIdentityKind.Planet, planetSuffix) ||
            !GalaxyMapIdentity.TryDeriveActiveWorld(
                cluster.Label, system.Label, planet.Label, out var expected))
        {
            return;
        }

        if (planet.ActiveWorld == expected)
        {
            return;
        }

        AddForRow(diagnostics, planet, "ACTIVEWORLD-MISMATCH", ValidationSeverity.Error,
            $"ActiveWorld {planet.ActiveWorld} does not match {cluster.Label}/{system.Label}/{planet.Label}; " +
            $"the expected value is {expected}.", nameof(Planet.ActiveWorld));
    }

    private static void ValidateAvailabilityRules(
        GalaxyMapRow row,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        foreach (var prefix in new[] { "Visible", "Usable", "UsablePlanet" })
        {
            var conditionalName = $"{prefix}Conditional";
            var functionName = $"{prefix}Function";
            var parameterName = $"{prefix}Parameter";
            if (!row.ExtraFields.ContainsKey(conditionalName) &&
                !row.ExtraFields.ContainsKey(functionName) &&
                !row.ExtraFields.ContainsKey(parameterName))
            {
                continue;
            }

            ValidateAvailabilityBit(row, conditionalName, diagnostics);
            ValidateAvailabilityBit(row, parameterName, diagnostics);
            if (!row.ExtraFields.TryGetValue(functionName, out var function) ||
                !int.TryParse(function, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFunction) ||
                parsedFunction < 0)
            {
                AddForRow(diagnostics, row, "RULE-FUNCTION", ValidationSeverity.Error,
                    $"{functionName} must be a non-negative whole-number function or plot identifier.", functionName);
            }
        }
    }

    private static void ValidateAvailabilityBit(
        GalaxyMapRow row,
        string column,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        if (!row.ExtraFields.TryGetValue(column, out var value) || value is not "0" and not "1")
        {
            AddForRow(diagnostics, row, "RULE-FLAG", ValidationSeverity.Error,
                $"{column} must be independently set to 0 or 1.", column);
        }
    }

    private static void ValidatePlotPlanets(
        IEnumerable<PlotPlanetEntry> plotPlanets,
        IReadOnlyDictionary<int, Planet> planets,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        foreach (var plotPlanet in plotPlanets)
        {
            if (!planets.TryGetValue(plotPlanet.RowId, out var planet))
            {
                AddForRow(diagnostics, plotPlanet, "REF-PLOT-PLANET", ValidationSeverity.Error,
                    "PlotPlanet must have a Planet with the same Row ID.", "Row ID");
                continue;
            }

            if (plotPlanet.Code != planet.ActiveWorld)
            {
                AddForRow(diagnostics, plotPlanet, "PLOT-CODE-MISMATCH", ValidationSeverity.Error,
                    $"PlotPlanet Code {plotPlanet.Code} must equal the linked Planet's ActiveWorld {planet.ActiveWorld}.",
                    nameof(PlotPlanetEntry.Code));
            }

            if (plotPlanet.Name != planet.Name)
            {
                AddForRow(diagnostics, plotPlanet, "PLOT-NAME-MISMATCH", ValidationSeverity.Warning,
                    $"PlotPlanet Name {plotPlanet.Name} differs from the linked Planet Name {planet.Name}.",
                    nameof(PlotPlanetEntry.Name));
            }

            if (!string.Equals(plotPlanet.NameText, planet.NameText, StringComparison.Ordinal))
            {
                AddForRow(diagnostics, plotPlanet, "PLOT-NAMETEXT-MISMATCH", ValidationSeverity.Warning,
                    $"PlotPlanet NameText '{plotPlanet.NameText}' differs from the linked Planet '{planet.NameText}'.",
                    nameof(PlotPlanetEntry.NameText));
            }
        }
    }

    private static void ValidateMapLinks(
        IEnumerable<Planet> planets,
        IReadOnlyDictionary<int, MapEntry> maps,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        foreach (var references in planets.Where(planet => planet.MapRowId >= 0)
                     .GroupBy(planet => planet.MapRowId))
        {
            if (!maps.TryGetValue(references.Key, out var map))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(map.MapName))
            {
                AddForRow(diagnostics, map, "MAP-NAME-MISSING", ValidationSeverity.Warning,
                    "This referenced Map row has no Map package name.", nameof(MapEntry.MapName));
            }

            if (string.IsNullOrWhiteSpace(map.StartPoint))
            {
                AddForRow(diagnostics, map, "MAP-START-MISSING", ValidationSeverity.Warning,
                    "This referenced Map row has no StartPoint.", nameof(MapEntry.StartPoint));
            }

            if (references.Skip(1).Any())
            {
                AddForRow(diagnostics, map, "MAP-SHARED", ValidationSeverity.Warning,
                    $"Map row {map.RowId} is referenced by multiple Planets: " +
                    string.Join(", ", references.Select(planet => planet.RowId)) + ".",
                    "Row ID");
            }
        }
    }

    private static void ValidateRelays(
        IEnumerable<RelayConnection> relays,
        IEnumerable<Cluster> clusters,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        var clusterCodes = new Dictionary<int, List<Cluster>>();
        foreach (var cluster in clusters)
        {
            if (!GalaxyMapIdentity.TryEncodeClusterRelayEndpoint(cluster.Label, out var code))
            {
                continue;
            }

            if (!clusterCodes.TryGetValue(code, out var matching))
            {
                matching = [];
                clusterCodes[code] = matching;
            }

            matching.Add(cluster);
        }

        var pairs = new Dictionary<(int Lower, int Upper), RelayConnection>();
        foreach (var relay in relays)
        {
            ValidateRelayEndpoint(relay, relay.StartClusterEncoded, nameof(RelayConnection.StartClusterEncoded),
                clusterCodes, diagnostics);
            ValidateRelayEndpoint(relay, relay.EndClusterEncoded, nameof(RelayConnection.EndClusterEncoded),
                clusterCodes, diagnostics);

            if (relay.StartClusterEncoded == relay.EndClusterEncoded)
            {
                AddForRow(diagnostics, relay, "RELAY-SELF", ValidationSeverity.Error,
                    "A Relay cannot connect a Cluster to itself.", nameof(RelayConnection.EndClusterEncoded));
            }

            var pair = relay.StartClusterEncoded <= relay.EndClusterEncoded
                ? (relay.StartClusterEncoded, relay.EndClusterEncoded)
                : (relay.EndClusterEncoded, relay.StartClusterEncoded);
            if (pairs.TryGetValue(pair, out var existing))
            {
                AddForRow(diagnostics, relay, "RELAY-DUPLICATE-PAIR", ValidationSeverity.Error,
                    $"Relay row {existing.RowId} already connects cluster codes {pair.Item1} and {pair.Item2}.",
                    nameof(RelayConnection.StartClusterEncoded));
            }
            else
            {
                pairs[pair] = relay;
            }
        }
    }

    private static void ValidateRelayEndpoint(
        RelayConnection relay,
        int encoded,
        string column,
        IReadOnlyDictionary<int, List<Cluster>> clusterCodes,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        if (encoded <= 0 || encoded % 10_000 != 0)
        {
            AddForRow(diagnostics, relay, "RELAY-ENCODING", ValidationSeverity.Error,
                $"Relay endpoint {encoded} must be a positive Cluster label suffix multiplied by 10000.", column);
            return;
        }

        if (clusterCodes.TryGetValue(encoded, out var clusters) && clusters.Count == 1)
        {
            return;
        }

        var isKnownVanillaCluster04 = relay.RowId == 6 &&
            relay.StartClusterEncoded == 80_000 && relay.EndClusterEncoded == 40_000 &&
            encoded == 40_000 && (relay.Origin?.IsBaseGame ?? true);
        if (isKnownVanillaCluster04)
        {
            AddForRow(diagnostics, relay, "BASEGAME-RELAY-CLUSTER04", ValidationSeverity.Info,
                "Known immutable BASEGAME relay row: endpoint 40000 refers to the absent Cluster04 label.", column);
            return;
        }

        var message = clusters is { Count: > 1 }
            ? $"Relay endpoint {encoded} is ambiguous across {clusters.Count} Cluster labels."
            : $"Relay endpoint {encoded} does not resolve to an effective Cluster label.";
        AddForRow(diagnostics, relay, "RELAY-UNRESOLVED", ValidationSeverity.Error, message, column);
    }

    private static void ValidateCoordinate(
        GalaxyMapRow row,
        double value,
        string column,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        if (!double.IsFinite(value))
        {
            AddForRow(diagnostics, row, "TYPE-NONFINITE", ValidationSeverity.Error,
                $"{column} must be a finite number.", column);
        }
        else if (value is < 0 or > 1)
        {
            AddForRow(diagnostics, row, "COORDINATE-OFF-CANVAS", ValidationSeverity.Warning,
                $"{column}={GalaxyMapNumber.FormatDisplay(value)} lies outside the normalised 0-1 canvas " +
                "and may be clipped. Off-canvas placement can be intentional.", column);
        }
    }

    private static void ValidatePositiveFinite(
        GalaxyMapRow row,
        double value,
        string column,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        if (!double.IsFinite(value))
        {
            AddForRow(diagnostics, row, "TYPE-NONFINITE", ValidationSeverity.Error,
                $"{column} must be a finite number.", column);
        }
        else if (value <= 0)
        {
            AddForRow(diagnostics, row, "VALUE-NONPOSITIVE-SCALE", ValidationSeverity.Warning,
                $"{column} is {GalaxyMapNumber.FormatDisplay(value)}; zero or negative sizes are normally invisible.",
                column);
        }
    }

    private static void ValidateNonNegativeFinite(
        GalaxyMapRow row,
        double value,
        string column,
        ICollection<ValidationDiagnostic> diagnostics)
    {
        if (!double.IsFinite(value))
        {
            AddForRow(diagnostics, row, "TYPE-NONFINITE", ValidationSeverity.Error,
                $"{column} must be a finite number.", column);
        }
        else if (value < 0)
        {
            AddForRow(diagnostics, row, "VALUE-NEGATIVE-SCALE", ValidationSeverity.Warning,
                $"{column} is {GalaxyMapNumber.FormatDisplay(value)}; negative sizes are normally invisible.",
                column);
        }
    }

    private static Dictionary<int, T> FirstById<T>(IEnumerable<T> rows) where T : GalaxyMapRow
    {
        var result = new Dictionary<int, T>();
        foreach (var row in rows)
        {
            result.TryAdd(row.RowId, row);
        }

        return result;
    }

    private static void AddForRow(
        ICollection<ValidationDiagnostic> diagnostics,
        GalaxyMapRow row,
        string code,
        ValidationSeverity severity,
        string message,
        string column,
        string? moduleTag = null)
        => diagnostics.Add(new ValidationDiagnostic(
            code,
            severity,
            message,
            moduleTag ?? row.Origin?.ModuleTag ?? GalaxyMapModule.BaseGameTag,
            row.Table.ToString(),
            row.RowId,
            column,
            row.CsvSnapshot?.SourceRowNumber));

    private static IReadOnlyList<ValidationDiagnostic> Order(IEnumerable<ValidationDiagnostic> diagnostics)
        => diagnostics.OrderByDescending(diagnostic => diagnostic.Severity)
            .ThenBy(diagnostic => diagnostic.ModuleTag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(diagnostic => diagnostic.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(diagnostic => diagnostic.RowId)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToArray();

}
