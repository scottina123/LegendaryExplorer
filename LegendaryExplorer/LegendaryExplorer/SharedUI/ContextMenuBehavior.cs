using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace LegendaryExplorer.SharedUI
{
    internal static class ContextMenuBehavior
    {
        private static bool IsEnabled;

        public static void EnableAlphabeticalSorting()
        {
            if (IsEnabled)
            {
                return;
            }

            IsEnabled = true;
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnMenuOpened));
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnMenuOpened));
        }

        private static void OnMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not ItemsControl menu)
            {
                return;
            }

            menu.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () => SortItems(menu));
        }

        private static void SortItems(ItemsControl menu)
        {
            if (menu.ItemsSource is not null)
            {
                SortItemsSource(menu);
                return;
            }

            int segmentStart = 0;
            while (segmentStart < menu.Items.Count)
            {
                if (menu.Items[segmentStart] is not MenuItem)
                {
                    segmentStart++;
                    continue;
                }

                int segmentEnd = segmentStart + 1;
                while (segmentEnd < menu.Items.Count && menu.Items[segmentEnd] is MenuItem)
                {
                    segmentEnd++;
                }

                SortSegment(menu, segmentStart, segmentEnd);
                segmentStart = segmentEnd;
            }
        }

        private static void SortSegment(ItemsControl menu, int start, int end)
        {
            MenuItem[] sortedItems = menu.Items
                .Cast<object>()
                .Skip(start)
                .Take(end - start)
                .Cast<MenuItem>()
                .OrderBy(GetMenuItemText, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            for (int offset = 0; offset < sortedItems.Length; offset++)
            {
                int targetIndex = start + offset;
                int currentIndex = menu.Items.IndexOf(sortedItems[offset]);
                if (currentIndex == targetIndex)
                {
                    continue;
                }

                menu.Items.RemoveAt(currentIndex);
                menu.Items.Insert(targetIndex, sortedItems[offset]);
            }
        }

        private static void SortItemsSource(ItemsControl menu)
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(menu.ItemsSource);
            if (view is ListCollectionView listCollectionView)
            {
                listCollectionView.CustomSort = new MenuItemComparer(menu, view.Cast<object>().ToArray());
            }
        }

        private static string GetMenuItemText(MenuItem menuItem)
        {
            string text = GetText(menuItem.Header);
            if (string.IsNullOrWhiteSpace(text) && menuItem.Command is RoutedUICommand command)
            {
                text = command.Text;
            }

            return RemoveAccessKeys(text).Trim();
        }

        private static string GetText(object value)
        {
            return value switch
            {
                null => string.Empty,
                string text => text,
                AccessText accessText => accessText.Text,
                TextBlock textBlock => textBlock.Text,
                ContentControl contentControl when !ReferenceEquals(contentControl.Content, contentControl) => GetText(contentControl.Content),
                _ => value.ToString() ?? string.Empty
            };
        }

        private static string RemoveAccessKeys(string text)
        {
            if (!text.Contains('_'))
            {
                return text;
            }

            var result = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '_' && i + 1 < text.Length)
                {
                    if (text[i + 1] == '_')
                    {
                        result.Append('_');
                        i++;
                    }

                    continue;
                }

                result.Append(text[i]);
            }

            return result.ToString();
        }

        private sealed class MenuItemComparer : IComparer
        {
            private readonly ItemsControl Menu;
            private readonly IReadOnlyList<object> OriginalItems;

            public MenuItemComparer(ItemsControl menu, IReadOnlyList<object> originalItems)
            {
                Menu = menu;
                OriginalItems = originalItems;
            }

            public int Compare(object x, object y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                int xGroup = GetGroup(x, out bool xIsSeparator);
                int yGroup = GetGroup(y, out bool yIsSeparator);
                int groupComparison = xGroup.CompareTo(yGroup);
                if (groupComparison != 0)
                {
                    return groupComparison;
                }

                if (xIsSeparator || yIsSeparator)
                {
                    return xIsSeparator.CompareTo(yIsSeparator);
                }

                return StringComparer.CurrentCultureIgnoreCase.Compare(GetItemText(x), GetItemText(y));
            }

            private int GetGroup(object item, out bool isSeparator)
            {
                int group = 0;
                foreach (object originalItem in OriginalItems)
                {
                    if (ReferenceEquals(originalItem, item))
                    {
                        isSeparator = originalItem is Separator;
                        return group;
                    }

                    if (originalItem is Separator)
                    {
                        group++;
                    }
                }

                isSeparator = item is Separator;
                return group;
            }

            private string GetItemText(object item)
            {
                if (Menu.ItemContainerGenerator.ContainerFromItem(item) is MenuItem menuItem)
                {
                    return GetMenuItemText(menuItem);
                }

                if (!string.IsNullOrWhiteSpace(Menu.DisplayMemberPath))
                {
                    PropertyDescriptor property = TypeDescriptor.GetProperties(item)[Menu.DisplayMemberPath];
                    if (property is not null)
                    {
                        return RemoveAccessKeys(GetText(property.GetValue(item))).Trim();
                    }
                }

                return RemoveAccessKeys(GetText(item)).Trim();
            }
        }
    }
}
