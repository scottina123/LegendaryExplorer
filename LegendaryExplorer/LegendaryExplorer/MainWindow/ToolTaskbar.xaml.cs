using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LegendaryExplorer.MainWindow
{
    public partial class ToolTaskbar : ToolListControl
    {
        private const double ShortcutStride = 42;
        private const string DragDataFormat = "LegendaryExplorer.PinnedTool";
        private readonly DispatcherTimer dragScrollTimer;
        private Button dragSourceButton;
        private Point dragStart;
        private Point dragPosition;
        private int dragScrollDirection;

        internal bool IsDragging { get; private set; }

        public ToolTaskbar()
        {
            InitializeComponent();
            dragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
            dragScrollTimer.Tick += DragScrollTimer_Tick;
        }

        public override void setToolList(IEnumerable<Tool> enumerable)
        {
            // Preserve the user's saved shortcut order.
            tools = enumerable.Where(tool => tool.open != null).ToList();
            PinnedToolList.ItemsSource = tools;
        }

        private void Shortcut_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            dragSourceButton = sender as Button;
            dragStart = e.GetPosition(this);
        }

        private void Shortcut_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!IsDragging && sender == dragSourceButton) dragSourceButton = null;
        }

        private void Shortcut_MouseMove(object sender, MouseEventArgs e)
        {
            if (IsDragging || e.LeftButton != MouseButtonState.Pressed
                || sender is not Button button || button != dragSourceButton || !button.IsMouseCaptured
                || button.DataContext is not Tool tool)
            {
                return;
            }

            Point position = e.GetPosition(this);
            if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            e.Handled = true;
            IsDragging = true;
            // Cancel the button press so releasing a dragged icon cannot launch its tool.
            button.ReleaseMouseCapture();
            try
            {
                DragDrop.DoDragDrop(button,
                    new DataObject(DragDataFormat, new TaskbarDragData(this, tool)), DragDropEffects.Move);
            }
            finally
            {
                ClearDropIndicator();
                IsDragging = false;
                dragSourceButton = null;
            }
        }

        protected override void Button_Click(object sender, RoutedEventArgs e)
        {
            if (IsDragging)
            {
                e.Handled = true;
                return;
            }
            base.Button_Click(sender, e);
        }

        private bool TryGetDraggedTool(DragEventArgs e, out Tool tool)
        {
            tool = null;
            if (e.Data.GetDataPresent(DragDataFormat)
                && e.Data.GetData(DragDataFormat) is TaskbarDragData data
                && data.Source == this && tools?.Contains(data.Tool) == true)
            {
                tool = data.Tool;
                return true;
            }
            return false;
        }

        private void Taskbar_DragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (!TryGetDraggedTool(e, out _))
            {
                ClearDropIndicator();
                return;
            }

            e.Effects = DragDropEffects.Move;
            dragPosition = e.GetPosition(TaskbarScrollViewer);
            UpdateDropIndicator();
            dragScrollDirection = dragPosition.X < 18 ? -1
                : dragPosition.X > TaskbarScrollViewer.ActualWidth - 18 ? 1 : 0;
            if (dragScrollDirection != 0 && TaskbarScrollViewer.ScrollableWidth > 0)
                dragScrollTimer.Start();
            else
                dragScrollTimer.Stop();
        }

        internal static int GetDropInsertionIndex(double contentX, int count) =>
            Math.Clamp((int)Math.Floor((contentX + ShortcutStride / 2) / ShortcutStride), 0, count);

        private int CurrentDropInsertionIndex =>
            GetDropInsertionIndex(TaskbarScrollViewer.TranslatePoint(dragPosition, PinnedToolList).X, tools.Count);

        private void UpdateDropIndicator()
        {
            double indicatorX = CurrentDropInsertionIndex * ShortcutStride - TaskbarScrollViewer.HorizontalOffset;
            DropIndicator.RenderTransform = new TranslateTransform(
                Math.Clamp(indicatorX, 0, Math.Max(0, TaskbarScrollViewer.ActualWidth - DropIndicator.Width)), 0);
            DropIndicator.Visibility = Visibility.Visible;
        }

        private void DragScrollTimer_Tick(object sender, EventArgs e)
        {
            TaskbarScrollViewer.ScrollToHorizontalOffset(
                TaskbarScrollViewer.HorizontalOffset + dragScrollDirection * ShortcutStride / 2);
            TaskbarScrollViewer.UpdateLayout();
            UpdateDropIndicator();
        }

        private void Taskbar_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (TryGetDraggedTool(e, out Tool tool))
            {
                dragPosition = e.GetPosition(TaskbarScrollViewer);
                int insertionIndex = CurrentDropInsertionIndex;
                e.Effects = DragDropEffects.Move;
                ClearDropIndicator();
                ToolSet.MovePinnedTool(tool, insertionIndex);
            }
            else
            {
                ClearDropIndicator();
            }
        }

        private void Taskbar_DragLeave(object sender, DragEventArgs e)
        {
            if (!new Rect(RenderSize).Contains(e.GetPosition(this))) ClearDropIndicator();
        }

        private void ClearDropIndicator()
        {
            dragScrollTimer.Stop();
            dragScrollDirection = 0;
            DropIndicator.Visibility = Visibility.Collapsed;
        }

        private sealed record TaskbarDragData(ToolTaskbar Source, Tool Tool);

        private void Taskbar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateScrollButtons();

        private void TaskbarScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateScrollButtons();

        private void UpdateScrollButtons()
        {
            if (TaskbarScrollViewer == null) return;

            // Compare with the full control width so the arrows can disappear again
            // when removing pins leaves enough space for all the remaining shortcuts.
            bool overflow = TaskbarScrollViewer.ExtentWidth > ActualWidth + 0.5;
            ScrollLeftButton.Visibility = ScrollRightButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            ScrollLeftButton.IsEnabled = TaskbarScrollViewer.HorizontalOffset > 0.5;
            ScrollRightButton.IsEnabled = TaskbarScrollViewer.HorizontalOffset < TaskbarScrollViewer.ScrollableWidth - 0.5;
        }

        private void ScrollLeft_Click(object sender, RoutedEventArgs e) =>
            TaskbarScrollViewer.ScrollToHorizontalOffset(TaskbarScrollViewer.HorizontalOffset - 126);

        private void ScrollRight_Click(object sender, RoutedEventArgs e) =>
            TaskbarScrollViewer.ScrollToHorizontalOffset(TaskbarScrollViewer.HorizontalOffset + 126);

        private void TaskbarScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (TaskbarScrollViewer.ScrollableWidth > 0)
            {
                TaskbarScrollViewer.ScrollToHorizontalOffset(TaskbarScrollViewer.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }
    }
}
