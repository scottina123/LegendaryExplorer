using System;
using FontAwesome5;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace LegendaryExplorer.SharedUI;

/// <summary>Gives context-menu and menu-bar dropdown actions consistent, theme-aware icons.</summary>
internal static class ContextMenuIcons
{
    private static bool _enabled;
    private static readonly DependencyProperty GeneratedIconProperty = DependencyProperty.RegisterAttached(
        "GeneratedIcon", typeof(FrameworkElement), typeof(ContextMenuIcons));
    private static readonly DependencyProperty GeneratedIconResourceKeyProperty = DependencyProperty.RegisterAttached(
        "GeneratedIconResourceKey", typeof(string), typeof(ContextMenuIcons));
    private static readonly DependencyProperty AppliedTemplateProperty = DependencyProperty.RegisterAttached(
        "AppliedTemplate", typeof(ControlTemplate), typeof(ContextMenuIcons));
    private static readonly DependencyProperty ObservingGeneratorProperty = DependencyProperty.RegisterAttached(
        "ObservingGenerator", typeof(bool), typeof(ContextMenuIcons), new PropertyMetadata(false));

    internal static void Enable()
    {
        if (_enabled)
            return;

        _enabled = true;
        EventManager.RegisterClassHandler(typeof(Menu), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnMenuLoaded), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOpened), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnOpened), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnItemLoaded), true);
    }

    private static void OnMenuLoaded(object sender, RoutedEventArgs e)
    {
        // Prepare static dropdowns before they take focus when first opened.
        if (sender is Menu menu)
            PopulateMenu(menu);
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || sender is not ItemsControl menu
            || (menu is MenuItem item && !IsMenuItem(item, includeTopLevel: true)))
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
                if (IsMenuItem(item))
                    PopulateItem(item, GetParentIcon(item), GetParentImageResourceKey(item));
                else if (ItemsControl.ItemsControlFromItemContainer(item) is Menu)
                    PopulateMenu(item);
            });
        }
    }

    private static bool IsMenuItem(MenuItem item, bool includeTopLevel = false)
    {
        bool hasParentItem = false;
        for (ItemsControl owner = ItemsControl.ItemsControlFromItemContainer(item); owner is not null;)
        {
            if (owner is ContextMenu)
                return true;
            if (owner is Menu)
                return includeTopLevel || hasParentItem;
            if (owner is not MenuItem parent)
                return false;
            hasParentItem = true;
            owner = ItemsControl.ItemsControlFromItemContainer(parent);
        }
        return false;
    }

    private static EFontAwesomeIcon? GetParentIcon(MenuItem item) =>
        ItemsControl.ItemsControlFromItemContainer(item) is MenuItem parent
            ? ContextMenuActionIcons.GetIcon(parent, GetParentIcon(parent))
            : null;

    private static string GetParentImageResourceKey(MenuItem item) =>
        ItemsControl.ItemsControlFromItemContainer(item) is MenuItem parent
            ? ContextMenuActionIcons.GetImageResourceKey(parent, GetParentImageResourceKey(parent))
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
        string parentImageResourceKey = menu is MenuItem imageParent ? GetParentImageResourceKey(imageParent) : null;
        if (menu is MenuItem parentItem)
        {
            parentIcon = ContextMenuActionIcons.GetIcon(parentItem, parentIcon);
            parentImageResourceKey = ContextMenuActionIcons.GetImageResourceKey(parentItem, parentImageResourceKey);
        }

        foreach (object entry in menu.Items)
        {
            if ((entry as MenuItem ?? menu.ItemContainerGenerator.ContainerFromItem(entry) as MenuItem) is { } item)
            {
                // Menu-bar headers keep their normal layout and downward-opening popup.
                if (menu is not Menu)
                    PopulateItem(item, parentIcon, parentImageResourceKey);
                PopulateMenu(item);
            }
        }
    }

    private static void PopulateItem(MenuItem item, EFontAwesomeIcon? parentIcon, string parentImageResourceKey)
    {
        // The pinned-file picker is embedded as a full control rather than an action label.
        if (item.Header is UserControl)
            return;

        var generatedIcon = (FrameworkElement)item.GetValue(GeneratedIconProperty);
        if (!BindingOperations.IsDataBound(item, MenuItem.IconProperty)
            && (item.Icon is null || ReferenceEquals(item.Icon, generatedIcon)))
        {
            string resourceKey = ContextMenuActionIcons.GetImageResourceKey(item, parentImageResourceKey);
            if (generatedIcon is null || resourceKey != (string)item.GetValue(GeneratedIconResourceKeyProperty))
            {
                // Menu image resources are x:Shared=False: each action needs its own control.
                generatedIcon = resourceKey is not null ? item.TryFindResource(resourceKey) as FrameworkElement : null;
                if (generatedIcon is null)
                {
                    generatedIcon = new ImageAwesome { Width = 16, Height = 16 };
                    generatedIcon.SetBinding(ImageAwesome.ForegroundProperty, new Binding(nameof(item.Foreground)) { Source = item });
                }
                generatedIcon.IsHitTestVisible = false;
                generatedIcon.Focusable = false;
                item.SetValue(GeneratedIconProperty, generatedIcon);
                item.SetValue(GeneratedIconResourceKeyProperty, resourceKey);
            }

            if (generatedIcon is ImageAwesome glyph)
                glyph.Icon = ContextMenuActionIcons.GetIcon(item, parentIcon);
            item.SetCurrentValue(MenuItem.IconProperty, generatedIcon);
        }

        var appliedTemplate = (ControlTemplate)item.GetValue(AppliedTemplateProperty);
        if (ReferenceEquals(item.Template, appliedTemplate) && appliedTemplate is not null)
            return;

        ValueSource templateSource = DependencyPropertyHelper.GetValueSource(item, Control.TemplateProperty);
        if (BindingOperations.IsDataBound(item, Control.TemplateProperty)
            || templateSource.BaseValueSource is BaseValueSource.Local or BaseValueSource.StyleTrigger or BaseValueSource.ParentTemplateTrigger
            || HasCustomTemplate(item))
            return;

        if (item.TryFindResource("ContextMenuActionItemTemplate") is ControlTemplate template)
        {
            item.SetCurrentValue(Control.TemplateProperty, template);
            item.SetValue(AppliedTemplateProperty, template);
        }
    }

    private static bool HasCustomTemplate(MenuItem item)
    {
        if (DependencyPropertyHelper.GetValueSource(item, FrameworkElement.StyleProperty).BaseValueSource == BaseValueSource.ImplicitStyleReference)
            return false;

        // Explicit styles can host input fields or other controls instead of an action row.
        // A BasedOn reference to the ordinary theme style is still safe to replace.
        var implicitStyle = item.TryFindResource(typeof(MenuItem)) as Style;
        for (Style style = item.Style; style is not null && !ReferenceEquals(style, implicitStyle); style = style.BasedOn)
        {
            foreach (SetterBase setterBase in style.Setters)
            {
                if (setterBase is Setter { Property: var property } && property == Control.TemplateProperty)
                    return true;
            }
        }
        return false;
    }
}
