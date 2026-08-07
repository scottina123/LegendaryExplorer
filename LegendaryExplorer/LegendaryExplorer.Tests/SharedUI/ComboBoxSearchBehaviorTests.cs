using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LegendaryExplorer.SharedUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Xceed.Wpf.Toolkit;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
public class ComboBoxSearchBehaviorTests
{
    [STATestMethod]
    public void DropdownSearchFiltersRestoresAndSupportsCheckComboBoxItems()
    {
        EnsureApplicationResources();

        var comboBox = new ComboBox
        {
            ItemsSource = new[] { "Alpha", "Beta", "Gamma", "Delta" },
            Width = 180
        };
        comboBox.Items.Filter = item => !Equals(item, "Delta");
        var checkComboBox = new CheckComboBox
        {
            ItemsSource = new[] { "Public", "Standalone", "Transactional" },
            Width = 180
        };
        var toolbarComboBox = new ComboBox
        {
            ItemsSource = new[] { "One", "Two", "Three" },
            Width = 180
        };
        var watermarkComboBox = new WatermarkComboBox
        {
            ItemsSource = new[] { "Paragon", "Renegade" },
            Width = 180,
            Watermark = "Alignment"
        };
        var propertyGridComboBox = new PropertyGridEditorComboBox
        {
            ItemsSource = new[] { "Default", "Custom" },
            Width = 180
        };
        var toolbar = new ToolBar { Items = { toolbarComboBox } };
        var content = new StackPanel { Children = { comboBox, checkComboBox, toolbar, watermarkComboBox, propertyGridComboBox } };

        var window = CreateTestWindow(content);
        try
        {
            window.Show();

            comboBox.IsDropDownOpen = true;
            FlushDispatcher();

            Assert.IsTrue(comboBox.IsLoaded, "The test dropdown should be loaded.");
            Popup popup = GetPopup(comboBox);
            Assert.IsNotNull(popup, "The dropdown template should contain a popup.");
            TextBox searchBox = FindSearchBox(popup);
            Assert.IsNotNull(searchBox, "The dropdown should contain a filter box.");

            searchBox.Text = "mm";
            FlushDispatcher();
            CollectionAssert.AreEqual(new[] { "Gamma" }, comboBox.Items.Cast<string>().ToArray());

            comboBox.IsDropDownOpen = false;
            FlushDispatcher();
            CollectionAssert.AreEqual(new[] { "Alpha", "Beta", "Gamma" }, comboBox.Items.Cast<string>().ToArray(),
                "Closing the dropdown should restore its original filter.");

            checkComboBox.IsDropDownOpen = true;
            FlushDispatcher();
            searchBox = FindSearchBox(GetPopup(checkComboBox));
            Assert.IsNotNull(searchBox, "The checkable dropdown should contain a filter box.");

            searchBox.Text = "stand";
            FlushDispatcher();
            CollectionAssert.AreEqual(new[] { "Standalone" }, checkComboBox.Items.Cast<string>().ToArray());

            checkComboBox.IsDropDownOpen = false;
            toolbarComboBox.IsDropDownOpen = true;
            FlushDispatcher();
            Assert.IsNotNull(FindSearchBox(GetPopup(toolbarComboBox)),
                "A ComboBox using the toolbar style should contain a filter box.");

            toolbarComboBox.IsDropDownOpen = false;
            watermarkComboBox.IsDropDownOpen = true;
            FlushDispatcher();
            Assert.IsNotNull(FindSearchBox(GetPopup(watermarkComboBox)),
                "A watermark dropdown should contain a filter box.");

            watermarkComboBox.IsDropDownOpen = false;
            propertyGridComboBox.IsDropDownOpen = true;
            FlushDispatcher();
            Assert.IsNotNull(FindSearchBox(GetPopup(propertyGridComboBox)),
                "A property-grid dropdown should contain a filter box.");

            propertyGridComboBox.IsDropDownOpen = false;
            Application.Current.Resources.MergedDictionaries[0] = (ResourceDictionary)Application.LoadComponent(
                new Uri("/LegendaryExplorer;component/DarkTheme.xaml", UriKind.Relative));
            var darkThemeComboBox = new ComboBox
            {
                ItemsSource = new[] { "Mass Effect", "Mass Effect 2", "Mass Effect 3" },
                Width = 180
            };
            content.Children.Add(darkThemeComboBox);
            FlushDispatcher();
            darkThemeComboBox.IsDropDownOpen = true;
            FlushDispatcher();
            searchBox = FindSearchBox(GetPopup(darkThemeComboBox));
            Assert.IsNotNull(searchBox, "A dark-theme dropdown should contain a filter box.");

            searchBox.Text = "3";
            FlushDispatcher();
            CollectionAssert.AreEqual(new[] { "Mass Effect 3" }, darkThemeComboBox.Items.Cast<string>().ToArray());
            darkThemeComboBox.IsDropDownOpen = false;
        }
        finally
        {
            watermarkComboBox.IsDropDownOpen = false;
            propertyGridComboBox.IsDropDownOpen = false;
            toolbarComboBox.IsDropDownOpen = false;
            checkComboBox.IsDropDownOpen = false;
            comboBox.IsDropDownOpen = false;
            window.Close();
        }
    }

    private static void EnsureApplicationResources()
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, typeof(App).Assembly);
        _ = Application.Current ?? new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/LegendaryExplorer;component/AppResources.xaml", UriKind.Relative));
    }

    private static Window CreateTestWindow(UIElement content) => new()
    {
        Content = content,
        Width = 240,
        Height = 210,
        Left = -10000,
        Top = -10000,
        ShowActivated = false,
        ShowInTaskbar = false
    };

    private static Popup GetPopup(Control control)
    {
        control.ApplyTemplate();
        return control.Template.FindName("PART_Popup", control) as Popup
               ?? control.Template.FindName("Popup", control) as Popup;
    }

    private static TextBox FindSearchBox(Popup popup) =>
        FindVisualDescendant<TextBox>(popup?.Child, textBox =>
            AutomationProperties.GetName(textBox) == "Filter dropdown items");

    private static T FindVisualDescendant<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result && predicate(result))
            {
                return result;
            }

            result = FindVisualDescendant(child, predicate);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static void FlushDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
