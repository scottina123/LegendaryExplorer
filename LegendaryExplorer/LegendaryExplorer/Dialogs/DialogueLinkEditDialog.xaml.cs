using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;

namespace LegendaryExplorer.Dialogs
{
    public sealed class DialogueLinkEditDialogResult
    {
        public DialogueLinkEditDialogResult(string selectedTarget, int replyStrRef, string selectedCategory)
        {
            SelectedTarget = selectedTarget;
            ReplyStrRef = replyStrRef;
            SelectedCategory = selectedCategory;
        }

        public string SelectedTarget { get; }
        public int ReplyStrRef { get; }
        public string SelectedCategory { get; }
    }

    public class DialogueLinkEditDialog : NotifyPropertyChangedWindowBase
    {
        private readonly bool showReplyOptions;
        private readonly Func<int, string> replyTextResolver;
        private readonly ComboBox targetLinkComboBox;
        private readonly TextBox replyStrRefTextBox;
        private readonly TextBlock replyStrRefPreviewTextBlock;
        private readonly ComboBox categoryComboBox;

        private DialogueLinkEditDialog(
            Control owner,
            IEnumerable<string> targetOptions,
            string selectedTarget,
            bool showReplyOptions,
            int replyStrRef,
            Func<int, string> replyTextResolver,
            IEnumerable<string> replyCategories,
            string selectedCategory)
        {
            Title = "Edit Link";
            Width = 560;
            MinWidth = 560;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            this.showReplyOptions = showReplyOptions;
            this.replyTextResolver = replyTextResolver;

            if (owner != null)
            {
                Owner = owner as Window ?? GetWindow(owner);
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = "Edit link",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var targetPanel = new StackPanel();
            Grid.SetRow(targetPanel, 1);
            targetPanel.Children.Add(new TextBlock
            {
                Text = "Target node",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            targetLinkComboBox = new ComboBox
            {
                IsTextSearchEnabled = true,
                ItemsSource = targetOptions?.ToList() ?? []
            };
            targetLinkComboBox.SelectedItem = selectedTarget;
            targetPanel.Children.Add(targetLinkComboBox);
            root.Children.Add(targetPanel);

            var replyOptionsPanel = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = showReplyOptions ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetRow(replyOptionsPanel, 2);
            replyOptionsPanel.Children.Add(new TextBlock
            {
                Text = "Dialogue wheel TLK string reference",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            replyStrRefTextBox = new TextBox
            {
                Text = replyStrRef > 0 ? replyStrRef.ToString() : string.Empty
            };
            replyOptionsPanel.Children.Add(replyStrRefTextBox);
            replyStrRefPreviewTextBlock = new TextBlock
            {
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush
            };
            replyOptionsPanel.Children.Add(replyStrRefPreviewTextBlock);
            replyOptionsPanel.Children.Add(new TextBlock
            {
                Text = "Dialogue wheel category",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 4)
            });
            categoryComboBox = new ComboBox
            {
                ItemsSource = replyCategories?.ToList() ?? []
            };
            categoryComboBox.SelectedItem = selectedCategory;
            replyOptionsPanel.Children.Add(categoryComboBox);
            root.Children.Add(replyOptionsPanel);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(buttonPanel, 3);

            var okButton = new Button
            {
                Content = "OK",
                Width = 70,
                Margin = new Thickness(0, 0, 6, 0),
                IsDefault = true
            };
            okButton.Click += OKButton_Click;
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 70,
                IsCancel = true
            };
            cancelButton.Click += CancelButton_Click;
            buttonPanel.Children.Add(cancelButton);

            root.Children.Add(buttonPanel);
            Content = root;

            if (showReplyOptions)
            {
                replyStrRefTextBox.TextChanged += (_, _) => UpdateReplyStrRefPreview();
                UpdateReplyStrRefPreview();
            }

            Loaded += (_, _) => targetLinkComboBox.Focus();
        }

        public DialogueLinkEditDialogResult Result { get; private set; }

        public static bool TryEditLink(
            Control owner,
            IEnumerable<string> targetOptions,
            string selectedTarget,
            bool showReplyOptions,
            int replyStrRef,
            Func<int, string> replyTextResolver,
            IEnumerable<string> replyCategories,
            string selectedCategory,
            out DialogueLinkEditDialogResult result)
        {
            var dialog = new DialogueLinkEditDialog(owner, targetOptions, selectedTarget, showReplyOptions, replyStrRef, replyTextResolver, replyCategories, selectedCategory);
            result = dialog.ShowDialog() == true ? dialog.Result : null;
            return result != null;
        }

        private void UpdateReplyStrRefPreview()
        {
            if (replyStrRefPreviewTextBlock == null)
            {
                return;
            }

            if (!int.TryParse(replyStrRefTextBox.Text, out int replyStrRef) || replyStrRef <= 0)
            {
                replyStrRefPreviewTextBlock.Text = "Enter a positive TLK string reference.";
                return;
            }

            string resolvedText = replyTextResolver?.Invoke(replyStrRef);
            resolvedText = RemoveWrappingQuotes(resolvedText);
            replyStrRefPreviewTextBlock.Text = string.IsNullOrWhiteSpace(resolvedText)
                ? "No TLK text found for this string reference."
                : resolvedText;
        }

        private static string RemoveWrappingQuotes(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2)
            {
                return text;
            }

            return text[0] switch
            {
                '"' when text[^1] == '"' => text[1..^1],
                '“' when text[^1] == '”' => text[1..^1],
                _ => text
            };
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (targetLinkComboBox.SelectedItem is not string selectedTarget || string.IsNullOrWhiteSpace(selectedTarget))
            {
                System.Windows.MessageBox.Show(this, "Select a target node.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int replyStrRef = 0;
            string selectedCategory = null;
            if (showReplyOptions)
            {
                if (!int.TryParse(replyStrRefTextBox.Text, out replyStrRef) || replyStrRef <= 0)
                {
                    System.Windows.MessageBox.Show(this, "The string reference must be a positive whole number.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                selectedCategory = categoryComboBox.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(selectedCategory))
                {
                    System.Windows.MessageBox.Show(this, "Select a dialogue wheel category.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            Result = new DialogueLinkEditDialogResult(selectedTarget, replyStrRef, selectedCategory);
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
