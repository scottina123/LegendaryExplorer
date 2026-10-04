using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FontAwesome5;
using LegendaryExplorer.SharedUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
[DoNotParallelize]
public class ToolbarMenuIconTests
{
    [STATestMethod]
    public void ToolbarDropdownsDecorateNestedAndGeneratedActionsWithoutChangingToolbarCaptionsInEveryTheme()
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

                var menuBar = new Menu { VerticalAlignment = VerticalAlignment.Top };
                var file = new MenuItem { Header = "File" };
                var save = new MenuItem { Header = "Save" };
                file.Items.Add(save);
                var pickerControl = new UserControl { Content = new TextBox { Text = "Pinned package filter" } };
                var pickerTemplate = new ControlTemplate(typeof(MenuItem))
                {
                    VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
                };
                pickerTemplate.VisualTree.SetValue(ContentPresenter.ContentSourceProperty, "Header");
                var embeddedPicker = new MenuItem { Header = pickerControl, Template = pickerTemplate };
                file.Items.Add(embeddedPicker);
                var tools = new MenuItem { Header = "Tools" };
                var toolbarAction = new MenuItem { Header = "Copy" };
                menuBar.Items.Add(file);
                menuBar.Items.Add(tools);
                menuBar.Items.Add(toolbarAction);
                var window = new Window
                {
                    Content = menuBar, Width = 480, Height = 320, Left = -10000, Top = -10000,
                    Opacity = 0, ShowActivated = false, ShowInTaskbar = false
                };

