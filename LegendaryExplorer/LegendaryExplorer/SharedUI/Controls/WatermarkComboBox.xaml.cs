using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LegendaryExplorer.SharedUI.Controls
{
    /// <summary>
    /// A ComboBox with watermark/placeholder text that respects theme settings.
    /// </summary>
    public partial class WatermarkComboBox : UserControl
    {
        public WatermarkComboBox()
        {
            InitializeComponent();
        }

        #region Dependency Properties

        public static readonly DependencyProperty WatermarkProperty =
            DependencyProperty.Register(nameof(Watermark), typeof(string), typeof(WatermarkComboBox),
                new PropertyMetadata(string.Empty));

        public string Watermark
        {
            get => (string)GetValue(WatermarkProperty);
            set => SetValue(WatermarkProperty, value);
        }

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(WatermarkComboBox),
                new PropertyMetadata(null));

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(WatermarkComboBox),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public object SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(WatermarkComboBox),
                new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        public static readonly DependencyProperty IsTextSearchEnabledProperty =
            DependencyProperty.Register(nameof(IsTextSearchEnabled), typeof(bool), typeof(WatermarkComboBox),
                new PropertyMetadata(true));

        public bool IsTextSearchEnabled
        {
            get => (bool)GetValue(IsTextSearchEnabledProperty);
            set => SetValue(IsTextSearchEnabledProperty, value);
        }

        public static readonly DependencyProperty VerticalContentAlignmentProperty =
            DependencyProperty.Register(nameof(VerticalContentAlignment), typeof(VerticalAlignment), typeof(WatermarkComboBox),
                new PropertyMetadata(VerticalAlignment.Center));

        public new VerticalAlignment VerticalContentAlignment
        {
            get => (VerticalAlignment)GetValue(VerticalContentAlignmentProperty);
            set => SetValue(VerticalContentAlignmentProperty, value);
        }

        public static readonly DependencyProperty ItemsPanelProperty =
            DependencyProperty.Register(nameof(ItemsPanel), typeof(ItemsPanelTemplate), typeof(WatermarkComboBox),
                new PropertyMetadata(null, OnItemsPanelChanged));

        public ItemsPanelTemplate ItemsPanel
        {
            get => (ItemsPanelTemplate)GetValue(ItemsPanelProperty);
            set => SetValue(ItemsPanelProperty, value);
        }

        private static void OnItemsPanelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is WatermarkComboBox control && e.NewValue is ItemsPanelTemplate template)
            {
                control.PART_ComboBox.ItemsPanel = template;
            }
        }

        #endregion

        #region Events

        public new event KeyEventHandler KeyUp
        {
            add => PART_ComboBox.KeyUp += value;
            remove => PART_ComboBox.KeyUp -= value;
        }

        public new event KeyEventHandler KeyDown
        {
            add => PART_ComboBox.KeyDown += value;
            remove => PART_ComboBox.KeyDown -= value;
        }

        public event SelectionChangedEventHandler SelectionChanged
        {
            add => PART_ComboBox.SelectionChanged += value;
            remove => PART_ComboBox.SelectionChanged -= value;
        }

        #endregion

        #region Event Handlers

        private void PART_ComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            // Apply ItemsPanel if set
            if (ItemsPanel != null)
            {
                PART_ComboBox.ItemsPanel = ItemsPanel;
            }
        }

        #endregion

        #region Methods

        public new void Focus()
        {
            PART_ComboBox.Focus();
        }

        #endregion
    }
}
