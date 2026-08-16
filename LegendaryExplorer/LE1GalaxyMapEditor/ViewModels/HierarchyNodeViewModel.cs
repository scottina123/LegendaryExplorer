using System.Collections.ObjectModel;
using System.ComponentModel;
using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;
using LE1GalaxyMapEditor.Services;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed class HierarchyNodeViewModel : ObservableObject, IDisposable
{
    private sealed class GalaxyRootItem : GalaxyMapRow
    {
    }

    private readonly Action<HierarchyNodeViewModel> _selectedAction;
    private bool _isSelected;
    private bool _isExpanded = true;
    private bool _isVisible = true;
    private bool _suppressSelectedAction;

    public HierarchyNodeViewModel(
        GalaxyMapRow item,
        Action<HierarchyNodeViewModel> selectedAction,
        HierarchyNodeViewModel? parent = null,
        int instanceCount = 1,
        Action<GalaxyMapRow>? cloneAction = null,
        Action<GalaxyMapRow>? deleteAction = null,
        Action<GalaxyMapRow>? moveAction = null,
        Action<Planet>? openPlanetDesignerAction = null,
        bool canMoveToParent = false,
        Action<HierarchyNodeViewModel>? addChildAction = null,
        Func<bool>? canAddChild = null)
    {
        Item = item;
        Parent = parent;
        _selectedAction = selectedAction;
        InstanceCount = Math.Max(1, instanceCount);
        SupportsAddChild = item is Cluster or GalaxySystem && addChildAction is not null;
        AddChildCommand = SupportsAddChild
            ? new RelayCommand(() => addChildAction!(this), () => canAddChild?.Invoke() ?? true)
            : null;
        CloneCommand = new RelayCommand(() => cloneAction?.Invoke(Item), () => cloneAction is not null);
        DeleteCommand = new RelayCommand(() => deleteAction?.Invoke(Item), () => deleteAction is not null);
        SupportsParentMove = item is GalaxySystem or Planet;
        CanMoveToParent = SupportsParentMove && canMoveToParent;
        MoveCommand = new RelayCommand(
            () => moveAction?.Invoke(Item),
            () => moveAction is not null && CanMoveToParent);
        OpenPlanetDesignerCommand = new RelayCommand(
            () =>
            {
                if (Item is Planet planet) openPlanetDesignerAction?.Invoke(planet);
            },
            () => openPlanetDesignerAction is not null && Item is Planet planet &&
                  PlanetAppearanceCodec.IsAppearanceCapable(planet));
        Item.PropertyChanged += ItemOnPropertyChanged;
    }

    private HierarchyNodeViewModel(
        Action<HierarchyNodeViewModel> selectedAction,
        Action<HierarchyNodeViewModel>? addChildAction,
        Func<bool>? canAddChild)
    {
        Item = new GalaxyRootItem();
        IsGalaxyRoot = true;
        _selectedAction = selectedAction;
        SupportsAddChild = addChildAction is not null;
        AddChildCommand = SupportsAddChild
            ? new RelayCommand(() => addChildAction!(this), () => canAddChild?.Invoke() ?? true)
            : null;
        OpenPlanetDesignerCommand = new RelayCommand(() => { }, () => false);
    }

    public static HierarchyNodeViewModel CreateGalaxyRoot(
        Action<HierarchyNodeViewModel> selectedAction,
        Action<HierarchyNodeViewModel>? addChildAction = null,
        Func<bool>? canAddChild = null)
        => new(selectedAction, addChildAction, canAddChild);

    public GalaxyMapRow Item { get; private set; }
    public GalaxyMapRow? Model => IsGalaxyRoot ? null : Item;
    public HierarchyNodeViewModel? Parent { get; }
    public ObservableCollection<HierarchyNodeViewModel> Children { get; } = [];
    public bool IsGalaxyRoot { get; }
    public bool SupportsRowActions => !IsGalaxyRoot;
    public bool SupportsAddChild { get; }
    public int InstanceCount { get; private set; }
    public bool HasMultipleInstances => InstanceCount > 1;

    public string DisplayName => IsGalaxyRoot ? "The Milky Way" : Item switch
    {
        Cluster cluster => cluster.DisplayName,
        GalaxySystem system => system.DisplayName,
        Planet planet => planet.DisplayName,
        _ => $"Row {Item.RowId}"
    };

    public string ItemType => IsGalaxyRoot ? "Galaxy" : Item switch
    {
        Cluster => "Cluster",
        GalaxySystem => "System",
        Planet => "Planet",
        _ => "Row"
    };

    public string Icon => IsGalaxyRoot ? "\u273A" : Item switch
    {
        Cluster => "\u2726",
        GalaxySystem => "\u2299",
        Planet => "\u25CF",
        _ => "\u2022"
    };

    public string ToolTipText => IsGalaxyRoot
        ? "Galaxy overview · BASEGAME"
        : $"{ItemType} row {Item.RowId} · {ModuleTag}";
    public ModuleColor ModuleColor => IsGalaxyRoot
        ? ModuleColor.BaseGameBlue
        : Item.Origin?.Color ?? ModuleColor.BaseGameBlue;
    public string ModuleTag => IsGalaxyRoot
        ? GalaxyMapModule.BaseGameTag
        : Item.Origin?.ModuleTag ?? GalaxyMapModule.BaseGameTag;
    public bool IsModuleOverride => !IsGalaxyRoot && Item.Origin?.OverridesLowerLayer == true;
    public RelayCommand? CloneCommand { get; }
    public RelayCommand? DeleteCommand { get; }
    public RelayCommand? MoveCommand { get; }
    public RelayCommand OpenPlanetDesignerCommand { get; }
    public RelayCommand? AddChildCommand { get; }
    public string AddChildMenuHeader => Item switch
    {
        GalaxyRootItem => "Add Cluster",
        Cluster => "Add System",
        GalaxySystem => "Add Planet/Object",
        _ => string.Empty
    };
    public bool SupportsParentMove { get; }
    public bool SupportsPlanetDesigner => Item is Planet planet && PlanetAppearanceCodec.IsAppearanceCapable(planet);
    public bool IsNearBottomEdge => Item switch
    {
        Cluster cluster => cluster.Y >= 0.97,
        GalaxySystem system => system.Y >= 0.97,
        Planet planet => planet.Y >= 0.97,
        _ => false
    };
    public string SystemMapToolTip => Item is Planet
        {
            VisualKind: PlanetVisualKind.Planet or PlanetVisualKind.RingedPlanet
        }
            ? "Double-click to open Planet Designer"
            : Item is Planet planet
                ? planet.VisualKind.ToString()
                : ToolTipText;
    public bool CanMoveToParent { get; private set; }
    public string MoveMenuHeader => Item is GalaxySystem ? "Move to Cluster…" : "Move to System…";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value && !_suppressSelectedAction)
            {
                _selectedAction(this);
            }
        }
    }

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }

    public bool ApplySearch(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var descendantMatches = false;
        foreach (var child in Children)
        {
            descendantMatches |= child.ApplySearch(terms);
        }

        var pathText = string.Join(' ', AncestorsAndSelf().Select(node =>
            $"{node.DisplayName} {node.ItemType} {node.ModuleTag} {(node.IsGalaxyRoot ? string.Empty : node.Item.RowId)}"));
        var selfMatches = terms.Count == 0 || terms.All(term =>
            pathText.Contains(term, StringComparison.OrdinalIgnoreCase));
        IsVisible = terms.Count == 0 || selfMatches || descendantMatches;
        if (terms.Count > 0 && IsVisible && Children.Count > 0)
        {
            IsExpanded = true;
        }

        return IsVisible;
    }

    internal void SetSelectedSilently(bool value)
    {
        try
        {
            _suppressSelectedAction = true;
            IsSelected = value;
        }
        finally
        {
            _suppressSelectedAction = false;
        }
    }

    internal void UpdateItem(GalaxyMapRow item, int instanceCount, bool canMoveToParent)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsGalaxyRoot)
        {
            throw new InvalidOperationException("The galaxy root cannot be retargeted to a CSV row.");
        }

        Item.PropertyChanged -= ItemOnPropertyChanged;
        Item = item;
        InstanceCount = Math.Max(1, instanceCount);
        CanMoveToParent = SupportsParentMove && canMoveToParent;
        Item.PropertyChanged += ItemOnPropertyChanged;

        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(InstanceCount));
        OnPropertyChanged(nameof(HasMultipleInstances));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ItemType));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(ToolTipText));
        OnPropertyChanged(nameof(ModuleColor));
        OnPropertyChanged(nameof(ModuleTag));
        OnPropertyChanged(nameof(IsModuleOverride));
        OnPropertyChanged(nameof(CanMoveToParent));
        OnPropertyChanged(nameof(SupportsPlanetDesigner));
        OnPropertyChanged(nameof(IsNearBottomEdge));
        OnPropertyChanged(nameof(SystemMapToolTip));
        OnPropertyChanged(nameof(MoveMenuHeader));
        MoveCommand?.RaiseCanExecuteChanged();
        OpenPlanetDesignerCommand.RaiseCanExecuteChanged();
    }

    public void ExpandAncestors()
    {
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.IsExpanded = true;
        }
    }

    internal void RaiseCommandStates()
    {
        AddChildCommand?.RaiseCanExecuteChanged();
        OpenPlanetDesignerCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (!IsGalaxyRoot)
        {
            Item.PropertyChanged -= ItemOnPropertyChanged;
        }

        foreach (var child in Children)
        {
            child.Dispose();
        }
    }

    private void ItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Cluster.DisplayName) or nameof(GalaxySystem.DisplayName) or
            nameof(Planet.DisplayName) or nameof(GalaxyMapRow.RowId))
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(ToolTipText));
        }

        if (e.PropertyName == nameof(GalaxyMapRow.Origin))
        {
            OnPropertyChanged(nameof(ModuleColor));
            OnPropertyChanged(nameof(ModuleTag));
            OnPropertyChanged(nameof(IsModuleOverride));
            OnPropertyChanged(nameof(ToolTipText));
        }

        if (e.PropertyName == nameof(Cluster.Y))
        {
            OnPropertyChanged(nameof(IsNearBottomEdge));
        }

        if (e.PropertyName == nameof(Planet.VisualKind))
        {
            OnPropertyChanged(nameof(SystemMapToolTip));
        }
    }

    private IEnumerable<HierarchyNodeViewModel> AncestorsAndSelf()
    {
        var path = new Stack<HierarchyNodeViewModel>();
        for (var node = this; node is not null; node = node.Parent)
        {
            path.Push(node);
        }

        return path;
    }
}
