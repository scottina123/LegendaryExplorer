using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LegendaryExplorer.MainWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.MainWindow;

[TestClass]
public class ToolListControlTests
{
    [STATestMethod]
    public void OneClickIsConsumedEvenIfTheHandlerIsRegisteredTwice()
    {
        int launches = 0;
        var panel = new TestToolListControl();
        Button button = panel.CreateButton(new Tool { open = () => launches++ });
        panel.RegisterClickHandler(button, handledEventsToo: true);

        var click = new RoutedEventArgs(Button.ClickEvent);
        button.RaiseEvent(click);

        Assert.AreEqual(1, launches);
        Assert.IsTrue(click.Handled);
    }

    [STATestMethod]
    public void RapidDuplicateClicksAreSuppressedAcrossRecreatedButtons()
    {
        int launches = 0;
        var panel = new TestToolListControl();
        var tool = new Tool { name = "Test tool", open = () => launches++ };
        panel.setToolList([tool]);
        Click(panel.CreateButton(tool));

        // Category changes and searches recreate buttons for the same tool.
        panel.setToolList([tool]);
        Click(panel.CreateButton(tool));

        Assert.AreEqual(1, launches);
    }

    [STATestMethod]
    public void SlowLaunchSuppressesReentrantAndQueuedClicksButAllowsLaterLaunches()
    {
        int launches = 0;
        var panel = new TestToolListControl();
        Button button = null;
        var tool = new Tool
        {
            open = () =>
            {
                if (++launches == 1)
                {
                    // Window startup can take longer than the double-click interval
                    // and pump messages before the launch handler has returned.
                    WaitForSeparateClick();
                    button.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Click(button)));
                    FlushDispatcher();
                    Assert.AreEqual(1, launches, "A click during startup must not launch a second window.");
                    button.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Click(button)));
                }
            }
        };
        button = panel.CreateButton(tool);

        Click(button);
        FlushDispatcher();
        Assert.AreEqual(1, launches, "A duplicate queued during startup must also be ignored.");

        WaitForSeparateClick();
        Click(button);
        Assert.AreEqual(2, launches, "A later intentional click should still open another window.");
    }

    [STATestMethod]
    public void DifferentToolsCanLaunchConsecutively()
    {
        int firstLaunches = 0;
        int secondLaunches = 0;
        var panel = new TestToolListControl();
        Button first = panel.CreateButton(new Tool { open = () => firstLaunches++ });
        Button second = panel.CreateButton(new Tool { open = () => secondLaunches++ });

        Click(first);
        Click(second);
        Click(first);

        Assert.AreEqual(1, firstLaunches);
        Assert.AreEqual(1, secondLaunches);
    }

    [STATestMethod]
    public void FailedLaunchCanBeRetried()
    {
        int attempts = 0;
        var panel = new TestToolListControl();
        Button button = panel.CreateButton(new Tool
        {
            open = () =>
            {
                if (++attempts == 1)
                {
                    throw new InvalidOperationException("Test launch failure");
                }
            }
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => Click(button));
        Click(button);

        Assert.AreEqual(2, attempts, "A failed launch must not leave the tool blocked.");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void WaitForSeparateClick() =>
        Thread.Sleep(System.Windows.Forms.SystemInformation.DoubleClickTime + 50);

    private static void FlushDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class TestToolListControl : ToolListControl
    {
        public Button CreateButton(Tool tool)
        {
            var button = new Button { DataContext = tool };
            RegisterClickHandler(button);
            return button;
        }

        public void RegisterClickHandler(Button button, bool handledEventsToo = false) =>
            button.AddHandler(Button.ClickEvent, new RoutedEventHandler(Button_Click), handledEventsToo);
    }
}