                var commandParameter = new object();
                var command = new RelayCommand(_ => { });
                var clone = new MenuItem
                {
                    Header = "Clone Tree", Command = command, CommandParameter = commandParameter,
                    IsCheckable = true, IsChecked = true, InputGestureText = "Ctrl+D"
                };
                var headerTemplate = new DataTemplate
                {
                    VisualTree = new FrameworkElementFactory(typeof(TextBlock))
                };
                headerTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new Binding());
                clone.HeaderTemplate = headerTemplate;

                var explicitIcon = new Image { Width = 16, Height = 16 };
                var explicitTool = new MenuItem { Header = "Open in Package Editor", Icon = explicitIcon };
                var boundIcon = new Image { Width = 16, Height = 16 };
                var boundTool = new MenuItem { Header = "Open in Mesh Explorer" };
                boundTool.SetBinding(MenuItem.IconProperty, new Binding("Icon")
                {
                    Source = new { Icon = boundIcon }
                });

                var customTemplate = new ControlTemplate(typeof(MenuItem))
                {
                    VisualTree = new FrameworkElementFactory(typeof(Border))
                };
                var customAction = new MenuItem { Header = "Find references", Template = customTemplate };
                var styleTemplate = new ControlTemplate(typeof(MenuItem))
                {
                    VisualTree = new FrameworkElementFactory(typeof(Border))
                };
                var customStyle = new Style(typeof(MenuItem));
                customStyle.Setters.Add(new Setter(Control.TemplateProperty, styleTemplate));
                var styledAction = new MenuItem { Header = "Rename object...", Style = customStyle };
                var compare = new MenuItem { Header = "Compare with another package" };
                var headerInfo = new MenuItem { Header = "Package file header info" };
                var bulkEditing = new MenuItem { Header = "Bulk Property Editing" };

                var late = new MenuItem { Header = "Clone Tree" };
                string openingHeader = "Rename object...";
                var targets = new ObservableCollection<string> { "Copy.pcc" };
                var export = new MenuItem { Header = "Export..." };
                export.ItemContainerStyle = new Style(typeof(MenuItem));
                export.ItemContainerStyle.Setters.Add(new Setter(HeaderedItemsControl.HeaderProperty, new Binding()));
                tools.SubmenuOpened += (_, e) =>
                {
                    if (!ReferenceEquals(e.OriginalSource, tools))
                        return;
                    late.Header = openingHeader;
                    // Several tools assign their file targets only when the menu opens.
                    export.ItemsSource ??= targets;
                };
                foreach (MenuItem action in new[] { clone, explicitTool, boundTool, customAction, styledAction, compare, headerInfo, bulkEditing, late, export })
                    tools.Items.Add(action);

                try
                {
                    window.Show();
                    FlushDispatcher();
                    AssertToolbarCaptions(file, tools, toolbarAction);
                    ControlTemplate toolbarTemplate = tools.Template;
                    Popup toolbarPopup = OpenToolbarPopup(tools);

                    AssertToolbarCaptions(file, tools, toolbarAction);
                    Assert.AreSame(toolbarTemplate, tools.Template, theme);
                    Assert.AreEqual(PlacementMode.Bottom, toolbarPopup.Placement, theme);

                    AssertIcon(clone, EFontAwesomeIcon.Solid_Clone);
                    AssertIcon(late, EFontAwesomeIcon.Solid_PencilAlt);
                    AssertIcon(export, EFontAwesomeIcon.Solid_FileExport);
                    AssertIcon(customAction, EFontAwesomeIcon.Solid_Search);
                    AssertIcon(styledAction, EFontAwesomeIcon.Solid_PencilAlt);
                    AssertIcon(compare, EFontAwesomeIcon.Solid_NotEqual);
                    AssertIcon(headerInfo, EFontAwesomeIcon.Solid_InfoCircle);
                    AssertIcon(bulkEditing, EFontAwesomeIcon.Solid_SlidersH);
                    Assert.AreSame(explicitIcon, explicitTool.Icon, theme);
                    Assert.AreSame(boundIcon, boundTool.Icon, theme);
                    Assert.IsTrue(BindingOperations.IsDataBound(boundTool, MenuItem.IconProperty), theme);
                    Assert.AreSame(customTemplate, customAction.Template, theme);
                    Assert.AreSame(styleTemplate, styledAction.Template, theme);
                    Assert.AreSame(command, clone.Command, theme);
                    Assert.AreSame(commandParameter, clone.CommandParameter, theme);
                    Assert.AreEqual("Ctrl+D", clone.InputGestureText, theme);
                    Assert.IsTrue(toolbarPopup.IsOpen, $"{theme}: The Tools popup must remain realized during its action checks.");

                    AssertCheckedActionTemplate(clone, headerTemplate, theme);
                    clone.IsEnabled = false;
                    FlushDispatcher();
                    AssertCheckedActionTemplate(clone, headerTemplate, theme);
                    var iconHost = (ContentPresenter)clone.Template.FindName("ActionIcon", clone);
                    Assert.IsTrue(iconHost.Opacity < 1, theme);
                    Assert.AreEqual(((SolidColorBrush)clone.FindResource(SystemColors.GrayTextBrushKey)).Color,
                        ((SolidColorBrush)clone.Foreground).Color, theme);

                    Assert.IsTrue(toolbarPopup.IsOpen, $"{theme}: The Tools popup must remain realized during its disabled-action checks.");
                    Assert.IsTrue(export.IsLoaded, $"{theme}: The Export submenu header must be loaded in the Tools popup.");
                    Assert.AreSame(targets, export.ItemsSource, theme);
                    Assert.AreEqual(1, export.Items.Count, $"{theme}: Opening Tools must assign its generated file targets.");
                    export.ApplyTemplate();
                    var nestedPopup = (Popup)export.Template.FindName("PART_Popup", export);
                    Assert.IsNotNull(nestedPopup, theme);
                    nestedPopup.Child.Opacity = 0;
                    export.IsSubmenuOpen = true;
                    FlushDispatcher();
                    Assert.AreEqual(PlacementMode.Right, nestedPopup.Placement, theme);
                    Assert.IsTrue(toolbarPopup.IsOpen, $"{theme}: The parent popup must stay realized when Export opens.");
                    Assert.IsTrue(export.IsSubmenuOpen, $"{theme}: Export must remain open to realize its file targets.");
                    Assert.IsTrue(nestedPopup.IsOpen, $"{theme}: The nested popup must follow Export.IsSubmenuOpen.");
                    AssertIcon(WaitForGeneratedIcon(export, 0), EFontAwesomeIcon.Solid_FileExport);
                    targets.Add("BIOA_Test.pcc");
                    AssertIcon(WaitForGeneratedIcon(export, 1), EFontAwesomeIcon.Solid_FileExport);

                    export.IsSubmenuOpen = false;
                    CloseToolbarPopup(tools);
                    FlushDispatcher();
                    openingHeader = "Change Links...";
                    OpenToolbarPopup(tools);
                    AssertIcon(late, EFontAwesomeIcon.Solid_Link);
                    Assert.AreSame(explicitIcon, explicitTool.Icon, theme);
                    Assert.AreSame(boundIcon, boundTool.Icon, theme);
                    AssertToolbarCaptions(file, tools, toolbarAction);
                    CloseToolbarPopup(tools);
                    FlushDispatcher();
                    OpenToolbarPopup(file);
                    AssertIcon(save, EFontAwesomeIcon.Solid_Save);
                    Assert.IsNull(embeddedPicker.Icon, "An embedded picker is a control, rather than an action row.");
                    Assert.AreSame(pickerTemplate, embeddedPicker.Template, theme);
                    Assert.AreSame(pickerControl, embeddedPicker.Header, theme);
                }
                finally
                {
                    export.IsSubmenuOpen = false;
                    CloseToolbarPopup(tools);
                    CloseToolbarPopup(file);
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

    private static Popup OpenToolbarPopup(MenuItem header)
    {
        header.ApplyTemplate();
        var popup = (Popup)header.Template.FindName("PART_Popup", header);
        Assert.IsNotNull(popup, $"{header.Header} must keep its native toolbar popup.");
        // A native Menu attempts mouse capture and immediately closes in this offscreen
        // test host, even with decoration disabled. Realize its actual popup directly
        // so these checks exercise menu ownership, layout, opening handlers and containers.
        popup.Child.Opacity = 0;
        // Replace the native template's two-way IsSubmenuOpen binding so opening
        // this fixture popup cannot reenter the window's native menu capture mode.
        BindingOperations.SetBinding(popup, Popup.IsOpenProperty, new Binding { Source = true, Mode = BindingMode.OneWay });
        header.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, header));
        FlushDispatcher();
        // Popup coerces IsOpen until its template subtree receives the render-time Loaded broadcast.
        var frame = new DispatcherFrame();
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (popup.IsOpen || DateTime.UtcNow >= deadline)
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
        Assert.IsTrue(popup.IsOpen,
            $"Header={header.Header}, HeaderLoaded={header.IsLoaded}, PopupLoaded={popup.IsLoaded}, " +
            $"RequestedOpen={popup.ReadLocalValue(Popup.IsOpenProperty)}, Items={header.Items.Count}");
        return popup;
    }

    private static void CloseToolbarPopup(MenuItem header)
    {
        if (header.Template?.FindName("PART_Popup", header) is Popup popup)
            BindingOperations.SetBinding(popup, Popup.IsOpenProperty, new Binding { Source = false, Mode = BindingMode.OneWay });
    }

    private static void AssertToolbarCaptions(params MenuItem[] captions)
    {
        foreach (MenuItem caption in captions)
            Assert.IsNull(caption.Icon, $"The toolbar caption '{caption.Header}' should keep its original appearance.");
    }

    private static void AssertCheckedActionTemplate(MenuItem item, DataTemplate headerTemplate, string theme)
    {
        Assert.IsTrue(item.IsCheckable, theme);
        Assert.IsTrue(item.IsChecked, theme);
        item.ApplyTemplate();
        var iconHost = (ContentPresenter)item.Template.FindName("ActionIcon", item);
        var checkmarkHost = (Border)item.Template.FindName("CheckmarkHost", item);
        var checkmark = (Path)item.Template.FindName("Checkmark", item);
        var headerHost = (ContentPresenter)item.Template.FindName("HeaderHost", item);
        Assert.IsNotNull(iconHost, theme);
        Assert.IsNotNull(checkmarkHost, theme);
        Assert.IsNotNull(checkmark, theme);
        Assert.IsNotNull(headerHost, theme);
        Assert.AreEqual(Visibility.Visible, iconHost.Visibility, theme);
        Assert.AreEqual(Visibility.Visible, checkmarkHost.Visibility, theme);
        Assert.AreEqual(Visibility.Visible, checkmark.Visibility, theme);
        Assert.AreNotEqual(Grid.GetColumn(iconHost), Grid.GetColumn(checkmarkHost), theme);
        Assert.AreSame(item.Icon, iconHost.Content, theme);
        Assert.AreSame(headerTemplate, headerHost.ContentTemplate, theme);
        Assert.AreSame(item.Foreground, ((ImageAwesome)item.Icon).Foreground, theme);
        Assert.AreSame(item.Foreground, checkmark.Fill, theme);
    }

    private static ResourceDictionary LoadResources(string path) =>
        (ResourceDictionary)Application.LoadComponent(new Uri($"/LegendaryExplorer;component/{path}", UriKind.Relative));

    private static void AssertIcon(MenuItem item, EFontAwesomeIcon expected)
    {
        Assert.IsNotNull(item, "The generated file target must be realized when the submenu opens.");
        Assert.IsInstanceOfType<ImageAwesome>(item.Icon, $"Header={item.Header}, IsLoaded={item.IsLoaded}");
        Assert.AreEqual(expected, ((ImageAwesome)item.Icon).Icon, item.Header?.ToString());
    }

    private static MenuItem WaitForGeneratedIcon(MenuItem parent, int index)
    {
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
        Assert.IsNotNull(container,
            $"Header={parent.Header}, IsLoaded={parent.IsLoaded}, IsVisible={parent.IsVisible}, " +
            $"IsSubmenuOpen={parent.IsSubmenuOpen}, HasItems={parent.HasItems}, Items={parent.Items.Count}, " +
            $"Generator={parent.ItemContainerGenerator.Status}, Index={index}, " +
            $"Owner={ItemsControl.ItemsControlFromItemContainer(parent)?.GetType().Name}");
        return container;
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
