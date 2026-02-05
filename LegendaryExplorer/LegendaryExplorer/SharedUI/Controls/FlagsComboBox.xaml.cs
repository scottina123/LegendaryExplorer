using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LegendaryExplorer.SharedUI.Controls
{
    /// <summary>
    /// A native WPF ComboBox with checkboxes for multi-selection of flags.
    /// Respects Windows theme settings (light/dark mode) via SystemColors.
    /// </summary>
    public partial class FlagsComboBox : UserControl, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<ItemSelectionChangedEventArgs> ItemSelectionChanged;

        private bool _isUpdatingSelection;

        public FlagsComboBox()
        {
            InitializeComponent();
        }

        #region Dependency Properties

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(FlagsComboBox),
                new PropertyMetadata(null, OnItemsSourceChanged));

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty DelimiterProperty =
            DependencyProperty.Register(nameof(Delimiter), typeof(string), typeof(FlagsComboBox),
                new PropertyMetadata(" ", OnDelimiterChanged));

        public string Delimiter
        {
            get => (string)GetValue(DelimiterProperty);
            set => SetValue(DelimiterProperty, value);
        }

        public static readonly DependencyProperty SelectedValueProperty =
            DependencyProperty.Register(nameof(SelectedValue), typeof(string), typeof(FlagsComboBox),
                new PropertyMetadata(string.Empty, OnSelectedValueChanged));

        public string SelectedValue
        {
            get => (string)GetValue(SelectedValueProperty);
            set => SetValue(SelectedValueProperty, value);
        }

        public static readonly DependencyProperty IsEditableProperty =
            DependencyProperty.Register(nameof(IsEditable), typeof(bool), typeof(FlagsComboBox),
                new PropertyMetadata(false));

        public bool IsEditable
        {
            get => (bool)GetValue(IsEditableProperty);
            set => SetValue(IsEditableProperty, value);
        }

        #endregion

        #region Properties

        private List<SelectableItem> _selectableItems = new();

        /// <summary>
        /// Gets the original items (not the SelectableItem wrappers)
        /// </summary>
        public IEnumerable Items => _selectableItems.Select(si => si.Item);

        /// <summary>
        /// Gets the selected text representation
        /// </summary>
        public string SelectedText => string.Join(Delimiter, _selectableItems.Where(si => si.IsSelected).Select(si => si.Item?.ToString() ?? string.Empty));

        /// <summary>
        /// ItemContainerGenerator for compatibility - returns a fake generator that supports the IsSelected check pattern
        /// </summary>
        public FlagsComboBoxItemContainerGenerator ItemContainerGenerator { get; private set; }

        #endregion

        #region Property Changed Handlers

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FlagsComboBox control)
            {
                control.RefreshItems();
            }
        }

        private static void OnDelimiterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FlagsComboBox control)
            {
                control.OnPropertyChanged(nameof(SelectedText));
            }
        }

        private static void OnSelectedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FlagsComboBox control && e.NewValue is string newValue)
            {
                control.SetSelectedFromString(newValue);
            }
        }

        #endregion

        #region Methods

        private void RefreshItems()
        {
            _selectableItems.Clear();

            if (ItemsSource != null)
            {
                foreach (var item in ItemsSource)
                {
                    var selectableItem = new SelectableItem { Item = item, IsSelected = false };
                    selectableItem.PropertyChanged += SelectableItem_PropertyChanged;
                    _selectableItems.Add(selectableItem);
                }
            }

            PART_ComboBox.ItemsSource = _selectableItems;
            ItemContainerGenerator = new FlagsComboBoxItemContainerGenerator(_selectableItems);
            SetSelectedFromString(SelectedValue);
            OnPropertyChanged(nameof(SelectedText));
        }

        private void SetSelectedFromString(string value)
        {
            _isUpdatingSelection = true;
            try
            {
                if (string.IsNullOrEmpty(value))
                {
                    foreach (var item in _selectableItems)
                    {
                        item.IsSelected = false;
                    }
                }
                else
                {
                    var selectedStrings = new HashSet<string>(
                        value.Split(new[] { Delimiter }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(s => s.Trim()));

                    foreach (var item in _selectableItems)
                    {
                        item.IsSelected = selectedStrings.Contains(item.Item?.ToString() ?? string.Empty);
                    }
                }

                OnPropertyChanged(nameof(SelectedText));
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        private void SelectableItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectableItem.IsSelected))
            {
                OnPropertyChanged(nameof(SelectedText));
            }
        }

        private void CheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingSelection) return;
            
            if (sender is CheckBox checkBox && checkBox.DataContext is SelectableItem selectableItem)
            {
                ItemSelectionChanged?.Invoke(this, new ItemSelectionChangedEventArgs(selectableItem.Item, selectableItem.IsSelected));
            }
            OnPropertyChanged(nameof(SelectedText));
        }

        private void PART_ComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            // Forward the GotFocus event
            RaiseEvent(new RoutedEventArgs(GotFocusEvent, this));
        }

        private void PART_ComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            // Find the internal TextBox (PART_EditableTextBox) and apply theme-aware styling
            if (sender is ComboBox comboBox)
            {
                var textBox = comboBox.Template?.FindName("PART_EditableTextBox", comboBox) as TextBox;
                if (textBox != null)
                {
                    // Bind to SystemColors which will be overridden by DarkTheme.xaml when dark mode is active
                    textBox.SetResourceReference(TextBox.BackgroundProperty, SystemColors.WindowBrushKey);
                    textBox.SetResourceReference(TextBox.ForegroundProperty, SystemColors.WindowTextBrushKey);
                    textBox.SetResourceReference(TextBox.CaretBrushProperty, SystemColors.WindowTextBrushKey);
                }
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region Helper Classes

        public class SelectableItem : INotifyPropertyChanged
        {
            private bool _isSelected;
            private object _item;

            public object Item
            {
                get => _item;
                set
                {
                    _item = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Item)));
                }
            }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected != value)
                    {
                        _isSelected = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        /// <summary>
        /// A fake ItemContainerGenerator that provides objects for compatibility
        /// with existing code that checks for selection state
        /// </summary>
        public class FlagsComboBoxItemContainerGenerator
        {
            private readonly List<SelectableItem> _items;

            public FlagsComboBoxItemContainerGenerator(List<SelectableItem> items)
            {
                _items = items;
            }

            /// <summary>
            /// Returns a ListBoxItem-compatible wrapper for the given item to support the
            /// is SelectorItem { IsSelected: true } pattern
            /// </summary>
            public ListBoxItem ContainerFromItem(object item)
            {
                var selectableItem = _items.FirstOrDefault(si => Equals(si.Item, item));
                if (selectableItem != null)
                {
                    // Return a ListBoxItem (which derives from ContentControl and has IsSelected)
                    var container = new ListBoxItem { IsSelected = selectableItem.IsSelected };
                    return container;
                }
                return null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Event args for item selection changed in FlagsComboBox
    /// </summary>
    public class ItemSelectionChangedEventArgs : EventArgs
    {
        public object Item { get; }
        public bool IsSelected { get; }

        public ItemSelectionChangedEventArgs(object item, bool isSelected)
        {
            Item = item;
            IsSelected = isSelected;
        }
    }
}
