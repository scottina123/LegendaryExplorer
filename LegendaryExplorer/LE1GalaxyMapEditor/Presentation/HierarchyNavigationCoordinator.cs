using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;
using LE1GalaxyMapEditor.ViewModels;
using LE1GalaxyMapEditor.Workflows;

namespace LE1GalaxyMapEditor.Presentation;

/// <summary>
/// Owns hierarchy identity, row-instance inspection, selection, and construction
/// of the three map presentation models. Business workflows communicate through
/// stable row keys; none of this state is part of the editor session aggregate.
/// </summary>
public sealed class HierarchyNavigationCoordinator : IDisposable
{
    private readonly EditorSession _session;
    private readonly PropertyInspectorViewModel _inspector;
    private readonly GalaxyMapTextureService _textures;
    private readonly Func<Cluster, ImageSource?> _clusterTexture;
    private readonly Func<bool> _hasActiveModule;
    private readonly Func<bool> _isRelayMode;
    private readonly Func<HierarchyNodeViewModel, bool> _interceptSelection;
    private readonly Action<GalaxyMapRow> _cloneRow;
    private readonly Action<GalaxyMapRow> _deleteRow;
    private readonly Action<GalaxyMapRow> _moveRow;
    private readonly Action<Planet> _openPlanetDesigner;
    private readonly Func<GalaxyMapRow, bool> _canMoveRow;
    private readonly Action<HierarchyNodeViewModel> _addChild;
    private readonly PropertyChangedEventHandler _modelChanged;
    private readonly Action<GalaxyMapModule> _activateModule;
    private readonly Dictionary<GalaxyMapRow, HierarchyNodeViewModel> _nodes =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GalaxyMapRowKey, HierarchyNodeViewModel> _nodesByKey = [];

    private GalaxyMapDocument? _document;
    private HierarchyNodeViewModel? _selectedNode;
    private HierarchyNodeViewModel? _galaxyRoot;
    private GalaxyMapRow? _inspectedPhysicalRow;

    public HierarchyNavigationCoordinator(
        EditorSession session,
        PropertyInspectorViewModel inspector,
        GalaxyMapTextureService textures,
        Func<Cluster, ImageSource?> clusterTexture,
        Func<bool> hasActiveModule,
        Func<bool> isRelayMode,
        Func<HierarchyNodeViewModel, bool> interceptSelection,
        Action<GalaxyMapRow> cloneRow,
        Action<GalaxyMapRow> deleteRow,
        Action<GalaxyMapRow> moveRow,
        Action<Planet> openPlanetDesigner,
        Func<GalaxyMapRow, bool> canMoveRow,
        Action<HierarchyNodeViewModel> addChild,
        PropertyChangedEventHandler modelChanged,
        Action<GalaxyMapModule> activateModule)
    {
        _session = session;
        _inspector = inspector;
        _textures = textures;
        _clusterTexture = clusterTexture;
        _hasActiveModule = hasActiveModule;
        _isRelayMode = isRelayMode;
        _interceptSelection = interceptSelection;
        _cloneRow = cloneRow;
        _deleteRow = deleteRow;
        _moveRow = moveRow;
        _openPlanetDesigner = openPlanetDesigner;
        _canMoveRow = canMoveRow;
        _addChild = addChild;
        _modelChanged = modelChanged;
        _activateModule = activateModule;
    }

    public event EventHandler? Changed;

    public ObservableCollection<HierarchyNodeViewModel> HierarchyRoots { get; } = [];
    public ObservableCollection<RowInstanceTabViewModel> RowInstanceTabs { get; } = [];
    public GalaxyMapDocument? Document => _document;
    public object? CurrentViewModel { get; private set; }
    public Cluster? CurrentCluster { get; private set; }
    public GalaxySystem? CurrentSystem { get; private set; }
    public GalaxyMapRowKey? SelectedKey => _selectedNode?.Model?.Key;
    public GalaxyMapRow? SelectedRow => _selectedNode?.Model;
    public bool HasMultipleRowInstances => RowInstanceTabs.Count > 1;
    public string? PreferredInstanceTag { get; set; }
    public bool InspectPhysicalInstance { get; set; }
    public NavigationTarget Navigation => CurrentViewModel switch
    {
        SystemViewModel systemView => new(systemView.System.ClusterRowId, systemView.System.RowId),
        ClusterViewModel clusterView => new(clusterView.Cluster.RowId, null),
        _ => NavigationTarget.Galaxy
    };

