using System;
using FontAwesome5;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace LegendaryExplorer.SharedUI;

/// <summary>Gives static and generated context-menu actions consistent, theme-aware icons.</summary>
internal static class ContextMenuIcons
{
    private static bool _enabled;
    private static readonly DependencyProperty GeneratedIconProperty = DependencyProperty.RegisterAttached(
        "GeneratedIcon", typeof(ImageAwesome), typeof(ContextMenuIcons));
    private static readonly DependencyProperty AppliedTemplateProperty = DependencyProperty.RegisterAttached(
        "AppliedTemplate", typeof(ControlTemplate), typeof(ContextMenuIcons));
    private static readonly DependencyProperty ObservingGeneratorProperty = DependencyProperty.RegisterAttached(
        "ObservingGenerator", typeof(bool), typeof(ContextMenuIcons), new PropertyMetadata(false));

    internal static void Enable()
    {
        if (_enabled)
            return;

        _enabled = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOpened), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnOpened), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnItemLoaded), true);
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || sender is not ItemsControl menu
            || (menu is MenuItem item && !IsContextMenuItem(item)))
            return;

        if (menu is ContextMenu)
            menu.SetCurrentValue(Grid.IsSharedSizeScopeProperty, true);

        // Tool-specific handlers can change destinations and add menu items on opening.
        menu.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () => PopulateMenu(menu));
    }

    private static void OnItemLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded is a direct event, but WPF broadcasts it with the loading subtree
        // as OriginalSource. Each generated MenuItem still needs its own decoration.
        if (sender is MenuItem item)
        {
            item.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
            {
                if (IsContextMenuItem(item))
                    PopulateItem(item, GetParentIcon(item));
            });
        }
    }

    private static bool IsContextMenuItem(MenuItem item)
    {
        for (ItemsControl owner = ItemsControl.ItemsControlFromItemContainer(item); owner is not null;)
        {
            if (owner is ContextMenu)
                return true;
            if (owner is not MenuItem parent)
                return false;
            owner = ItemsControl.ItemsControlFromItemContainer(parent);
        }
        return false;
    }

    private static EFontAwesomeIcon? GetParentIcon(MenuItem item) =>
        ItemsControl.ItemsControlFromItemContainer(item) is MenuItem parent
            ? ContextMenuActionIcons.GetIcon(parent, GetParentIcon(parent))
            : null;

    internal static void PopulateMenu(ItemsControl menu)
    {
        if (!(bool)menu.GetValue(ObservingGeneratorProperty))
        {
            menu.SetValue(ObservingGeneratorProperty, true);
            // ItemsSource can add containers while a submenu is already open.
            // Follow generation as well as opening and loading the popup.
            menu.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                if (menu.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                    menu.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () => PopulateMenu(menu));
            };
        }

        EFontAwesomeIcon? parentIcon = menu is MenuItem parent ? GetParentIcon(parent) : null;
        if (menu is MenuItem parentItem)
            parentIcon = ContextMenuActionIcons.GetIcon(parentItem, parentIcon);

        foreach (object entry in menu.Items)
        {
            if ((entry as MenuItem ?? menu.ItemContainerGenerator.ContainerFromItem(entry) as MenuItem) is { } item)
            {
                PopulateItem(item, parentIcon);
                PopulateMenu(item);
            }
        }
    }

    private static void PopulateItem(MenuItem item, EFontAwesomeIcon? parentIcon)
    {
        var generatedIcon = (ImageAwesome)item.GetValue(GeneratedIconProperty);
        if (!BindingOperations.IsDataBound(item, MenuItem.IconProperty)
            && (item.Icon is null || ReferenceEquals(item.Icon, generatedIcon)))
        {
            if (generatedIcon is null)
            {
                generatedIcon = new ImageAwesome { Width = 16, Height = 16, IsHitTestVisible = false, Focusable = false };
                generatedIcon.SetBinding(ImageAwesome.ForegroundProperty, new Binding(nameof(item.Foreground)) { Source = item });
                item.SetValue(GeneratedIconProperty, generatedIcon);
            }

            generatedIcon.Icon = ContextMenuActionIcons.GetIcon(item, parentIcon);
            item.SetCurrentValue(MenuItem.IconProperty, generatedIcon);
        }

        var appliedTemplate = (ControlTemplate)item.GetValue(AppliedTemplateProperty);
        if (ReferenceEquals(item.Template, appliedTemplate) && appliedTemplate is not null)
            return;

        ValueSource templateSource = DependencyPropertyHelper.GetValueSource(item, Control.TemplateProperty);
        if (BindingOperations.IsDataBound(item, Control.TemplateProperty)
            || templateSource.BaseValueSource is BaseValueSource.Local or BaseValueSource.StyleTrigger or BaseValueSource.ParentTemplateTrigger)
            return;

        if (item.TryFindResource("ContextMenuActionItemTemplate") is ControlTemplate template)
        {
            item.SetCurrentValue(Control.TemplateProperty, template);
            item.SetValue(AppliedTemplateProperty, template);
        }
    }
}
