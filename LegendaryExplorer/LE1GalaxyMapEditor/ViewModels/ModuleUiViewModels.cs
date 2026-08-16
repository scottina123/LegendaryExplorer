using LE1GalaxyMapEditor.Infrastructure;
using LE1GalaxyMapEditor.Models;

namespace LE1GalaxyMapEditor.ViewModels;

public sealed class ModuleBarItemViewModel
{
    public ModuleBarItemViewModel(
        GalaxyMapModule module,
        bool isActive,
        bool isDirty,
        Action<GalaxyMapModule> edit)
    {
        Module = module;
        IsActive = isActive;
        IsDirty = isDirty;
        EditCommand = new RelayCommand(() => edit(module));
    }

    public GalaxyMapModule Module { get; }
    public string Name => Module.Name;
    public string Tag => Module.Tag;
    public int LoadOrder => Module.LoadOrder;
    public ModuleColor Color => Module.Color;
    public bool IsActive { get; }
    public bool IsDirty { get; }
    public RelayCommand EditCommand { get; }
}

public sealed class RowInstanceTabViewModel
{
    public RowInstanceTabViewModel(
        GalaxyMapModule module,
        bool isEffective,
        bool isSelected,
        Action<GalaxyMapModule> select)
    {
        Module = module;
        IsEffective = isEffective;
        IsSelected = isSelected;
        SelectCommand = new RelayCommand(() => select(module));
    }

    public GalaxyMapModule Module { get; }
    public string Label => Module.Tag;
    public string Detail => $"Priority {Module.LoadOrder}" + (IsEffective ? " • effective" : string.Empty);
    public ModuleColor Color => Module.Color;
    public bool IsEffective { get; }
    public bool IsSelected { get; }
    public RelayCommand SelectCommand { get; }
}