    public void AttachDocument(
        GalaxyMapDocument document,
        GalaxyMapRowKey? selectionKey,
        NavigationTarget navigation,
        bool preserveHierarchy = false)
    {
        if (_document is not null)
        {
            foreach (var row in AllRows(_document))
            {
                row.PropertyChanged -= _modelChanged;
            }
        }

        var canPreserveHierarchy = preserveHierarchy && CanRetargetHierarchy(document);
        _document = document;
        foreach (var row in AllRows(document))
        {
            row.PropertyChanged += _modelChanged;
        }

        if (canPreserveHierarchy)
        {
            RetargetHierarchy(document);
        }
        else
        {
            BuildHierarchy();
        }

        if (navigation.SystemRowId is { } systemId &&
            document.SystemsByRowId.TryGetValue(systemId, out var system) &&
            system.Cluster is not null)
        {
            NavigateSystem(system);
        }
        else if (navigation.ClusterRowId is { } clusterId &&
                 document.ClustersByRowId.TryGetValue(clusterId, out var cluster))
        {
            NavigateCluster(cluster);
        }
        else
        {
            NavigateGalaxy();
        }

        if (selectionKey is { } key && _nodesByKey.TryGetValue(key, out var node))
        {
            SelectNodeCore(node);
        }
        else
        {
            ClearRowInstanceTabs();
            _inspector.Inspect(null);
        }

        OnChanged();
    }

    public void ActivateHierarchyNode(HierarchyNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var belongs = node.IsGalaxyRoot
            ? ReferenceEquals(node, _galaxyRoot)
            : node.Model is { } model && _nodes.TryGetValue(model, out var current) && ReferenceEquals(node, current);
        if (belongs)
        {
            SelectFromHierarchy(node);
        }
    }

