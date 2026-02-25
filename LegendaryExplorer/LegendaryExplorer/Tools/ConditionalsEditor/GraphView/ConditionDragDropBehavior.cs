using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LegendaryExplorer.Tools.ConditionalsEditor.GraphView
{
    /// <summary>
    /// Provides attached behaviors for drag-and-drop of condition nodes within the graph view.
    /// </summary>
    public static class ConditionDragDropBehavior
    {
        private static readonly string DragFormat = "ConditionNode";
        private static Point _dragStartPoint;
        private static bool _isDragging;
        private static bool _canStartDrag;

        #region EnableDrag Attached Property

        public static readonly DependencyProperty EnableDragProperty =
            DependencyProperty.RegisterAttached("EnableDrag", typeof(bool), typeof(ConditionDragDropBehavior),
                new PropertyMetadata(false, OnEnableDragChanged));

        public static bool GetEnableDrag(DependencyObject obj) => (bool)obj.GetValue(EnableDragProperty);
        public static void SetEnableDrag(DependencyObject obj, bool value) => obj.SetValue(EnableDragProperty, value);

        private static void OnEnableDragChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement element)
            {
                if ((bool)e.NewValue)
                {
                    element.PreviewMouseLeftButtonDown += Element_PreviewMouseLeftButtonDown;
                    element.PreviewMouseMove += Element_PreviewMouseMove;
                    element.PreviewMouseLeftButtonUp += Element_PreviewMouseLeftButtonUp;
                }
                else
                {
                    element.PreviewMouseLeftButtonDown -= Element_PreviewMouseLeftButtonDown;
                    element.PreviewMouseMove -= Element_PreviewMouseMove;
                    element.PreviewMouseLeftButtonUp -= Element_PreviewMouseLeftButtonUp;
                }
            }
        }

        private static void Element_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _canStartDrag = false;

            // Don't start drag if clicking on a ComboBox, TextBox, CheckBox, or Button
            if (e.OriginalSource is DependencyObject source)
            {
                if (FindAncestor<ComboBox>(source) != null ||
                    FindAncestor<TextBox>(source) != null ||
                    FindAncestor<CheckBox>(source) != null ||
                    FindAncestor<Button>(source) != null ||
                    FindAncestor<ToggleButton>(source) != null)
                {
                    return;
                }
            }

            _dragStartPoint = e.GetPosition(null);
            _isDragging = false;
            _canStartDrag = true;
        }

        private static void Element_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_canStartDrag || e.LeftButton != MouseButtonState.Pressed || _isDragging)
                return;

            Point position = e.GetPosition(null);
            Vector diff = _dragStartPoint - position;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is FrameworkElement element && element.DataContext is ConditionNodeViewModel node)
                {
                    _isDragging = true;
                    var data = new DataObject(DragFormat, node);
                    DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
                    _isDragging = false;
                }
            }
        }

        private static void Element_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _canStartDrag = false;
        }

        #endregion

        #region EnableDrop Attached Property

        public static readonly DependencyProperty EnableDropProperty =
            DependencyProperty.RegisterAttached("EnableDrop", typeof(bool), typeof(ConditionDragDropBehavior),
                new PropertyMetadata(false, OnEnableDropChanged));

        public static bool GetEnableDrop(DependencyObject obj) => (bool)obj.GetValue(EnableDropProperty);
        public static void SetEnableDrop(DependencyObject obj, bool value) => obj.SetValue(EnableDropProperty, value);

        private static void OnEnableDropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement element)
            {
                element.AllowDrop = (bool)e.NewValue;
                if ((bool)e.NewValue)
                {
                    element.DragOver += Element_DragOver;
                    element.Drop += Element_Drop;
                }
                else
                {
                    element.DragOver -= Element_DragOver;
                    element.Drop -= Element_Drop;
                }
            }
        }

        private static void Element_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;

            if (!e.Data.GetDataPresent(DragFormat))
                return;

            var dragged = e.Data.GetData(DragFormat) as ConditionNodeViewModel;
            if (dragged == null) return;

            if (sender is FrameworkElement element)
            {
                var targetGroup = element.DataContext as ConditionGroupViewModel;
                if (targetGroup == null) return;

                // Prevent dropping a group onto itself or its descendants
                if (dragged is ConditionGroupViewModel dragGroup && targetGroup.IsOrContains(dragGroup))
                    return;

                // Prevent dropping into current parent at the exact same position (no-op)
                // This is allowed — user might be reordering
                e.Effects = DragDropEffects.Move;
            }

            e.Handled = true;
        }

        private static void Element_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DragFormat))
                return;

            var dragged = e.Data.GetData(DragFormat) as ConditionNodeViewModel;
            if (dragged == null) return;

            if (sender is FrameworkElement element)
            {
                var targetGroup = element.DataContext as ConditionGroupViewModel;
                if (targetGroup == null) return;

                // Prevent dropping a group onto itself or its descendants
                if (dragged is ConditionGroupViewModel dragGroup && targetGroup.IsOrContains(dragGroup))
                    return;

                // Remove from old parent
                dragged.Parent?.Children.Remove(dragged);

                // Determine insert position based on mouse location
                int insertIndex = GetInsertIndex(element, e, targetGroup);

                // Add to new parent
                dragged.Parent = targetGroup;
                if (insertIndex >= 0 && insertIndex <= targetGroup.Children.Count)
                    targetGroup.Children.Insert(insertIndex, dragged);
                else
                    targetGroup.Children.Add(dragged);
            }

            e.Handled = true;
        }

        private static int GetInsertIndex(FrameworkElement dropTarget, DragEventArgs e, ConditionGroupViewModel group)
        {
            // Try to find which child element we're hovering over
            Point pos = e.GetPosition(dropTarget);

            if (dropTarget is ItemsControl itemsControl)
            {
                for (int i = 0; i < group.Children.Count; i++)
                {
                    var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                    if (container != null)
                    {
                        Point relPos = e.GetPosition(container);
                        if (relPos.Y < container.ActualHeight / 2)
                            return i;
                    }
                }
            }

            return group.Children.Count;
        }

        #endregion

        /// <summary>
        /// Walks up the visual tree to find an ancestor of the specified type.
        /// </summary>
        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T found)
                    return found;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
