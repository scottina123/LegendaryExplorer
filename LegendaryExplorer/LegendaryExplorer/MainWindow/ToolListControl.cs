using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;

namespace LegendaryExplorer.MainWindow
{
    public abstract class ToolListControl : UserControl
    {
        protected List<Tool> tools;

        private readonly HashSet<Tool> openingTools = new();
        private readonly Dictionary<Tool, long> lastLaunchTimes = new();

        public virtual void setToolList(IEnumerable<Tool> enumerable)
        {
            tools = enumerable.ToList();
            tools.Sort((x, y) => x.name.CompareTo(y.name));
        }

        protected virtual void Button_Click(object sender, RoutedEventArgs e)
        {
            if (!e.Handled && (sender as Button)?.DataContext is Tool t)
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    t.IsFavorited = !t.IsFavorited;
                }
                else
                {
                    // Ignore duplicate input even if opening the window pumps the dispatcher.
                    if (openingTools.Contains(t)
                        || (lastLaunchTimes.TryGetValue(t, out long lastLaunchTime)
                            && Environment.TickCount64 - lastLaunchTime < System.Windows.Forms.SystemInformation.DoubleClickTime))
                    {
                        return;
                    }

                    openingTools.Add(t);
                    try
                    {
                        t.open();
                        // Start the debounce after startup, so queued clicks cannot open another copy.
                        lastLaunchTimes[t] = Environment.TickCount64;
                    }
                    finally
                    {
                        openingTools.Remove(t);
                    }
                }
            }
        }

        
        protected void Button_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
        }

        protected virtual void Button_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Button b)
            {
                if (b.FindName("highlightUnderline") is Rectangle r)
                {
                    r.Visibility = Visibility.Visible;
                }

                if (b.FindName("toolIcon") is Image img)
                {
                    img.Opacity = 1;
                }
            }
        }

        protected virtual void Button_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Button b)
            {
                if (b.FindName("highlightUnderline") is Rectangle r)
                {
                    r.Visibility = Visibility.Hidden;
                }

                if (b.FindName("toolIcon") is Image img)
                {
                    img.Opacity = 0.85;
                }
            }
        }
    }
}
