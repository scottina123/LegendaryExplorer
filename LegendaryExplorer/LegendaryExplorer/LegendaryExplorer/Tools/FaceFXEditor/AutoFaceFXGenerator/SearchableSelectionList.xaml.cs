using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace LegendaryExplorer.Tools.FaceFXEditor.AutoFaceFXGenerator
{
    /// <summary>
    /// An always-visible selection list with a text filter. The source is copied into
    /// a private view so filtering here cannot affect another control using the same collection.
    /// </summary>
    public partial class SearchableSelectionList : UserControl
    {
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(SearchableSelectionList),
            new PropertyMetadata(null, OnItemsSourceChanged));

        public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
            nameof(SelectedItem), typeof(object), typeof(SearchableSelectionList),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedItemChanged));

        public static readonly DependencyProperty SearchWatermarkProperty = DependencyProperty.Register(
            nameof(SearchWatermark), typeof(string), typeof(SearchableSelectionList),
            new PropertyMetadata("Search..."));

        private static readonly DependencyPropertyKey FilteredItemsPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(FilteredItems), typeof(ICollectionView),
                typeof(SearchableSelectionList), new PropertyMetadata(null));

        public static readonly DependencyProperty FilteredItemsProperty = FilteredItemsPropertyKey.DependencyProperty;

        private bool _isSynchronizingSelection;
        private string _searchText = string.Empty;

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public object SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public string SearchWatermark
        {
            get => (string)GetValue(SearchWatermarkProperty);
            set => SetValue(SearchWatermarkProperty, value);
        }

        public ICollectionView FilteredItems
        {
            get => (ICollectionView)GetValue(FilteredItemsProperty);
            private set => SetValue(FilteredItemsPropertyKey, value);
        }

        public SearchableSelectionList()
        {
            InitializeComponent();
            RebuildView(ItemsSource);
        }

        internal static bool MatchesSearch(object item, string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            string label = Convert.ToString(item, CultureInfo.CurrentCulture) ?? string.Empty;
            return searchText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .All(term => label.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        private static void OnItemsSourceChanged(DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs eventArgs)
        {
            var control = (SearchableSelectionList)dependencyObject;
            control.RebuildView(eventArgs.NewValue as IEnumerable);
        }

        private static void OnSelectedItemChanged(DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs eventArgs)
        {
            ((SearchableSelectionList)dependencyObject).SynchronizeListSelection();
        }

        private void RebuildView(IEnumerable source)
        {
            List<object> items = source?.Cast<object>().ToList() ?? [];
            FilteredItems = new ListCollectionView(items);
            _searchText = string.Empty;
            FilterBox?.Clear();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (FilteredItems == null)
                return;

            bool wasSynchronizingSelection = _isSynchronizingSelection;
            _isSynchronizingSelection = true;
            try
            {
                FilteredItems.Filter = string.IsNullOrWhiteSpace(_searchText)
                    ? null
                    : item => MatchesSearch(item, _searchText);
                FilteredItems.Refresh();
                SynchronizeListSelection();
            }
            finally
            {
                _isSynchronizingSelection = wasSynchronizingSelection;
            }
        }

        private void SynchronizeListSelection()
        {
            if (SelectionList == null || FilteredItems == null)
                return;

            bool wasSynchronizingSelection = _isSynchronizingSelection;
            _isSynchronizingSelection = true;
            try
            {
                SelectionList.SelectedItem = SelectedItem != null && FilteredItems.Contains(SelectedItem)
                    ? SelectedItem
                    : null;
            }
            finally
            {
                _isSynchronizingSelection = wasSynchronizingSelection;
            }
        }

        private void FilterBox_OnTextChanged(object sender, TextChangedEventArgs eventArgs)
        {
            _searchText = FilterBox.Text;
            ApplyFilter();
        }

        private void FilterBox_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
        {
            if (eventArgs.Key != Key.Down)
                return;

            object firstItem = FilteredItems?.Cast<object>().FirstOrDefault();
            if (firstItem == null)
                return;

            SelectionList.SelectedItem = firstItem;
            SelectionList.ScrollIntoView(firstItem);
            SelectionList.Focus();
            eventArgs.Handled = true;
        }

        private void SelectionList_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
        {
            if (!_isSynchronizingSelection && SelectionList.SelectedItem != null)
                SetCurrentValue(SelectedItemProperty, SelectionList.SelectedItem);
        }
    }
}
