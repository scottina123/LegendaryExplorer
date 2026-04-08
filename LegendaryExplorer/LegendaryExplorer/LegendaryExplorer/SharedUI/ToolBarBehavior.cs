using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

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
                    toolBar.SizeChanged += OnToolBarSizeChanged;
                    // If already loaded, apply immediately
                    if (toolBar.IsLoaded)
                    {
                        ApplyOverflowButtonStyle(toolBar);
                    }
                }
                else
                {
                    toolBar.Loaded -= OnToolBarLoaded;
                    toolBar.SizeChanged -= OnToolBarSizeChanged;
                }
            }
        }

        private static void OnToolBarLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ToolBar toolBar)
            {
                ApplyOverflowButtonStyle(toolBar);
                toolBar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ApplyOverflowButtonStyle(toolBar)));
            }
        }

        private static void OnToolBarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ToolBar toolBar && toolBar.IsLoaded)
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
                    overflowButton.SetBinding(Control.BackgroundProperty, new Binding(nameof(ToolBar.Background))
                    {
                        Source = toolBar,
                        Mode = BindingMode.OneWay
                    });
                    overflowButton.SetBinding(Control.BorderBrushProperty, new Binding(nameof(ToolBar.BorderBrush))
                    {
                        Source = toolBar,
                        Mode = BindingMode.OneWay
                    });
                    overflowButton.SetBinding(Control.ForegroundProperty, new Binding(nameof(ToolBar.Foreground))
                    {
                        Source = toolBar,
                        Mode = BindingMode.OneWay
                    });

                    // Find any template visuals inside the button and bind them to the toolbar theme.
                    StyleOverflowButtonVisuals(overflowButton, toolBar);
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
        private static void StyleOverflowButtonVisuals(ToggleButton button, ToolBar toolBar)
        {
            // We need to wait for the button's template to be applied
            button.ApplyTemplate();

            // Walk the visual tree and apply toolbar theme bindings to the template visuals
            int childCount = VisualTreeHelper.GetChildrenCount(button);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(button, i);
                ApplyToolbarTheme(child, toolBar);
            }
        }

        /// <summary>
        /// Recursively applies toolbar background and foreground to the overflow button visuals.
        /// </summary>
        private static void ApplyToolbarTheme(DependencyObject element, ToolBar toolBar)
        {
            if (element is Border border)
            {
                border.SetBinding(Border.BackgroundProperty, new Binding(nameof(ToolBar.Background))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
                border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(ToolBar.BorderBrush))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
            }
            else if (element is Panel panel && panel is not ToolBarPanel && panel is not ToolBarOverflowPanel)
            {
                panel.SetBinding(Panel.BackgroundProperty, new Binding(nameof(ToolBar.Background))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
            }
            else if (element is Control control)
            {
                control.SetBinding(Control.BackgroundProperty, new Binding(nameof(ToolBar.Background))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
                control.SetBinding(Control.BorderBrushProperty, new Binding(nameof(ToolBar.BorderBrush))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
                control.SetBinding(Control.ForegroundProperty, new Binding(nameof(ToolBar.Foreground))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
            }
            else if (element is Shape shape)
            {
                shape.SetBinding(Shape.FillProperty, new Binding(nameof(ToolBar.Foreground))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
                shape.SetBinding(Shape.StrokeProperty, new Binding(nameof(ToolBar.Foreground))
                {
                    Source = toolBar,
                    Mode = BindingMode.OneWay
                });
            }

            int childCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childCount; i++)
            {
                ApplyToolbarTheme(VisualTreeHelper.GetChild(element, i), toolBar);
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
