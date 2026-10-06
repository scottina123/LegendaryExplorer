using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using FontAwesome5;
using LegendaryExplorer.SharedUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
[DoNotParallelize]
public class MenuIconParityTests
{
    [STATestMethod]
    public void DropdownAndContextActionsUseCanonicalIconsForStaticDynamicAndTargetActionsInEveryTheme()
    {
        WithThemeResources(theme =>
        {
            DropdownAndContextActionsUseTheirCanonicalImageResourcesInEveryTheme(theme);
            DynamicActionsCanChangeBetweenGlyphsAndImagesWhileExplicitAndBoundIconsStayIntact(theme);
            TargetSubmenusInheritCanonicalImagesWhileRecognizedActionsAndOverridesKeepTheirIcons(theme);
        });
    }

    private static void DropdownAndContextActionsUseTheirCanonicalImageResourcesInEveryTheme(string theme)
    {
        var dropdown = new Menu();
        var tools = new MenuItem { Header = "Tools" };
        dropdown.Items.Add(tools);
        var contextMenu = new ContextMenu();
        var pairs = new List<(MenuItem Dropdown, MenuItem Context, string Resource)>();

        foreach ((string caption, string resource) in CanonicalActions)
        {
            var dropdownAction = new MenuItem { Header = caption };
            var contextAction = new MenuItem { Header = caption };
            tools.Items.Add(dropdownAction);
            contextMenu.Items.Add(contextAction);
            pairs.Add((dropdownAction, contextAction, resource));
        }

        ContextMenuIcons.PopulateMenu(dropdown);
        ContextMenuIcons.PopulateMenu(contextMenu);
        Assert.IsNull(tools.Icon, $"{theme}: The toolbar caption must keep its own layout.");

        var generatedImages = new HashSet<Image>();
        var initialIcons = new List<(MenuItem Item, Image Icon)>();
        foreach ((MenuItem dropdownAction, MenuItem contextAction, string resource) in pairs)
        {
            Image dropdownIcon = AssertCanonicalImage(dropdownAction, resource, theme);
            Image contextIcon = AssertCanonicalImage(contextAction, resource, theme);
            Assert.IsTrue(generatedImages.Add(dropdownIcon),
                $"{theme}: '{dropdownAction.Header}' needs its own dropdown icon control.");
            Assert.IsTrue(generatedImages.Add(contextIcon),
                $"{theme}: '{contextAction.Header}' needs its own context-menu icon control.");
            Assert.AreSame(dropdownIcon.Source, contextIcon.Source, theme);
            initialIcons.Add((dropdownAction, dropdownIcon));
            initialIcons.Add((contextAction, contextIcon));
        }

        ContextMenuIcons.PopulateMenu(dropdown);
        ContextMenuIcons.PopulateMenu(contextMenu);
        foreach ((MenuItem item, Image icon) in initialIcons)
        {
            Assert.AreSame(icon, item.Icon,
                $"{theme}: Reopening a menu should reuse the generated image for '{item.Header}'.");
        }
    }

