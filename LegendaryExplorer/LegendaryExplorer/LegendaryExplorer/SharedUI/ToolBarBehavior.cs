using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace LegendaryExplorer.SharedUI
{
    /// <summary>
    /// Attached behavior for ToolBar controls that styles the overflow button and its popup to match the toolbar's background.
    /// This removes the white background from the overflow arrow button and dropdown that appears in toolbars.
    /// </summary>
    public static class ToolBarBehavior
    {
        public static readonly DependencyProperty RemoveOverflowButtonWhiteBackgroundProperty = DependencyProperty.RegisterAttached(
            "RemoveOverflowButtonWhiteBackground", typeof(bool), typeof(ToolBarBehavior), new PropertyMetadata(false, OnPropertyChanged));

        private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ToolBar toolBar)
            {
                var enable = (bool)e.NewValue;
                if (enable)
                {
                    toolBar.Loaded += OnToolBarLoaded;
                    // If already loaded, apply immediately
                    if (toolBar.IsLoaded)
                    {
                        ApplyOverflowButtonStyle(toolBar);
                    }
                }
                else
                {
                    toolBar.Loaded -= OnToolBarLoaded;
                }
            }
        }

        private static void OnToolBarLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ToolBar toolBar)
            {
                ApplyOverflowButtonStyle(toolBar);
            }
        }

        private static void ApplyOverflowButtonStyle(ToolBar toolBar)
        {
            // Find the OverflowGrid by its template part name
            if (toolBar.Template?.FindName("OverflowGrid", toolBar) is Grid overflowGrid)
            {
                // Bind the overflow grid's background to match the toolbar
                overflowGrid.SetBinding(Panel.BackgroundProperty, new Binding(nameof(ToolBar.Background))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });

                // Find the ToggleButton inside and style its template elements
                if (FindChild<ToggleButton>(overflowGrid) is ToggleButton overflowButton)
                {
                    // Set the button's background to transparent so the grid's background shows through
                    overflowButton.Background = Brushes.Transparent;

                    // Find any Border elements inside the button's visual tree and make them transparent
                    StyleOverflowButtonVisuals(overflowButton);
                }
            }

            // Style the overflow popup/dropdown
            StyleOverflowPopup(toolBar);
        }

        /// <summary>
        /// Styles the overflow popup that appears when the overflow button is clicked.
        /// </summary>
        private static void StyleOverflowPopup(ToolBar toolBar)
        {
            // Find the Popup named "OverflowPopup" in the toolbar template
            if (toolBar.Template?.FindName("OverflowPopup", toolBar) is Popup overflowPopup)
            {
                // When the popup opens, style its contents
                overflowPopup.Opened += (s, e) =>
                {
                    if (overflowPopup.Child != null)
                    {
                        StylePopupContents(overflowPopup.Child, toolBar);
                    }
                };

                // If popup already has a child, style it now
                if (overflowPopup.Child != null)
                {
                    StylePopupContents(overflowPopup.Child, toolBar);
                }
            }

            // Also look for the ToolBarOverflowPanel directly
            if (toolBar.Template?.FindName("PART_ToolBarOverflowPanel", toolBar) is ToolBarOverflowPanel overflowPanel)
            {
                overflowPanel.SetBinding(Panel.BackgroundProperty, new Binding(nameof(ToolBar.Background))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
            }
        }

        /// <summary>
        /// Styles the contents of the overflow popup to match the toolbar background.
        /// </summary>
        private static void StylePopupContents(UIElement popupChild, ToolBar toolBar)
        {
            // Style any Border elements in the popup
            if (popupChild is Border border)
            {
                border.SetBinding(Border.BackgroundProperty, new Binding(nameof(ToolBar.Background))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
                
                // Also style children of the border
                if (border.Child != null)
                {
                    StylePopupContents(border.Child, toolBar);
                }
            }
            else if (popupChild is Panel panel)
            {
                panel.SetBinding(Panel.BackgroundProperty, new Binding(nameof(ToolBar.Background))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });

                // Style children of the panel
                foreach (UIElement child in panel.Children)
                {
                    StylePopupContents(child, toolBar);
                }
            }
            else if (popupChild is FrameworkElement element)
            {
                // Walk the visual tree for other elements
                int childCount = VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < childCount; i++)
                {
                    if (VisualTreeHelper.GetChild(element, i) is UIElement child)
                    {
                        StylePopupContents(child, toolBar);
                    }
                }
            }
        }

        /// <summary>
        /// Makes the overflow button's internal borders transparent so the parent background shows through.
        /// </summary>
        private static void StyleOverflowButtonVisuals(ToggleButton button)
        {
            // We need to wait for the button's template to be applied
            button.ApplyTemplate();

            // Walk the visual tree and make backgrounds transparent
            int childCount = VisualTreeHelper.GetChildrenCount(button);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(button, i);
                SetTransparentBackgrounds(child);
            }
        }

        /// <summary>
        /// Recursively sets backgrounds to transparent for Border and Panel elements.
        /// </summary>
        private static void SetTransparentBackgrounds(DependencyObject element)
        {
            if (element is Border border)
            {
                border.Background = Brushes.Transparent;
            }
            else if (element is Panel panel && panel is not ToolBarPanel && panel is not ToolBarOverflowPanel)
            {
                panel.Background = Brushes.Transparent;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childCount; i++)
            {
                SetTransparentBackgrounds(VisualTreeHelper.GetChild(element, i));
            }
        }

        /// <summary>
        /// Finds a child element of the specified type in the visual tree.
        /// </summary>
        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                var result = FindChild<T>(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        public static void SetRemoveOverflowButtonWhiteBackground(DependencyObject element, bool value)
        {
            element.SetValue(RemoveOverflowButtonWhiteBackgroundProperty, value);
        }

        public static bool GetRemoveOverflowButtonWhiteBackground(DependencyObject element)
        {
            return (bool)element.GetValue(RemoveOverflowButtonWhiteBackgroundProperty);
        }
    }
}
