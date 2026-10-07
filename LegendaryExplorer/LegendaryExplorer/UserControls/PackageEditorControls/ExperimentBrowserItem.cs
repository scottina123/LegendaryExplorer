using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FontAwesome5;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.UserControls.PackageEditorControls
{
    internal sealed class ExperimentBrowserItem
    {
        private readonly MenuItem _menuItem;

        public string Name { get; }
        public string Category { get; }
        public string Description { get; }
        public EFontAwesomeIcon Icon { get; }
        public ImageSource IconImage { get; }
        public string SelectionKey => $"{Category}\n{Name}";

        public bool IsEnabled
        {
            get
            {
                if (!_menuItem.IsEnabled)
                {
                    return false;
                }

                if (_menuItem.Command is RoutedCommand routedCommand)
                {
                    return routedCommand.CanExecute(_menuItem.CommandParameter,
                        _menuItem.CommandTarget ?? _menuItem);
                }

                return _menuItem.Command?.CanExecute(_menuItem.CommandParameter) ?? true;
            }
        }

        public ExperimentBrowserItem(MenuItem menuItem, string name, string category, string description)
        {
            _menuItem = menuItem;
            Name = name;
            Category = category;
            Description = description;
            Icon = menuItem.Icon is ImageAwesome glyph ? glyph.Icon : ContextMenuActionIcons.GetIcon(menuItem);

            // Share image sources, not controls: menu icons may already have a visual parent.
            IconImage = menuItem.Icon switch
            {
                ImageAwesome => null,
                Image image => image.Source,
                ImageSource source => source,
                _ => null
            };
            if (IconImage == null && menuItem.Icon == null
                && ContextMenuActionIcons.GetImageResourceKey(menuItem) is string resourceKey)
            {
                IconImage = (menuItem.TryFindResource(resourceKey) as Image)?.Source;
            }
        }

        public void Invoke()
        {
            if (!IsEnabled)
            {
                return;
            }

            if (_menuItem.Command is RoutedCommand routedCommand)
            {
                routedCommand.Execute(_menuItem.CommandParameter, _menuItem.CommandTarget ?? _menuItem);
            }
            else if (_menuItem.Command is ICommand command)
            {
                command.Execute(_menuItem.CommandParameter);
            }
            else
            {
                if (_menuItem.IsCheckable)
                {
                    _menuItem.IsChecked = !_menuItem.IsChecked;
                }

                _menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, _menuItem));
            }
        }
    }

    internal static class ExperimentBrowserCatalog
    {
        public static IReadOnlyList<ExperimentBrowserItem> Create(IEnumerable<MenuItem> rootItems,
            Func<MenuItem, string> categorySelector)
        {
            var experiments = new List<ExperimentBrowserItem>();
            foreach (MenuItem menuItem in rootItems)
            {
                AddMenuItem(menuItem, categorySelector(menuItem), experiments);
            }

            return experiments
                .OrderBy(experiment => experiment.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(experiment => experiment.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string GetHeader(MenuItem menuItem)
        {
            return menuItem.Header switch
            {
                AccessText accessText => accessText.Text,
                null => string.Empty,
                var header => header.ToString()
            };
        }

        private static void AddMenuItem(MenuItem menuItem, string category,
            List<ExperimentBrowserItem> experiments)
        {
            if (menuItem.Visibility != Visibility.Visible)
            {
                return;
            }

            List<MenuItem> children = menuItem.Items.OfType<MenuItem>().ToList();
            if (children.Count > 0)
            {
                foreach (MenuItem child in children)
                {
                    AddMenuItem(child, category, experiments);
                }

                return;
            }

            string name = GetHeader(menuItem);
            if (string.IsNullOrWhiteSpace(name) || name.TrimStart().StartsWith(">>", StringComparison.Ordinal))
            {
                return;
            }

            string description = menuItem.ToolTip switch
            {
                ToolTip toolTip => toolTip.Content?.ToString(),
                null => null,
                var value => value.ToString()
            };
            experiments.Add(new ExperimentBrowserItem(menuItem, name, category, description));
        }
    }
}
