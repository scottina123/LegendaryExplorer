using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public sealed record CloneRowChange(
    int RowId,
    string Label,
    int Name,
    string NameText,
    bool CloneChildren);

public sealed record CloneDefaults(int RowId, string Label);

public sealed record PlanetCreationChange(
    PlanetCreationTemplate Template,
    string NameText,
    int Name,
    double Scale,
    LandableDestinationChange? Destination);

public sealed record MoveDestinationChoice(
    int RowId,
    string DisplayName,
    string Detail,
    string CurrentLabel,
    string ResultingLabel);

public sealed class RowAuthoringWorkflow(
    EditorSession session,
    EditSessionService edits,
    InspectorEditWorkflow inspectorEdits)
{
    public CloneDefaults GetCloneDefaults(GalaxyMapRow source)
    {
        var workspace = session.Workspace ?? throw new InvalidOperationException("A workspace is required.");
        var active = session.ActiveModule ?? throw new InvalidOperationException(
            "Create or select a writable module before cloning content.");
        var suggestedId = new ModuleIdAllocator(workspace).NextAvailable(active, source.Table);
        var prefix = source is Cluster ? "Cluster" : source is GalaxySystem ? "System" : "Planet";
        var labels = source switch
        {
            GalaxySystem system => system.Cluster?.Systems.Select(row => row.Label) ?? [],
            Planet planet => planet.System?.Planets.Select(row => row.Label) ?? [],
            _ => session.Document?.Clusters.Select(row => row.Label) ?? []
        };
        return new CloneDefaults(suggestedId, NextLabel(prefix, labels));
    }

    public string GetSuggestedClusterLabel()
    {
        var workspace = session.Workspace ?? throw new InvalidOperationException("A workspace is required.");
        return NextLabel(
            "Cluster",
            workspace.Layers.SelectMany(layer => layer.Clusters).Select(cluster => cluster.Label));
    }

    public WorkflowResult CreateCluster(string label, HistoryPresentationState presentation)
        => CreateFactoryRow(
            GalaxyMapTable.Cluster,
            factory => factory.CreateCluster(label: label),
            rowId => new NavigationTarget(rowId, null),
            "Created a new Cluster in the active module.",
            presentation);

    public WorkflowResult CreateSystem(Cluster cluster, HistoryPresentationState presentation)
        => CreateFactoryRow(
            GalaxyMapTable.System,
            factory => factory.CreateSystem(cluster),
            rowId => new NavigationTarget(cluster.RowId, rowId),
            "Created a new System in the active module.",
            presentation);

    public WorkflowResult CreatePlanet(
        GalaxySystem system,
        PlanetCreationChange request,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace?.ActiveLayer is not { } layer)
        {
            return WorkflowResult.Failure("Create or open a writable module before adding rows.");
        }

        try
        {
            var allocator = new ModuleIdAllocator(workspace);
            var planetId = allocator.NextAvailable(layer.Module, GalaxyMapTable.Planet);
            var planetKey = new GalaxyMapRowKey(GalaxyMapTable.Planet, planetId);
            var tables = new HashSet<GalaxyMapTable> { GalaxyMapTable.Planet };
            var keys = new HashSet<GalaxyMapRowKey> { planetKey };
            int? mapId = null;

            if (request.Template != PlanetCreationTemplate.AsteroidBelt && request.Destination is { } destination)
            {
                mapId = allocator.NextAvailable(layer.Module, GalaxyMapTable.Map);
                keys.Add(new GalaxyMapRowKey(GalaxyMapTable.Map, mapId.Value));
                tables.Add(GalaxyMapTable.Map);
                if (destination.AddPlotPlanet)
                {
                    keys.Add(new GalaxyMapRowKey(GalaxyMapTable.PlotPlanet, planetId));
                    tables.Add(GalaxyMapTable.PlotPlanet);
                }
            }

            return edits.ExecuteMutation(new EditMutationRequest(
                keys,
                tables,
                () =>
                {
                    var effective = new GalaxyMapRowFactory(workspace).CreatePlanet(
                        system, request.Template, request.NameText, request.Name, request.Scale);
                    var planet = (Planet)(layer.Find(effective.Key) ??
                        throw new InvalidOperationException("The new Planet row was not added to the active module."));

                    if (mapId is not { } linkedMapId || request.Destination is not { } linkedDestination)
                    {
                        return;
                    }

                    var map = new MapEntry
                    {
                        RowId = linkedMapId,
                        MapName = linkedDestination.MapName,
                        StartPoint = linkedDestination.StartPoint
                    };
                    GalaxyMapRowAuthoring.PrepareNewRow(layer, map);
                    layer.Upsert(map);
                    planet.MapRowId = linkedMapId;
                    planet.Event = linkedDestination.Event;
                    planet.ButtonLabel = linkedDestination.ButtonLabel;
                    GalaxyMapRowAuthoring.MarkDirty(planet, "Map", "Event", "ButtonLabel");
                    if (linkedDestination.AddPlotPlanet)
                    {
                        layer.Upsert(GalaxyMapRowAuthoring.CreatePlotPlanetRow(layer, planet));
                    }
                },
                presentation with
                {
                    SelectionKey = planetKey,
                    Navigation = new NavigationTarget(system.ClusterRowId, system.RowId)
                },
                $"created {request.Template} Planet row {planetId}.",
                IsStructural: true));
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    public WorkflowResult Clone(
        GalaxyMapRow source,
        CloneRowChange request,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        var active = session.ActiveModule;
        if (workspace?.ActiveLayer is not { } layer || active is null)
        {
            return WorkflowResult.Failure("A writable active module is required.");
        }

        try
        {
            ValidateNewId(source.Table, request.RowId, workspace, active);
            if (string.IsNullOrWhiteSpace(request.Label) || string.IsNullOrWhiteSpace(request.NameText))
            {
                throw new InvalidOperationException("A unique label and display name are required.");
            }
            ValidateLabel(source, request.Label);
            if (request.CloneChildren && source is Cluster sourceWithSystems)
            {
                if (sourceWithSystems.Systems.Count > GalaxyMapIdentityLimits.MaxSystemLabel)
                {
                    throw new InvalidOperationException(
                        "This Cluster cannot be cloned with children because the game supports only System01-System09 per Cluster.");
                }
                if (sourceWithSystems.Systems.Any(system =>
                        system.Planets.Count > GalaxyMapIdentityLimits.MaxPlanetLabel))
                {
                    throw new InvalidOperationException(
                        "This Cluster cannot be cloned with children because the game supports only Planet01-Planet99 per System.");
                }
            }
            if (request.CloneChildren && source is GalaxySystem sourceWithPlanets &&
                sourceWithPlanets.Planets.Count > GalaxyMapIdentityLimits.MaxPlanetLabel)
            {
                throw new InvalidOperationException(
                    "This System cannot be cloned with children because the game supports only Planet01-Planet99 per System.");
            }

            var duplicateLabel = source switch
            {
                Cluster => session.Document!.Clusters.Any(row =>
                    string.Equals(row.Label, request.Label, StringComparison.OrdinalIgnoreCase)),
                GalaxySystem system => system.Cluster!.Systems.Any(row =>
                    string.Equals(row.Label, request.Label, StringComparison.OrdinalIgnoreCase)),
                Planet planet => planet.System!.Planets.Any(row =>
                    string.Equals(row.Label, request.Label, StringComparison.OrdinalIgnoreCase)),
                _ => false
            };
            if (duplicateLabel)
            {
                throw new InvalidOperationException($"The label '{request.Label}' is already used in this scope.");
            }

            var planned = new Dictionary<GalaxyMapTable, HashSet<int>>();
            int Next(GalaxyMapTable table)
            {
                var range = active.Reservations.GetRange(table) ??
                            throw new InvalidOperationException($"No reserved {table} range exists.");
                var occupied = workspace.Layers.SelectMany(layer => layer.Rows(table)).Select(row => row.RowId).ToHashSet();
                if (planned.TryGetValue(table, out var already))
                {
                    occupied.UnionWith(already);
                }
                for (long candidate = range.Start; candidate <= range.End; candidate++)
                {
                    if (occupied.Contains((int)candidate))
                    {
                        continue;
                    }
                    (planned.GetValueOrDefault(table) ?? (planned[table] = [])).Add((int)candidate);
                    return (int)candidate;
                }
                throw new InvalidOperationException($"The reserved {table} range is exhausted.");
            }

            var created = new List<GalaxyMapRow>();
            GalaxyMapRow root;
            if (source is Cluster sourceCluster)
            {
                var cluster = GalaxyMapRowCloner.Clone(sourceCluster);
                SetIdentity(cluster, request);
                GalaxyMapRowAuthoring.PrepareNewRow(layer, cluster);
                created.Add(cluster);
                root = cluster;
                if (request.CloneChildren)
                {
                    var systemIndex = 1;
                    foreach (var sourceSystem in sourceCluster.Systems)
                    {
                        var system = GalaxyMapRowCloner.Clone(sourceSystem);
                        system.RowId = Next(GalaxyMapTable.System);
                        system.Label = $"System{systemIndex++:D2}";
                        system.ClusterRowId = cluster.RowId;
                        GalaxyMapRowAuthoring.PrepareNewRow(layer, system);
                        created.Add(system);
                        ClonePlanets(sourceSystem, system, cluster.Label, created, layer, Next);
                    }
                }
            }
            else if (source is GalaxySystem sourceSystem)
            {
                var system = GalaxyMapRowCloner.Clone(sourceSystem);
                SetIdentity(system, request);
                GalaxyMapRowAuthoring.PrepareNewRow(layer, system);
                created.Add(system);
                root = system;
                if (request.CloneChildren)
                {
                    ClonePlanets(sourceSystem, system, sourceSystem.Cluster?.Label ?? string.Empty, created, layer, Next);
                }
            }
            else if (source is Planet sourcePlanet)
            {
                var planet = GalaxyMapRowCloner.Clone(sourcePlanet);
                SetIdentity(planet, request);
                planet.ActiveWorld = DeriveActiveWorld(
                    sourcePlanet.System?.Cluster?.Label,
                    sourcePlanet.System?.Label,
                    planet.Label);
                ClonePlanetLinks(sourcePlanet, planet, created, layer, Next);
                GalaxyMapRowAuthoring.PrepareNewRow(layer, planet);
                created.Insert(0, planet);
                root = planet;
            }
            else
            {
                throw new InvalidOperationException("Only Clusters, Systems and Planets can be cloned.");
            }

            var navigation = root switch
            {
                Cluster cluster => new NavigationTarget(cluster.RowId, null),
                GalaxySystem system => new NavigationTarget(system.ClusterRowId, system.RowId),
                Planet planet => new NavigationTarget((source as Planet)?.System?.ClusterRowId, planet.SystemRowId),
                _ => NavigationTarget.Galaxy
            };
            return edits.ExecuteMutation(new EditMutationRequest(
                created.Select(row => row.Key).ToArray(),
                created.Select(row => row.Table).ToArray(),
                () =>
                {
                    foreach (var row in created)
                    {
                        layer.Upsert(row);
                    }
                },
                presentation with { SelectionKey = root.Key, Navigation = navigation },
                $"Cloned {source.Table} row {source.RowId} as row {root.RowId} with {created.Count - 1} child/link row(s).",
                IsStructural: true));
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    public IReadOnlyList<MoveDestinationChoice> GetMoveDestinations(GalaxyMapRow source)
    {
        var document = session.Document ?? throw new InvalidOperationException("A document is required.");
        return source switch
        {
            GalaxySystem system => document.Clusters
                .Where(cluster => cluster.RowId != system.ClusterRowId)
                .OrderBy(cluster => cluster.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(cluster => (Cluster: cluster, ResultingLabel: FindAvailableScopedLabel(
                    "System", system.Label, cluster.Systems.Select(candidate => candidate.Label))))
                .Where(option => option.ResultingLabel is not null &&
                    TryLabelSuffix(option.Cluster.Label, "Cluster", out var clusterNumber) && clusterNumber > 0)
                .Select(option => new MoveDestinationChoice(
                    option.Cluster.RowId,
                    option.Cluster.DisplayName,
                    $"{option.Cluster.DisplayName} • Cluster row {option.Cluster.RowId}",
                    system.Label,
                    option.ResultingLabel!))
                .ToArray(),
            Planet planet => document.Systems
                .Where(system => system.RowId != planet.SystemRowId)
                .OrderBy(system => system.Cluster?.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(system => system.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(system => (System: system, ResultingLabel: FindAvailableScopedLabel(
                    "Planet", planet.Label, system.Planets.Select(candidate => candidate.Label))))
                .Where(option => option.ResultingLabel is not null && option.System.Cluster is not null &&
                    TryCalculateActiveWorld(
                        option.System.Cluster.Label,
                        option.System.Label,
                        option.ResultingLabel!,
                        out _))
                .Select(option => new MoveDestinationChoice(
                    option.System.RowId,
                    $"{option.System.Cluster?.DisplayName ?? "Missing Cluster"} / {option.System.DisplayName}",
                    $"{option.System.DisplayName} • System row {option.System.RowId}",
                    planet.Label,
                    option.ResultingLabel!))
                .ToArray(),
            _ => []
        };
    }

    public WorkflowResult Move(
        GalaxyMapRow source,
        int destinationParentRowId,
        GalaxyMapModule target,
        HistoryPresentationState presentation)
    {
        var document = session.Document;
        var workspace = session.Workspace;
        if (document is null || workspace is null || source is not (GalaxySystem or Planet))
        {
            return WorkflowResult.Failure("Only Systems and Planets can be moved between parents.");
        }

        var layer = workspace.ModuleLayers.FirstOrDefault(candidate =>
            string.Equals(candidate.Module.Tag, target.Tag, StringComparison.OrdinalIgnoreCase));
        if (layer is null || layer.Module.IsReadOnly || layer.Module.IsBaseGame)
        {
            return WorkflowResult.Failure("Choose a writable module before moving this row.");
        }

        try
        {
            MoveParentChange request;
            switch (source)
            {
                case GalaxySystem system when document.ClustersByRowId.TryGetValue(destinationParentRowId, out var cluster):
                {
                    if (system.ClusterRowId == cluster.RowId)
                    {
                        return WorkflowResult.Failure($"{system.DisplayName} is already in {cluster.DisplayName}.");
                    }

                    var label = AvailableScopedLabel(
                        "System",
                        system.Label,
                        cluster.Systems.Where(candidate => candidate.RowId != system.RowId)
                            .Select(candidate => candidate.Label));
                    if (!TryLabelSuffix(cluster.Label, "Cluster", out var clusterNumber) || clusterNumber <= 0 ||
                        clusterNumber > GalaxyMapIdentityLimits.MaxClusterLabel ||
                        !TryLabelSuffix(label, "System", out var systemNumber) ||
                        systemNumber is <= 0 or > GalaxyMapIdentityLimits.MaxSystemLabel ||
                        system.Planets.Any(planet => !TryCalculateActiveWorld(
                            cluster.Label,
                            label,
                            planet.Label,
                            out _)))
                    {
                        return WorkflowResult.Failure(
                            "The destination labels cannot produce valid ActiveWorld IDs for every child Planet.");
                    }
                    request = new MoveParentChange(
                        cluster.RowId,
                        label,
                        new NavigationTarget(cluster.RowId, null),
                        MoveSummary(system.DisplayName, cluster.DisplayName, system.Label, label));
                    break;
                }
                case Planet planet when document.SystemsByRowId.TryGetValue(destinationParentRowId, out var system):
                {
                    if (system.Cluster is null)
                    {
                        return WorkflowResult.Failure($"System row {system.RowId} has no valid parent Cluster.");
                    }
                    if (planet.SystemRowId == system.RowId)
                    {
                        return WorkflowResult.Failure($"{planet.DisplayName} is already in {system.DisplayName}.");
                    }

                    var label = AvailableScopedLabel(
                        "Planet",
                        planet.Label,
                        system.Planets.Where(candidate => candidate.RowId != planet.RowId)
                            .Select(candidate => candidate.Label));
                    if (!TryCalculateActiveWorld(system.Cluster.Label, system.Label, label, out _))
                    {
                        return WorkflowResult.Failure(
                            "The destination labels cannot produce a valid ActiveWorld ID for this Planet.");
                    }
                    request = new MoveParentChange(
                        system.RowId,
                        label,
                        new NavigationTarget(system.ClusterRowId, system.RowId),
                        MoveSummary(planet.DisplayName, system.DisplayName, planet.Label, label));
                    break;
                }
                default:
                    return WorkflowResult.Failure(
                        $"Destination row {destinationParentRowId} is not a valid parent for this {source.Table}.");
            }

            workspace.SetActiveModule(layer.Module);
            var result = inspectorEdits.ApplyEdit(
                source,
                InspectorEditWorkflow.MoveParentProperty,
                request,
                layer.Module,
                presentation);
            return result.Result ?? WorkflowResult.Failure("The row move was not handled.");
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    public WorkflowResult StageCoordinates(
        GalaxyMapRow row,
        GalaxyMapLayer layer,
        double x,
        double y,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace is null)
        {
            return WorkflowResult.Failure("A workspace is required before moving map coordinates.");
        }

        workspace.SetActiveModule(layer.Module);
        var replacement = layer.Find(row.Key) is { } physical
            ? GalaxyMapRowCloner.Clone(physical)
            : GalaxyMapRowCloner.CloneForOverride(row, layer.Module);
        SetCoordinates(replacement, x, y);
        GalaxyMapRowAuthoring.MarkDirty(replacement, "X", "Y");
        return edits.ExecuteMutation(new EditMutationRequest(
            [replacement.Key],
            [replacement.Table],
            () => layer.Upsert(replacement),
            presentation with { SelectionKey = replacement.Key },
            $"Moved {RowDisplayName(replacement)} to X {x:0.00}, Y {y:0.00}.",
            IsStructural: false));
    }

    public WorkflowResult Delete(GalaxyMapRow row, HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace is null)
        {
            return WorkflowResult.Failure("A workspace is required before deleting rows.");
        }

        // The hierarchy exposes the effective (highest-mounted) row, but row
        // actions author against the module the user explicitly made active.
        // Prefer its same-key physical row when one exists; otherwise retain
        // the existing owning-layer behaviour for unambiguous rows.
        var layer = workspace.ActiveLayer is { } activeLayer && activeLayer.Find(row.Key) is not null
            ? activeLayer
            : WritableOwningLayer(row);
        if (layer is null)
        {
            return WorkflowResult.Failure(
                "BASEGAME and read-only module rows cannot be deleted: the 2DA partial format has no safe deletion tombstone.");
        }

        workspace.SetActiveModule(layer.Module);
        var rows = new List<GalaxyMapRow> { layer.Find(row.Key)! };
        var removesEntity = workspace.GetOverrideChain(row.Key).Count == 1;
        if (removesEntity && row is Cluster cluster)
        {
            var systems = layer.Systems.Where(system => system.ClusterRowId == cluster.RowId).ToArray();
            rows.AddRange(systems);
            rows.AddRange(layer.Planets.Where(planet => systems.Any(system => system.RowId == planet.SystemRowId)));
            if (session.Document?.TryGetRelayCode(cluster, out var relayCode, out _) == true)
            {
                rows.AddRange(layer.Relays.Where(relay =>
                    relay.StartClusterEncoded == relayCode || relay.EndClusterEncoded == relayCode));
            }
        }
        else if (removesEntity && row is GalaxySystem system)
        {
            rows.AddRange(layer.Planets.Where(planet => planet.SystemRowId == system.RowId));
        }

        foreach (var planet in rows.OfType<Planet>().ToArray())
        {
            if (layer.PlotPlanets.FirstOrDefault(plot => plot.RowId == planet.RowId) is { } plot)
            {
                rows.Add(plot);
            }
            if (layer.Maps.FirstOrDefault(map => map.RowId == planet.MapRowId) is { } map &&
                !layer.Planets.Any(other => other.RowId != planet.RowId && other.MapRowId == map.RowId))
            {
                rows.Add(map);
            }
        }

        var navigation = row is Cluster
            ? NavigationTarget.Galaxy
            : new NavigationTarget(
                (row as GalaxySystem)?.ClusterRowId ?? (row as Planet)?.System?.ClusterRowId,
                null);
        return edits.ExecuteMutation(new EditMutationRequest(
            rows.Select(item => item.Key).ToArray(),
            rows.Select(item => item.Table).Distinct().ToArray(),
            () =>
            {
                foreach (var item in rows.Distinct().ToArray())
                {
                    layer.Remove(item);
                }
            },
            presentation with { SelectionKey = null, Navigation = navigation },
            $"Deleted {row.Table} row {row.RowId} and {rows.Count - 1} owned child/link row(s).",
            IsStructural: true));
    }

    public GalaxyMapLayer? WritableOwningLayer(GalaxyMapRow row)
        => session.Workspace?.ModuleLayers.FirstOrDefault(layer =>
            !layer.Module.IsReadOnly &&
            layer.Find(row.Key) is not null &&
            string.Equals(layer.Module.Tag, row.Origin?.ModuleTag, StringComparison.OrdinalIgnoreCase));

    public GalaxyMapLayer? MovableOwningLayer(GalaxyMapRow row)
        => WritableOwningLayer(row);

    private WorkflowResult CreateFactoryRow(
        GalaxyMapTable table,
        Func<GalaxyMapRowFactory, GalaxyMapRow> create,
        Func<int, NavigationTarget> navigation,
        string message,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace?.ActiveLayer is not { } layer)
        {
            return WorkflowResult.Failure("Create or open a writable module before adding rows.");
        }

        try
        {
            var rowId = new ModuleIdAllocator(workspace).NextAvailable(layer.Module, table);
            var key = new GalaxyMapRowKey(table, rowId);
            return edits.ExecuteMutation(new EditMutationRequest(
                [key],
                [table],
                () => create(new GalaxyMapRowFactory(workspace)),
                presentation with { SelectionKey = key, Navigation = navigation(rowId) },
                message,
                IsStructural: true));
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    private static void ClonePlanets(
        GalaxySystem source,
        GalaxySystem target,
        string clusterLabel,
        List<GalaxyMapRow> created,
        GalaxyMapLayer layer,
        Func<GalaxyMapTable, int> next)
    {
        var planetIndex = 1;
        foreach (var old in source.Planets)
        {
            var planet = GalaxyMapRowCloner.Clone(old);
            planet.RowId = next(GalaxyMapTable.Planet);
            planet.Label = $"Planet{planetIndex++:D2}";
            planet.SystemRowId = target.RowId;
            planet.ActiveWorld = DeriveActiveWorld(clusterLabel, target.Label, planet.Label);
            ClonePlanetLinks(old, planet, created, layer, next);
            GalaxyMapRowAuthoring.PrepareNewRow(layer, planet);
            created.Add(planet);
        }
    }

    private static void ClonePlanetLinks(
        Planet source,
        Planet target,
        List<GalaxyMapRow> created,
        GalaxyMapLayer layer,
        Func<GalaxyMapTable, int> next)
    {
        // Appearance data may be copied as a visual base, but a cloned Planet
        // must never inherit the source row's material-instance identity.
        target.SetExtraField("Shader", string.Empty);
        if (source.PlotPlanet is { } oldPlot)
        {
            var plot = GalaxyMapRowCloner.Clone(oldPlot);
            plot.RowId = target.RowId;
            plot.Code = target.ActiveWorld;
            plot.Name = target.Name;
            plot.NameText = target.NameText;
            GalaxyMapRowAuthoring.PrepareNewRow(layer, plot);
            created.Add(plot);
        }
        if (source.LinkedMap is { } oldMap)
        {
            var map = GalaxyMapRowCloner.Clone(oldMap);
            map.RowId = next(GalaxyMapTable.Map);
            target.MapRowId = map.RowId;
            GalaxyMapRowAuthoring.PrepareNewRow(layer, map);
            created.Add(map);
        }
        else
        {
            target.MapRowId = -1;
        }
    }

    private static void ValidateNewId(
        GalaxyMapTable table,
        int rowId,
        GalaxyMapWorkspace workspace,
        GalaxyMapModule active)
    {
        var range = active.Reservations.GetRange(table);
        if (range is null || !range.Value.Contains(rowId))
        {
            throw new InvalidOperationException(
                $"Row ID {rowId} is outside {active.Tag}'s reserved {table} range ({range?.ToString() ?? "none"}).");
        }
        if (workspace.Layers.SelectMany(layer => layer.Rows(table)).Any(row => row.RowId == rowId))
        {
            throw new InvalidOperationException($"{table} row ID {rowId} is already in use.");
        }
    }

    private static void SetIdentity(GalaxyMapRow row, CloneRowChange request)
    {
        row.RowId = request.RowId;
        switch (row)
        {
            case Cluster cluster:
                cluster.Label = request.Label;
                cluster.Name = request.Name;
                cluster.NameText = request.NameText;
                break;
            case GalaxySystem system:
                system.Label = request.Label;
                system.Name = request.Name;
                system.NameText = request.NameText;
                break;
            case Planet planet:
                planet.Label = request.Label;
                planet.Name = request.Name;
                planet.NameText = request.NameText;
                break;
        }
    }

    private static void ValidateLabel(GalaxyMapRow row, string label)
    {
        var (prefix, maximum) = row switch
        {
            Cluster => ("Cluster", GalaxyMapIdentityLimits.MaxClusterLabel),
            GalaxySystem => ("System", GalaxyMapIdentityLimits.MaxSystemLabel),
            Planet => ("Planet", GalaxyMapIdentityLimits.MaxPlanetLabel),
            _ => throw new InvalidOperationException($"{row.Table} rows do not use a numbered galaxy-map label.")
        };
        var minimum = row is Cluster ? GalaxyMapIdentityLimits.MinAuthoredClusterLabel : 1;
        if (!TryLabelSuffix(label, prefix, out var suffix) || suffix < minimum || suffix > maximum)
        {
            throw new InvalidOperationException(
                $"Use a {prefix} label from {prefix}{minimum:D2} to {prefix}{maximum:D2}.");
        }
    }

    private static string NextLabel(string prefix, IEnumerable<string> labels)
    {
        var maximum = GalaxyMapIdentityLimits.MaxLabel(prefix);
        var minimum = string.Equals(prefix, "Cluster", StringComparison.OrdinalIgnoreCase)
            ? GalaxyMapIdentityLimits.MinAuthoredClusterLabel
            : 1;
        var used = labels
            .Select(label => TryLabelSuffix(label, prefix, out var suffix) ? suffix : 0)
            .Where(suffix => suffix is > 0 && suffix <= maximum)
            .ToHashSet();
        for (var suffix = minimum; suffix <= maximum; suffix++)
        {
            if (!used.Contains(suffix))
            {
                return $"{prefix}{suffix:D2}";
            }
        }
        throw new InvalidOperationException(
            $"No {prefix} label is available in the supported {prefix}{minimum:D2}-{prefix}{maximum:D2} range.");
    }

    private static int DeriveActiveWorld(string? cluster, string? system, string planet)
    {
        if (GalaxyMapIdentity.TryDeriveActiveWorld(cluster, system, planet, out var activeWorld))
        {
            return activeWorld;
        }
        throw new InvalidOperationException(
            "The Cluster/System/Planet label chain is outside the game's supported ActiveWorld range.");
    }

    private static string MoveSummary(string item, string destination, string oldLabel, string newLabel)
        => string.Equals(oldLabel, newLabel, StringComparison.OrdinalIgnoreCase)
            ? $"Moved {item} to {destination}."
            : $"Moved {item} to {destination} and changed {oldLabel} to {newLabel} to avoid a label collision.";

    private static string AvailableScopedLabel(string prefix, string preferred, IEnumerable<string> existingLabels)
        => FindAvailableScopedLabel(prefix, preferred, existingLabels) ??
           throw new InvalidOperationException(
               $"The destination already uses all {GalaxyMapIdentityLimits.MaxLabel(prefix)} available {prefix} labels.");

    private static string? FindAvailableScopedLabel(
        string prefix,
        string preferred,
        IEnumerable<string> existingLabels)
    {
        var maximum = GalaxyMapIdentityLimits.MaxLabel(prefix);
        var used = existingLabels
            .Select(label => TryLabelSuffix(label, prefix, out var suffix) ? suffix : 0)
            .Where(suffix => suffix is > 0 && suffix <= maximum)
            .ToHashSet();
        if (TryLabelSuffix(preferred, prefix, out var preferredSuffix) &&
            preferredSuffix > 0 && preferredSuffix <= maximum && !used.Contains(preferredSuffix))
        {
            return preferred;
        }

        for (var suffix = 1; suffix <= maximum; suffix++)
        {
            if (!used.Contains(suffix))
            {
                return $"{prefix}{suffix:D2}";
            }
        }
        return null;
    }

    private static bool TryCalculateActiveWorld(
        string clusterLabel,
        string systemLabel,
        string planetLabel,
        out int activeWorld)
        => GalaxyMapIdentity.TryDeriveActiveWorld(
            clusterLabel, systemLabel, planetLabel, out activeWorld);

    private static bool TryLabelSuffix(string label, string prefix, out int suffix)
    {
        suffix = 0;
        if (!Enum.TryParse<GalaxyMapIdentityKind>(prefix, ignoreCase: true, out var kind))
        {
            return false;
        }

        var parsed = GalaxyMapIdentity.ParseLabelSyntax(label, kind);
        suffix = parsed.Suffix;
        return parsed.Status == GalaxyMapLabelParseStatus.Parsed;
    }

    private static void SetCoordinates(GalaxyMapRow row, double x, double y)
    {
        switch (row)
        {
            case Cluster cluster:
                cluster.X = x;
                cluster.Y = y;
                break;
            case GalaxySystem system:
                system.X = x;
                system.Y = y;
                break;
            case Planet planet:
                planet.X = x;
                planet.Y = y;
                break;
            default:
                throw new ArgumentException($"{row.Table} rows do not have map coordinates.", nameof(row));
        }
    }

    private static string RowDisplayName(GalaxyMapRow row) => row switch
    {
        Cluster cluster => cluster.DisplayName,
        GalaxySystem system => system.DisplayName,
        Planet planet => planet.DisplayName,
        _ => $"{row.Table} row {row.RowId}"
    };

    private static bool IsExpectedOperationFailure(Exception exception)
        => exception is InvalidOperationException or ArgumentException or OverflowException;
}
