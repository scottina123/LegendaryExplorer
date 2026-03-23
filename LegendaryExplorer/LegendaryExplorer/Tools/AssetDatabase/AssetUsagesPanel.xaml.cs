using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LegendaryExplorer.Tools.AssetDatabase
{
    public partial class AssetUsagesPanel : UserControl
    {
        public static readonly DependencyProperty UsagesSourceProperty =
            DependencyProperty.Register(nameof(UsagesSource), typeof(IEnumerable), typeof(AssetUsagesPanel),
                new PropertyMetadata(null, OnUsagesSourceChanged));

        public static readonly DependencyProperty FilterTextProperty =
            DependencyProperty.Register(nameof(FilterText), typeof(string), typeof(AssetUsagesPanel),
                new PropertyMetadata(string.Empty, OnFilterTextChanged));

        public static readonly DependencyProperty HeaderTextProperty =
            DependencyProperty.Register(nameof(HeaderText), typeof(string), typeof(AssetUsagesPanel),
                new PropertyMetadata("Usages"));

        public static readonly DependencyProperty HeaderToolTipProperty =
            DependencyProperty.Register(nameof(HeaderToolTip), typeof(string), typeof(AssetUsagesPanel));

        public static readonly DependencyProperty UsageContextMenuProperty =
            DependencyProperty.Register(nameof(UsageContextMenu), typeof(ContextMenu), typeof(AssetUsagesPanel),
                new PropertyMetadata(null, OnUsageContextMenuChanged));

        public IEnumerable UsagesSource
        {
            get => (IEnumerable)GetValue(UsagesSourceProperty);
            set => SetValue(UsagesSourceProperty, value);
        }

        public string HeaderText
        {
            get => (string)GetValue(HeaderTextProperty);
            set => SetValue(HeaderTextProperty, value);
        }

        public string FilterText
        {
            get => (string)GetValue(FilterTextProperty);
            set => SetValue(FilterTextProperty, value);
        }

        public string HeaderToolTip
        {
            get => (string)GetValue(HeaderToolTipProperty);
            set => SetValue(HeaderToolTipProperty, value);
        }

        public ContextMenu UsageContextMenu
        {
            get => (ContextMenu)GetValue(UsageContextMenuProperty);
            set => SetValue(UsageContextMenuProperty, value);
        }

        public object SelectedItem => internalListBox.SelectedItem;

        public int SelectedIndex => internalListBox.SelectedIndex;

        public AssetUsagesPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (internalListBox.ContextMenu == null && UsageContextMenu == null)
            {
                SetDefaultContextMenu();
            }

            RefreshFilter();
        }

        private static void OnUsageContextMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AssetUsagesPanel panel)
            {
                panel.internalListBox.ContextMenu = e.NewValue as ContextMenu;
            }
        }

        private static void OnUsagesSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AssetUsagesPanel panel)
            {
                panel.RefreshFilter();
            }
        }

        private static void OnFilterTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AssetUsagesPanel panel)
            {
                panel.RefreshFilter();
            }
        }

        public void RefreshFilter()
        {
            if (internalListBox?.ItemsSource == null)
            {
                return;
            }

            ICollectionView view = CollectionViewSource.GetDefaultView(internalListBox.ItemsSource);
            if (view == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(FilterText))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = item => Window.GetWindow(this) is AssetDatabaseWindow window
                    && window.UsageMatchesSearch(item, FilterText);
            }

            view.Refresh();
        }

        private void SetDefaultContextMenu()
        {
            var menu = new ContextMenu();
            var openUsageItem = new MenuItem
            {
                Header = "Open Usage",
                ToolTip = "Opens this Usage in Package Editor."
            };
            openUsageItem.SetBinding(MenuItem.CommandProperty,
                new System.Windows.Data.Binding("OpenUsagePkgCommand"));

            var openExplorerItem = new MenuItem
            {
                Header = "Open in Windows Explorer",
                ToolTip = "Opens this file in Windows Explorer."
            };
            openExplorerItem.SetBinding(MenuItem.CommandProperty,
                new System.Windows.Data.Binding("OpenInWindowsExplorerCommand"));

            menu.Items.Add(openUsageItem);
            menu.Items.Add(openExplorerItem);
            internalListBox.ContextMenu = menu;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is AssetDatabaseWindow window)
            {
                window.CopyUsagesFromPanel(this);
            }
        }
    }
}
