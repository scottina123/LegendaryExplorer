using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ScaleformTests
{
    [TestMethod]
    public void UntouchedMoviesRoundTripExactlyIncludingCompressionAndOpaqueRecords()
    {
        foreach (string signature in new[] { "FWS", "CWS", "GFX", "CFX" })
        {
            byte[] original = Movie(signature,
                Tag(1009, new byte[] { 0xAD, 0xDE, 0, 0xFF }, longHeader: true),
                Tag(43, CString("frame")), Tag(1), Tag(0));
            var document = ScaleformDocument.Parse(original);
            Assert.AreEqual(signature, document.Signature);
            Assert.AreEqual(24, document.FrameRate);
            Assert.IsFalse(document.IsModified);
            CollectionAssert.AreEqual(original, document.Compile());
            // Parse and Compile return owned copies rather than retaining caller arrays.
            original[0] = 0;
            Assert.AreEqual((byte)signature[0], document.Compile()[0]);
        }
    }

    [TestMethod]
    public void TextEditRebuildsNestedLengthsAndPreservesUneditedHeadersAndTrailers()
    {
        byte[] opaque = Tag(1001, new byte[] { 0xAB, 0xCD, 0xEF }, longHeader: true);
        byte[] nested = Join(new byte[] { 0x25, 0x01, 1, 0 }, Tag(43, Join(CString("old"), new byte[] { 1 })),
            opaque, Tag(1), Tag(0), new byte[] { 0xD3, 0x31 });
        byte[] original = Movie("GFX", Tag(39, nested), opaque, Tag(1), Tag(0), new byte[] { 0xFA, 0xCE });
        var document = ScaleformDocument.Parse(original);
        string editedText = new('z', 80); // Crosses both a child and parent short-tag-header boundary.
        document.Tags[0].Children[0].SetText(editedText);
        Assert.IsTrue(document.IsModified);
        var compiled = document.Compile();
        var reparsed = ScaleformDocument.Parse(compiled);
        var sprite = reparsed.Tags[0];
        Assert.AreEqual(editedText, sprite.Children[0].TextValue);
        Assert.AreEqual((byte)1, sprite.Children[0].Payload.Span[^1], "Named-anchor flag must survive.");
        CollectionAssert.AreEqual(opaque, compiled.AsSpan(reparsed.Tags[1].OriginalOffset, opaque.Length).ToArray());
        CollectionAssert.AreEqual(opaque, compiled.AsSpan(sprite.Children[1].OriginalOffset, opaque.Length).ToArray());
        CollectionAssert.AreEqual(new byte[] { 0xFA, 0xCE }, compiled[^2..]);
        CollectionAssert.AreEqual(new byte[] { 0xD3, 0x31 }, sprite.Payload.Span[^2..].ToArray());
        CollectionAssert.AreEqual(nested[..4], sprite.Payload.Span[..4].ToArray(), "Sprite identity and frame count must survive.");
    }

    [TestMethod]
    public void CompressedEditsKeepSignatureAndChangeOnlySupportedFields()
    {
        foreach (string signature in new[] { "CWS", "CFX" })
        {
            var document = ScaleformDocument.Parse(Movie(signature,
                Tag(9, new byte[] { 1, 2, 3 }), Tag(77, CString("<rdf/>")),
                Tag(999, new byte[] { 200, 201, 202 }), Tag(1), Tag(0)));
            document.FrameRate = 29.5;
            document.Tags[0].SetBackgroundColor(10, 20, 30);
            document.Tags[1].SetText("<rdf>☃</rdf>");
            var result = ScaleformDocument.Parse(document.Compile());
            Assert.AreEqual(signature, result.Signature);
            Assert.AreEqual(29.5, result.FrameRate);
            Assert.AreEqual(document.FrameCount, result.FrameCount);
            Assert.AreEqual("<rdf>☃</rdf>", result.Tags[1].TextValue);
            CollectionAssert.AreEqual(new byte[] { 10, 20, 30 }, result.Tags[0].Payload.ToArray());
            CollectionAssert.AreEqual(new byte[] { 200, 201, 202 }, result.Tags[2].Payload.ToArray());
        }
    }

    [TestMethod]
    public void DynamicTextEditPreservesStyleAndVariableData()
    {
        // ID, minimal RECT, HasText+HasFont+HasTextColor+HasMaxLength, HasLayout,
        // FontID/Height, RGBA, MaxLength, Align/Margins/Indent/Leading, VariableName, InitialText.
        byte[] prefix = { 0x12, 0, 8, 0, 0x87, 0x20, 2, 0, 0xF0, 0, 1, 2, 3, 4, 60, 0,
            2, 3, 0, 4, 0, 5, 0, 0xFF, 0xFF };
        byte[] payload = Join(prefix, CString("_root.test"), CString("hello"), new byte[] { 0xEE });
        var document = ScaleformDocument.Parse(Movie("FWS", Tag(37, payload), Tag(1), Tag(0)));
        var tag = document.Tags[0];
        Assert.IsTrue(tag.CanEditText);
        Assert.AreEqual("hello", tag.TextValue);
        tag.SetText("longer text");
        tag.SetText("second");
        var result = ScaleformDocument.Parse(document.Compile()).Tags[0];
        Assert.AreEqual("second", result.TextValue);
        CollectionAssert.AreEqual(Join(prefix, CString("_root.test")), result.Payload.Span[..(prefix.Length + 11)].ToArray());
        Assert.AreEqual((byte)0xEE, result.Payload.Span[^1]);
        tag.SetText("hello");
        Assert.IsFalse(document.IsModified);
    }

    [TestMethod]
    public void DynamicTextWithoutInitialValueCanReceiveOne()
    {
        byte[] payload = { 1, 0, 8, 0, 0, 0, 0 }; // ID, RECT, flags, empty variable
        var document = ScaleformDocument.Parse(Movie("FWS", Tag(37, payload), Tag(1), Tag(0)));
        Assert.IsTrue(document.Tags[0].CanEditText);
        Assert.AreEqual(string.Empty, document.Tags[0].TextValue);
        document.Tags[0].SetText("new");
        var tag = ScaleformDocument.Parse(document.Compile()).Tags[0];
        Assert.AreEqual("new", tag.TextValue);
        Assert.AreEqual((byte)0x80, tag.Payload.Span[4]);
        Assert.AreEqual(11, tag.Payload.Length);
    }

    [TestMethod]
    public void InvalidTextIsOpaqueAndCannotBeAccidentallyReencoded()
    {
        byte[] original = Movie("FWS", Tag(43, new byte[] { 0xC3, 0x28, 0 }), Tag(77, new byte[] { 1, 2 }), Tag(1), Tag(0));
        var document = ScaleformDocument.Parse(original);
        Assert.IsFalse(document.Tags[0].CanEditText);
        Assert.IsFalse(document.Tags[1].CanEditText);
        Assert.ThrowsExactly<InvalidOperationException>(() => document.Tags[0].SetText("replacement"));
        CollectionAssert.AreEqual(original, document.Compile());
        byte[] classFont = Join(new byte[] { 1, 0, 8, 0, 0x80, 0x80 }, CString("FontClass"),
            new byte[] { 0xF0, 0 }, CString("variable"), CString("initial"));
        var classDocument = ScaleformDocument.Parse(Movie("FWS", Tag(37, classFont), Tag(1), Tag(0)));
        Assert.IsFalse(classDocument.Tags[0].CanEditText, "Unverified FontClass layouts must remain opaque.");
    }

    [TestMethod]
    public void DoInitActionEditKeepsSpriteIdAndValidatesActionFraming()
    {
        var document = ScaleformDocument.Parse(Movie("FWS", Tag(59, new byte[] { 0x34, 0x12, 7, 0 }), Tag(1), Tag(0)));
        var tag = document.Tags[0];
        tag.SetActionBytes(new byte[] { 6, 7, 0 });
        CollectionAssert.AreEqual(new byte[] { 0x34, 0x12, 6, 7, 0 }, ScaleformDocument.Parse(document.Compile()).Tags[0].Payload.ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => tag.SetActionBytes(new byte[] { 0x81, 2, 0, 1 }));
        Assert.ThrowsExactly<InvalidDataException>(() => tag.SetActionBytes(new byte[] { 0, 7 }));
        Assert.ThrowsExactly<InvalidDataException>(() => tag.SetActionBytes(new byte[] { 7 }));
    }

    [TestMethod]
    public void CorruptAndUnsupportedMoviesAreRejectedBeforeEditing()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(new byte[7]));
        byte[] valid = Movie("FWS", Tag(1), Tag(0));
        byte[] unsupported = (byte[])valid.Clone();
        Encoding.ASCII.GetBytes("ZWS").CopyTo(unsupported, 0);
        Assert.ThrowsExactly<NotSupportedException>(() => ScaleformDocument.Parse(unsupported));
        unsupported[0] = (byte)'X';
        Assert.ThrowsExactly<NotSupportedException>(() => ScaleformDocument.Parse(unsupported));
        byte[] wrongLength = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongLength.AsSpan(4), (uint)valid.Length + 1);
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(wrongLength));
        BinaryPrimitives.WriteUInt32LittleEndian(wrongLength.AsSpan(4), uint.MaxValue);
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(wrongLength));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(Movie("FWS", Tag(1))));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(Movie("FWS", new byte[] { 0xFF, 0xFF, 0, 1 })));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(Movie("FWS", new byte[] { 0xC1, 0xFA }, Tag(0))));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(Movie("FWS", Tag(0, new byte[] { 1 }))));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(Movie("FWS", Tag(39, new byte[] { 1, 0, 1, 0 }), Tag(0))));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(Movie("FWS", Tag(9, new byte[] { 1 }), Tag(0))));
    }

    [TestMethod]
    public void BadCompressedChecksumTruncationAndDeclaredLengthsAreRejected()
    {
        byte[] valid = Movie("CFX", Tag(43, CString("text")), Tag(1), Tag(0));
        byte[] badChecksum = (byte[])valid.Clone();
        badChecksum[^1] ^= 0xFF;
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(badChecksum));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(valid[..^2]));
        // Keep the checksum while truncating the actual DEFLATE stream.
        byte[] missingDeflateEnd = Join(valid[..^5], valid[^4..]);
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(missingDeflateEnd));
        byte[] tooSmall = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooSmall.AsSpan(4), 14);
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(tooSmall));
        byte[] tooLarge = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooLarge.AsSpan(4), (uint)valid.Length + 1000);
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(tooLarge));
    }

    [TestMethod]
    public void ExcessiveSpriteNestingIsRejected()
    {
        byte[] nested = Tag(0);
        for (int i = 0; i < 65; i++)
            nested = Tag(39, Join(new byte[] { 1, 0, 0, 0 }, nested, Tag(0)));
        Assert.ThrowsExactly<InvalidDataException>(() => ScaleformDocument.Parse(Movie("FWS", nested, Tag(0))));
    }

    [TestMethod]
    public void FrameRateRejectsInvalidValuesAndRevertingRestoresExactOriginal()
    {
        byte[] original = Movie("CWS", Tag(43, CString("original")), Tag(1), Tag(0));
        var document = ScaleformDocument.Parse(original);
        foreach (double invalid in new[] { 0.0, -1, double.NaN, double.PositiveInfinity, 256, 0.00001 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => document.FrameRate = invalid);
        document.FrameRate = 30;
        document.Tags[0].SetText("changed");
        document.FrameRate = 24;
        document.Tags[0].SetText("original");
        Assert.IsFalse(document.IsModified);
        CollectionAssert.AreEqual(original, document.Compile());
    }

    private static byte[] Movie(string signature, params byte[][] records)
    {
        // RECT: NBits=1, all coordinates zero, followed by FIXED8 rate=24 and frame count=1.
        byte[] body = Join(new byte[] { 8, 0, 0, 24, 1, 0 }, Join(records));
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes(signature));
        output.WriteByte(10);
        output.Write(BitConverter.GetBytes((uint)body.Length + 8));
        if (signature is "CWS" or "CFX")
        {
            using var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
            compressor.Write(body);
        }
        else
            output.Write(body);
        return output.ToArray();
    }

    private static byte[] Tag(ushort code, byte[] payload = null, bool longHeader = false)
    {
        payload ??= Array.Empty<byte>();
        bool longForm = longHeader || payload.Length >= 63;
        byte[] bytes = new byte[payload.Length + (longForm ? 6 : 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)((code << 6) | (longForm ? 63 : payload.Length)));
        if (longForm)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(bytes, bytes.Length - payload.Length);
        return bytes;
    }

    private static byte[] CString(string value) => Join(Encoding.UTF8.GetBytes(value), new byte[] { 0 });
    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(a => a).ToArray();
}
