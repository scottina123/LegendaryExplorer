using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.Workflows.Editing;

public sealed record LandableDestinationChange(
    string MapName,
    string StartPoint,
    string Event,
    int? ButtonLabel,
    bool AddPlotPlanet);

public sealed class PlanetRelationshipWorkflow(EditorSession session, EditSessionService edits)
{
    public WorkflowResult AddPlotPlanet(
        Planet planet,
        GalaxyMapModule target,
        HistoryPresentationState presentation)
    {
        if (session.Workspace is null || planet.PlotPlanet is not null)
        {
            return WorkflowResult.Failure("Create or open a writable module before adding PlotPlanet data.");
        }

        session.Workspace.SetActiveModule(target);
        var layer = session.Workspace.ActiveLayer!;
        var plot = GalaxyMapRowAuthoring.CreatePlotPlanetRow(layer, planet);
        return edits.ExecuteMutation(new EditMutationRequest(
            [plot.Key],
            [GalaxyMapTable.PlotPlanet],
            () => layer.Upsert(plot),
            presentation,
            $"Added PlotPlanet row {plot.RowId} to the active module.",
            IsStructural: true));
    }

    public WorkflowResult ConfigureLandableDestination(
        Planet planet,
        GalaxyMapModule target,
        LandableDestinationChange request,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace is null)
        {
            return WorkflowResult.Failure("A workspace is required to configure a landable destination.");
        }

        if (planet.OrbitRing == 2)
        {
            return WorkflowResult.Failure("Asteroid belts cannot be configured as landable destinations.");
        }

