using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests;

[TestClass]
public class ScaleformTextFormatTests
{
    [TestMethod]
    public void BinaryFormattingRoundTripsAndPreservesVariableMetricsMaxLengthAndTail()
    {
        byte[] payload = StyledField("original");
        var document = Movie(payload);
        var tag = document.Tags[0];
        Assert.IsTrue(tag.CanEditTextFormat);
        var original = tag.GetTextFormat();
        Assert.AreEqual((ushort)2, original.FontId);
        Assert.AreEqual(12, original.FontSize);
        Assert.AreEqual(0x01020304u, original.Color);
        Assert.AreEqual(ScaleformTextAlignment.Center, original.Alignment);
        var expected = original with { FontId = 5, FontSize = 28.5, Color = 0xAABBCCDD, Alignment = ScaleformTextAlignment.Justify };
        tag.SetTextFormat(expected);
        var result = ScaleformDocument.Parse(document.Compile()).Tags[0];
        Assert.AreEqual(expected, result.GetTextFormat());
        Assert.AreEqual("original", result.TextValue);
        CollectionAssert.AreEqual(payload.AsSpan(14, 2).ToArray(), result.Payload.Span.Slice(14, 2).ToArray(), "MaxLength must survive.");
        CollectionAssert.AreEqual(payload.AsSpan(17, 8).ToArray(), result.Payload.Span.Slice(17, 8).ToArray(), "Margins, indentation and leading must survive.");
        CollectionAssert.AreEqual(payload.AsSpan(25, 11).ToArray(), result.Payload.Span.Slice(25, 11).ToArray(), "VariableName must survive.");
        CollectionAssert.AreEqual(new byte[] { 0xEE, 0xFF }, result.Payload.Span[^2..].ToArray());
    }

    [TestMethod]
    public void NoOpFormatIsByteIdenticalIncludingHtmlSpellingAndUnknownAttributes()
    {
        var document = Movie(StyledField("<P ALIGN='right'><FONT color='#aaBBcc' size='20.25' face='Shared &amp; Font' letterSpacing='1.5' data-x='>'><B>Hello</B></FONT></P>", html: true));
        byte[] original = document.Compile();
        var tag = document.Tags[0];
        var format = tag.GetTextFormat();
        Assert.AreEqual(20.25, format.FontSize);
        Assert.AreEqual(0xAABBCC04u, format.Color);
        Assert.AreEqual("Shared & Font", format.FontName);
        Assert.AreEqual(1.5, format.LetterSpacing);
        Assert.AreEqual(ScaleformTextAlignment.Right, format.Alignment);
        tag.SetTextFormat(format);
        Assert.IsFalse(document.IsModified);
        CollectionAssert.AreEqual(original, document.Compile());
    }

