using System.Globalization;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.ViewModels;
using LE1GalaxyMapEditor.Workflows;
using LE1GalaxyMapEditor.Workflows.Editing;

namespace LE1GalaxyMapEditor.Presentation;

public enum InspectorOptionSet
{
    Clusters,
    Systems,
    Maps,
    RelayClusters
}

public enum InspectorActionId
{
    LinkClusterTexture,
    ConfigureLandableDestination,
    AddPlotPlanet,
    AddLinkedMap,
    DeleteLinkedPlotPlanet,
    DeleteLinkedMap,
    BeginRelayCreation,
    CancelRelayEdit,
    RedirectRelay,
    RemoveRelay
}

public sealed record InspectorActionDescriptor(
    InspectorActionId Id,
    string Section,
    string Label,
    string Detail,
    bool IsPrimary = false,
    bool IsDestructive = false,
    object? Payload = null);

public interface IInspectorPresentationWorkflow
{
    bool CanEdit { get; }
    void BeginEdit();
    string? ValidateEdit(GalaxyMapRow row, string propertyName, object? value);
    bool ApplyManagedEdit(GalaxyMapRow row, string propertyName, object? value);
    IReadOnlyList<InspectorFieldOption> GetOptions(InspectorOptionSet optionSet);
    IReadOnlyList<InspectorActionDescriptor> GetActions(GalaxyMapRow row);
    void ExecuteAction(GalaxyMapRow row, InspectorActionDescriptor action);
}

public sealed class NullInspectorPresentationWorkflow : IInspectorPresentationWorkflow
{
    public static NullInspectorPresentationWorkflow Instance { get; } = new();
    public bool CanEdit => true;
    public void BeginEdit() { }
    public string? ValidateEdit(GalaxyMapRow row, string propertyName, object? value) => null;
    public bool ApplyManagedEdit(GalaxyMapRow row, string propertyName, object? value) => false;
    public IReadOnlyList<InspectorFieldOption> GetOptions(InspectorOptionSet optionSet) => [];
    public IReadOnlyList<InspectorActionDescriptor> GetActions(GalaxyMapRow row) => [];
    public void ExecuteAction(GalaxyMapRow row, InspectorActionDescriptor action) { }
}