    public void SelectMapNode(HierarchyNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Model is { } model && _nodes.TryGetValue(model, out var current) && ReferenceEquals(node, current))
        {
            SelectFromMap(node);
        }
    }

    public bool TrySelect(GalaxyMapRowKey key)
    {
        if (!_nodesByKey.TryGetValue(key, out var node))
        {
            return false;
        }

        SelectFromHierarchy(node);
        return true;
    }

    public bool TrySelectWithoutNavigation(GalaxyMapRowKey key)
    {
        if (!_nodesByKey.TryGetValue(key, out var node))
        {
            return false;
        }

        SelectNodeCore(node);
        return true;
    }

    public void RefreshInspector(GalaxyMapRow row) => ShowInspectorForRow(row);

    public bool TryGetNode(GalaxyMapRowKey key, out HierarchyNodeViewModel node)
        => _nodesByKey.TryGetValue(key, out node!);

    public void NavigateGalaxy()
    {
        if (_document is null)
        {
            SetCurrent(null, null, null);
            return;
        }

        var clusterNodes = (IReadOnlyList<HierarchyNodeViewModel>?)_galaxyRoot?.Children ?? [];
        SetCurrent(
            new GalaxyViewModel(
                clusterNodes,
                _document.Relays,
                SelectFromMap,
                EnterCluster,
                _isRelayMode(),
                _textures.GetGalaxyTexture()),
            null,
            null);
    }

    public void NavigateCluster(Cluster cluster)
    {
        if (!_nodes.TryGetValue(cluster, out var clusterNode))
        {
            return;
        }

        SetCurrent(
            new ClusterViewModel(
                cluster,
                clusterNode.Children,
                SelectFromMap,
                EnterSystem,
                _clusterTexture(cluster)),
            cluster,
            null);
    }

    public void NavigateSystem(GalaxySystem system)
    {
        if (!_nodes.TryGetValue(system, out var systemNode))
        {
            return;
        }

        var usesNebula = system.ShowNebula == 1 && system.Cluster is not null;
        SetCurrent(
            new SystemViewModel(
                system,
                systemNode.Children,
                SelectFromMap,
                usesNebula ? _clusterTexture(system.Cluster!) : _textures.GetSystemTexture(),
                usesNebula),
            system.Cluster,
            system);
    }

    public void RestoreInspectionState(string? preferredInstanceTag, bool inspectPhysicalInstance)
    {
        PreferredInstanceTag = preferredInstanceTag;
        InspectPhysicalInstance = inspectPhysicalInstance;
    }

    public void RaiseNodeCommandStates()
    {
        foreach (var node in _nodes.Values)
        {
            node.RaiseCommandStates();
        }
    }

    public void Dispose()
    {
        if (_document is not null)
        {
            foreach (var row in AllRows(_document))
            {
                row.PropertyChanged -= _modelChanged;
            }
        }

        DisposeHierarchy();
        ClearRowInstanceTabs();
    }

    private bool CanRetargetHierarchy(GalaxyMapDocument document)
    {
        if (_galaxyRoot is null || HierarchyRoots.Count != 1 || !ReferenceEquals(HierarchyRoots[0], _galaxyRoot))
        {
            return false;
        }

        var systems = document.Clusters.SelectMany(cluster => cluster.Systems).ToArray();
        var planets = systems.SelectMany(system => system.Planets).ToArray();
        if (_nodesByKey.Count != document.Clusters.Count + systems.Length + planets.Length)
        {
            return false;
        }

        return document.Clusters.All(cluster =>
                   _nodesByKey.TryGetValue(cluster.Key, out var node) && ReferenceEquals(node.Parent, _galaxyRoot)) &&
               systems.All(system => system.Cluster is not null &&
                   _nodesByKey.TryGetValue(system.Key, out var node) && node.Parent?.Model?.Key == system.Cluster.Key) &&
               planets.All(planet => planet.System is not null &&
                   _nodesByKey.TryGetValue(planet.Key, out var node) && node.Parent?.Model?.Key == planet.System.Key);
    }

    private void RetargetHierarchy(GalaxyMapDocument document)
    {
        _nodes.Clear();
        foreach (var row in document.Clusters.Cast<GalaxyMapRow>()
                     .Concat(document.Clusters.SelectMany(cluster => cluster.Systems))
                     .Concat(document.Clusters.SelectMany(cluster => cluster.Systems).SelectMany(system => system.Planets)))
        {
            var node = _nodesByKey[row.Key];
            node.UpdateItem(row, _session.Workspace?.GetOverrideChain(row.Key).Count ?? 1, _canMoveRow(row));
            _nodes[row] = node;
        }
    }

    private void BuildHierarchy()
    {
        DisposeHierarchy();
        if (_document is null)
        {
            return;
        }

        _galaxyRoot = HierarchyNodeViewModel.CreateGalaxyRoot(SelectFromHierarchy, _addChild, _hasActiveModule);
        HierarchyRoots.Add(_galaxyRoot);
        foreach (var cluster in _document.Clusters)
        {
            var clusterNode = CreateNode(cluster, _galaxyRoot);
            _galaxyRoot.Children.Add(clusterNode);
            foreach (var system in cluster.Systems)
            {
                var systemNode = CreateNode(system, clusterNode);
                clusterNode.Children.Add(systemNode);
                foreach (var planet in system.Planets)
                {
                    systemNode.Children.Add(CreateNode(planet, systemNode));
                }
            }
        }

        _selectedNode = null;
    }

    private HierarchyNodeViewModel CreateNode(GalaxyMapRow row, HierarchyNodeViewModel? parent = null)
    {
        var node = new HierarchyNodeViewModel(
            row,
            SelectFromHierarchy,
            parent,
            _session.Workspace?.GetOverrideChain(row.Key).Count ?? 1,
            _cloneRow,
            _deleteRow,
            _moveRow,
            _openPlanetDesigner,
            _canMoveRow(row),
            _addChild,
            _hasActiveModule);
        _nodes[row] = node;
        _nodesByKey[row.Key] = node;
        return node;
    }

    private void SelectFromHierarchy(HierarchyNodeViewModel node)
    {
        if (_interceptSelection(node))
        {
            return;
        }

        ResetInstancePreferenceForNewNode(node);
        SelectNodeCore(node);
        if (node.IsGalaxyRoot)
        {
            NavigateGalaxy();
            return;
        }

        switch (node.Item)
        {
            case Cluster cluster:
                NavigateCluster(cluster);
                break;
            case GalaxySystem system:
                NavigateSystem(system);
                break;
            case Planet { System: not null } planet:
                NavigateSystem(planet.System);
                break;
        }
    }

    private void SelectFromMap(HierarchyNodeViewModel node)
    {
        if (_interceptSelection(node))
        {
            return;
        }

        ResetInstancePreferenceForNewNode(node);
        SelectNodeCore(node);
    }

    private void ResetInstancePreferenceForNewNode(HierarchyNodeViewModel node)
    {
        if (_selectedNode?.Model?.Key != node.Model?.Key)
        {
            PreferredInstanceTag = null;
            InspectPhysicalInstance = false;
        }
    }

    private void SelectNodeCore(HierarchyNodeViewModel node)
    {
        if (!ReferenceEquals(_selectedNode, node))
        {
            var previous = _selectedNode;
            _selectedNode = node;
            previous?.SetSelectedSilently(false);
        }

        if (!node.IsSelected)
        {
            node.SetSelectedSilently(true);
        }

        node.ExpandAncestors();
        if (node.IsGalaxyRoot)
        {
            ClearRowInstanceTabs();
            _inspector.InspectGalaxy();
        }
        else
        {
            ShowInspectorForRow(node.Model!);
        }

        OnChanged();
    }

    private void ShowInspectorForRow(GalaxyMapRow effectiveRow)
    {
        if (_inspectedPhysicalRow is not null)
        {
            _inspectedPhysicalRow.PropertyChanged -= _modelChanged;
            _inspectedPhysicalRow = null;
        }

        RowInstanceTabs.Clear();
        var chain = _session.Workspace?.GetOverrideChain(effectiveRow.Key) ?? [];
        var effectiveTag = effectiveRow.Origin?.ModuleTag ?? GalaxyMapModule.BaseGameTag;
        var selectedTag = chain.Any(row => string.Equals(row.Origin?.ModuleTag, PreferredInstanceTag,
                StringComparison.OrdinalIgnoreCase))
            ? PreferredInstanceTag!
            : effectiveTag;
        foreach (var instance in chain)
        {
            if (instance.Origin?.Module is not { } module)
            {
                continue;
            }

            RowInstanceTabs.Add(new RowInstanceTabViewModel(
                module,
                string.Equals(module.Tag, effectiveTag, StringComparison.OrdinalIgnoreCase),
                string.Equals(module.Tag, selectedTag, StringComparison.OrdinalIgnoreCase),
                selected => SelectRowInstance(effectiveRow.Key, selected)));
        }

        if (InspectPhysicalInstance)
        {
            var physical = chain.FirstOrDefault(row =>
                string.Equals(row.Origin?.ModuleTag, selectedTag, StringComparison.OrdinalIgnoreCase));
            if (physical is not null)
            {
                var isWritablePhysical = physical.Origin?.Module is { IsReadOnly: false, IsBaseGame: false };
                var inspectionRow = isWritablePhysical ? physical : GalaxyMapRowCloner.Clone(physical);
                HydrateInspectionRelationships(inspectionRow, effectiveRow);
                _inspectedPhysicalRow = inspectionRow;
                var canCreateOverride = _session.Workspace?.Modules.Any(module => !module.IsReadOnly) == true;
                if (isWritablePhysical || canCreateOverride)
                {
                    inspectionRow.PropertyChanged += _modelChanged;
                }

                _inspector.Inspect(inspectionRow, isWritablePhysical || canCreateOverride);
                OnChanged();
                return;
            }
        }

        PreferredInstanceTag = effectiveTag;
        _inspector.Inspect(effectiveRow, _session.Workspace?.Modules.Any(module => !module.IsReadOnly) == true);
        OnChanged();
    }

    private void SelectRowInstance(GalaxyMapRowKey key, GalaxyMapModule module)
    {
        if (_session.Workspace?.Resolve(key) is not { } effective)
        {
            return;
        }

        PreferredInstanceTag = module.Tag;
        InspectPhysicalInstance = true;
        if (!module.IsReadOnly && !module.IsBaseGame)
        {
            _activateModule(module);
        }

        ShowInspectorForRow(effective);
    }

    private void ClearRowInstanceTabs()
    {
        if (_inspectedPhysicalRow is not null)
        {
            _inspectedPhysicalRow.PropertyChanged -= _modelChanged;
            _inspectedPhysicalRow = null;
        }

        RowInstanceTabs.Clear();
        OnChanged();
    }

    private void EnterCluster(HierarchyNodeViewModel node)
    {
        if (!_isRelayMode() && node.Item is Cluster cluster)
        {
            SelectNodeCore(node);
            NavigateCluster(cluster);
        }
    }

    private void EnterSystem(HierarchyNodeViewModel node)
    {
        if (node.Item is GalaxySystem system)
        {
            SelectNodeCore(node);
            NavigateSystem(system);
        }
    }

    private void SetCurrent(object? viewModel, Cluster? cluster, GalaxySystem? system)
    {
        CurrentViewModel = viewModel;
        CurrentCluster = cluster;
        CurrentSystem = system;
        OnChanged();
    }

    private void DisposeHierarchy()
    {
        foreach (var root in HierarchyRoots)
        {
            root.Dispose();
        }

        HierarchyRoots.Clear();
        _nodes.Clear();
        _nodesByKey.Clear();
        _galaxyRoot = null;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static void HydrateInspectionRelationships(GalaxyMapRow physical, GalaxyMapRow effective)
    {
        switch (physical, effective)
        {
            case (GalaxySystem physicalSystem, GalaxySystem effectiveSystem):
                physicalSystem.Cluster = effectiveSystem.Cluster;
                break;
            case (Planet physicalPlanet, Planet effectivePlanet):
                physicalPlanet.System = effectivePlanet.System;
                physicalPlanet.PlotPlanet = effectivePlanet.PlotPlanet;
                physicalPlanet.LinkedMap = effectivePlanet.LinkedMap;
                break;
            case (RelayConnection physicalRelay, RelayConnection effectiveRelay):
                physicalRelay.StartCluster = effectiveRelay.StartCluster;
                physicalRelay.EndCluster = effectiveRelay.EndCluster;
                break;
        }
    }

    private static IEnumerable<GalaxyMapRow> AllRows(GalaxyMapDocument document)
        => document.Clusters.Cast<GalaxyMapRow>()
            .Concat(document.Systems)
            .Concat(document.Planets)
            .Concat(document.PlotPlanets)
            .Concat(document.Maps)
            .Concat(document.Relays);
}
