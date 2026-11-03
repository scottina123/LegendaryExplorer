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
    public void OneFavoriteActionTogglesTheTaskbarWithoutLaunching()
    {
        WithIsolatedTools(() =>
        {
            int launches = 0;
            var tool = new Tool { name = "Test tool", open = () => launches++ };
            var menu = new ToolContextMenu { PlacementTarget = new Button { DataContext = tool } };
            Open(menu);
            Assert.HasCount(1, menu.Items);
            var favorite = (MenuItem)menu.Items[0];
            Assert.AreEqual("Favorite", favorite.Header);
            Click(favorite);
            Assert.IsTrue(tool.IsFavorited && tool.IsPinnedToTaskbar);
            Assert.AreEqual("Unfavorite", favorite.Header);
            Click(favorite);
            Assert.IsFalse(tool.IsFavorited || tool.IsPinnedToTaskbar);
            Assert.AreEqual("Favorite", favorite.Header);
            Assert.AreEqual(0, launches);
        });
    }

    [STATestMethod]
    public void ReusedMenuTargetsCurrentToolAndDisablesUnavailableTools()
    {
        WithIsolatedTools(() =>
        {
            var first = new Tool { name = "First", open = () => { }, IsFavorited = true };
            var second = new Tool { name = "Second", open = () => { } };
            var menu = new ToolContextMenu { PlacementTarget = new Button { DataContext = first } };
            Open(menu);
            Assert.AreEqual("Unfavorite", ((MenuItem)menu.Items[0]).Header);
            menu.PlacementTarget = new Button { DataContext = second };
            Open(menu);
            Click((MenuItem)menu.Items[0]);
            Assert.IsTrue(first.IsFavorited && second.IsFavorited);
            menu.PlacementTarget = new Button { DataContext = new Tool { name = "Coming soon" } };
            Open(menu);
            Assert.IsFalse(((MenuItem)menu.Items[0]).IsEnabled);
            menu.PlacementTarget = new Button();
            Open(menu);
            Assert.IsFalse(((MenuItem)menu.Items[0]).IsEnabled);
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
