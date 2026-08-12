using LegendaryExplorer.Tools.LevelEditor.Scene3D;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.LevelEditor;

[TestClass]
public class SceneViewportTests
{
    [TestMethod]
    public void SixteenByNineViewportPillarboxesWideHost()
    {
        var viewport = MeshRenderContext.CalculateFittedViewport(2560, 1080, 16f / 9f);

        Assert.AreEqual(320f, viewport.X, 0.001f);
        Assert.AreEqual(0f, viewport.Y, 0.001f);
        Assert.AreEqual(1920f, viewport.Width, 0.001f);
        Assert.AreEqual(1080f, viewport.Height, 0.001f);
    }

    [TestMethod]
    public void SixteenByNineViewportLetterboxesTallHost()
    {
        var viewport = MeshRenderContext.CalculateFittedViewport(1280, 1024, 16f / 9f);

        Assert.AreEqual(0f, viewport.X, 0.001f);
        Assert.AreEqual(152f, viewport.Y, 0.001f);
        Assert.AreEqual(1280f, viewport.Width, 0.001f);
        Assert.AreEqual(720f, viewport.Height, 0.001f);
    }

    [TestMethod]
    public void UnconstrainedViewportUsesEntireHost()
    {
        var viewport = MeshRenderContext.CalculateFittedViewport(1000, 500, null);

        Assert.AreEqual(0f, viewport.X, 0.001f);
        Assert.AreEqual(0f, viewport.Y, 0.001f);
        Assert.AreEqual(1000f, viewport.Width, 0.001f);
        Assert.AreEqual(500f, viewport.Height, 0.001f);
    }
}
