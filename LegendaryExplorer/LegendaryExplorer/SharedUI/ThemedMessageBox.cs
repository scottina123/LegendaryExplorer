using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LegendaryExplorer.SharedUI
{
    /// <summary>
    /// A drop-in for <see cref="MessageBox"/> (same overloads and results) drawn as a WPF window, so it follows the app theme (Light / Dark /
    /// Modern Dark): the native message box ignores WPF themes. Files opt in with <c>using MessageBox = LegendaryExplorer.SharedUI.ThemedMessageBox;</c>
    /// </summary>
    public static class ThemedMessageBox
    {
        public static MessageBoxResult Show(string messageBoxText) =>
            Show(null, messageBoxText, string.Empty, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
        public static MessageBoxResult Show(string messageBoxText, string caption) =>
            Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
            Show(null, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
            Show(null, messageBoxText, caption, button, icon, MessageBoxResult.None);
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) =>
            Show(null, messageBoxText, caption, button, icon, defaultResult);
        public static MessageBoxResult Show(Window owner, string messageBoxText) =>
            Show(owner, messageBoxText, string.Empty, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
        public static MessageBoxResult Show(Window owner, string messageBoxText, string caption) =>
            Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
        public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button) =>
            Show(owner, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);
        public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
            Show(owner, messageBoxText, caption, button, icon, MessageBoxResult.None);

        public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                return dispatcher.Invoke(() => Show(owner, messageBoxText, caption, button, icon, defaultResult));
            }

            owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            MessageBoxResult[] results = button switch
            {
                MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
                MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
                MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
                _ => [MessageBoxResult.OK]
            };
            if (!results.Contains(defaultResult)) defaultResult = results[0];
            //what closing the window (X or Esc) means, as the native box: Cancel where there is one, otherwise OK; a Yes/No box answers No
            MessageBoxResult dismissResult = results.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
                : button is MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK;
            MessageBoxResult result = dismissResult;

            var window = new Window
            {
                Title = caption ?? string.Empty,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = owner is null,
                WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                MinWidth = 340,
                MaxWidth = 700,
                UseLayoutRounding = true,
            };
            if (owner is { IsLoaded: true } && !ReferenceEquals(owner, window)) window.Owner = owner;
            window.SetResourceReference(Control.BackgroundProperty, SystemColors.ControlBrushKey);
            window.SetResourceReference(Control.ForegroundProperty, SystemColors.ControlTextBrushKey);

            var text = new TextBlock
            {
                Text = messageBoxText ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 600,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.ControlTextBrushKey);

            var body = new DockPanel { Margin = new Thickness(20, 20, 24, 20), LastChildFill = true };
            if (GetIcon(icon) is { } iconSource)
            {
                var image = new Image { Source = iconSource, Width = 32, Height = 32, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(image, Dock.Left);
                body.Children.Add(image);
            }
            body.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 10, 12, 10) };
            foreach (MessageBoxResult option in results)
            {
                var optionButton = new Button
                {
                    Content = option switch { MessageBoxResult.OK => "OK", MessageBoxResult.Cancel => "Cancel", MessageBoxResult.Yes => "_Yes", MessageBoxResult.No => "_No", _ => option.ToString() },
                    MinWidth = 88,
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(6, 0, 0, 0),
                    IsDefault = option == defaultResult,
                    IsCancel = option == dismissResult && results.Length > 1,
                };
                MessageBoxResult chosen = option;
                optionButton.Click += (_, _) =>
                {
                    result = chosen;
                    window.Close();
                };
                buttons.Children.Add(optionButton);
            }
            var buttonBar = new Border { Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
            buttonBar.SetResourceReference(Border.BackgroundProperty, SystemColors.ControlLightBrushKey);
            buttonBar.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlDarkBrushKey);
            DockPanel.SetDock(buttonBar, Dock.Bottom);

            var root = new DockPanel();
            root.Children.Add(buttonBar);
            root.Children.Add(body);
            window.Content = root;

            // Esc dismisses as the native box does (an OK-only box too), and Ctrl+C copies the message
            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    result = dismissResult;
                    window.Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    try { Clipboard.SetText($"{caption}\n\n{messageBoxText}"); } catch { /* clipboard busy */ }
                    e.Handled = true;
                }
            };
            window.Loaded += (_, _) =>
            {
                if (buttons.Children.OfType<Button>().FirstOrDefault(b => b.IsDefault) is { } defaultButton) defaultButton.Focus();
            };

            window.ShowDialog();
            return result;
        }

        private static ImageSource GetIcon(MessageBoxImage icon)
        {
            System.Drawing.Icon systemIcon = icon switch
            {
                MessageBoxImage.Error => System.Drawing.SystemIcons.Error,
                MessageBoxImage.Warning => System.Drawing.SystemIcons.Warning,
                MessageBoxImage.Question => System.Drawing.SystemIcons.Question,
                MessageBoxImage.Information => System.Drawing.SystemIcons.Information,
                _ => null
            };
            if (systemIcon is null) return null;
            try
            {
                return Imaging.CreateBitmapSourceFromHIcon(systemIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch
            {
                return null;
            }
        }
    }
}