    private static void DynamicActionsCanChangeBetweenGlyphsAndImagesWhileExplicitAndBoundIconsStayIntact(string theme)
    {
        var dropdown = new Menu();
        var tools = new MenuItem { Header = "Tools" };
        dropdown.Items.Add(tools);
        var contextMenu = new ContextMenu();
        foreach (ItemsControl menu in new ItemsControl[] { tools, contextMenu })
        {
            var dynamicAction = new MenuItem { Header = "Copy value" };
            var explicitIcon = (Image)Application.Current.FindResource("PackageEditorMenuIcon");
            var explicitAction = new MenuItem { Header = "Open in Package Editor", Icon = explicitIcon };
            var boundIcon = (Image)Application.Current.FindResource("MeshplorerMenuIcon");
            var boundAction = new MenuItem { Header = "Open in Mesh Explorer" };
            boundAction.SetBinding(MenuItem.IconProperty, new Binding("Icon")
            {
                Source = new { Icon = boundIcon }
            });
            menu.Items.Add(dynamicAction);
            menu.Items.Add(explicitAction);
            menu.Items.Add(boundAction);

            ContextMenuIcons.PopulateMenu(menu);
            AssertGlyph(dynamicAction, EFontAwesomeIcon.Solid_Copy, theme);

            dynamicAction.Header = "Export Mesh to UDK";
            ContextMenuIcons.PopulateMenu(menu);
            Image generatedImage = AssertCanonicalImage(dynamicAction, "UDKMenuIcon", theme);
            ContextMenuIcons.PopulateMenu(menu);
            Assert.AreSame(generatedImage, dynamicAction.Icon, theme);

            dynamicAction.Header = "Rename object...";
            ContextMenuIcons.PopulateMenu(menu);
            AssertGlyph(dynamicAction, EFontAwesomeIcon.Solid_PencilAlt, theme);
            object generatedGlyph = dynamicAction.Icon;
            ContextMenuIcons.PopulateMenu(menu);
            Assert.AreSame(generatedGlyph, dynamicAction.Icon, theme);

            // A changed destination must only update icons owned by the decorator.
            explicitAction.Header = "Open in Sequence Editor";
            boundAction.Header = "Export Mesh to UDK";
            ContextMenuIcons.PopulateMenu(menu);
            Assert.AreSame(explicitIcon, explicitAction.Icon, theme);
            Assert.AreSame(boundIcon, boundAction.Icon, theme);
            Assert.IsTrue(BindingOperations.IsDataBound(boundAction, MenuItem.IconProperty), theme);
        }
    }

    private static void TargetSubmenusInheritCanonicalImagesWhileRecognizedActionsAndOverridesKeepTheirIcons(string theme)
    {
        var dropdown = new Menu();
        var tools = new MenuItem { Header = "Tools" };
        dropdown.Items.Add(tools);
        var contextMenu = new ContextMenu();
        foreach (ItemsControl menu in new ItemsControl[] { tools, contextMenu })
        {
            foreach ((string caption, string target, string resource) in new[]
                     {
                         ("Open file location", "Copy.pcc", "WindowsExplorerMenuIcon"),
                         ("Open sequence reference in Sequence Editor",
                             "BioD_Test.TheWorld.PersistentLevel.Main_Sequence.SeqRef_Default", "SequenceEditorMenuIcon")
                     })
            {
                var parent = new MenuItem { Header = caption };
                var targetItem = new MenuItem { Header = target };
                var copyAction = new MenuItem { Header = "Copy value" };
                var overrideIcon = (Image)Application.Current.FindResource("MeshplorerMenuIcon");
                var overriddenTarget = new MenuItem { Header = "Another target", Icon = overrideIcon };
                parent.Items.Add(targetItem);
                parent.Items.Add(copyAction);
                parent.Items.Add(overriddenTarget);
                menu.Items.Add(parent);

                ContextMenuIcons.PopulateMenu(menu);
                Image parentIcon = AssertCanonicalImage(parent, resource, theme);
                Image targetIcon = AssertCanonicalImage(targetItem, resource, theme);
                Assert.AreNotSame(parentIcon, targetIcon,
                    $"{theme}: Parent actions and their targets must own separate image controls.");
                AssertGlyph(copyAction, EFontAwesomeIcon.Solid_Copy, theme);
                Assert.AreSame(overrideIcon, overriddenTarget.Icon, theme);

                // Containers are also decorated individually when a target submenu opens.
                ContextMenuIcons.PopulateMenu(parent);
                Assert.AreSame(targetIcon, targetItem.Icon, theme);
                AssertGlyph(copyAction, EFontAwesomeIcon.Solid_Copy, theme);
                Assert.AreSame(overrideIcon, overriddenTarget.Icon, theme);
            }
        }
    }

