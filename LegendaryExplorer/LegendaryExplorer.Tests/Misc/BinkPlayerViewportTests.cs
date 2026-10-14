using System.Drawing;
using LegendaryExplorer.Misc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Misc;

[TestClass]
public class BinkPlayerViewportTests
{
    [TestMethod]
    [DataRow(960, 540, 1920, 1080, 0, 0, 960, 540)]
    [DataRow(960, 800, 1920, 1080, 0, 130, 960, 540)]
    [DataRow(1400, 540, 1920, 1080, 220, 0, 960, 540)]
    [DataRow(800, 600, 720, 1280, 231, 0, 337, 600)]
    [DataRow(101, 101, 1920, 1080, 0, 22, 101, 56)]
    [DataRow(1, 10, 1920, 1080, 0, 4, 1, 1)]
    [DataRow(10, 1, 720, 1280, 4, 0, 1, 1)]
    public void CalculateVideoBounds_FitsAndCentersDifferentAspectRatios(
        int clientWidth, int clientHeight, int movieWidth, int movieHeight,
        int x, int y, int width, int height)
    {
        Rectangle bounds = BinkPlayerViewport.CalculateVideoBounds(
            new Size(clientWidth, clientHeight), new Size(movieWidth, movieHeight));

        Assert.AreEqual(new Rectangle(x, y, width, height), bounds);
    }

    [TestMethod]
    public void CalculateSliderBounds_UsesVideoBottomInsideLetterboxedClient()
    {
        Rectangle bounds = BinkPlayerViewport.CalculateSliderBounds(new Size(960, 800), new Size(1920, 1080));

        Assert.AreEqual(new Rectangle(0, 654, 960, 16), bounds);
        Assert.IsTrue(bounds.Bottom < 800, "The native slider is inside the video, above the lower letterbox.");
    }

    [TestMethod]
    public void CalculateSliderBounds_UsesPillarboxedVideoWidth()
    {
        Rectangle bounds = BinkPlayerViewport.CalculateSliderBounds(new Size(1400, 540), new Size(1920, 1080));

        Assert.AreEqual(new Rectangle(220, 524, 960, 16), bounds);
    }

    [TestMethod]
    public void CalculateSliderBounds_ClampsMaskToTinyVideoHeight()
    {
        Rectangle bounds = BinkPlayerViewport.CalculateSliderBounds(new Size(16, 9), new Size(1920, 1080));

        Assert.AreEqual(new Rectangle(0, 0, 16, 9), bounds);
    }

    [TestMethod]
    [DataRow(0, 540, 1920, 1080)]
    [DataRow(960, 0, 1920, 1080)]
    [DataRow(960, 540, 0, 1080)]
    [DataRow(960, 540, 1920, 0)]
    [DataRow(-1, 540, 1920, 1080)]
    public void CalculateBounds_ReturnsEmptyWhenDimensionsAreUnavailable(
        int clientWidth, int clientHeight, int movieWidth, int movieHeight)
    {
        var clientSize = new Size(clientWidth, clientHeight);
        var movieSize = new Size(movieWidth, movieHeight);

        Assert.AreEqual(Rectangle.Empty, BinkPlayerViewport.CalculateVideoBounds(clientSize, movieSize));
        Assert.AreEqual(Rectangle.Empty, BinkPlayerViewport.CalculateSliderBounds(clientSize, movieSize));
    }

    [TestMethod]
    public void CalculateVideoBounds_DoesNotOverflowLargeDimensions()
    {
        Rectangle bounds = BinkPlayerViewport.CalculateVideoBounds(
            new Size(100_000, 100_000), new Size(100_000, 50_000));

        Assert.AreEqual(new Rectangle(0, 25_000, 100_000, 50_000), bounds);
    }

    [TestMethod]
    [DataRow(640, 480, 0.75, 479, 478)]
    [DataRow(800, 450, 0.25, 200, 448)]
    [DataRow(400, 600, 0.75, 299, 598)]
    [DataRow(400, 2000, 0.75, 299, 2012)]
    public void CalculateSeekPoint_PassesNativeHitTestIncludingTallClients(
        int clientWidth, int clientHeight, double position, int x, int y)
    {
        Point point = BinkPlayerViewport.CalculateSeekPoint(
            new Size(clientWidth, clientHeight), new Size(1920, 1080), position);

        Assert.AreEqual(new Point(x, y), point);
    }

    [TestMethod]
    [DataRow(-1.0, 227)]
    [DataRow(0.0, 227)]
    [DataRow(1.0, 1172)]
    [DataRow(2.0, 1172)]
    public void CalculateSeekPoint_ClampsPositionInsideNativeSliderHitArea(double position, int expectedX)
    {
        Point point = BinkPlayerViewport.CalculateSeekPoint(
            new Size(1400, 540), new Size(1920, 1080), position);

        Assert.AreEqual(expectedX, point.X);
    }

    [TestMethod]
    [DataRow(0.0, 9)]
    [DataRow(1.0, 790)]
    public void CalculateSeekPoint_EndpointsDoNotTogglePlayback(double position, int expectedX)
    {
        Point point = BinkPlayerViewport.CalculateSeekPoint(
            new Size(800, 450), new Size(1920, 1080), position);

        Assert.AreEqual(new Point(expectedX, 448), point);
    }

    [TestMethod]
    [DataRow(1, 0.0, 0)]
    [DataRow(1, 1.0, 0)]
    [DataRow(2, 1.0, 0)]
    public void CalculateSeekPoint_DoesNotCreateInvertedBoundsInTinyClients(int clientWidth, double position, int expectedX)
    {
        Point point = BinkPlayerViewport.CalculateSeekPoint(
            new Size(clientWidth, 1), new Size(1920, 1080), position);

        Assert.AreEqual(expectedX, point.X);
    }

    [TestMethod]
    public void CalculateSeekPoint_ClampsCoordinatesToSignedWindowMessageRange()
    {
        Point point = BinkPlayerViewport.CalculateSeekPoint(
            new Size(100_000, 100_000), new Size(1920, 1080), 0.75);

        Assert.AreEqual(new Point(short.MaxValue, short.MaxValue), point);
    }

    [TestMethod]
    public void CalculateSeekPoint_ReturnsEmptyWhenDimensionsAreUnavailable()
    {
        Assert.AreEqual(Point.Empty,
            BinkPlayerViewport.CalculateSeekPoint(new Size(640, 480), Size.Empty, 0.75));
    }
}