public sealed class MainInspectorPresentationWorkflow(
    EditorSession session,
    RelayWorkflow relay,
    Func<bool> canEdit,
    Action beginEdit,
    Func<GalaxyMapRow, string, object?, string?> validateEdit,
    Func<GalaxyMapRow, string, object?, bool> applyManagedEdit,
    Action<GalaxyMapRow, InspectorActionDescriptor> executeAction) : IInspectorPresentationWorkflow
{
    public bool CanEdit => canEdit();

    public void BeginEdit() => beginEdit();

    public string? ValidateEdit(GalaxyMapRow row, string propertyName, object? value)
        => validateEdit(row, propertyName, value);

    public bool ApplyManagedEdit(GalaxyMapRow row, string propertyName, object? value)
        => applyManagedEdit(row, propertyName, value);

    public IReadOnlyList<InspectorFieldOption> GetOptions(InspectorOptionSet optionSet)
        => optionSet switch
        {
            InspectorOptionSet.Clusters => session.Document?.Clusters
                .OrderBy(cluster => cluster.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(cluster => new InspectorFieldOption(
                    cluster.RowId.ToString(CultureInfo.InvariantCulture),
                    $"{cluster.DisplayName} • row {cluster.RowId}"))
                .ToArray() ?? [],
            InspectorOptionSet.Systems => session.Document?.Systems
                .OrderBy(system => system.Cluster?.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(system => system.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(system => new InspectorFieldOption(
                    system.RowId.ToString(CultureInfo.InvariantCulture),
                    $"{system.Cluster?.DisplayName ?? "Missing Cluster"} / {system.DisplayName} • row {system.RowId}"))
                .ToArray() ?? [],
            InspectorOptionSet.Maps => MapOptions(),
            InspectorOptionSet.RelayClusters => session.Document?.Clusters
                .Select(cluster => (
                    Cluster: cluster,
                    Valid: GalaxyMapIdentity.TryEncodeClusterRelayEndpoint(cluster.Label, out var encoded),
                    Encoded: encoded))
                .Where(item => item.Valid)
                .OrderBy(item => item.Cluster.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(item => new InspectorFieldOption(
                    item.Encoded.ToString(CultureInfo.InvariantCulture),
                    $"{item.Cluster.DisplayName} • {item.Encoded}"))
                .ToArray() ?? [],
            _ => []
        };

    public IReadOnlyList<InspectorActionDescriptor> GetActions(GalaxyMapRow row)
    {
        var actions = new List<InspectorActionDescriptor>();
        switch (row)
        {
            case Cluster cluster:
                if (session.ActiveModule?.IsPccBacked != true)
                {
                actions.Add(new InspectorActionDescriptor(
                    InspectorActionId.LinkClusterTexture,
                    "Cluster background texture",
                    "Link module texture…",
                    "Choose a PNG, JPEG, BMP, GIF, or TIFF image to stage inside the target module's textures folder.",
                    IsPrimary: true));
                }
                AddRelayActions(cluster, actions);
                break;
            case Planet planet:
                AddPlanetActions(planet, actions);
                break;
        }
        return actions;
    }

    public void ExecuteAction(GalaxyMapRow row, InspectorActionDescriptor action)
        => executeAction(row, action);

    private IReadOnlyList<InspectorFieldOption> MapOptions()
    {
        var options = new List<InspectorFieldOption> { new("-1", "No linked Map") };
        if (session.Document is not null)
        {
            options.AddRange(session.Document.Maps.OrderBy(map => map.RowId).Select(map => new InspectorFieldOption(
                map.RowId.ToString(CultureInfo.InvariantCulture),
                $"{(string.IsNullOrWhiteSpace(map.MapName) ? "Unnamed Map" : map.MapName)} • row {map.RowId}")));
        }
        return options;
    }

    private void AddRelayActions(Cluster cluster, ICollection<InspectorActionDescriptor> actions)
    {
        foreach (var connection in session.Document?.GetRelaysForCluster(cluster) ?? [])
        {
            var otherCluster = ReferenceEquals(connection.StartCluster, cluster)
                ? connection.EndCluster
                : connection.StartCluster;
            var otherCode = ReferenceEquals(connection.StartCluster, cluster)
                ? connection.EndClusterEncoded
                : connection.StartClusterEncoded;
            var destination = otherCluster?.DisplayName ??
                              $"unresolved Cluster{otherCode / 10_000:00} ({otherCode})";
            actions.Add(new InspectorActionDescriptor(
                InspectorActionId.RedirectRelay,
                "Relay connections",
                $"Redirect connection from {destination}…",
                $"Overrides Relay row {connection.RowId}; then click its new destination Cluster.",
                IsPrimary: true,
                Payload: connection));
            if (relay.CanBreak(connection))
            {
                actions.Add(new InspectorActionDescriptor(
                    InspectorActionId.RemoveRelay,
                    "Relay connections",
                    $"Break connection to {destination}",
                    $"Removes active-module Relay row {connection.RowId}.",
                    IsDestructive: true,
                    Payload: connection));
            }
        }

        actions.Add(relay.Source?.Key == cluster.Key
            ? new InspectorActionDescriptor(
                InspectorActionId.CancelRelayEdit,
                "Relay connections",
                "Cancel relay edit",
                "Return to normal Cluster selection.")
            : new InspectorActionDescriptor(
                InspectorActionId.BeginRelayCreation,
                "Relay connections",
                "Add relay connection…",
                "Then click another Cluster on the Galaxy view.",
                IsPrimary: true));
    }

    private static void AddPlanetActions(Planet planet, ICollection<InspectorActionDescriptor> actions)
    {
        var isAsteroidBelt = planet.OrbitRing == 2;
        if (!isAsteroidBelt)
        {
            actions.Add(new InspectorActionDescriptor(
                InspectorActionId.ConfigureLandableDestination,
                "Optional relationships",
                planet.LinkedMap is null ? "Configure landable destination…" : "Edit landable destination…",
                "Creates or updates the linked Map, StartPoint, Remote Event and optional use-button TLK.",
                IsPrimary: true));
        }
        if (planet.PlotPlanet is null)
        {
            actions.Add(new InspectorActionDescriptor(
                InspectorActionId.AddPlotPlanet,
                "Optional relationships",
                "Add PlotPlanet properties",
                "Creates a PlotPlanet _part row in the active module using this Planet's row ID.",
                IsPrimary: true));
        }
        if (!isAsteroidBelt && planet.LinkedMap is null)
        {
            actions.Add(new InspectorActionDescriptor(
                InspectorActionId.AddLinkedMap,
                "Optional relationships",
                "Add linked Map",
                "Creates a blank linked Map row for manual configuration."));
        }
        if (planet.PlotPlanet is not null)
        {
            actions.Add(new InspectorActionDescriptor(
                InspectorActionId.DeleteLinkedPlotPlanet,
                "Linked PlotPlanet actions",
                "Delete linked PlotPlanet",
                "Stages removal of the module-owned linked row.",
                IsDestructive: true));
        }
        if (planet.LinkedMap is not null)
        {
            actions.Add(new InspectorActionDescriptor(
                InspectorActionId.DeleteLinkedMap,
                "Linked Map actions",
                "Delete linked Map",
                "Stages removal of the module-owned Map and clears the Planet link.",
                IsDestructive: true));
        }
    }

}