    private static readonly (string Caption, string Resource)[] CanonicalActions =
    {
        ("Export Mesh to UDK", "UDKMenuIcon"),
        ("Import New Mesh from UDK", "UDKMenuIcon"),
        ("Replace single LOD from UDK", "UDKMenuIcon"),
        ("Export Mesh to PSK with UModel", "UModelMenuIcon"),
        ("Import from Excel", "ExcelMenuIcon"),
        ("Export to Excel", "ExcelMenuIcon"),
        ("Open file location", "WindowsExplorerMenuIcon"),
        ("Open local database folder", "WindowsExplorerMenuIcon"),
        ("Open in Windows Explorer", "WindowsExplorerMenuIcon"),
        ("Package Editor", "PackageEditorMenuIcon"),
        ("Open Referenced Object in Package Editor", "PackageEditorMenuIcon"),
        ("Sequence Editor", "SequenceEditorMenuIcon"),
        ("Open in Mesh Explorer", "MeshplorerMenuIcon"),
        ("Open in Dialogue Editor", "DialogueEditorMenuIcon"),
        ("Open in Interp Editor", "InterpEditorMenuIcon"),
        ("Open in FaceFX Editor", "FaceFXEditorMenuIcon"),
        ("Open Referenced State Event in Plot Editor", "PlotEditorMenuIcon"),
        ("Open Conditional in Conditionals Editor", "ConditionalsEditorMenuIcon"),
        ("Open Male Audio in Soundplorer", "SoundplorerMenuIcon"),
        ("Open in Pathfinding Editor", "PathfindingEditorMenuIcon"),
        ("Open in WwiseEditor", "WwiseEditorMenuIcon"),
        ("Open in Level Editor", "LevelEditorMenuIcon"),
        ("Open in SFXGalaxy Editor (LE3)", "SFXGalaxyEditorMenuIcon"),
        ("Open Gesture Animation Importer", "AnimationImporterMenuIcon"),
        ("Gesture Animation Importer", "AnimationImporterMenuIcon"),
        ("Open in Animation Viewer", "AnimViewerMenuIcon"),
        ("Coalesced Editor", "CoalescedEditorMenuIcon"),
        ("Hex Converter", "HexConverterMenuIcon"),
        ("Open in TLK Editor", "TLKEditorMenuIcon"),
        ("Open in Asset Viewer", "AssetViewerMenuIcon")
    };

    private static Image AssertCanonicalImage(MenuItem item, string resource, string theme)
    {
        string diagnostic = $"{theme}: '{item.Header}' must use {resource}.";
        Assert.IsInstanceOfType<Image>(item.Icon, diagnostic);
        var actual = (Image)item.Icon;
        var canonical = (Image)Application.Current.FindResource(resource);
        Assert.AreNotSame(canonical, actual, diagnostic);
        Assert.IsInstanceOfType<BitmapImage>(actual.Source, diagnostic);
        Assert.IsInstanceOfType<BitmapImage>(canonical.Source, diagnostic);
        Assert.AreEqual(((BitmapImage)canonical.Source).UriSource, ((BitmapImage)actual.Source).UriSource, diagnostic);
        Assert.AreSame(canonical.Source, actual.Source, diagnostic);
        Assert.AreEqual(16d, actual.Width, diagnostic);
        Assert.AreEqual(16d, actual.Height, diagnostic);
        return actual;
    }

    private static void AssertGlyph(MenuItem item, EFontAwesomeIcon expected, string theme)
    {
        Assert.IsInstanceOfType<ImageAwesome>(item.Icon, $"{theme}: {item.Header}");
        Assert.AreEqual(expected, ((ImageAwesome)item.Icon).Icon, theme);
    }

    private static void WithThemeResources(Action<string> verify)
    {
        FieldInfo resourceAssembly = typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!;
        object previousAssembly = resourceAssembly.GetValue(null);
        resourceAssembly.SetValue(null, typeof(App).Assembly);
        _ = Application.Current ?? new Application();
        ResourceDictionary previousResources = Application.Current.Resources;
        ShutdownMode previousShutdownMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            foreach (string theme in new[] { "LightTheme.xaml", "DarkTheme.xaml", "ModernDarkTheme.xaml" })
            {
                Application.Current.Resources = LoadResources("AppResources.xaml");
                if (theme != "LightTheme.xaml")
                    Application.Current.Resources.MergedDictionaries.Add(LoadResources(theme));
                verify(theme);
            }
        }
        finally
        {
            Application.Current.Resources = previousResources;
            Application.Current.ShutdownMode = previousShutdownMode;
            resourceAssembly.SetValue(null, previousAssembly);
        }
    }

    private static ResourceDictionary LoadResources(string path) =>
        (ResourceDictionary)Application.LoadComponent(new Uri($"/LegendaryExplorer;component/{path}", UriKind.Relative));
}
