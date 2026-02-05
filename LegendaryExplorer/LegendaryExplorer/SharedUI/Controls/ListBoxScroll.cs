using System.Windows;
using System.Windows.Controls;

namespace LegendaryExplorer.SharedUI.Controls
{
    public class ListBoxScroll : ListBox
    {
        static ListBoxScroll()
        {
            // This makes ListBoxScroll use the same default style as ListBox
            // which is necessary for theme styles to apply correctly
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ListBoxScroll), 
                new FrameworkPropertyMetadata(typeof(ListBox)));
        }

        public ListBoxScroll()
        {
            SelectionChanged += ListBoxScroll_SelectionChanged;
        }

        void ListBoxScroll_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ScrollIntoView(SelectedItem);
        }
    }
}
