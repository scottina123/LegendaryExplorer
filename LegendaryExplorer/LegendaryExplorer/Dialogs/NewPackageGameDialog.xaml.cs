using System;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Dialogs
{
    public partial class NewPackageGameDialog : Window
    {
        public NewPackageGameDialog(Window owner, string titleText, bool allowLocFile)
        {
            Owner = owner;
            TitleText = titleText;
            DataContext = this;
            InitializeComponent();
            CreateLocFileCheckBox.Visibility = allowLocFile ? Visibility.Visible : Visibility.Collapsed;
        }

        public string TitleText { get; }
        public MEGame SelectedGame { get; private set; }
        public bool CreateLocFile => CreateLocFileCheckBox.IsChecked == true;

        private void GameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string gameName } && Enum.TryParse(gameName, out MEGame game))
            {
                SelectedGame = game;
                DialogResult = true;
            }
        }
    }
}
