using System;
using System.Linq;
using LegendaryExplorer.MainWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.MainWindow;

[TestClass]
public class ToolMenuPagesTests
{
    [STATestMethod]
    public void PagesContainTwelveAlphabeticalToolsWithoutDuplicatesOrUnavailableEntries()
    {
        Tool[] tools = Enumerable.Range(0, 25).Reverse()
            .Select(i => new Tool { name = $"Tool {i:00}", open = () => { } }).ToArray();
        var pages = new ToolMenuPages(tools.Append(new Tool { name = "Coming soon" }));
        Assert.AreEqual(25, pages.Count);
        Assert.AreEqual(3, pages.PageCount);
        Assert.HasCount(12, pages.GetPage(0));
        Assert.HasCount(12, pages.GetPage(1));
        Assert.HasCount(1, pages.GetPage(2));
        CollectionAssert.AreEqual(tools.Reverse().ToArray(), Enumerable.Range(0, pages.PageCount)
            .SelectMany(pages.GetPage).ToArray());
        CollectionAssert.AreEqual(pages.GetPage(0).ToArray(), pages.GetPage(-1).ToArray());
        CollectionAssert.AreEqual(pages.GetPage(2).ToArray(), pages.GetPage(99).ToArray());
    }

    [STATestMethod]
    public void GalaxyMapEditorSortsUnderGAndBothNamesRemainSearchable()
    {
        var galaxy = new Tool { name = "SFXGalaxy Editor (LE2/LE3)", Abbreviation = "gme", open = () => { } };
        Tool[] tools = [new() { name = "Sequence Editor", open = () => { } }, galaxy,
            new() { name = "Hex Converter", open = () => { } }, new() { name = "FaceFX Editor", open = () => { } }];
        CollectionAssert.AreEqual(new[] { "FaceFX Editor", "Galaxy Map Editor (LE2/LE3)", "Hex Converter", "Sequence Editor" },
            new ToolMenuPages(tools).GetPage(0).Select(tool => tool.DisplayName).ToArray());
        foreach (string search in new[] { "Galaxy Map Editor", "SFXGalaxy", "GME", "  galaxy   map  " })
            CollectionAssert.AreEqual(new[] { galaxy }, new ToolMenuPages(tools, search).GetPage(0).ToArray());
    }

    [STATestMethod]
    public void SearchAcceptsAbbreviationsAndNamesAndEmptyResultsHaveSafeNavigation()
    {
        var package = new Tool { name = "Package Editor", Abbreviation = "pkg", open = () => { } };
        var plot = new Tool { name = "Plot Editor", Abbreviation = "plt", open = () => { } };
        Tool[] tools = [plot, package];
        foreach (string search in new[] { "PKG", "package editor" })
            CollectionAssert.AreEqual(new[] { package }, new ToolMenuPages(tools, search).GetPage(0).ToArray());
        var empty = new ToolMenuPages(tools, "zzzzzzzzzzzzzzzz");
        Assert.AreEqual(0, empty.Count);
        Assert.AreEqual(1, empty.PageCount);
        Assert.HasCount(0, empty.GetPage(99));
    }
}
