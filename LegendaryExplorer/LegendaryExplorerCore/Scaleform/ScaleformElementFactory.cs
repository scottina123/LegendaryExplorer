using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LegendaryExplorerCore.Scaleform;

public enum ScaleformElementKind { Panel, Text, Image, Button }

/// <summary>Portable authored artwork. Image pixels are straight RGBA, row major.</summary>
public sealed class ScaleformElementSpec
{
    public ScaleformElementKind Kind { get; set; }
    public string Name { get; set; } = "newElement";
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 80;
    public uint Color { get; set; } = 0x153D60FF; // RRGGBBAA
    public string Text { get; set; } = "New text";
    public ushort FontId { get; set; }
    public double FontSize { get; set; } = 24;
    public byte[] Pixels { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    /// <summary>Retained for a draft's lifetime so removing other new artwork never renumbers this element.</summary>
    public ushort[] ReservedIds { get; set; }
}

public sealed record ScaleformCreatedElement(ushort CharacterId, ScaleformTag Definition, ScaleformTag ContentTag);

public sealed partial class ScaleformDocument
{
    /// <summary>
    /// Adds dictionary artwork without touching original definitions. Panels, images and
    /// buttons are movie clips so their instances can receive visual button events.
    /// Text uses an existing movie font; no platform font is silently baked into the game.
    /// </summary>
    public ScaleformCreatedElement AddElementDefinition(ScaleformElementSpec spec, ushort? beforeSpriteId = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (Version < 6) throw new NotSupportedException("Creating UI elements requires SWF 6 or later.");
        if ((spec.Name?.Length ?? 0) > 100 || !Regex.IsMatch(spec.Name ?? "", @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException("Give the element a name starting with a letter, using letters, numbers and underscores.");
        if (!double.IsFinite(spec.Width) || !double.IsFinite(spec.Height) || spec.Width is < 1 or > 8192 || spec.Height is < 1 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(spec), "Element dimensions must be between 1 and 8192 pixels.");
        if (!Enum.IsDefined(spec.Kind)) throw new ArgumentException("Choose a supported UI element.");
        var definitions = ScaleformScene.Build(this).Definitions;
        ScaleformTag owner = null;
        if (beforeSpriteId.HasValue && (!definitions.TryGetValue(beforeSpriteId.Value, out owner) || owner.Code != 39))
            throw new ArgumentException("Choose an existing movie clip to contain the new artwork.", nameof(beforeSpriteId));
        var ids = new HashSet<ushort>(definitions.Keys);
        foreach (var imported in ElementImports()) ids.Add(imported.Id);
        int needed = spec.Kind switch { ScaleformElementKind.Text => 1, ScaleformElementKind.Image => 3, ScaleformElementKind.Button => spec.FontId != 0 ? 3 : 2, _ => 2 };
        var allocated = new List<ushort>();
        if (spec.ReservedIds != null)
        {
            if (spec.ReservedIds.Length != needed || spec.ReservedIds.Distinct().Count() != needed || spec.ReservedIds.Any(id => id is 0 or ushort.MaxValue || ids.Contains(id)))
                throw new InvalidOperationException("This new element's artwork IDs are already in use. Reload its draft before adding it again.");
            allocated.AddRange(spec.ReservedIds);
        }
        else for (int id = 1; id < ushort.MaxValue && allocated.Count < needed; id++)
                if (!ids.Contains((ushort)id)) allocated.Add((ushort)id);
        if (allocated.Count != needed) throw new InvalidOperationException("The movie has no free artwork IDs.");
        bool hasText = spec.Kind == ScaleformElementKind.Text || spec.Kind == ScaleformElementKind.Button && spec.FontId != 0;
        if (hasText)
        {
            if (!GetElementFonts().Any(p => p.Id == spec.FontId))
                throw new ArgumentException("Choose an existing movie font for this text.");
            if (!double.IsFinite(spec.FontSize) || spec.FontSize is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(spec), "Font size must be between 1 and 512.");
            if ((spec.Text ?? "").Contains('\0') || Encoding.UTF8.GetByteCount(spec.Text ?? "") > 65535) throw new ArgumentException("The text is too long or contains a null character.");
        }
        if (spec.Kind == ScaleformElementKind.Image && (spec.PixelWidth is < 1 or > 8192 || spec.PixelHeight is < 1 or > 8192 ||
            (long)spec.PixelWidth * spec.PixelHeight > 16_777_216 || spec.Pixels?.LongLength != (long)spec.PixelWidth * spec.PixelHeight * 4))
            throw new ArgumentException("Choose an image up to 16 megapixels with valid RGBA pixels.");

        // Construct all records before mutating the document, so rejected artwork is atomic.
        var records = new List<ScaleformTag>();
        ushort character;
        ScaleformTag content;
        if (spec.Kind == ScaleformElementKind.Text)
        {
            character = allocated[0];
            content = CreateTimelineRecord(37, ElementText(character, spec));
            records.Add(content);
        }
        else
        {
            int next = 0;
            ushort? bitmap = null;
            if (spec.Kind == ScaleformElementKind.Image)
            {
                bitmap = allocated[next++];
                records.Add(CreateTimelineRecord(36, ElementBitmap(bitmap.Value, spec)));
            }
            ushort shape = allocated[next++];
            records.Add(CreateTimelineRecord(32, ElementRectangle(shape, spec, bitmap)));
            var children = new List<ScaleformTag> { ElementPlacement(shape, 1, "background") };
            content = records[^1];
            if (hasText)
            {
                ushort text = allocated[next++];
                content = CreateTimelineRecord(37, ElementText(text, spec));
                records.Add(content);
                children.Add(ElementPlacement(text, 2, "label"));
            }
            character = allocated[next];
            using var clip = new MemoryStream();
            ElementU16(clip, character); ElementU16(clip, 1);
            foreach (var child in children) child.WriteRecord(clip);
            CreateTimelineRecord(1, []).WriteRecord(clip); CreateTimelineRecord(0, []).WriteRecord(clip);
            records.Add(CreateTimelineRecord(39, clip.ToArray()));
        }
        // Adobe SWF v19 chapter 13 requires every sprite dependency to be declared in
        // the main file before its DefineSprite. Never move original records to achieve
        // that ordering; insert new artwork before the containing root declaration.
        int insertion = owner == null ? timelineTags.FindIndex(t => t.Code is 1 or 0) : FindRootRecordIndex(owner);
        if (insertion < 0) throw new InvalidDataException("The movie has no frame terminator.");
        if (hasText && !ElementFontDeclaredBefore(spec.FontId, insertion))
            throw new InvalidOperationException(owner == null
                ? "This font is introduced after the first movie frame. Choose an opening-frame font."
                : "This font is introduced after the containing movie clip. Choose a font declared before this group.");
        for (int i = 0; i < records.Count; i++) InsertRecord(null, insertion + i, records[i]);
        spec.ReservedIds ??= allocated.ToArray();
        return new ScaleformCreatedElement(character, records[^1], content);
    }

    /// <summary>Font candidates usable by dynamic text, including imported shared fonts.</summary>
    public IReadOnlyList<(ushort Id, string Name)> GetElementFonts()
    {
        var fonts = new List<(ushort, string)>();
        foreach (var pair in ScaleformScene.Build(this).Definitions.Where(p => p.Value.Code is 48 or 75 or 1005))
        {
            string name = "Font " + pair.Key;
            var bytes = pair.Value.Payload.Span;
            if (pair.Value.Code is 48 or 75 && bytes.Length > 5 && bytes.Length >= 5 + bytes[4])
                name = Encoding.UTF8.GetString(bytes.Slice(5, bytes[4]));
            fonts.Add((pair.Key, name));
        }
        var usedFonts = new HashSet<ushort>();
        foreach (var field in ScaleformScene.Build(this).Definitions.Values.Where(t => t.Code == 37))
        {
            var data = field.Payload.Span;
            if (data.Length < 5) continue;
            int flags = 2 + (5 + 4 * (data[2] >> 3) + 7) / 8;
            if (flags + 4 <= data.Length && (data[flags] & 1) != 0) usedFonts.Add(BinaryPrimitives.ReadUInt16LittleEndian(data[(flags + 2)..]));
        }
        fonts.AddRange(ElementImports().Where(p => usedFonts.Contains(p.Id) || p.Name.Contains("font", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("$", StringComparison.Ordinal)));
        return fonts.DistinctBy(p => p.Item1).ToArray();
    }

    /// <summary>Font choices already declared when this editable text definition is introduced.</summary>
    public IReadOnlyList<(ushort Id, string Name)> GetTextFonts(ScaleformTag text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.CanEditTextFormat) throw new ArgumentException("Choose an editable text definition from this movie.", nameof(text));
        int declaration = FindRootRecordIndex(text);
        return GetElementFonts().Where(font => ElementFontDeclaredBefore(font.Id, declaration)).ToArray();
    }

    private bool ElementFontDeclaredBefore(ushort id, int insertion)
    {
        for (int i = 0; i < insertion; i++)
        {
            var tag = timelineTags[i];
            if (tag.Code is 48 or 75 or 1005 && tag.Payload.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(tag.Payload.Span) == id) return true;
            if (tag.Code is 57 or 71)
            {
                var data = tag.Payload.Span; int end = data.IndexOf((byte)0), offset = end + 1 + (tag.Code == 71 ? 2 : 0);
                if (end < 0 || offset + 2 > data.Length) continue;
                int count = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]); offset += 2;
                for (int j = 0; j < count && offset + 2 <= data.Length; j++)
                {
                    if (BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) == id) return true;
                    offset += 2; int length = data[offset..].IndexOf((byte)0); if (length < 0) break; offset += length + 1;
                }
            }
        }
        return false;
    }

