using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LegendaryExplorerCore.Scaleform;

public enum ScaleformTextAlignment { Left, Right, Center, Justify }

/// <summary>Whole-field text formatting. Sizes and spacing are pixels; color is RRGGBBAA.</summary>
public sealed record ScaleformTextFormat
{
    public ushort? FontId { get; init; }
    public string FontName { get; init; } = string.Empty;
    public double FontSize { get; init; } = 12;
    public uint Color { get; init; } = 0xFFFFFFFF;
    public ScaleformTextAlignment Alignment { get; init; }
    public double LetterSpacing { get; init; }
}

public sealed partial class ScaleformTag
{
    // Match complete tags and quoted attributes without interpreting a '>' inside a value.
    // Comments are consumed as a unit so apparent tags inside them remain untouched.
    private static readonly Regex HtmlTags = new(@"<!--[\s\S]*?-->|<(?<close>/)?(?<name>[A-Za-z][A-Za-z0-9]*)(?<attributes>(?:""[^""]*""|'[^']*'|[^'"">])*)>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex HtmlAttributes = new(@"(?<name>[^\s='""<>/]+)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>Whether this DefineEditText has a verified, editable AVM1 field layout.</summary>
    public bool CanEditTextFormat => Code == 37 && CanEditText && TryReadTextLayout(out _);

    /// <summary>Reads binary defaults with the first HTML font/paragraph overrides applied.</summary>
    public ScaleformTextFormat GetTextFormat()
    {
        var layout = ReadTextLayout();
        var format = new ScaleformTextFormat
        {
            FontId = layout.FontId,
            FontSize = layout.FontHeight / 20.0,
            Color = layout.Color,
            Alignment = layout.Alignment
        };
        if ((layout.Flags2 & 2) == 0 || !layout.HasText) return format;
        bool color = false, size = false, face = false, spacing = false, align = false;
        foreach (Match tag in HtmlTags.Matches(TextValue))
        {
            if (tag.Groups["close"].Success) continue;
            string name = tag.Groups["name"].Value;
            foreach (Match attribute in HtmlAttributes.Matches(tag.Groups["attributes"].Value))
            {
                string key = attribute.Groups["name"].Value;
                string value = WebUtility.HtmlDecode(AttributeValue(attribute).Value);
                if (name.Equals("font", StringComparison.OrdinalIgnoreCase))
                {
                    if (!color && key.Equals("color", StringComparison.OrdinalIgnoreCase) && TryHtmlColor(value, out uint rgb))
                    { format = format with { Color = (rgb << 8) | (format.Color & 255) }; color = true; }
                    else if (!size && key.Equals("size", StringComparison.OrdinalIgnoreCase) && TryHtmlSize(value, layout.FontHeight / 20.0, out double pixels))
                    { format = format with { FontSize = pixels }; size = true; }
                    else if (!spacing && key.Equals("letterspacing", StringComparison.OrdinalIgnoreCase) && TryNumber(value, out double pixelsSpacing))
                    { format = format with { LetterSpacing = pixelsSpacing }; spacing = true; }
                    else if (!face && key.Equals("face", StringComparison.OrdinalIgnoreCase))
                    { format = format with { FontName = value }; face = true; }
                }
                else if (!align && name.Equals("p", StringComparison.OrdinalIgnoreCase) && key.Equals("align", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(value, true, out ScaleformTextAlignment alignment) && Enum.IsDefined(alignment))
                { format = format with { Alignment = alignment }; align = true; }
            }
        }
        return format;
    }

    /// <summary>Changes whole-field styling while retaining variable names, layout metrics and opaque tails.</summary>
    public void SetTextFormat(ScaleformTextFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var layout = ReadTextLayout();
        var current = GetTextFormat();
        if (format == current) return;
        if (!double.IsFinite(format.FontSize) || format.FontSize < 0.05 || format.FontSize > ushort.MaxValue / 20.0)
            throw new ArgumentOutOfRangeException(nameof(format), "Font size must fit a positive 16-bit twip height (0.05 to 3276.75 pixels).");
        if (!double.IsFinite(format.LetterSpacing) || format.LetterSpacing is < -512 or > 512)
            throw new ArgumentOutOfRangeException(nameof(format), "Letter spacing must be between -512 and 512 pixels.");
        if (!Enum.IsDefined(format.Alignment)) throw new ArgumentOutOfRangeException(nameof(format), "Choose a supported text alignment.");
        if (format.FontId is 0 or ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(format), "Choose a valid movie font ID.");
        string fontName = format.FontName ?? string.Empty;
        if (fontName.Contains('\0')) throw new ArgumentException("A font name cannot contain a null character.", nameof(format));
        _ = EncodeText(fontName); // Validate UTF-8/code-page representability before making any changes.

        bool fontChanged = format.FontId != current.FontId;
        bool sizeChanged = format.FontSize != current.FontSize;
        bool colorChanged = format.Color != current.Color;
        bool alignmentChanged = format.Alignment != current.Alignment;
        bool spacingChanged = format.LetterSpacing != current.LetterSpacing;
        bool nameChanged = fontName != current.FontName;
        var data = Payload.Span;
        byte flags1 = layout.Flags1, flags2 = layout.Flags2;
        ushort? fontId = fontChanged ? format.FontId : layout.FontId;
        bool hasFont = fontId.HasValue;
        flags1 = hasFont ? (byte)(flags1 | 1) : (byte)(flags1 & ~1);
        if (colorChanged) flags1 |= 4;
        if (alignmentChanged) flags2 |= 0x20;

        string text = TextValue;
        bool html = (flags2 & 2) != 0;
        // Spacing and a font size without an embedded font require Flash HTML formatting.
        bool htmlSize = !hasFont && (sizeChanged || fontChanged);
        bool needsHtml = spacingChanged || nameChanged || htmlSize;
        if (needsHtml && !html)
        {
            text = EscapeHtmlText(text);
            flags2 |= 2;
            html = true;
        }
        if (html)
        {
            text = RewriteHtmlFormat(text, format, colorChanged, sizeChanged, nameChanged || fontChanged && fontName.Length != 0, spacingChanged, alignmentChanged);
            var additions = new List<string>();
            if (spacingChanged) additions.Add("LETTERSPACING=\"" + Number(format.LetterSpacing) + "\"");
            if (htmlSize) additions.Add("SIZE=\"" + Number(format.FontSize) + "\"");
            if ((nameChanged || fontChanged) && fontName.Length != 0) additions.Add("FACE=\"" + EscapeHtmlAttribute(fontName) + "\"");
            if (additions.Count != 0) text = "<FONT " + string.Join(" ", additions) + ">" + text + "</FONT>";
        }
        bool hasText = layout.HasText || text.Length != 0;
        if (hasText) flags1 |= 0x80;
        byte[] encoded = hasText ? EncodeText(text) : Array.Empty<byte>();
        using var output = new MemoryStream();
        output.Write(data[..layout.FlagsOffset]);
        output.WriteByte(flags1); output.WriteByte(flags2);
        if (hasFont)
        {
            WriteU16(output, fontId.Value);
            WriteU16(output, sizeChanged ? checked((ushort)Math.Round(format.FontSize * 20)) : layout.FontHeight);
        }
        if ((flags1 & 4) != 0)
        {
            uint color = colorChanged ? format.Color : layout.Color;
            output.WriteByte((byte)(color >> 24)); output.WriteByte((byte)(color >> 16));
            output.WriteByte((byte)(color >> 8)); output.WriteByte((byte)color);
        }
        if (layout.MaxLengthOffset >= 0) output.Write(data.Slice(layout.MaxLengthOffset, 2));
        if ((flags2 & 0x20) != 0)
        {
            output.WriteByte((byte)(alignmentChanged ? format.Alignment : layout.Alignment));
            if (layout.LayoutOffset >= 0) output.Write(data.Slice(layout.LayoutOffset + 1, 8));
            else output.Write(new byte[8]);
        }
        output.Write(data.Slice(layout.VariableOffset, layout.TextOffset - layout.VariableOffset));
        if (hasText) { output.Write(encoded); output.WriteByte(0); }
        output.Write(data[layout.TailOffset..]);
        byte[] edited = output.ToArray();
        replacement = edited.AsSpan().SequenceEqual(originalPayload.Span) ? null : edited;
        LocateEditableText();
    }

    private static string RewriteHtmlFormat(string text, ScaleformTextFormat format, bool color, bool size, bool face, bool spacing, bool align)
    {
        return HtmlTags.Replace(text, tag =>
        {
            if (tag.Groups["close"].Success) return tag.Value;
            bool font = tag.Groups["name"].Value.Equals("font", StringComparison.OrdinalIgnoreCase);
            bool paragraph = tag.Groups["name"].Value.Equals("p", StringComparison.OrdinalIgnoreCase);
            if (!font && !paragraph) return tag.Value;
            Group attributes = tag.Groups["attributes"];
            string replacementAttributes = HtmlAttributes.Replace(attributes.Value, attribute =>
            {
                string name = attribute.Groups["name"].Value;
                string value = null;
                if (font)
                {
                    if (color && name.Equals("color", StringComparison.OrdinalIgnoreCase)) value = "#" + (format.Color >> 8).ToString("X6", CultureInfo.InvariantCulture);
                    else if (size && name.Equals("size", StringComparison.OrdinalIgnoreCase)) value = Number(format.FontSize);
                    else if (spacing && name.Equals("letterspacing", StringComparison.OrdinalIgnoreCase)) value = Number(format.LetterSpacing);
                    else if (face && name.Equals("face", StringComparison.OrdinalIgnoreCase)) value = EscapeHtmlAttribute(format.FontName ?? string.Empty);
                }
                else if (align && name.Equals("align", StringComparison.OrdinalIgnoreCase)) value = format.Alignment.ToString().ToUpperInvariant();
                if (value == null) return attribute.Value;
                Group oldValue = AttributeValue(attribute);
                int start = oldValue.Index - attribute.Index;
                // Preserve quoting unless a formerly unquoted value needs quotes for a font name.
                if (attribute.Groups["bare"].Success) value = "\"" + value + "\"";
                return attribute.Value[..start] + value + attribute.Value[(start + oldValue.Length)..];
            });
            int localStart = attributes.Index - tag.Index;
            return tag.Value[..localStart] + replacementAttributes + tag.Value[(localStart + attributes.Length)..];
        });
    }

    private EditTextLayout ReadTextLayout()
    {
        if (!CanEditTextFormat || !TryReadTextLayout(out var layout))
            throw new InvalidOperationException("This record does not contain a supported editable text format.");
        return layout;
    }

    private bool TryReadTextLayout(out EditTextLayout layout)
    {
        layout = null;
        if (Code != 37) return false;
        try
        {
            var data = Payload.Span;
            Require(data, 0, 2);
            int offset = 2 + ScaleformDocument.ReadRectangle(data, 2, out _, out _, out _, out _);
            Require(data, offset, 2);
            var result = new EditTextLayout { FlagsOffset = offset, Flags1 = data[offset], Flags2 = data[offset + 1] };
            if ((result.Flags2 & 0x80) != 0) return false;
            offset += 2;
            if ((result.Flags1 & 1) != 0)
            {
                Require(data, offset, 4);
                result.FontId = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
                result.FontHeight = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 2)..]);
                offset += 4;
            }
            if ((result.Flags1 & 4) != 0)
            {
                Require(data, offset, 4);
                result.Color = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]); offset += 4;
            }
            if ((result.Flags1 & 2) != 0) { Require(data, offset, 2); result.MaxLengthOffset = offset; offset += 2; }
            if ((result.Flags2 & 0x20) != 0)
            {
                Require(data, offset, 9);
                if (data[offset] > 3) return false;
                result.LayoutOffset = offset; result.Alignment = (ScaleformTextAlignment)data[offset]; offset += 9;
            }
            result.VariableOffset = offset;
            offset = EndOfString(data, offset) + 1;
            result.TextOffset = offset;
            if (result.HasText)
            {
                int end = EndOfString(data, offset);
                _ = DecodeText(data.Slice(offset, end - offset));
                result.TailOffset = end + 1;
            }
            else
            {
                if (offset != data.Length) return false;
                result.TailOffset = offset;
            }
            layout = result; return true;
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or NotSupportedException) { return false; }
    }

    private byte[] EncodeText(string value)
    {
        if (version >= 6) return StrictUtf8.GetBytes(value);
        if (value.Any(c => c > 127)) throw new NotSupportedException("Non-ASCII edits in pre-SWF6 files require a code-page choice and are not supported.");
        return Encoding.ASCII.GetBytes(value);
    }

    private static Group AttributeValue(Match attribute) => attribute.Groups["double"].Success ? attribute.Groups["double"] : attribute.Groups["single"].Success ? attribute.Groups["single"] : attribute.Groups["bare"];
    private static bool TryNumber(string value, out double number) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    private static bool TryHtmlSize(string value, double defaultSize, out double size)
    {
        value = value.Trim();
        if (!TryNumber(value, out size)) return false;
        if (value.StartsWith('+') || value.StartsWith('-')) size += defaultSize;
        return double.IsFinite(size) && size > 0;
    }
    private static bool TryHtmlColor(string value, out uint rgb)
    {
        rgb = 0;
        value = value.Trim();
        if (value.StartsWith('#')) value = value[1..];
        else if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return value.Length == 6 && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }
    private static string Number(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    private static string EscapeHtmlText(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r\n", "<BR>").Replace("\r", "<BR>").Replace("\n", "<BR>");
    private static string EscapeHtmlAttribute(string value) => WebUtility.HtmlEncode(value);
    private static void WriteU16(Stream output, ushort value) { output.WriteByte((byte)value); output.WriteByte((byte)(value >> 8)); }

    private sealed class EditTextLayout
    {
        public int FlagsOffset, VariableOffset, TextOffset, TailOffset;
        public int MaxLengthOffset = -1, LayoutOffset = -1;
        public byte Flags1, Flags2;
        public ushort? FontId;
        public ushort FontHeight = 240;
        public uint Color = 0xFFFFFFFF;
        public ScaleformTextAlignment Alignment;
        public bool HasText => (Flags1 & 0x80) != 0;
    }
}
