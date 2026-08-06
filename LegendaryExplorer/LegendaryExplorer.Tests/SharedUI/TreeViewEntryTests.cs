using System.Collections.Generic;
using LegendaryExplorer.SharedUI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.SharedUI;

[TestClass]
public class TreeViewEntryTests
{
    [TestMethod]
    public void FlattenTreeReturnsDepthFirstSublinkOrder()
    {
        var root = new TreeViewEntry(null, "root");
        var first = new TreeViewEntry(null, "first") { Parent = root };
        var second = new TreeViewEntry(null, "second") { Parent = root };
        var firstChild = new TreeViewEntry(null, "first child") { Parent = first };
        var firstGrandchild = new TreeViewEntry(null, "first grandchild") { Parent = firstChild };
        var secondChild = new TreeViewEntry(null, "second child") { Parent = second };

        root.Sublinks.Add(first);
        root.Sublinks.Add(second);
        first.Sublinks.Add(firstChild);
        firstChild.Sublinks.Add(firstGrandchild);
        second.Sublinks.Add(secondChild);

        List<TreeViewEntry> flattened = root.FlattenTree();

        CollectionAssert.AreEqual(
            new[] { root, first, firstChild, firstGrandchild, second, secondChild },
            flattened);
    }
}