    private IEnumerable<(ushort Id, string Name)> ElementImports()
    {
        foreach (var tag in Tags.Where(t => t.Code is 57 or 71))
        {
            byte[] data = tag.Payload.ToArray();
            int end = Array.IndexOf(data, (byte)0);
            int offset = end + 1 + (tag.Code == 71 ? 2 : 0);
            if (end < 0 || offset + 2 > data.Length) continue;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)); offset += 2;
            for (int i = 0; i < count && offset + 2 < data.Length; i++)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)); offset += 2;
                int length = Array.IndexOf(data, (byte)0, offset) - offset;
                if (length < 0) break;
                yield return (id, Encoding.UTF8.GetString(data, offset, length)); offset += length + 1;
            }
        }
    }

    private ScaleformTag ElementPlacement(ushort character, ushort depth, string name)
    {
        using var payload = new MemoryStream(); payload.WriteByte(0x26);
        ElementU16(payload, depth); ElementU16(payload, character); payload.Write(ScaleformPlacement.EncodeMatrix(ScaleformMatrix.Identity));
        payload.Write(Encoding.UTF8.GetBytes(name)); payload.WriteByte(0);
        return CreateTimelineRecord(26, payload.ToArray());
    }

    private static byte[] ElementBitmap(ushort id, ScaleformElementSpec spec)
    {
        using var output = new MemoryStream(); ElementU16(output, id); output.WriteByte(5);
        ElementU16(output, spec.PixelWidth); ElementU16(output, spec.PixelHeight);
        using (var zip = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[spec.PixelWidth * 4];
            for (int y = 0; y < spec.PixelHeight; y++)
            {
                for (int x = 0; x < spec.PixelWidth; x++)
                {
                    int p = (y * spec.PixelWidth + x) * 4, q = x * 4, a = spec.Pixels[p + 3];
                    row[q] = (byte)a;
                    row[q + 1] = (byte)((spec.Pixels[p] * a + 127) / 255);
                    row[q + 2] = (byte)((spec.Pixels[p + 1] * a + 127) / 255);
                    row[q + 3] = (byte)((spec.Pixels[p + 2] * a + 127) / 255);
                }
                zip.Write(row);
            }
        }
        return output.ToArray();
    }

    private static byte[] ElementText(ushort id, ScaleformElementSpec spec)
    {
        using var output = new MemoryStream(); ElementU16(output, id); output.Write(ElementRect(spec.Width, spec.Height));
        output.WriteByte(0xAD); // HasText, Multiline, ReadOnly, HasTextColor, HasFont
        output.WriteByte(0x01); // UseOutlines, no HTML interpretation of ordinary text
        ElementU16(output, spec.FontId); ElementU16(output, (int)Math.Round(spec.FontSize * 20));
        ElementColor(output, spec.Kind == ScaleformElementKind.Button ? 0xFFFFFFFF : spec.Color);
        output.WriteByte(0); // No script-bound variable: address the named field's .text.
        output.Write(Encoding.UTF8.GetBytes(spec.Text ?? "")); output.WriteByte(0);
        return output.ToArray();
    }

    private static byte[] ElementRectangle(ushort id, ScaleformElementSpec spec, ushort? bitmap)
    {
        using var output = new MemoryStream(); ElementU16(output, id); output.Write(ElementRect(spec.Width, spec.Height));
        output.WriteByte(1); output.WriteByte(bitmap.HasValue ? (byte)0x41 : (byte)0);
        if (bitmap.HasValue)
        {
            ElementU16(output, bitmap.Value);
            output.Write(ScaleformPlacement.EncodeMatrix(new ScaleformMatrix(20 * spec.Width / spec.PixelWidth, 0, 0, 20 * spec.Height / spec.PixelHeight, 0, 0)));
        }
        else ElementColor(output, spec.Color);
        output.WriteByte(0); // no strokes
        var bits = new ElementBits(); bits.Write(1, 4); bits.Write(0, 4);
        bits.Write(0, 1); bits.Write(5, 5); bits.Write(1, 5); bits.Write(0, 1); bits.Write(0, 1); bits.Write(1, 1);
        int w = (int)Math.Round(spec.Width * 20), h = (int)Math.Round(spec.Height * 20);
        Edge(w, false); Edge(h, true); Edge(-w, false); Edge(-h, true);
        bits.Write(0, 6); output.Write(bits.Finish()); return output.ToArray();
        void Edge(int distance, bool vertical)
        {
            while (distance != 0)
            {
                int chunk = Math.Clamp(distance, -65535, 65535), n = ElementSignedBits(chunk);
                bits.Write(1, 1); bits.Write(1, 1); bits.Write(Math.Max(2, n) - 2, 4); bits.Write(0, 1); bits.Write(vertical ? 1 : 0, 1); bits.Write(chunk, Math.Max(2, n));
                distance -= chunk;
            }
        }
    }

    private static byte[] ElementRect(double width, double height)
    {
        int w = (int)Math.Round(width * 20), h = (int)Math.Round(height * 20), n = Math.Max(ElementSignedBits(w), ElementSignedBits(h));
        var bits = new ElementBits(); bits.Write(n, 5); bits.Write(0, n); bits.Write(w, n); bits.Write(0, n); bits.Write(h, n); return bits.Finish();
    }
    private static int ElementSignedBits(int value) { for (int i = 1; i < 31; i++) if (value >= -(1 << (i - 1)) && value < (1 << (i - 1))) return i; return 32; }
    private static void ElementU16(Stream stream, int value) { stream.WriteByte((byte)value); stream.WriteByte((byte)(value >> 8)); }
    private static void ElementColor(Stream stream, uint rgba) { stream.WriteByte((byte)(rgba >> 24)); stream.WriteByte((byte)(rgba >> 16)); stream.WriteByte((byte)(rgba >> 8)); stream.WriteByte((byte)rgba); }
    private sealed class ElementBits
    {
        private readonly List<byte> bytes = new(); private int count;
        public void Write(int value, int n) { for (int i = n - 1; i >= 0; i--) { if (count % 8 == 0) bytes.Add(0); if (((value >> i) & 1) != 0) bytes[^1] |= (byte)(1 << (7 - count % 8)); count++; } }
        public byte[] Finish() => bytes.ToArray();
    }
}
