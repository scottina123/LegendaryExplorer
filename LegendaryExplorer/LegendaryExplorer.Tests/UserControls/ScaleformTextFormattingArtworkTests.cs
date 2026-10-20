using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LegendaryExplorer.UserControls.ExportLoaderControls.Scaleform;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.UserControls;

[TestClass]
public class ScaleformTextFormattingArtworkTests
{
    [STATestMethod]
    public void PreviewUsesTextColorSizeAndFont()
    {
        var document = Movie("WWWW", 360, 160, 24);
        var tag = document.Tags.Single(t => t.Code == 37);
        var original = Render(document);
        tag.SetTextFormat(tag.GetTextFormat() with { FontName = "Arial", FontSize = 40, Color = 0xF02040FF });
        var larger = Render(document);
        Assert.IsTrue(larger.Bounds.Height > original.Bounds.Height + 8, "A larger font must change the actual preview glyph size.");
        Assert.IsTrue(larger.Red > larger.Green * 4, "The requested color must reach the rasterized glyphs.");
        Assert.IsTrue(larger.Red > larger.Blue * 2);

        tag.SetTextFormat(tag.GetTextFormat() with { FontName = "Courier New" });
        var monospaced = Render(document);
        Assert.IsTrue(Math.Abs(monospaced.Bounds.Width - larger.Bounds.Width) > 20,
            "Changing the font must change its glyph metrics, as well as its saved name.");
    }

    [STATestMethod]
    public void TrackingMovesGlyphsWithoutStretchingAndChangesWrapping()
    {
        var document = Movie("MMMM", 360, 160, 32);
        var tag = document.Tags.Single(t => t.Code == 37);
        var original = Render(document);
        tag.SetTextFormat(tag.GetTextFormat() with { LetterSpacing = 8 });
        var tracked = Render(document);
        Assert.IsTrue(tracked.Bounds.Width >= original.Bounds.Width + 21, "Four glyphs must gain three gaps of tracking.");
        Assert.AreEqual(original.Bounds.Height, tracked.Bounds.Height, 1);
        Assert.AreEqual(original.Alpha, tracked.Alpha, original.Alpha * .10,
            "Tracking must move the existing glyphs rather than scale their painted area.");

        document = Movie("MMMM MMMM", 220, 160, 24);
        tag = document.Tags.Single(t => t.Code == 37);
        original = Render(document);
        tag.SetTextFormat(tag.GetTextFormat() with { LetterSpacing = 8 });
        tracked = Render(document);
        Assert.IsTrue(tracked.Bounds.Height > original.Bounds.Height * 1.5,
            "Tracking must participate in word wrapping, so the second word moves to the next line.");
    }

    [STATestMethod]
    public void AlignmentPositionsTrackedTextAndHtmlDefaultsAreVisible()
    {
        var document = Movie("MMMM", 360, 160, 24);
        var tag = document.Tags.Single(t => t.Code == 37);
        tag.SetTextFormat(tag.GetTextFormat() with { LetterSpacing = 6 });
        var left = Render(document);
        tag.SetTextFormat(tag.GetTextFormat() with { Alignment = ScaleformTextAlignment.Center });
        var center = Render(document);
        tag.SetTextFormat(tag.GetTextFormat() with { Alignment = ScaleformTextAlignment.Right });
        var right = Render(document);
        Assert.IsTrue(center.Bounds.X > left.Bounds.X + 80);
        Assert.IsTrue(right.Bounds.X > center.Bounds.X + 80);
        Assert.AreEqual(center.Bounds.X - left.Bounds.X, right.Bounds.X - center.Bounds.X, 2,
            "Center alignment must place the same tracked line halfway between its left and right positions.");

        document = Movie("MMMM", 360, 160, 16);
        tag = document.Tags.Single(t => t.Code == 37);
        tag.SetTextFormat(tag.GetTextFormat() with
        {
            FontName = "Courier New", FontSize = 32, Color = 0x20D040FF,
            Alignment = ScaleformTextAlignment.Right, LetterSpacing = 6
        });
        var expected = Render(document);
        var html = Movie("<P ALIGN=\"RIGHT\"><FONT FACE=\"Courier New\" SIZE=\"32\" COLOR=\"#20D040\" LETTERSPACING=\"6\">MMMM</FONT></P>",
            360, 160, 16, true);
        var actual = Render(html);
        CollectionAssert.AreEqual(expected.Pixels, actual.Pixels,
            "Existing HTML field styles must produce the same preview as applying those styles in the editor.");
    }

    private static Raster Render(ScaleformDocument document)
    {
        var drawing = new ScaleformArtworkRenderer(document).RenderCharacter(2);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawDrawing(drawing);
        var bitmap = new RenderTargetBitmap(400, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[400 * 180 * 4];
        bitmap.CopyPixels(pixels, 400 * 4, 0);
        int xmin = 400, xmax = -1, ymin = 180, ymax = -1;
        double red = 0, green = 0, blue = 0, alpha = 0;
        for (int y = 0; y < 180; y++)
            for (int x = 0; x < 400; x++)
            {
                int i = (y * 400 + x) * 4;
                blue += pixels[i]; green += pixels[i + 1]; red += pixels[i + 2]; alpha += pixels[i + 3];
                if (pixels[i + 3] < 32) continue;
                xmin = Math.Min(xmin, x); xmax = Math.Max(xmax, x);
                ymin = Math.Min(ymin, y); ymax = Math.Max(ymax, y);
            }
        Assert.IsTrue(xmax >= xmin && ymax >= ymin, "The text preview must paint visible glyphs.");
        return new Raster(new Rect(xmin, ymin, xmax - xmin + 1, ymax - ymin + 1), red, green, blue, alpha, pixels);
    }

    private sealed record Raster(Rect Bounds, double Red, double Green, double Blue, double Alpha, byte[] Pixels);

    private static ScaleformDocument Movie(string text, int width, int height, int fontSize, bool html = false)
    {
        // A self-contained editable field with a system-font fallback. Font 1 intentionally has
        // no embedded glyphs; these tests exercise the native dynamic-text preview's metrics.
        byte[] field = Join([2, 0], Rectangle(0, width * 20, 0, height * 20),
            [0x85, html ? (byte)0x22 : (byte)0x20], [1, 0], BitConverter.GetBytes((ushort)(fontSize * 20)),
            [255, 255, 255, 255], new byte[9], CString("caption"), CString(text));
        byte[] body = Join(Rectangle(0, 8000, 0, 3600), [0, 24, 1, 0], Tag(37, field), Tag(1, []), Tag(0, []));
        return ScaleformDocument.Parse(Join("FWS"u8.ToArray(), [10], BitConverter.GetBytes((uint)body.Length + 8), body));
    }

    private static byte[] Rectangle(int xmin, int xmax, int ymin, int ymax)
    {
        var bytes = new List<byte>();
        int position = 0;
        void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--, position++)
            {
                if (position % 8 == 0) bytes.Add(0);
                bytes[^1] |= (byte)(((value >> i) & 1) << (7 - position % 8));
            }
        }
        Write(15, 5);
        foreach (int value in new[] { xmin, xmax, ymin, ymax }) Write(value, 15);
        return bytes.ToArray();
    }

    private static byte[] CString(string value) => Encoding.UTF8.GetBytes(value + "\0");
    private static byte[] Tag(ushort code, byte[] payload)
    {
        int header = payload.Length < 63 ? 2 : 6;
        byte[] result = new byte[header + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)((code << 6) | (header == 2 ? payload.Length : 63)));
        if (header == 6) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(result, header);
        return result;
    }
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
}
