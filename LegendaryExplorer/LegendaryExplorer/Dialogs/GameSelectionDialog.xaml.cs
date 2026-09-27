using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Misc;
using LegendaryExplorerCore.Packages;

namespace LegendaryExplorer.Dialogs
{
    public partial class GameSelectionDialog : NotifyPropertyChangedWindowBase
    {
        public GameSelectionDialog(Window owner, string titleText, bool allowLocFile = false,
            string promptText = "Choose a game to create the file for:", MEGame[] supportedGames = null, MEGame? defaultGame = null)
        {
            Owner = owner;
            TitleText = titleText;
            PromptText = promptText;
            DataContext = this;
            InitializeComponent();
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            CreateLocFileCheckBox.Visibility = allowLocFile ? Visibility.Visible : Visibility.Collapsed;
            CreateBlankConversationCheckBox.Visibility = allowLocFile ? Visibility.Visible : Visibility.Collapsed;

            foreach (var button in new[] { LE1Button, LE2Button, LE3Button, ME1Button, ME2Button, ME3Button })
            {
                var game = Enum.Parse<MEGame>((string)button.Tag);
                button.IsEnabled = supportedGames == null || supportedGames.Contains(game);
                if (!button.IsEnabled)
                {
                    button.ToolTip = "This game is not supported by this tool.";
                    ToolTipService.SetShowOnDisabled(button, true);
                }

                if (button.IsEnabled && game == defaultGame)
                {
                    button.IsDefault = true;
                    ContentRendered += (_, _) => button.Focus();
                }
            }
        }

        public string TitleText { get; }
        public string PromptText { get; }
        public MEGame SelectedGame { get; private set; }
        public bool CreateLocFile => CreateLocFileCheckBox.IsChecked == true;
        public bool CreateBlankConversation => CreateLocFile && CreateBlankConversationCheckBox.IsChecked == true;

        private void GameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { IsEnabled: true, Tag: string gameName } && Enum.TryParse(gameName, out MEGame game))
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
