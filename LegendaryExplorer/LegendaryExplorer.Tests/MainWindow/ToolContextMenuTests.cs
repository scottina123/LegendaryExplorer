using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.MainWindow;
using LegendaryExplorer.Misc.AppSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.MainWindow;

[TestClass]
[DoNotParallelize]
public class ToolContextMenuTests
{
    [STATestMethod]
    public void ActionsToggleFavoritesAndPinsIndependentlyWithoutLaunchingTheTool()
    {
        WithIsolatedTools(() =>
        {
            int launches = 0;
            var tool = new Tool { name = "Test tool", open = () => launches++ };
            var host = new Button { DataContext = tool };
            var menu = new ToolContextMenu { PlacementTarget = host };
            host.ContextMenu = menu;
            Open(menu);
            var favorite = (MenuItem)menu.Items[0];
            var pin = (MenuItem)menu.Items[1];

            Assert.AreEqual("Favorite", favorite.Header);
            Assert.AreEqual("Pin to taskbar", pin.Header);
            Click(favorite);
            Assert.IsTrue(tool.IsFavorited);
            Assert.IsFalse(tool.IsPinnedToTaskbar);
            Assert.AreEqual("Unfavorite", favorite.Header);

            Click(pin);
            Assert.IsTrue(tool.IsFavorited);
            Assert.IsTrue(tool.IsPinnedToTaskbar);
            Assert.AreEqual("Unpin from taskbar", pin.Header);

            Click(favorite);
            Assert.IsFalse(tool.IsFavorited);
            Assert.IsTrue(tool.IsPinnedToTaskbar, "Removing a favorite must retain its taskbar shortcut.");
            Click(pin);
            Assert.IsFalse(tool.IsPinnedToTaskbar);
            Assert.AreEqual("Pin to taskbar", pin.Header);
            Assert.AreEqual(0, launches, "Context menu actions must not launch the tool.");
        });
    }

    [STATestMethod]
    public void OpeningRefreshesChangedStateAndRetargetsToAnotherShortcut()
    {
        WithIsolatedTools(() =>
        {
            var first = new Tool { name = "First tool", open = () => { } };
            var second = new Tool { name = "Second tool", open = () => { } };
            var menu = new ToolContextMenu { PlacementTarget = new Button { DataContext = first } };
            Open(menu);
            first.IsFavorited = true;
            first.IsPinnedToTaskbar = true;
            Open(menu);
            Assert.AreEqual("Unfavorite", ((MenuItem)menu.Items[0]).Header);
            Assert.AreEqual("Unpin from taskbar", ((MenuItem)menu.Items[1]).Header);

            menu.PlacementTarget = new Button { DataContext = second };
            Open(menu);
            Assert.AreEqual("Favorite", ((MenuItem)menu.Items[0]).Header);
            Assert.AreEqual("Pin to taskbar", ((MenuItem)menu.Items[1]).Header);
            Click((MenuItem)menu.Items[1]);
            Assert.IsTrue(second.IsPinnedToTaskbar);
            Assert.IsTrue(first.IsPinnedToTaskbar, "A reused menu must act on its current placement target.");
        });
    }

    [STATestMethod]
    public void ComingSoonAndMissingTargetsCannotCreateUnusableShortcuts()
    {
        WithIsolatedTools(() =>
        {
            var tool = new Tool { name = "Coming soon" };
            var menu = new ToolContextMenu { PlacementTarget = new Button { DataContext = tool } };
            Open(menu);
            Assert.IsTrue(((MenuItem)menu.Items[0]).IsEnabled);
            Assert.IsFalse(((MenuItem)menu.Items[1]).IsEnabled);
            Click((MenuItem)menu.Items[1]);
            Assert.IsFalse(tool.IsPinnedToTaskbar);

            menu.PlacementTarget = new Button();
            Open(menu);
            Assert.IsFalse(((MenuItem)menu.Items[0]).IsEnabled);
            Assert.IsFalse(((MenuItem)menu.Items[1]).IsEnabled);
            Click((MenuItem)menu.Items[0]);
        });
    }

    private static void Open(ContextMenu menu) => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));

    private static void Click(MenuItem item)
    {
        var click = new RoutedEventArgs(MenuItem.ClickEvent);
        item.RaiseEvent(click);
        Assert.IsTrue(click.Handled);
    }

    private static void WithIsolatedTools(Action check)
    {
        var itemsField = typeof(ToolSet).GetField("items", BindingFlags.Static | BindingFlags.NonPublic)!;
        var loadedField = typeof(Settings).GetField("Loaded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousItems = itemsField.GetValue(null);
        object previousLoaded = loadedField.GetValue(null);
        loadedField.SetValue(null, false);
        itemsField.SetValue(null, null); // Exercise menu behavior without saving the user's tools or preferences.
        try
        {
            check();
        }
        finally
        {
            itemsField.SetValue(null, previousItems);
            loadedField.SetValue(null, previousLoaded);
        }
    }
}