    [TestMethod]
    public void HtmlChangesUpdateAllOverridesAndRetainUnrelatedMarkup()
    {
        const string html = "<!-- <FONT COLOR='#999999'> -->" +
            "<P ALIGN='right' data-x='keep'><FONT COLOR='#112233' SIZE='18' FACE='old' LETTERSPACING='2' KERNING='1'><B>A</B></FONT>" +
            "<FONT color=0x445566 size=24 face=second letterspacing=3>B</FONT></P><P ALIGN=LEFT>C</P>";
        var document = Movie(StyledField(html, html: true));
        var tag = document.Tags[0];
        var format = tag.GetTextFormat() with { FontId = 9, FontName = "New & Shared Font", FontSize = 30, Color = 0xABCDEF80, LetterSpacing = -0.5, Alignment = ScaleformTextAlignment.Center };
        tag.SetTextFormat(format);
        var result = ScaleformDocument.Parse(document.Compile()).Tags[0];
        Assert.AreEqual(format, result.GetTextFormat());
        StringAssert.Contains(result.TextValue, "<!-- <FONT COLOR='#999999'> -->");
        StringAssert.Contains(result.TextValue, "data-x='keep'");
        StringAssert.Contains(result.TextValue, "KERNING='1'><B>A</B>");
        StringAssert.Contains(result.TextValue, "COLOR='#ABCDEF'");
        StringAssert.Contains(result.TextValue, "color=\"#ABCDEF\"");
        StringAssert.Contains(result.TextValue, "face=\"New &amp; Shared Font\"");
        StringAssert.Contains(result.TextValue, "ALIGN=\"CENTER\"");
        Assert.IsFalse(result.TextValue.Contains("letterspacing=3", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RelativeHtmlSizeUsesBinaryDefaultAndChangesToAnAbsoluteSize()
    {
        foreach (var (relative, expected) in new[] { ("+2", 14.0), ("-2", 10.0) })
        {
            var document = Movie(StyledField("<FONT SIZE='" + relative + "'>relative</FONT>", html: true));
            var tag = document.Tags[0];
            Assert.AreEqual(expected, tag.GetTextFormat().FontSize);
            byte[] original = document.Compile();
            tag.SetTextFormat(tag.GetTextFormat());
            CollectionAssert.AreEqual(original, document.Compile());
            tag.SetTextFormat(tag.GetTextFormat() with { FontSize = 24 });
            var result = ScaleformDocument.Parse(document.Compile()).Tags[0];
            Assert.AreEqual(24, result.GetTextFormat().FontSize);
            Assert.AreEqual("<FONT SIZE='24'>relative</FONT>", result.TextValue);
        }
    }

    [TestMethod]
    public void StringAndFormatEditsComposeInEitherOrderAcrossOptionalFieldInsertion()
    {
        foreach (bool textFirst in new[] { false, true })
        {
            var document = Movie(PlainField("old"));
            var tag = document.Tags[0];
            var expected = tag.GetTextFormat() with { Color = 0x010203FF, Alignment = ScaleformTextAlignment.Right };
            if (textFirst) tag.SetText("first");
            tag.SetTextFormat(expected);
            tag.SetText("final text");
            var result = ScaleformDocument.Parse(document.Compile()).Tags[0];
            Assert.AreEqual("final text", result.TextValue);
            Assert.AreEqual(expected, result.GetTextFormat());
            StringAssert.Contains(Encoding.UTF8.GetString(result.Payload.Span), "_root.test\0");
        }
    }

    [TestMethod]
    public void LetterSpacingEncodesPlainTextBeforeEnablingHtml()
    {
        var document = Movie(StyledField("<B>literal & text</B>\r\nnext"));
        var tag = document.Tags[0];
        var expected = tag.GetTextFormat() with { LetterSpacing = 2.25 };
        tag.SetTextFormat(expected);
        Assert.AreEqual("<FONT LETTERSPACING=\"2.25\">&lt;B&gt;literal &amp; text&lt;/B&gt;<BR>next</FONT>", tag.TextValue);
        Assert.AreEqual((byte)2, (byte)(tag.Payload.Span[5] & 2));
        Assert.AreEqual(expected, ScaleformDocument.Parse(document.Compile()).Tags[0].GetTextFormat());
        byte[] once = document.Compile();
        tag.SetTextFormat(expected);
        CollectionAssert.AreEqual(once, document.Compile());
    }

    [TestMethod]
    public void SizeWithoutFontUsesHtmlAndDoesNotInventFontId()
    {
        var document = Movie(PlainField("<hello>"));
        var tag = document.Tags[0];
        var expected = tag.GetTextFormat() with { FontSize = 22.5 };
        tag.SetTextFormat(expected);
        var result = ScaleformDocument.Parse(document.Compile()).Tags[0];
        Assert.IsNull(result.GetTextFormat().FontId);
        Assert.AreEqual(expected, result.GetTextFormat());
        Assert.AreEqual((byte)0, (byte)(result.Payload.Span[4] & 1));
        Assert.AreEqual("<FONT SIZE=\"22.5\">&lt;hello&gt;</FONT>", result.TextValue);
    }

    [TestMethod]
    public void EmptyFieldSupportsFormattingBeforeAnInitialTextValueExists()
    {
        var document = Movie(PlainField(null));
        var tag = document.Tags[0];
        Assert.IsTrue(tag.CanEditTextFormat);
        tag.SetTextFormat(tag.GetTextFormat() with { Color = 0x44556677, Alignment = ScaleformTextAlignment.Right });
        Assert.AreEqual(string.Empty, tag.TextValue);
        Assert.AreEqual((byte)0, (byte)(tag.Payload.Span[4] & 0x80));
        tag.SetText("added later");
        var result = ScaleformDocument.Parse(document.Compile()).Tags[0];
        Assert.AreEqual("added later", result.TextValue);
        Assert.AreEqual(0x44556677u, result.GetTextFormat().Color);
        Assert.AreEqual(ScaleformTextAlignment.Right, result.GetTextFormat().Alignment);
    }

    [TestMethod]
    public void RemovingEmbeddedFontRetainsSizeThroughHtml()
    {
        var document = Movie(StyledField("original"));
        var tag = document.Tags[0];
        tag.SetTextFormat(tag.GetTextFormat() with { FontSize = 24 });
        var expected = tag.GetTextFormat() with { FontId = null };
        tag.SetTextFormat(expected);
        var result = ScaleformDocument.Parse(document.Compile()).Tags[0];
        Assert.AreEqual(expected, result.GetTextFormat());
        Assert.AreEqual("<FONT SIZE=\"24\">original</FONT>", result.TextValue);
    }

    [TestMethod]
    public void CanonicalHtmlCanBeRestoredWhenFinalFormatMatchesPlainSource()
    {
        var edited = Movie(StyledField("<literal>"));
        var originalFormat = edited.Tags[0].GetTextFormat();
        edited.Tags[0].SetTextFormat(originalFormat with { LetterSpacing = 2 });
        edited.Tags[0].SetTextFormat(originalFormat);
        string canonical = edited.Tags[0].TextValue;
        Assert.AreEqual(originalFormat, edited.Tags[0].GetTextFormat());

        var rebuilt = Movie(StyledField("<literal>"));
        rebuilt.Tags[0].SetTextFormat(originalFormat); // This is deliberately a binary no-op.
        rebuilt.Tags[0].SetHtmlText(canonical);
        var result = ScaleformDocument.Parse(rebuilt.Compile()).Tags[0];
        Assert.AreEqual(canonical, result.TextValue);
        Assert.AreEqual(originalFormat, result.GetTextFormat());
        Assert.AreEqual((byte)2, (byte)(result.Payload.Span[5] & 2));
        CollectionAssert.AreEqual(edited.Compile(), rebuilt.Compile());
    }

    [TestMethod]
    public void HtmlTextSetterPreservesExistingFieldsAndRejectsInvalidStringsAtomically()
    {
        var document = Movie(StyledField("<B>original</B>", html: true));
        var tag = document.Tags[0];
        byte[] original = document.Compile();
        tag.SetHtmlText(tag.TextValue);
        Assert.IsFalse(document.IsModified);
        Assert.ThrowsExactly<ArgumentException>(() => tag.SetHtmlText("null\0character"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => tag.SetHtmlText("\uD800"));
        CollectionAssert.AreEqual(original, document.Compile());
        tag.SetHtmlText("<FONT LETTERSPACING='1'>new</FONT>");
        CollectionAssert.AreEqual(StyledField("", html: true).AsSpan(0, 36).ToArray(), tag.Payload.Span[..36].ToArray());
        CollectionAssert.AreEqual(new byte[] { 0xEE, 0xFF }, tag.Payload.Span[^2..].ToArray());
        Assert.AreEqual(1, ScaleformDocument.Parse(document.Compile()).Tags[0].GetTextFormat().LetterSpacing);
    }

    [TestMethod]
    public void InvalidFormattingIsRejectedAtomically()
    {
        var document = Movie(StyledField("original"));
        var tag = document.Tags[0];
        var format = tag.GetTextFormat();
        byte[] original = document.Compile();
        foreach (var bad in new[]
        {
            format with { FontSize = double.NaN }, format with { FontSize = 0 }, format with { FontSize = 3276.8 },
            format with { LetterSpacing = double.PositiveInfinity }, format with { LetterSpacing = -512.1 },
            format with { FontId = 0 }, format with { FontId = ushort.MaxValue }, format with { Alignment = (ScaleformTextAlignment)4 }
        })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tag.SetTextFormat(bad));
            CollectionAssert.AreEqual(original, document.Compile());
        }
        Assert.ThrowsExactly<ArgumentException>(() => tag.SetTextFormat(format with { FontName = "nul\0name" }));
        Assert.ThrowsExactly<EncoderFallbackException>(() => tag.SetTextFormat(format with { FontName = "\uD800" }));
        CollectionAssert.AreEqual(original, document.Compile());
    }

    [TestMethod]
    public void FontClassAndMalformedFieldRemainOpaque()
    {
        var fontClass = Movie(Join(new byte[] { 1, 0, 8, 0, 0x80, 0x80 }, CString("FontClass"), new byte[] { 0xF0, 0 }, CString("variable"), CString("initial")));
        var malformed = Movie(new byte[] { 1, 0, 8, 0, 0x80, 0, 0, 0xFF, 0 });
        foreach (var document in new[] { fontClass, malformed })
        {
            byte[] original = document.Compile();
            var tag = document.Tags[0];
            Assert.IsFalse(tag.CanEditTextFormat);
            Assert.ThrowsExactly<InvalidOperationException>(() => tag.GetTextFormat());
            Assert.ThrowsExactly<InvalidOperationException>(() => tag.SetTextFormat(new ScaleformTextFormat()));
            CollectionAssert.AreEqual(original, document.Compile());
        }
    }

    private static byte[] StyledField(string text, bool html = false)
    {
        byte[] prefix = { 0x12, 0, 8, 0, 0x87, (byte)(0x20 | (html ? 2 : 0)), 2, 0, 0xF0, 0, 1, 2, 3, 4, 60, 0,
            2, 3, 0, 4, 0, 5, 0, 0xFF, 0xFF };
        return Join(prefix, CString("_root.test"), CString(text), new byte[] { 0xEE, 0xFF });
    }

    private static byte[] PlainField(string text) => Join(new byte[] { 1, 0, 8, 0, (byte)(text == null ? 0 : 0x80), 0 }, CString("_root.test"), text == null ? Array.Empty<byte>() : CString(text));

    private static ScaleformDocument Movie(byte[] field)
    {
        byte[] body = Join(new byte[] { 8, 0, 0, 24, 1, 0 }, Tag(37, field), Tag(1, []), Tag(0, []));
        return ScaleformDocument.Parse(Join(Encoding.ASCII.GetBytes("FWS"), new byte[] { 10 }, BitConverter.GetBytes((uint)body.Length + 8), body));
    }

    private static byte[] Tag(ushort code, byte[] payload)
    {
        bool longHeader = payload.Length >= 63;
        byte[] bytes = new byte[payload.Length + (longHeader ? 6 : 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)((code << 6) | (longHeader ? 63 : payload.Length)));
        if (longHeader) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)payload.Length);
        payload.CopyTo(bytes, longHeader ? 6 : 2);
        return bytes;
    }

    private static byte[] CString(string value) => Join(Encoding.UTF8.GetBytes(value), new byte[] { 0 });
    private static byte[] Join(params byte[][] values) => values.SelectMany(v => v).ToArray();
}
