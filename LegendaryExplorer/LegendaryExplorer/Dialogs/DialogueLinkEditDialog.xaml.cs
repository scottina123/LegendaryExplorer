using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LegendaryExplorer.DialogueEditor;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Dialogs
{
    public sealed class DialogueLinkEditDialogResult
    {
        public DialogueLinkEditDialogResult(string selectedTarget, int replyStrRef, string selectedCategory, int selectedOrder)
        {
            SelectedTarget = selectedTarget;
            ReplyStrRef = replyStrRef;
            SelectedCategory = selectedCategory;
            SelectedOrder = selectedOrder;
        }

        public string SelectedTarget { get; }
        public int ReplyStrRef { get; }
        public string SelectedCategory { get; }
        public int SelectedOrder { get; }
    }

    public class DialogueLinkEditDialog : NotifyPropertyChangedWindowBase
    {
        private readonly bool showReplyOptions;
        private readonly Func<int, string> replyTextResolver;
        private readonly ComboBox targetLinkComboBox;
        private readonly ObservableCollection<string> outgoingConnectionOrder;
        private readonly ListBox outgoingConnectionOrderListBox;
        private readonly TextBox replyStrRefTextBox;
        private readonly TextBlock replyStrRefPreviewTextBlock;
        private readonly ComboBox categoryComboBox;
        private readonly Button moveOrderTopButton;
        private readonly Button moveOrderUpButton;
        private readonly Button moveOrderDownButton;
        private readonly Button moveOrderBottomButton;
        private int selectedOrder;
        private bool suppressOrderSelectionChanged;

        private DialogueLinkEditDialog(
            Control owner,
            IEnumerable<string> targetOptions,
            string selectedTarget,
            IEnumerable<string> outgoingConnectionOrder,
            int selectedOrder,
            bool showReplyOptions,
            int replyStrRef,
            Func<int, string> replyTextResolver,
            IEnumerable<string> replyCategories,
            string selectedCategory)
        {
            Title = "Edit Link";
            Width = 720;
            MinWidth = 720;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            this.showReplyOptions = showReplyOptions;
            this.replyTextResolver = replyTextResolver;
            this.outgoingConnectionOrder = new ObservableCollection<string>(outgoingConnectionOrder?.ToList() ?? []);
            this.selectedOrder = this.outgoingConnectionOrder.Count == 0
                ? -1
                : Math.Clamp(selectedOrder, 0, this.outgoingConnectionOrder.Count - 1);

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

            var orderPanel = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = this.outgoingConnectionOrder.Count > 0 ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetRow(orderPanel, 2);
            orderPanel.Children.Add(new TextBlock
            {
                Text = "Outgoing connection order",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            orderPanel.Children.Add(new TextBlock
            {
                Text = "Focus the list and use ↑ or ↓ to move this link, or Ctrl+↑ / Ctrl+↓ to move it to the top or bottom.",
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = SystemColors.GrayTextBrush
            });

            var orderGrid = new Grid();
            orderGrid.ColumnDefinitions.Add(new ColumnDefinition());
            orderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            outgoingConnectionOrderListBox = new ListBox
            {
                ItemsSource = this.outgoingConnectionOrder,
                MinHeight = 90,
                MaxHeight = 180
            };
            outgoingConnectionOrderListBox.SelectionChanged += OutgoingConnectionOrderListBox_SelectionChanged;
            outgoingConnectionOrderListBox.PreviewKeyDown += OutgoingConnectionOrderListBox_PreviewKeyDown;
            SetSelectedOrder(this.selectedOrder);
            orderGrid.Children.Add(outgoingConnectionOrderListBox);

            var orderButtons = new StackPanel
            {
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(orderButtons, 1);

            moveOrderTopButton = new Button
            {
                Content = "⇑",
                Width = 32,
                Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "Move this link to the top"
            };
            moveOrderTopButton.Click += (_, _) => MoveSelectedOrderTo(0);
            orderButtons.Children.Add(moveOrderTopButton);

            moveOrderUpButton = new Button
            {
                Content = "↑",
                Width = 32,
                Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "Move this link up"
            };
            moveOrderUpButton.Click += (_, _) => MoveSelectedOrderBy(-1);
            orderButtons.Children.Add(moveOrderUpButton);

            moveOrderDownButton = new Button
            {
                Content = "↓",
                Width = 32,
                ToolTip = "Move this link down"
            };
            moveOrderDownButton.Click += (_, _) => MoveSelectedOrderBy(1);
            orderButtons.Children.Add(moveOrderDownButton);

            moveOrderBottomButton = new Button
            {
                Content = "⇓",
                Width = 32,
                ToolTip = "Move this link to the bottom"
            };
            moveOrderBottomButton.Click += (_, _) => MoveSelectedOrderTo(this.outgoingConnectionOrder.Count - 1);
            orderButtons.Children.Add(moveOrderBottomButton);

            orderGrid.Children.Add(orderButtons);
            orderPanel.Children.Add(orderGrid);
            root.Children.Add(orderPanel);

            var replyOptionsPanel = new StackPanel
            {
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = showReplyOptions ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetRow(replyOptionsPanel, 3);
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
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            foreach (string replyCategory in replyCategories?.ToList() ?? [])
            {
                categoryComboBox.Items.Add(CreateCategoryItem(replyCategory));
            }
            SelectCategoryItem(selectedCategory);
            replyOptionsPanel.Children.Add(categoryComboBox);
            root.Children.Add(replyOptionsPanel);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(buttonPanel, 4);

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

            UpdateOrderButtons();

            Loaded += (_, _) => targetLinkComboBox.Focus();
        }

        public DialogueLinkEditDialogResult Result { get; private set; }

        public static bool TryEditLink(
            Control owner,
            IEnumerable<string> targetOptions,
            string selectedTarget,
            IEnumerable<string> outgoingConnectionOrder,
            int selectedOrder,
            bool showReplyOptions,
            int replyStrRef,
            Func<int, string> replyTextResolver,
            IEnumerable<string> replyCategories,
            string selectedCategory,
            out DialogueLinkEditDialogResult result)
        {
            var dialog = new DialogueLinkEditDialog(owner, targetOptions, selectedTarget, outgoingConnectionOrder, selectedOrder, showReplyOptions, replyStrRef, replyTextResolver, replyCategories, selectedCategory);
            result = dialog.ShowDialog() == true ? dialog.Result : null;
            return result != null;
        }

        private void SetSelectedOrder(int order)
        {
            selectedOrder = order;
            suppressOrderSelectionChanged = true;
            outgoingConnectionOrderListBox.SelectedIndex = selectedOrder;
            if (selectedOrder >= 0)
            {
                outgoingConnectionOrderListBox.ScrollIntoView(outgoingConnectionOrderListBox.SelectedItem);
            }
            suppressOrderSelectionChanged = false;
            UpdateOrderButtons();
        }

        private void MoveSelectedOrderBy(int offset)
        {
            if (selectedOrder < 0 || outgoingConnectionOrder.Count == 0)
            {
                return;
            }

            int targetIndex = selectedOrder + offset;
            if (targetIndex < 0 || targetIndex >= outgoingConnectionOrder.Count)
            {
                return;
            }

            string item = outgoingConnectionOrder[selectedOrder];
            outgoingConnectionOrder.RemoveAt(selectedOrder);
            outgoingConnectionOrder.Insert(targetIndex, item);
            SetSelectedOrder(targetIndex);
        }

        private void MoveSelectedOrderTo(int targetIndex)
        {
            if (selectedOrder < 0 || outgoingConnectionOrder.Count == 0)
            {
                return;
            }

            targetIndex = Math.Clamp(targetIndex, 0, outgoingConnectionOrder.Count - 1);
            if (targetIndex == selectedOrder)
            {
                return;
            }

            string item = outgoingConnectionOrder[selectedOrder];
            outgoingConnectionOrder.RemoveAt(selectedOrder);
            outgoingConnectionOrder.Insert(targetIndex, item);
            SetSelectedOrder(targetIndex);
        }

        private void UpdateOrderButtons()
        {
            bool hasMultipleItems = outgoingConnectionOrder.Count > 1 && selectedOrder >= 0;
            if (moveOrderUpButton != null)
            {
                moveOrderUpButton.IsEnabled = hasMultipleItems && selectedOrder > 0;
            }

            if (moveOrderDownButton != null)
            {
                moveOrderDownButton.IsEnabled = hasMultipleItems && selectedOrder < outgoingConnectionOrder.Count - 1;
            }

            if (moveOrderTopButton != null)
            {
                moveOrderTopButton.IsEnabled = hasMultipleItems && selectedOrder > 0;
            }

            if (moveOrderBottomButton != null)
            {
                moveOrderBottomButton.IsEnabled = hasMultipleItems && selectedOrder < outgoingConnectionOrder.Count - 1;
            }
        }

        private void OutgoingConnectionOrderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressOrderSelectionChanged || outgoingConnectionOrderListBox.SelectedIndex < 0 || outgoingConnectionOrder.Count == 0)
            {
                return;
            }

            if (outgoingConnectionOrderListBox.SelectedIndex == selectedOrder)
            {
                UpdateOrderButtons();
                return;
            }

            string selectedItem = selectedOrder >= 0 && selectedOrder < outgoingConnectionOrder.Count
                ? outgoingConnectionOrder[selectedOrder]
                : null;
            if (selectedItem == null)
            {
                SetSelectedOrder(outgoingConnectionOrderListBox.SelectedIndex);
                return;
            }

            int targetIndex = outgoingConnectionOrderListBox.SelectedIndex;
            outgoingConnectionOrder.RemoveAt(selectedOrder);
            outgoingConnectionOrder.Insert(targetIndex, selectedItem);
            SetSelectedOrder(targetIndex);
        }

        private void OutgoingConnectionOrderListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                MoveSelectedOrderTo(0);
                e.Handled = true;
            }
            else if (e.Key == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                MoveSelectedOrderTo(outgoingConnectionOrder.Count - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                MoveSelectedOrderBy(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                MoveSelectedOrderBy(1);
                e.Handled = true;
            }
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

        private ComboBoxItem CreateCategoryItem(string category)
        {
            return new ComboBoxItem
            {
                Content = new TextBlock
                {
                    Text = DialogueEditorWindow.GetReplyCategoryDisplayText(category),
                    Foreground = GetReplyCategoryBrush(category)
                },
                Tag = category,
                Foreground = GetReplyCategoryBrush(category)
            };
        }

        private void SelectCategoryItem(string category)
        {
            categoryComboBox.SelectedItem = categoryComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, category, StringComparison.Ordinal));
        }

        private static Brush GetReplyCategoryBrush(string category)
        {
            if (!Enum.TryParse(category, out EReplyCategory replyCategory))
            {
                return ToBrush(DObj.connectionColor);
            }

            return ToBrush(replyCategory switch
            {
                EReplyCategory.REPLY_CATEGORY_PARAGON_INTERRUPT => DObj.paraintColor,
                EReplyCategory.REPLY_CATEGORY_RENEGADE_INTERRUPT => DObj.renintColor,
                EReplyCategory.REPLY_CATEGORY_AGREE => DObj.agreeColor,
                EReplyCategory.REPLY_CATEGORY_DISAGREE => DObj.disagreeColor,
                EReplyCategory.REPLY_CATEGORY_FRIENDLY => DObj.friendlyColor,
                EReplyCategory.REPLY_CATEGORY_HOSTILE => DObj.hostileColor,
                _ => DObj.connectionColor
            });
        }

        private static Brush ToBrush(System.Drawing.Color color)
        {
            return new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
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

                selectedCategory = (categoryComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
                if (string.IsNullOrWhiteSpace(selectedCategory))
                {
                    System.Windows.MessageBox.Show(this, "Select a dialogue wheel category.", "Dialogue Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            Result = new DialogueLinkEditDialogResult(selectedTarget, replyStrRef, selectedCategory, selectedOrder);
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
