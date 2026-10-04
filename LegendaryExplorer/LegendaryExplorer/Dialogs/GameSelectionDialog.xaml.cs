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
        private readonly MEGame[] _supportedGames;

        public GameSelectionDialog(Window owner, string titleText, bool allowLocFile = false,
            string promptText = "Choose a game to create the file for:", MEGame[] supportedGames = null, MEGame? defaultGame = null)
        {
            Owner = owner;
            TitleText = titleText;
            PromptText = promptText;
            _supportedGames = supportedGames;
            DataContext = this;
            InitializeComponent();
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            CreateLocFileCheckBox.Visibility = allowLocFile ? Visibility.Visible : Visibility.Collapsed;
            ConversationOptionsPanel.Visibility = allowLocFile ? Visibility.Visible : Visibility.Collapsed;

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

            UpdateConversationNameValidation();
        }

        public string TitleText { get; }
        public string PromptText { get; }
        public MEGame SelectedGame { get; private set; }
        public bool CreateLocFile => CreateLocFileCheckBox.IsChecked == true;
        public bool CreateBlankConversation => CreateLocFile
            && SelectedGame is MEGame.ME3 or MEGame.LE3
            && CreateBlankConversationCheckBox.IsChecked == true;

        public (string TopPackageName, string ConversationName) GetBlankConversationNames(string defaultName)
        {
            string defaultObjectName = new(defaultName.Select(character =>
                char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
            if (string.IsNullOrEmpty(defaultObjectName))
            {
                defaultObjectName = "Conversation";
            }
            if (!char.IsLetter(defaultObjectName[0]) && defaultObjectName[0] != '_')
            {
                defaultObjectName = "_" + defaultObjectName;
            }

            string topPackageName = TopPackageNameTextBox.Text.Trim();
            string conversationName = ConversationNameTextBox.Text.Trim();
            if (topPackageName.Length == 0)
            {
                topPackageName = defaultObjectName;
            }
            if (conversationName.Length == 0)
            {
                conversationName = defaultObjectName;
            }
            conversationName = StripConversationSuffix(conversationName);
            if (conversationName.Length == 0)
            {
                conversationName = "Conversation";
            }
            return (topPackageName, conversationName);
        }

        private static string StripConversationSuffix(string name) =>
            name.EndsWith("_dlg", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        private static bool IsValidOptionalObjectName(string name) => name.Length == 0
            || ((char.IsLetter(name[0]) || name[0] == '_')
                && name.All(character => char.IsLetterOrDigit(character) || character == '_'));

        private void ConversationOptions_Changed(object sender, RoutedEventArgs e) => UpdateConversationNameValidation();

        private void ConversationName_TextChanged(object sender, TextChangedEventArgs e) => UpdateConversationNameValidation();

        private void UpdateConversationNameValidation()
        {
            if (TopPackageNameTextBox == null || ConversationNameTextBox == null || ConversationNameValidationText == null)
            {
                return;
            }

            string conversationName = ConversationNameTextBox.Text.Trim();
            bool namesValid = IsValidOptionalObjectName(TopPackageNameTextBox.Text.Trim())
                && IsValidOptionalObjectName(conversationName)
                && (conversationName.Length == 0 || StripConversationSuffix(conversationName).Length > 0);
            bool needsNames = CreateLocFile && CreateBlankConversationCheckBox.IsChecked == true;
            ConversationNameValidationText.Visibility = needsNames && !namesValid ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in new[] { ME3Button, LE3Button })
            {
                var game = Enum.Parse<MEGame>((string)button.Tag);
                bool gameSupported = _supportedGames == null || _supportedGames.Contains(game);
                button.IsEnabled = gameSupported && (!needsNames || namesValid);
                if (gameSupported)
                {
                    button.ToolTip = needsNames && !namesValid ? "Enter valid BioConversation names." : null;
                    ToolTipService.SetShowOnDisabled(button, true);
                }
            }
        }

        private void GameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { IsEnabled: true, Tag: string gameName } && Enum.TryParse(gameName, out MEGame game))
            {
                SelectedGame = game;
                DialogResult = true;
            }
        }
    }
}
