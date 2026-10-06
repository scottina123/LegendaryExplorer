using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.Packages;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class GameSelectionDialogTests
{
    [STATestMethod]
    [DataRow(MEGame.ME1, false)]
    [DataRow(MEGame.ME2, false)]
    [DataRow(MEGame.ME3, true)]
    [DataRow(MEGame.LE1, true)]
    [DataRow(MEGame.LE2, true)]
    [DataRow(MEGame.LE3, true)]
    public void LocalizationCanBeSelectedForEveryGameWithBlankConversationChecked(
        MEGame game, bool expectedBlankConversation)
    {
        var dialog = CreateDialog();
        try
        {
            FindCheckBox(dialog, "CreateLocFileCheckBox").IsChecked = true;
            FindCheckBox(dialog, "CreateBlankConversationCheckBox").IsChecked = true;

            // Check before opening the dialog so a regression cannot leave a modal
            // unsupported-game warning waiting for user input during the test.
            Assert.IsFalse(dialog.CreateBlankConversation,
                "Blank conversation generation requires a supported selected game.");

            Assert.IsTrue(SelectGame(dialog, game) == true,
                $"Selecting {game} must accept the dialog when localization is requested.");
            Assert.AreEqual(game, dialog.SelectedGame);
            Assert.IsTrue(dialog.CreateLocFile);
            Assert.AreEqual(expectedBlankConversation, dialog.CreateBlankConversation);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void BlankConversationRequiresLocalizationEvenForSupportedGames(MEGame game)
    {
        var dialog = CreateDialog();
        try
        {
            FindCheckBox(dialog, "CreateBlankConversationCheckBox").IsChecked = true;
            FindCheckBox(dialog, "CreateLocFileCheckBox").IsChecked = false;

            Assert.IsTrue(SelectGame(dialog, game) == true);
            Assert.AreEqual(game, dialog.SelectedGame);
            Assert.IsFalse(dialog.CreateLocFile);
            Assert.IsFalse(dialog.CreateBlankConversation);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void LocalizationDoesNotEnableUnsupportedGameButtons()
    {
        var dialog = CreateDialog(supportedGames: [MEGame.LE2]);
        try
        {
            FindCheckBox(dialog, "CreateLocFileCheckBox").IsChecked = true;
            FindCheckBox(dialog, "CreateBlankConversationCheckBox").IsChecked = true;

            foreach (MEGame game in new[]
                     { MEGame.ME1, MEGame.ME2, MEGame.ME3, MEGame.LE1, MEGame.LE2, MEGame.LE3 })
            {
                Assert.AreEqual(game == MEGame.LE2, FindGameButton(dialog, game).IsEnabled);
            }
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void InlineConversationNamesAreEnabledOnlyWhenLocalizationIsChecked()
    {
        var dialog = CreateDialog();
        try
        {
            var options = (FrameworkElement)dialog.FindName("ConversationOptionsPanel");
            var topPackageName = FindTextBox(dialog, "TopPackageNameTextBox");
            var conversationName = FindTextBox(dialog, "ConversationNameTextBox");
            Assert.AreEqual(Visibility.Visible, options.Visibility);

            foreach (bool createLoc in new[] { false, true, false })
            {
                FindCheckBox(dialog, "CreateLocFileCheckBox").IsChecked = createLoc;
                dialog.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));

                Assert.AreEqual(createLoc, options.IsEnabled);
                Assert.AreEqual(createLoc, topPackageName.IsEnabled);
                Assert.AreEqual(createLoc, conversationName.IsEnabled);
                Assert.AreEqual(createLoc, FindCheckBox(dialog, "CreateBlankConversationCheckBox").IsEnabled);
            }
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void GenericGameSelectionHidesInlineConversationNames()
    {
        var dialog = CreateDialog(allowLocFile: false);
        try
        {
            Assert.AreEqual(Visibility.Collapsed,
                ((FrameworkElement)dialog.FindName("ConversationOptionsPanel")).Visibility);
            Assert.AreEqual(Visibility.Collapsed,
                FindCheckBox(dialog, "CreateLocFileCheckBox").Visibility);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    public void InlineConversationNamesAreTrimmedAndRemoveConversationSuffix()
    {
        var dialog = CreateDialog();
        try
        {
            FindTextBox(dialog, "TopPackageNameTextBox").Text = "  CustomPackage  ";
            FindTextBox(dialog, "ConversationNameTextBox").Text = "  CustomConversation_DLG  ";

            var names = dialog.GetBlankConversationNames("BioD_Default");
            Assert.AreEqual("CustomPackage", names.TopPackageName);
            Assert.AreEqual("CustomConversation", names.ConversationName);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    [DataRow("123-Level Name", "_123_Level_Name", "_123_Level_Name")]
    [DataRow("BioD_Default_DLG", "BioD_Default_DLG", "BioD_Default")]
    public void EmptyInlineConversationNamesUseValidLevelNameDefaults(
        string levelName, string expectedTopPackageName, string expectedConversationName)
    {
        var dialog = CreateDialog();
        try
        {
            FindTextBox(dialog, "TopPackageNameTextBox").Text = "  ";
            FindTextBox(dialog, "ConversationNameTextBox").Text = "  ";

            var names = dialog.GetBlankConversationNames(levelName);
            Assert.AreEqual(expectedTopPackageName, names.TopPackageName);
            Assert.AreEqual(expectedConversationName, names.ConversationName);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    [DataRow(MEGame.ME3, "TopPackageNameTextBox", "Invalid Package")]
    [DataRow(MEGame.ME3, "ConversationNameTextBox", "Invalid.Conversation")]
    [DataRow(MEGame.ME3, "ConversationNameTextBox", "_dlg")]
    [DataRow(MEGame.LE1, "TopPackageNameTextBox", "Invalid Package")]
    [DataRow(MEGame.LE1, "ConversationNameTextBox", "Invalid.Conversation")]
    [DataRow(MEGame.LE1, "ConversationNameTextBox", "_dlg")]
    [DataRow(MEGame.LE2, "TopPackageNameTextBox", "Invalid Package")]
    [DataRow(MEGame.LE2, "ConversationNameTextBox", "Invalid.Conversation")]
    [DataRow(MEGame.LE2, "ConversationNameTextBox", "_dlg")]
    [DataRow(MEGame.LE3, "TopPackageNameTextBox", "Invalid Package")]
    [DataRow(MEGame.LE3, "ConversationNameTextBox", "Invalid.Conversation")]
    [DataRow(MEGame.LE3, "ConversationNameTextBox", "_dlg")]
    public void InvalidActiveConversationNamePreventsSupportedGameSelection(
        MEGame game, string textBoxName, string invalidName)
    {
        var dialog = CreateDialog();
        try
        {
            FindCheckBox(dialog, "CreateLocFileCheckBox").IsChecked = true;
            FindCheckBox(dialog, "CreateBlankConversationCheckBox").IsChecked = true;
            FindTextBox(dialog, textBoxName).Text = invalidName;

            Assert.IsFalse(FindGameButton(dialog, game).IsEnabled);
            Assert.IsFalse(SelectGame(dialog, game) == true);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    [DataRow(MEGame.ME1, true, true)]
    [DataRow(MEGame.ME2, true, true)]
    [DataRow(MEGame.LE1, true, false)]
    [DataRow(MEGame.LE2, true, false)]
    [DataRow(MEGame.LE3, true, false)]
    [DataRow(MEGame.ME3, false, true)]
    [DataRow(MEGame.LE1, false, true)]
    [DataRow(MEGame.LE2, false, true)]
    public void InactiveConversationNamesDoNotPreventGameSelection(
        MEGame game, bool createLoc, bool createConversation)
    {
        var dialog = CreateDialog();
        try
        {
            FindCheckBox(dialog, "CreateLocFileCheckBox").IsChecked = createLoc;
            FindCheckBox(dialog, "CreateBlankConversationCheckBox").IsChecked = createConversation;
            FindTextBox(dialog, "TopPackageNameTextBox").Text = "Invalid Package";
            FindTextBox(dialog, "ConversationNameTextBox").Text = "Invalid.Conversation";

            Assert.IsTrue(SelectGame(dialog, game) == true);
            Assert.AreEqual(createLoc, dialog.CreateLocFile);
            Assert.IsFalse(dialog.CreateBlankConversation);
        }
        finally
        {
            dialog.Close();
        }
    }

    [STATestMethod]
    [DataRow(MEGame.ME3)]
    [DataRow(MEGame.LE1)]
    [DataRow(MEGame.LE2)]
    [DataRow(MEGame.LE3)]
    public void CorrectingConversationNamesAllowsSupportedGameSelection(MEGame game)
    {
        var dialog = CreateDialog(supportedGames: [game]);
        try
        {
            FindCheckBox(dialog, "CreateLocFileCheckBox").IsChecked = true;
            FindCheckBox(dialog, "CreateBlankConversationCheckBox").IsChecked = true;
            FindTextBox(dialog, "TopPackageNameTextBox").Text = "Invalid Package";
            Assert.IsFalse(FindGameButton(dialog, game).IsEnabled);

            FindTextBox(dialog, "TopPackageNameTextBox").Text = "ValidPackage";
            FindTextBox(dialog, "ConversationNameTextBox").Text = "ValidConversation";

            foreach (MEGame candidate in new[]
                     { MEGame.ME1, MEGame.ME2, MEGame.ME3, MEGame.LE1, MEGame.LE2, MEGame.LE3 })
            {
                Assert.AreEqual(candidate == game, FindGameButton(dialog, candidate).IsEnabled);
            }
            Assert.IsTrue(SelectGame(dialog, game) == true);
            Assert.IsTrue(dialog.CreateBlankConversation);
        }
        finally
        {
            dialog.Close();
        }
    }

    private static GameSelectionDialog CreateDialog(MEGame[] supportedGames = null, bool allowLocFile = true) =>
        new(null, "Create new level file", allowLocFile: allowLocFile, supportedGames: supportedGames)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Opacity = 0
        };

    private static CheckBox FindCheckBox(GameSelectionDialog dialog, string name) =>
        (CheckBox)dialog.FindName(name);

    private static TextBox FindTextBox(GameSelectionDialog dialog, string name) =>
        (TextBox)dialog.FindName(name);

    private static Button FindGameButton(GameSelectionDialog dialog, MEGame game) =>
        (Button)dialog.FindName($"{game}Button");

    private static bool? SelectGame(GameSelectionDialog dialog, MEGame game)
    {
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            FindGameButton(dialog, game).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // A button that fails to accept the dialog should fail the assertion
            // instead of leaving ShowDialog running indefinitely.
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
        }));
        return dialog.ShowDialog();
    }
}
