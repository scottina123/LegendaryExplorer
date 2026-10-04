using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FontAwesome5;
using LegendaryExplorer.SharedUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
public class ContextMenuIconTests
{
    [STATestMethod]
    public void OpenedAndGeneratedContextActionsKeepToolIconsCommandsAndSeparateCheckmarksInEveryTheme()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(App).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ResourceDictionary previousResources = Application.Current.Resources;
        ContextMenuIcons.Enable();

        try
        {
            foreach (string theme in new[] { "LightTheme.xaml", "DarkTheme.xaml", "ModernDarkTheme.xaml" })
            {
                Application.Current.Resources = LoadResources("AppResources.xaml");
                if (theme != "LightTheme.xaml")
                    Application.Current.Resources.MergedDictionaries.Add(LoadResources(theme));

                var host = new Border { Background = Brushes.Transparent };
                var menuBarItem = new MenuItem { Header = "Copy" };
                var menuBar = new Menu();
                menuBar.Items.Add(menuBarItem);
                var content = new DockPanel();
                DockPanel.SetDock(menuBar, Dock.Top);
                content.Children.Add(menuBar);
                content.Children.Add(host);
                var window = new Window
                {
                    Content = content, Width = 480, Height = 320, Left = -10000, Top = -10000,
                    Opacity = 0, ShowActivated = false, ShowInTaskbar = false
                };
                var menu = new ContextMenu { PlacementTarget = host, Opacity = 0 };
                host.ContextMenu = menu;
                var copy = new MenuItem { Header = "Copy value" };
                var trash = new MenuItem { Header = "Trash entry and children" };
                var rename = new MenuItem { Header = "Rename object..." };
                var archetype = new MenuItem { Header = "Go to Archetype" };
                var links = new MenuItem { Header = "Change Links..." };
                var find = new MenuItem { Header = "Find references" };
                var command = new RelayCommand(_ => { });
                var clone = new MenuItem
                {
                    Header = "Clone Tree", Command = command, CommandParameter = host,
                    IsCheckable = true, IsChecked = true, InputGestureText = "Ctrl+D"
                };
                var headerTemplate = new DataTemplate
                {
                    VisualTree = new FrameworkElementFactory(typeof(TextBlock))
                };
                headerTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding());
                clone.HeaderTemplate = headerTemplate;

                var toolImage = new Image { Width = 16, Height = 16 };
                var openTool = new MenuItem { Header = "Open in Package Editor", Icon = toolImage };
                var boundToolImage = new Image { Width = 16, Height = 16 };
                var boundOpenTool = new MenuItem { Header = "Open in Mesh Explorer" };
                boundOpenTool.SetBinding(MenuItem.IconProperty, new Binding("Icon")
                {
                    Source = new { Icon = boundToolImage }
                });
                var late = new MenuItem { Header = "Clone Tree" };
                string openingHeader = "Rename object...";
                menu.Opened += (_, _) => late.Header = openingHeader;
                var targets = new ObservableCollection<string> { "Copy.pcc" };
                var generated = new MenuItem { Header = "Clone Tree", ItemsSource = targets };
                generated.ItemContainerStyle = new Style(typeof(MenuItem));
                generated.ItemContainerStyle.Setters.Add(new Setter(HeaderedItemsControl.HeaderProperty, new Binding()));
                foreach (MenuItem item in new[] { copy, trash, rename, archetype, links, find, clone, openTool, boundOpenTool, late, generated })
                    menu.Items.Add(item);

                try
                {
                    window.Show();
                    FlushDispatcher();
                    Assert.IsNull(menuBarItem.Icon, "A menu-bar action must not be changed by context-menu decoration.");
                    menu.IsOpen = true;
                    FlushDispatcher();

                    AssertIcon(copy, EFontAwesomeIcon.Solid_Copy);
                    AssertIcon(trash, EFontAwesomeIcon.Solid_TrashAlt);
                    AssertIcon(rename, EFontAwesomeIcon.Solid_PencilAlt);
                    AssertIcon(archetype, EFontAwesomeIcon.Solid_LevelUpAlt);
                    AssertIcon(links, EFontAwesomeIcon.Solid_Link);
                    AssertIcon(find, EFontAwesomeIcon.Solid_Search);
                    AssertIcon(clone, EFontAwesomeIcon.Solid_Clone);
                    AssertIcon(late, EFontAwesomeIcon.Solid_PencilAlt);
                    Assert.AreSame(toolImage, openTool.Icon);
                    Assert.AreSame(boundToolImage, boundOpenTool.Icon);
                    Assert.IsTrue(BindingOperations.IsDataBound(boundOpenTool, MenuItem.IconProperty));
                    Assert.AreSame(command, clone.Command);
                    Assert.AreSame(host, clone.CommandParameter);
                    Assert.IsTrue(clone.IsChecked);
                    Assert.IsTrue(clone.IsCheckable);
                    Assert.AreEqual("Ctrl+D", clone.InputGestureText);

                    clone.ApplyTemplate();
                    var iconHost = (ContentPresenter)clone.Template.FindName("ActionIcon", clone);
                    var checkmarkHost = (Border)clone.Template.FindName("CheckmarkHost", clone);
                    var checkmark = (Path)clone.Template.FindName("Checkmark", clone);
                    var headerHost = (ContentPresenter)clone.Template.FindName("HeaderHost", clone);
                    Assert.AreEqual(Visibility.Visible, iconHost.Visibility, theme);
                    Assert.AreEqual(Visibility.Visible, checkmarkHost.Visibility, theme);
                    Assert.AreEqual(Visibility.Visible, checkmark.Visibility, theme);
                    Assert.AreNotEqual(Grid.GetColumn(iconHost), Grid.GetColumn(checkmarkHost));
                    Assert.AreSame(clone.Icon, iconHost.Content);
                    Assert.AreSame(headerTemplate, headerHost.ContentTemplate);
                    Assert.AreEqual(clone.Foreground, ((ImageAwesome)clone.Icon).Foreground);

                    generated.IsSubmenuOpen = true;
                    FlushDispatcher();
                    AssertIcon(WaitForGeneratedIcon(generated, 0), EFontAwesomeIcon.Solid_Clone);
                    targets.Add("Second object");
                    AssertIcon(WaitForGeneratedIcon(generated, 1), EFontAwesomeIcon.Solid_Clone);
                    generated.IsSubmenuOpen = false;
                    menu.IsOpen = false;
                    FlushDispatcher();
                    openingHeader = "Change Links...";
                    menu.IsOpen = true;
                    FlushDispatcher();
                    AssertIcon(late, EFontAwesomeIcon.Solid_Link);
                    Assert.AreSame(toolImage, openTool.Icon);
                    Assert.IsTrue(clone.IsChecked);
                }
                finally
                {
                    menu.IsOpen = false;
                    window.Close();
                    FlushDispatcher();
                }
            }
        }
        finally
        {
            Application.Current.Resources = previousResources;
        }
    }

    private static ResourceDictionary LoadResources(string path) =>
        (ResourceDictionary)Application.LoadComponent(new Uri($"/LegendaryExplorer;component/{path}", UriKind.Relative));

    private static void AssertIcon(MenuItem item, EFontAwesomeIcon expected)
    {
        Assert.IsNotNull(item, "The generated menu item must be realized when the submenu opens.");
        string diagnostic = $"Header={item.Header}, IsLoaded={item.IsLoaded}, Icon={item.Icon?.GetType().Name ?? "null"}";
        for (ItemsControl owner = ItemsControl.ItemsControlFromItemContainer(item); owner is not null;)
        {
            diagnostic += $", owner={owner.GetType().Name}";
            if (owner is not MenuItem parent)
                break;
            diagnostic += $"({parent.Header})";
            owner = ItemsControl.ItemsControlFromItemContainer(parent);
        }
        Assert.IsInstanceOfType<ImageAwesome>(item.Icon, diagnostic);
        Assert.AreEqual(expected, ((ImageAwesome)item.Icon).Icon, item.Header?.ToString());
    }

    private static MenuItem WaitForGeneratedIcon(MenuItem parent, int index)
    {
        // A newly added container receives its Loaded broadcast on a later render tick,
        // after an Invoke(ApplicationIdle) can already have returned.
        MenuItem container = null;
        var frame = new DispatcherFrame();
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        timer.Tick += (_, _) =>
        {
            container = parent.ItemContainerGenerator.ContainerFromIndex(index) as MenuItem;
            if (container is { IsLoaded: true, Icon: not null } || DateTime.UtcNow >= deadline)
                frame.Continue = false;
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
        return container;
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
