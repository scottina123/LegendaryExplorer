using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ScaleformElementArtworkTests
{
    [STATestMethod]
    public void CreatedPanelsAndImagesRenderTheirActualShapeAndAlphaAfterSave()
    {
        var movie = Movie();
        var panel = movie.AddElementDefinition(new() { Kind = ScaleformElementKind.Panel, Name = "Panel", Width = 8, Height = 6, Color = 0x20A040FF });
        var image = movie.AddElementDefinition(new() { Kind = ScaleformElementKind.Image, Name = "Image", Width = 8, Height = 4,
            PixelWidth = 2, PixelHeight = 1, Pixels = [200, 100, 50, 128, 0, 255, 0, 255] });
        var reopened = ScaleformDocument.Parse(movie.Compile());
        var renderer = new ScaleformArtworkRenderer(reopened);
        var panelDrawing = renderer.RenderCharacter(panel.CharacterId);
        Assert.AreEqual(new Rect(0, 0, 8, 6), panelDrawing.Bounds);
        var panelPixel = Pixel(panelDrawing, 2, 2);
        CollectionAssert.AreEqual(new byte[] { 64, 160, 32, 255 }, panelPixel);
        var imageDrawing = renderer.RenderCharacter(image.CharacterId);
        Assert.AreEqual(new Rect(0, 0, 8, 4), imageDrawing.Bounds);
        var alphaPixel = Pixel(imageDrawing, 1, 1);
        Assert.IsTrue(Math.Abs(alphaPixel[3] - 128) < 2);
        Assert.IsTrue(Math.Abs(alphaPixel[2] - 100) < 3, "Embedded alpha pixels must remain premultiplied once, without darkening twice.");
        var greenPixel = Pixel(imageDrawing, 6, 1);
        Assert.IsTrue(greenPixel[1] > 245 && greenPixel[3] > 245);
        Assert.AreEqual(0, renderer.Warnings.Count);
    }

    private static byte[] Pixel(Drawing drawing, int x, int y)
    {
        var visual = new DrawingVisual(); using (var context = visual.RenderOpen()) context.DrawDrawing(drawing);
        var bitmap = new RenderTargetBitmap(10, 10, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0); return pixel;
    }
    private static ScaleformDocument Movie()
    {
        byte[] body = [8, 0, 0, 24, 1, 0, 0x40, 0, 0, 0];
        return ScaleformDocument.Parse("FWS"u8.ToArray().Concat(new byte[] { 8 }).Concat(BitConverter.GetBytes(body.Length + 8)).Concat(body).ToArray());
    }
}
