using System.IO;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs
{
    public partial class OpenPccToolSelector : Window
    {
        public string SelectedTool { get; private set; }

        private OpenPccToolSelector(string fileName)
        {
            InitializeComponent();
            CustomWindowChrome.ApplyCustomChrome(this);
            DirectionsTextBlock.Text = $"Open {Path.GetFileName(fileName)} with:";
        }

        public static string GetTool(string fileName)
        {
            var dialog = new OpenPccToolSelector(fileName);

            return dialog.ShowDialog() == true ? dialog.SelectedTool : null;
        }

        private void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tool })
            {
                SelectedTool = tool;
                DialogResult = true;
            }
        }
    }
}
