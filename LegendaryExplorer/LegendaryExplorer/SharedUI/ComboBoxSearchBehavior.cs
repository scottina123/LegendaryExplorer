using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xceed.Wpf.Toolkit;

namespace LegendaryExplorer.SharedUI;

/// <summary>
/// Adds a filter box to application dropdown lists without requiring every ComboBox declaration
/// to opt in individually.
/// </summary>
public static class ComboBoxSearchBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ComboBoxSearchBehavior),
        new FrameworkPropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SearchMemberPathProperty = DependencyProperty.RegisterAttached(
        "SearchMemberPath",
        typeof(string),
        typeof(ComboBoxSearchBehavior),
        new FrameworkPropertyMetadata(null));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(SearchState),
        typeof(ComboBoxSearchBehavior));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked",
        typeof(bool),
        typeof(ComboBoxSearchBehavior));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static string GetSearchMemberPath(DependencyObject element) => (string)element.GetValue(SearchMemberPathProperty);

    public static void SetSearchMemberPath(DependencyObject element, string value) => element.SetValue(SearchMemberPathProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            Hook(element);
        }
        else
        {
            Unhook(element);
        }
    }

    private static void Hook(DependencyObject element)
    {
        if ((bool)element.GetValue(IsHookedProperty))
        {
            return;
        }

        switch (element)
        {
            case ComboBox comboBox:
                comboBox.DropDownOpened += OnComboBoxOpened;
                comboBox.DropDownClosed += OnComboBoxClosed;
                break;
            case CheckComboBox checkComboBox:
                checkComboBox.Opened += OnCheckComboBoxOpened;
                checkComboBox.Closed += OnCheckComboBoxClosed;
                break;
            default:
                return;
        }

        element.SetValue(IsHookedProperty, true);
    }

    private static void Unhook(DependencyObject element)
    {
        if (!(bool)element.GetValue(IsHookedProperty))
        {
            return;
        }

        switch (element)
        {
            case ComboBox comboBox:
                comboBox.DropDownOpened -= OnComboBoxOpened;
                comboBox.DropDownClosed -= OnComboBoxClosed;
                Close(comboBox);
                break;
            case CheckComboBox checkComboBox:
                checkComboBox.Opened -= OnCheckComboBoxOpened;
                checkComboBox.Closed -= OnCheckComboBoxClosed;
                Close(checkComboBox);
                break;
        }

        element.SetValue(IsHookedProperty, false);
    }

    private static void OnComboBoxOpened(object sender, EventArgs e)
    {
        if (sender is ComboBox comboBox && GetIsEnabled(comboBox))
        {
            comboBox.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (comboBox.IsDropDownOpen)
                {
                    Open(comboBox, FindPopup(comboBox), () => comboBox.IsDropDownOpen = false);
                }
            });
        }
    }

    private static void OnComboBoxClosed(object sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            Close(comboBox);
        }
    }

    private static void OnCheckComboBoxOpened(object sender, EventArgs e)
    {
        if (sender is CheckComboBox comboBox && GetIsEnabled(comboBox))
        {
            comboBox.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (comboBox.IsDropDownOpen)
                {
                    Open(comboBox, FindPopup(comboBox), () => comboBox.IsDropDownOpen = false);
                }
            });
        }
    }

    private static void OnCheckComboBoxClosed(object sender, EventArgs e)
    {
        if (sender is CheckComboBox comboBox)
        {
            Close(comboBox);
        }
    }

    private static Popup FindPopup(Control control)
    {
        control.ApplyTemplate();

        return control.Template?.FindName("PART_Popup", control) as Popup
               ?? control.Template?.FindName("Popup", control) as Popup
               ?? FindVisualChild<Popup>(control);
    }

    private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
            {
                return result;
            }

            result = FindVisualChild<T>(child);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static void Open(ItemsControl owner, Popup popup, Action closeDropDown)
    {
        if (popup?.Child is not UIElement popupContent)
        {
            return;
        }

        var state = (SearchState)owner.GetValue(StateProperty);
        if (state == null || state.Popup != popup)
        {
            state = CreateState(owner, popup, popupContent, closeDropDown);
            owner.SetValue(StateProperty, state);
        }

        if (!state.IsFiltering)
        {
            state.IsFiltering = true;
            state.SearchTextCache.Clear();
            state.CanFilter = owner.Items.CanFilter;
            state.OriginalFilter = state.CanFilter ? owner.Items.Filter : null;
        }

        state.IgnoreTextChanges = true;
        state.SearchBox.Clear();
        state.IgnoreTextChanges = false;
        ApplyFilter(state);

        state.SearchBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (popup.IsOpen)
            {
                state.SearchBox.Focus();
                state.SearchBox.SelectAll();
            }
        });
    }

    private static SearchState CreateState(ItemsControl owner, Popup popup, UIElement popupContent, Action closeDropDown)
    {
        popup.Child = null;

        var searchBox = new TextBox
        {
            Margin = new Thickness(4, 4, 4, 2),
            MinHeight = 24,
            Padding = new Thickness(4, 2, 22, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        searchBox.SetResourceReference(Control.BackgroundProperty, SystemColors.WindowBrushKey);
        searchBox.SetResourceReference(Control.ForegroundProperty, SystemColors.WindowTextBrushKey);
        searchBox.SetResourceReference(Control.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
        AutomationProperties.SetName(searchBox, "Filter dropdown items");

        var placeholder = new TextBlock
        {
            Text = "Filter items...",
            Margin = new Thickness(9, 0, 26, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);

        var searchIcon = new TextBlock
        {
            Text = "\u2315",
            Margin = new Thickness(0, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        searchIcon.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);

        var searchArea = new Grid();
        DockPanel.SetDock(searchArea, Dock.Top);
        searchArea.Children.Add(searchBox);
        searchArea.Children.Add(placeholder);
        searchArea.Children.Add(searchIcon);

        var wrapper = new DockPanel
        {
            LastChildFill = true,
            SnapsToDevicePixels = true
        };
        wrapper.SetResourceReference(Panel.BackgroundProperty, SystemColors.WindowBrushKey);
        wrapper.SetBinding(FrameworkElement.MinWidthProperty, new Binding(nameof(FrameworkElement.ActualWidth)) { Source = owner });
        wrapper.Children.Add(searchArea);
        wrapper.Children.Add(popupContent);
        popup.Child = wrapper;

        var state = new SearchState(owner, popup, searchBox, placeholder, closeDropDown);
        searchBox.TextChanged += (_, _) =>
        {
            placeholder.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (!state.IgnoreTextChanges)
            {
                ApplyFilter(state);
            }
        };
        searchBox.PreviewKeyDown += (_, args) => OnSearchBoxKeyDown(state, args);
        return state;
    }

    private static void OnSearchBoxKeyDown(SearchState state, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (!string.IsNullOrEmpty(state.SearchBox.Text))
        {
            state.SearchBox.Clear();
        }
        else
        {
            state.CloseDropDown();
        }

        e.Handled = true;
    }

    private static void ApplyFilter(SearchState state)
    {
        if (!state.IsFiltering || !state.CanFilter)
        {
            return;
        }

        string query = state.SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            state.Owner.Items.Filter = state.OriginalFilter;
            return;
        }

        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        state.Owner.Items.Filter = item =>
            (state.OriginalFilter?.Invoke(item) ?? true)
            && MatchesAllTerms(GetItemSearchText(state, item), terms);
    }

    private static bool MatchesAllTerms(string candidate, IReadOnlyList<string> terms)
    {
        for (int i = 0; i < terms.Count; i++)
        {
            if (candidate.IndexOf(terms[i], StringComparison.CurrentCultureIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetItemSearchText(SearchState state, object item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        if (state.SearchTextCache.TryGetValue(item, out string cachedText))
        {
            return cachedText;
        }

        var text = new StringBuilder();
        object displayedItem = item;

        if (item is ComboBoxItem comboBoxItem)
        {
            AddSearchValue(text, TextSearch.GetText(comboBoxItem));
            displayedItem = comboBoxItem.Content;
            AddSearchValue(text, GetElementText(displayedItem));
        }

        string memberPath = GetSearchMemberPath(state.Owner);
        if (string.IsNullOrWhiteSpace(memberPath))
        {
            memberPath = TextSearch.GetTextPath(state.Owner);
        }
        if (string.IsNullOrWhiteSpace(memberPath))
        {
            memberPath = state.Owner.DisplayMemberPath;
        }

        if (!string.IsNullOrWhiteSpace(memberPath))
        {
            AddSearchValue(text, ReadMemberPath(displayedItem, memberPath));
        }

        string defaultText = Convert.ToString(displayedItem, CultureInfo.CurrentCulture);
        AddSearchValue(text, defaultText);

        // Data templates frequently bind one or more simple properties while the model itself
        // retains Object.ToString(). Include those values so filtering follows what users see.
        if (state.Owner.ItemTemplate != null || LooksLikeTypeName(displayedItem, defaultText))
        {
            AddSimplePropertyValues(text, displayedItem);
        }

        string result = text.ToString();
        state.SearchTextCache[item] = result;
        return result;
    }

    private static object ReadMemberPath(object item, string memberPath)
    {
        object value = item;
        foreach (string memberName in memberPath.Split('.'))
        {
            if (value == null)
            {
                return null;
            }

            PropertyDescriptor property = TypeDescriptor.GetProperties(value).Find(memberName, true);
            if (property == null)
            {
                return null;
            }

            try
            {
                value = property.GetValue(value);
            }
            catch
            {
                return null;
            }
        }

        return value;
    }

    private static string GetElementText(object value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string text:
                return text;
            case TextBlock textBlock:
                return textBlock.Text;
            case ContentControl contentControl:
                return GetElementText(contentControl.Content);
            case Panel panel:
            {
                var text = new StringBuilder();
                foreach (UIElement child in panel.Children)
                {
                    AddSearchValue(text, GetElementText(child));
                }
                return text.ToString();
            }
            default:
                return Convert.ToString(value, CultureInfo.CurrentCulture);
        }
    }

    private static bool LooksLikeTypeName(object item, string text)
    {
        if (item == null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        Type type = item.GetType();
        return string.Equals(text, type.FullName, StringComparison.Ordinal)
               || string.Equals(text, type.Name, StringComparison.Ordinal);
    }

    private static void AddSimplePropertyValues(StringBuilder text, object item)
    {
        if (item == null || item is string)
        {
            return;
        }

        foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(item))
        {
            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!IsSimpleSearchValue(propertyType))
            {
                continue;
            }

            try
            {
                AddSearchValue(text, property.GetValue(item));
            }
            catch
            {
                // A display model should not break its dropdown because one optional property getter failed.
            }
        }
    }

    private static bool IsSimpleSearchValue(Type type) =>
        type == typeof(string)
        || type == typeof(char)
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(Guid)
        || type.IsPrimitive
        || type.IsEnum;

    private static void AddSearchValue(StringBuilder text, object value)
    {
        string stringValue = Convert.ToString(value, CultureInfo.CurrentCulture);
        if (string.IsNullOrWhiteSpace(stringValue))
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append(' ');
        }
        text.Append(stringValue);
    }

    private static void Close(ItemsControl owner)
    {
        if (owner.GetValue(StateProperty) is not SearchState { IsFiltering: true } state)
        {
            return;
        }

        if (state.CanFilter)
        {
            owner.Items.Filter = state.OriginalFilter;
        }

        state.IsFiltering = false;
        state.OriginalFilter = null;
        state.SearchTextCache.Clear();
        state.IgnoreTextChanges = true;
        state.SearchBox.Clear();
        state.IgnoreTextChanges = false;
        state.Placeholder.Visibility = Visibility.Visible;
    }

    private sealed class SearchState(
        ItemsControl owner,
        Popup popup,
        TextBox searchBox,
        TextBlock placeholder,
        Action closeDropDown)
    {
        public ItemsControl Owner { get; } = owner;
        public Popup Popup { get; } = popup;
        public TextBox SearchBox { get; } = searchBox;
        public TextBlock Placeholder { get; } = placeholder;
        public Action CloseDropDown { get; } = closeDropDown;
        public Dictionary<object, string> SearchTextCache { get; } = new();
        public Predicate<object> OriginalFilter { get; set; }
        public bool CanFilter { get; set; }
        public bool IgnoreTextChanges { get; set; }
        public bool IsFiltering { get; set; }
    }
}