        workspace.SetActiveModule(target);
        var layer = workspace.ActiveLayer!;
        try
        {
            var planetOverride = layer.Find(planet.Key) is Planet existingPlanet
                ? GalaxyMapRowCloner.Clone(existingPlanet)
                : GalaxyMapRowCloner.CloneForOverride(planet, target) as Planet
                  ?? throw new InvalidOperationException("The Planet override could not be created.");
            planetOverride.Origin = new GalaxyMapRowOrigin(target, workspace.GetOverrideChain(planet.Key).Any(candidate =>
                !string.Equals(candidate.Origin?.ModuleTag, target.Tag, StringComparison.OrdinalIgnoreCase)));

            MapEntry map;
            var mapIsShared = planet.LinkedMap is not null &&
                              session.Document?.Planets.Any(other =>
                                  other.RowId != planet.RowId && other.MapRowId == planet.MapRowId) == true;
            if (planet.LinkedMap is { } linkedMap && !mapIsShared)
            {
                map = layer.Find(linkedMap.Key) is MapEntry existingMap
                    ? GalaxyMapRowCloner.Clone(existingMap)
                    : GalaxyMapRowCloner.CloneForOverride(linkedMap, target) as MapEntry
                      ?? throw new InvalidOperationException("The Map override could not be created.");
                map.Origin = new GalaxyMapRowOrigin(target, workspace.GetOverrideChain(linkedMap.Key).Any(candidate =>
                    !string.Equals(candidate.Origin?.ModuleTag, target.Tag, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                var mapId = new ModuleIdAllocator(workspace).NextAvailable(target, GalaxyMapTable.Map);
                map = planet.LinkedMap is { } sharedMap ? GalaxyMapRowCloner.Clone(sharedMap) : new MapEntry();
                map.RowId = mapId;
                GalaxyMapRowAuthoring.PrepareNewRow(layer, map);
            }

            map.MapName = request.MapName;
            map.StartPoint = request.StartPoint;
            GalaxyMapRowAuthoring.MarkDirty(map, "Map", "StartPoint");
            planetOverride.MapRowId = map.RowId;
            planetOverride.Event = request.Event;
            planetOverride.ButtonLabel = request.ButtonLabel;
            GalaxyMapRowAuthoring.MarkDirty(planetOverride, "Map", "Event", "ButtonLabel");

            PlotPlanetEntry? plot = null;
            if (request.AddPlotPlanet && planet.PlotPlanet is null)
            {
                plot = GalaxyMapRowAuthoring.CreatePlotPlanetRow(layer, planetOverride);
            }

            var affected = new List<GalaxyMapRowKey> { planet.Key, map.Key };
            var tables = new List<GalaxyMapTable> { GalaxyMapTable.Planet, GalaxyMapTable.Map };
            if (plot is not null)
            {
                affected.Add(plot.Key);
                tables.Add(GalaxyMapTable.PlotPlanet);
            }

            return edits.ExecuteMutation(new EditMutationRequest(
                affected,
                tables,
                () =>
                {
                    layer.Upsert(map);
                    layer.Upsert(planetOverride);
                    if (plot is not null)
                    {
                        layer.Upsert(plot);
                    }
                },
                presentation,
                $"Configured landable destination for Planet row {planet.RowId}.",
                IsStructural: true));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    public WorkflowResult AddLinkedMap(
        Planet planet,
        GalaxyMapModule target,
        HistoryPresentationState presentation)
    {
        var workspace = session.Workspace;
        if (workspace is null || planet.LinkedMap is not null)
        {
            return WorkflowResult.Failure("Create or open a writable module before adding a Map link.");
        }

        workspace.SetActiveModule(target);
        var layer = workspace.ActiveLayer!;
        try
        {
            var mapId = new ModuleIdAllocator(workspace).NextAvailable(target, GalaxyMapTable.Map);
            var map = new MapEntry
            {
                RowId = mapId,
                MapName = "BIOA_NEW_MAP",
                StartPoint = string.Empty
            };
            GalaxyMapRowAuthoring.PrepareNewRow(layer, map);

            var planetOverride = layer.Find(planet.Key) is null
                ? GalaxyMapRowCloner.CloneForOverride(planet, target)
                : GalaxyMapRowCloner.Clone(planet);
            planetOverride.Origin = new GalaxyMapRowOrigin(target, workspace.GetOverrideChain(planet.Key).Count > 0);
            ((Planet)planetOverride).MapRowId = mapId;
            GalaxyMapRowAuthoring.EnsureSnapshot(planetOverride).MarkDirty("Map");

            return edits.ExecuteMutation(new EditMutationRequest(
                [map.Key, planet.Key],
                [GalaxyMapTable.Map, GalaxyMapTable.Planet],
                () =>
                {
                    layer.Upsert(map);
                    layer.Upsert(planetOverride);
                },
                presentation,
                $"Created Map row {mapId} and linked Planet row {planet.RowId}.",
                IsStructural: true));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            return WorkflowResult.Failure(exception.Message);
        }
    }

    public WorkflowResult DeleteLinkedPlotPlanet(Planet planet, HistoryPresentationState presentation)
    {
        if (session.Workspace is null || planet.PlotPlanet is not { } linked)
        {
            return WorkflowResult.Failure("The Planet has no linked PlotPlanet row.");
        }

        var layer = WritableOwningLayer(linked);
        if (layer is null)
        {
            return WorkflowResult.Failure("Only a PlotPlanet row owned by a writable module can be deleted safely.");
        }

        session.Workspace.SetActiveModule(layer.Module);
        return edits.ExecuteMutation(new EditMutationRequest(
            [linked.Key],
            [GalaxyMapTable.PlotPlanet],
            () => layer.Remove(layer.Find(linked.Key)!),
            presentation,
            $"Deleted linked PlotPlanet row {linked.RowId}.",
            IsStructural: true));
    }

    public WorkflowResult DeleteLinkedMap(
        Planet planet,
        GalaxyMapModule target,
        HistoryPresentationState presentation)
    {
        if (session.Workspace is null || planet.LinkedMap is not { } linked)
        {
            return WorkflowResult.Failure("The Planet has no linked Map row.");
        }

        var mapLayer = WritableOwningLayer(linked);
        var planetLayer = session.Workspace.ModuleLayers.FirstOrDefault(layer =>
            string.Equals(layer.Module.Tag, target.Tag, StringComparison.OrdinalIgnoreCase));
        if (mapLayer is null || planetLayer is null || !ReferenceEquals(mapLayer, planetLayer))
        {
            return WorkflowResult.Failure(
                "The Map and editable Planet instance must belong to the same writable module before the link can be deleted safely.");
        }

        session.Workspace.SetActiveModule(planetLayer.Module);
        var replacement = planetLayer.Find(planet.Key) is { } existing
            ? GalaxyMapRowCloner.Clone((Planet)existing)
            : GalaxyMapRowCloner.CloneForOverride(planet, planetLayer.Module) as Planet;
        replacement!.MapRowId = -1;
        GalaxyMapRowAuthoring.EnsureSnapshot(replacement).MarkDirty("Map");
        return edits.ExecuteMutation(new EditMutationRequest(
            [linked.Key, planet.Key],
            [GalaxyMapTable.Map, GalaxyMapTable.Planet],
            () =>
            {
                mapLayer.Remove(mapLayer.Find(linked.Key)!);
                planetLayer.Upsert(replacement);
            },
            presentation,
            $"Deleted linked Map row {linked.RowId}.",
            IsStructural: true));
    }

    private GalaxyMapLayer? WritableOwningLayer(GalaxyMapRow row)
        => session.Workspace?.ModuleLayers.FirstOrDefault(layer =>
            !layer.Module.IsReadOnly &&
            layer.Find(row.Key) is not null &&
            string.Equals(layer.Module.Tag, row.Origin?.ModuleTag, StringComparison.OrdinalIgnoreCase));

}
