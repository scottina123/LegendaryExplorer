using System;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Dialogs
{
    public partial class NewPackageGameDialog : NotifyPropertyChangedWindowBase
    {
        public NewPackageGameDialog(Window owner, string titleText, bool allowLocFile)
        {
            Owner = owner;
            TitleText = titleText;
            DataContext = this;
            InitializeComponent();
            CreateLocFileCheckBox.Visibility = allowLocFile ? Visibility.Visible : Visibility.Collapsed;
            CreateBlankConversationCheckBox.Visibility = allowLocFile ? Visibility.Visible : Visibility.Collapsed;
        }

        public string TitleText { get; }
        public MEGame SelectedGame { get; private set; }
        public bool CreateLocFile => CreateLocFileCheckBox.IsChecked == true;
        public bool CreateBlankConversation => CreateLocFile && CreateBlankConversationCheckBox.IsChecked == true;

        private void GameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string gameName } && Enum.TryParse(gameName, out MEGame game))
            {
                if (CreateBlankConversation && game is not (MEGame.ME3 or MEGame.LE3))
                {
                    MessageBox.Show(this, "Blank BioConversation generation is available only for ME3 and LE3 level files.", "Unsupported game", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SelectedGame = game;
                DialogResult = true;
            }
        }
    }
}
