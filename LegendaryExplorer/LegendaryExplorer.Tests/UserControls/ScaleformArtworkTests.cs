using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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
public class ScaleformArtworkTests
{
    [STATestMethod]
    public void ShapeContoursKeepHolesAndCompileBytesUntouched()
    {
        byte[] movie = Movie(Shape(1, Colors.Red, 100, 100, hole: true));
        var document = ScaleformDocument.Parse(movie);
        var renderer = new ScaleformArtworkRenderer(document);
        var drawing = renderer.RenderCharacter(1);
        var pixels = Pixels(drawing);
        Assert.AreEqual(Colors.Red, Pixel(pixels, 10, 10));
        Assert.AreEqual((byte)0, Pixel(pixels, 50, 50).A, "A closed inner contour must stay a hole.");
        Assert.AreEqual(new Rect(0, 0, 100, 100), renderer.GetCharacterBounds(1));
        Assert.IsEmpty(renderer.Warnings);
        CollectionAssert.AreEqual(movie, document.Compile(), "Preview must never edit a movie.");
    }

    [STATestMethod]
    public void ExternalBitmapUsesGfxTargetDimensionsAndBitmapMatrixUnits()
    {
        byte[] image = Join(BitConverter.GetBytes((uint)9), BitConverter.GetBytes((ushort)13), BitConverter.GetBytes((ushort)40), BitConverter.GetBytes((ushort)20), [0], Sized("native_image.tga"), [0]);
        var document = ScaleformDocument.Parse(Movie(Tag(1009, image), BitmapShape(1, 9, 40, 20)));
        string requested = null;
        var renderer = new ScaleformArtworkRenderer(document, name =>
        {
            requested = name;
            return BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255 }, 8);
        });
        byte[] pixels = Pixels(renderer.RenderCharacter(1));
        Assert.AreEqual("native_image.tga", requested);
        Assert.AreEqual(Colors.Blue, Pixel(pixels, 35, 15));
        Assert.AreEqual((byte)0, Pixel(pixels, 45, 15).A);
        Assert.AreEqual(new Rect(0, 0, 40, 20), renderer.GetCharacterBounds(9));
        Assert.IsEmpty(renderer.Warnings);
    }

    [STATestMethod]
    public void NestedSpriteMasksAndTransformInvalidationUseRealPlacementData()
    {
        byte[] sprite = Join(BitConverter.GetBytes((ushort)3), BitConverter.GetBytes((ushort)1),
            Place(1, 1, clipDepth: 2), Place(2, 2), Tag(1, []), Tag(0, []));
        var document = ScaleformDocument.Parse(Movie(Shape(1, Colors.Red, 50, 50), Shape(2, Colors.Blue, 100, 100), Tag(39, sprite)));
        var renderer = new ScaleformArtworkRenderer(document);
        var pixels = Pixels(renderer.RenderCharacter(3));
        Assert.AreEqual(Colors.Blue, Pixel(pixels, 25, 25));
        Assert.AreEqual((byte)0, Pixel(pixels, 75, 25).A, "ClipDepth must constrain following depths.");
        var content = ScaleformScene.Build(document, 3).Items.Single(item => item.Depth == 2);
        content.PlacementTag.SetPlacementMatrix(new ScaleformMatrix(1, 0, 0, 1, 30, 0));
        renderer.InvalidateScene();
        pixels = Pixels(renderer.RenderCharacter(3));
        Assert.AreEqual((byte)0, Pixel(pixels, 25, 25).A);
        Assert.AreEqual(Colors.Blue, Pixel(pixels, 35, 25));
    }

    [STATestMethod]
    public void MalformedArtworkReportsWarningAndPreservesOpaqueRecord()
    {
        byte[] movie = Movie(Tag(32, [1, 0, 0x80]));
        var document = ScaleformDocument.Parse(movie);
        var renderer = new ScaleformArtworkRenderer(document);
        Assert.IsNotNull(renderer.RenderCharacter(1));
        Assert.IsNotEmpty(renderer.Warnings);
        CollectionAssert.AreEqual(movie, document.Compile());
    }

    [STATestMethod]
    public void StaticTextUsesEmbeddedGlyphGeometryAndTextHeight()
    {
        var glyph = new BitWriter(); glyph.Bits(1, 4); glyph.Bits(0, 4); Contour(glyph, 0, 0, 1024, 1024); glyph.Bits(0, 6);
        byte[] font = Tag(10, Join(BitConverter.GetBytes((ushort)2), BitConverter.GetBytes((ushort)2), glyph.ToArray()));
        var text = new BitWriter(); text.Bytes(BitConverter.GetBytes((ushort)3)); text.Bytes(Rectangle(2000, 2000)); text.Bytes(Matrix(1, 0, 0));
        text.Bytes([1, 12, 0x8C, 2, 0, 0, 255, 0, 255]); text.Bytes(BitConverter.GetBytes((ushort)1000)); text.Bytes([1]);
        text.Bits(0, 1); text.Bits(1024, 12); text.Bytes([0]);
        var renderer = new ScaleformArtworkRenderer(ScaleformDocument.Parse(Movie(font, Tag(33, text.ToArray()))));
        byte[] pixels = Pixels(renderer.RenderCharacter(3));
        Assert.AreEqual(Colors.Lime, Pixel(pixels, 25, 25));
        Assert.AreEqual((byte)0, Pixel(pixels, 75, 25).A);
        Assert.IsEmpty(renderer.Warnings);
    }

    [STATestMethod]
    public void TimelineColorTransformAndHiddenPlacementsAffectActualPixels()
    {
        var cx = new BitWriter(); cx.Bits(1, 1); cx.Bits(1, 1); cx.Bits(11, 4);
        foreach (int value in new[] { 0, 0, 0, 256, 0, 128, 255, 0 }) cx.Bits(value, 11);
        byte[] colored = Tag(26, Join([14, 1, 0, 1, 0], Matrix(1, 0, 0), cx.ToArray()));
        byte[] hidden = Tag(70, Join([6, 0x20, 2, 0, 2, 0], Matrix(1, 0, 0), [0]));
        var document = ScaleformDocument.Parse(Movie(Shape(1, Colors.Red, 50, 50), Shape(2, Colors.Red, 50, 50), colored, hidden));
        Assert.IsFalse(ScaleformScene.Build(document).Items[1].Visible, "The authored visible flag must be false.");
        var renderer = new ScaleformArtworkRenderer(document);
        Assert.AreEqual(Color.FromRgb(0, 128, 255), Pixel(Pixels(renderer.RenderTimeline()), 25, 25));
        Assert.IsEmpty(renderer.Warnings);
    }

    [STATestMethod]
    public void ImportedSharedSymbolsUseExplicitFileAndNameResolver()
    {
        byte[] import = Tag(71, Join(Encoding.UTF8.GetBytes("shared.gfx\0"), [1, 0, 1, 0, 4, 0], Encoding.UTF8.GetBytes("closeButton\0")));
        string requestedFile = null, requestedName = null; int requestedFrame = 0;
        var renderer = new ScaleformArtworkRenderer(ScaleformDocument.Parse(Movie(import)), externalSymbolResolver: (file, name, frame) =>
        {
            requestedFile = file; requestedName = name; requestedFrame = frame;
            return new GeometryDrawing(Brushes.Yellow, null, new RectangleGeometry(new Rect(0, 0, 20, 10)));
        });
        Assert.AreEqual(Colors.Yellow, Pixel(Pixels(renderer.RenderCharacter(4, 7)), 5, 5));
        Assert.AreEqual("shared.gfx", requestedFile); Assert.AreEqual("closeButton", requestedName); Assert.AreEqual(7, requestedFrame);
        Assert.AreEqual(new Rect(0, 0, 20, 10), renderer.GetCharacterBounds(4));
        Assert.IsEmpty(renderer.Warnings);
    }

    [STATestMethod]
    public void GradientCoordinatesAndQuadraticEdgesRemainInPixelSpace()
    {
        var gradient = new BitWriter(); gradient.Bytes([1, 0]); gradient.Bytes(Rectangle(2000, 2000)); gradient.Bytes([1, 0x10]);
        var matrix = new BitWriter(); matrix.Bits(1, 1); matrix.Bits(14, 5); matrix.Bits(4096, 14); matrix.Bits(4096, 14);
        matrix.Bits(0, 1); matrix.Bits(16, 5); matrix.Bits(1000, 16); matrix.Bits(0, 16);
        gradient.Bytes(matrix.ToArray()); gradient.Bytes([2, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0]);
        gradient.Bits(1, 4); gradient.Bits(0, 4); Contour(gradient, 0, 0, 2000, 2000); gradient.Bits(0, 6);
        var curve = new BitWriter(); curve.Bytes([2, 0]); curve.Bytes(Rectangle(2000, 2000)); curve.Bytes([1, 0, 0, 255, 255, 255, 0]);
        curve.Bits(1, 4); curve.Bits(0, 4); curve.Bits(0, 1); curve.Bits(5, 5); curve.Bits(16, 5); curve.Bits(0, 16); curve.Bits(1000, 16); curve.Bits(1, 1);
        curve.Bits(1, 1); curve.Bits(0, 1); curve.Bits(14, 4); curve.Bits(1000, 16); curve.Bits(-2000, 16); curve.Bits(1000, 16); curve.Bits(2000, 16);
        Edge(curve, -2000, 0); curve.Bits(0, 6);
        var renderer = new ScaleformArtworkRenderer(ScaleformDocument.Parse(Movie(Tag(32, gradient.ToArray()), Tag(32, curve.ToArray()))));
        byte[] pixels = Pixels(renderer.RenderCharacter(1));
        Assert.IsTrue(Pixel(pixels, 10, 50).R > Pixel(pixels, 10, 50).B);
        Assert.IsTrue(Pixel(pixels, 90, 50).B > Pixel(pixels, 90, 50).R);
        pixels = Pixels(renderer.RenderCharacter(2));
        Assert.AreEqual(Colors.Cyan, Pixel(pixels, 50, 10));
        Assert.AreEqual((byte)0, Pixel(pixels, 50, 60).A);
        Assert.IsEmpty(renderer.Warnings);
    }

    private static byte[] Shape(ushort id, Color color, int width, int height, bool hole = false)
    {
        var b = new BitWriter();
        b.Bytes(BitConverter.GetBytes(id)); b.Bytes(Rectangle(width * 20, height * 20));
        b.Bytes([1, 0, color.R, color.G, color.B, color.A, 0]);
        b.Bits(1, 4); b.Bits(0, 4);
        Contour(b, 0, 0, width * 20, height * 20);
        if (hole) Contour(b, width * 5, height * 5, width * 10, height * 10);
        b.Bits(0, 6); return Tag(32, b.ToArray());
    }
    private static byte[] BitmapShape(ushort id, ushort bitmap, int width, int height)
    {
        var b = new BitWriter(); b.Bytes(BitConverter.GetBytes(id)); b.Bytes(Rectangle(width * 20, height * 20));
        b.Bytes([1, 0x41]); b.Bytes(BitConverter.GetBytes(bitmap)); b.Bytes(Matrix(20, 0, 0)); b.Bytes([0]);
        b.Bits(1, 4); b.Bits(0, 4); Contour(b, 0, 0, width * 20, height * 20); b.Bits(0, 6);
        return Tag(32, b.ToArray());
    }
    private static void Contour(BitWriter b, int x, int y, int width, int height)
    {
        b.Bits(0, 1); b.Bits(5, 5); b.Bits(16, 5); b.Bits(x, 16); b.Bits(y, 16); b.Bits(1, 1);
        Edge(b, width, 0); Edge(b, 0, height); Edge(b, -width, 0); Edge(b, 0, -height);
    }
    private static void Edge(BitWriter b, int dx, int dy) { b.Bits(1, 1); b.Bits(1, 1); b.Bits(14, 4); b.Bits(1, 1); b.Bits(dx, 16); b.Bits(dy, 16); }
    private static byte[] Place(ushort id, ushort depth, ushort? clipDepth = null) => Tag(26, Join(
        [(byte)(clipDepth.HasValue ? 0x46 : 6)], BitConverter.GetBytes(depth), BitConverter.GetBytes(id), Matrix(1, 0, 0), clipDepth.HasValue ? BitConverter.GetBytes(clipDepth.Value) : []));
    private static byte[] Matrix(int scale, int x, int y)
    {
        var b = new BitWriter(); b.Bits(scale == 1 ? 0 : 1, 1);
        if (scale != 1) { b.Bits(23, 5); b.Bits(scale * 65536, 23); b.Bits(scale * 65536, 23); }
        b.Bits(0, 1); b.Bits(16, 5); b.Bits(x * 20, 16); b.Bits(y * 20, 16); return b.ToArray();
    }
    private static byte[] Rectangle(int width, int height)
    {
        var b = new BitWriter(); b.Bits(16, 5); b.Bits(0, 16); b.Bits(width, 16); b.Bits(0, 16); b.Bits(height, 16); return b.ToArray();
    }
    private static byte[] Movie(params byte[][] tags)
    {
        byte[] body = Join(Rectangle(2400, 2400), [0, 24, 1, 0], Join(tags), Tag(1, []), Tag(0, []));
        return Join("FWS"u8.ToArray(), [10], BitConverter.GetBytes((uint)body.Length + 8), body);
    }
    private static byte[] Sized(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); return Join([(byte)bytes.Length], bytes); }
    private static byte[] Tag(ushort code, byte[] data)
    {
        bool extended = data.Length >= 63;
        return Join(BitConverter.GetBytes((ushort)((code << 6) | (extended ? 63 : data.Length))), extended ? BitConverter.GetBytes(data.Length) : [], data);
    }
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static byte[] Pixels(Drawing drawing)
    {
        var visual = new DrawingVisual(); using (var context = visual.RenderOpen()) context.DrawDrawing(drawing);
        var target = new RenderTargetBitmap(120, 120, 96, 96, PixelFormats.Pbgra32); target.Render(visual);
        byte[] pixels = new byte[120 * 120 * 4]; target.CopyPixels(pixels, 120 * 4, 0); return pixels;
    }
    private static Color Pixel(byte[] pixels, int x, int y) { int p = (y * 120 + x) * 4; return Color.FromArgb(pixels[p + 3], pixels[p + 2], pixels[p + 1], pixels[p]); }
    private sealed class BitWriter
    {
        private readonly List<byte> bytes = []; private int position;
        public void Bits(int value, int count)
        {
            for (int n = count - 1; n >= 0; n--)
            {
                if ((position & 7) == 0) bytes.Add(0);
                if (((value >> n) & 1) != 0) bytes[^1] |= (byte)(1 << (7 - (position & 7)));
                position++;
            }
        }
        public void Bytes(byte[] data) { position = (position + 7) & ~7; bytes.AddRange(data); position += data.Length * 8; }
        public byte[] ToArray() => bytes.ToArray();
    }
}
