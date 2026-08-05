using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryExplorer.SharedUI.Bases;
using LegendaryExplorerCore.Misc;
using MessageBox = Xceed.Wpf.Toolkit.MessageBox;

namespace LegendaryExplorer.Dialogs
{
    /// <summary>
    /// Dialog that has copy button, designed for showing lists of short lines of text
    /// </summary>
    public partial class ListDialog : TrackingNotifyPropertyChangedWindowBase
    {
        public ObservableCollectionExtended<object> Items { get; } = new();
        // Backwards-compatible handler for EntryStringPair items
        public Action<EntryStringPair> DoubleClickEntryHandler { get; set; }
        public Action SecondaryActionHandler { get; set; }
        public Action TertiaryActionHandler { get; set; }
        public Action QuaternaryActionHandler { get; set; }
        public Action QuinaryActionHandler { get; set; }
        // General-purpose handler for arbitrary list items (strings, etc.)
        public Action<object> DoubleClickItemHandler { get; set; }
        private string topText;
        private string secondaryActionText;
        private Visibility secondaryActionVisibility = Visibility.Collapsed;
        private string tertiaryActionText;
        private Visibility tertiaryActionVisibility = Visibility.Collapsed;
        private string quaternaryActionText;
        private Visibility quaternaryActionVisibility = Visibility.Collapsed;
        private string quinaryActionText;
        private Visibility quinaryActionVisibility = Visibility.Collapsed;

        public string TopText
        {
            get => topText;
            set => SetProperty(ref topText, value);
        }

        public string SecondaryActionText
        {
            get => secondaryActionText;
            set
            {
                if (SetProperty(ref secondaryActionText, value))
                {
                    SecondaryActionVisibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        public Visibility SecondaryActionVisibility
        {
            get => secondaryActionVisibility;
            set => SetProperty(ref secondaryActionVisibility, value);
        }

        public string TertiaryActionText
        {
            get => tertiaryActionText;
            set
            {
                if (SetProperty(ref tertiaryActionText, value))
                {
                    TertiaryActionVisibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        public Visibility TertiaryActionVisibility
        {
            get => tertiaryActionVisibility;
            set => SetProperty(ref tertiaryActionVisibility, value);
        }

        public string QuaternaryActionText
        {
            get => quaternaryActionText;
            set
            {
                if (SetProperty(ref quaternaryActionText, value))
                {
                    QuaternaryActionVisibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        public Visibility QuaternaryActionVisibility
        {
            get => quaternaryActionVisibility;
            set => SetProperty(ref quaternaryActionVisibility, value);
        }

        public string QuinaryActionText
        {
            get => quinaryActionText;
            set
            {
                if (SetProperty(ref quinaryActionText, value))
                {
                    QuinaryActionVisibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        public Visibility QuinaryActionVisibility
        {
            get => quinaryActionVisibility;
            set => SetProperty(ref quinaryActionVisibility, value);
        }

        private ListDialog(string title, string message, Window owner, int width = 0, int height = 0) : base("List Dialog", false)
        {
            DataContext = this;
            InitializeComponent();
            Title = title;
            if (width != 0)
            {
                Width = width;
            }
            if (height != 0)
            {
                Height = height;
            }
            Owner = owner;
            TopText = message;
        }

        public ListDialog(IEnumerable<EntryStringPair> listItems, string title, string message, Window owner, int width = 0, int height = 0) : this(title, message, owner, width, height)
        {
            Items.ReplaceAll(listItems);
        }

        public ListDialog(IEnumerable<string> listItems, string title, string message, Window owner, int width = 0, int height = 0) : this(title, message, owner, width, height)
        {
            Items.ReplaceAll(listItems);
        }

        private void CopyItemsToClipBoard_Click(object sender, RoutedEventArgs e)
        {
            string toClipboard = string.Join("\n", Items);
            try
            {
                Clipboard.SetText(toClipboard);
                ListDialog_Status.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                //yes, this actually happens sometimes...
                MessageBox.Show("Could not set data to clipboard:\n" + ex.Message);
            }
        }

        private void ListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListView listView
                || e.ChangedButton != MouseButton.Left
                || e.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(listView, source) is not ListViewItem listViewItem)
            {
                return;
            }

            var ctx = listViewItem.DataContext;
            if (ctx is EntryStringPair esp && (esp.Entry is not null || esp.Openable is not null))
            {
                if (DoubleClickEntryHandler == null)
                {
                    MessageBox.Show("This dialog doesn't support double click to goto yet, please report this");
                }
                else
                {
                    DoubleClickEntryHandler.Invoke(esp);
                    e.Handled = true;
                }
            }
            else if (ctx != null)
            {
                if (DoubleClickItemHandler == null)
                {
                    // No-op: dialog may be used just for display
                }
                else
                {
                    DoubleClickItemHandler.Invoke(ctx);
                    e.Handled = true;
                }
            }
        }

        private void SecondaryAction_Click(object sender, RoutedEventArgs e)
        {
            SecondaryActionHandler?.Invoke();
        }

        private void TertiaryAction_Click(object sender, RoutedEventArgs e)
        {
            TertiaryActionHandler?.Invoke();
        }

        private void QuaternaryAction_Click(object sender, RoutedEventArgs e)
        {
            QuaternaryActionHandler?.Invoke();
        }

        private void QuinaryAction_Click(object sender, RoutedEventArgs e)
        {
            QuinaryActionHandler?.Invoke();
        }
    }
}
