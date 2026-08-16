using System.Collections.ObjectModel;

namespace LE1GalaxyMapEditor.Models;

public sealed class GalaxyMapDocument
{
    public string SourceFolder { get; internal set; } = string.Empty;
    public bool IsSourceReadOnly => true;

    public ObservableCollection<Cluster> Clusters { get; } = [];
    public ObservableCollection<GalaxySystem> Systems { get; } = [];
    public ObservableCollection<Planet> Planets { get; } = [];
    public ObservableCollection<PlotPlanetEntry> PlotPlanets { get; } = [];
    public ObservableCollection<MapEntry> Maps { get; } = [];
    public ObservableCollection<RelayConnection> Relays { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    public IReadOnlyDictionary<int, Cluster> ClustersByRowId { get; private set; }
        = new Dictionary<int, Cluster>();
    public IReadOnlyDictionary<int, GalaxySystem> SystemsByRowId { get; private set; }
        = new Dictionary<int, GalaxySystem>();
    public IReadOnlyDictionary<int, Planet> PlanetsByRowId { get; private set; }
        = new Dictionary<int, Planet>();
    public IReadOnlyDictionary<int, PlotPlanetEntry> PlotPlanetsByRowId { get; private set; }
        = new Dictionary<int, PlotPlanetEntry>();
    public IReadOnlyDictionary<int, MapEntry> MapsByRowId { get; private set; }
        = new Dictionary<int, MapEntry>();

    public IReadOnlyList<RelayConnection> GetRelaysForCluster(Cluster cluster)
        => Relays.Where(relay => ReferenceEquals(relay.StartCluster, cluster) || ReferenceEquals(relay.EndCluster, cluster))
            .ToArray();

    public bool TryGetRelayCode(Cluster cluster, out int code, out string error)
    {
        code = 0;
        error = string.Empty;
        var parsed = GalaxyMapIdentity.ParseLabelSyntax(
            cluster.Label, GalaxyMapIdentityKind.Cluster);
        if (parsed.Status == GalaxyMapLabelParseStatus.Malformed)
        {
            error = $"Cluster label '{cluster.Label}' does not end in a numeric Cluster code.";
            return false;
        }

        if (parsed.Status == GalaxyMapLabelParseStatus.NumericOverflow ||
            !GalaxyMapIdentity.IsValidExistingSuffix(GalaxyMapIdentityKind.Cluster, parsed.Suffix))
        {
            error = $"Cluster label '{cluster.Label}' is too large to encode as a Relay endpoint.";
            return false;
        }

        _ = GalaxyMapIdentity.TryEncodeClusterRelayEndpoint(cluster.Label, out var relayCode);
        code = relayCode;
        var matches = Clusters.Count(candidate =>
            GalaxyMapIdentity.TryEncodeClusterRelayEndpoint(candidate.Label, out var candidateCode) &&
            candidateCode == relayCode);
        if (matches != 1)
        {
            error = $"Cluster label code {code} is ambiguous across {matches} Clusters.";
            code = 0;
            return false;
        }

        return true;
    }

    public void RebuildRelationships()
    {
        Warnings.Clear();

        ClustersByRowId = BuildIndex(Clusters, "Cluster");
        SystemsByRowId = BuildIndex(Systems, "System");
        PlanetsByRowId = BuildIndex(Planets, "Planet");
        PlotPlanetsByRowId = BuildIndex(PlotPlanets, "PlotPlanet");
        MapsByRowId = BuildIndex(Maps, "Map");

        foreach (var cluster in Clusters)
        {
            cluster.Systems.Clear();
        }

        foreach (var system in Systems)
        {
            system.Cluster = null;
            system.Planets.Clear();
            if (ClustersByRowId.TryGetValue(system.ClusterRowId, out var cluster))
            {
                system.Cluster = cluster;
                cluster.Systems.Add(system);
            }
            else
            {
                Warnings.Add($"System row {system.RowId} references missing Cluster row {system.ClusterRowId}.");
            }
        }

        foreach (var planet in Planets)
        {
            planet.System = null;
            planet.PlotPlanet = null;
            planet.LinkedMap = null;

            if (SystemsByRowId.TryGetValue(planet.SystemRowId, out var system))
            {
                planet.System = system;
                system.Planets.Add(planet);
            }
            else
            {
                Warnings.Add($"Planet row {planet.RowId} references missing System row {planet.SystemRowId}.");
            }

            if (PlotPlanetsByRowId.TryGetValue(planet.RowId, out var plotPlanet))
            {
                planet.PlotPlanet = plotPlanet;
            }

            if (planet.MapRowId >= 0)
            {
                if (MapsByRowId.TryGetValue(planet.MapRowId, out var map))
                {
                    planet.LinkedMap = map;
                }
                else
                {
                    Warnings.Add($"Planet row {planet.RowId} references missing Map row {planet.MapRowId}.");
                }
            }
        }

        foreach (var plotPlanet in PlotPlanets)
        {
            if (!PlanetsByRowId.ContainsKey(plotPlanet.RowId))
            {
                Warnings.Add($"PlotPlanet row {plotPlanet.RowId} has no Planet with the same row ID.");
            }
        }

        RelinkRelays();
    }

    private Dictionary<int, T> BuildIndex<T>(IEnumerable<T> rows, string tableName) where T : GalaxyMapRow
    {
        var result = new Dictionary<int, T>();
        foreach (var row in rows)
        {
            if (!result.TryAdd(row.RowId, row))
            {
                Warnings.Add($"{tableName} contains duplicate row ID {row.RowId}; the first row is used for links.");
            }
        }

        return result;
    }

    private void RelinkRelays()
    {
        var candidates = new Dictionary<int, List<Cluster>>();
        foreach (var cluster in Clusters)
        {
            var parsed = GalaxyMapIdentity.ParseLabelSyntax(
                cluster.Label, GalaxyMapIdentityKind.Cluster);
            if (parsed.Status == GalaxyMapLabelParseStatus.Malformed)
            {
                Warnings.Add($"Cluster row {cluster.RowId} label '{cluster.Label}' cannot be used to resolve Relay endpoints.");
                continue;
            }

            if (parsed.Status == GalaxyMapLabelParseStatus.NumericOverflow ||
                !GalaxyMapIdentity.IsValidExistingSuffix(GalaxyMapIdentityKind.Cluster, parsed.Suffix))
            {
                Warnings.Add($"Cluster row {cluster.RowId} label '{cluster.Label}' is too large to encode as a Relay endpoint.");
                continue;
            }

            _ = GalaxyMapIdentity.TryEncodeClusterRelayEndpoint(cluster.Label, out var encoded);
            if (!candidates.TryGetValue(encoded, out var list))
            {
                list = [];
                candidates[encoded] = list;
            }

            list.Add(cluster);
        }

        var relayLookup = new Dictionary<int, Cluster>();
        foreach (var (encoded, matchingClusters) in candidates)
        {
            if (matchingClusters.Count == 1)
            {
                relayLookup[encoded] = matchingClusters[0];
            }
            else
            {
                Warnings.Add($"Relay code {encoded} is ambiguous across {matchingClusters.Count} Cluster labels.");
            }
        }

        foreach (var relay in Relays)
        {
            relay.StartCluster = relayLookup.GetValueOrDefault(relay.StartClusterEncoded);
            relay.EndCluster = relayLookup.GetValueOrDefault(relay.EndClusterEncoded);

            if (!relay.IsResolved)
            {
                var missing = relay.StartCluster is null ? relay.StartClusterEncoded : relay.EndClusterEncoded;
                Warnings.Add($"Relay row {relay.RowId} references an unavailable Cluster label code {missing}.");
            }
        }
    }
}
