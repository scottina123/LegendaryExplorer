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
    /// <remarks>
    /// Each toolbar template instance is styled once. Earlier versions re-ran the full visual-tree walk and
    /// attached a new <see cref="Popup.Opened"/> handler on every <see cref="FrameworkElement.SizeChanged"/>,
    /// so long-lived toolbars accumulated thousands of handlers and got slower the longer the app ran.
    /// </remarks>
    public static class ToolBarBehavior
    {
        public static readonly DependencyProperty RemoveOverflowButtonWhiteBackgroundProperty = DependencyProperty.RegisterAttached(
            "RemoveOverflowButtonWhiteBackground", typeof(bool), typeof(ToolBarBehavior), new PropertyMetadata(false, OnPropertyChanged));

        /// <summary>
        /// The overflow grid of the template instance that has already been styled.
        /// A new template instance (re-templating) has a new grid, which triggers styling again.
        /// </summary>
        private static readonly DependencyProperty StyledOverflowGridProperty = DependencyProperty.RegisterAttached(
            "StyledOverflowGrid", typeof(Grid), typeof(ToolBarBehavior), new PropertyMetadata(null));

        private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ToolBar toolBar)
            {
                toolBar.Loaded -= OnToolBarLoaded;
                toolBar.SizeChanged -= OnToolBarSizeChanged;
                if ((bool)e.NewValue)
                {
                    toolBar.Loaded += OnToolBarLoaded;
                    toolBar.SizeChanged += OnToolBarSizeChanged;
                    // If already loaded, apply immediately
                    if (toolBar.IsLoaded)
                    {
                        ApplyOverflowButtonStyle(toolBar);
                    }
                }
            }
        }

        private static void OnToolBarLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ToolBar toolBar)
            {
                ApplyOverflowButtonStyle(toolBar);
                if (!IsStyled(toolBar))
                {
                    // Template parts may not exist until layout has run.
                    toolBar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ApplyOverflowButtonStyle(toolBar)));
                }
            }
        }

        private static void OnToolBarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Cheap once styled: only a template name lookup.
            if (sender is ToolBar toolBar && toolBar.IsLoaded && !IsStyled(toolBar))
            {
                ApplyOverflowButtonStyle(toolBar);
            }
        }

        private static Grid FindOverflowGrid(ToolBar toolBar) =>
            toolBar.Template?.FindName("OverflowGrid", toolBar) as Grid;

        private static bool IsStyled(ToolBar toolBar)
        {
            Grid overflowGrid = FindOverflowGrid(toolBar);
            return overflowGrid != null && ReferenceEquals(toolBar.GetValue(StyledOverflowGridProperty), overflowGrid);
        }

        private static void ApplyOverflowButtonStyle(ToolBar toolBar)
        {
            // Find the OverflowGrid by its template part name
            if (FindOverflowGrid(toolBar) is not Grid overflowGrid
                || ReferenceEquals(toolBar.GetValue(StyledOverflowGridProperty), overflowGrid))
            {
                return;
            }

            // Bind the overflow grid's background to match the toolbar
            overflowGrid.SetBinding(Panel.BackgroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Background)));

            // Find the ToggleButton inside and style its template elements
            if (FindChild<ToggleButton>(overflowGrid) is ToggleButton overflowButton)
            {
                overflowButton.SetBinding(Control.BackgroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Background)));
                overflowButton.SetBinding(Control.BorderBrushProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.BorderBrush)));
                overflowButton.SetBinding(Control.ForegroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Foreground)));

                // Find any template visuals inside the button and bind them to the toolbar theme.
                StyleOverflowButtonVisuals(overflowButton, toolBar);
            }

            // Style the overflow popup/dropdown
            StyleOverflowPopup(toolBar);

            toolBar.SetValue(StyledOverflowGridProperty, overflowGrid);
        }

        private static Binding CreateToolBarBinding(ToolBar toolBar, string path) => new(path)
        {
            Source = toolBar,
            Mode = BindingMode.OneWay
        };

        /// <summary>
        /// Styles the overflow popup that appears when the overflow button is clicked.
        /// </summary>
        private static void StyleOverflowPopup(ToolBar toolBar)
        {
            // Find the Popup named "OverflowPopup" in the toolbar template
            if (toolBar.Template?.FindName("OverflowPopup", toolBar) is Popup overflowPopup)
            {
                // A static handler can be detached first, so a template instance never holds more than one.
                overflowPopup.Opened -= OnOverflowPopupOpened;
                overflowPopup.Opened += OnOverflowPopupOpened;

                // If popup already has a child, style it now
                if (overflowPopup.Child != null)
                {
                    StylePopupContents(overflowPopup.Child, toolBar);
                }
            }

            // Also look for the ToolBarOverflowPanel directly
            if (toolBar.Template?.FindName("PART_ToolBarOverflowPanel", toolBar) is ToolBarOverflowPanel overflowPanel)
            {
                overflowPanel.SetBinding(Panel.BackgroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Background)));
            }
        }

        private static void OnOverflowPopupOpened(object sender, EventArgs e)
        {
            if (sender is Popup { Child: not null } popup && popup.TemplatedParent is ToolBar toolBar)
            {
                StylePopupContents(popup.Child, toolBar);
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
                border.SetBinding(Border.BackgroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Background)));

                // Also style children of the border
                if (border.Child != null)
                {
                    StylePopupContents(border.Child, toolBar);
                }
            }
            else if (popupChild is Panel panel)
            {
                panel.SetBinding(Panel.BackgroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Background)));

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
                border.SetBinding(Border.BackgroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Background)));
                border.SetBinding(Border.BorderBrushProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.BorderBrush)));
            }
            else if (element is Panel panel && panel is not ToolBarPanel && panel is not ToolBarOverflowPanel)
            {
                panel.SetBinding(Panel.BackgroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Background)));
            }
            else if (element is Control control)
            {
                control.SetBinding(Control.BackgroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Background)));
                control.SetBinding(Control.BorderBrushProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.BorderBrush)));
                control.SetBinding(Control.ForegroundProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Foreground)));
            }
            else if (element is Shape shape)
            {
                shape.SetBinding(Shape.FillProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Foreground)));
                shape.SetBinding(Shape.StrokeProperty, CreateToolBarBinding(toolBar, nameof(ToolBar.Foreground)));
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
