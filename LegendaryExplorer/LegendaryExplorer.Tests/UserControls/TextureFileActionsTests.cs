using System.Reflection;
using LegendaryExplorer.UserControls.ExportLoaderControls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class TextureFileActionsTests
{
    [TestMethod]
    [DataRow(512, 512, 256, 256, true)]
    [DataRow(1024, 512, 512, 256, true)]
    [DataRow(512, 1024, 256, 1024, false)]
    [DataRow(1024, 512, 1024, 256, false)]
    [DataRow(0, 512, 512, 512, false)]
    [DataRow(512, 0, 512, 512, false)]
    [DataRow(512, 512, 512, 0, false)]
    [DataRow(2147483647, 2147483647, 2147483647, 2147483647, true)]
    public void TextureReplacementRequiresMatchingAspectRatio(int width, int height, int originalWidth, int originalHeight, bool expected)
    {
        var method = typeof(TextureViewerExportLoader).Assembly
            .GetType("LegendaryExplorer.UserControls.ExportLoaderControls.TextureViewer.TextureFileActions")!
            .GetMethod("HasSameAspectRatio", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.AreEqual(expected, method.Invoke(null, [width, height, originalWidth, originalHeight]));
    }
}
